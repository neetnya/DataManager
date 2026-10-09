using System;
using System.Collections.Generic;

namespace DataManager.Models
{
    /// <summary>列的配置项集合。</summary>
    [Flags]
    public enum ColumnFlags
    {
        None = 0,

        /// <summary>快捷查询的输入源列（每表最多 1 个）。</summary>
        Quick = 1,

        /// <summary>快捷查询的输出目标列（每表最多 1 个）。</summary>
        Queried = 2,

        /// <summary>有值则列内唯一。</summary>
        Unique = 4,

        /// <summary>分组列：值可下拉选择、可挂标签。</summary>
        Group = 8,

        /// <summary>参与固定排序。</summary>
        Sort = 16,
    }

    /// <summary>一张用户自建的表。</summary>
    public sealed class AppTable
    {
        public long Id { get; set; }

        public string Name { get; set; }

        /// <summary>按用户建表顺序排列的列。</summary>
        public List<AppColumn> Columns { get; } = new List<AppColumn>();

        /// <summary>该表对应的真实 SQLite 表名。</summary>
        public string PhysicalName => "t_" + Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>快捷查询列（输入源）。</summary>
        public AppColumn QuickColumn => Find(ColumnFlags.Quick);

        /// <summary>被查询列（输出目标）。</summary>
        public AppColumn QueriedColumn => Find(ColumnFlags.Queried);

        /// <summary>该表是否可用快捷查询（必须成对存在）。</summary>
        public bool HasQuickQuery => QuickColumn != null && QueriedColumn != null;

        private AppColumn Find(ColumnFlags flag)
        {
            foreach (AppColumn c in Columns)
            {
                if ((c.Flags & flag) == flag)
                {
                    return c;
                }
            }

            return null;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>一个无名称的列，仅以顺序索引标识。</summary>
    public sealed class AppColumn
    {
        public long Id { get; set; }

        public long TableId { get; set; }

        /// <summary>用户在设置里排定的顺序，从 0 开始。</summary>
        public int Ordinal { get; set; }

        public ColumnFlags Flags { get; set; }

        /// <summary>排序列是否为降序（默认升序）。</summary>
        public bool SortDescending { get; set; }

        /// <summary>
        /// 固定排序优先级，数字小的先排。默认与建表顺序一致，可由用户自定义。
        /// </summary>
        public int SortPriority { get; set; }

        /// <summary>
        /// 该列在真实 SQLite 表中的列名（c0 / c1 / ...）。
        /// 一旦分配就永不改变、永不复用，因此删列/调序都不需要搬动数据。
        /// </summary>
        public string PhysicalName { get; set; }

        /// <summary>界面上展示的列标题，例如“列 3”。</summary>
        public string DisplayName => "列 " + (Ordinal + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public bool IsQuick => (Flags & ColumnFlags.Quick) != 0;

        public bool IsQueried => (Flags & ColumnFlags.Queried) != 0;

        public bool IsUnique => (Flags & ColumnFlags.Unique) != 0;

        public bool IsGroup => (Flags & ColumnFlags.Group) != 0;

        public bool IsSort => (Flags & ColumnFlags.Sort) != 0;

        public AppColumn Clone()
        {
            return new AppColumn
            {
                Id = Id,
                TableId = TableId,
                Ordinal = Ordinal,
                Flags = Flags,
                SortDescending = SortDescending,
                SortPriority = SortPriority,
                PhysicalName = PhysicalName,
            };
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>一行数据。</summary>
    public sealed class DataRow
    {
        public DataRow(long rowId, string[] values)
        {
            RowId = rowId;
            Values = values ?? Array.Empty<string>();
        }

        public long RowId { get; }

        /// <summary>按列顺序排列的值；NULL 统一转成空字符串。</summary>
        public string[] Values { get; }

        public string Get(int ordinal)
        {
            if (ordinal < 0 || ordinal >= Values.Length)
            {
                return string.Empty;
            }

            return Values[ordinal] ?? string.Empty;
        }
    }

    /// <summary>标签归属的类型。</summary>
    public enum TagScope
    {
        /// <summary>归属整行。</summary>
        Row = 0,

        /// <summary>归属“分组列的值”。</summary>
        GroupValue = 1,
    }

    /// <summary>一个表内的标签。</summary>
    public sealed class Tag
    {
        public long Id { get; set; }

        public long TableId { get; set; }

        public string Name { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>标签与“行”或“分组值”的绑定关系。</summary>
    public sealed class TagLink
    {
        public long Id { get; set; }

        public long TagId { get; set; }

        public long TableId { get; set; }

        public TagScope Scope { get; set; }

        /// <summary>归属行时的行号；归属分组值时为 0。</summary>
        public long RowId { get; set; }

        /// <summary>归属分组值时的列 Id。</summary>
        public long ColumnId { get; set; }

        /// <summary>归属分组值时的分组值。</summary>
        public string GroupValue { get; set; }
    }
}
