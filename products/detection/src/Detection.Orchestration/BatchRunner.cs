using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Detection.Core;
using Detection.Vendor;
using Gmix.Data;

namespace Detection.Orchestration
{
    /// <summary>一次批次的启动参数。</summary>
    public sealed class BatchRunRequest
    {
        public string batchId { get; set; }

        public string operatorName { get; set; }

        public string station { get; set; }

        public BatchDeviceInfo deviceInfo { get; set; }

        public CalibrationStatus calibration { get; set; }

        public bool hasBaseline { get; set; }

        public ReferenceRange referenceRange { get; set; }

        public string calibrationId { get; set; }

        public string ofdrConfigHash { get; set; }

        public string retryOf { get; set; }

        public PathPlan plan { get; set; }
    }

    /// <summary>批次运行结果。</summary>
    public sealed class BatchRunResult
    {
        public DetectionBatch Batch { get; set; }
        public BatchState State { get; set; }
    }

    /// <summary>
    /// 批次状态机（SDD §4.1）：Idle → SelfCheck → Running ⇄ Paused → Completed | CompletedWithFailures | Aborted。
    /// 编排线程为全批次唯一写者；对 UI 只发事件。
    /// </summary>
    public sealed class BatchRunner
    {
        readonly DetectionStore _store;
        readonly PathExecutor _executor;
        readonly DetectionConfig _config;

        int _pauseRequested;
        int _abortRequested;
        readonly ManualResetEventSlim _pauseGate = new ManualResetEventSlim(false);

        public event Action<PathResult> PathCompleted;
        public event Action<BatchState> BatchStateChanged;
        public event Action<int, int> Progress;

        public BatchState CurrentState { get; private set; }

        public BatchRunner(DetectionStore store, GroupPipeline pipeline, IGroupProducer producer, DetectionConfig config,
            Ocs1x8Client ocs1x8, Ocs8x8Client ocs8x8)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
            if (config == null) throw new ArgumentNullException(nameof(config));

            _store = store;
            _config = config;
            _executor = new PathExecutor(pipeline, producer, config, ocs1x8, ocs8x8);
            CurrentState = BatchState.Idle;
        }

        public void Pause()
        {
            if (Interlocked.CompareExchange(ref _pauseRequested, 1, 0) == 0) _pauseGate.Reset();
        }

        public void Resume()
        {
            Interlocked.Exchange(ref _pauseRequested, 0);
            _pauseGate.Set();
        }

        public void Abort()
        {
            Interlocked.Exchange(ref _abortRequested, 1);
            _pauseGate.Set();
        }

        /// <summary>启动新批次。批次目录已存在时拒绝启动（FR-104）。</summary>
        public BatchRunResult Run(BatchRunRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrEmpty(request.batchId)) throw new ArgumentException("batchId 为空", nameof(request));
            if (request.plan == null) throw new ArgumentException("路径计划为空", nameof(request));

            List<string> planErrors = request.plan.Validate(request.retryOf == null);
            if (planErrors.Count > 0)
                throw new InvalidOperationException("路径计划非法: " + string.Join(";", planErrors.ToArray()));

            if (Directory.Exists(_store.BatchDirectory(request.batchId)))
                throw new InvalidOperationException("批次目录已存在，拒绝覆盖: " + request.batchId);

            var batch = new DetectionBatch
            {
                schemaVersion = DetectionBatch.SchemaVersion,
                batchId = request.batchId,
                batchState = BatchStateNames.Running,
                retryOf = request.retryOf,
                retestBatchIds = new string[0],
                planVersion = request.plan.planVersion,
                configHash = _config.ComputeConfigHash(),
                ofdrConfigHash = request.ofdrConfigHash,
                calibrationStatus = CalibrationStatusNames.Of(request.calibration),
                baselineId = request.hasBaseline ? _config.baselineId : null,
                baselineVersion = request.hasBaseline ? _config.baselineVersion : null,
                deviceInfo = request.deviceInfo,
                startedAt = DetectionStore.UtcNowIso(),
                @operator = request.operatorName,
                station = request.station,
                summary = new BatchSummaryInfo(),
                paths = CreatePendingPaths(request.plan)
            };

            batch.summary = BatchSummaryCalculator.Compute(batch.paths);
            _store.Write(batch);

            return RunInternal(batch, request, 0);
        }

        /// <summary>
        /// 恢复未完成批次（SDD §4.3）：已完成路径保持，未完成路径 attempt+1 后重做。
        /// 调用前应先由 <see cref="RecoveryService"/> 废弃不完整 staging。
        /// </summary>
        public BatchRunResult Resume(string batchId, BatchRunRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            DetectionBatch batch = _store.Read(batchId);
            if (batch == null) throw new InvalidOperationException("批次不存在: " + batchId);
            if (batch.paths == null || batch.paths.Length == 0) throw new InvalidOperationException("批次无路径记录: " + batchId);

            int startIndex = batch.paths.Length;
            for (int i = 0; i < batch.paths.Length; i++)
            {
                PathResult path = batch.paths[i];
                if (path == null) continue;
                if (IsTerminal(path.pathState)) continue;

                path.attempt++;
                path.pathState = PathStateNames.Pending;
                if (i < startIndex) startIndex = i;
            }

            batch.batchState = BatchStateNames.Running;
            _store.Write(batch);
            return RunInternal(batch, request, startIndex);
        }

        BatchRunResult RunInternal(DetectionBatch batch, BatchRunRequest request, int startIndex)
        {
            try
            {
                SetState(BatchState.SelfCheck);
                if (_config.settleMs <= 0)
                    throw new InvalidOperationException("settleMs 未配置：光路稳定时间未实测（TBD-O03），必须现场填写，禁止使用默认值");

                SetState(BatchState.Running);

                for (int index = startIndex; index < batch.paths.Length; index++)
                {
                    WaitWhilePaused();
                    if (Volatile.Read(ref _abortRequested) == 1) break;

                    PathPlanEntry entry = FindEntry(request.plan, batch.paths[index].pathId);
                    if (entry == null)
                    {
                        batch.paths[index].pathState = PathStateNames.NotAttempted;
                        continue;
                    }

                    string groupId = DetectionNaming.GroupId(DateTime.UtcNow, index + 1);
                    PathResult result = _executor.Execute(entry, groupId, batch.paths[index].attempt,
                        request.calibration, request.hasBaseline, request.referenceRange, batch.configHash, request.calibrationId);

                    batch.paths[index] = result;
                    batch.summary = BatchSummaryCalculator.Compute(batch.paths);
                    _store.Write(batch);

                    Action<PathResult> handler = PathCompleted;
                    if (handler != null) handler(result);

                    Action<int, int> progress = Progress;
                    if (progress != null) progress(batch.summary.completed + batch.summary.measurementFailed, batch.summary.total);
                }

                if (Volatile.Read(ref _abortRequested) == 1)
                {
                    SetState(BatchState.Aborting);
                    for (int i = 0; i < batch.paths.Length; i++)
                    {
                        if (!IsTerminal(batch.paths[i].pathState))
                            batch.paths[i].pathState = PathStateNames.NotAttempted;
                    }
                    batch.summary = BatchSummaryCalculator.Compute(batch.paths);
                    batch.batchState = BatchStateNames.Aborted;
                    batch.endedAt = DetectionStore.UtcNowIso();
                    _store.Write(batch);
                    _store.WriteIndexCsv(batch);
                    SetState(BatchState.Aborted);
                    return new BatchRunResult { Batch = batch, State = BatchState.Aborted };
                }

                batch.summary = BatchSummaryCalculator.Compute(batch.paths);
                BatchState terminal = BatchSummaryCalculator.ResolveTerminalState(batch.summary);
                batch.batchState = terminal == BatchState.Completed
                    ? BatchStateNames.Completed
                    : BatchStateNames.CompletedWithFailures;
                batch.endedAt = DetectionStore.UtcNowIso();
                _store.Write(batch);
                _store.WriteIndexCsv(batch);
                SetState(terminal);
                return new BatchRunResult { Batch = batch, State = terminal };
            }
            finally
            {
                _pauseGate.Set();
            }
        }

        void WaitWhilePaused()
        {
            if (Volatile.Read(ref _pauseRequested) == 0) return;

            SetState(BatchState.Paused);
            while (Volatile.Read(ref _pauseRequested) == 1 && Volatile.Read(ref _abortRequested) == 0)
                _pauseGate.Wait(100);

            if (Volatile.Read(ref _abortRequested) == 0) SetState(BatchState.Running);
        }

        void SetState(BatchState state)
        {
            CurrentState = state;
            Action<BatchState> handler = BatchStateChanged;
            if (handler != null) handler(state);
        }

        static bool IsTerminal(string pathState)
        {
            return string.Equals(pathState, PathStateNames.Computed, StringComparison.Ordinal)
                || string.Equals(pathState, PathStateNames.Failed, StringComparison.Ordinal)
                || string.Equals(pathState, PathStateNames.NotAttempted, StringComparison.Ordinal);
        }

        static PathResult[] CreatePendingPaths(PathPlan plan)
        {
            var paths = new PathResult[plan.entries.Length];
            for (int i = 0; i < plan.entries.Length; i++)
            {
                PathPlanEntry entry = plan.entries[i];
                paths[i] = new PathResult
                {
                    pathId = entry.pathId,
                    inChannel = entry.inChannel,
                    outChannel = entry.outChannel,
                    pathState = PathStateNames.Pending,
                    averageCount = entry.averageCount,
                    attempt = 1,
                    scanIds = new string[0],
                    flags = new string[0],
                    notes = new PathNote[0]
                };
            }
            return paths;
        }

        static PathPlanEntry FindEntry(PathPlan plan, string pathId)
        {
            if (plan == null || plan.entries == null) return null;
            foreach (PathPlanEntry entry in plan.entries)
            {
                if (entry != null && string.Equals(entry.pathId, pathId, StringComparison.Ordinal)) return entry;
            }
            return null;
        }
    }
}
