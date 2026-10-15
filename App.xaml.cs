using System;
using System.Diagnostics;
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

            // 打开软件时自动做一次本地备份；失败不阻断启动
            try
            {
                Services.BackupService.CreateLocalBackup(Data.Connection, "startup");
            }
            catch (Exception)
            {
                // 启动备份失败不影响正常使用
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

            void Show()
            {
                Dialogs.MessageWindow.Show(
                    Current?.MainWindow,
                    "发生了致命错误：\n\n" + ex.Message,
                    "轻量数据管理工具",
                    Dialogs.MessageTone.Danger);
            }

            Dispatcher dispatcher = Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                Show();
                return;
            }

            try
            {
                dispatcher.Invoke(Show);
            }
            catch (Exception)
            {
                // 调度失败时不再二次抛，避免盖住原始致命错误
            }
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

            // 先释放单实例互斥，再拉起新进程，否则新实例会以为旧的还在
            if (RestartRequested)
            {
                TryRelaunch();
            }

            base.OnExit(e);
        }

        private static void TryRelaunch()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    exe = System.IO.Path.Combine(AppContext.BaseDirectory, "DataManager.exe");
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
                // 拉起失败时用户按提示手动再开即可
            }
        }
    }
}
