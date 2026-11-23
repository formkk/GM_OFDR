using System;
using System.Diagnostics;
using System.Threading;
using GmixLx.Core;
using GmixLx.Vendor;
using Gmix.Data;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 单路径流水线（SDD §4.2）：切 1×8 → 切 8×8 → 稳定等待 → N 次扫描与组提交 → 指标与关注提示。
    /// 串口客户端可注入 null 表示不接设备（编排与存储自测），此时跳过切换步骤。
    /// </summary>
    public sealed class PathExecutor
    {
        readonly GroupPipeline _pipeline;
        readonly IGroupProducer _producer;
        readonly DetectionConfig _config;
        readonly Ocs1x8Client _ocs1x8;
        readonly Ocs8x8Client _ocs8x8;

        public PathExecutor(GroupPipeline pipeline, IGroupProducer producer, DetectionConfig config,
            Ocs1x8Client ocs1x8, Ocs8x8Client ocs8x8)
        {
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
            if (producer == null) throw new ArgumentNullException(nameof(producer));
            if (config == null) throw new ArgumentNullException(nameof(config));

            _pipeline = pipeline;
            _producer = producer;
            _config = config;
            _ocs1x8 = ocs1x8;
            _ocs8x8 = ocs8x8;
        }

        public PathResult Execute(PathPlanEntry entry, string groupId, int attempt, CalibrationStatus calibration,
            bool hasBaseline, ReferenceRange range, string configHash, string calibrationId)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(groupId)) throw new ArgumentException("groupId 为空", nameof(groupId));

            var result = new PathResult
            {
                pathId = entry.pathId,
                inChannel = entry.inChannel,
                outChannel = entry.outChannel,
                averageCount = entry.averageCount,
                attempt = attempt,
                scanIds = new string[0],
                flags = new string[0],
                notes = new PathNote[0]
            };

            var watch = Stopwatch.StartNew();
            try
            {
                SwitchOptics(entry, result);

                if (_config.settleMs < 0 || _config.settleMs > 500)
                    throw new InvalidOperationException("settleMs 超出配置范围 0~500 ms（SDD §8.1）");
                if (_config.settleMs > 0)
                    Thread.Sleep(_config.settleMs);

                GroupProduction production = _producer.Produce(_pipeline.StagingGroupDir(groupId), entry, entry.averageCount);
                if (production == null)
                    throw new InvalidOperationException("扫描产出端返回 null: " + entry.pathId);

                if (!production.Succeeded)
                {
                    _pipeline.Store.MarkFailed(groupId);
                    watch.Stop();
                    result.pathState = PathStateNames.Failed;
                    result.connectivity = ConnectivityNames.InvalidFrame;
                    result.validCount = 0;
                    result.durationMs = watch.ElapsedMilliseconds;
                    result.errorClass = (production.ErrorClass ?? ErrorClass.Measurement).ToString();
                    result.flags = FlagEvaluator.ToNames(FlagEvaluator.Evaluate(result, calibration, hasBaseline, range));
                    return result;
                }

                _pipeline.Commit(groupId, entry, production, configHash, calibrationId, DateTime.UtcNow);

                GroupMetrics metrics = MetricsReader.ReadGroup(_pipeline.FinalGroupDir(groupId), entry.averageCount);

                result.groupId = groupId;
                result.validCount = production.ValidCount;
                result.scanIds = production.ScanIds ?? new string[0];
                result.avgScanId = production.AvgScanId;
                result.connectivity = ResolveConnectivity(metrics);
                result.ilDb = metrics.IlDb;
                result.rlDb = metrics.RlDb;
                result.metricSource = metrics.MetricSource;
                result.evidence = metrics.Evidence;
                result.pathState = PathStateNames.Computed;

                watch.Stop();
                result.durationMs = watch.ElapsedMilliseconds;
                result.flags = FlagEvaluator.ToNames(FlagEvaluator.Evaluate(result, calibration, hasBaseline, range));
                return result;
            }
            catch (OcsException ex)
            {
                _pipeline.Store.MarkFailed(groupId);
                watch.Stop();
                result.pathState = PathStateNames.Failed;
                result.connectivity = ConnectivityNames.InvalidFrame;
                result.durationMs = watch.ElapsedMilliseconds;
                result.errorClass = ex.Class.ToString();
                result.flags = FlagEvaluator.ToNames(FlagEvaluator.Evaluate(result, calibration, hasBaseline, range));
                return result;
            }
            catch (GroupConsistencyException ex)
            {
                watch.Stop();
                result.pathState = PathStateNames.Failed;
                result.connectivity = ConnectivityNames.InvalidFrame;
                result.durationMs = watch.ElapsedMilliseconds;
                result.errorClass = ErrorClass.Measurement.ToString();
                result.flags = FlagEvaluator.ToNames(FlagEvaluator.Evaluate(result, calibration, hasBaseline, range));
                result.notes = new[] { new PathNote { text = ex.Message, @operator = "system", atUtc = DetectionStore.UtcNowIso() } };
                return result;
            }
        }

        void SwitchOptics(PathPlanEntry entry, PathResult result)
        {
            OcsStateSnapshot snapshot = null;

            if (_ocs1x8 != null)
            {
                Ocs1x8State state = _ocs1x8.SetOutput(entry.route1x8);
                snapshot = new OcsStateSnapshot { route1x8 = state.ToString() };
            }

            if (_ocs8x8 != null)
            {
                Ocs8x8State matrix = _ocs8x8.SetRoute(entry.inChannel, entry.outChannel);
                if (snapshot == null) snapshot = new OcsStateSnapshot();
                snapshot.matrix8x8 = matrix.ToString();
            }

            if (snapshot != null) result.ocsState = snapshot;
        }

        /// <summary>
        /// 连通性判定（SRS §4.5.1）：帧无效 → invalidFrame；无 `zeroPeak=false` 峰 → noPeak；
        /// 峰存在但指标未算出 → notComputed；否则 ok。
        /// </summary>
        static string ResolveConnectivity(GroupMetrics metrics)
        {
            if (!metrics.FrameValid) return ConnectivityNames.InvalidFrame;
            if (metrics.Evidence != null && metrics.Evidence.peakPresent == false) return ConnectivityNames.NoPeak;
            if (!metrics.IlDb.HasValue && !metrics.RlDb.HasValue) return ConnectivityNames.NotComputed;
            return ConnectivityNames.Ok;
        }
    }
}
