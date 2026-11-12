using System;
using System.Collections.Generic;
using Detection.Core;
using Gmix.Data;

namespace Detection.Orchestration
{
    /// <summary>批次恢复检查结果（SDD §4.3）。</summary>
    public sealed class RecoveryPlan
    {
        public bool Exists { get; set; }

        /// <summary>批次快照状态；非终态即视为未完成。</summary>
        public string BatchState { get; set; }

        /// <summary>可继续恢复。</summary>
        public bool Resumable { get; set; }

        public string[] PendingPathIds { get; set; }

        public string[] CompletedPathIds { get; set; }
    }

    /// <summary>
    /// 批次恢复（SDD §4.3）：识别未完成批次、废弃无 `.ready` 交接的 staging 半成品组。
    /// 已提交的组不重采；`attempt` 递增由 <see cref="BatchRunner.Resume"/> 负责。
    /// </summary>
    public sealed class RecoveryService
    {
        readonly DetectionStore _store;

        public RecoveryService(DetectionStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            _store = store;
        }

        public RecoveryPlan Inspect(string batchId)
        {
            var plan = new RecoveryPlan
            {
                PendingPathIds = new string[0],
                CompletedPathIds = new string[0]
            };

            DetectionBatch batch = _store.Read(batchId);
            if (batch == null) return plan;

            plan.Exists = true;
            plan.BatchState = batch.batchState;

            var pending = new List<string>();
            var completed = new List<string>();
            if (batch.paths != null)
            {
                foreach (PathResult path in batch.paths)
                {
                    if (path == null || string.IsNullOrEmpty(path.pathId)) continue;

                    if (string.Equals(path.pathState, PathStateNames.Computed, StringComparison.Ordinal)
                        || string.Equals(path.pathState, PathStateNames.Failed, StringComparison.Ordinal))
                    {
                        completed.Add(path.pathId);
                    }
                    else
                    {
                        pending.Add(path.pathId);
                    }
                }
            }

            plan.PendingPathIds = pending.ToArray();
            plan.CompletedPathIds = completed.ToArray();
            plan.Resumable = pending.Count > 0
                && !string.Equals(batch.batchState, BatchStateNames.Aborted, StringComparison.Ordinal)
                && !string.Equals(batch.batchState, BatchStateNames.Completed, StringComparison.Ordinal)
                && !string.Equals(batch.batchState, BatchStateNames.CompletedWithFailures, StringComparison.Ordinal);
            return plan;
        }

        /// <summary>删除批次内不含任何 `.ready` 的 staging 组（半成品整体废弃重采），返回删除数。</summary>
        public int DiscardIncompleteStaging(string batchId)
        {
            string batchDir = _store.BatchDirectory(batchId);
            if (!System.IO.Directory.Exists(batchDir)) return 0;

            var groupStore = new GroupStore(batchDir);
            return groupStore.CleanupStaging();
        }
    }
}
