using System;

namespace GmixLx.Core
{
    /// <summary>
    /// LGA50 距离轴映射（纯函数）：index × 点距。
    /// 依据：实测 1.017 m ↔ index 5501、2.104 m ↔ index 11374，折算 185.0 µm/点（TBD-04，可配置）。
    /// dB → 线性功率的换算归 <see cref="CurveMetrics"/>（与 demo 的指标公式同处，避免两份口径）。
    /// </summary>
    public static class CurveMapping
    {
        /// <summary>默认点距 185.0 µm（实测值；demo 默认 200 µm，现场可覆盖）。</summary>
        public const double DefaultPitchM = 0.000185;

        /// <summary>默认轴原点；FIBER_START 非 0 时由调用方传入。</summary>
        public const double DefaultOriginM = 0.0;

        /// <summary>生成距离轴：<c>originM + index × pitchM</c>。</summary>
        public static double[] ToDistance(int pointCount, double pitchM, double originM)
        {
            if (pointCount <= 0) throw new ArgumentOutOfRangeException("pointCount", "点数必须大于 0");
            if (pitchM <= 0 || double.IsNaN(pitchM) || double.IsInfinity(pitchM))
                throw new ArgumentOutOfRangeException("pitchM", "点距必须为有限正数");

            var distance = new double[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                distance[i] = originM + i * pitchM;
            }
            return distance;
        }
    }
}
