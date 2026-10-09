using System.Collections.Generic;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DataManager.Dialogs
{
    /// <summary>消息窗口的按钮组合。</summary>
    public enum MessageButtons
    {
        /// <summary>只有一个确定。</summary>
        Ok,

        /// <summary>确定 / 取消。</summary>
        OkCancel,

        /// <summary>是 / 否。</summary>
        YesNo,
    }

    /// <summary>“有未保存改动”时用户的选择。</summary>
    public enum SavePromptResult
    {
        /// <summary>取消关闭，回到设置窗口。</summary>
        Cancel,

        /// <summary>保存后关闭。</summary>
        Save,

        /// <summary>不保存，直接丢弃并关闭。</summary>
        Discard,
    }

    /// <summary>消息的语气，决定图标与主按钮样式。</summary>
    public enum MessageTone
    {
        Info,
        Warning,
        Danger,
    }

    /// <summary>
    /// 自绘的消息 / 确认窗口。
    /// 用自绘窗口代替系统 MessageBox 的原因：系统 MessageBox 会播放系统提示音，
    /// 且样式与软件其余部分不一致。
    /// </summary>
    public partial class MessageWindow : Window
    {
        private MessageWindow()
        {
            InitializeComponent();
        }

        /// <summary>用户点了“确定”或“是”返回 true。</summary>
        public bool Accepted { get; private set; }

        private void Configure(string message, string title, MessageButtons buttons, MessageTone tone)
        {
            Title = title ?? "提示";
            TxtMessage.Text = message ?? string.Empty;

            switch (tone)
            {
                case MessageTone.Danger:
                    TxtIcon.Text = "\uE7BA";     // 叉
                    TxtIcon.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
                    break;

                case MessageTone.Warning:
                    TxtIcon.Text = "\uE7BA";
                    TxtIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0x77, 0x00));
                    break;

                default:
                    TxtIcon.Text = "\uE946";     // 信息
                    TxtIcon.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
                    break;
            }

            // 图标用系统符号字体，避免依赖特定中文字体
            TxtIcon.FontFamily = new FontFamily("Segoe MDL2 Assets");

            Button primary;
            switch (buttons)
            {
                case MessageButtons.OkCancel:
                    primary = MakeButton("确定", true);
                    Buttons.Children.Add(primary);
                    Buttons.Children.Add(MakeButton("取消", false));
                    break;

                case MessageButtons.YesNo:
                    primary = MakeButton("是", true);
                    Buttons.Children.Add(primary);
                    Buttons.Children.Add(MakeButton("否", false));
                    break;

                default:
                    primary = MakeButton("确定", true);
                    Buttons.Children.Add(primary);
                    break;
            }

            // 危险的确认默认聚焦在“取消 / 否”，避免顺手回车误删
            Button safe = null;
            foreach (UIElement child in Buttons.Children)
            {
                if (child is Button b && !ReferenceEquals(b, primary))
                {
                    safe = b;
                }
            }

            if (tone == MessageTone.Danger && safe != null)
            {
                safe.IsDefault = true;
                primary.IsDefault = false;
            }
            else
            {
                primary.IsDefault = true;
            }

            if (safe != null)
            {
                safe.IsCancel = true;
            }
        }

        internal Button MakeButton(string text, bool primary)        {
            var button = new Button
            {
                Content = text,
                MinWidth = 82,
                Margin = new Thickness(8, 0, 0, 0),
                Style = primary
                    ? (Style)Application.Current.Resources["PrimaryButton"]
                    : (Style)Application.Current.Resources["BaseButton"],
            };

            button.Click += (s, e) =>
            {
                Accepted = primary;
                this.CloseWithResult(primary);
            };

            return button;
        }

        /// <summary>弹出一个提示（只有一个确定）。</summary>
        public static void Show(Window owner, string message, string title, MessageTone tone = MessageTone.Info)
        {
            if (DialogService.Suppressed)
            {
                return;
            }

            var window = new MessageWindow { Owner = owner };
            window.Configure(message, title, MessageButtons.Ok, tone);
            window.ShowDialog();
        }

        /// <summary>弹出一个确认框，返回用户是否确认。</summary>
        public static bool Confirm(
            Window owner,
            string message,
            string title,
            MessageTone tone = MessageTone.Warning,
            MessageButtons buttons = MessageButtons.OkCancel)
        {
            if (DialogService.Suppressed)
            {
                // 测试模式下危险操作一律视为“不确认”，避免误删
                return false;
            }

            var window = new MessageWindow { Owner = owner };
            window.Configure(message, title, buttons, tone);
            window.ShowDialog();
            return window.Accepted;
        }

        /// <summary>“保存 / 不保存 / 取消”三选一。</summary>
        public static SavePromptResult PromptSave(Window owner, string message, string title)
        {
            if (DialogService.Suppressed)
            {
                // 测试模式下不要卡在弹窗上：按“不保存”处理
                return SavePromptResult.Discard;
            }

            var window = new MessageWindow { Owner = owner };
            window.Title = title ?? "未保存的改动";
            window.TxtMessage.Text = message ?? string.Empty;

            window.TxtIcon.Text = "\uE7BA";
            window.TxtIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0x77, 0x00));
            window.TxtIcon.FontFamily = new FontFamily("Segoe MDL2 Assets");

            SavePromptResult choice = SavePromptResult.Cancel;

            Button save = window.MakeButton("保存", true);
            save.Click += (s, e) =>
            {
                choice = SavePromptResult.Save;
                window.CloseWithResult(true);
            };

            Button discard = window.MakeButton("不保存", false);
            discard.Click += (s, e) =>
            {
                choice = SavePromptResult.Discard;
                window.CloseWithResult(false);
            };

            Button cancel = window.MakeButton("取消", false);
            cancel.IsCancel = true;
            cancel.Click += (s, e) =>
            {
                choice = SavePromptResult.Cancel;
                window.CloseWithResult(false);
            };

            window.Buttons.Children.Add(save);
            window.Buttons.Children.Add(discard);
            window.Buttons.Children.Add(cancel);

            window.ShowDialog();
            return choice;
        }
    }

    /// <summary>全局对话框开关。</summary>
    public static class DialogService
    {
        /// <summary>
        /// 为 true 时所有自绘弹窗直接返回而不显示。
        /// 界面自动化测试会打开它，避免测试卡在等待人工点击上。
        /// </summary>
        public static bool Suppressed { get; set; }
    }
}
