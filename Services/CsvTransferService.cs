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
        /// <summary>导入 CSV 时允许的最大列数，防止畸形文件导致超宽表或 SQLite 列数超限。</summary>
        public const int MaxImportColumns = 1000;

        /// <summary>清洗后文件名的最大字符数（不含扩展名）。</summary>
        private const int MaxSanitizedFileNameLength = 100;

        private static readonly HashSet<string> WindowsReservedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

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
        /// 使用流式读取降低大文件内存峰值，并校验最大列数（不超过 1000 列）。
        /// 表名取 CSV 文件名。返回新表的 id（表名允许重复，不能靠名字选中）。
        /// </summary>
        public long ImportAsNewTable(string csvPath, IList<AppTable> allTables)
        {
            int rowCount = 0;
            int columnCount = 0;

            // 第一遍流式扫描：确定行数与最大列数，避免把大文件全部加载到内存
            foreach (string[] r in CsvService.ReadRows(csvPath))
            {
                rowCount++;
                if (r.Length > columnCount)
                {
                    columnCount = r.Length;
                    if (columnCount > MaxImportColumns)
                    {
                        throw new InvalidOperationException(
                            "CSV 文件列数超过上限（最多支持 " + MaxImportColumns.ToString(CultureInfo.InvariantCulture) + " 列）。");
                    }
                }
            }

            if (rowCount == 0)
            {
                throw new InvalidOperationException("CSV 文件没有任何数据行。");
            }

            if (columnCount == 0)
            {
                throw new InvalidOperationException("CSV 文件没有任何数据列。");
            }

            string name = TableNameFromFile(csvPath);
            long tableId;

            using (SqliteConnection.Tx tx = _db.Transaction())
            {
                tableId = _schema.CreateTable(name);
                var table = new AppTable { Id = tableId, Name = name };
                for (int i = 0; i < columnCount; i++)
                {
                    table.Columns.Add(_schema.AddColumn(tableId, ColumnFlags.None, false));
                }

                using (SqliteStatement st = PrepareInsert(table, columnCount))
                {
                    // 第二遍流式读取并直接写入 SQLite
                    foreach (string[] record in CsvService.ReadRows(csvPath))
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

            return tableId;
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
        /// 单文件先写临时文件 .tmp 再原子替换，避免中断留下残缺 CSV。
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

                string fileName = MakeUniqueFileName(SanitizeFileName(table.Name), usedNames) + ".csv";
                string full = Path.Combine(targetDirectory, fileName);
                string tmp = full + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                bool moved = false;

                try
                {
                    CsvService.WriteRows(tmp, EnumerateTableRows(table, rows));
                    File.Move(tmp, full, true);
                    moved = true;
                }
                finally
                {
                    if (!moved && File.Exists(tmp))
                    {
                        try
                        {
                            File.Delete(tmp);
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                }

                written.Add(full);
            }

            return written;
        }

        private static IEnumerable<string[]> EnumerateTableRows(AppTable table, List<DataRow> rows)
        {
            int colCount = table.Columns.Count;
            foreach (DataRow r in rows)
            {
                var fields = new string[colCount];
                for (int i = 0; i < colCount; i++)
                {
                    fields[i] = r.Get(i);
                }

                yield return fields;
            }
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

        /// <summary>
        /// 把表名清洗成合法的文件名：替换非法字符、拦截 Windows 保留设备名并截断超长文件名（不超过 100 字符）。
        /// </summary>
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
                if (char.IsControl(c))
                {
                    continue;
                }

                sb.Append(invalid.Contains(c) ? '_' : c);
            }

            string result = sb.ToString().Trim().TrimEnd('.');
            if (result.Length == 0 || result == "." || result == "..")
            {
                return "未命名";
            }

            // 拦截 Windows 保留设备名（CON/PRN/AUX/NUL/COM1-9/LPT1-9）
            int dot = result.IndexOf('.');
            string stem = (dot >= 0 ? result.Substring(0, dot) : result).Trim();
            if (WindowsReservedDeviceNames.Contains(stem))
            {
                result = "_" + result;
            }

            // 截断超长文件名（<= 100 字符）
            if (result.Length > MaxSanitizedFileNameLength)
            {
                result = result.Substring(0, MaxSanitizedFileNameLength).Trim().TrimEnd('.');
                if (result.Length == 0)
                {
                    return "未命名";
                }
            }

            return result;
        }
    }
}
