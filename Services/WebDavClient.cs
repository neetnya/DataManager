using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DataManager.Services
{
    /// <summary>
    /// WebDAV 操作失败时抛出的统一异常。消息均为简体中文，涉及 HTTP 错误时包含状态码与原因短语。
    /// </summary>
    public sealed class WebDavException : Exception
    {
        /// <summary>用指定消息创建异常。</summary>
        public WebDavException(string message)
            : base(message)
        {
        }

        /// <summary>用指定消息与内部异常创建异常。</summary>
        public WebDavException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>WebDAV 目录中的一项（文件或集合）。</summary>
    public sealed class WebDavEntry
    {
        /// <summary>相对名称（不含路径），已做百分号解码。</summary>
        public string Name { get; set; }

        /// <summary>文件大小（字节）；目录或服务器未提供时为 0。</summary>
        public long Length { get; set; }

        /// <summary>最后修改时间（UTC）；服务器未提供时为 null。</summary>
        public DateTime? LastModified { get; set; }

        /// <summary>是否为集合（目录）。</summary>
        public bool IsCollection { get; set; }
    }

    /// <summary>
    /// 极简 WebDAV 客户端：只用 .NET 共享框架里的 System.Net.Http，不依赖任何 NuGet 包。
    ///
    /// 设计要点：
    ///   * 构造时创建一个 HttpClient（PreAuthenticate = false，AllowAutoRedirect = true），
    ///     Timeout 设为无限，超时由每个请求自己的 CancellationTokenSource 控制（控制类请求默认 100 秒）。
    ///   * 认证使用显式 Authorization: Basic 头（很多 WebDAV 服务器对 Credentials 挑战应答处理不正确）。
    ///   * 所有方法对外都是同步的；内部用 async/await 且处处 ConfigureAwait(false)，因此不会死锁。
    ///   * 上传使用自定义 HttpContent（读取本地文件时按块上报进度）；下载先写同目录临时文件，成功后再覆盖目标文件。
    /// </summary>
    public sealed class WebDavClient : IDisposable
    {
        // PROPFIND 不在 HttpMethod 的静态成员里，需要自行构造。
        private static readonly HttpMethod PropFindMethod = new HttpMethod("PROPFIND");

        // MKCOL（建目录）同样不在 HttpMethod 里。
        private static readonly HttpMethod MkColMethod = new HttpMethod("MKCOL");

        // 控制类请求（PROPFIND / HEAD）的默认超时：100 秒。
        private static readonly TimeSpan ControlRequestTimeout = TimeSpan.FromSeconds(100);

        // DAV: 命名空间。
        private static readonly XNamespace DavNamespace = XNamespace.Get("DAV:");

        // 上传/下载进度上报的最小字节间隔，避免大文件时回调过于频繁。
        private const int ProgressReportInterval = 64 * 1024;

        // 上传/下载的读写缓冲区大小（128 KiB）。
        private const int CopyBufferSize = 128 * 1024;

        // User-Agent，部分服务器会拒绝没有 UA 的请求。
        private const string UserAgent = "DataManager-WebDav/1.0";

        // 上传动作名：用于让 404 的提示更有针对性（PUT 不会自动建目录）。
        private const string UploadAction = "上传文件";

        // PROPFIND 请求体：显式列出需要查询的属性，兼容性优于空体（allprop）。
        private const string PropFindBody =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<D:propfind xmlns:D=\"DAV:\"><D:prop>" +
            "<D:resourcetype/><D:getcontentlength/><D:getlastmodified/>" +
            "</D:prop></D:propfind>";

        private readonly HttpClient _httpClient;

        // 已构造好的 Authorization 头；用户名为空（匿名）时为 null。
        private readonly string _authorization;

        private bool _disposed;

        /// <summary>
        /// 创建 WebDAV 客户端。
        /// </summary>
        /// <param name="baseUrl">基地址，例如 https://example.com/dav/ 或 https://example.com/dav（结尾斜杠可有可无）。</param>
        /// <param name="username">用户名；为空字符串表示匿名访问。</param>
        /// <param name="password">密码；用户名非空时才参与 Basic 认证。</param>
        public WebDavClient(string baseUrl, string username, string password)
        {
            BaseUrl = NormalizeBaseUrl(baseUrl);

            var handler = new HttpClientHandler
            {
                // 不使用 Credentials/挑战应答：改为每个请求显式带 Authorization 头。
                PreAuthenticate = false,
                AllowAutoRedirect = true,
            };

            _httpClient = new HttpClient(handler, disposeHandler: true);

            // 总超时交给每个请求的 CancellationTokenSource 控制，避免大文件传输被固定超时打断。
            _httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");

            if (!string.IsNullOrEmpty(username))
            {
                // Basic 认证使用 UTF-8 编码，兼容中文用户名；用户名含冒号的情况由服务器自行处理。
                string raw = username + ":" + (password ?? string.Empty);
                _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            }
        }

        /// <summary>规范化后的基地址，始终以 / 结尾。</summary>
        public string BaseUrl { get; }

        /// <summary>
        /// 连接测试：对基地址发 PROPFIND Depth:0。成功返回 true；失败抛 <see cref="WebDavException"/>。
        /// </summary>
        public bool TestConnection()
        {
            ThrowIfDisposed();
            return TestConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 列出目录下条目（PROPFIND Depth:1）。返回项的 Name 为相对名称（不含路径），已过滤集合自身。
        /// </summary>
        public List<WebDavEntry> List()
        {
            ThrowIfDisposed();
            return ListAsync(null, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 列出指定子目录下的条目（PROPFIND Depth:1）。remoteDirectory 为空表示基地址目录。
        /// 目录不存在（404）时返回空列表，便于界面直接显示“还没有备份”。
        /// </summary>
        public List<WebDavEntry> List(string remoteDirectory)
        {
            ThrowIfDisposed();
            return ListAsync(remoteDirectory, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 判断远端文件是否存在。404 返回 false，其他错误抛 <see cref="WebDavException"/>。
        /// </summary>
        public bool Exists(string remoteName)
        {
            ThrowIfDisposed();
            return ExistsAsync(remoteName, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 判断远端目录是否存在。目录用结尾斜杠表示，避免服务器 301 重定向。
        /// </summary>
        public bool DirectoryExists(string remoteDirectory)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(remoteDirectory))
            {
                return true;   // 基地址本身就是目录
            }

            return DirectoryExistsAsync(NormalizeRelativePath(remoteDirectory), CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        private async Task<bool> DirectoryExistsAsync(string remoteDirectory, CancellationToken externalToken)
        {
            const string Action = "检查目录是否存在";
            TimeSpan timeout = ControlRequestTimeout;

            using (var cts = CreateLinkedTokenSource(externalToken, timeout))
            {
                try
                {
                    // 注意：这里不能用 ResolveUri——它会把结尾斜杠去掉，
                    // 而集合必须带斜杠，否则服务器会回 301。
                    string uri = ResolveCollectionUri(remoteDirectory);
                    return await ExistsByPropFindAsync(uri, Action, cts).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw MapException(ex, Action, timeout, cts, externalToken);
                }
            }
        }

        /// <summary>
        /// 取远端文件大小（字节）。不存在返回 -1；服务器未提供长度时也返回 -1。
        /// </summary>
        public long GetContentLength(string remoteName)
        {
            ThrowIfDisposed();
            return GetContentLengthAsync(remoteName, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 上传本地文件（PUT）。progress 报告已发送字节数（可为 null）。
        /// 取消时抛 OperationCanceledException；超时或其他失败抛 <see cref="WebDavException"/>。
        /// </summary>
        public void Upload(string localFilePath, string remoteName, IProgress<long> progress, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            UploadAsync(localFilePath, remoteName, progress, cancellationToken).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 下载到本地文件（GET）。先写同目录临时文件，全部成功后再覆盖目标文件，因此失败的下载不会破坏已有文件。
        /// progress 报告已接收字节数（可为 null）。
        /// 取消时抛 OperationCanceledException；超时或其他失败抛 <see cref="WebDavException"/>。
        /// </summary>
        public void Download(string remoteName, string localFilePath, IProgress<long> progress, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            DownloadAsync(remoteName, localFilePath, progress, cancellationToken).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 远端文件最后修改时间（UTC）。取不到（不存在或服务器未提供）返回 null。
        /// </summary>
        public DateTime? GetLastModified(string remoteName)
        {
            ThrowIfDisposed();
            return GetLastModifiedAsync(remoteName, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 解析集合（目录）的 Uri，并保证以 “/” 结尾。
        /// WebDAV 里集合用结尾斜杠表示；漏掉时很多服务器会回 301 重定向，
        /// 而重定向对 PROPFIND / MKCOL 这类方法并不总是被正确处理。
        /// </summary>
        private string ResolveCollectionUri(string remoteDirectory)
        {
            string uri = ResolveUri(remoteDirectory);
            return uri.EndsWith("/", StringComparison.Ordinal) ? uri : uri + "/";
        }

        /// <summary>
        /// 确保远端目录存在，不存在就逐级 MKCOL 创建。
        ///
        /// WebDAV 的 PUT **不会**自动创建中间目录：直接往一个不存在的目录里 PUT，
        /// 服务器只会回 404，看起来就像“地址填错了”。这就是很多 WebDAV 客户端
        /// 要求先选一个已存在目录的原因。这里由程序自己补齐，用户只要填目录名即可。
        ///
        /// remoteDirectory 为空表示根目录（无需创建）。已存在的目录会被跳过。
        /// </summary>
        public void EnsureDirectory(string remoteDirectory, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureDirectoryAsync(remoteDirectory, cancellationToken).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 把“远端目录 + 文件名”拼成相对路径。目录为空时只返回文件名。
        /// 会剔除多余的分隔符与 “.”、“..” 段，避免拼出越界路径。
        /// </summary>
        public static string CombineRemotePath(string remoteDirectory, string fileName)
        {
            string name = (fileName ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
            string directory = NormalizeRelativePath(remoteDirectory);

            if (directory.Length == 0)
            {
                return name;
            }

            if (name.Length == 0)
            {
                return directory;
            }

            return directory + "/" + name;
        }

        /// <summary>把相对路径规范化：统一分隔符、去掉空段与 “.”/“..” 段。</summary>
        private static string NormalizeRelativePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string normalized = value.Replace('\\', '/').Trim();
            string[] parts = normalized.Split('/');
            var builder = new StringBuilder(normalized.Length);

            for (int i = 0; i < parts.Length; i++)
            {
                string segment = parts[i].Trim();
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('/');
                }

                builder.Append(segment);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 把远端相对路径解析为最终请求的绝对 URI：按 “/” 分段，每段单独做 URL 转义后再拼到 BaseUrl 上。
        /// remoteName 为空时返回 BaseUrl 本身。反斜杠会被当作路径分隔符，"." 与 ".." 段会被忽略（防止越出基地址）。
        /// </summary>
        public string ResolveUri(string remoteName)
        {
            if (string.IsNullOrEmpty(remoteName))
            {
                return BaseUrl;
            }

            string normalized = remoteName.Replace('\\', '/').Trim();
            string[] parts = normalized.Split('/');

            var builder = new StringBuilder(BaseUrl.Length + normalized.Length + 8);
            builder.Append(BaseUrl);

            bool first = true;
            for (int i = 0; i < parts.Length; i++)
            {
                string segment = parts[i];
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append('/');
                }

                // 每段单独转义：空格 -> %20，中文 -> UTF-8 百分号编码，"/" 作为分隔符保留。
                builder.Append(Uri.EscapeDataString(segment));
                first = false;
            }

            return builder.ToString();
        }

        /// <summary>
        /// 解析 PROPFIND 的 207 Multi-Status 响应（容错：允许 DAV: 前缀或默认命名空间、允许缺少个别元素）。
        /// 注意：本方法纯粹解析 XML，不会过滤集合自身；<see cref="List"/> 会做这一步过滤。
        /// </summary>
        public static List<WebDavEntry> ParsePropFindResponse(string xml)
        {
            List<RawEntry> raws = ParsePropFindRaw(xml);

            var result = new List<WebDavEntry>(raws.Count);
            for (int i = 0; i < raws.Count; i++)
            {
                result.Add(ToEntry(raws[i]));
            }

            return result;
        }

        /// <summary>释放 HttpClient。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _httpClient.Dispose();
        }

        // ------------------------------------------------------------------
        // 基地址规范化
        // ------------------------------------------------------------------

        /// <summary>把用户输入的地址规范化成始终以 “/” 结尾的绝对基地址。</summary>
        private static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new WebDavException("WebDAV 地址不能为空。");
            }

            string text = baseUrl.Trim();

            if (text.StartsWith("//", StringComparison.Ordinal))
            {
                // 协议相对写法 //host/path
                text = "https:" + text;
            }
            else if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // 允许省略协议，默认按 https 处理。
                text = "https://" + text;
            }

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri))
            {
                throw new WebDavException("WebDAV 地址格式无效：" + baseUrl);
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new WebDavException("WebDAV 地址只支持 http 或 https 协议：" + baseUrl);
            }

            // 手工拼 Host 与端口，避免 UriComponents.HostAndPort 把默认端口（80/443）也带上。
            string host = uri.Host;
            if (string.IsNullOrEmpty(host))
            {
                throw new WebDavException("WebDAV 地址缺少主机名：" + baseUrl);
            }

            if (uri.HostNameType == UriHostNameType.IPv6)
            {
                host = "[" + host + "]";
            }

            string authority = uri.IsDefaultPort
                ? host
                : host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

            string path = uri.AbsolutePath;
            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }

            if (!path.EndsWith("/", StringComparison.Ordinal))
            {
                path += "/";
            }

            return uri.Scheme + "://" + authority + path;
        }

        // ------------------------------------------------------------------
        // 请求辅助
        // ------------------------------------------------------------------

        /// <summary>创建请求并带上显式 Basic 认证头。</summary>
        private HttpRequestMessage CreateRequest(HttpMethod method, string uri)
        {
            var request = new HttpRequestMessage(method, uri);
            if (_authorization != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", _authorization);
            }

            return request;
        }

        /// <summary>PROPFIND 的请求体。</summary>
        private static HttpContent CreatePropFindContent()
        {
            var content = new StringContent(PropFindBody, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            content.Headers.ContentType.CharSet = "utf-8";
            return content;
        }

        /// <summary>创建与外部令牌联动、带超时的 CancellationTokenSource。</summary>
        private static CancellationTokenSource CreateLinkedTokenSource(CancellationToken externalToken, TimeSpan timeout)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            if (timeout > TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                cts.CancelAfter(timeout);
            }

            return cts;
        }

        /// <summary>把 HTTP 错误状态包装成中文 WebDavException（含状态码与原因短语）。</summary>
        private static async Task<WebDavException> CreateHttpErrorAsync(HttpResponseMessage response, string action)
        {
            string body = await ReadErrorBodyAsync(response).ConfigureAwait(false);
            string reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
                ? response.StatusCode.ToString()
                : response.ReasonPhrase;

            var builder = new StringBuilder();
            builder.Append("WebDAV ").Append(action).Append("失败：HTTP ")
                   .Append(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture))
                   .Append(' ').Append(reason).Append('。');
            builder.Append(DescribeStatusCode(response.StatusCode, action));

            if (!string.IsNullOrEmpty(body))
            {
                builder.Append(" 服务器返回：").Append(body);
            }

            // 附上实际请求的地址：WebDAV 出错时最常见的原因就是路径拼错了，
            // 而界面上填的是“目录 + 文件名”两段，不打印出来根本没法核对。
            try
            {
                Uri target = response.RequestMessage?.RequestUri;
                if (target != null)
                {
                    builder.Append("\n实际请求：").Append(target.AbsoluteUri);
                }
            }
            catch (Exception)
            {
                // 拿不到请求地址不影响主流程
            }

            return new WebDavException(builder.ToString());
        }

        /// <summary>针对常见状态码追加中文提示。</summary>
        private static string DescribeStatusCode(HttpStatusCode statusCode, string action)
        {
            switch ((int)statusCode)
            {
                case 400:
                    return "请求格式不正确（远端路径可能包含非法字符）。";
                case 401:
                    return "认证失败，请检查用户名和密码。";
                case 403:
                    return "服务器拒绝访问，请检查账号权限。";
                case 404:
                    // 上传时 404 几乎都是“目标目录不存在”，PUT 不会自动建目录，
                    // 这里把话说清楚，避免用户以为是地址写错了。
                    if (string.Equals(action, UploadAction, StringComparison.Ordinal))
                    {
                        return "远端目标不存在：WebDAV 的 PUT 不会自动创建目录。\n" +
                               "请检查【远端目录】是否填写正确，或该目录是否已在服务器上创建。";
                    }

                    return "远端路径不存在。";
                case 405:
                    return "服务器不允许该操作（HTTP 方法不被支持）。";
                case 409:
                    return "远端目录不存在或存在冲突。";
                case 412:
                    return "服务器上的文件已被其他程序修改。";
                case 423:
                    return "远端资源已被锁定。";
                case 507:
                    return "服务器存储空间不足。";
                default:
                    if ((int)statusCode >= 500)
                    {
                        return "服务器内部错误，请稍后重试。";
                    }

                    return string.Empty;
            }
        }

        /// <summary>读取错误响应体的一小段，便于定位问题。</summary>
        private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response)
        {
            try
            {
                if (response.Content == null)
                {
                    return null;
                }

                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
                if (text.Length > 200)
                {
                    text = text.Substring(0, 200) + "…";
                }

                return text;
            }
            catch (Exception)
            {
                // 读取响应体只是锦上添花，任何失败都忽略。
                return null;
            }
        }

        /// <summary>
        /// 统一异常映射：WebDavException 原样抛出；调用方主动取消抛 OperationCanceledException；
        /// 其余（HttpRequestException / TaskCanceledException / IOException 等）一律包装成 WebDavException。
        /// </summary>
        private static Exception MapException(Exception ex, string action, TimeSpan timeout, CancellationTokenSource cts, CancellationToken externalToken)
        {
            var webDav = ex as WebDavException;
            if (webDav != null)
            {
                return webDav;
            }

            var canceled = ex as OperationCanceledException;
            if (canceled != null)
            {
                if (externalToken.IsCancellationRequested)
                {
                    // 调用方主动取消：按 .NET 约定原样抛出取消异常，便于上层区分“用户取消”与“失败”。
                    return new OperationCanceledException("WebDAV " + action + "已取消。", canceled, externalToken);
                }

                if (cts != null && cts.IsCancellationRequested)
                {
                    return new WebDavException("WebDAV " + action + "超时：" + DescribeTimeout(timeout), canceled);
                }

                return new WebDavException("WebDAV " + action + "被中断：" + canceled.Message, canceled);
            }

            var http = ex as HttpRequestException;
            if (http != null)
            {
                string detail = http.Message;
                if (http.InnerException != null && !string.IsNullOrEmpty(http.InnerException.Message))
                {
                    detail = detail + "：" + http.InnerException.Message;
                }

                return new WebDavException("WebDAV " + action + "失败：无法访问服务器（" + detail + "）。", http);
            }

            var io = ex as IOException;
            if (io != null)
            {
                return new WebDavException("WebDAV " + action + "失败：本地文件或网络读写错误（" + io.Message + "）。", io);
            }

            var unauthorized = ex as UnauthorizedAccessException;
            if (unauthorized != null)
            {
                return new WebDavException("WebDAV " + action + "失败：没有权限访问本地文件（" + unauthorized.Message + "）。", unauthorized);
            }

            return new WebDavException("WebDAV " + action + "失败：" + ex.Message, ex);
        }

        /// <summary>描述超时时长。</summary>
        private static string DescribeTimeout(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout == System.Threading.Timeout.InfiniteTimeSpan)
            {
                return "等待时间过长，连接被中断或服务器长时间无响应。";
            }

            return "超过 " + timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " 秒服务器无响应。";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WebDavClient));
            }
        }

        // ------------------------------------------------------------------
        // 连接测试
        // ------------------------------------------------------------------

        private async Task<bool> TestConnectionAsync(CancellationToken externalToken)
        {
            const string Action = "连接测试";
            TimeSpan timeout = ControlRequestTimeout;

            using (var cts = CreateLinkedTokenSource(externalToken, timeout))
            {
                try
                {
                    using (var request = CreateRequest(PropFindMethod, BaseUrl))
                    {
                        request.Headers.TryAddWithoutValidation("Depth", "0");
                        request.Content = CreatePropFindContent();

                        using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            int code = (int)response.StatusCode;

                            // 正常 WebDAV 服务器返回 207 Multi-Status；个别服务器对 PROPFIND 返回 200。
                            if (code == 207 || code == 200)
                            {
                                return true;
                            }

                            throw await CreateHttpErrorAsync(response, Action).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw MapException(ex, Action, timeout, cts, externalToken);
                }
            }
        }

        // ------------------------------------------------------------------
        // 列目录
        // ------------------------------------------------------------------

        private async Task<List<WebDavEntry>> ListAsync(string remoteDirectory, CancellationToken externalToken)
        {
            const string Action = "列出目录";
            TimeSpan timeout = ControlRequestTimeout;

            // 目标 Uri 与“集合自身”的路径键：目录为空时就是基地址本身。
            string targetUri = string.IsNullOrWhiteSpace(remoteDirectory)
                ? BaseUrl
                : ResolveCollectionUri(remoteDirectory);

            using (var cts = CreateLinkedTokenSource(externalToken, timeout))
            {
                try
                {
                    using (var request = CreateRequest(PropFindMethod, targetUri))
                    {
                        request.Headers.TryAddWithoutValidation("Depth", "1");
                        request.Content = CreatePropFindContent();

                        using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            int code = (int)response.StatusCode;

                            // 目录还不存在时返回空列表，而不是抛错：
                            // 界面上“还没有备份”比一条报错更合适。
                            if (code == 404 || code == 410)
                            {
                                return new List<WebDavEntry>();
                            }

                            if (code != 207 && code != 200)
                            {
                                throw await CreateHttpErrorAsync(response, Action).ConfigureAwait(false);
                            }

                            string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            List<RawEntry> raws = ParsePropFindRaw(xml);

                            // Depth:1 的结果里包含基地址对应的集合自身，这里按 href 路径比对后剔除。
                            string selfKey = GetPathKey(targetUri);

                            var result = new List<WebDavEntry>(raws.Count);
                            for (int i = 0; i < raws.Count; i++)
                            {
                                RawEntry raw = raws[i];
                                if (selfKey != null && string.Equals(GetPathKey(raw.Href), selfKey, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                result.Add(ToEntry(raw));
                            }

                            return result;
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw MapException(ex, Action, timeout, cts, externalToken);
                }
            }
        }

        // ------------------------------------------------------------------
        // 存在性 / 大小 / 修改时间
        // ------------------------------------------------------------------

        private async Task<bool> ExistsAsync(string remoteName, CancellationToken externalToken)
        {
            const string Action = "检查文件是否存在";
            TimeSpan timeout = ControlRequestTimeout;

            using (var cts = CreateLinkedTokenSource(externalToken, timeout))
            {
                try
                {
                    string uri = ResolveUri(remoteName);

                    using (var request = CreateRequest(HttpMethod.Head, uri))
                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        int code = (int)response.StatusCode;
                        if (code >= 200 && code < 300)
                        {
                            return true;
                        }

                        if (code == 404 || code == 410)
                        {
                            return false;
                        }

                        // 部分服务器不支持 HEAD（405/501），或对不存在的资源返回 403，退回 PROPFIND Depth:0 判断。
                        if (code == 405 || code == 501 || code == 403)
                        {
                            return await ExistsByPropFindAsync(uri, Action, cts).ConfigureAwait(false);
                        }

                        throw await CreateHttpErrorAsync(response, Action).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    throw MapException(ex, Action, timeout, cts, externalToken);
                }
            }
        }

        private async Task<bool> ExistsByPropFindAsync(string uri, string action, CancellationTokenSource cts)
        {
            using (var request = CreateRequest(PropFindMethod, uri))
            {
                request.Headers.TryAddWithoutValidation("Depth", "0");
                request.Content = CreatePropFindContent();

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    int code = (int)response.StatusCode;
                    if (code == 207 || code == 200)
                    {
                        return true;
                    }

                    if (code == 404 || code == 410)
                    {
                        return false;
                    }

                    throw await CreateHttpErrorAsync(response, action).ConfigureAwait(false);
                }
            }
        }

        private async Task<long> GetContentLengthAsync(string remoteName, CancellationToken externalToken)
        {
            const string Action = "获取文件大小";
            TimeSpan timeout = ControlRequestTimeout;

            using (var cts = CreateLinkedTokenSource(externalToken, timeout))
            {
                try
                {
                    string uri = ResolveUri(remoteName);

                    using (var request = CreateRequest(HttpMethod.Head, uri))
                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        int code = (int)response.StatusCode;
                        if (code >= 200 && code < 300)
                        {
                            long? length = response.Content.Headers.ContentLength;
                            if (length.HasValue)
                            {
                                return length.Value;
                            }

                            // 服务器没给 Content-Length（例如分块传输），退回 PROPFIND 查询。
                            return await GetContentLengthByPropFindAsync(uri, Action, cts).ConfigureAwait(false);
                        }

                        if (code == 404 || code == 410)
                        {
                            return -1;
                        }

                        if (code == 405 || code == 501 || code == 403)
                        {
                            return await GetContentLengthByPropFindAsync(uri, Action, cts).ConfigureAwait(false);
                        }

                        throw await CreateHttpErrorAsync(response, Action).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    throw MapException(ex, Action, timeout, cts, externalToken);
                }
            }
        }

        private async Task<long> GetContentLengthByPropFindAsync(string uri, string action, CancellationTokenSource cts)
        {
            using (var request = CreateRequest(PropFindMethod, uri))
            {
                request.Headers.TryAddWithoutValidation("Depth", "0");
                request.Content = CreatePropFindContent();

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    int code = (int)response.StatusCode;
                    if (code != 207 && code != 200)
                    {
                        if (code == 404 || code == 410)
                        {
                            return -1;
                        }

                        throw await CreateHttpErrorAsync(response, action).ConfigureAwait(false);
                    }

                    string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    List<RawEntry> raws = ParsePropFindRaw(xml);

                    for (int i = 0; i < raws.Count; i++)
                    {
                        if (raws[i].HasLength)
                        {
                            return raws[i].Length;
                        }
                    }

                    return -1;
                }
            }
        }

        private async Task<DateTime?> GetLastModifiedAsync(string remoteName, CancellationToken externalToken)
        {
            const string Action = "获取修改时间";
            TimeSpan timeout = ControlRequestTimeout;

            using (var cts = CreateLinkedTokenSource(externalToken, timeout))
            {
                try
                {
                    string uri = ResolveUri(remoteName);

                    using (var request = CreateRequest(HttpMethod.Head, uri))
                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        int code = (int)response.StatusCode;
                        if (code >= 200 && code < 300)
                        {
                            DateTimeOffset? lastModified = response.Content.Headers.LastModified;
                            if (lastModified.HasValue)
                            {
                                return lastModified.Value.UtcDateTime;
                            }

                            return await GetLastModifiedByPropFindAsync(uri, Action, cts).ConfigureAwait(false);
                        }

                        if (code == 404 || code == 410)
                        {
                            return null;
                        }

                        if (code == 405 || code == 501 || code == 403)
                        {
                            return await GetLastModifiedByPropFindAsync(uri, Action, cts).ConfigureAwait(false);
                        }

                        throw await CreateHttpErrorAsync(response, Action).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    throw MapException(ex, Action, timeout, cts, externalToken);
                }
            }
        }

        private async Task<DateTime?> GetLastModifiedByPropFindAsync(string uri, string action, CancellationTokenSource cts)
        {
            using (var request = CreateRequest(PropFindMethod, uri))
            {
                request.Headers.TryAddWithoutValidation("Depth", "0");
                request.Content = CreatePropFindContent();

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    int code = (int)response.StatusCode;
                    if (code != 207 && code != 200)
                    {
                        if (code == 404 || code == 410)
                        {
                            return null;
                        }

                        throw await CreateHttpErrorAsync(response, action).ConfigureAwait(false);
                    }

                    string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    List<RawEntry> raws = ParsePropFindRaw(xml);

                    for (int i = 0; i < raws.Count; i++)
                    {
                        if (raws[i].LastModified.HasValue)
                        {
                            return raws[i].LastModified;
                        }
                    }

                    return null;
                }
            }
        }

        // ------------------------------------------------------------------
        // 建目录（MKCOL）
        // ------------------------------------------------------------------

        private async Task EnsureDirectoryAsync(string remoteDirectory, CancellationToken externalToken)
        {
            const string Action = "创建远端目录";
            TimeSpan timeout = ControlRequestTimeout;

            string directory = NormalizeRelativePath(remoteDirectory);
            if (directory.Length == 0)
            {
                // 根目录：地址本身就是它，不需要创建。
                return;
            }

            string[] segments = directory.Split('/');
            var accumulated = new StringBuilder();

            for (int i = 0; i < segments.Length; i++)
            {
                if (accumulated.Length > 0)
                {
                    accumulated.Append('/');
                }

                accumulated.Append(segments[i]);
                string current = accumulated.ToString();

                using (var cts = CreateLinkedTokenSource(externalToken, timeout))
                {
                    try
                    {
                        await CreateCollectionAsync(current, Action, cts).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw MapException(ex, Action, timeout, cts, externalToken);
                    }
                }
            }
        }

        /// <summary>
        /// 对单级目录发 MKCOL。
        ///
        /// 201 = 新建成功。405 是**二义**的：RFC 4918 允许服务器用 405 表示“集合已存在”，
        /// 但 405 的本义是“不支持该方法”。所以收到 405 时必须回查一次，确认目录真的存在；
        /// 否则会把“服务器不支持 MKCOL”误判成成功，等到 PUT 时才报 404，反而更难排查。
        /// </summary>
        private async Task CreateCollectionAsync(string relativePath, string action, CancellationTokenSource cts)
        {
            string uri = ResolveCollectionUri(relativePath);

            using (var request = CreateRequest(MkColMethod, uri))
            using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
            {
                int code = (int)response.StatusCode;

                if (code == 201 || code == 200 || code == 204)
                {
                    return;
                }

                if (code == 405 || code == 301)
                {
                    // 可能是“已存在”，也可能是“不支持 MKCOL”——回查确认。
                    if (await CollectionExistsAsync(uri, cts).ConfigureAwait(false))
                    {
                        return;
                    }

                    throw new WebDavException(
                        "WebDAV 创建远端目录失败：服务器不支持 MKCOL（建目录），且目录「" + relativePath + "」不存在。\n" +
                        "请先在网盘上手动创建该目录，或把【远端目录】留空。");
                }

                throw await CreateHttpErrorAsync(response, action).ConfigureAwait(false);
            }
        }

        /// <summary>用 PROPFIND Depth:0 确认目标是否为已存在的集合。</summary>
        private async Task<bool> CollectionExistsAsync(string uri, CancellationTokenSource cts)
        {
            using (var request = CreateRequest(PropFindMethod, uri))
            {
                request.Headers.TryAddWithoutValidation("Depth", "0");
                request.Content = CreatePropFindContent();

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    int code = (int)response.StatusCode;
                    if (code == 404 || code == 410)
                    {
                        return false;
                    }

                    if (code != 207 && code != 200)
                    {
                        return false;
                    }

                    string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    List<RawEntry> raws = ParsePropFindRaw(xml);

                    // 只要能查到条目，就说明这个路径确实存在。
                    return raws.Count > 0;
                }
            }
        }

        // ------------------------------------------------------------------
        // 上传 / 下载
        // ------------------------------------------------------------------

        private async Task UploadAsync(string localFilePath, string remoteName, IProgress<long> progress, CancellationToken cancellationToken)
        {
            const string Action = UploadAction;

            // 传输不设总时长上限：大文件在慢速链路上耗时很长，取消完全交给调用方的令牌。
            TimeSpan timeout = System.Threading.Timeout.InfiniteTimeSpan;

            if (string.IsNullOrEmpty(localFilePath))
            {
                throw new WebDavException("WebDAV 上传失败：本地文件路径不能为空。");
            }

            if (!File.Exists(localFilePath))
            {
                throw new WebDavException("WebDAV 上传失败：本地文件不存在：" + localFilePath);
            }

            long length;
            try
            {
                length = new FileInfo(localFilePath).Length;
            }
            catch (Exception ex)
            {
                throw new WebDavException("WebDAV 上传失败：无法读取本地文件信息（" + ex.Message + "）。", ex);
            }

            using (var cts = CreateLinkedTokenSource(cancellationToken, timeout))
            {
                try
                {
                    string uri = ResolveUri(remoteName);

                    using (var request = CreateRequest(HttpMethod.Put, uri))
                    using (var content = new ProgressFileContent(localFilePath, length, progress, cts.Token))
                    {
                        request.Content = content;

                        using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            int code = (int)response.StatusCode;

                            // 200/201/204 都视为成功（有的服务器返回 204 No Content）。
                            if (code == 200 || code == 201 || code == 204)
                            {
                                return;
                            }

                            throw await CreateHttpErrorAsync(response, Action).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw MapException(ex, Action, timeout, cts, cancellationToken);
                }
            }
        }

        private async Task DownloadAsync(string remoteName, string localFilePath, IProgress<long> progress, CancellationToken cancellationToken)
        {
            const string Action = "下载文件";

            // 传输不设总时长上限，取消完全交给调用方的令牌。
            TimeSpan timeout = System.Threading.Timeout.InfiniteTimeSpan;

            if (string.IsNullOrEmpty(localFilePath))
            {
                throw new WebDavException("WebDAV 下载失败：本地文件路径不能为空。");
            }

            string targetPath;
            string tempPath;
            try
            {
                targetPath = Path.GetFullPath(localFilePath);
                string directory = Path.GetDirectoryName(targetPath);
                if (string.IsNullOrEmpty(directory))
                {
                    directory = Directory.GetCurrentDirectory();
                }

                Directory.CreateDirectory(directory);

                // 临时文件与目标文件同目录（同一卷），保证最后的 File.Move 是快速替换。
                tempPath = Path.Combine(directory, Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part");
            }
            catch (Exception ex)
            {
                throw new WebDavException("WebDAV 下载失败：无法准备本地临时文件（" + ex.Message + "）。", ex);
            }

            bool moved = false;

            using (var cts = CreateLinkedTokenSource(cancellationToken, timeout))
            {
                try
                {
                    string uri = ResolveUri(remoteName);

                    using (var request = CreateRequest(HttpMethod.Get, uri))
                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        int code = (int)response.StatusCode;
                        if (code < 200 || code >= 300)
                        {
                            throw await CreateHttpErrorAsync(response, Action).ConfigureAwait(false);
                        }

                        long total = 0;
                        long lastReported = 0;
                        byte[] buffer = new byte[CopyBufferSize];

                        using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
                        {
                            int read;
                            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false)) > 0)
                            {
                                await target.WriteAsync(buffer, 0, read, cts.Token).ConfigureAwait(false);
                                total += read;

                                if (progress != null && total - lastReported >= ProgressReportInterval)
                                {
                                    lastReported = total;
                                    progress.Report(total);
                                }
                            }

                            await target.FlushAsync(cts.Token).ConfigureAwait(false);
                        }

                        if (progress != null && lastReported != total)
                        {
                            progress.Report(total);
                        }

                        // 全部字节落盘后才覆盖目标文件：中途失败不会破坏原有文件。
                        File.Move(tempPath, targetPath, true);
                        moved = true;
                    }
                }
                catch (Exception ex)
                {
                    throw MapException(ex, Action, timeout, cts, cancellationToken);
                }
                finally
                {
                    if (!moved)
                    {
                        try
                        {
                            if (File.Exists(tempPath))
                            {
                                File.Delete(tempPath);
                            }
                        }
                        catch (IOException)
                        {
                            // 清理临时文件失败不影响主流程。
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 上传用的 HttpContent：打开本地文件按块写入请求流，并上报已发送字节数。
        /// TryComputeLength 返回真实文件长度，因此会带上 Content-Length，服务器可预分配空间。
        /// </summary>
        private sealed class ProgressFileContent : HttpContent
        {
            private readonly string _localFilePath;
            private readonly long _length;
            private readonly IProgress<long> _progress;
            private readonly CancellationToken _cancellationToken;

            public ProgressFileContent(string localFilePath, long length, IProgress<long> progress, CancellationToken cancellationToken)
            {
                _localFilePath = localFilePath;
                _length = length;
                _progress = progress;
                _cancellationToken = cancellationToken;

                Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _length;
                return true;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                return SerializeCoreAsync(stream, _cancellationToken);
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken cancellationToken)
            {
                return SerializeCoreAsync(stream, cancellationToken);
            }

            /// <summary>把文件内容写入目标流；每次写完一块就累加并（按间隔）上报进度。</summary>
            private async Task SerializeCoreAsync(Stream target, CancellationToken cancellationToken)
            {
                byte[] buffer = new byte[CopyBufferSize];
                long total = 0;
                long lastReported = 0;

                // FileShare.ReadWrite：即使别的程序也打开了该文件（例如数据库文件）也能读取。
                using (var source = new FileStream(_localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CopyBufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        total += read;

                        if (_progress != null && (total - lastReported >= ProgressReportInterval || total >= _length))
                        {
                            lastReported = total;
                            _progress.Report(total);
                        }
                    }
                }

                // 保证最后一次上报的是精确的总字节数。
                if (_progress != null && lastReported != total)
                {
                    _progress.Report(total);
                }
            }
        }

        // ------------------------------------------------------------------
        // PROPFIND 解析
        // ------------------------------------------------------------------

        /// <summary>解析中间结果：保留 href 与“长度是否存在”等信息，供内部方法使用。</summary>
        private sealed class RawEntry
        {
            public string Href;
            public string Name = string.Empty;
            public long Length;
            public bool HasLength;
            public DateTime? LastModified;
            public bool IsCollection;
        }

        private static WebDavEntry ToEntry(RawEntry raw)
        {
            return new WebDavEntry
            {
                Name = raw.Name,
                Length = raw.Length,
                LastModified = raw.LastModified,
                IsCollection = raw.IsCollection,
            };
        }

        /// <summary>解析 207 响应，得到原始条目列表。</summary>
        private static List<RawEntry> ParsePropFindRaw(string xml)
        {
            var list = new List<RawEntry>();
            if (string.IsNullOrWhiteSpace(xml))
            {
                return list;
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(xml, LoadOptions.None);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new WebDavException("解析 WebDAV 响应失败：返回内容不是合法的 XML（" + ex.Message + "）。", ex);
            }

            XElement root = document.Root;
            if (root == null)
            {
                return list;
            }

            foreach (XElement response in FindElements(root, "response"))
            {
                var raw = new RawEntry();

                XElement hrefElement = FindFirstElement(response, "href");
                if (hrefElement != null)
                {
                    raw.Href = hrefElement.Value.Trim();
                }

                raw.Name = ExtractName(raw.Href);

                XElement lengthElement = FindFirstElement(response, "getcontentlength");
                if (lengthElement != null)
                {
                    long parsed;
                    if (long.TryParse(lengthElement.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        raw.Length = parsed;
                        raw.HasLength = true;
                    }
                }

                XElement modifiedElement = FindFirstElement(response, "getlastmodified");
                if (modifiedElement != null)
                {
                    raw.LastModified = ParseHttpDate(modifiedElement.Value);
                }

                XElement resourceTypeElement = FindFirstElement(response, "resourcetype");
                if (resourceTypeElement != null && FindFirstElement(resourceTypeElement, "collection") != null)
                {
                    raw.IsCollection = true;
                }

                list.Add(raw);
            }

            return list;
        }

        /// <summary>
        /// 判断元素是否为 DAV: 命名空间（或未声明命名空间）下的指定名字。
        /// 这样 &lt;D:href&gt; 与默认命名空间 &lt;href xmlns="DAV:"&gt; 都能匹配。
        /// </summary>
        private static bool IsDavElement(XElement element, string localName)
        {
            if (!string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return element.Name.Namespace == DavNamespace || element.Name.Namespace == XNamespace.None;
        }

        /// <summary>按本地名深度查找所有后代元素（容错：服务器可能有多层 propstat 包装）。</summary>
        private static IEnumerable<XElement> FindElements(XElement parent, string localName)
        {
            foreach (XElement element in parent.Descendants())
            {
                if (IsDavElement(element, localName))
                {
                    yield return element;
                }
            }
        }

        private static XElement FindFirstElement(XElement parent, string localName)
        {
            foreach (XElement element in FindElements(parent, localName))
            {
                return element;
            }

            return null;
        }

        /// <summary>从 href 里取最后一段作为相对名称（百分号解码、去掉结尾斜杠）。</summary>
        private static string ExtractName(string href)
        {
            if (string.IsNullOrEmpty(href))
            {
                return string.Empty;
            }

            string path = StripQueryAndFragment(href).TrimEnd('/');
            int slash = path.LastIndexOf('/');
            if (slash >= 0)
            {
                path = path.Substring(slash + 1);
            }

            try
            {
                return Uri.UnescapeDataString(path);
            }
            catch (UriFormatException)
            {
                return path;
            }
        }

        /// <summary>去掉 href 里的查询串与片段。</summary>
        private static string StripQueryAndFragment(string value)
        {
            int index = value.IndexOfAny(new[] { '?', '#' });
            return index >= 0 ? value.Substring(0, index) : value;
        }

        /// <summary>
        /// 计算用于比较“是否同一路径”的键：href 可能是完整 URL 或绝对路径，这里统一成解码后的路径。
        /// </summary>
        private static string GetPathKey(string uriOrHref)
        {
            if (string.IsNullOrEmpty(uriOrHref))
            {
                return null;
            }

            string value = StripQueryAndFragment(uriOrHref.Trim());

            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Uri parsed;
                if (Uri.TryCreate(value, UriKind.Absolute, out parsed))
                {
                    value = parsed.AbsolutePath;
                }
            }

            try
            {
                value = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                // 解码失败就按原样比较。
            }

            return value.TrimEnd('/');
        }

        /// <summary>解析 HTTP 日期（RFC1123，例如 "Mon, 01 Jan 2024 10:00:00 GMT"），失败返回 null。</summary>
        private static DateTime? ParseHttpDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string text = value.Trim();

            DateTimeOffset parsedOffset;
            if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsedOffset))
            {
                return parsedOffset.UtcDateTime;
            }

            DateTime parsed;
            if (DateTime.TryParseExact(
                    text,
                    "r",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return null;
        }
    }
}
