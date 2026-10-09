using System;
using System.Collections.Generic;
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

        /// <summary>窗口内发生过改动，关闭时调用方需要刷新。</summary>
        public bool Changed { get; private set; }

        public TagEditorWindow(AppTable table, AppColumn column, RowVm row, AppData data)
        {
            InitializeComponent();

            _table = table;
            _column = column;
            _row = row;
            _data = data;
            _toGroup = column.IsGroup;

            BuildScopeText();
            RebuildTagRows();

            Loaded += (s, e) => TxtNewTag.Focus();
        }

        private string CurrentValue => _row.Cells[_column.Ordinal].Value ?? string.Empty;

        /// <summary>当前归属对象上已挂的标签 Id。</summary>
        private HashSet<long> AppliedTagIds()
        {
            var ids = new HashSet<long>();

            if (_toGroup)
            {
                foreach (Tag t in _row.Cells[_column.Ordinal].GroupTags)
                {
                    ids.Add(t.Id);
                }
            }
            else
            {
                foreach (Tag t in _row.RowTags)
                {
                    ids.Add(t.Id);
                }
            }

            return ids;
        }

        /// <summary>该标签在整张表里被多少行用到（分组标签算同组所有行）。</summary>
        private int CountRowsUsing(Tag tag)
        {
            List<TagLink> links = _data.Schema.LoadTagLinks(_table.Id);
            List<DataRow> all = _data.Rows.Query(_table, new QuerySpec());

            int count = 0;
            foreach (DataRow r in all)
            {
                bool hit = false;

                foreach (TagLink l in links)
                {
                    if (l.TagId != tag.Id)
                    {
                        continue;
                    }

                    if (l.Scope == TagScope.Row)
                    {
                        if (l.RowId == r.RowId)
                        {
                            hit = true;
                            break;
                        }
                    }
                    else
                    {
                        // 分组归属：该行对应分组列的值等于标签绑定的值
                        for (int i = 0; i < _table.Columns.Count; i++)
                        {
                            AppColumn c = _table.Columns[i];
                            if (c.Id == l.ColumnId && c.IsGroup &&
                                string.Equals(r.Get(i), l.GroupValue, StringComparison.Ordinal))
                            {
                                hit = true;
                                break;
                            }
                        }

                        if (hit)
                        {
                            break;
                        }
                    }
                }

                if (hit)
                {
                    count++;
                }
            }

            return count;
        }

        private void BuildScopeText()
        {
            if (_toGroup)
            {
                string shown = CurrentValue.Length == 0 ? "(空值)" : CurrentValue;
                TxtScope.Text = "归属分组值：" + shown + "（该分组下的所有行共用这些标签）";
            }
            else
            {
                TxtScope.Text = "归属整行（第 " + _row.DisplayIndex + " 行）";
            }
        }

        private void RebuildTagRows()
        {
            TagRows.Children.Clear();

            HashSet<long> applied = AppliedTagIds();
            List<Tag> all = _data.Schema.LoadTags(_table.Id);

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
                TagRows.Children.Add(BuildTagRow(tag, applied.Contains(tag.Id)));
            }

            TxtSummary.Text = "本表共 " + all.Count + " 个标签";
        }

        private UIElement BuildTagRow(Tag tag, bool isApplied)
        {
            var grid = new Grid { Margin = new Thickness(4, 3, 4, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 名称 + 使用行数
            var name = new TextBlock
            {
                Text = tag.Name + "（" + CountRowsUsing(tag) + " 行）",
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
            };

            toggle.Click += (s, e) =>
            {
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
                int rows = CountRowsUsing(tag);

                if (!MessageWindow.Confirm(
                        this,
                        "删除标签「" + tag.Name + "」？\n\n" +
                        "该标签目前被 " + rows + " 行使用，删除后这些行上的标记会消失。\n" +
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

                _data.Schema.RenameTag(tag.Id, input);
                Changed = true;
                RebuildTagRows();
            };

            Grid.SetColumn(rename, 3);
            grid.Children.Add(rename);

            return grid;
        }

        /// <summary>把标签加到当前归属对象上。</summary>
        private void AddToCurrent(Tag tag)
        {
            try
            {
                if (_toGroup)
                {
                    _data.Schema.LinkTagToGroupValue(tag.Id, _table.Id, _column.Id, CurrentValue);
                }
                else
                {
                    _data.Schema.LinkTagToRow(tag.Id, _table.Id, _row.RowId);
                }

                Changed = true;
                RefreshCurrentTags();
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "添加标签失败：\n\n" + ex.Message, "标签", MessageTone.Danger);
            }
        }

        /// <summary>把标签从当前归属对象上移除（标签本身保留）。</summary>
        private void RemoveFromCurrent(Tag tag)
        {
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
                            string.Equals(l.GroupValue, CurrentValue, StringComparison.Ordinal))
                        {
                            _data.Schema.UnlinkTag(l.Id);
                        }
                    }
                    else
                    {
                        if (l.Scope == TagScope.Row && l.RowId == _row.RowId)
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
                        string.Equals(l.GroupValue, CurrentValue, StringComparison.Ordinal))
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
            string name = (TxtNewTag.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                TxtNewTag.Focus();
                return;
            }

            try
            {
                // 同名标签复用
                Tag existing = null;
                foreach (Tag t in _data.Schema.LoadTags(_table.Id))
                {
                    if (string.Equals(t.Name, name, StringComparison.Ordinal))
                    {
                        existing = t;
                        break;
                    }
                }

                Tag tag = existing ?? _data.Schema.EnsureTag(_table.Id, name);
                TxtNewTag.Text = string.Empty;

                // 已经在当前归属上就当作“移除”，方便反复切换
                if (AppliedTagIds().Contains(tag.Id))
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
