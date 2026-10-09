using System;
using System.Runtime.InteropServices;

namespace DataManager.Interop
{
    /// <summary>
    /// winsqlite3.dll（Windows 10 起随系统提供的 SQLite 3.43.x）的 P/Invoke 声明。
    /// 仅声明本程序实际用到的入口点，不依赖任何第三方包。
    /// </summary>
    internal static class SqliteNative
    {
        private const string Dll = "winsqlite3.dll";

        // ---- 结果码 ----
        public const int SQLITE_OK = 0;
        public const int SQLITE_ERROR = 1;
        public const int SQLITE_BUSY = 5;
        public const int SQLITE_LOCKED = 6;
        public const int SQLITE_READONLY = 8;
        public const int SQLITE_CORRUPT = 11;
        public const int SQLITE_FULL = 13;
        public const int SQLITE_CANTOPEN = 14;
        public const int SQLITE_CONSTRAINT = 19;
        public const int SQLITE_MISUSE = 21;
        public const int SQLITE_ROW = 100;
        public const int SQLITE_DONE = 101;

        // 扩展结果码
        public const int SQLITE_CONSTRAINT_UNIQUE = 2067;
        public const int SQLITE_CONSTRAINT_PRIMARYKEY = 1555;

        // ---- open_v2 标志 ----
        public const int SQLITE_OPEN_READONLY = 0x00000001;
        public const int SQLITE_OPEN_READWRITE = 0x00000002;
        public const int SQLITE_OPEN_CREATE = 0x00000004;
        public const int SQLITE_OPEN_URI = 0x00000040;
        public const int SQLITE_OPEN_NOMUTEX = 0x00008000;
        public const int SQLITE_OPEN_FULLMUTEX = 0x00010000;

        // ---- 列类型 ----
        public const int SQLITE_INTEGER = 1;
        public const int SQLITE_FLOAT = 2;
        public const int SQLITE_TEXT = 3;
        public const int SQLITE_BLOB = 4;
        public const int SQLITE_NULL = 5;

        // ---- 文本编码 ----
        public const int SQLITE_UTF8 = 1;

        // ---- limit 类别 ----
        public const int SQLITE_LIMIT_LENGTH = 0;
        public const int SQLITE_LIMIT_SQL_LENGTH = 1;
        public const int SQLITE_LIMIT_COLUMN = 2;
        public const int SQLITE_LIMIT_VARIABLE_NUMBER = 9;

        /// <summary>SQLITE_TRANSIENT：让 SQLite 自行复制传入的字符串。</summary>
        public static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        // ---------------------------------------------------------------
        // 打开 / 关闭
        // ---------------------------------------------------------------

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
        public static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close_v2")]
        public static extern int sqlite3_close_v2(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
        public static extern IntPtr sqlite3_errmsg(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errcode")]
        public static extern int sqlite3_errcode(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_extended_errcode")]
        public static extern int sqlite3_extended_errcode(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_extended_result_codes")]
        public static extern int sqlite3_extended_result_codes(IntPtr db, int onoff);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_busy_timeout")]
        public static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_changes")]
        public static extern int sqlite3_changes(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_last_insert_rowid")]
        public static extern long sqlite3_last_insert_rowid(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_libversion")]
        public static extern IntPtr sqlite3_libversion();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errstr")]
        public static extern IntPtr sqlite3_errstr(int resultCode);

        /// <summary>
        /// 结果码的英文描述（sqlite3_errstr）。注意它描述的是“结果码”本身，
        /// 与 sqlite3_errmsg（描述某个连接上最后一次错误）不同——
        /// 排查备份失败时必须用它，否则从目标连接读 errmsg 只会得到 “not an error”。
        /// </summary>
        public static string ErrorText(int resultCode)
        {
            try
            {
                IntPtr ptr = sqlite3_errstr(resultCode);
                if (ptr != IntPtr.Zero)
                {
                    string text = Marshal.PtrToStringUTF8(ptr);
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
            }
            catch (EntryPointNotFoundException)
            {
                // 老版本 winsqlite3.dll 没有该入口时退回到纯数字码
            }
            catch (DllNotFoundException)
            {
            }

            return "错误码 " + resultCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------
        // 语句
        // ---------------------------------------------------------------

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
        public static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, out IntPtr pzTail);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
        public static extern int sqlite3_step(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_reset")]
        public static extern int sqlite3_reset(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_clear_bindings")]
        public static extern int sqlite3_clear_bindings(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
        public static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_count")]
        public static extern int sqlite3_column_count(IntPtr stmt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_type")]
        public static extern int sqlite3_column_type(IntPtr stmt, int iCol);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
        public static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_bytes")]
        public static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int64")]
        public static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_null")]
        public static extern int sqlite3_bind_null(IntPtr stmt, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_int64")]
        public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_text")]
        public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);

        // ---------------------------------------------------------------
        // 在线备份（用于本地/远端备份，保证拿到一致的数据库快照）
        // ---------------------------------------------------------------

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_init")]
        public static extern IntPtr sqlite3_backup_init(IntPtr pDest, byte[] zDestName, IntPtr pSource, byte[] zSourceName);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_step")]
        public static extern int sqlite3_backup_step(IntPtr pBackup, int nPage);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_finish")]
        public static extern int sqlite3_backup_finish(IntPtr pBackup);

        // ---------------------------------------------------------------
        // 辅助
        // ---------------------------------------------------------------

        public static string Utf8ToString(IntPtr ptr, int byteLen)
        {
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            return byteLen <= 0 ? string.Empty : Marshal.PtrToStringUTF8(ptr, byteLen);
        }

        /// <summary>把托管字符串转成以 NUL 结尾的 UTF-8 字节数组。</summary>
        public static byte[] Utf8Z(string text)
        {
            if (text == null)
            {
                return new byte[] { 0 };
            }

            int count = System.Text.Encoding.UTF8.GetByteCount(text);
            byte[] buffer = new byte[count + 1];
            System.Text.Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0);
            buffer[count] = 0;
            return buffer;
        }
    }
}
