using System.Collections.Generic;

namespace GmixLx.Core
{
    /// <summary>IL/RL 参考范围；数值口径待 TBD-D05a，此处只接收外部基线的结果。</summary>
    public sealed class ReferenceRange
    {
        public double? ilMinDb { get; set; }
        public double? ilMaxDb { get; set; }
        public double? rlMinDb { get; set; }
        public double? rlMaxDb { get; set; }
    }

    /// <summary>
    /// 关注提示派生（SRS §4.6 FR-051）。只标记，不改变测量值、不产生合格结论。
    /// `peak_unexpected` 依赖 TBD-D05b 的窗口规则，`cnn_suspicious` 属后期，均不在此产生。
    /// </summary>
    public static class FlagEvaluator
    {
        public static AttentionFlag[] Evaluate(PathResult path, CalibrationStatus calibration, bool hasBaseline, ReferenceRange range)
        {
            var flags = new List<AttentionFlag>();
            if (path == null) return flags.ToArray();

            if (string.Equals(path.pathState, PathStateNames.Failed, System.StringComparison.Ordinal))
                flags.Add(AttentionFlag.MeasurementFailed);

            if (calibration != CalibrationStatus.Calibrated)
                flags.Add(AttentionFlag.CalibrationInvalid);

            if (string.Equals(path.connectivity, ConnectivityNames.NoPeak, System.StringComparison.Ordinal))
                flags.Add(AttentionFlag.NoPeak);

            if (path.validCount < path.averageCount)
                flags.Add(AttentionFlag.AvgIncomplete);

            if (!hasBaseline || range == null)
            {
                flags.Add(AttentionFlag.BaselineMissing);
            }
            else if (Outside(path.ilDb, range.ilMinDb, range.ilMaxDb) || Outside(path.rlDb, range.rlMinDb, range.rlMaxDb))
            {
                flags.Add(AttentionFlag.OutOfReference);
            }

            return flags.ToArray();
        }

        /// <summary>值缺失时不算越界（未标定/不可算由 `calibration_invalid` 表达）。</summary>
        static bool Outside(double? value, double? min, double? max)
        {
            if (!value.HasValue) return false;
            if (min.HasValue && value.Value < min.Value) return true;
            if (max.HasValue && value.Value > max.Value) return true;
            return false;
        }

        /// <summary>转为 `detection.json` 的字符串数组。</summary>
        public static string[] ToNames(AttentionFlag[] flags)
        {
            if (flags == null) return new string[0];
            var names = new string[flags.Length];
            for (int i = 0; i < flags.Length; i++) names[i] = AttentionFlagNames.Of(flags[i]);
            return names;
        }
    }
}
