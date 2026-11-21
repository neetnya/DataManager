using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DataManager
{
    public enum AppExitAction { None, Shutdown, Restart }

    public partial class App : Application
    {
        private const string SingleInstanceMutexName = @"Local\DataManager.SingleInstance.9F2A1C";

        private Mutex _instanceMutex;
        private bool _ownsInstanceMutex;
        private EventWaitHandle _activateSignal;
        private RegisteredWaitHandle _activateRegistration;
        private int _handlingUnhandledException;

        /// <summary>全局数据入口。</summary>
        public static AppData Data { get; internal set; }

        /// <summary>本地配置。</summary>
        public static Services.AppSettings Settings { get; set; }

        /// <summary>退出行为（正常退出或重启）。</summary>
        public static AppExitAction ExitAction { get; set; } = AppExitAction.None;

        /// <summary>是否需要重启软件（恢复备份后）。</summary>
        public static bool RestartRequested
        {
            get => ExitAction == AppExitAction.Restart;
            set => ExitAction = value ? AppExitAction.Restart : (ExitAction == AppExitAction.Restart ? AppExitAction.None : ExitAction);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // 单实例：重复启动时激活已有窗口
            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isFirstInstance);
            _ownsInstanceMutex = isFirstInstance;

            if (!isFirstInstance)
            {
                // 首个实例可能刚刚拿到互斥锁、尚未创建激活事件，短暂重试
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        using (EventWaitHandle signal = EventWaitHandle.OpenExisting(ActivateEventName()))
                        {
                            signal.Set();
                        }

                        break;
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        if (attempt < 9)
                        {
                            Thread.Sleep(50);
                        }
                    }
                    catch (Exception)
                    {
                        break;
                    }
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
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Services.AppLog.Error("后台任务发生未观察的异常。", args.Exception);
                args.SetObserved();
            };

            base.OnStartup(e);

            try
            {
                Settings = Services.SettingsService.Load();
                Data = new AppData();
            }
            catch (Exception ex)
            {
                Services.AppLog.Error("应用初始化失败。", ex);
                Dialogs.MessageWindow.Show(
                    null,
                    "初始化失败：\n\n" + ex.Message,
                    "轻量数据管理工具",
                    Dialogs.MessageTone.Danger);
                Shutdown(1);
                return;
            }

            // 打开软件时自动做一次本地备份并按策略清理旧备份；失败不阻断启动
            try
            {
                Services.BackupService.CreateLocalBackup(Data.Connection, "startup");
                if (Settings != null)
                {
                    Services.BackupService.PruneLocalBackups(Settings.BackupKeepCount);
                }
            }
            catch (Exception ex)
            {
                Services.AppLog.Warn("启动本地备份或清理旧备份失败：" + ex.Message);
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
            Services.AppLog.Error("界面线程发生未处理异常。", e.Exception);

            // 重入守卫：防止异常处理弹窗本身再次触发异常造成无限弹窗
            if (Interlocked.Exchange(ref _handlingUnhandledException, 1) != 0)
            {
                return;
            }

            try
            {
                Window owner = MainWindow != null && MainWindow.IsLoaded ? MainWindow : null;
                Dialogs.MessageWindow.Show(
                    owner,
                    "发生了未处理的错误：\n\n" + e.Exception.Message,
                    "轻量数据管理工具",
                    Dialogs.MessageTone.Danger);
            }
            catch (Exception showEx)
            {
                Services.AppLog.Error("显示错误弹窗失败。", showEx);
            }
            finally
            {
                Interlocked.Exchange(ref _handlingUnhandledException, 0);
            }
        }

        private static void ReportFatal(Exception ex)
        {
            if (ex == null)
            {
                return;
            }

            Services.AppLog.Error("发生致命异常。", ex);

            string message = "发生了致命错误：\n\n" + ex.Message;

            void ShowCustomDialog()
            {
                Window main = Current?.MainWindow;
                Window owner = main != null && main.IsLoaded ? main : null;
                Dialogs.MessageWindow.Show(
                    owner,
                    message,
                    "轻量数据管理工具",
                    Dialogs.MessageTone.Danger);
            }

            void ShowFallbackBox()
            {
                try
                {
                    MessageBox.Show(message, "轻量数据管理工具", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception)
                {
                }
            }

            Dispatcher dispatcher = Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                ShowFallbackBox();
                return;
            }

            if (dispatcher.CheckAccess())
            {
                try
                {
                    ShowCustomDialog();
                }
                catch (Exception)
                {
                    ShowFallbackBox();
                }

                return;
            }

            try
            {
                dispatcher.Invoke(ShowCustomDialog);
            }
            catch (Exception)
            {
                ShowFallbackBox();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_activateRegistration != null)
            {
                using (var unregistered = new ManualResetEvent(false))
                {
                    if (_activateRegistration.Unregister(unregistered))
                    {
                        unregistered.WaitOne(2000);
                    }
                }

                _activateRegistration = null;
            }

            try
            {
                Data?.CloseDatabase();
                Data?.Dispose();
            }
            catch (Exception ex)
            {
                Services.AppLog.Warn("退出阶段关闭数据库异常：" + ex.Message);
            }

            _activateSignal?.Dispose();
            _activateSignal = null;

            if (_instanceMutex != null)
            {
                if (_ownsInstanceMutex)
                {
                    try
                    {
                        _instanceMutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }

                    _ownsInstanceMutex = false;
                }

                _instanceMutex.Dispose();
                _instanceMutex = null;
            }

            // 先释放单实例互斥，再拉起新进程，否则新实例会以为旧的还在
            if (ExitAction == AppExitAction.Restart)
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
            catch (Exception ex)
            {
                Services.AppLog.Error("自动重启进程失败。", ex);
            }
        }
    }
}
