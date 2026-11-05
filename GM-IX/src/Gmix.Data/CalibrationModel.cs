using System;
using System.Collections.Generic;

namespace Gmix.Data
{
    /// <summary>
    /// Schema v0.2 校准模型：距离比例、绝对回损偏移、插损斜率/截距。
    /// 公式均为自有口径，不保证与厂商 UI 数值一致。
    /// </summary>
    public sealed class CalibrationModel
    {
        public string calibrationId { get; set; }
        public string configHash { get; set; }
        public double distScale { get; set; }
        public double? rlOffsetK { get; set; }
        public double? ilSlope { get; set; }
        public double? ilIntercept { get; set; }
        public double? triggerThresholdDb { get; set; }

        public CalibrationModel()
        {
            distScale = 1.0;
        }

        public static CalibrationModel FromDocument(CalibrationDocument doc)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            var m = new CalibrationModel();
            m.calibrationId = doc.calibrationId;
            m.configHash = doc.configHash;
            m.distScale = doc.distScale <= 0 ? 1.0 : doc.distScale;
            m.rlOffsetK = doc.rlOffsetK;
            m.ilSlope = doc.ilSlope;
            m.ilIntercept = doc.ilIntercept;
            m.triggerThresholdDb = doc.triggerThresholdDb;
            return m;
        }

        public bool MatchesConfig(string currentConfigHash)
        {
            return !string.IsNullOrEmpty(configHash)
                && !string.IsNullOrEmpty(currentConfigHash)
                && string.Equals(configHash, currentConfigHash, StringComparison.Ordinal);
        }

        public bool HasAbsoluteRl()
        {
            return rlOffsetK.HasValue && IsFinite(rlOffsetK.Value);
        }

        public bool HasIlCalibration()
        {
            return ilSlope.HasValue && ilIntercept.HasValue
                && IsFinite(ilSlope.Value) && IsFinite(ilIntercept.Value);
        }

        public double ApplyDistance(double rawDistanceM)
        {
            return rawDistanceM * distScale;
        }

        /// <summary>绝对回损 = 相对回损 + K；未标定时返回 null。</summary>
        public double? ApplyRl(double rawRlDb)
        {
            if (!HasAbsoluteRl()) return null;
            return rawRlDb + rlOffsetK.Value;
        }

        /// <summary>总插损 = 斜率 × 原始插损 + 截距；截距只加一次。</summary>
        public double? ApplyIlTotal(double rawIlDb)
        {
            if (!HasIlCalibration()) return null;
            return ilSlope.Value * rawIlDb + ilIntercept.Value;
        }

        /// <summary>逐事件台阶只乘斜率，不重复加截距。</summary>
        public double ApplyIlStep(double rawStepLossDb)
        {
            if (!ilSlope.HasValue || !IsFinite(ilSlope.Value)) return rawStepLossDb;
            return ilSlope.Value * rawStepLossDb;
        }

        public List<string> Validate()
        {
            var errors = new List<string>();
            if (distScale <= 0 || !IsFinite(distScale))
                errors.Add("distScale 必须为正有限数");
            if (rlOffsetK.HasValue && !IsFinite(rlOffsetK.Value))
                errors.Add("rlOffsetK 不是有限数");
            if (ilSlope.HasValue && !IsFinite(ilSlope.Value))
                errors.Add("ilSlope 不是有限数");
            if (ilIntercept.HasValue && !IsFinite(ilIntercept.Value))
                errors.Add("ilIntercept 不是有限数");
            return errors;
        }

        static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
