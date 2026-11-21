using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DataManager.Services
{
    /// <summary>
    /// 主数据库文件已替换，但替换后清理残留 -wal/-shm/-journal 伴随文件失败时抛出的异常。
    /// 调用方据此可明确区分“替换前中止”与“替换后伴随文件清理失败”，优先回滚备份。
    /// </summary>
    public sealed class DatabaseSidecarCleanupException : IOException
    {
        public DatabaseSidecarCleanupException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    /// 本地备份：在软件目录生成带时间戳的 sqlite 备份文件。
    /// 以及 WebDAV 备份/恢复所依赖的文件替换逻辑。
    /// </summary>
    public static class BackupService
    {
        private static readonly TimeSpan StalePartialAge = TimeSpan.FromHours(1);

        private static readonly HashSet<string> WindowsReservedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static readonly string[] SqliteSidecarSuffixes = new[] { "-wal", "-shm", "-journal" };

        /// <summary>
        /// 生成远端备份文件名：<c>前缀_yyyyMMdd_HHmmss.sqlite</c>。
        ///
        /// 每次上传都用新名字，就不用依赖服务器正确实现 PUT 覆盖语义
        /// （有些 WebDAV 服务对已存在文件不会真正覆盖），历史版本也天然保留。
        /// </summary>
        public static string RemoteBackupFileName(string prefix)
        {
            string safe = SafeFileName(prefix);
            if (string.IsNullOrEmpty(safe))
            {
                safe = "DataManager";
            }

            return safe + "_" +
                   DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) +
                   ".sqlite";
        }

        /// <summary>
        /// 从备份文件名里解析出时间（用于列表倒序）；解析不出来时返回 <paramref name="fallback"/>。
        /// </summary>
        public static DateTime ParseBackupTime(string fileName, DateTime fallback)
        {
            if (!string.IsNullOrEmpty(fileName))
            {
                // 形如 “DataManager_20261009_233045.sqlite” 或 “DataManager_20261009_233045_123.sqlite”
                string name = Path.GetFileNameWithoutExtension(fileName);
                string[] parts = name.Split('_');
                for (int i = parts.Length - 1; i >= 1; i--)
                {
                    if (parts[i].Length == 6 && parts[i - 1].Length == 8)
                    {
                        string stamp = parts[i - 1] + "_" + parts[i];
                        DateTime parsed;
                        if (DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out parsed))
                        {
                            return parsed;
                        }
                    }
                }
            }

            return fallback;
        }

        /// <summary>
        /// 只保留文件名，去掉任何目录成分、控制字符与非法字符，并规避 Windows 保留设备名。
        /// </summary>
        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            string value = name.Trim().Replace('\\', '/');
            int slash = value.LastIndexOf('/');
            if (slash >= 0)
            {
                value = value.Substring(slash + 1);
            }

            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsControl(c))
                {
                    continue;
                }

                if (c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }

            string cleaned = sb.ToString().Trim().TrimEnd('.');
            if (cleaned.Length == 0 || cleaned == "." || cleaned == "..")
            {
                return string.Empty;
            }

            if (IsWindowsReservedDeviceName(cleaned))
            {
                cleaned = "_" + cleaned;
            }

            return cleaned;
        }

        /// <summary>判断文件名（不含扩展名或首段）是否为 Windows 保留设备名。</summary>
        private static bool IsWindowsReservedDeviceName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            int dot = fileName.IndexOf('.');
            string stem = (dot >= 0 ? fileName.Substring(0, dot) : fileName).Trim();
            return WindowsReservedDeviceNames.Contains(stem);
        }

        /// <summary>生成不与现有文件冲突的备份文件路径，冲突时追加毫秒与序号。</summary>
        private static string AllocateUniqueBackupPath(string directory, string prefix)
        {
            string safePrefix = SafeFileName(prefix);
            if (string.IsNullOrEmpty(safePrefix))
            {
                safePrefix = "backup";
            }

            DateTime now = DateTime.Now;
            string baseStamp = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string candidate = Path.Combine(directory, safePrefix + "_" + baseStamp + ".sqlite");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            string msStamp = now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            candidate = Path.Combine(directory, safePrefix + "_" + msStamp + ".sqlite");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            for (int seq = 1; seq < 10000; seq++)
            {
                candidate = Path.Combine(
                    directory,
                    safePrefix + "_" + msStamp + "_" + seq.ToString("D2", CultureInfo.InvariantCulture) + ".sqlite");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(
                directory,
                safePrefix + "_" + msStamp + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".sqlite");
        }

        /// <summary>
        /// 生成一份本地备份，返回备份文件的完整路径。
        /// </summary>
        public static string CreateLocalBackup(Data.SqliteConnection db, string prefix)
        {
            if (db == null)
            {
                throw new ArgumentNullException(nameof(db));
            }

            AppPaths.EnsureCreated();
            CleanStalePartials();
            string target = AllocateUniqueBackupPath(AppPaths.BackupDirectory, prefix ?? "backup");
            db.BackupTo(target);
            return target;
        }

        /// <summary>
        /// 清理历史失败留下的 “*.sqlite.partial” 与 “*.replace” 残留文件。
        /// 仅删除最后修改时间超过 1 小时的残留，避免误删进行中的备份或上传快照。
        /// </summary>
        public static int CleanStalePartials()
        {
            int removed = 0;
            DateTime cutoffUtc = DateTime.UtcNow - StalePartialAge;

            foreach (string directory in new[] { AppPaths.BackupDirectory, AppPaths.RestoreStageDirectory, AppPaths.DataDirectory })
            {
                string[] files;
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    files = Directory.GetFiles(directory, "*.partial")
                        .Concat(Directory.GetFiles(directory, "*.replace"))
                        .ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    try
                    {
                        DateTime lastWriteUtc = File.GetLastWriteTimeUtc(file);
                        if (lastWriteUtc > cutoffUtc)
                        {
                            // 仍在活跃窗口内，跳过以防误删正在进行的写入
                            continue;
                        }

                        File.Delete(file);
                        removed++;
                    }
                    catch (IOException)
                    {
                        // 文件被占用时跳过
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }

            return removed;
        }

        /// <summary>列出本地备份文件（按时间倒序）。</summary>
        public static List<FileInfo> ListLocalBackups()
        {
            AppPaths.EnsureCreated();
            var dir = new DirectoryInfo(AppPaths.BackupDirectory);
            return dir.GetFiles("*.sqlite")
                      .OrderByDescending(f => f.LastWriteTimeUtc)
                      .ToList();
        }

        /// <summary>
        /// 按保留策略清理本地备份。keepCount 为 0 表示保留全部。
        /// 优先保护近期的 <c>before_restore_*.sqlite</c> 不被常规 <c>startup_</c> 备份冲掉。
        /// 返回被删除的文件数。
        /// </summary>
        public static int PruneLocalBackups(int keepCount)
        {
            if (keepCount <= 0)
            {
                return 0;
            }

            List<FileInfo> files = ListLocalBackups();
            if (files.Count <= keepCount)
            {
                return 0;
            }

            var keepSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 若 keepCount >= 2，优先为最近的恢复前备份（before_restore_*.sqlite）保留最多 3 个名额，
            // 同时至少留 1 个名额给最新的常规/手动备份，避免被大量 startup_ 备份冲掉。
            if (keepCount >= 2)
            {
                int maxProtectedRestore = Math.Min(3, keepCount - 1);
                int protectedCount = 0;
                for (int i = 0; i < files.Count && protectedCount < maxProtectedRestore; i++)
                {
                    if (files[i].Name.StartsWith("before_restore_", StringComparison.OrdinalIgnoreCase))
                    {
                        keepSet.Add(files[i].FullName);
                        protectedCount++;
                    }
                }
            }

            // 剩余名额按时间倒序补齐
            for (int i = 0; i < files.Count && keepSet.Count < keepCount; i++)
            {
                keepSet.Add(files[i].FullName);
            }

            int removed = 0;
            for (int i = 0; i < files.Count; i++)
            {
                if (keepSet.Contains(files[i].FullName))
                {
                    continue;
                }

                try
                {
                    files[i].Delete();
                    removed++;
                }
                catch (IOException)
                {
                    // 文件被占用时跳过
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return removed;
        }

        /// <summary>
        /// 用指定的 sqlite 文件替换当前数据库。调用方需保证已关闭数据库连接，
        /// 并在成功后重启软件。
        /// 替换前先清理旧的 -wal / -shm / -journal（失败则中止替换保护主库），
        /// 先写到同目录的临时文件再 Move 覆盖，替换后再次清理伴随文件。
        /// </summary>
        public static void ReplaceDatabase(string sourceSqlitePath)
        {
            if (!File.Exists(sourceSqlitePath))
            {
                throw new FileNotFoundException("用于恢复的数据库文件不存在。", sourceSqlitePath);
            }

            AppPaths.EnsureCreated();

            string destination = AppPaths.DatabaseFile;
            string tmp = destination + ".replace";
            bool completed = false;

            // 替换前先尝试清理旧的 -wal / -shm / -journal；若仍被占用则中止替换以保护主库
            foreach (string suffix in SqliteSidecarSuffixes)
            {
                string side = destination + suffix;
                if (File.Exists(side))
                {
                    try
                    {
                        File.Delete(side);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new IOException(
                            "替换前无法清理旧数据库伴随文件（" + Path.GetFileName(side) + "），已中止替换以保护当前数据库：" + ex.Message,
                            ex);
                    }
                }
            }

            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }

                File.Copy(sourceSqlitePath, tmp, true);
                File.Move(tmp, destination, true);
                completed = true;

                // 主文件换成新库后再清一次伴随文件，避免旧日志残留套到新库上
                foreach (string suffix in SqliteSidecarSuffixes)
                {
                    string side = destination + suffix;
                    if (File.Exists(side))
                    {
                        try
                        {
                            File.Delete(side);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            throw new DatabaseSidecarCleanupException(
                                "主数据库文件已替换，但清理残留伴随文件（" + Path.GetFileName(side) + "）失败：" + ex.Message,
                                ex);
                        }
                    }
                }
            }
            finally
            {
                if (!completed && File.Exists(tmp))
                {
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// 使用 SQLite 在线备份 API 把当前数据库备份成临时一致快照，返回路径（用于恢复前暂存）。
        /// </summary>
        public static string StageCurrentDatabase(Data.SqliteConnection db)
        {
            if (db == null)
            {
                throw new ArgumentNullException(nameof(db));
            }

            AppPaths.EnsureCreated();
            Directory.CreateDirectory(AppPaths.RestoreStageDirectory);
            string staged = AllocateUniqueBackupPath(AppPaths.RestoreStageDirectory, "restore_stage");
            db.BackupTo(staged);
            return staged;
        }

        /// <summary>
        /// 校验一个文件是否为本软件可用的 SQLite 数据库。
        /// 校验完整 16 字节文件头、以只读方式打开执行完整性检查，并核验核心元数据表存在。
        /// </summary>
        public static bool IsValidSqlite(string path, out string reason)
        {
            reason = null;

            try
            {
                if (!File.Exists(path))
                {
                    reason = "文件不存在。";
                    return false;
                }

                var info = new FileInfo(path);
                if (info.Length < 100)
                {
                    reason = "文件过小，不是有效的 SQLite 数据库。";
                    return false;
                }

                // SQLite 文件头固定为 16 字节："SQLite format 3\0"
                byte[] header = new byte[16];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int read = fs.Read(header, 0, 16);
                    if (read < 16)
                    {
                        reason = "文件头不完整。";
                        return false;
                    }
                }

                string magic = Encoding.ASCII.GetString(header, 0, 15);
                if (!string.Equals(magic, "SQLite format 3", StringComparison.Ordinal) || header[15] != 0)
                {
                    reason = "不是 SQLite 数据库文件（文件头不匹配）。";
                    return false;
                }

                // 以只读方式打开，避免对备份文件产生写入或生成伴随文件
                using (var probe = new Data.SqliteConnection())
                {
                    probe.OpenReadOnly(path);
                    string ok = probe.Scalar("PRAGMA quick_check");
                    if (!string.Equals(ok, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "数据库完整性检查未通过：" + (string.IsNullOrEmpty(ok) ? "返回空结果" : ok);
                        return false;
                    }

                    long metaCount = probe.ScalarLong(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('meta_table', 'meta_column')");
                    if (metaCount < 2)
                    {
                        reason = "该 SQLite 文件不是本软件的数据库（缺少 meta_table 或 meta_column 表）。";
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = "无法读取数据库文件：" + ex.Message;
                return false;
            }
        }

        /// <summary>人类可读的文件大小。</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            }

            if (bytes < 1024L * 1024 * 1024)
            {
                return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            }

            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }
    }
}
