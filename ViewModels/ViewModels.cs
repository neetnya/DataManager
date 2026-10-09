using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using DataManager.Models;

namespace DataManager.ViewModels
{
    /// <summary>可绑定通知的基类。</summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>一个单元格的视图模型。</summary>
    public sealed class CellVm : ObservableObject
    {
        private string _value;

        public CellVm(int ordinal, string value, bool isGroup, bool isUnique)
        {
            Ordinal = ordinal;
            _value = value ?? string.Empty;
            IsGroup = isGroup;
            IsUnique = isUnique;
        }

        public int Ordinal { get; }

        public bool IsGroup { get; }

        public bool IsUnique { get; }

        /// <summary>分组列的候选值（去重后），供编辑时下拉选择。</summary>
        public List<string> GroupCandidates { get; set; } = new List<string>();

        public string Value
        {
            get => _value;
            set
            {
                string v = value ?? string.Empty;
                if (!string.Equals(_value, v, StringComparison.Ordinal))
                {
                    _value = v;
                    Raise(nameof(Value));
                }
            }
        }

        /// <summary>挂在“该分组值”上的标签（仅分组列会填充）。</summary>
        public ObservableCollection<Tag> GroupTags { get; } = new ObservableCollection<Tag>();

        public bool HasGroupTags => GroupTags.Count > 0;

        public void RefreshGroupTags(IEnumerable<Tag> tags)
        {
            GroupTags.Clear();
            foreach (Tag t in tags)
            {
                GroupTags.Add(t);
            }

            Raise(nameof(HasGroupTags));
            Raise(nameof(GroupTagText));
        }

        /// <summary>用于在单元格里紧凑展示的标签文本。</summary>
        public string GroupTagText
        {
            get
            {
                if (GroupTags.Count == 0)
                {
                    return string.Empty;
                }

                var parts = new List<string>(GroupTags.Count);
                foreach (Tag t in GroupTags)
                {
                    parts.Add(t.Name);
                }

                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>一行的视图模型。</summary>
    public sealed class RowVm : ObservableObject
    {
        private int _displayIndex;

        public RowVm(long rowId, int columnCount)
        {
            RowId = rowId;
            Cells = new CellVm[columnCount];
            RowTags = new ObservableCollection<Tag>();
        }

        /// <summary>表格里显示的行号（从 1 开始，仅用于展示）。</summary>
        public int DisplayIndex
        {
            get => _displayIndex;
            set
            {
                if (_displayIndex != value)
                {
                    _displayIndex = value;
                    Raise(nameof(DisplayIndex));
                }
            }
        }

        public long RowId { get; }

        public CellVm[] Cells { get; }

        /// <summary>挂在“整行”上的标签。</summary>
        public ObservableCollection<Tag> RowTags { get; }

        public bool HasRowTags => RowTags.Count > 0;

        public void RefreshRowTags(IEnumerable<Tag> tags)
        {
            RowTags.Clear();
            foreach (Tag t in tags)
            {
                RowTags.Add(t);
            }

            Raise(nameof(HasRowTags));
            Raise(nameof(RowTagText));
        }

        public string RowTagText
        {
            get
            {
                if (RowTags.Count == 0)
                {
                    return string.Empty;
                }

                var parts = new List<string>(RowTags.Count);
                foreach (Tag t in RowTags)
                {
                    parts.Add(t.Name);
                }

                return string.Join(" · ", parts);
            }
        }
    }
}
