using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DataManager.Data;
using DataManager.Models;

namespace DataManager.Services
{
    /// <summary>一次查询的条件。</summary>
    public sealed class QuerySpec
    {
        /// <summary>列 Id → 模糊搜索关键字（空/未给出表示不过滤）。</summary>
        public Dictionary<long, string> ColumnFilters { get; } = new Dictionary<long, string>();

        /// <summary>标签搜索关键字（独立入口）。</summary>
        public string TagFilter { get; set; }

        /// <summary>被“快捷查询”模糊命中后追加的筛选（列 Id + 关键字）。</summary>
        public long? QuickFilterColumnId { get; set; }

        public string QuickFilterText { get; set; }

        /// <summary>当前是否有任何筛选生效。</summary>
        public bool HasAnyFilter
        {
            get
            {
                if (!string.IsNullOrEmpty(TagFilter) || !string.IsNullOrEmpty(QuickFilterText))
                {
                    return true;
                }

                foreach (KeyValuePair<long, string> kv in ColumnFilters)
                {
                    if (!string.IsNullOrEmpty(kv.Value))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>快捷查询的结果。</summary>
    public enum QuickQueryOutcome
    {
        /// <summary>精确命中：已回写剪贴板并定位到该行。</summary>
        Exact,

        /// <summary>未精确命中但模糊命中：已对当前表做筛选。</summary>
        Fuzzy,

        /// <summary>完全无结果。</summary>
        None,
    }

    /// <summary>快捷查询的执行结果。</summary>
    public sealed class QuickQueryResult
    {
        public QuickQueryOutcome Outcome { get; set; }

        /// <summary>被回写到剪贴板的值（仅 Exact 时有意义）。</summary>
        public string ClipboardValue { get; set; }

        /// <summary>需要跳转并选中的行（仅 Exact 时有意义）。</summary>
        public long? RowId { get; set; }

        /// <summary>模糊命中时的筛选列。</summary>
        public AppColumn FilterColumn { get; set; }

        /// <summary>模糊命中时的筛选文本。</summary>
        public string FilterText { get; set; }

        /// <summary>模糊命中的行数。</summary>
        public int MatchCount { get; set; }
    }

    /// <summary>行数据的读写与查询。</summary>
    public sealed class RowStore
    {
        private readonly SqliteConnection _db;
        private readonly SchemaStore _schema;

        public RowStore(SqliteConnection db, SchemaStore schema)
        {
            _db = db;
            _schema = schema;
        }

        private static string Physical(AppTable table)
        {
            return Sql.Ident(table.PhysicalName);
        }

        // ---------------------------------------------------------------
        // 查询
        // ---------------------------------------------------------------

        /// <summary>按固定排序优先级生成 ORDER BY 子句。</summary>
        private static string BuildOrderBy(AppTable table)
        {
            var sortCols = new List<AppColumn>();
            foreach (AppColumn c in table.Columns)
            {
                if (c.IsSort)
                {
                    sortCols.Add(c);
                }
            }

            // 排序优先级由用户在设置里自定义；相同优先级时回退到建表顺序
            sortCols.Sort((a, b) =>
            {
                int c = a.SortPriority.CompareTo(b.SortPriority);
                return c != 0 ? c : a.Ordinal.CompareTo(b.Ordinal);
            });

            if (sortCols.Count == 0)
            {
                // 没有任何排序列时保持插入顺序，保证结果稳定
                return " ORDER BY rid";
            }

            var sb = new StringBuilder(" ORDER BY ");
            for (int i = 0; i < sortCols.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(Sql.Ident(sortCols[i].PhysicalName));
                sb.Append(sortCols[i].SortDescending ? " DESC" : " ASC");
            }

            return sb.ToString();
        }

        /// <summary>构造 WHERE 子句与参数。</summary>
        private string BuildWhere(AppTable table, QuerySpec spec, List<string> args)
        {
            var clauses = new List<string>();

            if (spec != null)
            {
                foreach (AppColumn c in table.Columns)
                {
                    if (spec.ColumnFilters.TryGetValue(c.Id, out string keyword) && !string.IsNullOrEmpty(keyword))
                    {
                        clauses.Add(Sql.Ident(c.PhysicalName) + " LIKE ? ESCAPE '\\'");
                        args.Add("%" + Sql.EscapeLike(keyword) + "%");
                    }
                }

                if (!string.IsNullOrEmpty(spec.QuickFilterText) && spec.QuickFilterColumnId.HasValue)
                {
                    foreach (AppColumn c in table.Columns)
                    {
                        if (c.Id == spec.QuickFilterColumnId.Value)
                        {
                            clauses.Add(Sql.Ident(c.PhysicalName) + " LIKE ? ESCAPE '\\'");
                            args.Add("%" + Sql.EscapeLike(spec.QuickFilterText) + "%");
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(spec.TagFilter))
                {
                    clauses.Add(BuildTagFilterClause(table, spec.TagFilter, args));
                }
            }

            return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
        }

        /// <summary>
        /// 标签过滤：命中“归属该行的标签”，或“归属该行所在分组值的标签”。
        /// </summary>
        private string BuildTagFilterClause(AppTable table, string keyword, List<string> args)
        {
            string tid = table.Id.ToString(CultureInfo.InvariantCulture);
            string physical = Sql.Ident(table.PhysicalName);
            string like = "%" + Sql.EscapeLike(keyword) + "%";

            var sb = new StringBuilder();
            sb.Append("rid IN (SELECT l.row_id FROM meta_tag_link l JOIN meta_tag g ON g.id = l.tag_id ");
            sb.Append("WHERE l.table_id = ? AND l.scope = 0 AND g.name LIKE ? ESCAPE '\\')");
            args.Add(tid);
            args.Add(like);

            // 分组值归属：该行的某个分组列的当前值，正好是某个被打标签的分组值
            foreach (AppColumn c in table.Columns)
            {
                if (!c.IsGroup)
                {
                    continue;
                }

                sb.Append(" OR rid IN (SELECT r0.rid FROM ").Append(physical).Append(" r0 WHERE r0.")
                  .Append(Sql.Ident(c.PhysicalName))
                  .Append(" IN (SELECT l.group_value FROM meta_tag_link l JOIN meta_tag g ON g.id = l.tag_id ")
                  .Append("WHERE l.table_id = ? AND l.scope = 1 AND l.column_id = ? AND g.name LIKE ? ESCAPE '\\'))");
                args.Add(tid);
                args.Add(c.Id.ToString(CultureInfo.InvariantCulture));
                args.Add(like);
            }

            return "(" + sb + ")";
        }

        /// <summary>按条件读取数据行（固定排序在此生效）。</summary>
        public List<DataRow> Query(AppTable table, QuerySpec spec)
        {
            var args = new List<string>();
            string sql = "SELECT rid";
            foreach (AppColumn c in table.Columns)
            {
                sql += ", " + Sql.Ident(c.PhysicalName);
            }

            sql += " FROM " + Physical(table);
            sql += BuildWhere(table, spec, args);
            sql += BuildOrderBy(table);

            var rows = new List<DataRow>();
            using (SqliteStatement st = _db.Prepare(sql))
            {
                for (int i = 0; i < args.Count; i++)
                {
                    st.Bind(i + 1, args[i]);
                }

                int colCount = table.Columns.Count;
                while (st.Read())
                {
                    var values = new string[colCount];
                    for (int i = 0; i < colCount; i++)
                    {
                        values[i] = st.GetString(i + 1) ?? string.Empty;
                    }

                    rows.Add(new DataRow(st.GetInt64(0), values));
                }
            }

            return rows;
        }

        /// <summary>读取某一行。</summary>
        public DataRow GetRow(AppTable table, long rowId)
        {
            string sql = "SELECT rid";
            foreach (AppColumn c in table.Columns)
            {
                sql += ", " + Sql.Ident(c.PhysicalName);
            }

            sql += " FROM " + Physical(table) + " WHERE rid = ?";

            using (SqliteStatement st = _db.Prepare(sql))
            {
                st.Bind(1, rowId);
                if (st.Read())
                {
                    var values = new string[table.Columns.Count];
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i] = st.GetString(i + 1) ?? string.Empty;
                    }

                    return new DataRow(st.GetInt64(0), values);
                }
            }

            return null;
        }

        /// <summary>某列当前已有的值（去重，用于分组下拉）。</summary>
        public List<string> DistinctValues(AppTable table, AppColumn column)
        {
            var list = new List<string>();
            string sql = "SELECT DISTINCT " + Sql.Ident(column.PhysicalName) + " FROM " + Physical(table) +
                         " WHERE " + Sql.Ident(column.PhysicalName) + " IS NOT NULL AND " + Sql.Ident(column.PhysicalName) + " <> ''" +
                         " ORDER BY " + Sql.Ident(column.PhysicalName);

            using (SqliteStatement st = _db.Prepare(sql))
            {
                while (st.Read())
                {
                    list.Add(st.GetString(0) ?? string.Empty);
                }
            }

            return list;
        }

        // ---------------------------------------------------------------
        // 唯一性校验
        // ---------------------------------------------------------------

        /// <summary>
        /// 校验“有值则列内唯一”。返回冲突时的提示文本，成功返回 null。
        /// </summary>
        public string ValidateUnique(AppTable table, AppColumn column, string value, long? excludeRowId)
        {
            if (!column.IsUnique)
            {
                return null;
            }

            // 空值不参与唯一性
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            string sql = "SELECT COUNT(*) FROM " + Physical(table) +
                         " WHERE " + Sql.Ident(column.PhysicalName) + " = ?";
            if (excludeRowId.HasValue)
            {
                sql += " AND rid <> ?";
            }

            long count = excludeRowId.HasValue
                ? _db.ScalarLong(sql, value, excludeRowId.Value.ToString(CultureInfo.InvariantCulture))
                : _db.ScalarLong(sql, value);

            if (count > 0)
            {
                return column.DisplayName + " 已存在相同的值：" + value + "\n该列配置为“唯一”，不能保存重复的值。";
            }

            return null;
        }

        /// <summary>校验整行的唯一性约束。返回第一条冲突提示，成功返回 null。</summary>
        public string ValidateRow(AppTable table, string[] values, long? excludeRowId)
        {
            for (int i = 0; i < table.Columns.Count && i < values.Length; i++)
            {
                string error = ValidateUnique(table, table.Columns[i], values[i], excludeRowId);
                if (error != null)
                {
                    return error;
                }
            }

            return null;
        }

        // ---------------------------------------------------------------
        // 写入
        // ---------------------------------------------------------------

        /// <summary>插入一行，返回新的行号。调用方需先做唯一性校验。</summary>
        public long InsertRow(AppTable table, string[] values)
        {
            if (table.Columns.Count == 0)
            {
                throw new InvalidOperationException("该表还没有任何列，无法添加数据。");
            }

            var names = new List<string>();
            var placeholders = new List<string>();
            for (int i = 0; i < table.Columns.Count; i++)
            {
                names.Add(Sql.Ident(table.Columns[i].PhysicalName));
                placeholders.Add("?");
            }

            string sql = "INSERT INTO " + Physical(table) +
                         " (" + string.Join(", ", names) + ") VALUES (" + string.Join(", ", placeholders) + ")";

            using (SqliteStatement st = _db.Prepare(sql))
            {
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    string v = i < values.Length ? values[i] : null;
                    st.Bind(i + 1, string.IsNullOrEmpty(v) ? null : v);
                }

                st.Step();
            }

            long rowId = _db.LastInsertRowId;
            ReconcileGroupsAfterWrite(table);
            return rowId;
        }

        /// <summary>更新一个单元格。返回 null 表示成功，否则为错误提示。</summary>
        public string UpdateCell(AppTable table, long rowId, AppColumn column, string value)
        {
            string error = ValidateUnique(table, column, value, rowId);
            if (error != null)
            {
                return error;
            }

            _db.Execute(
                "UPDATE " + Physical(table) + " SET " + Sql.Ident(column.PhysicalName) + " = ? WHERE rid = ?",
                string.IsNullOrEmpty(value) ? null : value,
                rowId.ToString(CultureInfo.InvariantCulture));

            if (column.IsGroup)
            {
                // 旧值若已无任何数据引用，其标签一并删除
                _schema.ReconcileGroupValue(table.Id, column);
            }

            return null;
        }

        /// <summary>更新整行。返回 null 表示成功，否则为错误提示。</summary>
        public string UpdateRow(AppTable table, long rowId, string[] values)
        {
            string error = ValidateRow(table, values, rowId);
            if (error != null)
            {
                return error;
            }

            var sets = new List<string>();
            for (int i = 0; i < table.Columns.Count; i++)
            {
                sets.Add(Sql.Ident(table.Columns[i].PhysicalName) + " = ?");
            }

            using (SqliteStatement st = _db.Prepare(
                "UPDATE " + Physical(table) + " SET " + string.Join(", ", sets) + " WHERE rid = ?"))
            {
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    string v = i < values.Length ? values[i] : null;
                    st.Bind(i + 1, string.IsNullOrEmpty(v) ? null : v);
                }

                st.Bind(table.Columns.Count + 1, rowId);
                st.Step();
            }

            ReconcileGroupsAfterWrite(table);
            return null;
        }

        private void ReconcileGroupsAfterWrite(AppTable table)
        {
            foreach (AppColumn c in table.Columns)
            {
                if (c.IsGroup)
                {
                    _schema.ReconcileGroupValue(table.Id, c);
                }
            }
        }

        /// <summary>删除若干行；这些行归属的标签一并删除。</summary>
        public void DeleteRows(AppTable table, IList<long> rowIds)
        {
            if (rowIds == null || rowIds.Count == 0)
            {
                return;
            }

            using (SqliteConnection.Tx tx = _db.Transaction())
            {
                using (SqliteStatement st = _db.Prepare("DELETE FROM " + Physical(table) + " WHERE rid = ?"))
                {
                    foreach (long id in rowIds)
                    {
                        st.Reset();
                        st.Bind(1, id);
                        st.Step();
                    }
                }

                tx.Complete();
            }

            _schema.RemoveRowLinks(table.Id, rowIds);
            ReconcileGroupsAfterWrite(table);
        }

        // ---------------------------------------------------------------
        // 快捷查询
        // ---------------------------------------------------------------

        /// <summary>
        /// 执行快捷查询。始终在全表范围内查找，不受当前筛选影响。
        /// </summary>
        public QuickQueryResult QuickQuery(AppTable table, string clipboardText)
        {
            var result = new QuickQueryResult();

            if (!table.HasQuickQuery)
            {
                result.Outcome = QuickQueryOutcome.None;
                return result;
            }

            AppColumn source = table.QuickColumn;
            AppColumn target = table.QueriedColumn;
            string text = clipboardText ?? string.Empty;

            // 1) 精确命中（大小写不敏感）
            string exactSql =
                "SELECT rid, " + Sql.Ident(target.PhysicalName) + " FROM " + Physical(table) +
                " WHERE " + Sql.Ident(source.PhysicalName) + " = ? COLLATE NOCASE LIMIT 1";

            using (SqliteStatement st = _db.Prepare(exactSql))
            {
                st.Bind(1, text);
                if (st.Read())
                {
                    result.Outcome = QuickQueryOutcome.Exact;
                    result.RowId = st.GetInt64(0);
                    result.ClipboardValue = st.GetString(1) ?? string.Empty;
                    return result;
                }
            }

            // 2) 模糊命中 → 对当前表做筛选
            string fuzzySql =
                "SELECT COUNT(*) FROM " + Physical(table) +
                " WHERE " + Sql.Ident(source.PhysicalName) + " LIKE ? ESCAPE '\\'";

            long matches = _db.ScalarLong(fuzzySql, "%" + Sql.EscapeLike(text) + "%");
            if (matches > 0)
            {
                result.Outcome = QuickQueryOutcome.Fuzzy;
                result.FilterColumn = source;
                result.FilterText = text;
                result.MatchCount = (int)matches;
                return result;
            }

            // 3) 完全无结果
            result.Outcome = QuickQueryOutcome.None;
            return result;
        }

        // ---------------------------------------------------------------
        // 标签查询
        // ---------------------------------------------------------------

        /// <summary>读取一张表里所有行/分组值的标签映射，用于界面展示。</summary>
        public List<TagLink> LoadLinks(AppTable table)
        {
            return _schema.LoadTagLinks(table.Id);
        }
    }
}
