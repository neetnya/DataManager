using System;
using System.Windows;

namespace DataManager
{
    /// <summary>窗口辅助方法。</summary>
    public static class WindowHelper
    {
        /// <summary>
        /// 关闭窗口并把结果回传给调用方。
        /// DialogResult 只能在通过 ShowDialog 显示的窗口上设置，
        /// 用 Show 显示时会抛 InvalidOperationException，这里统一兜底。
        /// </summary>
        public static void CloseWithResult(this Window window, bool result)
        {
            if (window == null)
            {
                return;
            }

            try
            {
                window.DialogResult = result;
            }
            catch (InvalidOperationException)
            {
                window.Close();
            }
        }
    }
}
