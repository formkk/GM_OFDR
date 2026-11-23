using System;
using System.Collections.Generic;
using System.IO;
using GmixLx.Core;
using Gmix.Data;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 组流水线（SDD §5.2）：staging 写入、`.ready` 校验、`group.json` 生成、原子提交。
    /// 复用 `Gmix.Core.GroupStore`，批次目录即 L2 数据根。
    /// </summary>
    public sealed class GroupPipeline
    {
        public GroupStore Store { get; }

        public GroupPipeline(GroupStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            Store = store;
        }

        public string StagingGroupDir(string groupId)
        {
            return Store.StagingGroupDir(groupId);
        }

        public string FinalGroupDir(string groupId)
        {
            return Store.FinalGroupDir(groupId);
        }

        /// <summary>
        /// 校验并提交一个组。校验项：每份扫描的 `.ready` 与文件清单、N&gt;1 时 `AVG/summary.json` 存在。
        /// 校验失败抛 <see cref="GroupConsistencyException"/>，staging 保留现场供排查。
        /// </summary>
        public void Commit(string groupId, PathPlanEntry entry, GroupProduction production, string configHash, string calibrationId, DateTime createdAtUtc)
        {
            if (string.IsNullOrEmpty(groupId)) throw new ArgumentException("groupId 为空", nameof(groupId));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (production == null) throw new ArgumentNullException(nameof(production));

            string staging = Store.StagingGroupDir(groupId);
            if (!Directory.Exists(staging))
                throw new GroupConsistencyException("staging 目录不存在: " + groupId);

            var errors = new List<string>();
            for (int k = 1; k <= entry.averageCount; k++)
            {
                string scanDir = Path.Combine(staging, DetectionNaming.ScanFolder(k));
                errors.AddRange(GroupStore.ValidateScan(scanDir));
            }

            if (!string.IsNullOrEmpty(production.AvgScanId))
            {
                string avgSummary = Path.Combine(staging, "AVG", "summary.json");
                if (!File.Exists(avgSummary)) errors.Add("AVG/summary.json 缺失");
            }

            if (errors.Count > 0)
                throw new GroupConsistencyException("组校验未通过: " + groupId + " — " + string.Join(";", errors.ToArray()));

            GroupDocument document = GroupAssembler.BuildDocument(
                groupId,
                createdAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                entry.averageCount,
                production.Acquisition,
                calibrationId,
                configHash,
                production.ScanIds,
                production.Rejected,
                production.Alignment,
                production.AvgScanId);

            JsonStore.Write(Path.Combine(staging, "group.json"), document);
            Store.Commit(groupId);
        }
    }
}
