using System;
using System.Collections.Generic;
using System.IO;

namespace Gmix.Data
{
    /// <summary>组一致性问题（错误分类的第五类，ICD Part B §4）。</summary>
    public sealed class GroupConsistencyException : Exception
    {
        public GroupConsistencyException(string message) : base(message) { }
    }

    /// <summary>
    /// 组目录与原子提交（ICD Part B §7 / §7.1）：
    /// 采集进程只写 staging，编排进程是唯一提交者；`.ready` 不进入最终组目录。
    /// </summary>
    public sealed class GroupStore
    {
        public const string StagingFolderName = "_staging";
        public const string FailedSuffix = ".failed";
        public const string ReadyFileName = ".ready";
        public const string ReadyTempFileName = ".ready.tmp";

        public string DataRoot { get; }

        public GroupStore(string dataRoot)
        {
            if (string.IsNullOrEmpty(dataRoot))
                throw new ArgumentException("dataRoot 不能为空", nameof(dataRoot));
            DataRoot = Path.GetFullPath(dataRoot);
        }

        public string StagingGroupDir(string groupId)
        {
            return Path.Combine(DataRoot, StagingFolderName, groupId);
        }

        public string FinalGroupDir(string groupId)
        {
            return Path.Combine(DataRoot, groupId);
        }

        /// <summary>校验一次扫描的 `.ready` 与文件清单；返回错误列表，空表示通过。</summary>
        public static List<string> ValidateScan(string scanDir)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(scanDir) || !Directory.Exists(scanDir))
            {
                errors.Add("扫描目录不存在: " + scanDir);
                return errors;
            }

            string readyPath = Path.Combine(scanDir, ReadyFileName);
            if (!File.Exists(readyPath))
            {
                errors.Add("缺少交接标记 .ready: " + scanDir);
                return errors;
            }

            ReadyDocument doc;
            try
            {
                doc = JsonStore.Read<ReadyDocument>(readyPath);
            }
            catch (Exception ex)
            {
                errors.Add(".ready 解析失败: " + ex.Message);
                return errors;
            }

            if (doc == null || doc.files == null || doc.files.Length == 0)
            {
                errors.Add(".ready 文件清单为空");
                return errors;
            }

            foreach (ReadyFileInfo file in doc.files)
            {
                if (file == null || string.IsNullOrEmpty(file.path))
                {
                    errors.Add(".ready 含空文件项");
                    continue;
                }
                string path = Path.Combine(scanDir, file.path);
                if (!File.Exists(path))
                {
                    errors.Add("清单文件缺失: " + file.path);
                    continue;
                }
                long actualBytes = new FileInfo(path).Length;
                if (actualBytes != file.bytes)
                {
                    errors.Add("字节数不符: " + file.path + " 实际 " + actualBytes + " != 声明 " + file.bytes);
                    continue;
                }
                if (!string.IsNullOrEmpty(file.sha256))
                {
                    string actualHash = HashUtil.Sha256File(path);
                    if (!string.Equals(actualHash, file.sha256, StringComparison.OrdinalIgnoreCase))
                        errors.Add("sha256 不匹配: " + file.path);
                }
            }

            return errors;
        }

        /// <summary>
        /// 原子提交：删除全部 `.ready` 后把 staging 目录移动到最终位置。
        /// 目标已存在即拒绝（不覆盖）；staging 不存在即拒绝。
        /// </summary>
        public void Commit(string groupId)
        {
            string staging = StagingGroupDir(groupId);
            string finalDir = FinalGroupDir(groupId);

            if (!Directory.Exists(staging))
                throw new GroupConsistencyException("staging 目录不存在: " + groupId);
            if (Directory.Exists(finalDir))
                throw new GroupConsistencyException("目标组已存在，拒绝覆盖: " + groupId);

            foreach (string ready in Directory.GetFiles(staging, ReadyFileName, SearchOption.AllDirectories))
            {
                foreach (string temp in Directory.GetFiles(Path.GetDirectoryName(ready), ReadyTempFileName))
                    File.Delete(temp);
                File.Delete(ready);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalDir));
            Directory.Move(staging, finalDir);
        }

        /// <summary>把失败组改名为 `.failed`，保留现场（ICD §2.1）。</summary>
        public string MarkFailed(string groupId)
        {
            string staging = StagingGroupDir(groupId);
            string failed = staging + FailedSuffix;
            if (Directory.Exists(failed)) Directory.Delete(failed, true);
            if (Directory.Exists(staging)) Directory.Move(staging, failed);
            return failed;
        }

        /// <summary>
        /// 启动清理：删除不含任何 `.ready` 且未标记 `.failed` 的 staging 组目录。
        /// 含部分 `.ready` 的目录视为"采集中"，不删除。返回删除的组数。
        /// </summary>
        public int CleanupStaging()
        {
            string root = Path.Combine(DataRoot, StagingFolderName);
            if (!Directory.Exists(root)) return 0;

            int removed = 0;
            foreach (string dir in Directory.GetDirectories(root))
            {
                if (dir.EndsWith(FailedSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                bool hasReady = Directory.GetFiles(dir, ReadyFileName, SearchOption.AllDirectories).Length > 0;
                if (hasReady) continue;
                Directory.Delete(dir, true);
                removed++;
            }
            return removed;
        }
    }
}
