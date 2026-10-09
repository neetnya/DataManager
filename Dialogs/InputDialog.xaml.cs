using System.Windows;

namespace DataManager
{
    /// <summary>一个简单的单行文本输入框。</summary>
    public partial class InputDialog : Window
    {
        public InputDialog()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                TxtValue.Focus();
                TxtValue.SelectAll();
            };
        }

        /// <summary>用户输入的内容；取消时为 null。</summary>
        public string Value => TxtValue.Text;

        /// <summary>弹出输入框。取消返回 null。</summary>
        public static string Show(Window owner, string title, string prompt, string initial)
        {
            var dialog = new InputDialog
            {
                Owner = owner,
                Title = title,
            };

            dialog.TxtPrompt.Text = prompt;
            dialog.TxtValue.Text = initial ?? string.Empty;

            return dialog.ShowDialog() == true ? dialog.TxtValue.Text : null;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            this.CloseWithResult(true);
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            this.CloseWithResult(false);
        }
    }
}
