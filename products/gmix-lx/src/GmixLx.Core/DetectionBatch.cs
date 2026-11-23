using System.Text.Json.Serialization;

namespace GmixLx.Core
{
    /// <summary>`detection.json` 的批次级模型（字段一一对应 SRS §6.2）。</summary>
    public sealed class DetectionBatch
    {
        public const string SchemaVersion = "1.0";

        public string schemaVersion { get; set; }

        public string batchId { get; set; }

        /// <summary>`Completed / CompletedWithFailures / Aborted`；运行中允许 `Running` 快照（SDD §5.3）。</summary>
        public string batchState { get; set; }

        public string retryOf { get; set; }

        public string[] retestBatchIds { get; set; }

        public string planVersion { get; set; }

        public string configHash { get; set; }

        public string ofdrConfigHash { get; set; }

        public string calibrationStatus { get; set; }

        public string baselineId { get; set; }

        public int? baselineVersion { get; set; }

        public BatchDeviceInfo deviceInfo { get; set; }

        public string startedAt { get; set; }

        public string endedAt { get; set; }

        public string @operator { get; set; }

        public string station { get; set; }

        public BatchSummaryInfo summary { get; set; }

        public PathResult[] paths { get; set; }
    }

    /// <summary>批次设备信息（SRS §6.2）。</summary>
    public sealed class BatchDeviceInfo
    {
        /// <summary>
        /// OFDR 平台标识（SRS §6.2，取值见 <see cref="OfdrPlatformNames"/>）：
        /// `QT2144` = 已退役的 GM-IX G1；`YinXun2` = 现行映讯 GM-IX；`LGA50` = 昊衡 LGA50-D。
        /// </summary>
        public string ofdrPlatform { get; set; }

        public string ofdrSn { get; set; }
        public string ocs1x8Sn { get; set; }
        public string ocs8x8Sn { get; set; }
    }

    /// <summary>批次汇总（SRS §4.8）；`total` = `paths[]` 长度，完整批次为 64。</summary>
    public sealed class BatchSummaryInfo
    {
        public int total { get; set; }
        public int completed { get; set; }
        public int measurementFailed { get; set; }
        public int notAttempted { get; set; }
        public int retried { get; set; }
    }

    /// <summary>单条路径结果（SRS §6.2 `paths[]` 元素）。</summary>
    public sealed class PathResult
    {
        public string pathId { get; set; }

        [JsonPropertyName("in")]
        public int inChannel { get; set; }

        public int outChannel { get; set; }

        /// <summary>`Computed / Failed / NotAttempted`；运行中允许 `Pending` 快照（SDD §5.3）。</summary>
        public string pathState { get; set; }

        public string groupId { get; set; }

        public int averageCount { get; set; }

        public int validCount { get; set; }

        public string avgScanId { get; set; }

        public string[] scanIds { get; set; }

        public OcsStateSnapshot ocsState { get; set; }

        /// <summary>`ok / noPeak / invalidFrame / notComputed`（SRS §4.5.1）。</summary>
        public string connectivity { get; set; }

        public double? ilDb { get; set; }

        public double? rlDb { get; set; }

        /// <summary>指向 L2 `summary.json` 的组内相对路径，例如 `AVG/summary.json`。</summary>
        public string metricSource { get; set; }

        public PathEvidence evidence { get; set; }

        public string[] flags { get; set; }

        public PathNote[] notes { get; set; }

        public int attempt { get; set; }

        public long? durationMs { get; set; }

        public string errorClass { get; set; }
    }

    /// <summary>路径执行时的 OCS 状态快照。</summary>
    public sealed class OcsStateSnapshot
    {
        /// <summary>1×8 路由，形如 `SW1-03`。</summary>
        public string route1x8 { get; set; }

        /// <summary>8×8 矩阵，形如 `SW01_02_..._08`。</summary>
        public string matrix8x8 { get; set; }
    }

    /// <summary>峰证据（SRS §6.2 `evidence`）；距离一律用米（FR-047）。</summary>
    public sealed class PathEvidence
    {
        public bool? peakPresent { get; set; }
        public double? peakPositionM { get; set; }
        public int? peakNo { get; set; }
        public bool? zeroPeakExcluded { get; set; }
    }

    /// <summary>人工备注（FR-054）。</summary>
    public sealed class PathNote
    {
        public string text { get; set; }
        public string @operator { get; set; }
        public string atUtc { get; set; }
    }
}
