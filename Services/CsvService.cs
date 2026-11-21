using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DataManager.Services
{
    /// <summary>
    /// CSV 读写服务：RFC 4180 风格的解析与转义，支持 BOM 探测、流式读写与 UTF-8 / GB18030 / GBK / 本地 ANSI 编码的自动识别。
    /// </summary>
    /// <remarks>
    /// 本机 NuGet 不可达，且 System.Text.Encoding.CodePages 不可用，
    /// 因此 GB18030(54936) / GBK(936) 等传统代码页通过 Win32 的 MultiByteToWideChar 解码；
    /// UTF-8 / UTF-16 / UTF-32 则使用 BCL 自带的编码器。
    /// 本文件不引用任何第三方包。
    /// </remarks>
    public static class CsvService
    {
        // ------------------------------------------------------------------
        // Win32 互操作：传统代码页解码
        // ------------------------------------------------------------------

        /// <summary>CP_ACP：系统当前 ANSI 代码页（简体中文 Windows 上即 936/GBK）。</summary>
        private const uint CP_ACP = 0;

        /// <summary>CP_GBK：简体中文 GBK 代码页。</summary>
        private const uint CP_GBK = 936;

        /// <summary>CP_GB18030：中文 GB18030 代码页。</summary>
        private const uint CP_GB18030 = 54936;

        /// <summary>CP_UTF8：UTF-8 代码页。</summary>
        private const uint CP_UTF8 = 65001;

        /// <summary>MB_ERR_INVALID_CHARS：遇到非法字节序列时让 MultiByteToWideChar 直接返回失败。</summary>
        private const uint MB_ERR_INVALID_CHARS = 0x00000008;

        /// <summary>
        /// MultiByteToWideChar：把多字节串按指定代码页解码为 UTF-16。
        /// </summary>
        [DllImport("kernel32.dll", EntryPoint = "MultiByteToWideChar", CharSet = CharSet.Unicode,
                   SetLastError = true, ExactSpelling = true)]
        private static extern int MultiByteToWideChar(
            uint codePage,
            uint dwFlags,
            [In] byte[] lpMultiByteStr,
            int cbMultiByte,
            [Out][MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 5)] char[] lpWideCharStr,
            int cchWideChar);

        // ------------------------------------------------------------------
        // 公开 API
        // ------------------------------------------------------------------

        /// <summary>
        /// 读取 CSV 文件的所有行（第一行也是数据，不作为表头）。
        /// 自动探测编码：BOM 优先；无 BOM 时按 UTF-8(严格) → GB18030(54936) → GBK(936) → CP_ACP → 兜底替换 顺序解码。
        /// </summary>
        /// <param name="path">CSV 文件路径。</param>
        /// <returns>逐行的字段数组；空文件返回 0 行。</returns>
        /// <exception cref="IOException">文件不存在、无权限或读取失败时抛出（消息为中文）。</exception>
        public static List<string[]> ReadFile(string path)
        {
            var rows = new List<string[]>();
            foreach (string[] row in ReadRows(path))
            {
                rows.Add(row);
            }

            return rows;
        }

        /// <summary>
        /// 流式读取 CSV 文件的每一行，避免大文件一次性占用过多内存。
        /// 自动探测编码（BOM 优先，无 BOM 时按 UTF-8(严格) → GB18030(54936) → GBK(936) → CP_ACP → 兜底替换）。
        /// </summary>
        /// <param name="path">CSV 文件路径。</param>
        /// <returns>按需惰性枚举的字段数组序列。</returns>
        public static IEnumerable<string[]> ReadRows(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new IOException("读取 CSV 文件失败：文件路径为空。");
            }

            FileStream fs;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            }
            catch (Exception ex)
            {
                throw new IOException("读取 CSV 文件失败：" + path + "。原因：" + ex.Message, ex);
            }

            return ReadRowsCore(fs, path);
        }

        private static IEnumerable<string[]> ReadRowsCore(FileStream fs, string path)
        {
            using (fs)
            {
                if (fs.Length == 0)
                {
                    yield break;
                }

                using (TextReader reader = CreateDetectedReader(fs, path))
                {
                    foreach (string[] row in ParseRows(reader))
                    {
                        yield return row;
                    }
                }
            }
        }

        /// <summary>
        /// 从字符串解析（用于测试/剪贴板），使用与 ReadRows 相同的分词逻辑，不做编码探测。
        /// 未闭合双引号会抛出带明确行号的 <see cref="FormatException"/>。
        /// </summary>
        /// <param name="text">CSV 文本；null 或空串返回 0 行。</param>
        /// <returns>逐行的字段数组，字段永不为 null（缺失字段为 ""）。</returns>
        public static List<string[]> Parse(string text)
        {
            var rows = new List<string[]>();
            if (string.IsNullOrEmpty(text))
            {
                return rows;
            }

            using (var reader = new StringReader(text))
            {
                foreach (string[] row in ParseRows(reader))
                {
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// 从任意 <see cref="TextReader"/> 流式解析 RFC 4180 CSV 行。
        /// 遇到未闭合双引号时抛出明确行号的 <see cref="FormatException"/>。
        /// </summary>
        private static IEnumerable<string[]> ParseRows(TextReader reader)
        {
            int next = reader.Read();
            if (next == -1)
            {
                yield break;
            }

            // 跳过可能残留的 BOM 字符
            if (next == '\uFEFF')
            {
                next = reader.Read();
            }

            int lineNumber = 1;

            while (next != -1)
            {
                var fields = new List<string>();
                bool rowFinished = false;

                while (!rowFinished)
                {
                    var field = new StringBuilder();

                    if (next == '"')
                    {
                        // ---- 引号字段：内部可包含 , " CR LF ----
                        int quoteStartLine = lineNumber;
                        bool closed = false;
                        next = reader.Read(); // 跳过起始引号

                        while (next != -1)
                        {
                            char c = (char)next;
                            if (c == '"')
                            {
                                int afterQuote = reader.Read();
                                if (afterQuote == '"')
                                {
                                    // 成对引号代表一个字面量引号
                                    field.Append('"');
                                    next = reader.Read();
                                    continue;
                                }

                                // 结束引号
                                closed = true;
                                next = afterQuote;
                                break;
                            }

                            if (c == '\r')
                            {
                                field.Append(c);
                                next = reader.Read();
                                if (next == '\n')
                                {
                                    field.Append('\n');
                                    next = reader.Read();
                                }

                                lineNumber++;
                                continue;
                            }

                            if (c == '\n')
                            {
                                lineNumber++;
                            }

                            field.Append(c);
                            next = reader.Read();
                        }

                        if (!closed)
                        {
                            throw new FormatException(
                                "CSV 格式错误：第 " + quoteStartLine.ToString(CultureInfo.InvariantCulture) + " 行存在未闭合的双引号。");
                        }

                        // 宽容处理：结束引号与分隔符之间若还有字符（如 "a"b），按字面量追加。
                        while (next != -1 && next != ',' && next != '\r' && next != '\n')
                        {
                            field.Append((char)next);
                            next = reader.Read();
                        }
                    }
                    else
                    {
                        // ---- 普通字段：直接读到分隔符或行尾 ----
                        while (next != -1 && next != ',' && next != '\r' && next != '\n')
                        {
                            field.Append((char)next);
                            next = reader.Read();
                        }
                    }

                    fields.Add(field.ToString());

                    if (next == -1)
                    {
                        // 文件末尾没有换行符：当前行同样有效。
                        rowFinished = true;
                    }
                    else if (next == ',')
                    {
                        next = reader.Read(); // 继续解析本行的下一个字段
                    }
                    else
                    {
                        // 行终止符：兼容 CRLF / LF / CR 三种写法。
                        if (next == '\r')
                        {
                            next = reader.Read();
                            if (next == '\n')
                            {
                                next = reader.Read();
                            }
                        }
                        else
                        {
                            next = reader.Read();
                        }

                        lineNumber++;
                        rowFinished = true;
                    }
                }

                yield return fields.ToArray();
            }
        }

        /// <summary>
        /// 把一行字段序列化为一行 CSV 文本（不含换行符）。
        /// </summary>
        /// <param name="fields">字段数组；null 或空数组返回空串。</param>
        /// <param name="preventFormulaInjection">为 true 时对以 =、+、-、@、Tab、CR 开头的字段加单引号前缀防 Excel 公式注入。</param>
        public static string FormatRow(string[] fields, bool preventFormulaInjection = false)
        {
            if (fields == null || fields.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(EscapeField(fields[i], preventFormulaInjection));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 流式写出 CSV 文件。UTF-8 with BOM（便于 Excel 正确识别中文）。每行 CRLF 结尾。
        /// </summary>
        /// <param name="path">目标文件路径。</param>
        /// <param name="rows">数据行序列；null 视为 0 行。</param>
        /// <param name="preventFormulaInjection">是否启用防 Excel 公式注入转义（默认 false 保持往返无损）。</param>
        /// <exception cref="IOException">路径为空或写入失败时抛出（消息为中文）。</exception>
        public static void WriteRows(string path, IEnumerable<string[]> rows, bool preventFormulaInjection = false)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new IOException("写入 CSV 文件失败：文件路径为空。");
            }

            try
            {
                // UTF8Encoding(true)：写入 BOM，Excel 打开时才能正确识别中文。
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                using (var writer = new StreamWriter(fs, new UTF8Encoding(true), 65536))
                {
                    if (rows != null)
                    {
                        foreach (string[] row in rows)
                        {
                            writer.Write(FormatRow(row, preventFormulaInjection));
                            writer.Write("\r\n"); // 每一行（含最后一行）都以 CRLF 结尾
                        }
                    }

                    writer.Flush();
                }
            }
            catch (Exception ex)
            {
                throw new IOException("写入 CSV 文件失败：" + path + "。原因：" + ex.Message, ex);
            }
        }

        /// <summary>
        /// 写出 CSV 文件。UTF-8 with BOM（便于 Excel 正确识别中文）。每行 CRLF 结尾。
        /// </summary>
        /// <param name="path">目标文件路径。</param>
        /// <param name="rows">数据行；null 视为 0 行。</param>
        /// <exception cref="IOException">路径为空或写入失败时抛出（消息为中文）。</exception>
        public static void WriteFile(string path, IEnumerable<string[]> rows)
        {
            WriteRows(path, rows, preventFormulaInjection: false);
        }

        /// <summary>
        /// 单个字段转义：包含 , " \r \n 时用双引号包裹，内部 " 变成 ""。
        /// 可选启用防 Excel 公式注入（对以 =、+、-、@、\t、\r 开头的字段前置单引号）。
        /// </summary>
        /// <param name="value">字段值；null 视为空串。</param>
        /// <param name="preventFormulaInjection">是否启用防 Excel 公式注入保护。</param>
        public static string EscapeField(string value, bool preventFormulaInjection = false)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (preventFormulaInjection)
            {
                char first = value[0];
                if (first == '=' || first == '+' || first == '-' || first == '@' || first == '\t' || first == '\r')
                {
                    value = "'" + value;
                }
            }

            bool needQuote = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == ',' || c == '"' || c == '\r' || c == '\n')
                {
                    needQuote = true;
                    break;
                }
            }

            if (!needQuote)
            {
                return value;
            }

            StringBuilder sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"')
                {
                    sb.Append('"'); // 内部引号翻倍
                }

                sb.Append(c);
            }

            sb.Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 编码探测与流式解码（私有）
        // ------------------------------------------------------------------

        /// <summary>
        /// 探测文件编码并构造流式 TextReader：
        /// 1) BOM(UTF-8/UTF-32/UTF-16) 优先；
        /// 2) 无 BOM 时流式校验是否严格 UTF-8；
        /// 3) 非 UTF-8 时按 GB18030(54936) → GBK(936) → CP_ACP → UTF-8 替换兜底顺序选择代码页流式解码。
        /// </summary>
        private static TextReader CreateDetectedReader(FileStream fs, string path)
        {
            byte[] bom = new byte[4];
            int bomRead = ReadExactUpTo(fs, bom, 4);

            // 1) BOM 探测（UTF-32 必须在 UTF-16 之前判断）
            if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            {
                fs.Position = 3;
                return new StreamReader(fs, new UTF8Encoding(false, false), false, 65536, leaveOpen: true);
            }

            if (bomRead >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00)
            {
                fs.Position = 4;
                return new StreamReader(fs, new UTF32Encoding(false, false), false, 65536, leaveOpen: true);
            }

            if (bomRead >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
            {
                fs.Position = 4;
                return new StreamReader(fs, new UTF32Encoding(true, false), false, 65536, leaveOpen: true);
            }

            if (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            {
                fs.Position = 2;
                return new StreamReader(fs, Encoding.Unicode, false, 65536, leaveOpen: true);
            }

            if (bomRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            {
                fs.Position = 2;
                return new StreamReader(fs, Encoding.BigEndianUnicode, false, 65536, leaveOpen: true);
            }

            // 2) 无 BOM：流式严格 UTF-8 验证（仅用固定 64 KB 缓冲区，避免整文件驻留内存）
            fs.Position = 0;
            if (IsStreamValidUtf8(fs))
            {
                fs.Position = 0;
                return new StreamReader(fs, new UTF8Encoding(false, false), false, 65536, leaveOpen: true);
            }

            // 3) 非严格 UTF-8：在样本上探测可用 Win32 代码页（GB18030 → GBK → CP_ACP）
            fs.Position = 0;
            byte[] sample = ReadSampleUpToNewline(fs, 65536);
            fs.Position = 0;

            uint? selectedCodePage = SelectFallbackCodePage(sample);
            if (selectedCodePage.HasValue)
            {
                return new Win32LineDecodingReader(fs, selectedCodePage.Value);
            }

            // 4) 兜底：UTF-8 替换字符流式解码，保证不抛异常
            return new StreamReader(fs, Encoding.UTF8, false, 65536, leaveOpen: true);
        }

        private static int ReadExactUpTo(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        /// <summary>流式检查整个文件是否为合法 UTF-8，仅占用 64 KiB 缓冲区。</summary>
        private static bool IsStreamValidUtf8(FileStream fs)
        {
            Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
            byte[] byteBuf = new byte[65536];
            char[] charBuf = new char[65536 + 4];

            try
            {
                while (true)
                {
                    int read = fs.Read(byteBuf, 0, byteBuf.Length);
                    bool flush = read == 0;
                    decoder.GetChars(byteBuf, 0, read, charBuf, 0, flush);
                    if (flush)
                    {
                        return true;
                    }
                }
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>读取最多 maxBytes 字节并在最近的换行符处截断，避免切破多字节字符。</summary>
        private static byte[] ReadSampleUpToNewline(FileStream fs, int maxBytes)
        {
            byte[] buf = new byte[maxBytes];
            int read = ReadExactUpTo(fs, buf, maxBytes);
            if (read == 0)
            {
                return Array.Empty<byte>();
            }

            if (read == maxBytes)
            {
                int lastLf = Array.LastIndexOf(buf, (byte)'\n', read - 1);
                if (lastLf > 0)
                {
                    read = lastLf + 1;
                }
            }

            if (read == buf.Length)
            {
                return buf;
            }

            byte[] result = new byte[read];
            Buffer.BlockCopy(buf, 0, result, 0, read);
            return result;
        }

        /// <summary>按 GB18030(54936) → GBK(936) → CP_ACP 顺序选择可用的 Win32 代码页。</summary>
        private static uint? SelectFallbackCodePage(byte[] sample)
        {
            if (sample == null || sample.Length == 0)
            {
                return null;
            }

            if (TryDecodeWithWin32(sample, CP_GB18030, MB_ERR_INVALID_CHARS) != null)
            {
                return CP_GB18030;
            }

            if (TryDecodeWithWin32(sample, CP_GBK, MB_ERR_INVALID_CHARS) != null)
            {
                return CP_GBK;
            }

            if (TryDecodeWithWin32(sample, CP_ACP, 0) != null)
            {
                return CP_ACP;
            }

            return null;
        }

        /// <summary>
        /// 把文件字节解码为文本：BOM 优先，其次严格 UTF-8，
        /// 最后按 GB18030(54936) → GBK(936) → CP_ACP → UTF-8 替换兜底顺序解码。
        /// </summary>
        private static string DecodeBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            // ---- 1) BOM 探测（UTF-32 的 BOM 必须在 UTF-16 的 BOM 之前判断）----
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                return new UTF32Encoding(false, true).GetString(bytes, 4, bytes.Length - 4);
            }

            if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            {
                return new UTF32Encoding(true, true).GetString(bytes, 4, bytes.Length - 4);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            // ---- 2) 无 BOM：严格 UTF-8 试验 ----
            string utf8Text = TryDecodeStrictUtf8(bytes);
            if (utf8Text != null)
            {
                return utf8Text;
            }

            // ---- 3) 回退链：GB18030(54936) → GBK(936) → CP_ACP ----
            string gb18030Text = TryDecodeWithWin32(bytes, CP_GB18030, MB_ERR_INVALID_CHARS);
            if (gb18030Text != null)
            {
                return gb18030Text;
            }

            string gbkText = TryDecodeWithWin32(bytes, CP_GBK, MB_ERR_INVALID_CHARS);
            if (gbkText != null)
            {
                return gbkText;
            }

            string ansiText = TryDecodeWithWin32(bytes, CP_ACP, 0);
            if (ansiText != null)
            {
                return ansiText;
            }

            // ---- 4) 兜底：用替换字符解码，保证不抛异常 ----
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// 严格 UTF-8 试验：优先用 Win32 MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS)；
        /// 仅在 kernel32 不可用时退回 BCL 的严格 UTF-8 解码器，避免对 GBK 文件重复解码。
        /// </summary>
        private static string TryDecodeStrictUtf8(byte[] bytes)
        {
            try
            {
                return TryDecodeWithWin32Core(bytes, bytes.Length, CP_UTF8, MB_ERR_INVALID_CHARS);
            }
            catch (DllNotFoundException)
            {
                // 极端环境（非 Windows 内核库）：改用 BCL 严格解码
            }
            catch (EntryPointNotFoundException)
            {
                // 同上
            }

            try
            {
                // throwOnInvalidBytes = true：非法字节序列直接抛 DecoderFallbackException
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        /// <summary>
        /// 调用 Win32 MultiByteToWideChar 解码；返回 null 表示解码失败（调用方需自行兜底）。
        /// </summary>
        private static string TryDecodeWithWin32(byte[] bytes, uint codePage, uint flags)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return TryDecodeWithWin32Core(bytes, bytes.Length, codePage, flags);
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        private static string TryDecodeWithWin32Core(byte[] bytes, int byteCount, uint codePage, uint flags)
        {
            if (bytes == null || byteCount <= 0)
            {
                return string.Empty;
            }

            int needed = MultiByteToWideChar(codePage, flags, bytes, byteCount, null, 0);
            if (needed <= 0)
            {
                return null;
            }

            char[] buffer = new char[needed];
            int written = MultiByteToWideChar(codePage, flags, bytes, byteCount, buffer, needed);
            if (written <= 0)
            {
                return null;
            }

            return new string(buffer, 0, written);
        }

        /// <summary>
        /// 基于 Win32 MultiByteToWideChar 的流式按行解码器：
        /// 按 0x0A（\n）边界切分原始字节（GB18030/GBK/CP_ACP 中 0x0A 永不作为多字节尾字节），
        /// 保证大文件流式读取且不会切破多字节字符。
        /// </summary>
        private sealed class Win32LineDecodingReader : TextReader
        {
            private readonly Stream _stream;
            private readonly uint _codePage;
            private readonly byte[] _ioBuffer = new byte[65536];
            private int _ioPos;
            private int _ioLen;
            private readonly MemoryStream _lineBytes = new MemoryStream(4096);
            private string _currentChunk = string.Empty;
            private int _chunkPos;
            private bool _eof;

            public Win32LineDecodingReader(Stream stream, uint codePage)
            {
                _stream = stream;
                _codePage = codePage;
            }

            public override int Read()
            {
                if (_chunkPos >= _currentChunk.Length)
                {
                    if (!FillNextChunk())
                    {
                        return -1;
                    }
                }

                return _currentChunk[_chunkPos++];
            }

            private bool FillNextChunk()
            {
                while (true)
                {
                    if (_eof)
                    {
                        return false;
                    }

                    _lineBytes.SetLength(0);
                    bool foundLf = false;

                    while (!foundLf)
                    {
                        if (_ioPos >= _ioLen)
                        {
                            _ioLen = _stream.Read(_ioBuffer, 0, _ioBuffer.Length);
                            _ioPos = 0;
                            if (_ioLen == 0)
                            {
                                _eof = true;
                                break;
                            }
                        }

                        int start = _ioPos;
                        int lfIndex = Array.IndexOf(_ioBuffer, (byte)'\n', start, _ioLen - start);
                        if (lfIndex >= 0)
                        {
                            _lineBytes.Write(_ioBuffer, start, lfIndex - start + 1);
                            _ioPos = lfIndex + 1;
                            foundLf = true;
                        }
                        else
                        {
                            _lineBytes.Write(_ioBuffer, start, _ioLen - start);
                            _ioPos = _ioLen;
                        }
                    }

                    int count = (int)_lineBytes.Length;
                    if (count == 0)
                    {
                        if (_eof)
                        {
                            return false;
                        }

                        continue;
                    }

                    byte[] raw = _lineBytes.GetBuffer();
                    string decoded = null;
                    try
                    {
                        decoded = TryDecodeWithWin32Core(raw, count, _codePage, 0);
                    }
                    catch (DllNotFoundException)
                    {
                    }
                    catch (EntryPointNotFoundException)
                    {
                    }

                    if (decoded == null)
                    {
                        decoded = Encoding.UTF8.GetString(raw, 0, count);
                    }

                    if (decoded.Length > 0)
                    {
                        _currentChunk = decoded;
                        _chunkPos = 0;
                        return true;
                    }
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _lineBytes.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
