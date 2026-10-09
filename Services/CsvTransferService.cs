using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DataManager.Data;
using DataManager.Models;

namespace DataManager.Services
{
    /// <summary>CSV 导入 / 导出的业务编排。</summary>
    public sealed class CsvTransferService
    {
        private readonly SqliteConnection _db;
        private readonly SchemaStore _schema;
        private readonly RowStore _rows;

        public CsvTransferService(SqliteConnection db, SchemaStore schema, RowStore rows)
        {
            _db = db;
            _schema = schema;
            _rows = rows;
        }

        /// <summary>
        /// 导入 CSV 为一张新表：按位置对应列，视作无任何配置的列，第一行也是数据。
        /// 表名取 CSV 文件名。返回新表名。
        /// </summary>
        public string ImportAsNewTable(string csvPath, IList<AppTable> allTables)
        {
            List<string[]> records = CsvService.ReadFile(csvPath);
            if (records.Count == 0)
            {
                throw new InvalidOperationException("CSV 文件没有任何数据行。");
            }

            int columnCount = 0;
            foreach (string[] r in records)
            {
                if (r.Length > columnCount)
                {
                    columnCount = r.Length;
                }
            }

            if (columnCount == 0)
            {
                throw new InvalidOperationException("CSV 文件没有任何数据列。");
            }

            string name = TableNameFromFile(csvPath);

            using (SqliteConnection.Tx tx = _db.Transaction())
            {
                long tableId = _schema.CreateTable(name);
                var table = new AppTable { Id = tableId, Name = name };
                for (int i = 0; i < columnCount; i++)
                {
                    table.Columns.Add(_schema.AddColumn(tableId, ColumnFlags.None, false));
                }

                using (SqliteStatement st = PrepareInsert(table, columnCount))
                {
                    foreach (string[] record in records)
                    {
                        st.Reset();
                        for (int i = 0; i < columnCount; i++)
                        {
                            string v = i < record.Length ? record[i] : null;

                            // 按需求：trim 在写入时就处理好
                            v = v?.Trim();

                            st.Bind(i + 1, string.IsNullOrEmpty(v) ? null : v);
                        }

                        st.Step();
                    }
                }

                tx.Complete();
            }

            return name;
        }

        /// <summary>
        /// 用 CSV 的文件名（不含扩展名）作为表名。为空时回退到“未命名01”格式。
        /// </summary>
        public string TableNameFromFile(string csvPath)
        {
            string name = null;
            try
            {
                name = Path.GetFileNameWithoutExtension(csvPath);
            }
            catch (Exception)
            {
                // 路径非法时回退
            }

            name = (name ?? string.Empty).Trim();
            return name.Length == 0 ? _schema.NextUntitledTableName() : name;
        }

        private SqliteStatement PrepareInsert(AppTable table, int columnCount)
        {
            var names = new List<string>();
            var marks = new List<string>();
            for (int i = 0; i < columnCount; i++)
            {
                names.Add(Sql.Ident(Sql.ColumnName(i)));
                marks.Add("?");
            }

            string sql = "INSERT INTO " + Sql.Ident(table.PhysicalName) +
                         " (" + string.Join(", ", names) + ") VALUES (" + string.Join(", ", marks) + ")";
            return _db.Prepare(sql);
        }

        /// <summary>
        /// 导出全部表：每张表一个 CSV，写入目标目录。
        /// 返回生成的文件列表。
        /// </summary>
        public List<string> ExportAll(IList<AppTable> tables, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);

            var written = new List<string>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (AppTable table in tables)
            {
                List<DataRow> rows = _rows.Query(table, new QuerySpec());

                var records = new List<string[]>(rows.Count);
                foreach (DataRow r in rows)
                {
                    var fields = new string[table.Columns.Count];
                    for (int i = 0; i < fields.Length; i++)
                    {
                        fields[i] = r.Get(i);
                    }

                    records.Add(fields);
                }

                string fileName = MakeUniqueFileName(SanitizeFileName(table.Name), usedNames) + ".csv";
                string full = Path.Combine(targetDirectory, fileName);
                CsvService.WriteFile(full, records);
                written.Add(full);
            }

            return written;
        }

        private static string MakeUniqueFileName(string baseName, HashSet<string> used)
        {
            if (used.Add(baseName))
            {
                return baseName;
            }

            for (int i = 2; i < 100000; i++)
            {
                string candidate = baseName + "_" + i.ToString(CultureInfo.InvariantCulture);
                if (used.Add(candidate))
                {
                    return candidate;
                }
            }

            return baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        /// <summary>把表名清洗成合法的文件名。</summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "未命名";
            }

            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(invalid.Contains(c) ? '_' : c);
            }

            string result = sb.ToString().Trim().TrimEnd('.');
            return result.Length == 0 ? "未命名" : result;
        }
    }
}
