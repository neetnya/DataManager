using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DataManager.Services
{
    /// <summary>
    /// 所有软件数据（数据库、配置、备份）统一存放于软件目录下。
    /// </summary>
    public static class AppPaths
    {
        private static readonly object BackupNameGate = new object();
        private static string _lastBackupStamp;
        private static int _backupSeq;

        static AppPaths()
        {
            // 以可执行文件所在目录为“软件目录”
            string exeDir = AppContext.BaseDirectory;
            Root = exeDir;

            DataDirectory = Path.Combine(Root, "Data");
            BackupDirectory = Path.Combine(Root, "Backup");
            DatabaseFile = Path.Combine(DataDirectory, "data.sqlite");
            LogFile = Path.Combine(DataDirectory, "app.log");
            SettingsFile = Path.Combine(Root, "config.json");
            RestoreStageDirectory = Path.Combine(Root, "Data", "restore");
        }

        /// <summary>软件目录。</summary>
        public static string Root { get; }

        /// <summary>数据目录。</summary>
        public static string DataDirectory { get; }

        /// <summary>本地备份目录（带时间戳的 sqlite 备份）。</summary>
        public static string BackupDirectory { get; }

        /// <summary>主数据库文件。</summary>
        public static string DatabaseFile { get; }

        /// <summary>滚动文本日志文件。</summary>
        public static string LogFile { get; }

        /// <summary>本地配置文件。</summary>
        public static string SettingsFile { get; }

        /// <summary>恢复备份时的临时目录。</summary>
        public static string RestoreStageDirectory { get; }

        /// <summary>确保必要目录存在。</summary>
        public static void EnsureCreated()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(BackupDirectory);
        }

        /// <summary>生成一个带时间戳的备份文件名（不含目录），清洗非法字符并避免同秒重名。</summary>
        public static string BackupFileName(string prefix)
        {
            string safe = SanitizePrefix(prefix);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

            int seq;
            lock (BackupNameGate)
            {
                if (string.Equals(_lastBackupStamp, stamp, StringComparison.Ordinal))
                {
                    _backupSeq++;
                    seq = _backupSeq;
                }
                else
                {
                    _lastBackupStamp = stamp;
                    _backupSeq = 0;
                    seq = 0;
                }
            }

            if (seq == 0)
            {
                return safe + "_" + stamp + ".sqlite";
            }

            return safe + "_" + stamp + "_" + seq.ToString("D2", CultureInfo.InvariantCulture) + ".sqlite";
        }

        private static string SanitizePrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return "backup";
            }

            string trimmed = prefix.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(trimmed.Length);
            foreach (char ch in trimmed)
            {
                bool bad = ch == '/' || ch == '\\' || ch == ':' || ch == '*' ||
                           ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|';
                if (!bad)
                {
                    for (int i = 0; i < invalid.Length; i++)
                    {
                        if (invalid[i] == ch)
                        {
                            bad = true;
                            break;
                        }
                    }
                }

                sb.Append(bad ? '_' : ch);
            }

            string result = sb.ToString().Trim();
            return result.Length == 0 ? "backup" : result;
        }
    }

    /// <summary>极简滚动文本日志（写入 Data\app.log，单文件上限 2MB）。</summary>
    public static class AppLog
    {
        private const long MaxFileBytes = 2L * 1024L * 1024L;
        private static readonly object Gate = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", message, ex);
        }

        public static void Write(string level, string message, Exception ex = null)
        {
            try
            {
                string logPath = AppPaths.LogFile;
                string dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var sb = new StringBuilder(160);
                sb.Append('[')
                  .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                  .Append("] [")
                  .Append(string.IsNullOrEmpty(level) ? "INFO" : level)
                  .Append("] ")
                  .AppendLine(message ?? string.Empty);

                if (ex != null)
                {
                    sb.AppendLine(ex.ToString());
                }

                lock (Gate)
                {
                    try
                    {
                        var info = new FileInfo(logPath);
                        if (info.Exists && info.Length >= MaxFileBytes)
                        {
                            File.Move(logPath, logPath + ".1", true);
                        }
                    }
                    catch (Exception)
                    {
                        // 滚动失败时继续尝试追加
                    }

                    File.AppendAllText(logPath, sb.ToString(), Utf8NoBom);
                }
            }
            catch (Exception)
            {
                // 日志写入失败不应影响主流程
            }
        }
    }
}
