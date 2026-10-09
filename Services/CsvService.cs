using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DataManager.Services
{
    /// <summary>
    /// CSV 读写服务：RFC 4180 风格的解析与转义，支持 BOM 探测与 UTF-8 / 本地 ANSI 编码的自动识别。
    /// </summary>
    /// <remarks>
    /// 本机 NuGet 不可达，且 System.Text.Encoding.CodePages 不可用，
    /// 因此 GBK(936) 等传统代码页一律通过 Win32 的 MultiByteToWideChar 解码；
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

        /// <summary>CP_UTF8：UTF-8 代码页。</summary>
        private const uint CP_UTF8 = 65001;

        /// <summary>MB_ERR_INVALID_CHARS：遇到非法字节序列时让 MultiByteToWideChar 直接返回失败。</summary>
        private const uint MB_ERR_INVALID_CHARS = 0x00000008;

        /// <summary>
        /// MultiByteToWideChar：把多字节串按指定代码页解码为 UTF-16。
        /// </summary>
        /// <remarks>
        /// 必须指定 CharSet.Unicode（并对输出数组写明 ArraySubType = U2）：
        /// 否则 char[] 会按默认的 CharSet.Ansi 编组为“每字符 1 字节”，
        /// 与 Win32 写入的 UTF-16（每字符 2 字节）不匹配，导致结果里混入 \0 字符。
        /// ExactSpelling = true 保证按原名绑定导出符号，不追加 A/W 后缀。
        /// </remarks>
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
        /// 自动探测编码：BOM(UTF-8/UTF-16LE/BE/UTF-32) 优先；无 BOM 时先尝试严格 UTF-8，
        /// 失败则回退到 Win32 MultiByteToWideChar(CP_ACP) 解码（中文 Windows 即 GBK/936）。
        /// </summary>
        /// <param name="path">CSV 文件路径。</param>
        /// <returns>逐行的字段数组；空文件返回 0 行。</returns>
        /// <exception cref="IOException">文件不存在、无权限或读取失败时抛出（消息为中文）。</exception>
        public static List<string[]> ReadFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new IOException("读取 CSV 文件失败：文件路径为空。");
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                throw new IOException("读取 CSV 文件失败：" + path + "。原因：" + ex.Message, ex);
            }

            return Parse(DecodeBytes(bytes));
        }

        /// <summary>
        /// 从字符串解析（用于测试/剪贴板），使用与 ReadFile 相同的分词逻辑，不做编码探测。
        /// </summary>
        /// <param name="text">CSV 文本；null 或空串返回 0 行。</param>
        /// <returns>逐行的字段数组，字段永不为 null（缺失字段为 ""）。</returns>
        public static List<string[]> Parse(string text)
        {
            List<string[]> rows = new List<string[]>();
            if (string.IsNullOrEmpty(text))
            {
                return rows;
            }

            int length = text.Length;
            int pos = 0;

            // 外部来源的文本可能残留 BOM 字符，这里先跳过，避免污染第一个字段。
            if (text[0] == '\uFEFF')
            {
                pos = 1;
            }

            // 逐行解析：空文件或只有 BOM 时不会产生任何行。
            while (pos < length)
            {
                List<string> fields = new List<string>();
                bool rowFinished = false;

                while (!rowFinished)
                {
                    StringBuilder field = new StringBuilder();

                    if (pos < length && text[pos] == '"')
                    {
                        // ---- 引号字段：内部可包含 , " CR LF ----
                        pos++; // 跳过起始引号
                        while (pos < length)
                        {
                            char c = text[pos];
                            if (c == '"')
                            {
                                if (pos + 1 < length && text[pos + 1] == '"')
                                {
                                    // 成对引号代表一个字面量引号
                                    field.Append('"');
                                    pos += 2;
                                    continue;
                                }

                                pos++; // 结束引号
                                break;
                            }

                            field.Append(c);
                            pos++;
                        }

                        // 宽容处理：结束引号与分隔符之间若还有字符（如 "a"b），按字面量追加。
                        while (pos < length && text[pos] != ',' && text[pos] != '\r' && text[pos] != '\n')
                        {
                            field.Append(text[pos]);
                            pos++;
                        }
                    }
                    else
                    {
                        // ---- 普通字段：直接读到分隔符或行尾 ----
                        while (pos < length && text[pos] != ',' && text[pos] != '\r' && text[pos] != '\n')
                        {
                            field.Append(text[pos]);
                            pos++;
                        }
                    }

                    fields.Add(field.ToString());

                    if (pos >= length)
                    {
                        // 文件末尾没有换行符：当前行同样有效。
                        rowFinished = true;
                    }
                    else if (text[pos] == ',')
                    {
                        pos++; // 继续解析本行的下一个字段
                    }
                    else
                    {
                        // 行终止符：兼容 CRLF / LF / CR 三种写法。
                        if (text[pos] == '\r')
                        {
                            pos++;
                            if (pos < length && text[pos] == '\n')
                            {
                                pos++;
                            }
                        }
                        else
                        {
                            pos++;
                        }

                        rowFinished = true;
                    }
                }

                rows.Add(fields.ToArray());
            }

            return rows;
        }

        /// <summary>
        /// 把一行字段序列化为一行 CSV 文本（不含换行符）。
        /// </summary>
        /// <param name="fields">字段数组；null 或空数组返回空串。</param>
        public static string FormatRow(string[] fields)
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

                sb.Append(EscapeField(fields[i]));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 写出 CSV 文件。UTF-8 with BOM（便于 Excel 正确识别中文）。每行 CRLF 结尾。
        /// </summary>
        /// <param name="path">目标文件路径。</param>
        /// <param name="rows">数据行；null 视为 0 行。</param>
        /// <exception cref="IOException">路径为空或写入失败时抛出（消息为中文）。</exception>
        public static void WriteFile(string path, IEnumerable<string[]> rows)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new IOException("写入 CSV 文件失败：文件路径为空。");
            }

            try
            {
                // UTF8Encoding(true)：写入 BOM，Excel 打开时才能正确识别中文。
                using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(true)))
                {
                    if (rows != null)
                    {
                        foreach (string[] row in rows)
                        {
                            writer.Write(FormatRow(row));
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
        /// 单个字段转义：包含 , " \r \n 时用双引号包裹，内部 " 变成 ""。
        /// </summary>
        /// <param name="value">字段值；null 视为空串。</param>
        public static string EscapeField(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
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
        // 编码探测（私有）
        // ------------------------------------------------------------------

        /// <summary>
        /// 把文件字节解码为文本：BOM 优先，其次严格 UTF-8，最后回退到系统 ANSI 代码页。
        /// </summary>
        private static string DecodeBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            // ---- 1) BOM 探测（UTF-32 的 BOM 必须以 UTF-16 的 BOM 之前判断）----
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

            // ---- 2) 无 BOM：先做严格 UTF-8 试验 ----
            string utf8Text = TryDecodeStrictUtf8(bytes);
            if (utf8Text != null)
            {
                return utf8Text;
            }

            // ---- 3) 回退：系统 ANSI 代码页（中文 Windows 即 GBK/936）----
            string ansiText = TryDecodeWithWin32(bytes, CP_ACP, 0);
            if (ansiText != null)
            {
                return ansiText;
            }

            // ---- 4) 兜底：用替换字符解码，保证不抛异常 ----
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// 严格 UTF-8 试验：优先用 Win32 MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS)，
        /// 失败返回 null；kernel32 不可用时退回 BCL 的严格 UTF-8 解码器。
        /// </summary>
        private static string TryDecodeStrictUtf8(byte[] bytes)
        {
            try
            {
                string text = TryDecodeWithWin32(bytes, CP_UTF8, MB_ERR_INVALID_CHARS);
                if (text != null)
                {
                    return text;
                }
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

            int needed = MultiByteToWideChar(codePage, flags, bytes, bytes.Length, null, 0);
            if (needed <= 0)
            {
                return null; // 例如 CP_ACP 遇到无法映射的字节
            }

            char[] buffer = new char[needed];
            int written = MultiByteToWideChar(codePage, flags, bytes, bytes.Length, buffer, needed);
            if (written <= 0)
            {
                return null;
            }

            return new string(buffer, 0, written);
        }
    }
}
