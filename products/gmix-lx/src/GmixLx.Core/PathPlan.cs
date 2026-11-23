using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GmixLx.Core
{
    /// <summary>一条路径的计划定义（SRS §4.2 FR-011）。</summary>
    public sealed class PathPlanEntry
    {
        /// <summary>`INdd_OUTdd`。</summary>
        public string pathId { get; set; }

        /// <summary>8×8 输入通道 1..8，对应 `SETSW` 的输入位次。</summary>
        [JsonPropertyName("in")]
        public int inChannel { get; set; }

        /// <summary>8×8 输出通道 1..8，0 表示断开（计划中不允许 0）。</summary>
        public int outChannel { get; set; }

        /// <summary>1×8 前级路由通道 1..8。</summary>
        public int route1x8 { get; set; }

        /// <summary>该路径扫描次数 N ≥ 1；N&gt;1 时生成 `AVG/`。</summary>
        public int averageCount { get; set; }

        /// <summary>期望的 DUT 端口标识；峰位置窗口规则待 TBD-D05b，暂只登记。</summary>
        public string expectedPort { get; set; }
    }

    /// <summary>路径计划（SRS §4.2）。重测批次持有子集计划，完整批次为 64 条。</summary>
    public sealed class PathPlan
    {
        public string planVersion { get; set; }

        public PathPlanEntry[] entries { get; set; }

        /// <summary>按 i=1..8、j=1..8 生成 64 条路径；`route1x8` 默认等于输入通道。</summary>
        public static PathPlan CreateFull(string planVersion, int averageCount)
        {
            if (averageCount < 1)
                throw new ArgumentOutOfRangeException(nameof(averageCount), averageCount, "N 必须 ≥ 1");

            var entries = new List<PathPlanEntry>(64);
            for (int i = 1; i <= 8; i++)
            {
                for (int j = 1; j <= 8; j++)
                {
                    entries.Add(new PathPlanEntry
                    {
                        pathId = DetectionNaming.PathId(i, j),
                        inChannel = i,
                        outChannel = j,
                        route1x8 = i,
                        averageCount = averageCount
                    });
                }
            }

            return new PathPlan { planVersion = planVersion, entries = entries.ToArray() };
        }

        /// <summary>
        /// 计划校验（FR-012）：无缺失、无重复、参数越界、N ≥ 1。
        /// `requireFull` 为 true 时要求恰好 64 条且覆盖 8×8 直积（完整批次）。
        /// </summary>
        public List<string> Validate(bool requireFull)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(planVersion)) errors.Add("planVersion 为空");
            if (entries == null || entries.Length == 0)
            {
                errors.Add("路径计划为空");
                return errors;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (PathPlanEntry entry in entries)
            {
                if (entry == null)
                {
                    errors.Add("路径计划含空条目");
                    continue;
                }

                if (string.IsNullOrEmpty(entry.pathId))
                {
                    errors.Add("pathId 为空");
                }
                else if (!seen.Add(entry.pathId))
                {
                    errors.Add("pathId 重复: " + entry.pathId);
                }

                if (entry.inChannel < 1 || entry.inChannel > 8)
                    errors.Add(entry.pathId + " 输入通道越界: " + entry.inChannel);
                if (entry.outChannel < 1 || entry.outChannel > 8)
                    errors.Add(entry.pathId + " 输出通道越界: " + entry.outChannel);
                if (entry.route1x8 < 1 || entry.route1x8 > 8)
                    errors.Add(entry.pathId + " 1×8 路由越界: " + entry.route1x8);
                if (entry.averageCount < 1)
                    errors.Add(entry.pathId + " 扫描次数 N 必须 ≥ 1: " + entry.averageCount);

                int inCh;
                int outCh;
                if (!string.IsNullOrEmpty(entry.pathId) && DetectionNaming.TryParsePathId(entry.pathId, out inCh, out outCh))
                {
                    if (inCh != entry.inChannel || outCh != entry.outChannel)
                        errors.Add(entry.pathId + " 与 in/out 不一致: " + entry.inChannel + "/" + entry.outChannel);
                }
                else if (!string.IsNullOrEmpty(entry.pathId))
                {
                    errors.Add("pathId 格式非法: " + entry.pathId);
                }
            }

            if (requireFull)
            {
                if (entries.Length != 64)
                    errors.Add("完整批次路径数必须为 64，实际 " + entries.Length);
                List<string> missing = DetectionNaming.FindMissingPaths(entries);
                if (missing.Count > 0)
                    errors.Add("路径缺失: " + string.Join(",", missing.ToArray()));
            }

            return errors;
        }
    }
}
