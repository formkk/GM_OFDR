using System;
using System.Globalization;
using GmixLx.Core;
using GmixLx.Orchestration;

namespace GmixLx.Tests
{
    /// <summary>
    /// 映讯_2 真机指标表联调：走完整台架（SDK + 两台 OCS + 原生轴 + 参考系翻译）。
    /// 手动运行：<c>--live-yinxun2-table [inFrom] [inTo] [outFrom] [outTo] [samples] [marginDb] [temporalN]</c>
    /// </summary>
    internal static class YinXun2LiveTable
    {
        public static int Run(int inFrom, int inTo, int outFrom, int outTo, int samples,
            double referenceMarginDb, uint temporalAverageN)
        {
            string sdk = YinXun2LiveSmoke.Resolve("references/YinXun_2/customer");
            string calibrationPath = YinXun2LiveSmoke.Resolve(
                "products/gmix-lx/config/yinxun2_reference_calibration.json");
            if (sdk == null || calibrationPath == null)
            {
                Console.WriteLine("LIVE YX2 TABLE FATAL: 找不到 SDK 目录或参考标定文件");
                return 2;
            }

            YinXun2ReferenceCalibration calibration = YinXun2ReferenceCalibration.Load(calibrationPath);
            CurveMetricsConfig reference = YinXun2PathMeasurer.DefaultReferenceMetrics();
            reference.NoiseFloorMarginDb = referenceMarginDb;

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "LIVE YX2 TABLE  IN {0}~{1}  OUT {2}~{3}  samples={4}  参考系余量={5} dB（原生 {6:F2} dB）  时间平均 n={7}",
                inFrom, inTo, outFrom, outTo, samples, referenceMarginDb,
                referenceMarginDb * calibration.DbAlpha, temporalAverageN));

            using (var station = YinXun2Station.Create(sdk, "COM4", "COM3", calibration, reference,
                1, temporalAverageN, 3.5, 20.0, 30000))
            {
                var options = new MetricTableOptions { SamplesPerPath = samples, SettleMs = 50 };
                MetricTableResult result = station.Runner.Run(inFrom, inTo, outFrom, outTo, options,
                    row => Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  IN={0} OUT={1}  IL-IN={2,7:F3} IL-DUT={3,7:F3} IL-OUT={4,7:F3}  RL-IN={5,8:F3} RL-DUT={6,8:F3} RL-OUT={7,8:F3}  path={8:F3} link={9:F3}  valid={10}  {11}",
                        row.InChannel, row.OutChannel, row.IlIn, row.IlDut, row.IlOut,
                        row.RlIn, row.RlDut, row.RlOut, row.DutPathM, row.LinkLengthM,
                        row.ValidSamples, row.Failure ?? "ok")),
                    System.Threading.CancellationToken.None);

                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "rows={0} valid={1} failed={2} elapsed={3} ms",
                    result.Rows.Count, result.ValidPaths, result.FailedPaths, result.DurationMs));
                return result.ValidPaths > 0 ? 0 : 1;
            }
        }
    }
}
