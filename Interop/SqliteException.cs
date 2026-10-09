using System;

namespace DataManager.Interop
{
    /// <summary>SQLite 调用失败时抛出的异常。</summary>
    public sealed class SqliteException : Exception
    {
        public SqliteException(int resultCode, string message)
            : base(message)
        {
            ResultCode = resultCode;
        }

        public SqliteException(int resultCode, string message, Exception inner)
            : base(message, inner)
        {
            ResultCode = resultCode;
        }

        public int ResultCode { get; }

        /// <summary>是否是唯一性约束冲突。</summary>
        public bool IsUniqueViolation
        {
            get
            {
                if (ResultCode == SqliteNative.SQLITE_CONSTRAINT_UNIQUE ||
                    ResultCode == SqliteNative.SQLITE_CONSTRAINT_PRIMARYKEY)
                {
                    return true;
                }

                // 扩展结果码未开启时只会得到 SQLITE_CONSTRAINT
                return ResultCode == SqliteNative.SQLITE_CONSTRAINT &&
                       Message != null &&
                       Message.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }
}
