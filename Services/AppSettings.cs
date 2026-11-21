using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataManager.Services
{
    /// <summary>本地配置（config.json）。WebDAV 密码在落盘时使用当前用户的 DPAPI 加密。</summary>
    public sealed class AppSettings
    {
        /// <summary>WebDAV 基地址。</summary>
        public string WebDavUrl { get; set; } = string.Empty;

        /// <summary>WebDAV 账号。</summary>
        public string WebDavUser { get; set; } = string.Empty;

        /// <summary>WebDAV 密码（内存中为明文，落盘时通过 DPAPI 透明加密）。</summary>
        public string WebDavPassword { get; set; } = string.Empty;

        /// <summary>WebDAV 上保存数据库的目录（相对基地址）；空表示直接用基地址。</summary>
        public string WebDavDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 远端备份文件名前缀（不是完整文件名）。实际上传的文件名为
        /// <c>前缀_yyyyMMdd_HHmmss.sqlite</c>，每次都是新文件，天然避开覆盖问题。
        /// </summary>
        public string WebDavFileNamePrefix { get; set; } = "DataManager";

        /// <summary>
        /// 【旧字段】早期版本用固定文件名，已改为“前缀 + 时间戳”。
        /// 仅用于读取老 config.json 并把用户原来的命名迁移成前缀，不再写回。
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string WebDavFileName { get; set; }

        /// <summary>本地备份保留份数；0 表示保留全部。</summary>
        public int BackupKeepCount { get; set; } = 0;

        // ---- 右侧表格的文本样式（全局设置）----

        /// <summary>表格字体名称。</summary>
        public string GridFontFamily { get; set; } = "Microsoft YaHei UI";

        /// <summary>表格字号。</summary>
        public double GridFontSize { get; set; } = 13;

        /// <summary>表格文字是否加粗。</summary>
        public bool GridFontBold { get; set; } = false;

        /// <summary>表格文字是否倾斜。</summary>
        public bool GridFontItalic { get; set; } = false;

        /// <summary>表格行高。</summary>
        public double GridRowHeight { get; set; } = 30;

        /// <summary>是否显示表格网格线。</summary>
        public bool GridShowLines { get; set; } = true;

        /// <summary>
        /// 分组列的分隔符：一个单元格里含多个分组值时用此符号分隔。
        /// 只存符号本身（默认“/”），前后空格由代码补齐。
        /// 空字符串表示不拆分（整段文本视为一个分组）。
        /// </summary>
        public string GroupSeparator { get; set; } = "/";

        // ---- 列宽记忆：表 Id → 物理列名 → 宽度比例（0~1） ----

        /// <summary>用户手动调整过的列宽（存占表格总宽度的比例，0~1）。</summary>
        public Dictionary<string, Dictionary<string, double>> ColumnWidths { get; set; }
            = new Dictionary<string, Dictionary<string, double>>();

        /// <summary>主窗口宽度。</summary>
        public double WindowWidth { get; set; } = 1180;

        /// <summary>主窗口高度。</summary>
        public double WindowHeight { get; set; } = 720;

        /// <summary>归一化可能为 null 的字典集合，防止反序列化脏数据导致空引用。</summary>
        internal void Normalize()
        {
            if (ColumnWidths == null)
            {
                ColumnWidths = new Dictionary<string, Dictionary<string, double>>();
                return;
            }

            var keys = new List<string>(ColumnWidths.Keys);
            foreach (string key in keys)
            {
                if (ColumnWidths[key] == null)
                {
                    ColumnWidths[key] = new Dictionary<string, double>();
                }
            }
        }

        public AppSettings Clone()
        {
            var copy = new AppSettings
            {
                WebDavUrl = WebDavUrl,
                WebDavUser = WebDavUser,
                WebDavPassword = WebDavPassword,
                WebDavDirectory = WebDavDirectory,
                WebDavFileNamePrefix = WebDavFileNamePrefix,
                WebDavFileName = WebDavFileName,
                BackupKeepCount = BackupKeepCount,
                GridFontFamily = GridFontFamily,
                GridFontSize = GridFontSize,
                GridFontBold = GridFontBold,
                GridFontItalic = GridFontItalic,
                GridRowHeight = GridRowHeight,
                GridShowLines = GridShowLines,
                GroupSeparator = GroupSeparator,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
            };

            if (ColumnWidths != null)
            {
                foreach (KeyValuePair<string, Dictionary<string, double>> kv in ColumnWidths)
                {
                    copy.ColumnWidths[kv.Key] = kv.Value != null
                        ? new Dictionary<string, double>(kv.Value)
                        : new Dictionary<string, double>();
                }
            }

            return copy;
        }

        /// <summary>
        /// 只覆盖外观与分组分隔符（设置窗口实时预览会改这两类字段）。
        /// 不碰 WebDAV、列宽、窗口尺寸，避免取消设置时把别处已保存的配置冲掉。
        /// </summary>
        public void CopyAppearanceFrom(AppSettings source)
        {
            if (source == null)
            {
                return;
            }

            GridFontFamily = source.GridFontFamily;
            GridFontSize = source.GridFontSize;
            GridFontBold = source.GridFontBold;
            GridFontItalic = source.GridFontItalic;
            GridRowHeight = source.GridRowHeight;
            GridShowLines = source.GridShowLines;
            GroupSeparator = source.GroupSeparator;
        }

        /// <summary>
        /// 把老配置迁移到新字段：旧版用固定文件名（WebDavFileName），
        /// 新版改为“前缀 + 时间戳”。这里把旧文件名去掉扩展名后当前缀，
        /// 这样用户原来的命名习惯（例如 MyData）能保留下来。
        /// </summary>
        public void MigrateLegacy()
        {
            if (string.IsNullOrWhiteSpace(WebDavFileName))
            {
                return;
            }

            // 只有在用户没另外设过前缀时才迁移，避免覆盖用户的新设置。
            if (string.IsNullOrWhiteSpace(WebDavFileNamePrefix) ||
                string.Equals(WebDavFileNamePrefix, "DataManager", StringComparison.Ordinal))
            {
                string legacy = WebDavFileName.Trim();
                int dot = legacy.LastIndexOf('.');
                if (dot > 0)
                {
                    legacy = legacy.Substring(0, dot);
                }

                if (legacy.Length > 0)
                {
                    WebDavFileNamePrefix = legacy;
                }
            }

            WebDavFileName = null;
        }

        /// <summary>记住某一列的宽度比例（0~1，占表格总宽度）。</summary>
        public void SetColumnWidth(long tableId, string physicalName, double width)
        {
            if (ColumnWidths == null)
            {
                ColumnWidths = new Dictionary<string, Dictionary<string, double>>();
            }

            string key = tableId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!ColumnWidths.TryGetValue(key, out Dictionary<string, double> map) || map == null)
            {
                map = new Dictionary<string, double>();
                ColumnWidths[key] = map;
            }

            map[physicalName] = width;
        }

        /// <summary>取某列记住的宽度比例（0~1）；没有则返回 0。</summary>
        public double GetColumnWidth(long tableId, string physicalName)
        {
            if (ColumnWidths == null)
            {
                return 0;
            }

            string key = tableId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ColumnWidths.TryGetValue(key, out Dictionary<string, double> map) &&
                map != null &&
                map.TryGetValue(physicalName, out double width))
            {
                return width;
            }

            return 0;
        }
    }

    /// <summary>分组列多值拆分的统一工具，全项目共用同一套拆分规则。</summary>
    public static class GroupValueSplitter
    {
        /// <summary>
        /// 实际生效的分隔符：把配置里的符号（默认“/”）前后各补一个空格。
        /// 配置为 null 时按默认“ / ”处理；配置为“”或纯空白时不拆分（返回空串）。
        /// </summary>
        public static string Separator
        {
            get
            {
                string s = App.Settings?.GroupSeparator;
                if (s == null)
                {
                    return " / ";
                }

                if (string.IsNullOrWhiteSpace(s))
                {
                    return string.Empty;
                }

                return " " + s.Trim() + " ";
            }
        }

        /// <summary>
        /// 把单元格文本按分隔符拆成若干分组值；去重、去空白、去掉空段。
        /// 分隔符为空时不做拆分，整段（trim 后）作为唯一分组。
        /// </summary>
        public static List<string> Split(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(value))
            {
                return result;
            }

            string sep = Separator;
            if (string.IsNullOrEmpty(sep))
            {
                string t = value.Trim();
                if (t.Length > 0)
                {
                    result.Add(t);
                }

                return result;
            }

            foreach (string part in value.Split(new[] { sep }, StringSplitOptions.None))
            {
                string t = part.Trim();
                if (t.Length > 0 && !result.Contains(t))
                {
                    result.Add(t);
                }
            }

            return result;
        }

        /// <summary>单元格文本中是否包含指定的分组值（作为其中一个拆分项）。</summary>
        public static bool Contains(string cellValue, string groupValue)
        {
            if (string.IsNullOrEmpty(groupValue))
            {
                return false;
            }

            foreach (string p in Split(cellValue))
            {
                if (string.Equals(p, groupValue, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>配置文件的读写。</summary>
    public static class SettingsService
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    string json = File.ReadAllText(AppPaths.SettingsFile);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        AppSettings s = JsonSerializer.Deserialize<AppSettings>(json, Options);
                        if (s != null)
                        {
                            s.Normalize();
                            s.MigrateLegacy();
                            s.WebDavPassword = DpapiHelper.Unprotect(s.WebDavPassword);
                            return s;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("读取配置文件失败，将备份为 config.json.bak 并回退默认配置。", ex);
                TryBackupCorruptConfig();
            }

            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            AppPaths.EnsureCreated();

            // 克隆副本并加密密码，避免污染内存中的明文配置
            AppSettings toSave = settings.Clone();
            toSave.Normalize();
            toSave.WebDavPassword = DpapiHelper.Protect(toSave.WebDavPassword);

            string json = JsonSerializer.Serialize(toSave, Options);
            string tmpPath = AppPaths.SettingsFile + ".tmp";
            bool moved = false;
            try
            {
                File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
                File.Move(tmpPath, AppPaths.SettingsFile, true);
                moved = true;
            }
            finally
            {
                if (!moved)
                {
                    try
                    {
                        if (File.Exists(tmpPath))
                        {
                            File.Delete(tmpPath);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static void TryBackupCorruptConfig()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak", true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("备份损坏的配置文件失败：" + ex.Message);
            }
        }

        /// <summary>通过 P/Invoke 调用 Windows DPAPI（crypt32.dll）实现零依赖加密。</summary>
        private static class DpapiHelper
        {
            private const string Prefix = "dpapi:";
            private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct DATA_BLOB
            {
                public int cbData;
                public IntPtr pbData;
            }

            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool CryptProtectData(
                ref DATA_BLOB pDataIn,
                string szDataDescr,
                IntPtr pOptionalEntropy,
                IntPtr pvReserved,
                IntPtr pPromptStruct,
                int dwFlags,
                out DATA_BLOB pDataOut);

            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool CryptUnprotectData(
                ref DATA_BLOB pDataIn,
                IntPtr ppszDataDescr,
                IntPtr pOptionalEntropy,
                IntPtr pvReserved,
                IntPtr pPromptStruct,
                int dwFlags,
                out DATA_BLOB pDataOut);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr LocalFree(IntPtr hMem);

            public static string Protect(string plainText)
            {
                if (string.IsNullOrEmpty(plainText))
                {
                    return string.Empty;
                }

                if (plainText.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    return plainText;
                }

                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                var inBlob = new DATA_BLOB();
                var outBlob = new DATA_BLOB();
                try
                {
                    inBlob.cbData = plainBytes.Length;
                    inBlob.pbData = Marshal.AllocHGlobal(plainBytes.Length);
                    Marshal.Copy(plainBytes, 0, inBlob.pbData, plainBytes.Length);

                    if (!CryptProtectData(
                        ref inBlob,
                        null,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN,
                        out outBlob))
                    {
                        AppLog.Warn("DPAPI 加密 WebDAV 密码失败，Win32 错误码：" + Marshal.GetLastWin32Error());
                        return plainText;
                    }

                    byte[] cipherBytes = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, cipherBytes, 0, outBlob.cbData);
                    return Prefix + Convert.ToBase64String(cipherBytes);
                }
                catch (Exception ex)
                {
                    AppLog.Error("DPAPI 加密 WebDAV 密码异常。", ex);
                    return plainText;
                }
                finally
                {
                    if (inBlob.pbData != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(inBlob.pbData);
                    }

                    if (outBlob.pbData != IntPtr.Zero)
                    {
                        LocalFree(outBlob.pbData);
                    }
                }
            }

            public static string Unprotect(string stored)
            {
                if (string.IsNullOrEmpty(stored))
                {
                    return string.Empty;
                }

                // 自动兼容旧版明文配置
                if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    return stored;
                }

                string base64 = stored.Substring(Prefix.Length);
                if (string.IsNullOrEmpty(base64))
                {
                    return string.Empty;
                }

                byte[] cipherBytes;
                try
                {
                    cipherBytes = Convert.FromBase64String(base64);
                }
                catch (FormatException ex)
                {
                    AppLog.Error("DPAPI 密文格式无效。", ex);
                    return string.Empty;
                }

                var inBlob = new DATA_BLOB();
                var outBlob = new DATA_BLOB();
                try
                {
                    inBlob.cbData = cipherBytes.Length;
                    inBlob.pbData = Marshal.AllocHGlobal(cipherBytes.Length);
                    Marshal.Copy(cipherBytes, 0, inBlob.pbData, cipherBytes.Length);

                    if (!CryptUnprotectData(
                        ref inBlob,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN,
                        out outBlob))
                    {
                        AppLog.Warn("DPAPI 解密 WebDAV 密码失败，Win32 错误码：" + Marshal.GetLastWin32Error());
                        return string.Empty;
                    }

                    byte[] plainBytes = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
                    return Encoding.UTF8.GetString(plainBytes);
                }
                catch (Exception ex)
                {
                    AppLog.Error("DPAPI 解密 WebDAV 密码异常。", ex);
                    return string.Empty;
                }
                finally
                {
                    if (inBlob.pbData != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(inBlob.pbData);
                    }

                    if (outBlob.pbData != IntPtr.Zero)
                    {
                        LocalFree(outBlob.pbData);
                    }
                }
            }
        }
    }
}
