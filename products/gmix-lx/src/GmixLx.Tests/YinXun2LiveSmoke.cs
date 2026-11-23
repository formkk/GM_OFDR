using System;
using System.Globalization;
using System.IO;
using GmixLx.Core;
using GmixLx.Orchestration;
using GmixLx.Vendor;

namespace GmixLx.Tests
{
    /// <summary>
    /// 映讯_2 真机冒烟：Open → ROI → 取一帧 ready → 参考标定重采样 → 8 峰与 11 列。
    /// 手动运行：<c>--live-yinxun2 [sdkDir] [m] [n] [roiStartM] [roiEndM] [timeoutMs]</c>
    /// 结论只反映当场光路，不写入标定文件。
    /// </summary>
    internal static class YinXun2LiveSmoke
    {
        const string SdkRelativePath = "references/YinXun_2/customer";
        const string CalibrationRelativePath = "products/gmix-lx/config/yinxun2_reference_calibration.json";

        public static int Run(string sdkDirectory, uint m, uint n,
            double roiStartM, double roiEndM, int timeoutMs)
        {
            string sdk = string.IsNullOrEmpty(sdkDirectory) ? Resolve(SdkRelativePath) : sdkDirectory;
            string calibrationPath = Resolve(CalibrationRelativePath);

            if (sdk == null || !Directory.Exists(sdk))
            {
                Console.WriteLine("LIVE YX2 FATAL: 未找到 SDK 目录（--live-yinxun2 [sdkDir] 指定）");
                return 2;
            }
            if (calibrationPath == null)
            {
                Console.WriteLine("LIVE YX2 FATAL: 未找到 " + CalibrationRelativePath);
                return 2;
            }

            Console.WriteLine("SDK   : " + sdk);
            Console.WriteLine("标定  : " + calibrationPath);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "参数  : m={0} n={1} ROI=[{2:F3}, {3:F3}] m 超时={4} ms", m, n, roiStartM, roiEndM, timeoutMs));

            YinXun2ReferenceCalibration calibration = YinXun2ReferenceCalibration.Load(calibrationPath);
            CurveMetricsConfig referenceConfig = YinXun2PathMeasurer.DefaultReferenceMetrics();
            CurveMetricsConfig nativeConfig = YinXun2PathMeasurer.ToNativeConfig(referenceConfig, calibration);

            using (var client = new YinXun2Client(new YinXun2NativeSdk()))
            {
                client.Open(sdk, m, n);
                Console.WriteLine("打开设备：OK");

                client.SetAnalysisRoi(roiStartM, roiEndM);
                Console.WriteLine("设置 ROI：OK");

                YinXun2Measurement frame = client.AcquireReady(timeoutMs);
                PrintFrame(frame, calibration.LevelOffsetDb);

                double[] levels = (double[])frame.LevelsDb.Clone();
                YinXun2CurvePreparation.MaskInternalSection(levels, frame.DistanceM, calibration);
                YinXun2CurvePreparation.ApplyLevelOffset(levels, calibration.LevelOffsetDb);

                double threshold = CurveMetrics.NoiseFloorMedian(levels) + nativeConfig.NoiseFloorMarginDb;
                int mergeGap = nativeConfig.RowsOfMeters(nativeConfig.PeakMergeGapM);
                var peaks = CurveMetrics.FindPeaks(levels, threshold, mergeGap);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "原生  : {0} 点 / 步距 {1:F9} m / 中位 {2:F2} dB / 阈值 {3:F2} dB / 峰数 {4}",
                    levels.Length, calibration.YinXunPitchM, CurveMetrics.NoiseFloorMedian(levels),
                    threshold, peaks.Count));
                for (int i = 0; i < peaks.Count; i++)
                {
                    double nativeM = peaks[i].Centroid * calibration.YinXunPitchM;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  P{0}  原生 {1,8:F4} m  参考系 {2,8:F4} m  {3,8:F2} dB  宽 {4}",
                        i + 1, nativeM, calibration.ToLga50Distance(nativeM), peaks[i].Db, peaks[i].Width));
                }

                PathMetrics metrics = YinXun2PathMeasurer.Measure(frame, calibration, referenceConfig, 1, 1);
                if (!metrics.Succeeded)
                {
                    Console.WriteLine("指标  : 不可计算 — " + metrics.Failure);
                    return 1;
                }

                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "指标  : IL_IN {0:F2} / IL_DUT {1:F2} / IL_OUT {2:F2} dB",
                    metrics.IlIn, metrics.IlDut, metrics.IlOut));
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "        RL_IN {0:F2} / RL_DUT {1:F2} / RL_OUT {2:F2} dB",
                    metrics.RlIn, metrics.RlDut, metrics.RlOut));
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "        链路 {0:F4} m / DUT 光程 {1:F4} m / 峰数 {2}",
                    metrics.LinkLengthM, metrics.DutPathM, metrics.PeakCount));
                return 0;
            }
        }

        static void PrintFrame(YinXun2Measurement frame, double levelOffsetDb)
        {
            int count = frame.LevelsDb.Length;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "帧    : {0} 点 / 平均 {1} / ready {2}", count, frame.AverageCount, frame.AverageReady));

            if (count == 0)
            {
                Console.WriteLine("帧    : 无曲线（距离轴为空）");
                return;
            }

            double step = count > 1 ? (frame.DistanceM[count - 1] - frame.DistanceM[0]) / (count - 1) : 0.0;
            double maxDeviation = 0.0;
            for (int i = 1; i < count; i++)
            {
                double deviation = Math.Abs((frame.DistanceM[i] - frame.DistanceM[i - 1]) - step);
                if (deviation > maxDeviation) maxDeviation = deviation;
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "轴    : [{0:F4}, {1:F4}] m / 步距 {2:F9} m / 最大偏差 {3:F9} m",
                frame.DistanceM[0], frame.DistanceM[count - 1], step, maxDeviation));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "电平  : min {0:F2} / 中位 {1:F2} / max {2:F2} dB（含 GUI 偏移 {3:F1} dB）",
                Min(frame.LevelsDb) + levelOffsetDb,
                CurveMetrics.NoiseFloorMedian(frame.LevelsDb) + levelOffsetDb,
                Max(frame.LevelsDb) + levelOffsetDb, levelOffsetDb));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "厂商  : 峰数 {0} / 主峰 {1:F4} m / IL {2:F2} dB / RL {3:F2} dB / loss_status {4} / ROI IL {5} / ROI RL {6}",
                frame.Peaks.Length, frame.PeakDistanceM, frame.InsertionLossDb, frame.ReturnLossDb,
                frame.LossStatus, frame.RoiIlStatus, frame.RoiRlStatus));
        }

        static double Min(double[] values)
        {
            double min = double.MaxValue;
            foreach (double value in values) if (value < min) min = value;
            return min;
        }

        static double Max(double[] values)
        {
            double max = double.MinValue;
            foreach (double value in values) if (value > max) max = value;
            return max;
        }

        internal static string Resolve(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
