using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DataManager.Interop;

namespace DataManager.Data
{
    /// <summary>SQL 文本构造辅助。</summary>
    public static class Sql
    {
        /// <summary>把标识符（表名/列名）安全地包成双引号形式。</summary>
        public static string Ident(string name)
        {
            return "\"" + (name ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        /// <summary>把值转成 SQL 字符串字面量。</summary>
        public static string Literal(string value)
        {
            if (value == null)
            {
                return "NULL";
            }

            return "'" + value.Replace("'", "''") + "'";
        }

        /// <summary>LIKE 通配转义：把 % _ \ 转义掉，配合 ESCAPE '\' 使用。</summary>
        public static string EscapeLike(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                if (c == '%' || c == '_' || c == '\\')
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>列定义名：c0 / c1 / c2 ...</summary>
        public static string ColumnName(int ordinal)
        {
            return "c" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>预编译语句，可重复绑定执行。</summary>
    public sealed class SqliteStatement : IDisposable
    {
        private readonly SqliteConnection _owner;
        private IntPtr _stmt;
        private bool _disposed;

        internal SqliteStatement(SqliteConnection owner, IntPtr stmt)
        {
            _owner = owner;
            _stmt = stmt;
        }

        internal IntPtr Handle => _stmt;

        public SqliteStatement Bind(int index, string value)
        {
            int rc;
            if (value == null)
            {
                rc = SqliteNative.sqlite3_bind_null(_stmt, index);
            }
            else
            {
                byte[] bytes = SqliteNative.Utf8Z(value);
                rc = SqliteNative.sqlite3_bind_text(_stmt, index, bytes, bytes.Length - 1, SqliteNative.SQLITE_TRANSIENT);
            }

            _owner.Check(rc, "绑定参数失败");
            return this;
        }

        public SqliteStatement Bind(int index, long value)
        {
            _owner.Check(SqliteNative.sqlite3_bind_int64(_stmt, index, value), "绑定参数失败");
            return this;
        }

        /// <summary>执行一步，返回 SQLITE_ROW / SQLITE_DONE 或抛错。</summary>
        public int Step()
        {
            int rc = SqliteNative.sqlite3_step(_stmt);
            if (rc == SqliteNative.SQLITE_ROW || rc == SqliteNative.SQLITE_DONE)
            {
                return rc;
            }

            _owner.Check(rc, "执行 SQL 失败");
            return rc;
        }

        public bool Read()
        {
            return Step() == SqliteNative.SQLITE_ROW;
        }

        public int ColumnCount => SqliteNative.sqlite3_column_count(_stmt);

        public bool IsNull(int index)
        {
            return SqliteNative.sqlite3_column_type(_stmt, index) == SqliteNative.SQLITE_NULL;
        }

        public string GetString(int index)
        {
            if (IsNull(index))
            {
                return null;
            }

            IntPtr ptr = SqliteNative.sqlite3_column_text(_stmt, index);
            int len = SqliteNative.sqlite3_column_bytes(_stmt, index);
            return SqliteNative.Utf8ToString(ptr, len);
        }

        public long GetInt64(int index)
        {
            return IsNull(index) ? 0L : SqliteNative.sqlite3_column_int64(_stmt, index);
        }

        /// <summary>读取当前行所有列为字符串（NULL → null）。</summary>
        public string[] GetRow()
        {
            int n = ColumnCount;
            var row = new string[n];
            for (int i = 0; i < n; i++)
            {
                row[i] = GetString(i);
            }

            return row;
        }

        public void Reset()
        {
            SqliteNative.sqlite3_reset(_stmt);
            SqliteNative.sqlite3_clear_bindings(_stmt);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_stmt != IntPtr.Zero)
            {
                SqliteNative.sqlite3_finalize(_stmt);
                _stmt = IntPtr.Zero;
            }
        }
    }

    /// <summary>轻量 SQLite 连接（基于 Windows 自带的 winsqlite3.dll）。</summary>
    public sealed class SqliteConnection : IDisposable
    {
        private readonly object _gate = new object();
        private IntPtr _db;
        private string _path;
        private bool _disposed;
        private int _txDepth;

        public bool IsOpen
        {
            get
            {
                lock (_gate)
                {
                    return _db != IntPtr.Zero;
                }
            }
        }

        /// <summary>当前是否处于未提交的事务中（含 savepoint 嵌套）。</summary>
        public bool HasOpenTransaction
        {
            get
            {
                lock (_gate)
                {
                    return _txDepth > 0;
                }
            }
        }

        /// <summary>当前数据库文件路径；未打开时为 null。</summary>
        public string Path
        {
            get
            {
                lock (_gate)
                {
                    return _path;
                }
            }
        }

        public void Open(string filePath)
        {
            Open(filePath, SqliteNative.SQLITE_OPEN_READWRITE | SqliteNative.SQLITE_OPEN_CREATE);
        }

        /// <summary>
        /// 以只读方式打开数据库。用于在已有写事务的连接之外，
        /// 另开一条连接读取“已提交”的数据（例如设置窗口打开期间做备份）。
        /// </summary>
        public void OpenReadOnly(string filePath)
        {
            Open(filePath, SqliteNative.SQLITE_OPEN_READONLY);
        }

        private void Open(string filePath, int openFlags)
        {
            lock (_gate)
            {
                if (_db != IntPtr.Zero)
                {
                    return;
                }

                byte[] path = SqliteNative.Utf8Z(filePath);
                int flags = openFlags | SqliteNative.SQLITE_OPEN_FULLMUTEX;
                int rc = SqliteNative.sqlite3_open_v2(path, out IntPtr db, flags, IntPtr.Zero);
                if (rc != SqliteNative.SQLITE_OK)
                {
                    if (db != IntPtr.Zero)
                    {
                        string msg = ErrorMessage(db);
                        SqliteNative.sqlite3_close_v2(db);
                        throw new SqliteException(rc, "无法打开数据库：" + msg);
                    }

                    throw new SqliteException(rc, "无法打开数据库：" + filePath);
                }

                _db = db;
                _path = filePath;
                SqliteNative.sqlite3_extended_result_codes(_db, 1);
                SqliteNative.sqlite3_busy_timeout(_db, 8000);
            }
        }

        internal void Check(int rc, string what)
        {
            if (rc == SqliteNative.SQLITE_OK || rc == SqliteNative.SQLITE_ROW || rc == SqliteNative.SQLITE_DONE)
            {
                return;
            }

            throw new SqliteException(SqliteNative.sqlite3_extended_errcode(_db), what + "：" + ErrorMessage(_db));
        }

        private static string ErrorMessage(IntPtr db)
        {
            IntPtr ptr = SqliteNative.sqlite3_errmsg(db);
            return ptr == IntPtr.Zero ? "未知错误" : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr) ?? "未知错误";
        }

        public string LastErrorMessage()
        {
            lock (_gate)
            {
                return _db == IntPtr.Zero ? "数据库未打开" : ErrorMessage(_db);
            }
        }

        public int LastErrorCode
        {
            get
            {
                lock (_gate)
                {
                    return _db == IntPtr.Zero ? 0 : SqliteNative.sqlite3_extended_errcode(_db);
                }
            }
        }

        public int Changes
        {
            get
            {
                lock (_gate)
                {
                    return _db == IntPtr.Zero ? 0 : SqliteNative.sqlite3_changes(_db);
                }
            }
        }

        public long LastInsertRowId
        {
            get
            {
                lock (_gate)
                {
                    return _db == IntPtr.Zero ? 0L : SqliteNative.sqlite3_last_insert_rowid(_db);
                }
            }
        }

        public SqliteStatement Prepare(string sql)
        {
            lock (_gate)
            {
                EnsureOpen();
                byte[] bytes = SqliteNative.Utf8Z(sql);
                int rc = SqliteNative.sqlite3_prepare_v2(_db, bytes, -1, out IntPtr stmt, out _);
                Check(rc, "编译 SQL 失败");
                if (stmt == IntPtr.Zero)
                {
                    throw new SqliteException(SqliteNative.SQLITE_ERROR, "空语句：" + sql);
                }

                return new SqliteStatement(this, stmt);
            }
        }

        /// <summary>执行不返回结果集的语句。</summary>
        public int Execute(string sql)
        {
            using (SqliteStatement st = Prepare(sql))
            {
                while (st.Step() == SqliteNative.SQLITE_ROW)
                {
                    // 丢弃结果行
                }
            }

            return Changes;
        }

        /// <summary>执行非查询语句，返回受影响行数。</summary>
        public int Execute(string sql, params string[] parameters)
        {
            using (SqliteStatement st = Prepare(sql))
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    st.Bind(i + 1, parameters[i]);
                }

                while (st.Step() == SqliteNative.SQLITE_ROW)
                {
                }
            }

            return Changes;
        }

        /// <summary>查询并返回全部行（每行为字符串数组，NULL 为 null）。</summary>
        public List<string[]> Query(string sql)
        {
            var rows = new List<string[]>();
            using (SqliteStatement st = Prepare(sql))
            {
                while (st.Read())
                {
                    rows.Add(st.GetRow());
                }
            }

            return rows;
        }

        public List<string[]> Query(string sql, params string[] parameters)
        {
            var rows = new List<string[]>();
            using (SqliteStatement st = Prepare(sql))
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    st.Bind(i + 1, parameters[i]);
                }

                while (st.Read())
                {
                    rows.Add(st.GetRow());
                }
            }

            return rows;
        }

        /// <summary>查询单个标量值（首行首列）。</summary>
        public string Scalar(string sql)
        {
            using (SqliteStatement st = Prepare(sql))
            {
                if (st.Read())
                {
                    return st.GetString(0);
                }
            }

            return null;
        }

        public string Scalar(string sql, params string[] parameters)
        {
            using (SqliteStatement st = Prepare(sql))
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    st.Bind(i + 1, parameters[i]);
                }

                if (st.Read())
                {
                    return st.GetString(0);
                }
            }

            return null;
        }

        public long ScalarLong(string sql, params string[] parameters)
        {
            using (SqliteStatement st = Prepare(sql))
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    st.Bind(i + 1, parameters[i]);
                }

                return st.Read() ? st.GetInt64(0) : 0L;
            }
        }

        // ---------------- 事务 ----------------

        public void BeginTransaction()
        {
            lock (_gate)
            {
                if (_txDepth == 0)
                {
                    Execute("BEGIN IMMEDIATE");
                }
                else
                {
                    Execute("SAVEPOINT sp" + _txDepth);
                }

                _txDepth++;
            }
        }

        public void Commit()
        {
            lock (_gate)
            {
                if (_txDepth <= 0)
                {
                    return;
                }

                _txDepth--;
                Execute(_txDepth == 0 ? "COMMIT" : "RELEASE sp" + _txDepth);
            }
        }

        public void Rollback()
        {
            lock (_gate)
            {
                if (_txDepth <= 0)
                {
                    return;
                }

                _txDepth--;
                try
                {
                    Execute(_txDepth == 0 ? "ROLLBACK" : "ROLLBACK TO sp" + _txDepth);
                    if (_txDepth > 0)
                    {
                        Execute("RELEASE sp" + _txDepth);
                    }
                }
                catch (SqliteException)
                {
                    // 回滚失败时忽略，交由上层处理
                }
            }
        }

        /// <summary>
        /// 开启一个作用域事务。必须显式调用 <see cref="Tx.Complete"/> 才会提交；
        /// 未调用就离开作用域（包括抛异常）时自动回滚。
        /// </summary>
        public Tx Transaction()
        {
            return new Tx(this);
        }

        /// <summary>事务作用域：Complete() 提交，否则 Dispose 时回滚。</summary>
        public sealed class Tx : IDisposable
        {
            private readonly SqliteConnection _c;
            private bool _done;
            private bool _completed;

            internal Tx(SqliteConnection c)
            {
                _c = c;
                _c.BeginTransaction();
            }

            /// <summary>提交事务。</summary>
            public void Complete()
            {
                if (_done)
                {
                    return;
                }

                _done = true;
                _completed = true;
                _c.Commit();
            }

            public void Dispose()
            {
                if (_done)
                {
                    return;
                }

                _done = true;

                if (!_completed)
                {
                    _c.Rollback();
                }
            }
        }

        private void EnsureOpen()
        {
            if (_db == IntPtr.Zero)
            {
                throw new InvalidOperationException("数据库尚未打开。");
            }
        }

        /// <summary>执行 VACUUM，压紧数据库文件。</summary>
        public void Vacuum()
        {
            Execute("VACUUM");
        }

        /// <summary>
        /// 把当前库在线备份到目标文件（线程安全、结果一致）。
        ///
        /// 先写 “&lt;目标&gt;.partial”，全部成功后才替换目标文件；任何失败都会清掉临时文件，
        /// 因此不会留下 0 字节的残缺备份。
        ///
        /// 关于“自己锁自己”：若本连接上还挂着未提交事务（例如设置窗口打开期间的建表/改列事务），
        /// 直接以本连接为备份源会被自己的写锁挡住，sqlite3_backup_step / sqlite3_backup_finish
        /// 双双返回 SQLITE_BUSY。这种情况下改用一条独立的只读连接读取已提交状态：
        /// 既不撞锁，语义上也正好是“只备份已保存的数据”。
        /// </summary>
        public void BackupTo(string destinationPath)
        {
            if (string.IsNullOrEmpty(destinationPath))
            {
                throw new ArgumentException("备份目标路径不能为空。", nameof(destinationPath));
            }

            lock (_gate)
            {
                EnsureOpen();

                string tmp = destinationPath + ".partial";
                bool completed = false;

                try
                {
                    string directory = System.IO.Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        System.IO.Directory.CreateDirectory(directory);
                    }

                    DeleteFileQuietly(tmp);

                    SqliteConnection dedicatedSource = null;
                    try
                    {
                        IntPtr source = _db;

                        if (_txDepth > 0 && !string.IsNullOrEmpty(_path))
                        {
                            dedicatedSource = new SqliteConnection();
                            dedicatedSource.OpenReadOnly(_path);
                            source = dedicatedSource._db;
                        }

                        RunBackup(source, tmp, dedicatedSource ?? this);
                    }
                    finally
                    {
                        // 必须先关闭目标连接，Windows 上才能移动/覆盖已打开的文件。
                        if (dedicatedSource != null)
                        {
                            dedicatedSource.Dispose();
                        }
                    }

                    System.IO.File.Move(tmp, destinationPath, true);
                    completed = true;
                }
                catch (SqliteException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException ||
                                           ex is UnauthorizedAccessException ||
                                           ex is NotSupportedException ||
                                           ex is ArgumentException)
                {
                    // 把 .NET 的英文 IO 异常翻译成用户能看懂的中文提示
                    throw new SqliteException(
                        SqliteNative.SQLITE_CANTOPEN,
                        "备份数据库失败：无法写入备份文件（" + destinationPath + "）。\n" +
                        "请检查该目录是否存在、是否有写入权限、磁盘是否已满。\n" +
                        "系统提示：" + ex.Message,
                        ex);
                }
                finally
                {
                    if (!completed)
                    {
                        DeleteFileQuietly(tmp);
                    }
                }
            }
        }

        /// <summary>执行一次完整的 backup_init → backup_step → backup_finish。</summary>
        private void RunBackup(IntPtr sourceHandle, string tmpPath, SqliteConnection errorSource)
        {
            using (var dest = new SqliteConnection())
            {
                dest.Open(tmpPath);

                IntPtr handle = SqliteNative.sqlite3_backup_init(
                    dest._db, SqliteNative.Utf8Z("main"), sourceHandle, SqliteNative.Utf8Z("main"));

                if (handle == IntPtr.Zero)
                {
                    int initCode = SqliteNative.sqlite3_errcode(dest._db);
                    throw new SqliteException(initCode, "备份数据库失败：" + DescribeBackupError(initCode, dest, errorSource));
                }

                int stepCode = SqliteNative.SQLITE_DONE;
                try
                {
                    stepCode = StepBackupWithRetry(handle);
                }
                finally
                {
                    int finishCode = SqliteNative.sqlite3_backup_finish(handle);
                    if (finishCode != SqliteNative.SQLITE_OK && stepCode == SqliteNative.SQLITE_DONE)
                    {
                        stepCode = finishCode;
                    }
                }

                if (stepCode != SqliteNative.SQLITE_DONE)
                {
                    throw new SqliteException(stepCode, "备份数据库失败：" + DescribeBackupError(stepCode, dest, errorSource));
                }
            }
        }

        /// <summary>
        /// 反复调用 backup_step 直到完成。sqlite3_backup_step 不会触发 busy handler，
        /// 遇到锁必须由调用方自己等待重试。
        /// </summary>
        private static int StepBackupWithRetry(IntPtr handle)
        {
            const int MaxAttempts = 20;

            for (int attempt = 1; ; attempt++)
            {
                int rc = SqliteNative.sqlite3_backup_step(handle, -1);
                if (rc == SqliteNative.SQLITE_DONE)
                {
                    return rc;
                }

                if ((rc == SqliteNative.SQLITE_BUSY || rc == SqliteNative.SQLITE_LOCKED) && attempt < MaxAttempts)
                {
                    Thread.Sleep(100);
                    continue;
                }

                return rc;
            }
        }

        /// <summary>
        /// 把备份失败翻译成能看懂的中文。注意不要只读目标连接的 errmsg——
        /// 备份失败时错误信息往往挂在源连接上，读目标连接只会得到 “not an error”。
        /// </summary>
        private static string DescribeBackupError(int resultCode, SqliteConnection destination, SqliteConnection source)
        {
            if (resultCode == SqliteNative.SQLITE_BUSY || resultCode == SqliteNative.SQLITE_LOCKED)
            {
                return "数据库正被占用，备份无法取得一致的快照。\n" +
                       "请关闭设置窗口（或其他正在改动表结构的窗口）后重试。";
            }

            if (resultCode == SqliteNative.SQLITE_READONLY)
            {
                return "数据库或备份目录为只读，无法写入。";
            }

            if (resultCode == SqliteNative.SQLITE_FULL)
            {
                return "磁盘空间不足，无法写入备份。";
            }

            if (resultCode == SqliteNative.SQLITE_CORRUPT)
            {
                return "数据库文件已损坏，无法备份。";
            }

            string detail = ReadConnectionError(source) ?? ReadConnectionError(destination);
            string text = SqliteNative.ErrorText(resultCode);

            return detail == null ? text : text + "（" + detail + "）";
        }

        /// <summary>读取某连接上的错误消息；“not an error” 视为没有信息。</summary>
        private static string ReadConnectionError(SqliteConnection connection)
        {
            if (connection == null)
            {
                return null;
            }

            try
            {
                string message = connection.LastErrorMessage();
                if (string.IsNullOrWhiteSpace(message) ||
                    string.Equals(message, "not an error", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return message;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void DeleteFileQuietly(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 清理临时文件失败不影响主流程
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>关闭当前连接（用于恢复备份后重启前释放文件句柄）。</summary>
        public void Close()
        {
            lock (_gate)
            {
                if (_db == IntPtr.Zero)
                {
                    return;
                }

                _txDepth = 0;
                SqliteNative.sqlite3_close_v2(_db);
                _db = IntPtr.Zero;
                _path = null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Close();
        }
    }
}
