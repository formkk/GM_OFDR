using System;
using GmixLx.Core;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 映讯_2 指标 → 契约参考系（LGA50）：
    /// 距离按轴传递换回参考系；IL 按 dB 斜率（alpha）换回参考系的 dB 尺度；
    /// RL 保持设备内部参考的相对量——beta 随会话漂移（TBD-Y11），套用会造成虚假绝对值。
    /// </summary>
    public sealed class YinXun2MetricsMapper : IPathMetricsMapper
    {
        readonly YinXun2ReferenceCalibration _calibration;

        public YinXun2MetricsMapper(YinXun2ReferenceCalibration calibration)
        {
            if (calibration == null) throw new ArgumentNullException("calibration");
            calibration.Validate();
            _calibration = calibration;
        }

        public PathMetrics ToReport(PathMetrics metrics)
        {
            if (metrics == null) throw new ArgumentNullException("metrics");
            if (!metrics.Succeeded) return metrics;

            return new PathMetrics
            {
                // 原生 IL = DbAlpha × 参考系 IL；差值运算中截距抵消，斜率需换回。
                // alpha = 0.9985 ± 0.0051，2026-11-17 用 FVA-3150 已知衰减扫描验证
                // （0–10 dB 正反扫，R²=0.99974）；设备 dB 刻度基本为 1.0，IL 不再放大 6.2%。
                IlIn = metrics.IlIn / _calibration.DbAlpha,
                IlDut = metrics.IlDut / _calibration.DbAlpha,
                IlOut = metrics.IlOut / _calibration.DbAlpha,

                // RL 是设备内部 0 dB 参考下的相对量（C-03/C-07）：不做跨设备换算
                RlIn = metrics.RlIn,
                RlDut = metrics.RlDut,
                RlOut = metrics.RlOut,

                // 光程按轴传递换算；DUT 光程是两峰之差，斜率取倒数即可
                DutPathM = metrics.DutPathM / _calibration.AxisA,
                LinkLengthM = _calibration.ToLga50Distance(metrics.LinkLengthM),

                PeakCount = metrics.PeakCount
            };
        }
    }
}
