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

    /// <summary>进度回调：同时传递已传输字节数与总大小（总大小 <= 0 表示未知大小）。</summary>
    internal sealed class TransferProgress : IProgress<long>, IDisposable
    {
        private readonly Action<long, long> _report;
        private readonly long _total;

        public TransferProgress(long total, Action<long, long> report)
        {
            _total = total;
            _report = report;
        }

        public void Report(long value)
        {
            _report(value, _total);
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
        private bool _isClosed;
        private string _httpWarnedUrl;

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
            if (_isClosed || !IsLoaded)
            {
                return;
            }

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
            bool hasRows = rows.Count > 0;
            BtnDeleteLocal.IsEnabled = !_busy && hasRows;
            BtnRestoreLocal.IsEnabled = !_busy && hasRows;
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
            int keepCount = App.Settings.BackupKeepCount;
            string createdPath = null;

            RunBusy("正在创建本地备份…", ct =>
            {
                ct.ThrowIfCancellationRequested();
                createdPath = BackupService.CreateLocalBackup(Data.Connection, "manual");
                BackupService.PruneLocalBackups(keepCount);
                return "本地备份已完成：" + Path.GetFileName(createdPath);
            }, afterSuccess: () =>
            {
                if (_isClosed || !IsLoaded)
                {
                    return;
                }

                RefreshBackups();
                MessageWindow.Show(this, "本地备份已完成：\n" + createdPath,
                    "本地备份", MessageTone.Info);
            });
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
                MessageWindow.Show(this, "无法打开备份文件夹：\n\n" + ex.Message, "打开文件夹", MessageTone.Danger);
            }
        }

        private void OnDeleteBackup(object sender, RoutedEventArgs e)
        {
            if (!(BackupGrid.SelectedItem is BackupRow row))
            {
                MessageWindow.Show(this, "请先在列表里选择一个本地备份文件。", "删除备份", MessageTone.Info);
                return;
            }

            if (!MessageWindow.Confirm(this, "确定要删除备份文件「" + row.FileName + "」吗？", "删除备份", MessageTone.Danger, MessageButtons.OkCancel))
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
                MessageWindow.Show(this, "删除失败：\n\n" + ex.Message, "删除备份", MessageTone.Danger);
            }
        }

        private void OnApplyRetention(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtKeepCount.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keep) || keep < 0)
            {
                MessageWindow.Show(this, "请输入 0 或正整数（0 表示保留全部）。", "保留策略", MessageTone.Info);
                return;
            }

            App.Settings.BackupKeepCount = keep;
            SettingsService.Save(App.Settings);

            int removed = BackupService.PruneLocalBackups(keep);
            RefreshBackups();
            MessageWindow.Show(this, keep == 0
                    ? "已设为保留全部本地备份。"
                    : "保留最近 " + keep.ToString(CultureInfo.InvariantCulture) + " 份，已清理 " + removed.ToString(CultureInfo.InvariantCulture) + " 个旧备份。",
                "保留策略", MessageTone.Info);
        }

        private void OnRestoreLocal(object sender, RoutedEventArgs e)
        {
            if (!(BackupGrid.SelectedItem is BackupRow row))
            {
                MessageWindow.Show(this, "请先在列表里选择一个本地备份文件。", "恢复本地备份", MessageTone.Info);
                return;
            }

            if (!MessageWindow.Confirm(
                this,
                "将用本地备份「" + row.FileName + "」覆盖当前数据库。\n\n" +
                "恢复前会自动在本地备份当前数据库，恢复后需要重启软件。\n\n确定继续吗？",
                "恢复本地备份",
                MessageTone.Danger,
                MessageButtons.OkCancel))
            {
                return;
            }

            ConfirmAndRestore(row.FullPath, "本地备份「" + row.FileName + "」");
        }

        // =================================================================
        // WebDAV
        // =================================================================

        /// <summary>若配置了明文 http:// 地址，弹出安全确认提示；返回 false 表示用户取消。</summary>
        private bool ConfirmHttpSecurityIfNeeded(string url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(_httpWarnedUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            bool ok = MessageWindow.Confirm(
                this,
                "当前 WebDAV 地址使用明文 HTTP 协议（http://），账号密码及数据库内容在网络传输过程中可能被窃听。\n\n" +
                "建议优先使用 HTTPS（https://）地址。仍要继续使用该明文地址吗？",
                "安全提示",
                MessageTone.Warning,
                MessageButtons.OkCancel);

            if (ok)
            {
                _httpWarnedUrl = url;
            }

            return ok;
        }

        private void OnSaveDavConfig(object sender, RoutedEventArgs e)
        {
            DavConfig config = ReadConfigFromUi();
            if (config == null)
            {
                return;
            }

            if (!ConfirmHttpSecurityIfNeeded(config.Url))
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
                MessageWindow.Show(this, "保存配置失败：\n\n" + ex.Message, "保存配置", MessageTone.Danger);
            }
        }

        private void OnTestConnection(object sender, RoutedEventArgs e)
        {
            // 所有界面取值都必须在 UI 线程上完成，后台线程碰控件会抛跨线程访问异常。
            DavConfig config = CaptureConfig();
            if (config == null)
            {
                return;
            }

            RunBusy("正在测试连接…", ct =>
            {
                using (WebDavClient client = new WebDavClient(config.Url, config.User, config.Password))
                {
                    client.TestConnection(ct);

                    // 连接能通不代表目录存在：PUT 不会自动建目录，这里顺便看一眼，
                    // 免得用户点“上传”时才撞上 404。
                    if (config.Directory.Length > 0)
                    {
                        bool exists = client.DirectoryExists(config.Directory, ct);
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
            string safePrefix = BackupService.SafeFileName(prefix);

            return new DavConfig
            {
                Url = (TxtUrl.Text ?? string.Empty).Trim(),
                User = (TxtUser.Text ?? string.Empty).Trim(),
                Password = TxtPassword.Password ?? string.Empty,
                Directory = (TxtRemoteDir.Text ?? string.Empty).Trim(),
                NamePrefix = string.IsNullOrEmpty(safePrefix) ? "DataManager" : safePrefix,
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

        /// <summary>在 UI 线程读取、校验并保存 WebDAV 配置；地址为空或取消明文 HTTP 警告时返回 null。</summary>
        private DavConfig CaptureConfig()
        {
            DavConfig config = ReadConfigFromUi();

            if (config.Url.Length == 0)
            {
                MessageWindow.Show(this, "请先填写 WebDAV 地址。", "WebDAV", MessageTone.Info);
                return null;
            }

            if (!ConfirmHttpSecurityIfNeeded(config.Url))
            {
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

            RunBusy("正在上传…", ct =>
            {
                // 先把当前库在线备份成一个一致快照，再上传这个快照
                string snapshot = Path.Combine(AppPaths.RestoreStageDirectory, "upload_snapshot.sqlite");
                Directory.CreateDirectory(AppPaths.RestoreStageDirectory);

                try
                {
                    TryDeleteFileQuietly(snapshot);

                    ct.ThrowIfCancellationRequested();
                    Data.Connection.BackupTo(snapshot);
                    long total = new FileInfo(snapshot).Length;

                    using (WebDavClient client = new WebDavClient(config.Url, config.User, config.Password))
                    using (var progress = new TransferProgress(total, ReportProgress))
                    {
                        // PUT 不会自动建目录，先把远端目录补齐（已存在则跳过）
                        client.EnsureDirectory(config.Directory, ct);
                        client.Upload(snapshot, config.FullRemotePath(remoteFileName), progress, ct);
                    }

                    return "上传完成：" + remoteFileName + "（" + BackupService.FormatSize(total) + "）";
                }
                finally
                {
                    TryDeleteFileQuietly(snapshot);
                }
            }, afterSuccess: () =>
            {
                if (_isClosed || !IsLoaded)
                {
                    return;
                }

                // 上传完顺手刷新远端列表，让用户立刻看到新备份
                RefreshRemoteList(config, showStatus: true);
            });
        }

        // =================================================================
        // 远端备份列表 / 恢复 / 删除
        // =================================================================

        /// <summary>远端备份文件的展示行。</summary>
        public sealed class RemoteBackupRow
        {
            public string FileName { get; set; }

            /// <summary>刷新列表时固定写入的远端相对路径，恢复与删除时直接使用。</summary>
            public string RemotePath { get; set; }

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
            if (_isClosed || !IsLoaded)
            {
                return;
            }

            List<RemoteBackupRow> fetchedRows = null;

            RunBusy("正在读取远端备份列表…", ct =>
            {
                var rows = new List<RemoteBackupRow>();

                using (WebDavClient client = new WebDavClient(config.Url, config.User, config.Password))
                {
                    foreach (WebDavEntry entry in client.List(config.Directory, ct))
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
                            RemotePath = config.FullRemotePath(entry.Name),
                            Length = entry.Length,
                            Time = entry.LastModified.HasValue
                                ? entry.LastModified.Value.ToLocalTime()
                                : BackupService.ParseBackupTime(entry.Name, DateTime.MinValue),
                        });
                    }
                }

                // 新的排前面
                rows.Sort((a, b) => b.Time.CompareTo(a.Time));
                fetchedRows = rows;

                return null;
            }, afterSuccess: () =>
            {
                if (_isClosed || !IsLoaded || fetchedRows == null)
                {
                    return;
                }

                RemoteGrid.ItemsSource = fetchedRows;
                bool hasRows = fetchedRows.Count > 0;
                BtnRestoreRemote.IsEnabled = !_busy && hasRows;
                BtnDeleteRemote.IsEnabled = !_busy && hasRows;

                if (showStatus)
                {
                    TxtRemoteStatus.Text = fetchedRows.Count == 0
                        ? "远端还没有备份文件。"
                        : "共 " + fetchedRows.Count.ToString(CultureInfo.InvariantCulture) + " 个远端备份。";
                }
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

            string remotePath = string.IsNullOrWhiteSpace(row.RemotePath)
                ? WebDavClient.CombineRemotePath(config.Directory, row.FileName)
                : row.RemotePath;

            if (!MessageWindow.Confirm(this, "将用 WebDAV 上的「" + remotePath + "」覆盖当前数据库。\n\n" +
                "恢复前会自动在本地备份当前数据库，恢复后需要重启软件。\n\n确定继续吗？",
                "从 WebDAV 恢复", MessageTone.Danger, MessageButtons.OkCancel))
            {
                return;
            }

            string downloaded = Path.Combine(AppPaths.RestoreStageDirectory, "downloaded.sqlite");

            RunBusy("正在下载…", ct =>
            {
                Directory.CreateDirectory(AppPaths.RestoreStageDirectory);
                TryDeleteFileQuietly(downloaded);

                long total;
                using (WebDavClient client = new WebDavClient(config.Url, config.User, config.Password))
                {
                    total = client.GetContentLength(remotePath, ct);
                    if (total <= 0)
                    {
                        ReportProgress(0, total);
                    }

                    using (var progress = new TransferProgress(total, ReportProgress))
                    {
                        client.Download(remotePath, downloaded, progress, ct);
                    }
                }

                return "下载完成：" + BackupService.FormatSize(new FileInfo(downloaded).Length);
            }, afterSuccess: () =>
            {
                try
                {
                    if (_isClosed || !IsLoaded)
                    {
                        return;
                    }

                    ConfirmAndRestore(downloaded, "WebDAV 上的「" + remotePath + "」");
                }
                finally
                {
                    TryDeleteFileQuietly(downloaded);
                }
            });
        }

        private void OnDeleteRemote(object sender, RoutedEventArgs e)
        {
            DavConfig config = CaptureConfig();
            if (config == null)
            {
                return;
            }

            if (!(RemoteGrid.SelectedItem is RemoteBackupRow row))
            {
                MessageWindow.Show(this, "请先在列表里选择一个远端备份文件。", "删除远端备份", MessageTone.Info);
                return;
            }

            string remotePath = string.IsNullOrWhiteSpace(row.RemotePath)
                ? WebDavClient.CombineRemotePath(config.Directory, row.FileName)
                : row.RemotePath;

            if (!MessageWindow.Confirm(
                this,
                "确定要永久删除 WebDAV 上的远端备份「" + remotePath + "」吗？",
                "删除远端备份",
                MessageTone.Danger,
                MessageButtons.OkCancel))
            {
                return;
            }

            RunBusy("正在删除远端备份…", ct =>
            {
                using (WebDavClient client = new WebDavClient(config.Url, config.User, config.Password))
                {
                    client.Delete(remotePath, ct);
                }

                return "已删除远端备份：" + row.FileName;
            }, afterSuccess: () =>
            {
                if (_isClosed || !IsLoaded)
                {
                    return;
                }

                RefreshRemoteList(config, showStatus: true);
            });
        }

        // =================================================================
        // 恢复
        // =================================================================

        /// <summary>校验来源文件 → 备份当前库 → 关闭连接 → 覆盖 → 提示重启。</summary>
        private void ConfirmAndRestore(string sourcePath, string description)
        {
            if (_isClosed || !IsLoaded)
            {
                return;
            }

            string reason;
            if (!BackupService.IsValidSqlite(sourcePath, out reason))
            {
                MessageWindow.Show(this, "无法用该文件恢复：\n\n" + reason,
                    "恢复失败", MessageTone.Danger);
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
                        "恢复", MessageTone.Danger, MessageButtons.OkCancel))
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
                bool rolledBack = false;

                // 若主库已被覆盖但伴随文件清理失败，优先回滚恢复前备份，避免直接以不一致状态重开主库
                if (ex is DatabaseSidecarCleanupException &&
                    currentBackup != null &&
                    File.Exists(currentBackup))
                {
                    try
                    {
                        BackupService.ReplaceDatabase(currentBackup);
                        rolledBack = true;
                    }
                    catch (Exception rollbackEx)
                    {
                        App.ExitAction = AppExitAction.Shutdown;
                        MessageWindow.Show(this, "恢复失败：\n\n" + ex.Message + "\n\n" +
                            "尝试回滚恢复前备份也失败：" + rollbackEx.Message + "\n\n" +
                            "恢复前的备份仍在：" + currentBackup + "\n\n" +
                            "软件将关闭，请手动检查后重新打开。",
                            "恢复失败", MessageTone.Danger);
                        this.CloseWithResult(true);
                        return;
                    }
                }

                try
                {
                    Data.ReopenDatabase();
                }
                catch (Exception reopenEx)
                {
                    App.ExitAction = AppExitAction.Shutdown;
                    MessageWindow.Show(this, "恢复失败：\n\n" + ex.Message + "\n\n" +
                        "当前数据库也无法重新打开：" + reopenEx.Message + "\n\n" +
                        (currentBackup != null ? "恢复前的备份仍在：" + currentBackup + "\n\n" : string.Empty) +
                        "软件将关闭，请重新打开后再试。",
                        "恢复失败", MessageTone.Danger);
                    this.CloseWithResult(true);
                    return;
                }

                MessageWindow.Show(this, "恢复失败：\n\n" + ex.Message + "\n\n" +
                    (rolledBack ? "已回滚并重新打开恢复前的数据库。" : "已重新打开原来的数据库。") +
                    (currentBackup != null ? "\n恢复前的备份仍在：" + currentBackup : string.Empty),
                    "恢复失败", MessageTone.Danger);
                return;
            }

            App.ExitAction = AppExitAction.Restart;

            MessageWindow.Show(this, "恢复完成。\n\n来源：" + description + "\n" +
                (currentBackup != null ? "恢复前的数据库已备份到：\n" + currentBackup + "\n\n" : string.Empty) +
                "软件将立即重启以重新打开数据库。",
                "恢复完成", MessageTone.Info);

            this.CloseWithResult(true);
        }

        // =================================================================
        // 忙碌状态
        // =================================================================

        private void ReportProgress(long transferred, long total)
        {
            if (_isClosed)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosed || !IsLoaded)
                {
                    return;
                }

                OpProgress.Visibility = Visibility.Visible;
                if (total <= 0)
                {
                    OpProgress.IsIndeterminate = true;
                    TxtDavStatus.Text = "正在下载… 已下载 " + BackupService.FormatSize(transferred);
                }
                else
                {
                    OpProgress.IsIndeterminate = false;
                    OpProgress.Value = Math.Min(100.0, transferred * 100.0 / total);
                }
            }));
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            BtnCancelOp.IsEnabled = busy;
            OpProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!busy)
            {
                OpProgress.IsIndeterminate = false;
                OpProgress.Value = 0;
            }

            // status 为 null 表示“不要改动状态文字”，
            // 这样后续动作（例如上传完自动刷新远端列表）不会把结果顶掉。
            if (status != null)
            {
                TxtDavStatus.Text = status;
            }

            // 本地备份区读写按钮与输入控件
            BtnLocalBackup.IsEnabled = !busy;
            BtnRefreshBackups.IsEnabled = !busy;
            BtnRestoreLocal.IsEnabled = !busy && BackupGrid.Items.Count > 0;
            BtnDeleteLocal.IsEnabled = !busy && BackupGrid.Items.Count > 0;
            TxtKeepCount.IsEnabled = !busy;
            BtnApplyKeep.IsEnabled = !busy;

            // WebDAV 区配置、网络读写按钮与输入控件
            TxtUrl.IsEnabled = !busy;
            TxtUser.IsEnabled = !busy;
            TxtPassword.IsEnabled = !busy;
            TxtRemoteDir.IsEnabled = !busy;
            TxtRemoteName.IsEnabled = !busy;
            BtnSaveConfig.IsEnabled = !busy;
            BtnTestConn.IsEnabled = !busy;
            BtnUpload.IsEnabled = !busy;
            BtnListRemote.IsEnabled = !busy;
            BtnRestoreRemote.IsEnabled = !busy && RemoteGrid.Items.Count > 0;
            BtnDeleteRemote.IsEnabled = !busy && RemoteGrid.Items.Count > 0;
        }

        /// <summary>
        /// 在后台线程执行一个耗时/网络操作，界面显示进度与状态。
        /// quiet = true 时不动 TxtDavStatus，用于不打断主操作结果的附带刷新。
        /// </summary>
        private void RunBusy(string initialStatus, Func<CancellationToken, string> operation, Action afterSuccess = null, bool quiet = false)
        {
            if (_busy || _isClosed || !IsLoaded)
            {
                return;
            }

            var localCts = new CancellationTokenSource();
            CancellationToken ct = localCts.Token;
            _cts = localCts;
            SetBusy(true, quiet ? null : initialStatus);

            Task<string> task = Task.Run(() => operation(ct));

            task.ContinueWith(t =>
            {
                try
                {
                    if (_isClosed)
                    {
                        return;
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_isClosed || !IsLoaded)
                        {
                            return;
                        }

                        if (ReferenceEquals(_cts, localCts))
                        {
                            _cts = null;
                        }

                        SetBusy(false, null);

                        if (t.IsFaulted || t.IsCanceled)
                        {
                            Exception ex = t.Exception?.GetBaseException()
                                ?? (t.IsCanceled ? (Exception)new OperationCanceledException() : new Exception("未知错误"));

                            if (ex is OperationCanceledException)
                            {
                                TxtDavStatus.Text = "操作已取消";
                                return;
                            }

                            if (!quiet)
                            {
                                TxtDavStatus.Text = "操作失败";
                            }

                            MessageWindow.Show(this, ex.Message, "操作失败", MessageTone.Danger);
                            return;
                        }

                        if (!quiet)
                        {
                            TxtDavStatus.Text = t.Result ?? string.Empty;
                        }

                        afterSuccess?.Invoke();
                    }));
                }
                finally
                {
                    if (ReferenceEquals(_cts, localCts))
                    {
                        _cts = null;
                    }

                    try
                    {
                        localCts.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
            }, TaskScheduler.Default);
        }

        private static void TryDeleteFileQuietly(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void OnCancelOperation(object sender, RoutedEventArgs e)
        {
            try
            {
                _cts?.Cancel();
                TxtDavStatus.Text = "正在取消…";
            }
            catch (ObjectDisposedException)
            {
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
                bool result = MessageWindow.Confirm(this, "有一个正在进行的后台操作。\n\n确定要中止并关闭吗？",
                    "操作进行中", MessageTone.Danger, MessageButtons.OkCancel);

                if (!result)
                {
                    e.Cancel = true;
                    return;
                }

                try
                {
                    _cts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception)
                {
                }
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;

            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception)
            {
            }

            base.OnClosed(e);
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
