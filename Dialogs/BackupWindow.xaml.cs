using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DataManager.Services;
using DataManager.Dialogs;

namespace DataManager
{
    /// <summary>本地备份文件的展示行。</summary>
    public sealed class BackupRow
    {
        public string FullPath { get; set; }

        public string FileName { get; set; }

        public long Length { get; set; }

        public DateTime Time { get; set; }

        public string SizeText => BackupService.FormatSize(Length);

        public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>进度回调：把字节数折算成百分比。</summary>
    internal sealed class PercentProgress : IProgress<long>, IDisposable
    {
        private readonly Action<double> _report;
        private readonly long _total;

        public PercentProgress(long total, Action<double> report)
        {
            _total = total;
            _report = report;
        }

        public void Report(long value)
        {
            if (_total <= 0)
            {
                _report(0);
                return;
            }

            double percent = Math.Min(100.0, value * 100.0 / _total);
            _report(percent);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>本地备份 / WebDAV 备份与恢复。</summary>
    public partial class BackupWindow : Window
    {
        private CancellationTokenSource _cts;
        private bool _busy;

        public BackupWindow()
        {
            InitializeComponent();

            TxtUrl.Text = App.Settings.WebDavUrl;
            TxtUser.Text = App.Settings.WebDavUser;
            TxtPassword.Password = App.Settings.WebDavPassword;
            TxtRemoteDir.Text = App.Settings.WebDavDirectory ?? string.Empty;
            TxtRemoteName.Text = string.IsNullOrWhiteSpace(App.Settings.WebDavFileNamePrefix)
                ? "DataManager"
                : App.Settings.WebDavFileNamePrefix;
            TxtKeepCount.Text = App.Settings.BackupKeepCount.ToString(CultureInfo.InvariantCulture);

            Loaded += (s, e) => RefreshBackups();
        }

        private AppData Data => App.Data;

        // =================================================================
        // 本地备份
        // =================================================================

        private void RefreshBackups()
        {
            var rows = new List<BackupRow>();
            foreach (FileInfo f in BackupService.ListLocalBackups())
            {
                rows.Add(new BackupRow
                {
                    FullPath = f.FullName,
                    FileName = f.Name,
                    Length = f.Length,
                    Time = f.LastWriteTime,
                });
            }

            BackupGrid.ItemsSource = rows;
            BtnDeleteBackup.IsEnabled = rows.Count > 0;
            TxtLocalStatus.Text = rows.Count == 0
                ? "还没有本地备份。"
                : "共 " + rows.Count.ToString(CultureInfo.InvariantCulture) + " 个本地备份，位于：" + AppPaths.BackupDirectory;
        }

        private void OnRefreshBackups(object sender, RoutedEventArgs e)
        {
            RefreshBackups();
        }

        private void OnLocalBackup(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = BackupService.CreateLocalBackup(Data.Connection, "manual");
                BackupService.PruneLocalBackups(App.Settings.BackupKeepCount);
                RefreshBackups();
                MessageWindow.Show(this, "本地备份已完成：\n" + path,
                "本地备份", Dialogs.MessageTone.Info);
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "本地备份失败：\n\n" + ex.Message, "本地备份", Dialogs.MessageTone.Danger);
            }
        }

        private void OnOpenBackupFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.EnsureCreated();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppPaths.BackupDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "无法打开备份文件夹：\n\n" + ex.Message, "打开文件夹", Dialogs.MessageTone.Danger);
            }
        }

        private void OnDeleteBackup(object sender, RoutedEventArgs e)
        {
            if (!(BackupGrid.SelectedItem is BackupRow row))
            {

                return;
            }

            if (!MessageWindow.Confirm(this, "确定要删除备份文件「" + row.FileName + "」吗？", "删除备份", Dialogs.MessageTone.Danger, Dialogs.MessageButtons.OkCancel))
            {
                return;
            }

            try
            {
                File.Delete(row.FullPath);
                RefreshBackups();
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "删除失败：\n\n" + ex.Message, "删除备份", Dialogs.MessageTone.Danger);
            }
        }

        private void OnApplyRetention(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtKeepCount.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keep) || keep < 0)
            {
                MessageWindow.Show(this, "请输入 0 或正整数（0 表示保留全部）。", "保留策略", Dialogs.MessageTone.Info);
                return;
            }

            App.Settings.BackupKeepCount = keep;
            SettingsService.Save(App.Settings);

            int removed = BackupService.PruneLocalBackups(keep);
            RefreshBackups();
            MessageWindow.Show(this, keep == 0
                    ? "已设为保留全部本地备份。"
                    : "保留最近 " + keep.ToString(CultureInfo.InvariantCulture) + " 份，已清理 " + removed.ToString(CultureInfo.InvariantCulture) + " 个旧备份。",
                "保留策略", Dialogs.MessageTone.Info);
        }

        private void OnRestoreLocal(object sender, RoutedEventArgs e)
        {
            if (!(BackupGrid.SelectedItem is BackupRow row))
            {

                return;
            }

            ConfirmAndRestore(row.FullPath, "本地备份「" + row.FileName + "」");
        }

        // =================================================================
        // WebDAV
        // =================================================================

        private void OnSaveDavConfig(object sender, RoutedEventArgs e)
        {
            DavConfig config = ReadConfigFromUi();
            if (config == null)
            {
                return;
            }

            ApplyConfigToSettings(config);

            try
            {
                SettingsService.Save(App.Settings);
                TxtDavStatus.Text = "配置已保存到 config.json";
            }
            catch (Exception ex)
            {
                MessageWindow.Show(this, "保存配置失败：\n\n" + ex.Message, "保存配置", Dialogs.MessageTone.Danger);
            }
        }

        private void OnTestConnection(object sender, RoutedEventArgs e)
        {
            // 所有界面取值都必须在 UI 线程上完成，后台线程碰控件会抛
            // “调用线程无法访问此对象，因为另一个线程拥有该对象”。
            DavConfig config = CaptureConfig();
            if (config == null)
            {
                return;
            }

            RunBusy("正在测试连接…", () =>
            {
                using (WebDavClient client = new WebDavClient(config.Url, config.User, config.Password))
                {
                    client.TestConnection();

                    // 连接能通不代表目录存在：PUT 不会自动建目录，这里顺便看一眼，
                    // 免得用户点“上传”时才撞上 404。
                    if (config.Directory.Length > 0)
                    {
                        bool exists = client.DirectoryExists(config.Directory);
                        return exists
                            ? "连接成功，目录「" + config.Directory + "」已存在"
                            : "连接成功，但目录「" + config.Directory + "」还不存在（上传时会自动创建）";
                    }

                    return "连接成功";
                }
            });
        }

        /// <summary>界面上的 WebDAV 配置快照（在 UI 线程取值，之后传给后台线程）。</summary>
        private sealed class DavConfig
        {
            public string Url;
            public string User;
            public string Password;
            public string Directory;

            /// <summary>文件名前缀；真正的文件名是「前缀_日期_时间.sqlite」。</summary>
            public string NamePrefix;

            public string FullRemotePath(string fileName) =>
                WebDavClient.CombineRemotePath(Directory, fileName);
        }

        /// <summary>只读界面上的 WebDAV 配置（不校验、不写盘）。</summary>
        private DavConfig ReadConfigFromUi()
        {
            string prefix = (TxtRemoteName.Text ?? string.Empty).Trim();

            return new DavConfig
            {
                Url = (TxtUrl.Text ?? string.Empty).Trim(),
                User = (TxtUser.Text ?? string.Empty).Trim(),
                Password = TxtPassword.Password ?? string.Empty,
                Directory = (TxtRemoteDir.Text ?? string.Empty).Trim(),
                NamePrefix = prefix.Length == 0 ? "DataManager" : BackupService.SafeFileName(prefix),
            };
        }

        /// <summary>把配置写回 App.Settings（不写盘）。</summary>
        private static void ApplyConfigToSettings(DavConfig config)
        {
            App.Settings.WebDavUrl = config.Url;
            App.Settings.WebDavUser = config.User;
            App.Settings.WebDavPassword = config.Password;
            App.Settings.WebDavDirectory = config.Directory;
            App.Settings.WebDavFileNamePrefix = config.NamePrefix;
        }

        /// <summary>在 UI 线程读取并保存 WebDAV 配置；地址为空时返回 null。</summary>
        private DavConfig CaptureConfig()
        {
            DavConfig config = ReadConfigFromUi();

            if (config.Url.Length == 0)
            {
                MessageWindow.Show(this, "请先填写 WebDAV 地址。", "WebDAV", MessageTone.Info);
                return null;
            }

            ApplyConfigToSettings(config);

            try
            {
                SettingsService.Save(App.Settings);
            }
            catch (Exception)
            {
                // 配置写盘失败不阻断操作
            }

            return config;
        }

        private void OnUpload(object sender, RoutedEventArgs e)
        {
            DavConfig config = CaptureConfig();
            if (config == null)
            {
                return;
            }

            // 每次上传都生成新文件名：不依赖服务器的 PUT 覆盖语义，历史备份也自动保留。
            string remoteFileName = BackupService.RemoteBackupFileName(config.NamePrefix);

            RunBusy("正在上传…", () =>
            {
                // 先把当前库在线备份成一个一致快照，再上传这个快照
                string snapshot = Path.Combine(AppPaths.RestoreStageDirectory, "upload_snapshot.sqlite");
                Directory.CreateDirectory(AppPaths.RestoreStageDirectory);
                if (File.Exists(snapshot))
                {
                    File.Delete(snapshot);
                }

                Data.Connection.BackupTo(snapshot);
                long total = new FileInfo(snapshot).Length;

                using (WebDavClient client = new WebDavClient(config.Url, config.User, config.Password))
                using (var progress = new PercentProgress(total, ReportProgress))
                {
                    // PUT 不会自动建目录，先把远端目录补齐（已存在则跳过）
                    client.EnsureDirectory(config.Directory, _cts.Token);
                    client.Upload(snapshot, config.FullRemotePath(remoteFileName), progress, _cts.Token);
                }

                try
                {
                    File.Delete(snapshot);
                }
                catch (Exception)
                {
                    // 清理快照失败无碍
                }

                return "上传完成：" + remoteFileName + "（" + BackupService.FormatSize(total) + "）";
            }, afterSuccess: () =>
            {
                // 上传完顺手刷新远端列表，让用户立刻看到新备份
                RefreshRemoteList(config, showStatus: true);
            });
        }

        // =================================================================
        // 远端备份列表 / 恢复
        // =================================================================

        /// <summary>远端备份文件的展示行。</summary>
        public sealed class RemoteBackupRow
        {
            public string FileName { get; set; }

            public long Length { get; set; }

            public DateTime Time { get; set; }

            public string SizeText => BackupService.FormatSize(Length);

            public string TimeText => Time == DateTime.MinValue
                ? "-"
                : Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void OnRefreshRemote(object sender, RoutedEventArgs e)
        {
            DavConfig config = CaptureConfig();
            if (config == null)
            {
                return;
            }

            RefreshRemoteList(config, showStatus: true);
        }

        /// <summary>拉取远端备份列表并展示；失败只提示，不打断界面。</summary>
        private void RefreshRemoteList(DavConfig config, bool showStatus)
        {
            RunBusy("正在读取远端备份列表…", () =>
            {
                var rows = new List<RemoteBackupRow>();

                using (WebDavClient client = new WebDavClient(config.Url, config.User, config.Password))
                {
                    foreach (WebDavEntry entry in client.List(config.Directory))
                    {
                        if (entry.IsCollection)
                        {
                            continue;
                        }

                        if (!entry.Name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        rows.Add(new RemoteBackupRow
                        {
                            FileName = entry.Name,
                            Length = entry.Length,
                            Time = entry.LastModified.HasValue
                                ? entry.LastModified.Value.ToLocalTime()
                                : BackupService.ParseBackupTime(entry.Name, DateTime.MinValue),
                        });
                    }
                }

                // 新的排前面
                rows.Sort((a, b) => b.Time.CompareTo(a.Time));

                Dispatcher.Invoke(() =>
                {
                    RemoteGrid.ItemsSource = rows;
                    BtnRestoreRemote.IsEnabled = rows.Count > 0;

                    if (showStatus)
                    {
                        TxtRemoteStatus.Text = rows.Count == 0
                            ? "远端还没有备份文件。"
                            : "共 " + rows.Count.ToString(CultureInfo.InvariantCulture) + " 个远端备份。";
                    }
                });

                return null;
            }, quiet: true);
        }

        private void OnDownloadAndRestore(object sender, RoutedEventArgs e)
        {
            DavConfig config = CaptureConfig();
            if (config == null)
            {
                return;
            }

            if (!(RemoteGrid.SelectedItem is RemoteBackupRow row))
            {
                MessageWindow.Show(this, "请先在列表里选择一个远端备份文件。", "从 WebDAV 恢复", MessageTone.Info);
                return;
            }

            string remotePath = WebDavClient.CombineRemotePath(config.Directory, row.FileName);

            if (!MessageWindow.Confirm(this, "将用 WebDAV 上的「" + remotePath + "」覆盖当前数据库。\n\n" +
                "恢复前会自动在本地备份当前数据库，恢复后需要重启软件。\n\n确定继续吗？",
                "从 WebDAV 恢复", MessageTone.Danger, MessageButtons.OkCancel))
            {
                return;
            }

            RunBusy("正在下载…", () =>
            {
                Directory.CreateDirectory(AppPaths.RestoreStageDirectory);
                string downloaded = Path.Combine(AppPaths.RestoreStageDirectory, "downloaded.sqlite");
                if (File.Exists(downloaded))
                {
                    File.Delete(downloaded);
                }

                long total;
                using (WebDavClient client = new WebDavClient(config.Url, config.User, config.Password))
                {
                    total = client.GetContentLength(remotePath);
                    using (var progress = new PercentProgress(total, ReportProgress))
                    {
                        client.Download(remotePath, downloaded, progress, _cts.Token);
                    }
                }

                return "下载完成：" + BackupService.FormatSize(new FileInfo(downloaded).Length);
            }, afterSuccess: () =>
            {
                string downloaded = Path.Combine(AppPaths.RestoreStageDirectory, "downloaded.sqlite");
                ConfirmAndRestore(downloaded, "WebDAV 上的「" + remotePath + "」");
            });
        }

        // =================================================================
        // 恢复
        // =================================================================

        /// <summary>校验来源文件 → 备份当前库 → 关闭连接 → 覆盖 → 提示重启。</summary>
        private void ConfirmAndRestore(string sourcePath, string description)
        {
            string reason;
            if (!BackupService.IsValidSqlite(sourcePath, out reason))
            {
                MessageWindow.Show(this, "无法用该文件恢复：\n\n" + reason,
                    "恢复失败", Dialogs.MessageTone.Danger);
                return;
            }

            string currentBackup;
            try
            {
                // 恢复前先备份当前库
                currentBackup = BackupService.CreateLocalBackup(Data.Connection, "before_restore");
            }
            catch (Exception ex)
            {
                if (!MessageWindow.Confirm(this, "恢复前的自动备份失败：\n\n" + ex.Message + "\n\n仍要继续恢复吗？",
                        "恢复", Dialogs.MessageTone.Danger, Dialogs.MessageButtons.OkCancel))
                {
                    return;
                }

                currentBackup = null;
            }

            try
            {
                // 关闭连接，释放文件句柄
                Data.CloseDatabase();
                BackupService.ReplaceDatabase(sourcePath);
            }
            catch (Exception ex)
            {
                try
                {
                    Data.ReopenDatabase();
                }
                catch (Exception reopenEx)
                {
                    App.RestartRequested = true;
                    MessageWindow.Show(this, "恢复失败：\n\n" + ex.Message + "\n\n" +
                        "当前数据库也无法重新打开：" + reopenEx.Message + "\n\n" +
                        (currentBackup != null ? "恢复前的备份仍在：" + currentBackup + "\n\n" : string.Empty) +
                        "软件将关闭，请重新打开后再试。",
                        "恢复失败", Dialogs.MessageTone.Danger);
                    this.CloseWithResult(true);
                    return;
                }

                MessageWindow.Show(this, "恢复失败：\n\n" + ex.Message + "\n\n" +
                    "已重新打开原来的数据库。" +
                    (currentBackup != null ? "\n恢复前的备份仍在：" + currentBackup : string.Empty),
                    "恢复失败", Dialogs.MessageTone.Danger);
                return;
            }

            App.RestartRequested = true;

            MessageWindow.Show(this, "恢复完成。\n\n来源：" + description + "\n" +
                (currentBackup != null ? "恢复前的数据库已备份到：\n" + currentBackup + "\n\n" : string.Empty) +
                "软件将立即重启以重新打开数据库。",
                "恢复完成", Dialogs.MessageTone.Info);

            this.CloseWithResult(true);
        }

        // =================================================================
        // 忙碌状态
        // =================================================================

        private void ReportProgress(double percent)
        {
            Dispatcher.Invoke(() =>
            {
                OpProgress.Visibility = Visibility.Visible;
                OpProgress.Value = percent;
            });
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            BtnCancelOp.IsEnabled = busy;
            OpProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!busy)
            {
                OpProgress.Value = 0;
            }

            // status 为 null 表示“不要改动状态文字”，
            // 这样后续动作（例如上传完自动刷新远端列表）不会把结果顶掉。
            if (status != null)
            {
                TxtDavStatus.Text = status;
            }

            TxtUrl.IsEnabled = !busy;
            TxtUser.IsEnabled = !busy;
            TxtPassword.IsEnabled = !busy;
            TxtRemoteDir.IsEnabled = !busy;
            TxtRemoteName.IsEnabled = !busy;
            BtnListRemote.IsEnabled = !busy;
            BtnRestoreRemote.IsEnabled = !busy && RemoteGrid.Items.Count > 0;
        }

        /// <summary>
        /// 在后台线程执行一个网络操作，界面显示进度与状态。
        /// quiet = true 时不动 TxtDavStatus，用于不打断主操作结果的附带刷新。
        /// </summary>
        private void RunBusy(string initialStatus, Func<string> operation, Action afterSuccess = null, bool quiet = false)
        {
            if (_busy)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            SetBusy(true, quiet ? null : initialStatus);

            Task<string> task = Task.Run(operation);

            task.ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    SetBusy(false, null);

                    if (t.IsFaulted)
                    {
                        Exception ex = t.Exception?.GetBaseException() ?? new Exception("未知错误");
                        if (ex is OperationCanceledException)
                        {
                            TxtDavStatus.Text = "操作已取消";
                            return;
                        }

                        if (!quiet)
                        {
                            TxtDavStatus.Text = "操作失败";
                        }

                        MessageWindow.Show(this, ex.Message, "操作失败", Dialogs.MessageTone.Danger);
                        return;
                    }

                    if (!quiet)
                    {
                        TxtDavStatus.Text = t.Result ?? string.Empty;
                    }

                    afterSuccess?.Invoke();
                });
            }, TaskScheduler.Default);
        }

        private void OnCancelOperation(object sender, RoutedEventArgs e)
        {
            try
            {
                _cts?.Cancel();
                TxtDavStatus.Text = "正在取消…";
            }
            catch (Exception)
            {
                // 忽略取消失败
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_busy)
            {
                bool result = MessageWindow.Confirm(this, "有一个正在进行的网络操作。\n\n确定要中止并关闭吗？",
                    "操作进行中", Dialogs.MessageTone.Danger, Dialogs.MessageButtons.OkCancel);

                if (!result)
                {
                    e.Cancel = true;
                    return;
                }

                try
                {
                    _cts?.Cancel();
                }
                catch (Exception)
                {
                }
            }

            base.OnClosing(e);
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
