using GmixLx.Core;
using GmixLx.Vendor;

namespace GmixLx.Tests
{
    /// <summary>
    /// 映讯_2 测试夹具：参考标定参数与合成帧（160000 点、内部峰 + 8 个 LGA50 网格峰）。
    /// 只有一套，标定测试与指标表测试共用，避免两处合成数据漂移。
    /// </summary>
    internal static class YinXun2Fixtures
    {
        public const double PitchM = 0.0004258363425773126;

        public static readonly double[] LgaPeaks =
            { 1.0171, 2.2633, 3.4354, 5.5278, 6.8198, 8.8165, 10.0979, 11.1414 };

        public static YinXun2ReferenceCalibration Calibration()
        {
            return new YinXun2ReferenceCalibration
            {
                Device = "YinXun_2",
                Status = "test",
                AxisA = 0.964511776,
                AxisBM = 1.089573026,
                // 2026-11-17 设备 dB 刻度；历史 LGA50 传递斜率 0.9414 见 transfer_slope_to_lga50
                DbAlpha = 0.9985,
                DbBeta = 94.183066,
                LevelOffsetDb = -90.0,
                InternalReferencePeakM = 1.089573,
                YinXunPitchM = PitchM,
                Lga50PointCount = 82152,
                Lga50PitchM = 0.000185,
                MaskBeforeLga50M = 0.2,
                VendorRoiStartM = 3.5
            };
        }

        public static YinXun2Measurement SyntheticFrame()
        {
            double[] axis;
            double[] levels;
            FillSynthetic(Calibration(), out axis, out levels);
            return new YinXun2Measurement
            {
                DistanceM = axis,
                LevelsDb = levels,
                AverageReady = true,
                AverageCount = 10
            };
        }

        /// <summary>合成一帧 YinXun_2 曲线：设备内部峰（1.0893 m）+ 8 个 LGA50 网格峰。</summary>
        public static void FillSynthetic(YinXun2ReferenceCalibration calibration,
            out double[] axis, out double[] levels)
        {
            const int count = 160000;
            axis = new double[count];
            levels = new double[count];
            for (int i = 0; i < count; i++)
            {
                axis[i] = i * PitchM;
                // 合成背景是测试用的低电平，不是设备真实噪声底：
                // 真实原生噪声底约 −36.8 dB，叠加 LevelOffsetDb（−90.0 dB）后约 −126.8 dB，与厂商 GUI 一致。
                // 这里取 −110.0 只为让背景远低于合成峰；叠加偏移后为 −200.0。
                levels[i] = -110.0;
            }

            AddPeak(axis, levels, 1.0893, -40.0);
            foreach (double d in LgaPeaks) AddPeak(axis, levels, calibration.ToYinXunDistance(d), -50.0);
        }

        static void AddPeak(double[] axis, double[] levels, double distanceM, double levelDb)
        {
            int center = 0;
            double best = double.MaxValue;
            for (int i = 0; i < axis.Length; i++)
            {
                double d = System.Math.Abs(axis[i] - distanceM);
                if (d < best) { best = d; center = i; }
            }
            for (int k = -5; k <= 5; k++)
            {
                int idx = center + k;
                if (idx < 0 || idx >= levels.Length) continue;
                double value = levelDb - System.Math.Abs(k) * 2.0;
                if (value > levels[idx]) levels[idx] = value;
            }
        }
    }
}
