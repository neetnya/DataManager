using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataManager.Models;
using DataManager.Services;
using DataManager.Dialogs;

namespace DataManager
{
    /// <summary>设置窗口里一行“列”的可编辑视图模型（改动先留在内存，点保存才写库）。</summary>
    public sealed class ColumnEditVm : INotifyPropertyChanged
    {
        private readonly SettingsWindow _owner;
        private int _displayOrdinal;

        public ColumnEditVm(SettingsWindow owner, AppColumn column, int displayOrdinal)
        {
            _owner = owner;
            Column = column;
            _displayOrdinal = displayOrdinal;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public AppColumn Column { get; }

        /// <summary>界面上展示的顺序（从 1 开始）。</summary>
        public int DisplayOrdinal
        {
            get => _displayOrdinal;
            set
            {
                if (_displayOrdinal != value)
                {
                    _displayOrdinal = value;
                    Raise(nameof(DisplayOrdinal));
                }
            }
        }

        public bool IsQuick
        {
            get => Column.IsQuick;
            set
            {
                if (value)
                {
                    // 一张表只允许一个快捷查询列
                    _owner.ClearFlag(ColumnFlags.Quick, this);
                }

                SetFlag(ColumnFlags.Quick, value);
            }
        }

        public bool IsQueried
        {
            get => Column.IsQueried;
            set
            {
                if (value)
                {
                    _owner.ClearFlag(ColumnFlags.Queried, this);
                }

                SetFlag(ColumnFlags.Queried, value);
            }
        }

        public bool IsUnique
        {
            get => Column.IsUnique;
            set => SetFlag(ColumnFlags.Unique, value);
        }

        public bool IsGroup
        {
            get => Column.IsGroup;
            set => SetFlag(ColumnFlags.Group, value);
        }

        public bool IsSort
        {
            get => Column.IsSort;
            set
            {
                SetFlag(ColumnFlags.Sort, value);
                _owner.NormalizeSortPriorities();
                Raise(nameof(SortDirectionIndex));
            }
        }

        public bool IsUrl
        {
            get => Column.IsUrl;
            set => SetFlag(ColumnFlags.Url, value);
        }

        public int SortDirectionIndex
        {
            get => Column.SortDescending ? 1 : 0;
            set
            {
                bool desc = value == 1;
                if (Column.SortDescending != desc)
                {
                    Column.SortDescending = desc;
                    Raise(nameof(SortDirectionIndex));
                    _owner.MarkDirty();
                }
            }
        }

        /// <summary>排序优先级（文本形式，便于手工输入）。</summary>
        public string SortPriorityText
        {
            get => Column.SortPriority.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                {
                    if (Column.SortPriority != v)
                    {
                        Column.SortPriority = v;
                        Raise(nameof(SortPriorityText));
                        _owner.MarkDirty();
                    }
                }
                else
                {
                    Raise(nameof(SortPriorityText));
                }
            }
        }

        private void SetFlag(ColumnFlags flag, bool on)
        {
            ColumnFlags before = Column.Flags;
            if (on)
            {
                Column.Flags |= flag;
            }
            else
            {
                Column.Flags &= ~flag;
            }

            if (before != Column.Flags)
            {
                RaiseAll();
                _owner.MarkDirty();
            }
        }

        /// <summary>由设置窗口调用：静默清除某个标记（单选互斥用）。</summary>
        public void ClearFlagSilently(ColumnFlags flag)
        {
            if ((Column.Flags & flag) == 0)
            {
                return;
            }

            Column.Flags &= ~flag;
            RaiseAll();
        }

        /// <summary>排序优先级被外部改动后刷新界面。</summary>
        public void RaisePriorityChanged()
        {
            Raise(nameof(SortPriorityText));
        }

        private void RaiseAll()
        {
            Raise(nameof(IsQuick));
            Raise(nameof(IsQueried));
            Raise(nameof(IsUnique));
            Raise(nameof(IsGroup));
            Raise(nameof(IsSort));
            Raise(nameof(IsUrl));
        }

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 设置窗口：创建/删除表、增删列、配置列属性。
    /// 改动先落到内存工作副本，点【保存】才写库；关闭时未保存会提示。
    /// 结构改动（建表/加列/删列）在打开期间处于一个事务里，取消即回滚。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly ObservableCollection<ColumnEditVm> _columnRows = new ObservableCollection<ColumnEditVm>();
        private readonly List<AppTable> _work = new List<AppTable>();
        private readonly Dictionary<long, List<AppColumn>> _columnCache = new Dictionary<long, List<AppColumn>>();

        private long? _currentTableId;
        private bool _dirty;
        private bool _committed;
        private bool _suppressSelection;
        private bool _transactionOpen;
        private readonly AppSettings _appearanceSnapshot;

        /// <summary>是否真的保存过（供主窗口判断是否需要刷新）。</summary>
        public bool SettingsChanged { get; private set; }

        public SettingsWindow()
        {
            InitializeComponent();

            ColumnGrid.ItemsSource = _columnRows;

            // 打开期间的结构改动放进一个事务：保存 = COMMIT，取消 = ROLLBACK
            App.Data.Connection.BeginTransaction();
            _transactionOpen = true;

            LoadWorkCopy();

            // 外观/分组分隔符会即时写进 App.Settings；取消时按打开窗口时的快照回滚
            _appearanceSnapshot = App.Settings.Clone();

            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private AppData Data => App.Data;

        private AppTable CurrentTable =>
            _currentTableId.HasValue ? _work.FirstOrDefault(t => t.Id == _currentTableId.Value) : null;

        /// <summary>把数据库里的表结构复制一份到内存工作副本。</summary>
        private void LoadWorkCopy()
        {
            Data.ReloadTables();
            _work.Clear();
            _columnCache.Clear();

            foreach (AppTable t in Data.Tables)
            {
                var copy = new AppTable { Id = t.Id, Name = t.Name };
                foreach (AppColumn c in t.Columns.OrderBy(x => x.Ordinal).ThenBy(x => x.Id))
                {
                    copy.Columns.Add(c.Clone());
                }

                _work.Add(copy);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            BuildAppearanceControls();
            TxtGroupSeparator.Text = App.Settings.GroupSeparator ?? string.Empty;
            RefreshTableList();
            if (TableList.Items.Count > 0)
            {
                TableList.SelectedIndex = 0;
            }
            else
            {
                _currentTableId = null;
                LoadColumns();
            }
        }

        // =================================================================
        // 外观设置
        // =================================================================

        private bool _appearanceReady;
        private bool _suppressAppearanceEvents;

        private void BuildAppearanceControls()
        {
            _suppressAppearanceEvents = true;

            // 常见中文字体排在前面，其余列出系统已安装的字体
            var families = new List<string>
            {
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "SimSun",
                "SimHei",
                "KaiTi",
                "FangSong",
                "DengXian",
                "Consolas",
                "Segoe UI",
            };

            foreach (FontFamily f in Fonts.SystemFontFamilies)
            {
                string name = f.Source;
                if (!string.IsNullOrWhiteSpace(name) && !families.Contains(name))
                {
                    families.Add(name);
                }
            }

            foreach (string name in families)
            {
                CmbFontFamily.Items.Add(name);
            }

            foreach (double size in new[] { 11.0, 12.0, 12.5, 13.0, 14.0, 15.0, 16.0, 18.0, 20.0 })
            {
                CmbFontSize.Items.Add(size.ToString("0.#", CultureInfo.InvariantCulture));
            }

            foreach (double h in new[] { 24.0, 26.0, 28.0, 30.0, 32.0, 36.0, 40.0, 46.0, 52.0 })
            {
                CmbRowHeight.Items.Add(h.ToString("0.#", CultureInfo.InvariantCulture));
            }

            AppSettings s = App.Settings;

            CmbFontFamily.SelectedItem = families.Contains(s.GridFontFamily) ? s.GridFontFamily : "Microsoft YaHei UI";

            string sizeText = s.GridFontSize.ToString("0.#", CultureInfo.InvariantCulture);
            if (!CmbFontSize.Items.Contains(sizeText))
            {
                CmbFontSize.Items.Add(sizeText);
            }

            CmbFontSize.SelectedItem = sizeText;

            string heightText = s.GridRowHeight.ToString("0.#", CultureInfo.InvariantCulture);
            if (!CmbRowHeight.Items.Contains(heightText))
            {
                CmbRowHeight.Items.Add(heightText);
            }

            CmbRowHeight.SelectedItem = heightText;

            ChkBold.IsChecked = s.GridFontBold;
            ChkItalic.IsChecked = s.GridFontItalic;
            ChkShowLines.IsChecked = s.GridShowLines;

            _suppressAppearanceEvents = false;
            _appearanceReady = true;

            UpdatePreview();
        }

        private void OnAppearanceChanged(object sender, RoutedEventArgs e)
        {
            ApplyAppearanceFromUi();
        }

        /// <summary>分组分隔符改动：立即写回内存配置并标记未保存。</summary>
        private void OnGroupSeparatorChanged(object sender, TextChangedEventArgs e)
        {
            App.Settings.GroupSeparator = TxtGroupSeparator.Text ?? string.Empty;
            MarkDirty();
        }

        private void ApplyAppearanceFromUi()
        {
            if (!_appearanceReady || _suppressAppearanceEvents)
            {
                return;
            }

            AppSettings s = App.Settings;

            if (CmbFontFamily.SelectedItem is string family && family.Length > 0)
            {
                s.GridFontFamily = family;
            }

            if (CmbFontSize.SelectedItem is string sizeText &&
                double.TryParse(sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double size) &&
                size > 0)
            {
                s.GridFontSize = size;
            }

            if (CmbRowHeight.SelectedItem is string heightText &&
                double.TryParse(heightText, NumberStyles.Float, CultureInfo.InvariantCulture, out double height) &&
                height > 0)
            {
                s.GridRowHeight = height;
            }

            s.GridFontBold = ChkBold.IsChecked == true;
            s.GridFontItalic = ChkItalic.IsChecked == true;
            s.GridShowLines = ChkShowLines.IsChecked == true;

            MarkDirty();
            UpdatePreview();

            // 立即应用到主窗口
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>外观改动时通知主窗口实时刷新。</summary>
        public event EventHandler AppearanceChanged;

        /// <summary>用几行示例数据把当前字体样式画出来。</summary>
        private void UpdatePreview()
        {
            PreviewArea.Children.Clear();

            var family = string.IsNullOrWhiteSpace(App.Settings.GridFontFamily)
                ? (Application.Current.Resources["UiFont"] as FontFamily)
                : new FontFamily(App.Settings.GridFontFamily);

            FontWeight weight = App.Settings.GridFontBold ? FontWeights.SemiBold : FontWeights.Normal;
            FontStyle style = App.Settings.GridFontItalic ? FontStyles.Italic : FontStyles.Normal;
            double size = App.Settings.GridFontSize > 0 ? App.Settings.GridFontSize : 13;
            double rowHeight = App.Settings.GridRowHeight > 0 ? App.Settings.GridRowHeight : 30;

            var samples = new[]
            {
                new[] { "示例文本 Sample 123", "值一" },
                new[] { "中文与English混排", "值二" },
            };

            for (int r = 0; r < samples.Length; r++)
            {
                var row = new Grid { Height = rowHeight };

                for (int c = 0; c < samples[r].Length; c++)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star),
                    });

                    var cell = new Border
                    {
                        BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                        BorderThickness = App.Settings.GridShowLines
                            ? new Thickness(0, r == 0 ? 0 : 1, c == 0 ? 0 : 1, 0)
                            : new Thickness(0),
                        Padding = new Thickness(8, 0, 8, 0),
                        Child = new TextBlock
                        {
                            Text = samples[r][c],
                            FontFamily = family,
                            FontSize = size,
                            FontWeight = weight,
                            FontStyle = style,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    };

                    Grid.SetColumn(cell, c);
                    row.Children.Add(cell);
                }

                PreviewArea.Children.Add(row);
            }
        }

        private void OnResetAppearance(object sender, RoutedEventArgs e)
        {
            var defaults = new AppSettings();

            _suppressAppearanceEvents = true;
            CmbFontFamily.SelectedItem = defaults.GridFontFamily;
            CmbFontSize.SelectedItem = defaults.GridFontSize.ToString("0.#", CultureInfo.InvariantCulture);
            CmbRowHeight.SelectedItem = defaults.GridRowHeight.ToString("0.#", CultureInfo.InvariantCulture);
            ChkBold.IsChecked = defaults.GridFontBold;
            ChkItalic.IsChecked = defaults.GridFontItalic;
            ChkShowLines.IsChecked = defaults.GridShowLines;
            _suppressAppearanceEvents = false;

            ApplyAppearanceFromUi();
        }

        // =================================================================
        // 数据（导入 / 导出 / 备份恢复）
        // =================================================================

        private void OnImportCsv(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要导入的 CSV 文件",
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                string tableName = Data.Csv.ImportAsNewTable(dialog.FileName, new List<AppTable>(Data.Tables));

                Data.ReloadTables();
                LoadWorkCopy();
                MarkDirty();
                RefreshTableList();

                // 选中刚导入的表
                long createdId = 0;
                foreach (AppTable t in _work)
                {
                    if (string.Equals(t.Name, tableName, StringComparison.Ordinal))
                    {
                        createdId = t.Id;
                        break;
                    }
                }

                if (createdId != 0)
                {
                    _currentTableId = createdId;
                    _suppressSelection = true;
                    foreach (ListBoxItem item in TableList.Items)
                    {
                        if ((long)item.Tag == createdId)
                        {
                            TableList.SelectedItem = item;
                            break;
                        }
                    }

                    _suppressSelection = false;
                    LoadColumns();
                }
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "导入失败：\n\n" + ex.Message, "导入 CSV", MessageTone.Danger);
            }
        }

        private void OnExportCsv(object sender, RoutedEventArgs e)
        {
            if (Data.Tables.Count == 0)
            {
                return;
            }

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择导出到的文件夹",
                Multiselect = false,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string directory = dialog.FolderName;
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            try
            {
                List<string> written = Data.Csv.ExportAll(new List<AppTable>(Data.Tables), directory);
                MessageWindow.Show(
                    this,
                    "已导出 " + written.Count.ToString(CultureInfo.InvariantCulture) + " 个 CSV 到：\n" + directory,
                    "导出 CSV",
                    MessageTone.Info);
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "导出失败：\n\n" + ex.Message, "导出 CSV", MessageTone.Danger);
            }
        }

        private void OnOpenBackup(object sender, RoutedEventArgs e)
        {
            // 备份窗口会直接读写数据库，先把设置窗口的改动落库
            if (_dirty && !TrySave())
            {
                return;
            }

            var window = new BackupWindow { Owner = this };
            window.ShowDialog();

            // 恢复备份后数据库已被替换，连接必须重开
            if (App.RestartRequested)
            {
                Application.Current.Shutdown();
            }
            else
            {
                Data.ReloadTables();
                LoadWorkCopy();
                RefreshTableList();
            }
        }

        // =================================================================
        // 表列表
        // =================================================================

        private void RefreshTableList()
        {
            _suppressSelection = true;
            TableList.Items.Clear();

            foreach (AppTable t in _work)
            {
                TableList.Items.Add(new ListBoxItem
                {
                    Content = t.Name,
                    Tag = t.Id,
                    ToolTip = "列数：" + t.Columns.Count.ToString(CultureInfo.InvariantCulture),
                });
            }

            if (_currentTableId.HasValue)
            {
                foreach (ListBoxItem item in TableList.Items)
                {
                    if ((long)item.Tag == _currentTableId.Value)
                    {
                        TableList.SelectedItem = item;
                        break;
                    }
                }
            }

            _suppressSelection = false;
        }

        /// <summary>把界面上当前表的列改动写回工作副本。</summary>
        private void FlushCurrentColumns()
        {
            AppTable table = CurrentTable;
            if (table == null)
            {
                return;
            }

            ColumnGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ColumnGrid.CommitEdit(DataGridEditingUnit.Row, true);

            var ordered = new List<AppColumn>(_columnRows.Count);
            foreach (ColumnEditVm vm in _columnRows)
            {
                ordered.Add(vm.Column);
            }

            _columnCache[table.Id] = ordered;
        }

        private void OnTableSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection)
            {
                return;
            }

            FlushCurrentColumns();

            if (!(TableList.SelectedItem is ListBoxItem item))
            {
                _currentTableId = null;
                LoadColumns();
                return;
            }

            _currentTableId = (long)item.Tag;
            LoadColumns();
        }

        private void LoadColumns()
        {
            _columnRows.Clear();
            AppTable table = CurrentTable;

            if (table == null)
            {
                UpdateColumnArea();
                return;
            }

            // 优先使用缓存（可能包含尚未落库的新增列），否则用表自身的列
            List<AppColumn> source = _columnCache.TryGetValue(table.Id, out List<AppColumn> cached)
                ? cached
                : table.Columns;

            int ordinal = 1;
            foreach (AppColumn c in source.OrderBy(x => x.Ordinal).ThenBy(x => x.Id))
            {
                _columnRows.Add(new ColumnEditVm(this, c, ordinal++));
            }

            UpdateColumnArea();
        }

        private void UpdateColumnArea()
        {
            AppTable table = CurrentTable;
            bool has = table != null;
            ColumnGrid.IsEnabled = has;
            TxtColumnCount.Text = has
                ? table.Name + " · 共 " + _columnRows.Count.ToString(CultureInfo.InvariantCulture) + " 列"
                : string.Empty;
        }

        // =================================================================
        // 表操作
        // =================================================================

        private void OnNewTable(object sender, RoutedEventArgs e)
        {
            string name = InputDialog.Show(this, "新建表", "请输入表名（表名允许重复）：", "新表");
            if (name == null)
            {
                return;
            }

            name = name.Trim();
            if (name.Length == 0)
            {
                MessageWindow.Show(this, "表名不能为空。", "新建表", Dialogs.MessageTone.Info);
                return;
            }

            try
            {
                FlushCurrentColumns();

                long id = Data.Schema.CreateTable(name);
                Data.ReloadTables();
                AppTable created = Data.FindTable(id);
                if (created == null)
                {
                    return;
                }

                var copy = new AppTable { Id = created.Id, Name = created.Name };
                _work.Add(copy);
                _columnCache[copy.Id] = new List<AppColumn>();

                _currentTableId = copy.Id;
                MarkDirty();
                RefreshTableList();
                LoadColumns();
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "新建表失败：\n\n" + ex.Message, "新建表", Dialogs.MessageTone.Danger);
            }
        }

        private void OnRenameTable(object sender, RoutedEventArgs e)
        {
            AppTable table = CurrentTable;
            if (table == null)
            {
                MessageWindow.Show(this, "请先选择一张表。", "重命名", Dialogs.MessageTone.Info);
                return;
            }

            string name = InputDialog.Show(this, "重命名表", "请输入新的表名：", table.Name);
            if (name == null)
            {
                return;
            }

            name = name.Trim();
            if (name.Length == 0)
            {
                MessageWindow.Show(this, "表名不能为空。", "重命名", Dialogs.MessageTone.Info);
                return;
            }

            try
            {
                Data.Schema.RenameTable(table.Id, name);
                table.Name = name;
                MarkDirty();
                RefreshTableList();
                UpdateColumnArea();
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "重命名失败：\n\n" + ex.Message, "重命名", Dialogs.MessageTone.Danger);
            }
        }

        private void OnDeleteTable(object sender, RoutedEventArgs e)
        {
            AppTable table = CurrentTable;
            if (table == null)
            {
                MessageWindow.Show(this, "请先选择一张表。", "删除表", Dialogs.MessageTone.Info);
                return;
            }

            long rowCount = Data.Schema.CountRows(table.Id);

            bool confirm = MessageWindow.Confirm(this, "确定要删除表「" + table.Name + "」吗？\n" +
                "该表有 " + rowCount.ToString(CultureInfo.InvariantCulture) + " 行数据。\n\n" +
                "删除后该表的所有数据与标签都会一并消失，且不可恢复。",
                "删除表",
                Dialogs.MessageTone.Danger, Dialogs.MessageButtons.OkCancel);

            if (!confirm)
            {
                return;
            }

            try
            {
                Data.Schema.DropTable(table.Id);
                _work.Remove(table);
                _columnCache.Remove(table.Id);
                _columnRows.Clear();
                _currentTableId = null;
                MarkDirty();
                RefreshTableList();

                if (TableList.Items.Count > 0)
                {
                    TableList.SelectedIndex = 0;
                }
                else
                {
                    LoadColumns();
                }
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "删除表失败：\n\n" + ex.Message, "删除表", Dialogs.MessageTone.Danger);
            }
        }

        // =================================================================
        // 列操作
        // =================================================================

        private void OnAddColumn(object sender, RoutedEventArgs e)
        {
            AppTable table = CurrentTable;
            if (table == null)
            {
                MessageWindow.Show(this, "请先创建或选择一张表。", "添加列", Dialogs.MessageTone.Info);
                return;
            }

            try
            {
                FlushCurrentColumns();

                // 立刻 ALTER TABLE 加真实列（在事务内，取消可回滚）
                AppColumn created = Data.Schema.AddColumn(table.Id, ColumnFlags.None, false);

                List<AppColumn> list = _columnCache.TryGetValue(table.Id, out List<AppColumn> cached)
                    ? cached
                    : new List<AppColumn>(table.Columns);

                list.Add(created);
                _columnCache[table.Id] = list;
                table.Columns.Add(created);

                _columnRows.Add(new ColumnEditVm(this, created, _columnRows.Count + 1));
                MarkDirty();
                UpdateColumnArea();
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "添加列失败：\n\n" + ex.Message, "添加列", Dialogs.MessageTone.Danger);
            }
        }

        private void OnDeleteColumn(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.DataContext is ColumnEditVm vm))
            {
                return;
            }

            AppTable table = CurrentTable;
            if (table == null)
            {
                return;
            }

            long nonEmpty = 0;
            try
            {
                nonEmpty = Data.Connection.ScalarLong(
                    "SELECT COUNT(*) FROM " + DataManager.Data.Sql.Ident(table.PhysicalName) +
                    " WHERE " + DataManager.Data.Sql.Ident(vm.Column.PhysicalName) + " IS NOT NULL AND " +
                    DataManager.Data.Sql.Ident(vm.Column.PhysicalName) + " <> ''");
            }
            catch (Exception)
            {
                // 统计失败不阻断删除
            }

            string extra = nonEmpty > 0
                ? "\n该列有 " + nonEmpty.ToString(CultureInfo.InvariantCulture) + " 个非空单元格，会一并删除。"
                : string.Empty;

            bool confirm = MessageWindow.Confirm(this, "确定要删除【" + vm.Column.DisplayName + "】吗？" + extra,
                "删除列",
                Dialogs.MessageTone.Danger, Dialogs.MessageButtons.OkCancel);

            if (!confirm)
            {
                return;
            }

            try
            {
                Data.Schema.DropColumn(vm.Column);

                table.Columns.Remove(vm.Column);
                if (_columnCache.TryGetValue(table.Id, out List<AppColumn> cached))
                {
                    cached.Remove(vm.Column);
                }

                _columnRows.Remove(vm);
                Renumber();
                MarkDirty();
                UpdateColumnArea();
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "删除列失败：\n\n" + ex.Message, "删除列", Dialogs.MessageTone.Danger);
            }
        }

        private void Renumber()
        {
            for (int i = 0; i < _columnRows.Count; i++)
            {
                _columnRows[i].DisplayOrdinal = i + 1;
            }
        }

        // =================================================================
        // 排序优先级
        // =================================================================

        private void OnSortUp(object sender, RoutedEventArgs e)
        {
            MoveSort(sender, -1);
        }

        private void OnSortDown(object sender, RoutedEventArgs e)
        {
            MoveSort(sender, +1);
        }

        private void MoveSort(object sender, int delta)
        {
            if (!(sender is Button b) || !(b.DataContext is ColumnEditVm vm))
            {
                return;
            }

            ColumnGrid.CommitEdit(DataGridEditingUnit.Cell, true);

            List<ColumnEditVm> sortable = SortableColumns().ToList();
            int index = sortable.IndexOf(vm);
            int target = index + delta;
            if (index < 0 || target < 0 || target >= sortable.Count)
            {
                return;
            }

            sortable.RemoveAt(index);
            sortable.Insert(target, vm);

            for (int i = 0; i < sortable.Count; i++)
            {
                sortable[i].Column.SortPriority = i;
                sortable[i].RaisePriorityChanged();
            }

            MarkDirty();
        }

        private IEnumerable<ColumnEditVm> SortableColumns()
        {
            return _columnRows
                .Where(x => x.IsSort)
                .OrderBy(x => x.Column.SortPriority)
                .ThenBy(x => x.Column.Ordinal);
        }

        /// <summary>让排序列的优先级保持 0,1,2... 连续。</summary>
        public void NormalizeSortPriorities()
        {
            List<ColumnEditVm> sortable = SortableColumns().ToList();
            for (int i = 0; i < sortable.Count; i++)
            {
                sortable[i].Column.SortPriority = i;
                sortable[i].RaisePriorityChanged();
            }
        }

        // =================================================================
        // 钩子（供 ColumnEditVm 调用）
        // =================================================================

        /// <summary>保证“每表最多一个”的单选互斥。</summary>
        public void ClearFlag(ColumnFlags flag, ColumnEditVm except)
        {
            foreach (ColumnEditVm vm in _columnRows)
            {
                if (ReferenceEquals(vm, except))
                {
                    continue;
                }

                if ((vm.Column.Flags & flag) != 0)
                {
                    vm.ClearFlagSilently(flag);
                }
            }
        }

        public void MarkDirty()
        {
            _dirty = true;
            TxtDirty.Visibility = Visibility.Visible;
        }

        // =================================================================
        // 保存 / 取消
        // =================================================================

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (!TrySave())
            {
                return;
            }

            CloseAsDialog(true);
        }

        /// <summary>
        /// 关闭窗口。以对话框方式显示时设置 DialogResult，否则直接关闭
        /// （DialogResult 只能在 ShowDialog 显示的窗口上设置）。
        /// </summary>
        private void CloseAsDialog(bool result)
        {
            this.CloseWithResult(result);
        }

        /// <summary>执行保存。成功返回 true。</summary>
        private bool TrySave()
        {
            FlushCurrentColumns();

            foreach (AppTable table in _work)
            {
                if (table.Id != _currentTableId)
                {
                    continue;
                }

                NormalizeSortPriorities();
            }

            // 校验每一张表的快捷查询/被查询是否成对
            foreach (AppTable table in _work)
            {
                List<AppColumn> cols = _columnCache.TryGetValue(table.Id, out List<AppColumn> cached)
                    ? cached
                    : table.Columns;

                int quick = cols.Count(x => x.IsQuick);
                int queried = cols.Count(x => x.IsQueried);

                if (quick > 1 || queried > 1)
                {
                    MessageWindow.Show(this, "表「" + table.Name + "」最多只能有一个【快捷查询】列和一个【被查询】列。",
                        "无法保存", Dialogs.MessageTone.Warning);
                    return false;
                }

                if (quick != queried)
                {
                    MessageWindow.Show(this, "表「" + table.Name + "」的【快捷查询】与【被查询】必须成对存在。\n\n" +
                        "当前：" + (quick > 0 ? "已设置快捷查询列" : "未设置快捷查询列") + "，" +
                        (queried > 0 ? "已设置被查询列" : "未设置被查询列") + "。\n" +
                        "请补齐另一个，或两个都不勾选。",
                        "无法保存", Dialogs.MessageTone.Warning);
                    return false;
                }
            }

            try
            {
                foreach (AppTable table in _work)
                {
                    List<AppColumn> cols = _columnCache.TryGetValue(table.Id, out List<AppColumn> cached)
                        ? cached
                        : table.Columns;

                    Data.Schema.UpdateColumns(table.Id, cols);
                }

                Data.Connection.Commit();
                _transactionOpen = false;
                _committed = true;
                SettingsChanged = true;
                Data.ReloadTables();
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "保存设置失败：\n\n" + ex.Message, "保存失败", Dialogs.MessageTone.Danger);
                return false;
            }

            try
            {
                SettingsService.Save(App.Settings);
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "表结构已保存，但写入 config.json 失败：\n\n" + ex.Message,
                    "保存设置", Dialogs.MessageTone.Warning);
            }

            return true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            CloseAsDialog(false);
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_committed)
            {
                return;
            }

            if (_dirty)
            {
                SavePromptResult result = MessageWindow.PromptSave(
                    this,
                    "设置有未保存的改动。\n\n要保存后再关闭吗？",
                    "未保存的改动");

                if (result == SavePromptResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (result == SavePromptResult.Save)
                {
                    if (!TrySave())
                    {
                        e.Cancel = true;
                        return;
                    }

                    return;
                }
            }

            // 取消或不保存：回滚事务
            Rollback();
        }

        private void Rollback()
        {
            if (!_transactionOpen)
            {
                return;
            }

            try
            {
                Data.Connection.Rollback();
                _transactionOpen = false;
                Data.ReloadTables();
            }
            catch (Exception)
            {
                // 回滚失败时忽略
            }

            App.Settings.CopyAppearanceFrom(_appearanceSnapshot);
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
