using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DataManager.Dialogs;
using DataManager.Models;
using DataManager.Services;
using DataManager.ViewModels;

namespace DataManager
{
    /// <summary>
    /// 标签管理窗口。按所选单元格决定标签归属：
    /// 所在列是分组列 → 归属该分组值；否则 → 归属该行。
    ///
    /// 已有的标签逐行列出，每行提供三个动作：
    ///   添加 / 移除（只影响当前归属对象）/ 删除标签（从整张表彻底删掉，不动数据行）
    /// </summary>
    public partial class TagEditorWindow : Window
    {
        private readonly AppTable _table;
        private readonly AppColumn _column;
        private readonly RowVm _row;
        private readonly AppData _data;
        private readonly bool _toGroup;

        /// <summary>选中的行（批量）；至少 1 行。</summary>
        private readonly List<RowVm> _rows;

        /// <summary>行标签模式下需要打标签的所有行 Id。</summary>
        private readonly HashSet<long> _rowIds = new HashSet<long>();

        /// <summary>分组标签模式下需要打标签的所有分组值。</summary>
        private readonly HashSet<string> _groupValues = new HashSet<string>();

        /// <summary>窗口内发生过改动，关闭时调用方需要刷新。</summary>
        public bool Changed { get; private set; }

        /// <summary>是否存在可打标签的有效目标（分组列单元格全为空时为 false）。</summary>
        private bool HasTargets => _toGroup ? _groupValues.Count > 0 : _rowIds.Count > 0;

        public TagEditorWindow(AppTable table, AppColumn column, List<RowVm> rows, AppData data)
        {
            InitializeComponent();

            _table = table;
            _column = column;
            _data = data;
            _toGroup = column.IsGroup;
            _rows = rows ?? new List<RowVm>();
            _row = _rows.Count > 0 ? _rows[0] : null;

            foreach (RowVm r in _rows)
            {
                if (_toGroup)
                {
                    // 分组列一个单元格可能含多个分组值，全部拆开作为打标签目标
                    foreach (string part in GroupValueSplitter.Split(r.Cells[_column.Ordinal].Value))
                    {
                        _groupValues.Add(part);
                    }
                }
                else
                {
                    _rowIds.Add(r.RowId);
                }
            }

            UpdateTargetControlsState();
            BuildScopeText();
            RebuildTagRows();

            Loaded += (s, e) =>
            {
                if (TxtNewTag.IsEnabled)
                {
                    TxtNewTag.Focus();
                }
            };
        }

        private string CurrentValue => _row != null ? (_row.Cells[_column.Ordinal].Value ?? string.Empty) : string.Empty;

        private void UpdateTargetControlsState()
        {
            bool canAdd = HasTargets;
            TxtNewTag.IsEnabled = canAdd;
            BtnAddTag.IsEnabled = canAdd;
            TxtAddHint.Text = canAdd ? "回车即可添加" : "空分组值不可打标签";
        }

        /// <summary>当前所有归属对象上都已挂的标签 Id（批量时为全部命中才算）。</summary>
        private HashSet<long> AppliedTagIds(List<Tag> tags, List<TagLink> links)
        {
            var ids = new HashSet<long>();

            foreach (Tag tag in tags)
            {
                if (IsAppliedToAllTargets(tag, links))
                {
                    ids.Add(tag.Id);
                }
            }

            return ids;
        }

        /// <summary>该标签是否已挂到所有目标对象上。</summary>
        private bool IsAppliedToAllTargets(Tag tag, List<TagLink> links)
        {
            if (_toGroup ? _groupValues.Count == 0 : _rowIds.Count == 0)
            {
                return false;
            }

            if (_toGroup)
            {
                foreach (string gv in _groupValues)
                {
                    bool hit = false;
                    foreach (TagLink l in links)
                    {
                        if (l.TagId == tag.Id && l.Scope == TagScope.GroupValue &&
                            l.ColumnId == _column.Id &&
                            string.Equals(l.GroupValue, gv, StringComparison.Ordinal))
                        {
                            hit = true;
                            break;
                        }
                    }

                    if (!hit)
                    {
                        return false;
                    }
                }
            }
            else
            {
                foreach (long rowId in _rowIds)
                {
                    bool hit = false;
                    foreach (TagLink l in links)
                    {
                        if (l.TagId == tag.Id && l.Scope == TagScope.Row && l.RowId == rowId)
                        {
                            hit = true;
                            break;
                        }
                    }

                    if (!hit)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static string GroupKey(long columnId, string value)
        {
            return columnId.ToString(CultureInfo.InvariantCulture) + "\u0001" + (value ?? string.Empty);
        }

        /// <summary>单次全表扫描预统计每个标签被多少行使用（分组标签算同组所有行）。</summary>
        private Dictionary<long, int> CountRowsUsingAllTags(List<DataRow> allRows, List<TagLink> links)
        {
            var rowTagIds = new Dictionary<long, HashSet<long>>();
            var groupTagIds = new Dictionary<string, HashSet<long>>();

            foreach (TagLink l in links)
            {
                if (l.Scope == TagScope.Row)
                {
                    if (!rowTagIds.TryGetValue(l.RowId, out HashSet<long> set))
                    {
                        set = new HashSet<long>();
                        rowTagIds[l.RowId] = set;
                    }

                    set.Add(l.TagId);
                }
                else
                {
                    string key = GroupKey(l.ColumnId, l.GroupValue);
                    if (!groupTagIds.TryGetValue(key, out HashSet<long> set))
                    {
                        set = new HashSet<long>();
                        groupTagIds[key] = set;
                    }

                    set.Add(l.TagId);
                }
            }

            var counts = new Dictionary<long, int>();
            var groupCols = new List<(int Index, long ColumnId)>();
            for (int i = 0; i < _table.Columns.Count; i++)
            {
                AppColumn c = _table.Columns[i];
                if (c.IsGroup)
                {
                    groupCols.Add((i, c.Id));
                }
            }

            foreach (DataRow r in allRows)
            {
                var hit = new HashSet<long>();

                if (rowTagIds.TryGetValue(r.RowId, out HashSet<long> direct))
                {
                    hit.UnionWith(direct);
                }

                for (int g = 0; g < groupCols.Count; g++)
                {
                    (int colIndex, long colId) = groupCols[g];
                    foreach (string part in GroupValueSplitter.Split(r.Get(colIndex)))
                    {
                        if (groupTagIds.TryGetValue(GroupKey(colId, part), out HashSet<long> viaGroup))
                        {
                            hit.UnionWith(viaGroup);
                        }
                    }
                }

                foreach (long tagId in hit)
                {
                    counts.TryGetValue(tagId, out int n);
                    counts[tagId] = n + 1;
                }
            }

            return counts;
        }

        private void BuildScopeText()
        {
            if (_toGroup)
            {
                if (_groupValues.Count == 0)
                {
                    TxtScope.Text = "当前分组列单元格为空，无法添加分组标签，请先填写分组值。";
                }
                else if (_groupValues.Count > 1)
                {
                    TxtScope.Text = "归属分组值（共 " + _groupValues.Count.ToString(CultureInfo.InvariantCulture) + " 个值，批量）：" +
                                    "该分组下的所有行共用这些标签";
                }
                else
                {
                    string shown = CurrentValue.Length == 0 ? "(空值)" : CurrentValue;
                    TxtScope.Text = "归属分组值：" + shown + "（该分组下的所有行共用这些标签）";
                }
            }
            else
            {
                if (_rowIds.Count == 0)
                {
                    TxtScope.Text = "未选中有效数据行。";
                }
                else if (_rowIds.Count > 1)
                {
                    TxtScope.Text = "归属整行（已选 " + _rowIds.Count.ToString(CultureInfo.InvariantCulture) + " 行，批量）";
                }
                else
                {
                    TxtScope.Text = "归属整行（第 " + _row.DisplayIndex.ToString(CultureInfo.InvariantCulture) + " 行）";
                }
            }
        }

        private void RebuildTagRows()
        {
            TagRows.Children.Clear();

            List<Tag> all = _data.Schema.LoadTags(_table.Id);
            List<TagLink> links = _data.Schema.LoadTagLinks(_table.Id);
            List<DataRow> allRows = _data.Rows.Query(_table, new QuerySpec(), false);

            HashSet<long> applied = AppliedTagIds(all, links);
            Dictionary<long, int> rowCounts = CountRowsUsingAllTags(allRows, links);

            if (all.Count == 0)
            {
                TagRows.Children.Add(new TextBlock
                {
                    Text = "还没有任何标签。在上面的输入框里新建一个。",
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextFaintBrush"],
                    Margin = new Thickness(6, 8, 6, 8),
                });
            }

            foreach (Tag tag in all)
            {
                rowCounts.TryGetValue(tag.Id, out int count);
                TagRows.Children.Add(BuildTagRow(tag, applied.Contains(tag.Id), count));
            }

            TxtSummary.Text = "本表共 " + all.Count.ToString(CultureInfo.InvariantCulture) + " 个标签";
        }

        private UIElement BuildTagRow(Tag tag, bool isApplied, int usedRowCount)
        {
            var grid = new Grid { Margin = new Thickness(4, 3, 4, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 名称 + 使用行数
            var name = new TextBlock
            {
                Text = tag.Name + "（" + usedRowCount.ToString(CultureInfo.InvariantCulture) + " 行）",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 8, 0),
            };

            if (isApplied)
            {
                name.FontWeight = FontWeights.SemiBold;
                name.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"];
            }

            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            // 添加 / 移除：只影响当前单元格对应的归属对象
            var toggle = new Button
            {
                Content = isApplied ? "移除" : "添加",
                MinWidth = 64,
                Padding = new Thickness(10, 2, 10, 2),
                MinHeight = 24,
                Margin = new Thickness(0, 0, 6, 0),
                IsEnabled = HasTargets,
                ToolTip = HasTargets ? null : "当前分组列单元格为空，无法添加或移除分组标签",
            };

            toggle.Click += (s, e) =>
            {
                if (!HasTargets)
                {
                    return;
                }

                if (isApplied)
                {
                    RemoveFromCurrent(tag);
                }
                else
                {
                    AddToCurrent(tag);
                }

                BuildScopeText();
                RebuildTagRows();
            };

            Grid.SetColumn(toggle, 1);
            grid.Children.Add(toggle);

            // 删除标签：从整张表彻底删除，不动任何数据行
            var delete = new Button
            {
                Content = "删除标签",
                Style = (Style)Application.Current.Resources["DangerButton"],
                Padding = new Thickness(10, 2, 10, 2),
                MinHeight = 24,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "从整张表删除该标签，不影响表格里的数据",
            };

            delete.Click += (s, e) =>
            {
                if (!MessageWindow.Confirm(
                        this,
                        "删除标签「" + tag.Name + "」？\n\n" +
                        "该标签目前被 " + usedRowCount.ToString(CultureInfo.InvariantCulture) + " 行使用，删除后这些行上的标记会消失。\n" +
                        "表格里的数据本身不受影响。",
                        "删除标签",
                        MessageTone.Danger,
                        MessageButtons.OkCancel))
                {
                    return;
                }

                _data.Schema.DeleteTag(tag.Id);
                Changed = true;
                BuildScopeText();
                RebuildTagRows();
            };

            Grid.SetColumn(delete, 2);
            grid.Children.Add(delete);

            // 重命名
            var rename = new Button
            {
                Content = "改名",
                Padding = new Thickness(10, 2, 10, 2),
                MinHeight = 24,
            };

            rename.Click += (s, e) =>
            {
                string input = InputDialog.Show(this, "重命名标签", "新的标签名称：", tag.Name);
                if (input == null)
                {
                    return;
                }

                input = input.Trim();
                if (input.Length == 0)
                {
                    return;
                }

                if (_data.Schema.TagNameExists(_table.Id, input, tag.Id))
                {
                    MessageWindow.Show(this, "该表已有同名标签。", "重命名标签", MessageTone.Warning);
                    return;
                }

                _data.Schema.RenameTag(tag.Id, input);
                Changed = true;
                RebuildTagRows();
            };

            Grid.SetColumn(rename, 3);
            grid.Children.Add(rename);

            return grid;
        }

        /// <summary>把标签加到所有目标对象上（批量）。</summary>
        private void AddToCurrent(Tag tag)
        {
            if (!HasTargets)
            {
                return;
            }

            try
            {
                if (_toGroup)
                {
                    foreach (string gv in _groupValues)
                    {
                        _data.Schema.LinkTagToGroupValue(tag.Id, _table.Id, _column.Id, gv);
                    }
                }
                else
                {
                    foreach (long rowId in _rowIds)
                    {
                        _data.Schema.LinkTagToRow(tag.Id, _table.Id, rowId);
                    }
                }

                Changed = true;
                RefreshCurrentTags();
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "添加标签失败：\n\n" + ex.Message, "标签", MessageTone.Danger);
            }
        }

        /// <summary>把标签从所有目标对象上移除（标签本身保留）。</summary>
        private void RemoveFromCurrent(Tag tag)
        {
            if (!HasTargets)
            {
                return;
            }

            try
            {
                List<TagLink> links = _data.Schema.LoadTagLinks(_table.Id);

                foreach (TagLink l in links)
                {
                    if (l.TagId != tag.Id)
                    {
                        continue;
                    }

                    if (_toGroup)
                    {
                        if (l.Scope == TagScope.GroupValue &&
                            l.ColumnId == _column.Id &&
                            _groupValues.Contains(l.GroupValue ?? string.Empty))
                        {
                            _data.Schema.UnlinkTag(l.Id);
                        }
                    }
                    else
                    {
                        if (l.Scope == TagScope.Row && _rowIds.Contains(l.RowId))
                        {
                            _data.Schema.UnlinkTag(l.Id);
                        }
                    }
                }

                // 没有任何引用的标签自动消失
                _data.Schema.PurgeOrphanTags(_table.Id);
                Changed = true;
                RefreshCurrentTags();
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "移除标签失败：\n\n" + ex.Message, "标签", MessageTone.Danger);
            }
        }

        /// <summary>重新从库里读当前单元格的标签，保持界面与数据一致。</summary>
        private void RefreshCurrentTags()
        {
            if (_row == null)
            {
                return;
            }

            List<TagLink> links = _data.Schema.LoadTagLinks(_table.Id);
            var tagsById = new Dictionary<long, Tag>();
            foreach (Tag t in _data.Schema.LoadTags(_table.Id))
            {
                tagsById[t.Id] = t;
            }

            var current = new List<Tag>();

            foreach (TagLink l in links)
            {
                if (!tagsById.TryGetValue(l.TagId, out Tag tag))
                {
                    continue;
                }

                if (_toGroup)
                {
                    if (l.Scope == TagScope.GroupValue &&
                        l.ColumnId == _column.Id &&
                        GroupValueSplitter.Contains(CurrentValue, l.GroupValue))
                    {
                        current.Add(tag);
                    }
                }
                else
                {
                    if (l.Scope == TagScope.Row && l.RowId == _row.RowId)
                    {
                        current.Add(tag);
                    }
                }
            }

            if (_toGroup)
            {
                _row.Cells[_column.Ordinal].RefreshGroupTags(current);
            }
            else
            {
                _row.RefreshRowTags(current);
            }
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            if (!HasTargets)
            {
                MessageWindow.Show(this, "当前分组列单元格为空，无法添加分组标签，请先填写分组值。", "标签", MessageTone.Warning);
                return;
            }

            string name = (TxtNewTag.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                TxtNewTag.Focus();
                return;
            }

            try
            {
                // 同名标签复用
                List<Tag> tags = _data.Schema.LoadTags(_table.Id);
                Tag existing = null;
                foreach (Tag t in tags)
                {
                    if (string.Equals(t.Name, name, StringComparison.Ordinal))
                    {
                        existing = t;
                        break;
                    }
                }

                Tag tag = existing ?? _data.Schema.EnsureTag(_table.Id, name);
                TxtNewTag.Text = string.Empty;

                List<TagLink> links = _data.Schema.LoadTagLinks(_table.Id);

                // 已经在当前归属上就当作“移除”，方便反复切换
                if (IsAppliedToAllTargets(tag, links))
                {
                    RemoveFromCurrent(tag);
                }
                else
                {
                    AddToCurrent(tag);
                }

                BuildScopeText();
                RebuildTagRows();
                TxtNewTag.Focus();
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "操作失败：\n\n" + ex.Message, "标签", MessageTone.Danger);
            }
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            this.CloseWithResult(Changed);
        }
    }
}
