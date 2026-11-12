using System;
using System.Collections.Generic;
using System.Globalization;

namespace Detection.Core
{
    /// <summary>
    /// 命名规则（SRS §6.6）：批次 `BATCH-yyyyMMdd-HHmmss-NNN`、组 `GR-yyyyMMdd-HHmm-NNN`
    /// 均为 UTC；路径 `INdd_OUTdd`。序号在批次内连续递增，不因分钟变化重置。
    /// </summary>
    public static class DetectionNaming
    {
        public const string BatchPrefix = "BATCH-";
        public const string GroupPrefix = "GR-";

        /// <summary>批次 ID：年月日-时分秒-三位序号（UTC）。</summary>
        public static string BatchId(DateTime utc, int sequence)
        {
            if (sequence < 0 || sequence > 999)
                throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "批次序号必须在 0..999");
            return BatchPrefix + utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + sequence.ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>组 ID：年月日-时分-三位序号（UTC）。</summary>
        public static string GroupId(DateTime utc, int sequence)
        {
            if (sequence < 1 || sequence > 999)
                throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "组序号必须在 1..999");
            return GroupPrefix + utc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "-" + sequence.ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>路径 ID：`IN01_OUT01`。</summary>
        public static string PathId(int inChannel, int outChannel)
        {
            return "IN" + inChannel.ToString("00", CultureInfo.InvariantCulture)
                + "_OUT" + outChannel.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>解析路径 ID；格式与数值不一致时返回 false。</summary>
        public static bool TryParsePathId(string pathId, out int inChannel, out int outChannel)
        {
            inChannel = 0;
            outChannel = 0;
            if (string.IsNullOrEmpty(pathId) || pathId.Length != 10) return false;
            if (!pathId.StartsWith("IN", StringComparison.Ordinal)) return false;
            if (pathId[4] != '_') return false;
            if (!pathId.Substring(5).StartsWith("OUT", StringComparison.Ordinal)) return false;
            return int.TryParse(pathId.Substring(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out inChannel)
                && int.TryParse(pathId.Substring(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out outChannel);
        }

        /// <summary>批次内组序号 → 组 ID 的数字部分；供路径顺序与组序号一一对应时使用。</summary>
        public static string ScanFolder(int repeatIndex)
        {
            if (repeatIndex < 1 || repeatIndex > 999)
                throw new ArgumentOutOfRangeException(nameof(repeatIndex), repeatIndex, "repeatIndex 必须在 1..999");
            return "R" + repeatIndex.ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>批次目录名 → 批次 ID；不含 `BATCH-` 前缀或格式不符返回 null。</summary>
        public static string BatchIdOfDirectory(string directoryName)
        {
            if (string.IsNullOrEmpty(directoryName)) return null;
            if (!directoryName.StartsWith(BatchPrefix, StringComparison.Ordinal)) return null;
            return directoryName;
        }

        /// <summary>校验 64 条路径是否覆盖 8×8 直积，返回缺失项描述；空表示完整。</summary>
        public static List<string> FindMissingPaths(IEnumerable<PathPlanEntry> entries)
        {
            var seen = new bool[9, 9];
            if (entries != null)
            {
                foreach (PathPlanEntry entry in entries)
                {
                    if (entry == null) continue;
                    if (entry.inChannel < 1 || entry.inChannel > 8) continue;
                    if (entry.outChannel < 1 || entry.outChannel > 8) continue;
                    seen[entry.inChannel, entry.outChannel] = true;
                }
            }

            var missing = new List<string>();
            for (int i = 1; i <= 8; i++)
                for (int j = 1; j <= 8; j++)
                    if (!seen[i, j]) missing.Add(PathId(i, j));
            return missing;
        }
    }
}
