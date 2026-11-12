using System;
using System.Collections.Generic;

namespace Detection.Core
{
    /// <summary>
    /// 批次汇总口径（SRS §4.8 FR-070/074）：`completed + measurementFailed + notAttempted = total`。
    /// `retried` 以 `attempt &gt; 1` 计（SDD §4.3：attempt 仅在批次恢复后重做时递增）。
    /// </summary>
    public static class BatchSummaryCalculator
    {
        public static BatchSummaryInfo Compute(IEnumerable<PathResult> paths)
        {
            var summary = new BatchSummaryInfo();
            if (paths == null) return summary;

            foreach (PathResult path in paths)
            {
                if (path == null) continue;

                summary.total++;
                if (string.Equals(path.pathState, PathStateNames.Computed, StringComparison.Ordinal))
                    summary.completed++;
                else if (string.Equals(path.pathState, PathStateNames.Failed, StringComparison.Ordinal))
                    summary.measurementFailed++;
                else if (string.Equals(path.pathState, PathStateNames.NotAttempted, StringComparison.Ordinal))
                    summary.notAttempted++;

                if (path.attempt > 1) summary.retried++;
            }

            return summary;
        }

        /// <summary>终态判定：存在 Failed 路径即 `CompletedWithFailures`。</summary>
        public static BatchState ResolveTerminalState(BatchSummaryInfo summary)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));
            return summary.measurementFailed > 0 ? BatchState.CompletedWithFailures : BatchState.Completed;
        }
    }

    /// <summary>路径状态的持久化字面量（SRS §6.2；运行中额外允许 `Pending`）。</summary>
    public static class PathStateNames
    {
        public const string Computed = "Computed";
        public const string Failed = "Failed";
        public const string NotAttempted = "NotAttempted";
        public const string Pending = "Pending";
    }

    /// <summary>批次状态的持久化字面量（SRS §6.2；运行中额外允许 `Running`）。</summary>
    public static class BatchStateNames
    {
        public const string Running = "Running";
        public const string Aborted = "Aborted";
        public const string Completed = "Completed";
        public const string CompletedWithFailures = "CompletedWithFailures";
    }

    /// <summary>连通性的持久化字面量（SRS §4.5.1）。</summary>
    public static class ConnectivityNames
    {
        public const string Ok = "ok";
        public const string NoPeak = "noPeak";
        public const string InvalidFrame = "invalidFrame";
        public const string NotComputed = "notComputed";
    }

    /// <summary>标定状态的持久化字面量（SRS §6.2）。</summary>
    public static class CalibrationStatusNames
    {
        public const string Calibrated = "calibrated";
        public const string Uncalibrated = "uncalibrated";
        public const string Mismatch = "mismatch";

        public static string Of(CalibrationStatus status)
        {
            switch (status)
            {
                case CalibrationStatus.Calibrated: return Calibrated;
                case CalibrationStatus.Uncalibrated: return Uncalibrated;
                case CalibrationStatus.Mismatch: return Mismatch;
                default: throw new ArgumentOutOfRangeException(nameof(status), status, "未知标定状态");
            }
        }
    }

    /// <summary>关注提示的持久化字面量（SRS §4.6）。</summary>
    public static class AttentionFlagNames
    {
        public const string MeasurementFailed = "measurement_failed";
        public const string CalibrationInvalid = "calibration_invalid";
        public const string NoPeak = "no_peak";
        public const string PeakUnexpected = "peak_unexpected";
        public const string OutOfReference = "out_of_reference";
        public const string BaselineMissing = "baseline_missing";
        public const string AvgIncomplete = "avg_incomplete";
        public const string CnnSuspicious = "cnn_suspicious";

        public static string Of(AttentionFlag flag)
        {
            switch (flag)
            {
                case AttentionFlag.MeasurementFailed: return MeasurementFailed;
                case AttentionFlag.CalibrationInvalid: return CalibrationInvalid;
                case AttentionFlag.NoPeak: return NoPeak;
                case AttentionFlag.PeakUnexpected: return PeakUnexpected;
                case AttentionFlag.OutOfReference: return OutOfReference;
                case AttentionFlag.BaselineMissing: return BaselineMissing;
                case AttentionFlag.AvgIncomplete: return AvgIncomplete;
                case AttentionFlag.CnnSuspicious: return CnnSuspicious;
                default: throw new ArgumentOutOfRangeException(nameof(flag), flag, "未知关注提示");
            }
        }
    }
}
