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
                TxtFooter.Text = "还没有数据表，请点击右上角【设置】创建。";
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
            // 恢复用户上次调整过的列宽；没有记录时按星号均分
            double saved = App.Settings.GetColumnWidth(_table.Id, column.PhysicalName);
            DataGridLength width = saved > 0
                ? new DataGridLength(saved)
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

        /// <summary>用户拖动列分隔线后记住列宽。</summary>
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

            // 找到这个 Thumb 属于哪个列头
            var header = FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(thumb);
            if (header == null || header.Column == null)
            {
                return;
            }

            var column = header.Column.GetValue(WidthHandlerProperty) as AppColumn;
            if (column == null)
            {
                return;
            }

            double width = header.Column.ActualWidth;
            if (width <= 0)
            {
                return;
            }

            double existing = App.Settings.GetColumnWidth(_table.Id, column.PhysicalName);
            if (Math.Abs(existing - width) < 0.5)
            {
                return;
            }

            App.Settings.SetColumnWidth(_table.Id, column.PhysicalName, width);
            _widthDirty = true;
            ScheduleWidthSave();
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
                    FontSize = 12,
                    MinHeight = 24,
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
                    FontSize = 12,
                    MinHeight = 24,
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

                        if (groupTags.TryGetValue(GroupKey(c.Id, value), out List<Tag> gt))
                        {
                            cell.RefreshGroupTags(gt);
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
        /// 粘贴：从锚点单元格开始按块填涂，只修改有值的单元格；
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
                    if (value.Length == 0)
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
                    Data.Connection.Execute(
                        "UPDATE " + Sql.Ident(_table.PhysicalName) + " SET " +
                        Sql.Ident(_table.Columns[col].PhysicalName) + " = ? WHERE rid = ?",
                        value,
                        row.RowId.ToString(CultureInfo.InvariantCulture));

                    row.Cells[col].Value = value;
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

            bool confirm = MessageWindow.Confirm(this, "确定要删除选中的 " + rowIds.Count.ToString(CultureInfo.InvariantCulture) + " 行吗？\n该行上的标签也会一并删除。",
                "删除行",
                Dialogs.MessageTone.Danger, Dialogs.MessageButtons.OkCancel);

            if (!confirm)
            {
                return;
            }

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

                    if (groupTagIds.TryGetValue(GroupKey(c.Id, row.Cells[i].Value), out HashSet<long> viaGroup))
                    {
                        hit.UnionWith(viaGroup);
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
                    RemoveTagFromCell(column, row, captured);
                };

                MenuRemoveTag.Items.Add(item);
            }
        }

        /// <summary>把某个标签从当前单元格对应的归属对象上移除（标签本身保留）。</summary>
        private void RemoveTagFromCell(AppColumn column, RowVm row, Tag tag)
        {
            string value = row.Cells[column.Ordinal].Value ?? string.Empty;

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
                        string.Equals(l.GroupValue, value, StringComparison.Ordinal))
                    {
                        Data.Schema.UnlinkTag(l.Id);
                    }
                }
                else
                {
                    if (l.Scope == TagScope.Row && l.RowId == row.RowId)
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
    }
}
