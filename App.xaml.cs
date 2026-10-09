using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DataManager
{
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = @"Local\DataManager.SingleInstance.9F2A1C";

        private Mutex _instanceMutex;
        private EventWaitHandle _activateSignal;
        private RegisteredWaitHandle _activateRegistration;

        /// <summary>全局数据入口。</summary>
        public static AppData Data { get; internal set; }

        /// <summary>本地配置。</summary>
        public static Services.AppSettings Settings { get; set; }

        /// <summary>是否需要重启软件（恢复备份后）。</summary>
        public static bool RestartRequested { get; set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // 单实例：重复启动时激活已有窗口
            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isFirstInstance);

            if (!isFirstInstance)
            {
                try
                {
                    _activateSignal = EventWaitHandle.OpenExisting(ActivateEventName());
                    _activateSignal.Set();
                }
                catch (Exception)
                {
                    // 找不到激活事件时直接退出即可
                }

                Shutdown();
                return;
            }

            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName());
            _activateRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activateSignal,
                (state, timedOut) => Dispatcher.BeginInvoke(new Action(ActivateMainWindow)),
                null,
                Timeout.Infinite,
                false);

            DispatcherUnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) => ReportFatal(args.ExceptionObject as Exception);

            base.OnStartup(e);

            try
            {
                Settings = Services.SettingsService.Load();
                Data = new AppData();
            }
            catch (Exception ex)
            {
                Dialogs.MessageWindow.Show(
                    null,
                    "初始化失败：\n\n" + ex.Message,
                    "轻量数据管理工具",
                    Dialogs.MessageTone.Danger);
                Shutdown(1);
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private static string ActivateEventName()
        {
            return SingleInstanceMutexName + ".Activate";
        }

        private void ActivateMainWindow()
        {
            Window w = MainWindow;
            if (w == null)
            {
                return;
            }

            if (w.WindowState == WindowState.Minimized)
            {
                w.WindowState = WindowState.Normal;
            }

            w.Activate();
            w.Topmost = true;
            w.Topmost = false;
            w.Focus();
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            Dialogs.MessageWindow.Show(
                MainWindow,
                "发生了未处理的错误：\n\n" + e.Exception.Message,
                "轻量数据管理工具",
                Dialogs.MessageTone.Danger);
        }

        private static void ReportFatal(Exception ex)
        {
            if (ex == null)
            {
                return;
            }

            Dialogs.MessageWindow.Show(
                null,
                "发生了致命错误：\n\n" + ex.Message,
                "轻量数据管理工具",
                Dialogs.MessageTone.Danger);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_activateRegistration != null)
            {
                _activateRegistration.Unregister(null);
            }

            try
            {
                Data?.CloseDatabase();
                Data?.Dispose();
            }
            catch (Exception)
            {
                // 退出阶段忽略清理错误
            }

            _activateSignal?.Dispose();
            _instanceMutex?.Dispose();

            base.OnExit(e);
        }
    }
}
