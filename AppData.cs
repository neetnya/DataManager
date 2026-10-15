using System;
using System.Collections.Generic;
using DataManager.Data;
using DataManager.Models;
using DataManager.Services;

namespace DataManager
{
    /// <summary>
    /// 应用级数据访问入口：持有连接与各个 Store，并向界面暴露当前表状态。
    /// </summary>
    public sealed class AppData : IDisposable
    {
        private readonly SqliteConnection _db = new SqliteConnection();
        private List<AppTable> _tables = new List<AppTable>();

        public AppData()
        {
            AppPaths.EnsureCreated();
            _db.Open(AppPaths.DatabaseFile);

            Schema = new SchemaStore(_db);
            Schema.Initialize();

            Rows = new RowStore(_db, Schema);
            Csv = new CsvTransferService(_db, Schema, Rows);

            ReloadTables();
        }

        public SqliteConnection Connection => _db;

        public SchemaStore Schema { get; }

        public RowStore Rows { get; }

        public CsvTransferService Csv { get; }

        /// <summary>当前所有表（引用会随 ReloadTables 变化，界面应重新读取）。</summary>
        public IReadOnlyList<AppTable> Tables => _tables;

        /// <summary>重新从数据库载入表结构。</summary>
        public void ReloadTables()
        {
            _tables = Schema.LoadTables();
        }

        public AppTable FindTable(long tableId)
        {
            foreach (AppTable t in _tables)
            {
                if (t.Id == tableId)
                {
                    return t;
                }
            }

            return null;
        }

        /// <summary>关闭数据库连接（恢复备份前调用）。</summary>
        public void CloseDatabase()
        {
            _db.Close();
        }

        /// <summary>关闭后再打开主库。恢复失败时用来把当前会话接回去。</summary>
        public void ReopenDatabase()
        {
            _db.Close();
            _db.Open(AppPaths.DatabaseFile);
            Schema.Initialize();
            ReloadTables();
        }

        public void Dispose()
        {
            _db.Dispose();
        }
    }
}
