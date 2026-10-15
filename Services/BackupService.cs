using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DataManager.Services
{
    /// <summary>
    /// 本地备份：在软件目录生成带时间戳的 sqlite 备份文件。
    /// 以及 WebDAV 备份/恢复所依赖的文件替换逻辑。
    /// </summary>
    public static class BackupService
    {
        /// <summary>
        /// 生成远端备份文件名：<c>前缀_yyyyMMdd_HHmmss.sqlite</c>。
        ///
        /// 每次上传都用新名字，就不用依赖服务器正确实现 PUT 覆盖语义
        /// （有些 WebDAV 服务对已存在文件不会真正覆盖），历史版本也天然保留。
        /// </summary>
        public static string RemoteBackupFileName(string prefix)
        {
            string safe = string.IsNullOrWhiteSpace(prefix) ? "DataManager" : prefix.Trim();

            // 去掉文件名里不允许出现在远端路径的字符
            foreach (char bad in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
            {
                safe = safe.Replace(bad, '_');
            }

            return safe + "_" +
                   DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) +
                   ".sqlite";
        }

        /// <summary>
        /// 从备份文件名里解析出时间（用于列表倒序）；解析不出来时返回 <see cref="DateTime.MinValue"/>。
        /// </summary>
        public static DateTime ParseBackupTime(string fileName, DateTime fallback)
        {
            if (!string.IsNullOrEmpty(fileName))
            {
                // 形如 “DataManager_20261009_233045.sqlite”
                string name = Path.GetFileNameWithoutExtension(fileName);
                int underscore = name.LastIndexOf('_');
                if (underscore > 0)
                {
                    int previous = name.LastIndexOf('_', underscore - 1);
                    if (previous > 0)
                    {
                        string stamp = name.Substring(previous + 1, underscore - previous - 1) + "_" +
                                       name.Substring(underscore + 1);
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
        /// 只保留文件名，去掉任何目录成分，避免把用户输入当成远端路径使用。
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

            foreach (char bad in new[] { ':', '*', '?', '"', '<', '>', '|' })
            {
                value = value.Replace(bad, '_');
            }

            return value.Trim();
        }

        /// <summary>
        /// 生成一份本地备份，返回备份文件的完整路径。
        /// </summary>
        public static string CreateLocalBackup(Data.SqliteConnection db, string prefix)
        {
            AppPaths.EnsureCreated();
            CleanStalePartials();
            string target = Path.Combine(AppPaths.BackupDirectory, AppPaths.BackupFileName(prefix ?? "backup"));
            db.BackupTo(target);
            return target;
        }

        /// <summary>
        /// 清理历史失败留下的 “*.sqlite.partial” 残留文件。
        /// 这些 0 字节文件是早期版本备份失败时产生的，对用户没有意义。
        /// </summary>
        public static int CleanStalePartials()
        {
            int removed = 0;

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
        /// 返回被删除的文件数。
        /// </summary>
        public static int PruneLocalBackups(int keepCount)
        {
            if (keepCount <= 0)
            {
                return 0;
            }

            List<FileInfo> files = ListLocalBackups();
            int removed = 0;
            for (int i = keepCount; i < files.Count; i++)
            {
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
        /// 先写到同目录的临时文件再 Move 覆盖，避免中断时把主库写成半截。
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

            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }

                File.Copy(sourceSqlitePath, tmp, true);
                File.Move(tmp, destination, true);
                completed = true;

                // 主文件换成新库后再清旧的 -wal / -shm，避免把旧日志套到新文件上
                foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
                {
                    string side = destination + suffix;
                    if (File.Exists(side))
                    {
                        File.Delete(side);
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

        /// <summary>把数据库备份成临时文件，返回路径（用于恢复前的自动备份）。</summary>
        public static string StageCurrentDatabase()
        {
            AppPaths.EnsureCreated();
            string staged = Path.Combine(AppPaths.RestoreStageDirectory, AppPaths.BackupFileName("restore_stage"));
            Directory.CreateDirectory(AppPaths.RestoreStageDirectory);
            File.Copy(AppPaths.DatabaseFile, staged, true);
            return staged;
        }

        /// <summary>
        /// 校验一个文件是否为可用的 SQLite 数据库。
        /// 只读文件头并尝试打开，避免把损坏或非 SQLite 的文件恢复进主库。
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

                // SQLite 文件头固定为 "SQLite format 3\0"
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

                string magic = System.Text.Encoding.ASCII.GetString(header, 0, 15);
                if (!string.Equals(magic, "SQLite format 3", StringComparison.Ordinal))
                {
                    reason = "不是 SQLite 数据库文件（文件头不匹配）。";
                    return false;
                }

                // 再做一次完整性检查，确认能真正打开并读到结构
                using (var probe = new Data.SqliteConnection())
                {
                    probe.Open(path);
                    string ok = probe.Scalar("PRAGMA quick_check");
                    if (!string.IsNullOrEmpty(ok) && !string.Equals(ok, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "数据库完整性检查未通过：" + ok;
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
        public static string FormatSize(long bytes)        {
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
