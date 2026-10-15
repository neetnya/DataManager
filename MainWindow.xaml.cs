using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DataManager.Data;
using DataManager.Models;
using DataManager.Services;
using DataManager.ViewModels;
using DataManager.Dialogs;

namespace DataManager
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<RowVm> _rowSource = new ObservableCollection<RowVm>();

        private AppTable _table;
        private QuerySpec _spec = new QuerySpec();
        private Dictionary<long, Tag> _tagsById = new Dictionary<long, Tag>();
        private List<string[]> _clipboardRows = new List<string[]>();

        private bool _suppressEditCommit;
        private bool _suppressWidthSave;
        private bool _widthDirty;
        private System.Windows.Threading.DispatcherTimer _widthSaveTimer;
        private CellVm _editingCell;
        private string _editingOriginal;
        private int _rightClickColumnIndex = -1;

        /// <summary>撤销历史：一次单元格修改（含粘贴的每个单元格）记一条，最多保留 10 步。</summary>
        private readonly List<UndoEntry> _undoStack = new List<UndoEntry>();
        private const int MaxUndoSteps = 10;

        public MainWindow()
        {
            InitializeComponent();

            Grid.ItemsSource = _rowSource;

            // 记录普通状态下的尺寸，供取消最大化后恢复
            Width = Math.Max(MinWidth, App.Settings.WindowWidth);
            Height = Math.Max(MinHeight, App.Settings.WindowHeight);

            // 打开即最大化
            WindowState = WindowState.Maximized;

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            HookColumnResizeEvents();
            RefreshTableList();
            SelectFirstTable();
        }

        private AppData Data => App.Data;

        // =================================================================
        // 表列表
        // =================================================================

        private void RefreshTableList()
        {
            long? keep = _table?.Id;
            TableList.Items.Clear();

            foreach (AppTable t in Data.Tables)
            {
                TableList.Items.Add(new ListBoxItem
                {
                    Content = t.Name,
                    Tag = t.Id,
                    ToolTip = "列数：" + t.Columns.Count.ToString(CultureInfo.InvariantCulture),
                });
            }

            TxtNoTables.Visibility = Data.Tables.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (keep.HasValue)
            {
                foreach (ListBoxItem item in TableList.Items)
                {
                    if ((long)item.Tag == keep.Value)
                    {
                        TableList.SelectedItem = item;
                        return;
                    }
                }
            }
        }

        private void SelectFirstTable()
        {
            if (TableList.Items.Count > 0)
            {
                TableList.SelectedIndex = 0;
            }
            else
            {
                _table = null;
                _rowSource.Clear();
                UpdateToolbarState();
                TxtFooter.Text = "还没有数据表，请点击左上角齿轮【设置】创建。";
            }
        }

        /// <summary>切换表或关窗前把列宽落盘。</summary>
        private void OnTableSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!(TableList.SelectedItem is ListBoxItem item))
            {
                return;
            }

            // 切表前把上一张表调整过的列宽落盘
            FlushColumnWidthsIfNeeded();

            long tableId = (long)item.Tag;
            AppTable table = Data.FindTable(tableId);
            if (table == null)
            {
                return;
            }

            _table = table;
            _spec = new QuerySpec();

            BuildColumns();
            ReloadRows();
            RebuildTagBar();
            UpdateToolbarState();
        }

        // =================================================================
        // 列构造
        // =================================================================

        private void BuildColumns()
        {
            _suppressWidthSave = true;
            Grid.Columns.Clear();

            // 行号由行头（RowHeader）显示，这里只放数据列
            foreach (AppColumn c in _table.Columns)
            {
                Grid.Columns.Add(BuildColumn(c));
            }

            _suppressWidthSave = false;
            ApplyAppearance();
        }

        /// <summary>
        /// 订阅列宽拖动结束。必须用 AddHandler(handledEventsToo: true)：
        /// 列头模板里的分隔条 Thumb 会把 DragCompleted 标记为已处理，
        /// 用 XAML 属性挂的事件收不到，列宽就永远记不下来。
        /// </summary>
        private void HookColumnResizeEvents()
        {
            Grid.AddHandler(
                System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
                new System.Windows.Controls.Primitives.DragCompletedEventHandler(OnColumnGripperDragCompleted),
                true);
        }

        private DataGridColumn BuildColumn(AppColumn column)
        {
            // 恢复用户上次调整过的列宽比例（0~1，Star 单位）；没有记录时按 1 Star 均分。
            // 旧版存的是像素值（> 1），识别为无效，回退到均分。
            double saved = App.Settings.GetColumnWidth(_table.Id, column.PhysicalName);
            DataGridLength width = saved > 0 && saved <= 1
                ? new DataGridLength(saved, DataGridLengthUnitType.Star)
                : new DataGridLength(1, DataGridLengthUnitType.Star);

            var col = new DataGridTemplateColumn
            {
                Width = width,
                MinWidth = 60,
                CellTemplate = BuildCellTemplate(column),
                CellEditingTemplate = BuildCellEditingTemplate(column),
                Header = BuildHeader(column),
                CanUserResize = true,
            };

            col.SetValue(WidthHandlerProperty, column);
            return col;
        }

        /// <summary>用户拖动列分隔线后，把整张表所有列的宽度比例一起落盘。</summary>
        private void OnColumnGripperDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            if (_suppressWidthSave || _table == null)
            {
                return;
            }

            if (!(e.OriginalSource is System.Windows.Controls.Primitives.Thumb thumb))
            {
                return;
            }

            // 找到这个 Thumb 属于哪个列头，确认是列宽拖动而非其他 Thumb
            var header = FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(thumb);
            if (header == null || header.Column == null)
            {
                return;
            }

            SaveAllColumnWidths();
        }

        /// <summary>把所有列当前宽度换算成比例（0~1）落盘，保证总和为 1。</summary>
        private void SaveAllColumnWidths()
        {
            if (_table == null || Grid.Columns.Count == 0)
            {
                return;
            }

            double total = 0;
            foreach (DataGridColumn col in Grid.Columns)
            {
                total += col.ActualWidth;
            }

            if (total <= 0)
            {
                return;
            }

            bool changed = false;
            foreach (DataGridColumn col in Grid.Columns)
            {
                var column = col.GetValue(WidthHandlerProperty) as AppColumn;
                if (column == null)
                {
                    continue;
                }

                double ratio = col.ActualWidth / total;
                double existing = App.Settings.GetColumnWidth(_table.Id, column.PhysicalName);
                if (Math.Abs(existing - ratio) < 0.0001)
                {
                    continue;
                }

                App.Settings.SetColumnWidth(_table.Id, column.PhysicalName, ratio);
                changed = true;
            }

            if (changed)
            {
                _widthDirty = true;
                ScheduleWidthSave();
            }
        }

        /// <summary>挂在列上、用于反查该列对应哪个 AppColumn 的附加属性。</summary>
        private static readonly DependencyProperty WidthHandlerProperty =
            DependencyProperty.RegisterAttached(
                "WidthHandler",
                typeof(object),
                typeof(MainWindow),
                new PropertyMetadata(null));

        /// <summary>把全局外观设置应用到右侧表格。</summary>
        public void ApplyAppearance()
        {
            AppSettings s = App.Settings;

            var family = string.IsNullOrWhiteSpace(s.GridFontFamily)
                ? (Application.Current.Resources["UiFont"] as FontFamily)
                : new FontFamily(s.GridFontFamily);

            Grid.FontFamily = family;
            Grid.FontSize = s.GridFontSize > 0 ? s.GridFontSize : 13;

            Grid.FontWeight = s.GridFontBold ? FontWeights.SemiBold : FontWeights.Normal;
            Grid.FontStyle = s.GridFontItalic ? FontStyles.Italic : FontStyles.Normal;

            if (s.GridRowHeight > 0)
            {
                Grid.RowHeight = s.GridRowHeight;
            }

            Grid.GridLinesVisibility = s.GridShowLines ? DataGridGridLinesVisibility.All : DataGridGridLinesVisibility.None;
        }

        /// <summary>列宽有改动时把 config.json 落盘（节流，避免拖动过程中频繁写盘）。</summary>
        private void FlushColumnWidthsIfNeeded()
        {
            if (!_widthDirty)
            {
                return;
            }

            _widthDirty = false;

            try
            {
                SettingsService.Save(App.Settings);
            }
            catch (Exception)
            {
                // 写配置失败不影响使用
            }
        }

        /// <summary>列宽变化后延迟落盘。</summary>
        private void ScheduleWidthSave()
        {
            if (_widthSaveTimer == null)
            {
                _widthSaveTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(700),
                };

                _widthSaveTimer.Tick += (s, e) =>
                {
                    _widthSaveTimer.Stop();
                    FlushColumnWidthsIfNeeded();
                };
            }

            _widthSaveTimer.Stop();
            _widthSaveTimer.Start();
        }

        /// <summary>列头：只有一个搜索控件（分组列为下拉）。列没有名字，所以不显示列名与配置文字。</summary>
        private object BuildHeader(AppColumn column)
        {
            var panel = new Grid { Margin = new Thickness(4, 0, 4, 0) };

            _spec.ColumnFilters.TryGetValue(column.Id, out string existing);
            existing = existing ?? string.Empty;

            FrameworkElement input;
            if (column.IsGroup)
            {
                // 分组列：查询用下拉列表，候选值为该列已有值（去重），也可直接输入。
                // 不提供“(全部)”项——清空输入框就等于全部，加一项是重复的。
                var combo = new ComboBox
                {
                    Tag = column.Id,
                    ToolTip = "按分组值筛选（可下拉选择，也可直接输入；清空表示全部）",
                    FontSize = 13,
                    Height = 28,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsEditable = true,
                    Text = existing,
                };

                foreach (string v in Data.Rows.DistinctValues(_table, column))
                {
                    combo.Items.Add(v);
                }

                combo.SelectionChanged += OnGroupFilterSelected;
                combo.LostFocus += OnGroupFilterCommitted;

                // 直接清空文本（例如全选删除、按 Esc 后清空）也要立刻生效，
                // 不能只等失焦——否则筛选条件还留着，用户会以为界面坏了。
                combo.AddHandler(
                    TextBox.TextChangedEvent,
                    new TextChangedEventHandler(OnGroupFilterTextChanged));

                input = combo;
            }
            else
            {
                var box = new TextBox
                {
                    Tag = column.Id,
                    ToolTip = "搜索本列（模糊、大小写不敏感）",
                    FontSize = 13,
                    Height = 28,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(6, 2, 6, 2),
                    Text = existing,
                };

                box.TextChanged += OnColumnSearchChanged;
                input = box;
            }

            panel.Children.Add(input);
            return panel;
        }

        /// <summary>分组列下拉选择：选中的值即筛选条件。</summary>
        private void OnGroupFilterSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is ComboBox combo) || !(combo.Tag is long columnId))
            {
                return;
            }

            if (combo.SelectedItem is string picked)
            {
                SetColumnFilter(columnId, picked);
            }
        }

        /// <summary>分组列手工输入后按回车或失焦生效；清空即取消该列筛选。</summary>
        private void OnGroupFilterCommitted(object sender, RoutedEventArgs e)
        {
            if (!(sender is ComboBox combo) || !(combo.Tag is long columnId))
            {
                return;
            }

            SetColumnFilter(columnId, (combo.Text ?? string.Empty).Trim());
        }

        /// <summary>
        /// 分组列输入框的文本变化。只在“清空”时立即生效：
        /// 逐字输入过程中不该反复查库（那是防抖负责的）。
        /// </summary>
        private void OnGroupFilterTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!(sender is ComboBox combo) || !(combo.Tag is long columnId))
            {
                return;
            }

            if (!string.IsNullOrEmpty(combo.Text))
            {
                return;
            }

            // 清空了：立刻取消该列筛选
            if (_spec.ColumnFilters.ContainsKey(columnId))
            {
                SetColumnFilter(columnId, string.Empty);
            }
        }

        private void SetColumnFilter(long columnId, string text)
        {
            if (_table == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                _spec.ColumnFilters.Remove(columnId);
            }
            else
            {
                _spec.ColumnFilters[columnId] = text;
            }

            DebounceReload();
        }

        /// <summary>单元格展示模板（值 + 分组/行标签角标）。</summary>
        private DataTemplate BuildCellTemplate(AppColumn column)
        {
            var template = new DataTemplate();

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding("Cells[" + column.Ordinal + "].Value")
            {
                Mode = BindingMode.OneWay,
            });
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Cells[" + column.Ordinal + "].Value"));

            // 分组列右侧显示分组标签角标；其余列右侧显示整行标签角标。
            bool isGroup = column.IsGroup;
            string tagBindingPath = isGroup ? "Cells[" + column.Ordinal + "].GroupTagText" : "RowTagText";

            var dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 0, 6, 0));
            dock.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dock.SetValue(DockPanel.LastChildFillProperty, true);

            var tagText = new FrameworkElementFactory(typeof(TextBlock));
            tagText.SetBinding(TextBlock.TextProperty, new Binding(tagBindingPath));
            tagText.SetValue(TextBlock.FontSizeProperty, 10.5);
            tagText.SetValue(TextBlock.ForegroundProperty, Application.Current.Resources["AccentBrush"]);
            tagText.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            tagText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            tagText.SetValue(FrameworkElement.MaxWidthProperty, 96.0);
            tagText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            tagText.SetValue(DockPanel.DockProperty, Dock.Right);
            dock.AppendChild(tagText);
            dock.AppendChild(text);

            template.VisualTree = dock;
            return template;
        }

        /// <summary>编辑模板：分组列用可编辑下拉，其余用文本框。</summary>
        private DataTemplate BuildCellEditingTemplate(AppColumn column)
        {
            var template = new DataTemplate();

            if (column.IsGroup)
            {
                var combo = new FrameworkElementFactory(typeof(ComboBox));
                combo.SetValue(ComboBox.IsEditableProperty, true);
                combo.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
                combo.SetBinding(ComboBox.TextProperty, new Binding("Cells[" + column.Ordinal + "].Value")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });
                combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Cells[" + column.Ordinal + "].GroupCandidates"));
                template.VisualTree = combo;
            }
            else
            {
                var box = new FrameworkElementFactory(typeof(TextBox));
                box.SetValue(Control.PaddingProperty, new Thickness(5, 2, 5, 2));
                box.SetValue(Control.BorderThicknessProperty, new Thickness(0));
                box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                box.SetBinding(TextBox.TextProperty, new Binding("Cells[" + column.Ordinal + "].Value")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });
                template.VisualTree = box;
            }

            return template;
        }

        // =================================================================
        // 载入数据
        // =================================================================

        private void ReloadRows()
        {
            _rowSource.Clear();

            if (_table == null)
            {
                return;
            }

            _tagsById = new Dictionary<long, Tag>();
            foreach (Tag t in Data.Schema.LoadTags(_table.Id))
            {
                _tagsById[t.Id] = t;
            }

            // 分组列候选值（去重）
            var candidates = new Dictionary<long, List<string>>();
            foreach (AppColumn c in _table.Columns)
            {
                if (c.IsGroup)
                {
                    candidates[c.Id] = Data.Rows.DistinctValues(_table, c);
                }
            }

            List<DataRow> rows = Data.Rows.Query(_table, _spec);
            var links = Data.Schema.LoadTagLinks(_table.Id);

            // 归属行的标签
            var rowTags = new Dictionary<long, List<Tag>>();
            // 归属分组值的标签：列 Id + 值 → 标签
            var groupTags = new Dictionary<string, List<Tag>>();

            foreach (TagLink l in links)
            {
                if (!_tagsById.TryGetValue(l.TagId, out Tag tag))
                {
                    continue;
                }

                if (l.Scope == TagScope.Row)
                {
                    if (!rowTags.TryGetValue(l.RowId, out List<Tag> list))
                    {
                        list = new List<Tag>();
                        rowTags[l.RowId] = list;
                    }

                    list.Add(tag);
                }
                else
                {
                    string key = GroupKey(l.ColumnId, l.GroupValue);
                    if (!groupTags.TryGetValue(key, out List<Tag> list))
                    {
                        list = new List<Tag>();
                        groupTags[key] = list;
                    }

                    list.Add(tag);
                }
            }

            int index = 1;
            foreach (DataRow r in rows)
            {
                var vm = new RowVm(r.RowId, _table.Columns.Count) { DisplayIndex = index++ };

                for (int i = 0; i < _table.Columns.Count; i++)
                {
                    AppColumn c = _table.Columns[i];
                    string value = r.Get(i);

                    var cell = new CellVm(c.Ordinal, value, c.IsGroup, c.IsUnique);
                    if (c.IsGroup)
                    {
                        cell.GroupCandidates = candidates.TryGetValue(c.Id, out List<string> list)
                            ? list
                            : new List<string>();

                        // 分组列可能一个单元格含多个分组值（按分隔符拆分），
                        // 把每个分组值上挂的标签合并到一起展示。
                        var collected = new List<Tag>();
                        var seen = new HashSet<long>();
                        foreach (string part in GroupValueSplitter.Split(value))
                        {
                            if (!groupTags.TryGetValue(GroupKey(c.Id, part), out List<Tag> gt))
                            {
                                continue;
                            }

                            foreach (Tag t in gt)
                            {
                                if (seen.Add(t.Id))
                                {
                                    collected.Add(t);
                                }
                            }
                        }

                        if (collected.Count > 0)
                        {
                            cell.RefreshGroupTags(collected);
                        }
                    }

                    vm.Cells[i] = cell;
                }

                if (rowTags.TryGetValue(r.RowId, out List<Tag> rt))
                {
                    vm.RefreshRowTags(rt);
                }

                _rowSource.Add(vm);
            }

            UpdateFooter();
            UpdateEmptyHint();
        }

        private static string GroupKey(long columnId, string value)
        {
            return columnId.ToString(CultureInfo.InvariantCulture) + "\u0001" + (value ?? string.Empty);
        }

        private void UpdateFooter()
        {
            if (_table == null)
            {
                TxtFooter.Text = string.Empty;
                return;
            }

            string text = "共 " + _rowSource.Count.ToString(CultureInfo.InvariantCulture) + " 行";
            if (_spec != null && _spec.HasAnyFilter)
            {
                text += "（已筛选）";
            }

            TxtFooter.Text = text;
        }

        private void UpdateEmptyHint()
        {
            bool empty = _table != null && _rowSource.Count == 0;
            TxtEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            TxtEmptyHint.Text = _spec != null && _spec.HasAnyFilter
                ? "没有符合条件的数据。"
                : "还没有数据。点击【添加】新增一行。";
        }

        private void UpdateToolbarState()
        {
            bool has = _table != null;
            BtnAddRow.IsEnabled = has && _table.Columns.Count > 0;
            BtnAddTag.IsEnabled = has;
            BtnDeleteRows.IsEnabled = has;

            BtnQuickQuery.Visibility = has && _table.HasQuickQuery ? Visibility.Visible : Visibility.Collapsed;
            BtnClearFilter.Visibility = _spec != null && _spec.HasAnyFilter ? Visibility.Visible : Visibility.Collapsed;
        }

        // =================================================================
        // 搜索
        // =================================================================

        private void OnColumnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (_table == null || !(sender is TextBox box) || !(box.Tag is long columnId))
            {
                return;
            }

            string text = box.Text ?? string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                _spec.ColumnFilters.Remove(columnId);
            }
            else
            {
                _spec.ColumnFilters[columnId] = text;
            }

            // 输入过程中防抖，避免每敲一个字都查一次库
            DebounceReload();
        }

        private System.Windows.Threading.DispatcherTimer _debounceTimer;

        /// <summary>延迟 250ms 再刷新，用户连续输入时只查一次。</summary>
        private void DebounceReload()
        {
            if (_debounceTimer == null)
            {
                _debounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250),
                };

                _debounceTimer.Tick += (s, args) =>
                {
                    _debounceTimer.Stop();
                    ReloadRows();
                    UpdateToolbarState();
                };
            }

            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnClearFilter(object sender, RoutedEventArgs e)
        {
            _spec = new QuerySpec();
            BuildColumns();
            ReloadRows();
            RebuildTagBar();
            UpdateToolbarState();
        }

        // =================================================================
        // 单元格编辑
        // =================================================================

        private void OnGridBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (_table == null || !(e.Row.Item is RowVm row))
            {
                return;
            }

            int index = e.Column.DisplayIndex;
            if (index < 0 || index >= _table.Columns.Count)
            {
                e.Cancel = true;
                return;
            }

            // URL 列不进入编辑：双击只负责用浏览器打开链接
            if (_table.Columns[index].IsUrl)
            {
                e.Cancel = true;
                return;
            }

            _editingCell = row.Cells[index];
            _editingOriginal = _editingCell.Value;
        }

        private void OnGridCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (_suppressEditCommit || _table == null || !(e.Row.Item is RowVm row) || _editingCell == null)
            {
                return;
            }

            int index = e.Column.DisplayIndex;
            if (index < 0 || index >= _table.Columns.Count)
            {
                return;
            }

            AppColumn column = _table.Columns[index];

            // 按需求：trim 在编辑时就处理好
            string newValue = (_editingCell.Value ?? string.Empty).Trim();
            string oldValue = _editingOriginal ?? string.Empty;

            if (string.Equals(newValue, oldValue, StringComparison.Ordinal))
            {
                // 值没变（或只差空白）时把界面上的值也归位
                if (!string.Equals(_editingCell.Value, oldValue, StringComparison.Ordinal))
                {
                    _suppressEditCommit = true;
                    _editingCell.Value = oldValue;
                    _suppressEditCommit = false;
                }

                _editingCell = null;
                return;
            }

            string error = Data.Rows.UpdateCell(_table, row.RowId, column, newValue);
            if (error != null)
            {
                // 违反唯一：阻止保存并提示，恢复原值
                _suppressEditCommit = true;
                _editingCell.Value = oldValue;
                _suppressEditCommit = false;

                MessageWindow.Show(this, error, "无法保存", Dialogs.MessageTone.Warning);

                // 分组列回滚后无需刷新标签
                _editingCell = null;
                return;
            }

            PushUndo(row.RowId, column.Ordinal, oldValue, newValue);

            if (column.IsGroup)
            {
                // 分组值变了：刷新该列的候选值与标签归属。
                // 用 BeginInvoke 延后，避免在 DataGrid 结束编辑的过程中重建行集合。
                List<string> fresh = Data.Rows.DistinctValues(_table, column);
                foreach (RowVm r in _rowSource)
                {
                    r.Cells[index].GroupCandidates = fresh;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ReloadRows();
                    RebuildTagBar();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }

            _editingCell = null;
        }

        private void OnGridLoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 双击单元格：URL 列用默认浏览器打开链接（不校验、不进入编辑）；
        /// 其余列直接进入编辑并立即聚焦（避免默认行为下要三击、或光标未落位的问题）。
        /// </summary>
        private void OnGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_table == null)
            {
                return;
            }

            // 只处理左键；右键双击交给上下文菜单流程
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            // 双击落在列头（筛选框）上时，交给搜索框本身处理，不要抢占
            DependencyObject source = e.OriginalSource as DependencyObject;
            if (source != null && FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(source) != null)
            {
                return;
            }

            DataGridCell cell = source != null ? FindAncestor<DataGridCell>(source) : null;
            if (cell == null || !(cell.DataContext is RowVm row) || cell.Column == null)
            {
                return;
            }

            int index = cell.Column.DisplayIndex;
            if (index < 0 || index >= _table.Columns.Count)
            {
                return;
            }

            AppColumn column = _table.Columns[index];

            if (column.IsUrl)
            {
                // BeginningEdit 已拦截 URL 列，这里直接打开链接即可
                OpenUrlInBrowser(row.Cells[index].Value);
                e.Handled = true;
                return;
            }

            // 普通列：进入编辑，并把焦点切到编辑控件，让光标立刻可用
            if (!cell.IsEditing)
            {
                Grid.CurrentCell = new DataGridCellInfo(cell);
                Grid.BeginEdit();
            }

            FocusEditingControl(cell);

            e.Handled = true;
        }

        /// <summary>把键盘焦点交给当前编辑单元格里的输入控件（TextBox / 可编辑 ComboBox），并全选文本。</summary>
        private void FocusEditingControl(DataGridCell cell)
        {
            // BeginEdit 切换模板是异步的，等布局完成后再聚焦
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!cell.IsEditing)
                {
                    return;
                }

                var content = cell.Content as UIElement;
                if (content == null)
                {
                    return;
                }

                Control target = null;
                if (content is TextBox tb)
                {
                    target = tb;
                }
                else if (content is ComboBox cb)
                {
                    target = cb;
                }

                // ComboBox 可编辑时，真正接收输入的是它内部的 TextBox 部件
                if (target is ComboBox combo && combo.IsEditable && combo.Template != null)
                {
                    target = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox ?? target;
                }

                if (target != null)
                {
                    target.Focus();
                    Keyboard.Focus(target);

                    if (target is TextBox box)
                    {
                        box.SelectAll();
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>用系统默认浏览器打开链接；内容为空或打开失败时给出轻提示。</summary>
        private void OpenUrlInBrowser(string url)
        {
            string link = (url ?? string.Empty).Trim();
            if (link.Length == 0)
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = link,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "无法打开链接：\n\n" + ex.Message, "URL", Dialogs.MessageTone.Warning);
            }
        }

        private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCellInfo();
        }

        private void UpdateCellInfo()
        {
            if (_table == null || Grid.SelectedCells.Count == 0)
            {
                TxtCellInfo.Text = string.Empty;
                return;
            }

            DataGridCellInfo info = Grid.SelectedCells[0];
            int index = info.Column.DisplayIndex;
            if (!(info.Item is RowVm row) || index < 0 || index >= _table.Columns.Count)
            {
                TxtCellInfo.Text = string.Empty;
                return;
            }

            AppColumn column = _table.Columns[index];
            string value = row.Cells[index].Value ?? string.Empty;
            string preview = value.Length > 60 ? value.Substring(0, 60) + "…" : value;

            var owner = new List<string> { "行 " + row.DisplayIndex.ToString(CultureInfo.InvariantCulture) };
            if (column.IsGroup)
            {
                owner.Add("分组值");
            }

            TxtCellInfo.Text = "已选：" + column.DisplayName + " / " + string.Join(" / ", owner) +
                               (preview.Length > 0 ? "　" + preview : string.Empty);
        }

        // =================================================================
        // 剪贴板：Ctrl+C / Ctrl+V
        // =================================================================

        /// <summary>
        /// 窗口级 Ctrl+F 快捷查询：只要软件在前台且当前表可用快捷查询就触发，
        /// 不再要求焦点落在表格内。用 PreviewKeyDown 在隧道路由阶段拦截，
        /// 早于表格和各输入控件收到按键。
        /// </summary>
        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                // 正在编辑单元格时，把 Ctrl+F 让给编辑控件（例如文本框里的查找），不抢占
                if (IsCellEditing())
                {
                    return;
                }

                if (_table != null && _table.HasQuickQuery)
                {
                    OnQuickQuery(this, new RoutedEventArgs());
                    e.Handled = true;
                }

                return;
            }

            if (e.Key == Key.Z && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                // 焦点在某个文本框（单元格编辑框、列头搜索框等）里时，
                // 把 Ctrl+Z 让给文本框自身的撤销，不抢占。
                if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
                {
                    return;
                }

                if (Undo())
                {
                    e.Handled = true;
                }
            }
        }

        private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 正在编辑单元格时，把快捷键让给编辑控件本身
            bool editing = IsCellEditing();

            // 焦点在列头搜索框 / 分组下拉里时，快捷键交给那个输入控件，
            // 否则在搜索框里 Ctrl+V 会去粘贴单元格数据，搜索框却还是空的。
            if (IsHeaderInputFocused())
            {
                return;
            }

            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.C && !editing)
                {
                    CopySelection();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.V && !editing)
                {
                    PasteAtSelection();
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Delete && !editing)
            {
                OnDeleteRows(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && editing)
            {
                CancelEdit();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 焦点是否落在列头上的搜索框或分组下拉里。
        /// 这些控件在 DataGrid 的可视树内，事件会先隧传到 DataGrid，
        /// 必须在处理前把它们排除掉。
        /// </summary>
        private bool IsHeaderInputFocused()
        {
            DependencyObject focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null)
            {
                return false;
            }

            // 搜索框 / 分组下拉在列头里
            if (focused is TextBox || focused is ComboBox)
            {
                return true;
            }

            // 兼容模板内部的子元素（例如可编辑 ComboBox 的 TextBox 部件）
            DependencyObject node = focused;
            while (node != null)
            {
                if (node is DataGridCell || node is DataGridRow)
                {
                    return false;
                }

                if (node is System.Windows.Controls.Primitives.DataGridColumnHeader)
                {
                    return true;
                }
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return false;
        }

        /// <summary>当前是否有一个单元格正处于编辑状态。</summary>
        private bool IsCellEditing()
        {
            if (!Grid.CurrentCell.IsValid || Grid.CurrentCell.Column == null)
            {
                return false;
            }

            var content = Grid.CurrentCell.Column.GetCellContent(Grid.CurrentCell.Item);
            if (content == null)
            {
                return false;
            }

            DependencyObject node = content;
            while (node != null)
            {
                if (node is DataGridCell cell)
                {
                    return cell.IsEditing;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return false;
        }

        private void CancelEdit()
        {
            if (_editingCell == null)
            {
                return;
            }

            _suppressEditCommit = true;
            _editingCell.Value = _editingOriginal;
            _suppressEditCommit = false;
            _editingCell = null;
            Grid.CancelEdit(DataGridEditingUnit.Cell);
            Grid.CancelEdit(DataGridEditingUnit.Row);
        }

        private void CopySelection()
        {
            if (_table == null || Grid.SelectedCells.Count == 0)
            {
                return;
            }

            var byRow = new SortedDictionary<int, SortedDictionary<int, string>>();
            foreach (DataGridCellInfo info in Grid.SelectedCells)
            {
                if (!(info.Item is RowVm row))
                {
                    continue;
                }

                int col = info.Column.DisplayIndex;
                if (col < 0 || col >= _table.Columns.Count)
                {
                    continue;
                }

                if (!byRow.TryGetValue(row.DisplayIndex, out SortedDictionary<int, string> line))
                {
                    line = new SortedDictionary<int, string>();
                    byRow[row.DisplayIndex] = line;
                }

                line[col] = row.Cells[col].Value ?? string.Empty;
            }

            if (byRow.Count == 0)
            {
                return;
            }

            var lines = new List<string>();
            foreach (KeyValuePair<int, SortedDictionary<int, string>> kv in byRow)
            {
                lines.Add(string.Join("\t", kv.Value.Values));
            }

            string text = string.Join("\r\n", lines);
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>按指定分隔符解析剪贴板文本（TSV 用；引号规则与 CSV 一致）。</summary>
        private static List<string[]> ParseDelimited(string text, char delimiter)
        {
            var rows = new List<string[]>();
            var field = new System.Text.StringBuilder();
            var current = new List<string>();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }

                    continue;
                }

                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    current.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    current.Add(field.ToString());
                    field.Clear();
                    rows.Add(current.ToArray());
                    current.Clear();
                }
                else if (c == '\n')
                {
                    current.Add(field.ToString());
                    field.Clear();
                    rows.Add(current.ToArray());
                    current.Clear();
                }
                else
                {
                    field.Append(c);
                }
            }

            // 最后一行（没有以换行结尾时）
            if (field.Length > 0 || current.Count > 0)
            {
                current.Add(field.ToString());
                rows.Add(current.ToArray());
            }

            return rows;
        }

        /// <summary>
        /// 粘贴：从锚点单元格开始按块填涂，空值会清空目标单元格；
        /// 唯一约束冲突时整体回滚并提示。
        /// </summary>
        private void PasteAtSelection()
        {
            if (_table == null || Grid.SelectedCells.Count == 0)
            {
                return;
            }

            string text;
            try
            {
                text = Clipboard.GetText();
            }
            catch (Exception)
            {
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // 含制表符 → 按 TSV（表格复制格式）解析；否则按 CSV 解析
            List<string[]> block = text.IndexOf('\t') >= 0
                ? ParseDelimited(text, '\t')
                : CsvService.Parse(text);

            if (block.Count == 0)
            {
                return;
            }

            DataGridCellInfo anchor = Grid.SelectedCells[0];
            if (!(anchor.Item is RowVm anchorRow))
            {
                return;
            }

            int startCol = anchor.Column.DisplayIndex;
            if (startCol < 0)
            {
                startCol = 0;
            }

            int startRowIndex = _rowSource.IndexOf(anchorRow);
            if (startRowIndex < 0)
            {
                return;
            }

            // 先做整体校验
            var pending = new List<(RowVm Row, int Col, string Value)>();
            for (int r = 0; r < block.Count; r++)
            {
                int targetRow = startRowIndex + r;
                if (targetRow >= _rowSource.Count)
                {
                    break;
                }

                RowVm row = _rowSource[targetRow];
                for (int c = 0; c < block[r].Length; c++)
                {
                    int targetCol = startCol + c;
                    if (targetCol >= _table.Columns.Count)
                    {
                        break;
                    }

                    string value = block[r][c] ?? string.Empty;
                    string old = row.Cells[targetCol].Value ?? string.Empty;
                    if (string.Equals(old, value, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    pending.Add((row, targetCol, value));
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            // 校验 + 写入放在同一个事务里，避免校验与写入之间被插入数据
            using (SqliteConnection.Tx tx = Data.Connection.Transaction())
            {
                var seen = new Dictionary<string, string>();
                foreach ((RowVm row, int col, string value) in pending)
                {
                    AppColumn column = _table.Columns[col];
                    if (column.IsUnique && value.Length > 0)
                    {
                        string error = Data.Rows.ValidateUnique(_table, column, value, row.RowId);
                        if (error != null)
                        {
                            MessageWindow.Show(this, error, "无法粘贴", Dialogs.MessageTone.Warning);
                            return;
                        }

                        string key = col.ToString(CultureInfo.InvariantCulture) + "\u0001" + value.ToLowerInvariant();
                        if (seen.TryGetValue(key, out string owner) &&
                            owner != row.RowId.ToString(CultureInfo.InvariantCulture))
                        {
                            MessageWindow.Show(this, column.DisplayName + " 在本次粘贴内容中存在重复的值：" + value,
                                "无法粘贴",
                                Dialogs.MessageTone.Warning);
                            return;
                        }

                        seen[key] = row.RowId.ToString(CultureInfo.InvariantCulture);
                    }
                }

                foreach ((RowVm row, int col, string value) in pending)
                {
                    string old = row.Cells[col].Value ?? string.Empty;
                    string error = Data.Rows.UpdateCell(_table, row.RowId, _table.Columns[col], value);
                    if (error != null)
                    {
                        MessageWindow.Show(this, error, "无法粘贴", Dialogs.MessageTone.Warning);
                        return;
                    }

                    row.Cells[col].Value = value;
                    PushUndo(row.RowId, col, old, value);
                }

                tx.Complete();
            }

            ReloadRows();
        }

        // =================================================================
        // 行操作
        // =================================================================

        private void OnAddRow(object sender, RoutedEventArgs e)
        {
            if (_table == null)
            {
                return;
            }

            if (_table.Columns.Count == 0)
            {

                return;
            }

            var dialog = new RowEditorWindow(_table, null, Data)
            {
                Owner = this,
            };

            if (dialog.ShowDialog() == true)
            {
                ReloadRows();
            }
        }

        private void OnDeleteRows(object sender, RoutedEventArgs e)
        {
            if (_table == null)
            {
                return;
            }

            var rowIds = new List<long>();
            foreach (DataGridCellInfo info in Grid.SelectedCells)
            {
                if (info.Item is RowVm row && !rowIds.Contains(row.RowId))
                {
                    rowIds.Add(row.RowId);
                }
            }

            foreach (object item in Grid.SelectedItems)
            {
                if (item is RowVm row && !rowIds.Contains(row.RowId))
                {
                    rowIds.Add(row.RowId);
                }
            }

            if (rowIds.Count == 0)
            {

                return;
            }

            // 删除前把行数据与行标签记录下来，供 Ctrl+Z 撤销恢复
            var deletedValues = new List<string[]>();
            var deletedRowTags = new List<List<long>>();

            List<TagLink> links = Data.Schema.LoadTagLinks(_table.Id);
            foreach (long rid in rowIds)
            {
                DataRow dataRow = Data.Rows.GetRow(_table, rid);
                if (dataRow == null)
                {
                    // 行已不存在（极端情况），跳过记录但继续删除其余
                    deletedValues.Add(Array.Empty<string>());
                }
                else
                {
                    deletedValues.Add(dataRow.Values);
                }

                var tags = new List<long>();
                foreach (TagLink l in links)
                {
                    if (l.Scope == TagScope.Row && l.RowId == rid)
                    {
                        tags.Add(l.TagId);
                    }
                }

                deletedRowTags.Add(tags);
            }

            PushUndoDeleteRows(deletedValues, deletedRowTags);

            Data.Rows.DeleteRows(_table, rowIds);
            ReloadRows();
        }

        // =================================================================
        // 标签
        // =================================================================

        /// <summary>
        /// 重建标签条：每个标签一个按钮，计数 = 当前视图内**实际带该标签的行数**
        /// （分组标签会把同组的所有行都算上）。左键筛选，右键重命名/删除。
        /// </summary>
        private void RebuildTagBar()
        {
            TagBarItems.Items.Clear();
            TagBar.Visibility = Visibility.Collapsed;

            if (_table == null)
            {
                return;
            }

            List<Tag> allTags = Data.Schema.LoadTags(_table.Id);
            if (allTags.Count == 0)
            {
                return;
            }

            List<TagLink> links = Data.Schema.LoadTagLinks(_table.Id);

            // 行归属：rowId → 标签集合
            var rowTagIds = new Dictionary<long, HashSet<long>>();
            // 分组归属：分组键 → 标签集合
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

            // 逐行统计：一行可能同时通过“行标签”和“分组标签”命中同一个标签，只算一次
            var rowCount = new Dictionary<long, int>();
            foreach (RowVm row in _rowSource)
            {
                var hit = new HashSet<long>();

                if (rowTagIds.TryGetValue(row.RowId, out HashSet<long> direct))
                {
                    hit.UnionWith(direct);
                }

                for (int i = 0; i < _table.Columns.Count; i++)
                {
                    AppColumn c = _table.Columns[i];
                    if (!c.IsGroup)
                    {
                        continue;
                    }

                    // 分组列一个单元格可能含多个分组值，逐个拆分项命中
                    foreach (string part in GroupValueSplitter.Split(row.Cells[i].Value))
                    {
                        if (groupTagIds.TryGetValue(GroupKey(c.Id, part), out HashSet<long> viaGroup))
                        {
                            hit.UnionWith(viaGroup);
                        }
                    }
                }

                foreach (long tagId in hit)
                {
                    rowCount.TryGetValue(tagId, out int n);
                    rowCount[tagId] = n + 1;
                }
            }

            var panel = new WrapPanel { Orientation = Orientation.Horizontal };

            foreach (Tag tag in allTags)
            {
                rowCount.TryGetValue(tag.Id, out int count);

                var button = new Button
                {
                    Style = (Style)Application.Current.Resources["TagChipButton"],
                    Content = count > 0
                        ? tag.Name + " (" + count.ToString(CultureInfo.InvariantCulture) + ")"
                        : tag.Name,
                    Tag = tag,
                    ToolTip = "左键：按该标签筛选／取消筛选\n右键：重命名或删除该标签",
                };

                if (string.Equals(_spec.TagFilter, tag.Name, StringComparison.Ordinal))
                {
                    button.FontWeight = FontWeights.SemiBold;
                }

                button.Click += OnTagFilterClick;
                button.MouseRightButtonUp += OnTagManageClick;
                panel.Children.Add(button);
            }

            TagBarItems.Items.Add(panel);
            TagBar.Visibility = Visibility.Visible;
        }

        /// <summary>点击标签：按该标签筛选（再点一次取消）。</summary>
        private void OnTagFilterClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is Tag tag))
            {
                return;
            }

            _spec.TagFilter = string.Equals(_spec.TagFilter, tag.Name, StringComparison.Ordinal)
                ? null
                : tag.Name;

            ReloadRows();
            RebuildTagBar();
            UpdateToolbarState();
        }

        /// <summary>右键标签：重命名 / 删除。</summary>
        private void OnTagManageClick(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is Tag tag))
            {
                return;
            }

            e.Handled = true;

            var menu = new ContextMenu();

            var rename = new MenuItem { Header = "重命名标签" };
            rename.Click += (s, args) =>
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

                if (Data.Schema.TagNameExists(_table.Id, input, tag.Id))
                {
                    MessageWindow.Show(this, "该表已有同名标签。", "重命名标签", Dialogs.MessageTone.Warning);
                    return;
                }

                Data.Schema.RenameTag(tag.Id, input);

                // 正在按旧名筛选时同步切换
                if (string.Equals(_spec.TagFilter, tag.Name, StringComparison.Ordinal))
                {
                    _spec.TagFilter = input;
                }

                ReloadRows();
                RebuildTagBar();
            };

            // 移除本表内该标签的所有使用（标签本身保留）
            var unlink = new MenuItem { Header = "移除标签（清除本表所有使用）" };
            unlink.Click += (s, args) =>
            {
                foreach (TagLink l in Data.Schema.LoadTagLinks(_table.Id))
                {
                    if (l.TagId == tag.Id)
                    {
                        Data.Schema.UnlinkTag(l.Id);
                    }
                }

                Data.Schema.PurgeOrphanTags(_table.Id);

                if (string.Equals(_spec.TagFilter, tag.Name, StringComparison.Ordinal))
                {
                    _spec.TagFilter = null;
                }

                ReloadRows();
                RebuildTagBar();
            };

            var delete = new MenuItem { Header = "删除标签" };
            delete.Click += (s, args) =>
            {
                int rows = CountRowsUsingTag(tag);
                if (!MessageWindow.Confirm(
                        this,
                        "删除标签「" + tag.Name + "」？\n\n" +
                        "该标签目前被 " + rows.ToString(CultureInfo.InvariantCulture) +
                        " 行使用，删除后这些行上的标记会消失。\n" +
                        "表格里的数据本身不受影响。",
                        "删除标签",
                        MessageTone.Danger,
                        MessageButtons.OkCancel))
                {
                    return;
                }

                Data.Schema.DeleteTag(tag.Id);

                if (string.Equals(_spec.TagFilter, tag.Name, StringComparison.Ordinal))
                {
                    _spec.TagFilter = null;
                }

                ReloadRows();
                RebuildTagBar();
            };

            menu.Items.Add(rename);
            menu.Items.Add(unlink);
            menu.Items.Add(delete);
            menu.IsOpen = true;
        }

        /// <summary>该标签在整张表里被多少行用到（分组标签算同组所有行）。</summary>
        private int CountRowsUsingTag(Tag tag)
        {
            if (_table == null)
            {
                return 0;
            }

            List<TagLink> links = Data.Schema.LoadTagLinks(_table.Id);
            List<DataRow> all = Data.Rows.Query(_table, new QuerySpec());
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
                        for (int i = 0; i < _table.Columns.Count; i++)
                        {
                            AppColumn c = _table.Columns[i];
                            if (c.Id == l.ColumnId && c.IsGroup &&
                                GroupValueSplitter.Contains(r.Get(i), l.GroupValue))
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

        /// <summary>右键点单元格时先选中它，保证右键菜单作用在正确的单元格上。</summary>
        private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject source = e.OriginalSource as DependencyObject;
            DataGridCell cell = FindAncestor<DataGridCell>(source);

            if (cell != null)
            {
                _rightClickColumnIndex = cell.Column != null ? cell.Column.DisplayIndex : -1;

                if (!cell.IsSelected)
                {
                    Grid.SelectedCells.Clear();
                    Grid.CurrentCell = new DataGridCellInfo(cell);
                    cell.IsSelected = true;
                }
            }
        }

        /// <summary>
        /// 右键菜单弹出前，按当前单元格决定是否显示「移除标签」子菜单，
        /// 并把当前单元格上已挂的标签逐个列出来。
        /// </summary>
        private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            MenuRemoveTag.Items.Clear();
            MenuRemoveTag.Visibility = Visibility.Collapsed;

            if (_table == null || Grid.SelectedCells.Count == 0)
            {
                MenuAddTag.IsEnabled = false;
                return;
            }

            DataGridCellInfo info = Grid.SelectedCells[0];
            int index = info.Column.DisplayIndex;

            if (!(info.Item is RowVm row) || index < 0 || index >= _table.Columns.Count)
            {
                MenuAddTag.IsEnabled = false;
                return;
            }

            MenuAddTag.IsEnabled = true;

            AppColumn column = _table.Columns[index];
            var current = new List<Tag>();

            if (column.IsGroup)
            {
                foreach (Tag t in row.Cells[index].GroupTags)
                {
                    current.Add(t);
                }
            }
            else
            {
                foreach (Tag t in row.RowTags)
                {
                    current.Add(t);
                }
            }

            if (current.Count == 0)
            {
                return;
            }

            MenuRemoveTag.Visibility = Visibility.Visible;

            foreach (Tag tag in current)
            {
                var item = new MenuItem { Header = tag.Name };
                Tag captured = tag;

                item.Click += (s, args) =>
                {
                    RemoveTagFromCell(column, captured);
                };

                MenuRemoveTag.Items.Add(item);
            }
        }

        /// <summary>收集当前所有选中的行（去重），用于批量操作标签。</summary>
        private List<RowVm> CollectSelectedRows()
        {
            var rows = new List<RowVm>();

            foreach (DataGridCellInfo info in Grid.SelectedCells)
            {
                if (info.Item is RowVm row && !rows.Contains(row))
                {
                    rows.Add(row);
                }
            }

            foreach (object item in Grid.SelectedItems)
            {
                if (item is RowVm row && !rows.Contains(row))
                {
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>把某个标签从所有选中行的对应归属对象上移除（标签本身保留，批量）。</summary>
        private void RemoveTagFromCell(AppColumn column, Tag tag)
        {
            List<RowVm> rows = CollectSelectedRows();
            if (rows.Count == 0)
            {
                return;
            }

            // 归纳所有目标：行标签按行 Id，分组标签按「该列的分组值」。
            var rowIds = new HashSet<long>();
            var groupValues = new HashSet<string>();

            if (column.IsGroup)
            {
                foreach (RowVm row in rows)
                {
                    foreach (string part in GroupValueSplitter.Split(row.Cells[column.Ordinal].Value ?? string.Empty))
                    {
                        groupValues.Add(part);
                    }
                }
            }
            else
            {
                foreach (RowVm row in rows)
                {
                    rowIds.Add(row.RowId);
                }
            }

            foreach (TagLink l in Data.Schema.LoadTagLinks(_table.Id))
            {
                if (l.TagId != tag.Id)
                {
                    continue;
                }

                if (column.IsGroup)
                {
                    if (l.Scope == TagScope.GroupValue &&
                        l.ColumnId == column.Id &&
                        groupValues.Contains(l.GroupValue ?? string.Empty))
                    {
                        Data.Schema.UnlinkTag(l.Id);
                    }
                }
                else
                {
                    if (l.Scope == TagScope.Row && rowIds.Contains(l.RowId))
                    {
                        Data.Schema.UnlinkTag(l.Id);
                    }
                }
            }

            Data.Schema.PurgeOrphanTags(_table.Id);
            ReloadRows();
            RebuildTagBar();
        }

        /// <summary>在可视树里向上找指定类型的祖先。</summary>
        private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
        {
            while (node != null)
            {
                if (node is T typed)
                {
                    return typed;
                }

                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return null;
        }

        private void OnMenuCopy(object sender, RoutedEventArgs e)
        {
            CopySelection();
        }

        private void OnAddTag(object sender, RoutedEventArgs e)
        {
            if (_table == null)
            {
                return;
            }

            // 收集所有选中的行（去重）：多选时批量打标签
            List<RowVm> rows = CollectSelectedRows();

            if (rows.Count == 0)
            {
                return;
            }

            // 确定作用列：右键菜单用右键点中的列，否则用第一个选中单元格所在列
            int index;
            if (sender == MenuAddTag && _rightClickColumnIndex >= 0)
            {
                index = _rightClickColumnIndex;
            }
            else if (Grid.SelectedCells.Count > 0)
            {
                index = Grid.SelectedCells[0].Column.DisplayIndex;
            }
            else
            {
                index = 0;
            }

            if (index < 0 || index >= _table.Columns.Count)
            {
                return;
            }

            AppColumn column = _table.Columns[index];
            var dialog = new TagEditorWindow(_table, column, rows, Data)
            {
                Owner = this,
            };

            dialog.ShowDialog();

            if (dialog.Changed)
            {
                ReloadRows();
                RebuildTagBar();
            }
        }

        // =================================================================
        // 快捷查询
        // =================================================================

        private void OnQuickQuery(object sender, RoutedEventArgs e)
        {
            if (_table == null || !_table.HasQuickQuery)
            {
                return;
            }

            string text;
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            }
            catch (Exception)
            {
                MessageWindow.Show(this, "无法读取剪贴板。", "快捷查询", Dialogs.MessageTone.Warning);
                return;
            }

            // 始终在全表范围内查找
            QuickQueryResult result = Data.Rows.QuickQuery(_table, text);

            switch (result.Outcome)
            {
                case QuickQueryOutcome.Exact:
                    try
                    {
                        Clipboard.SetText(result.ClipboardValue ?? string.Empty);
                    }
                    catch (Exception)
                    {
                        MessageWindow.Show(this, "查询到结果，但写入剪贴板失败。", "快捷查询", Dialogs.MessageTone.Warning);
                    }

                    ClearAllFilters();
                    JumpToRow(result.RowId);
                    break;

                case QuickQueryOutcome.Fuzzy:
                    _spec.QuickFilterColumnId = result.FilterColumn.Id;
                    _spec.QuickFilterText = result.FilterText;
                    ReloadRows();
                    UpdateToolbarState();
                    break;

                default:
                    MessageWindow.Show(this, "没有查询到结果。\n\n查询内容：" + (string.IsNullOrEmpty(text) ? "(剪贴板为空)" : text),
                        "快捷查询",
                        Dialogs.MessageTone.Info);
                    break;
            }
        }

        private void ClearAllFilters()
        {
            _spec = new QuerySpec();
            BuildColumns();
            ReloadRows();
            RebuildTagBar();
            UpdateToolbarState();
        }

        /// <summary>跳转并选中指定行。</summary>
        private void JumpToRow(long? rowId)
        {
            if (!rowId.HasValue)
            {
                return;
            }

            RowVm target = null;
            foreach (RowVm r in _rowSource)
            {
                if (r.RowId == rowId.Value)
                {
                    target = r;
                    break;
                }
            }

            if (target == null)
            {
                return;
            }

            if (Grid.Columns.Count == 0)
            {
                return;
            }

            DataGridColumn first = Grid.Columns[0];

            Grid.SelectedCells.Clear();
            Grid.ScrollIntoView(target, first);
            Grid.CurrentCell = new DataGridCellInfo(target, first);
            Grid.SelectedCells.Add(Grid.CurrentCell);
            UpdateCellInfo();
        }
        // =================================================================
        // 设置
        // =================================================================

        private void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            Grid.CommitEdit(DataGridEditingUnit.Cell, true);
            Grid.CommitEdit(DataGridEditingUnit.Row, true);

            var window = new SettingsWindow
            {
                Owner = this,
            };

            // 外观设置实时预览到主窗口
            window.AppearanceChanged += (s, args) => ApplyAppearance();

            window.ShowDialog();

            if (window.SettingsChanged)
            {
                Data.ReloadTables();
                RefreshTableList();

                if (_table != null)
                {
                    AppTable refreshed = Data.FindTable(_table.Id);
                    if (refreshed == null)
                    {
                        SelectFirstTable();
                    }
                    else
                    {
                        _table = refreshed;
                        _spec = new QuerySpec();
                        BuildColumns();
                        ReloadRows();
                        RebuildTagBar();
                        UpdateToolbarState();
                    }
                }
                else
                {
                    SelectFirstTable();
                }

            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                // 最大化状态下 Width/Height 是屏幕尺寸，存下来没意义；
                // RestoreBounds 才是窗口处于普通状态时的尺寸，存这个。
                Rect bounds = WindowState == WindowState.Maximized
                    ? RestoreBounds
                    : new Rect(Left, Top, Width, Height);

                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    App.Settings.WindowWidth = bounds.Width;
                    App.Settings.WindowHeight = bounds.Height;
                }

                FlushColumnWidthsIfNeeded();
                SettingsService.Save(App.Settings);
            }
            catch (Exception)
            {
                // 保存窗口尺寸失败不影响退出
            }

            base.OnClosing(e);
        }

        // =================================================================
        // 撤销（Ctrl+Z）：单元格值修改
        // =================================================================

        /// <summary>撤销条目类型。</summary>
        private enum UndoKind
        {
            /// <summary>单元格值修改。</summary>
            Cell,

            /// <summary>删除行。</summary>
            DeleteRows,
        }

        /// <summary>一条可撤销操作。</summary>
        private sealed class UndoEntry
        {
            public UndoKind Kind;

            // —— 单元格修改（Cell）——
            public long TableId;
            public long RowId;
            public int ColumnOrdinal;
            public string OldValue;
            public string NewValue;

            // —— 删除行（DeleteRows）——
            /// <summary>被删行的值，按列顺序（Ordinal）。</summary>
            public List<string[]> DeletedRows;

            /// <summary>与 DeletedRows 一一对应：每行归属的行标签 TagId。</summary>
            public List<List<long>> DeletedRowTags;
        }

        /// <summary>记录一次已落库的单元格值变更，供 Ctrl+Z 恢复。超出上限时丢弃最旧记录。</summary>
        private void PushUndo(long rowId, int columnOrdinal, string oldValue, string newValue)
        {
            if (_table == null)
            {
                return;
            }

            _undoStack.Add(new UndoEntry
            {
                Kind = UndoKind.Cell,
                TableId = _table.Id,
                RowId = rowId,
                ColumnOrdinal = columnOrdinal,
                OldValue = oldValue ?? string.Empty,
                NewValue = newValue ?? string.Empty,
            });

            TrimUndoStack();
        }

        /// <summary>记录一次删除行操作（含行数据与行标签），供 Ctrl+Z 恢复。</summary>
        private void PushUndoDeleteRows(List<string[]> values, List<List<long>> tags)
        {
            if (_table == null)
            {
                return;
            }

            _undoStack.Add(new UndoEntry
            {
                Kind = UndoKind.DeleteRows,
                TableId = _table.Id,
                DeletedRows = values,
                DeletedRowTags = tags,
            });

            TrimUndoStack();
        }

        private void TrimUndoStack()
        {
            while (_undoStack.Count > MaxUndoSteps)
            {
                _undoStack.RemoveAt(0);
            }
        }

        /// <summary>
        /// 撤销最近一次操作（单元格修改或删除行）。返回是否执行了撤销。
        /// </summary>
        private bool Undo()
        {
            if (_undoStack.Count == 0)
            {
                return false;
            }

            UndoEntry entry = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);

            // 撤销针对的是当前表；跨表或表已删时直接丢弃这条记录
            if (_table == null || _table.Id != entry.TableId)
            {
                return true;
            }

            if (entry.Kind == UndoKind.DeleteRows)
            {
                UndoDeleteRows(entry);
            }
            else
            {
                UndoCell(entry);
            }

            ReloadRows();
            RebuildTagBar();
            return true;
        }

        /// <summary>撤销一次单元格修改：把旧值写回数据库。</summary>
        private void UndoCell(UndoEntry entry)
        {
            AppColumn column = entry.ColumnOrdinal >= 0 && entry.ColumnOrdinal < _table.Columns.Count
                ? _table.Columns[entry.ColumnOrdinal]
                : null;
            if (column == null)
            {
                return;
            }

            string error = Data.Rows.UpdateCell(_table, entry.RowId, column, entry.OldValue);
            if (error != null)
            {
                MessageWindow.Show(this, "无法撤销：" + error, "撤销", Dialogs.MessageTone.Warning);
            }
        }

        /// <summary>撤销一次删除行：把被删的行按原值重新插入，并恢复其行标签。</summary>
        private void UndoDeleteRows(UndoEntry entry)
        {
            if (entry.DeletedRows == null || entry.DeletedRows.Count == 0)
            {
                return;
            }

            using (SqliteConnection.Tx tx = Data.Connection.Transaction())
            {
                for (int i = 0; i < entry.DeletedRows.Count; i++)
                {
                    string[] values = entry.DeletedRows[i];

                    // 重新插入行数据；唯一约束冲突时整批回滚
                    string error = Data.Rows.ValidateRow(_table, values, null);
                    if (error != null)
                    {
                        MessageWindow.Show(this, "无法撤销删除：" + error, "撤销", Dialogs.MessageTone.Warning);
                        return;
                    }

                    long newRowId = Data.Rows.InsertRow(_table, values);

                    // 恢复该行归属的行标签（重新绑定到新 rid）
                    if (entry.DeletedRowTags != null && i < entry.DeletedRowTags.Count)
                    {
                        foreach (long tagId in entry.DeletedRowTags[i])
                        {
                            Data.Schema.LinkTagToRow(tagId, _table.Id, newRowId);
                        }
                    }
                }

                tx.Complete();
            }
        }
    }
}
