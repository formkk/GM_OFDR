using System;

namespace Gmix.Data
{
    /// <summary>
    /// DS-01~08 的唯一实现位置（SRS §6 / SDD §8.4）：只有 UI 呈现层可调用，
    /// 存储与处理层不得引用。数据字段恒为正刻度，此处只做显示变换，不写回任何字段。
    /// </summary>
    public static class DisplayTransform
    {
        /// <summary>DS-06：未标定态的显示锚点（本机 g50 四峰拟合，+147→-45、+120→-72）。</summary>
        public const double DefaultOffsetK = -192.0;

        /// <summary>DS-01：相对电平 → 显示刻度。</summary>
        public static double Level(double dbRel, double offsetK)
        {
            return dbRel + offsetK;
        }

        /// <summary>DS-02：总回损。已标定显示标定值；未标定显示 raw + 偏移并标注。</summary>
        public static string TotalRlText(double? rlDb, double? rlRawDb, double offsetK, bool calibrated)
        {
            if (calibrated)
                return rlDb.HasValue ? rlDb.Value.ToString("0.00") : "—";
            if (!rlRawDb.HasValue) return "—";
            return (rlRawDb.Value + offsetK).ToString("0.00") + "（未标定）";
        }

        /// <summary>DS-04：总插损取负显示；该规则只作用于 IL 域，严禁套用到任何 RL 字段。</summary>
        public static string TotalIlText(double? ilDb)
        {
            return ilDb.HasValue ? (-ilDb.Value).ToString("0.00") : "—";
        }

        /// <summary>DS-03：台阶为正损耗，原样显示。</summary>
        public static string StepLossText(double? stepLossDb)
        {
            return stepLossDb.HasValue ? stepLossDb.Value.ToString("0.00") : "—";
        }

        /// <summary>DS-05：精度恒为正，原样显示；null → "—"（不取绝对值，避免掩盖异常）。</summary>
        public static string PrecisionText(double? precisionDb)
        {
            return precisionDb.HasValue ? precisionDb.Value.ToString("0.00") : "—";
        }

        /// <summary>DS-06：未标定态是否必须显示"未标定"标注。</summary>
        public static bool RequiresUncalibratedBadge(bool calibrated)
        {
            return !calibrated;
        }
    }
}
