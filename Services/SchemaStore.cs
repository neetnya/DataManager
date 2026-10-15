using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DataManager.Data;
using DataManager.Interop;
using DataManager.Models;

namespace DataManager.Services
{
    /// <summary>
    /// 负责元数据（表/列/标签）读写，以及用户表的动态 DDL（建表、加列、删列）。
    /// 每张用户表对应一张真实 SQLite 表，表名 "t_&lt;id&gt;"，列名 c0/c1/...。
    /// </summary>
    public sealed class SchemaStore
    {
        private readonly SqliteConnection _db;

        public SchemaStore(SqliteConnection db)
        {
            _db = db;
        }

        public SqliteConnection Connection => _db;

        // ---------------------------------------------------------------
        // 初始化
        // ---------------------------------------------------------------

        /// <summary>创建元数据表（幂等）。</summary>
        public void Initialize()
        {
            _db.Execute(@"
CREATE TABLE IF NOT EXISTS meta_table (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL,
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL
)");

            _db.Execute(@"
CREATE TABLE IF NOT EXISTS meta_column (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    table_id    INTEGER NOT NULL,
    ordinal     INTEGER NOT NULL,
    col_name    TEXT NOT NULL,
    is_quick    INTEGER NOT NULL DEFAULT 0,
    is_queried  INTEGER NOT NULL DEFAULT 0,
    is_unique   INTEGER NOT NULL DEFAULT 0,
    is_group    INTEGER NOT NULL DEFAULT 0,
    is_sort     INTEGER NOT NULL DEFAULT 0,
    is_url      INTEGER NOT NULL DEFAULT 0,
    sort_desc   INTEGER NOT NULL DEFAULT 0,
    sort_prio   INTEGER NOT NULL DEFAULT 0
)");

            _db.Execute(@"
CREATE TABLE IF NOT EXISTS meta_tag (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    table_id    INTEGER NOT NULL,
    name        TEXT NOT NULL
)");

            _db.Execute(@"
CREATE TABLE IF NOT EXISTS meta_tag_link (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    tag_id      INTEGER NOT NULL,
    table_id    INTEGER NOT NULL,
    scope       INTEGER NOT NULL DEFAULT 0,
    row_id      INTEGER NOT NULL DEFAULT 0,
    column_id   INTEGER NOT NULL DEFAULT 0,
    group_value TEXT
)");

            _db.Execute("CREATE INDEX IF NOT EXISTS ix_meta_column_table ON meta_column(table_id, ordinal)");
            _db.Execute("CREATE INDEX IF NOT EXISTS ix_meta_tag_table ON meta_tag(table_id, name)");
            _db.Execute("CREATE INDEX IF NOT EXISTS ix_meta_tag_link_tag ON meta_tag_link(tag_id)");
            _db.Execute("CREATE INDEX IF NOT EXISTS ix_meta_tag_link_table ON meta_tag_link(table_id)");
            _db.Execute("CREATE INDEX IF NOT EXISTS ix_meta_tag_link_row ON meta_tag_link(table_id, row_id)");
            _db.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_meta_tag_name ON meta_tag(table_id, name)");

            Migrate();
        }

        /// <summary>老版本数据库的结构升级（幂等）。</summary>
        private void Migrate()
        {
            if (!ColumnExists("meta_column", "sort_prio"))
            {
                _db.Execute("ALTER TABLE meta_column ADD COLUMN sort_prio INTEGER NOT NULL DEFAULT 0");
            }

            if (!ColumnExists("meta_column", "is_url"))
            {
                _db.Execute("ALTER TABLE meta_column ADD COLUMN is_url INTEGER NOT NULL DEFAULT 0");
            }
        }

        private bool ColumnExists(string table, string column)
        {
            using (SqliteStatement st = _db.Prepare("PRAGMA table_info(" + Sql.Ident(table) + ")"))
            {
                while (st.Read())
                {
                    if (string.Equals(st.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // ---------------------------------------------------------------
        // 读取
        // ---------------------------------------------------------------

        /// <summary>读取所有表及其列配置。</summary>
        public List<AppTable> LoadTables()
        {
            var tables = new List<AppTable>();
            var byId = new Dictionary<long, AppTable>();

            using (SqliteStatement st = _db.Prepare("SELECT id, name FROM meta_table ORDER BY sort_order, id"))
            {
                while (st.Read())
                {
                    var t = new AppTable { Id = st.GetInt64(0), Name = st.GetString(1) };
                    tables.Add(t);
                    byId[t.Id] = t;
                }
            }

            using (SqliteStatement st = _db.Prepare(
                "SELECT id, table_id, ordinal, col_name, is_quick, is_queried, is_unique, is_group, is_sort, is_url, sort_desc, sort_prio " +
                "FROM meta_column ORDER BY table_id, ordinal, id"))
            {
                while (st.Read())
                {
                    long tableId = st.GetInt64(1);
                    if (!byId.TryGetValue(tableId, out AppTable table))
                    {
                        continue;
                    }

                    var col = new AppColumn
                    {
                        Id = st.GetInt64(0),
                        TableId = tableId,
                        Ordinal = (int)st.GetInt64(2),
                        PhysicalName = st.GetString(3),
                        Flags = ColumnFlags.None,
                    };

                    if (st.GetInt64(4) != 0)
                    {
                        col.Flags |= ColumnFlags.Quick;
                    }

                    if (st.GetInt64(5) != 0)
                    {
                        col.Flags |= ColumnFlags.Queried;
                    }

                    if (st.GetInt64(6) != 0)
                    {
                        col.Flags |= ColumnFlags.Unique;
                    }

                    if (st.GetInt64(7) != 0)
                    {
                        col.Flags |= ColumnFlags.Group;
                    }

                    if (st.GetInt64(8) != 0)
                    {
                        col.Flags |= ColumnFlags.Sort;
                    }

                    if (st.GetInt64(9) != 0)
                    {
                        col.Flags |= ColumnFlags.Url;
                    }

                    col.SortDescending = st.GetInt64(10) != 0;
                    col.SortPriority = (int)st.GetInt64(11);
                    table.Columns.Add(col);
                }
            }

            return tables;
        }

        /// <summary>读取某张表的全部标签。</summary>
        public List<Tag> LoadTags(long tableId)
        {
            var list = new List<Tag>();
            using (SqliteStatement st = _db.Prepare("SELECT id, table_id, name FROM meta_tag WHERE table_id = ? ORDER BY name"))
            {
                st.Bind(1, tableId);
                while (st.Read())
                {
                    list.Add(new Tag { Id = st.GetInt64(0), TableId = st.GetInt64(1), Name = st.GetString(2) });
                }
            }

            return list;
        }

        /// <summary>读取某张表的全部标签绑定。</summary>
        public List<TagLink> LoadTagLinks(long tableId)
        {
            var list = new List<TagLink>();
            using (SqliteStatement st = _db.Prepare(
                "SELECT id, tag_id, table_id, scope, row_id, column_id, group_value FROM meta_tag_link WHERE table_id = ?"))
            {
                st.Bind(1, tableId);
                while (st.Read())
                {
                    list.Add(new TagLink
                    {
                        Id = st.GetInt64(0),
                        TagId = st.GetInt64(1),
                        TableId = st.GetInt64(2),
                        Scope = (TagScope)st.GetInt64(3),
                        RowId = st.GetInt64(4),
                        ColumnId = st.GetInt64(5),
                        GroupValue = st.GetString(6),
                    });
                }
            }

            return list;
        }

        // ---------------------------------------------------------------
        // 表级操作
        // ---------------------------------------------------------------

        /// <summary>创建一张新表（只有元数据，尚无列）。</summary>
        public long CreateTable(string name)
        {
            using (SqliteStatement st = _db.Prepare(
                "INSERT INTO meta_table (name, sort_order, created_at) VALUES (?, ?, ?)"))
            {
                st.Bind(1, name ?? string.Empty);
                st.Bind(2, NextTableSortOrder());
                st.Bind(3, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                st.Step();
            }

            long id = _db.LastInsertRowId;

            // 真实表一开始只有主键列，后续加列用 ALTER TABLE
            _db.Execute("CREATE TABLE " + Sql.Ident("t_" + id.ToString(CultureInfo.InvariantCulture)) + " (rid INTEGER PRIMARY KEY AUTOINCREMENT)");
            return id;
        }

        private long NextTableSortOrder()
        {
            return _db.ScalarLong("SELECT IFNULL(MAX(sort_order), 0) + 1 FROM meta_table");
        }

        /// <summary>重命名表。</summary>
        public void RenameTable(long tableId, string newName)
        {
            _db.Execute("UPDATE meta_table SET name = ? WHERE id = ?", newName ?? string.Empty, tableId.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>删除表：连同真实表、列、标签、标签绑定、行数据一起删除。</summary>
        public void DropTable(long tableId)
        {
            string tid = tableId.ToString(CultureInfo.InvariantCulture);
            _db.Execute("DROP TABLE IF EXISTS " + Sql.Ident("t_" + tid));
            _db.Execute("DELETE FROM meta_column WHERE table_id = ?", tid);
            _db.Execute("DELETE FROM meta_tag_link WHERE table_id = ?", tid);
            _db.Execute("DELETE FROM meta_tag WHERE table_id = ?", tid);
            _db.Execute("DELETE FROM meta_table WHERE id = ?", tid);
        }

        /// <summary>统计某张表的数据行数（用于删除确认提示）。</summary>
        public long CountRows(long tableId)
        {
            if (!PhysicalTableExists(tableId))
            {
                return 0;
            }

            return _db.ScalarLong("SELECT COUNT(*) FROM " + Sql.Ident("t_" + tableId.ToString(CultureInfo.InvariantCulture)));
        }

        private bool PhysicalTableExists(long tableId)
        {
            string name = "t_" + tableId.ToString(CultureInfo.InvariantCulture);
            return _db.Scalar("SELECT name FROM sqlite_master WHERE type = 'table' AND name = ?", name) != null;
        }

        // ---------------------------------------------------------------
        // 列级操作
        // ---------------------------------------------------------------

        /// <summary>为某张表追加一个新列（立刻 ALTER TABLE 加真实列）。</summary>
        public AppColumn AddColumn(long tableId, ColumnFlags flags, bool sortDescending)
        {
            int ordinal = (int)_db.ScalarLong(
                "SELECT IFNULL(MAX(ordinal), -1) + 1 FROM meta_column WHERE table_id = ?",
                tableId.ToString(CultureInfo.InvariantCulture));

            // 分配一个永不复用的物理列名，避免删列后重名
            string physical = NextPhysicalColumnName(tableId);

            // 排序优先级默认与建表顺序一致
            int sortPriority = (int)_db.ScalarLong(
                "SELECT IFNULL(MAX(sort_prio), -1) + 1 FROM meta_column WHERE table_id = ?",
                tableId.ToString(CultureInfo.InvariantCulture));

            _db.Execute(
                "ALTER TABLE " + Sql.Ident("t_" + tableId.ToString(CultureInfo.InvariantCulture)) +
                " ADD COLUMN " + Sql.Ident(physical) + " TEXT");

            using (SqliteStatement st = _db.Prepare(
                "INSERT INTO meta_column (table_id, ordinal, col_name, is_quick, is_queried, is_unique, is_group, is_sort, is_url, sort_desc, sort_prio) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"))
            {
                st.Bind(1, tableId);
                st.Bind(2, ordinal);
                st.Bind(3, physical);
                st.Bind(4, (flags & ColumnFlags.Quick) != 0 ? 1L : 0L);
                st.Bind(5, (flags & ColumnFlags.Queried) != 0 ? 1L : 0L);
                st.Bind(6, (flags & ColumnFlags.Unique) != 0 ? 1L : 0L);
                st.Bind(7, (flags & ColumnFlags.Group) != 0 ? 1L : 0L);
                st.Bind(8, (flags & ColumnFlags.Sort) != 0 ? 1L : 0L);
                st.Bind(9, (flags & ColumnFlags.Url) != 0 ? 1L : 0L);
                st.Bind(10, sortDescending ? 1L : 0L);
                st.Bind(11, (long)sortPriority);
                st.Step();
            }

            long id = _db.LastInsertRowId;
            return new AppColumn
            {
                Id = id,
                TableId = tableId,
                Ordinal = ordinal,
                PhysicalName = physical,
                Flags = flags,
                SortDescending = sortDescending,
                SortPriority = sortPriority,
            };
        }

        private string NextPhysicalColumnName(long tableId)
        {
            int max = -1;
            using (SqliteStatement st = _db.Prepare("SELECT col_name FROM meta_column WHERE table_id = ?"))
            {
                st.Bind(1, tableId);
                while (st.Read())
                {
                    string n = st.GetString(0);
                    if (!string.IsNullOrEmpty(n) && n.Length > 1 && n[0] == 'c' &&
                        int.TryParse(n.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    {
                        if (v > max)
                        {
                            max = v;
                        }
                    }
                }
            }

            return Sql.ColumnName(max + 1);
        }

        /// <summary>删除列：该列数据一并删除。</summary>
        public void DropColumn(AppColumn column)
        {
            string tid = column.TableId.ToString(CultureInfo.InvariantCulture);
            string cid = column.Id.ToString(CultureInfo.InvariantCulture);

            _db.Execute(
                "ALTER TABLE " + Sql.Ident("t_" + tid) + " DROP COLUMN " + Sql.Ident(column.PhysicalName));

            // 挂在“该分组列的值”上的标签绑定随之失效
            _db.Execute("DELETE FROM meta_tag_link WHERE table_id = ? AND column_id = ?", tid, cid);
            _db.Execute("DELETE FROM meta_column WHERE id = ?", cid);
            PurgeOrphanTags(column.TableId);
        }

        /// <summary>把列配置写回数据库（设置窗口保存时调用）。</summary>
        public void UpdateColumn(AppColumn column)
        {
            using (SqliteStatement st = _db.Prepare(
                "UPDATE meta_column SET ordinal = ?, is_quick = ?, is_queried = ?, is_unique = ?, is_group = ?, is_sort = ?, is_url = ?, sort_desc = ?, sort_prio = ? WHERE id = ?"))
            {
                st.Bind(1, (long)column.Ordinal);
                st.Bind(2, column.IsQuick ? 1L : 0L);
                st.Bind(3, column.IsQueried ? 1L : 0L);
                st.Bind(4, column.IsUnique ? 1L : 0L);
                st.Bind(5, column.IsGroup ? 1L : 0L);
                st.Bind(6, column.IsSort ? 1L : 0L);
                st.Bind(7, column.IsUrl ? 1L : 0L);
                st.Bind(8, column.SortDescending ? 1L : 0L);
                st.Bind(9, (long)column.SortPriority);
                st.Bind(10, column.Id);
                st.Step();
            }
        }

        /// <summary>批量写回某张表的列顺序与配置。</summary>
        public void UpdateColumns(long tableId, IEnumerable<AppColumn> columns)
        {
            using (SqliteConnection.Tx tx = _db.Transaction())
            {
                int ordinal = 0;
                foreach (AppColumn c in columns)
                {
                    c.TableId = tableId;
                    c.Ordinal = ordinal++;
                    UpdateColumn(c);
                }

                tx.Complete();
            }
        }

        // ---------------------------------------------------------------
        // 标签操作
        // ---------------------------------------------------------------

        /// <summary>按名称取得标签，不存在则创建。</summary>
        public Tag EnsureTag(long tableId, string name)
        {
            using (SqliteStatement st = _db.Prepare("SELECT id, table_id, name FROM meta_tag WHERE table_id = ? AND name = ?"))
            {
                st.Bind(1, tableId);
                st.Bind(2, name);
                if (st.Read())
                {
                    return new Tag { Id = st.GetInt64(0), TableId = st.GetInt64(1), Name = st.GetString(2) };
                }
            }

            _db.Execute("INSERT INTO meta_tag (table_id, name) VALUES (?, ?)", tableId.ToString(CultureInfo.InvariantCulture), name);
            return new Tag { Id = _db.LastInsertRowId, TableId = tableId, Name = name };
        }

        /// <summary>本表是否已有同名标签（可排除正在改名的那一条）。</summary>
        public bool TagNameExists(long tableId, string name, long excludeTagId)
        {
            using (SqliteStatement st = _db.Prepare(
                "SELECT 1 FROM meta_tag WHERE table_id = ? AND name = ? AND id <> ? LIMIT 1"))
            {
                st.Bind(1, tableId);
                st.Bind(2, name);
                st.Bind(3, excludeTagId);
                return st.Read();
            }
        }

        /// <summary>重命名标签（本表内全局生效）。</summary>
        public void RenameTag(long tagId, string newName)
        {
            _db.Execute("UPDATE meta_tag SET name = ? WHERE id = ?", newName, tagId.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>删除标签及其全部绑定。</summary>
        public void DeleteTag(long tagId)
        {
            string tid = tagId.ToString(CultureInfo.InvariantCulture);
            _db.Execute("DELETE FROM meta_tag_link WHERE tag_id = ?", tid);
            _db.Execute("DELETE FROM meta_tag WHERE id = ?", tid);
        }

        /// <summary>给某一行绑定标签（scope = Row）。</summary>
        public void LinkTagToRow(long tagId, long tableId, long rowId)
        {
            if (HasRowLink(tagId, rowId))
            {
                return;
            }

            _db.Execute(
                "INSERT INTO meta_tag_link (tag_id, table_id, scope, row_id, column_id, group_value) VALUES (?, ?, 0, ?, 0, NULL)",
                tagId.ToString(CultureInfo.InvariantCulture),
                tableId.ToString(CultureInfo.InvariantCulture),
                rowId.ToString(CultureInfo.InvariantCulture));
        }

        private bool HasRowLink(long tagId, long rowId)
        {
            return _db.Scalar(
                "SELECT id FROM meta_tag_link WHERE tag_id = ? AND row_id = ? AND scope = 0",
                tagId.ToString(CultureInfo.InvariantCulture),
                rowId.ToString(CultureInfo.InvariantCulture)) != null;
        }

        /// <summary>给某个分组值绑定标签（scope = GroupValue）。</summary>
        public void LinkTagToGroupValue(long tagId, long tableId, long columnId, string groupValue)
        {
            bool exists = _db.Scalar(
                "SELECT id FROM meta_tag_link WHERE tag_id = ? AND column_id = ? AND group_value = ? AND scope = 1",
                tagId.ToString(CultureInfo.InvariantCulture),
                columnId.ToString(CultureInfo.InvariantCulture),
                groupValue) != null;

            if (exists)
            {
                return;
            }

            _db.Execute(
                "INSERT INTO meta_tag_link (tag_id, table_id, scope, row_id, column_id, group_value) VALUES (?, ?, 1, 0, ?, ?)",
                tagId.ToString(CultureInfo.InvariantCulture),
                tableId.ToString(CultureInfo.InvariantCulture),
                columnId.ToString(CultureInfo.InvariantCulture),
                groupValue);
        }

        /// <summary>解除一条标签绑定。</summary>
        public void UnlinkTag(long linkId)
        {
            _db.Execute("DELETE FROM meta_tag_link WHERE id = ?", linkId.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>行被删除时，删掉该行归属的标签绑定。</summary>
        public void RemoveRowLinks(long tableId, IEnumerable<long> rowIds)
        {
            string tid = tableId.ToString(CultureInfo.InvariantCulture);
            using (SqliteConnection.Tx tx = _db.Transaction())
            {
                foreach (long rowId in rowIds)
                {
                    _db.Execute(
                        "DELETE FROM meta_tag_link WHERE table_id = ? AND scope = 0 AND row_id = ?",
                        tid,
                        rowId.ToString(CultureInfo.InvariantCulture));
                }

                tx.Complete();
            }

            PurgeOrphanTags(tableId);
        }

        /// <summary>
        /// 分组值被编辑后调用：旧值若已无任何数据引用，其标签一并删除。
        /// 只处理“该分组列”，不联动修改同组其他数据。
        /// </summary>
        public void ReconcileGroupValue(long tableId, AppColumn groupColumn)
        {
            // 找出该列上所有绑定了标签、但实际数据里已不存在的分组值
            var stale = new List<long>();
            string sql =
                "SELECT l.id, l.group_value FROM meta_tag_link l WHERE l.table_id = ? AND l.scope = 1 AND l.column_id = ?";
            using (SqliteStatement st = _db.Prepare(sql))
            {
                st.Bind(1, tableId);
                st.Bind(2, groupColumn.Id);
                while (st.Read())
                {
                    long linkId = st.GetInt64(0);
                    string value = st.GetString(1);
                    if (!GroupValueExists(tableId, groupColumn, value))
                    {
                        stale.Add(linkId);
                    }
                }
            }

            if (stale.Count == 0)
            {
                return;
            }

            using (SqliteConnection.Tx tx = _db.Transaction())
            {
                foreach (long linkId in stale)
                {
                    _db.Execute("DELETE FROM meta_tag_link WHERE id = ?", linkId.ToString(CultureInfo.InvariantCulture));
                }

                tx.Complete();
            }

            PurgeOrphanTags(tableId);
        }

        /// <summary>对整张表的所有分组列做一次孤儿分组值清理。</summary>
        public void ReconcileAllGroupValues(AppTable table)
        {
            foreach (AppColumn c in table.Columns)
            {
                if (c.IsGroup)
                {
                    ReconcileGroupValue(table.Id, c);
                }
            }
        }

        private bool GroupValueExists(long tableId, AppColumn groupColumn, string value)
        {
            // 分组列：只要任一单元格按分隔符拆分后包含该值，即视为存在
            if (groupColumn.IsGroup)
            {
                string sql = "SELECT " + Sql.Ident(groupColumn.PhysicalName) + " FROM " +
                             Sql.Ident("t_" + tableId.ToString(CultureInfo.InvariantCulture));
                using (SqliteStatement st = _db.Prepare(sql))
                {
                    while (st.Read())
                    {
                        string raw = st.GetString(0) ?? string.Empty;
                        if (GroupValueSplitter.Contains(raw, value))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            long count = _db.ScalarLong(
                "SELECT COUNT(*) FROM " + Sql.Ident("t_" + tableId.ToString(CultureInfo.InvariantCulture)) +
                " WHERE " + Sql.Ident(groupColumn.PhysicalName) + " = ?",
                value);
            return count > 0;
        }

        /// <summary>删除不再被任何绑定引用的标签。</summary>
        public void PurgeOrphanTags(long tableId)
        {
            _db.Execute(
                "DELETE FROM meta_tag WHERE table_id = ? AND id NOT IN (SELECT tag_id FROM meta_tag_link WHERE table_id = ?)",
                tableId.ToString(CultureInfo.InvariantCulture),
                tableId.ToString(CultureInfo.InvariantCulture));
        }

        // ---------------------------------------------------------------
        // 导入
        // ---------------------------------------------------------------

        /// <summary>生成“未命名01”格式的下一个表名。</summary>
        public string NextUntitledTableName()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            using (SqliteStatement st = _db.Prepare("SELECT name FROM meta_table"))
            {
                while (st.Read())
                {
                    used.Add(st.GetString(0) ?? string.Empty);
                }
            }

            for (int i = 1; i < 100000; i++)
            {
                string candidate = "未命名" + i.ToString("D2", CultureInfo.InvariantCulture);
                if (!used.Contains(candidate))
                {
                    return candidate;
                }
            }

            return "未命名" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }
    }
}
