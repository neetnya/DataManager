using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DataManager.Models;
using DataManager.Services;
using DataManager.Dialogs;

namespace DataManager
{
    /// <summary>新增 / 编辑一行的窗口。</summary>
    public partial class RowEditorWindow : Window
    {
        private readonly AppTable _table;
        private readonly DataRow _existing;
        private readonly AppData _data;
        private readonly List<Control> _inputs = new List<Control>();

        public RowEditorWindow(AppTable table, DataRow existing, AppData data)
        {
            InitializeComponent();

            _table = table;
            _existing = existing;
            _data = data;

            if (existing != null)
            {
                Title = "编辑数据";
                BtnContinueAdd.Visibility = Visibility.Collapsed;
            }

            BuildFields();
        }

        private void BuildFields()
        {
            for (int i = 0; i < _table.Columns.Count; i++)
            {
                AppColumn column = _table.Columns[i];
                string current = _existing != null ? _existing.Get(i) : string.Empty;

                var label = new TextBlock
                {
                    Text = BuildLabel(column),
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextMutedBrush"],
                    Margin = new Thickness(0, _inputs.Count == 0 ? 0 : 12, 0, 5),
                };

                Fields.Children.Add(label);

                Control input;
                if (column.IsGroup)
                {
                    var combo = new ComboBox
                    {
                        IsEditable = true,
                        Text = current,
                        MinHeight = 30,
                        VerticalContentAlignment = VerticalAlignment.Center,
                    };

                    foreach (string candidate in _data.Rows.DistinctValues(_table, column))
                    {
                        combo.Items.Add(candidate);
                    }

                    input = combo;
                }
                else
                {
                    input = new TextBox
                    {
                        Text = current,
                        MinHeight = 30,
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = false,
                    };
                }

                input.Tag = column;
                _inputs.Add(input);
                Fields.Children.Add(input);
            }

            if (_table.Columns.Count > 0)
            {
                _inputs[0].Focus();
            }
        }

        private static string BuildLabel(AppColumn column)
        {
            var marks = new List<string>();
            if (column.IsQuick)
            {
                marks.Add("快捷查询");
            }

            if (column.IsQueried)
            {
                marks.Add("被查询");
            }

            if (column.IsUnique)
            {
                marks.Add("唯一");
            }

            if (column.IsGroup)
            {
                marks.Add("分组");
            }

            string suffix = marks.Count > 0 ? "（" + string.Join("、", marks) + "）" : string.Empty;
            return column.DisplayName + suffix;
        }

        private string[] CollectValues()
        {
            var values = new string[_table.Columns.Count];
            for (int i = 0; i < _inputs.Count; i++)
            {
                values[i] = _inputs[i] switch
                {
                    ComboBox combo => (combo.Text ?? string.Empty).Trim(),
                    TextBox box => (box.Text ?? string.Empty).Trim(),
                    _ => string.Empty,
                };
            }

            return values;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (TrySave())
            {
                this.CloseWithResult(true);
            }
        }

        /// <summary>保存成功后立即插入数据、不关闭窗口，且不清空表单，方便连续录入相近的多条。</summary>
        private void OnContinueAdd(object sender, RoutedEventArgs e)
        {
            TrySave();
        }

        /// <summary>把当前表单内容写入数据表；成功返回 true。</summary>
        private bool TrySave()
        {
            string[] values = CollectValues();

            string error = _data.Rows.ValidateRow(_table, values, _existing?.RowId);
            if (error != null)
            {
                MessageWindow.Show(this, error, "无法保存", Dialogs.MessageTone.Warning);
                return false;
            }

            try
            {
                if (_existing == null)
                {
                    _data.Rows.InsertRow(_table, values);
                }
                else
                {
                    string updateError = _data.Rows.UpdateRow(_table, _existing.RowId, values);
                    if (updateError != null)
                    {
                        MessageWindow.Show(this, updateError, "无法保存", Dialogs.MessageTone.Warning);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "保存失败：\n\n" + ex.Message, "无法保存", Dialogs.MessageTone.Danger);
                return false;
            }

            return true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            this.CloseWithResult(false);
        }
    }
}
