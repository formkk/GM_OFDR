using System;
using System.Globalization;
using System.IO;
using GmixLx.Core;
using GmixLx.Orchestration;
using GmixLx.Vendor;

namespace GmixLx.Tests
{
    /// <summary>
    /// C3 单元测试：YinXun_2 参考标定加载、轴/dB 映射、LGA50 网格重采样与 8 峰复用。
    /// 使用合成曲线，不依赖设备或 capture 目录。
    /// </summary>
    internal static class YinXun2CalibrationTests
    {
        static void Check(ref int failures, string name, bool ok, string detail = null)
        {
            if (!ok)
            {
                failures++;
                Console.WriteLine("FAIL " + name + (string.IsNullOrEmpty(detail) ? string.Empty : " :: " + detail));
            }
        }

        static bool Near(double a, double b, double tol)
        {
            return Math.Abs(a - b) <= tol * Math.Max(1.0, Math.Abs(b));
        }

        static YinXun2ReferenceCalibration Reference()
        {
            return YinXun2Fixtures.Calibration();
        }

        public static void Run(ref int failures)
        {
            RunLoader(ref failures);
            RunAxisAndDb(ref failures);
            RunNativePeaks(ref failures);
            RunPipeline(ref failures);
            Console.WriteLine("PASS YinXun_2 参考标定（加载/轴/dB/原生峰）");
        }

        static void RunLoader(ref int failures)
        {
            string path = Path.Combine(Path.GetTempPath(), "yx2_calib_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path,
                "{\"device\":\"YinXun_2\",\"status\":\"test\"," +
                "\"axis\":{\"a\":0.964511776,\"b_m\":1.089573026}," +
                "\"db\":{\"alpha\":0.94138,\"beta\":94.183066}," +
                "\"internal_reference_peak_m\":1.089573," +
                "\"lga50_point_count\":82152,\"lga50_pitch_m\":0.000185," +
                "\"mask_before_lga50_m\":0.2,\"vendor_roi_start_m\":3.5}");
            try
            {
                YinXun2ReferenceCalibration loaded = YinXun2ReferenceCalibration.Load(path);
                Check(ref failures, "YinXun_2 标定：加载 AxisA", Near(loaded.AxisA, 0.964511776, 1e-12));
                Check(ref failures, "YinXun_2 标定：加载 LGA50 点数", loaded.Lga50PointCount == 82152);
                Check(ref failures, "YinXun_2 标定：加载后轴映射",
                    Near(loaded.ToYinXunDistance(1.0171), 2.0708, 0.001));
            }
            finally
            {
                File.Delete(path);
            }

            string bad = Path.Combine(Path.GetTempPath(), "yx2_bad_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(bad,
                "{\"axis\":{\"a\":0.96,\"b_m\":1.0},\"db\":{\"alpha\":0,\"beta\":0}}");
            bool threw = false;
            try { YinXun2ReferenceCalibration.Load(bad); }
            catch (InvalidDataException) { threw = true; }
            finally { File.Delete(bad); }
            Check(ref failures, "YinXun_2 标定：DbAlpha=0 拒绝", threw);
        }

        static void RunAxisAndDb(ref int failures)
        {
            YinXun2ReferenceCalibration c = Reference();
            Check(ref failures, "YinXun_2 标定：LGA50 4.44 m → YinXun",
                Near(c.ToYinXunDistance(4.44), 0.964511776 * 4.44 + 1.089573026, 1e-12));
            Check(ref failures, "YinXun_2 标定：轴往返",
                Near(c.ToLga50Distance(c.ToYinXunDistance(7.5)), 7.5, 1e-12));
            Check(ref failures, "YinXun_2 标定：dB 往返",
                Near(c.ToLga50Level(c.ToYinXunLevel(-50.0)), -50.0, 1e-12));
            Check(ref failures, "YinXun_2 标定：dB 零电平映射",
                Near(c.ToLga50Level(94.183066), 0.0, 1e-9));
        }

        /// <summary>原生轴直接找峰（不重采样）；峰位置经标定换到参考系应与 LGA50 一致。</summary>
        static void RunNativePeaks(ref int failures)
        {
            YinXun2ReferenceCalibration c = Reference();
            YinXun2Measurement frame = YinXun2Fixtures.SyntheticFrame();

            var levels = (double[])frame.LevelsDb.Clone();
            YinXun2CurvePreparation.MaskInternalSection(levels, frame.DistanceM, c);

            Check(ref failures, "YinXun_2 原生：点数不被改变", levels.Length == 160000);
            Check(ref failures, "YinXun_2 原生：内部峰被屏蔽",
                levels[0] < CurveMetrics.NoiseFloorMedian(levels) + 30.0);

            CurveMetricsConfig native = YinXun2PathMeasurer.ToNativeConfig(YinXun2PathMeasurer.DefaultReferenceMetrics(), c);
            Check(ref failures, "YinXun_2 原生：步距取标定值", native.MetersPerRow == c.YinXunPitchM);
            Check(ref failures, "YinXun_2 原生：A/D 已换到原生轴",
                Math.Abs(native.MarkAM - c.ToYinXunDistance(4.44)) < 1e-12);
            Check(ref failures, "YinXun_2 原生：阈值余量按 dB 斜率折算",
                Math.Abs(native.NoiseFloorMarginDb - 30.0 * c.DbAlpha) < 1e-9,
                native.NoiseFloorMarginDb.ToString("F4", CultureInfo.InvariantCulture));
            CurveMetricsConfig deviceDefault = YinXun2PathMeasurer.DefaultNativeMetrics(c);
            Check(ref failures, "YinXun_2 默认余量 22 dB → 原生 21.97",
                Math.Abs(deviceDefault.NoiseFloorMarginDb - 22.0 * c.DbAlpha) < 1e-9,
                deviceDefault.NoiseFloorMarginDb.ToString("F4", CultureInfo.InvariantCulture));

            int mergeGap = native.RowsOfMeters(native.PeakMergeGapM);
            var peaks = CurveMetrics.FindPeaks(levels,
                CurveMetrics.NoiseFloorMedian(levels) + native.NoiseFloorMarginDb, mergeGap);
            Check(ref failures, "YinXun_2 原生：8 峰", peaks.Count == 8);
            if (peaks.Count == 8)
            {
                for (int i = 0; i < 8; i++)
                {
                    double reference = c.ToLga50Distance(peaks[i].Centroid * c.YinXunPitchM);
                    Check(ref failures,
                        string.Format(CultureInfo.InvariantCulture, "YinXun_2 原生：P{0} 参考系 {1:F4} m", i + 1, YinXun2Fixtures.LgaPeaks[i]),
                        Math.Abs(reference - YinXun2Fixtures.LgaPeaks[i]) < 0.01);
                }
            }
        }

        /// <summary>厂商帧（距离/电平，160000 点）直接走测量管线，得到与 LGA50 同口径的 11 列。</summary>
        static void RunPipeline(ref int failures)
        {
            YinXun2ReferenceCalibration c = Reference();
            double[] axis;
            double[] levels;
            YinXun2Fixtures.FillSynthetic(c, out axis, out levels);

            var measurement = new YinXun2Measurement
            {
                DistanceM = axis,
                LevelsDb = levels,
                AverageReady = true,
                AverageCount = 10
            };

            CurveMetricsConfig reference = YinXun2PathMeasurer.DefaultReferenceMetrics();
            Check(ref failures, "YinXun_2 管线：判据默认取 LGA50 参考系", reference.MetersPerRow == 0.000185);

            PathMetrics metrics = YinXun2PathMeasurer.Measure(measurement, c, reference, 1, 1);
            Check(ref failures, "YinXun_2 管线：指标可计算", metrics.Succeeded,
                metrics.Failure);
            Check(ref failures, "YinXun_2 管线：峰数 8", metrics.PeakCount == 8);

            // 8 峰位置：P4=5.5278、P7=10.0979、P8=11.1414
            Check(ref failures, "YinXun_2 管线：链路长度取 P8",
                Math.Abs(metrics.LinkLengthM - 11.1414) < 0.02,
                metrics.LinkLengthM.ToString("F4", CultureInfo.InvariantCulture));
            Check(ref failures, "YinXun_2 管线：DUT 光程取 P7−P4",
                Math.Abs(metrics.DutPathM - 4.5701) < 0.02,
                metrics.DutPathM.ToString("F4", CultureInfo.InvariantCulture));
        }
    }
}
