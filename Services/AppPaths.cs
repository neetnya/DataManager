using System;
using System.IO;

namespace DataManager.Services
{
    /// <summary>
    /// 所有软件数据（数据库、配置、备份）统一存放于软件目录下。
    /// </summary>
    public static class AppPaths
    {
        static AppPaths()
        {
            // 以可执行文件所在目录为“软件目录”
            string exeDir = AppContext.BaseDirectory;
            Root = exeDir;

            DataDirectory = Path.Combine(Root, "Data");
            BackupDirectory = Path.Combine(Root, "Backup");
            DatabaseFile = Path.Combine(DataDirectory, "data.sqlite");
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

        /// <summary>本地配置文件（WebDAV 地址/账号/密码，不加密）。</summary>
        public static string SettingsFile { get; }

        /// <summary>恢复备份时的临时目录。</summary>
        public static string RestoreStageDirectory { get; }

        /// <summary>确保必要目录存在。</summary>
        public static void EnsureCreated()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(BackupDirectory);
        }

        /// <summary>生成一个带时间戳的备份文件名（不含目录）。</summary>
        public static string BackupFileName(string prefix)
        {
            return prefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".sqlite";
        }
    }
}
