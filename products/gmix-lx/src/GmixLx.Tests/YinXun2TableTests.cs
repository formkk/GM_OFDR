using System;
using System.Globalization;
using System.Threading;
using GmixLx.Core;
using GmixLx.Orchestration;
using GmixLx.Vendor;

namespace GmixLx.Tests
{
    /// <summary>
    /// C5 单元测试：映讯_2 走 64 路指标表。
    /// 用假 SDK 提供曲线、内存串口回放 OCS 命令，验证切路、11 列口径与失败隔离；
    /// 不依赖真机、原生 DLL 或串口。
    /// </summary>
    internal static class YinXun2TableTests
    {
        static void Check(ref int failures, string name, bool ok, string detail = null)
        {
            if (ok) return;
            failures++;
            Console.WriteLine("FAIL " + name + (string.IsNullOrEmpty(detail) ? string.Empty : " :: " + detail));
        }

        public static void Run(ref int failures)
        {
            RunTable(ref failures);
            RunFailureIsolation(ref failures);
            Console.WriteLine("PASS YinXun_2 指标表（曲线源/切路/失败隔离）");
        }

        static void RunTable(ref int failures)
        {
            YinXun2ReferenceCalibration calibration = YinXun2Fixtures.Calibration();
            var fake = new FakeYinXun2Sdk { Fallback = YinXun2Fixtures.SyntheticFrame() };

            using (var client = new YinXun2Client(fake))
            using (var ocs1x8 = new Ocs1x8Client(Transport1x8(), 300))
            using (var ocs8x8 = new Ocs8x8Client(Transport8x8(), 300))
            {
                client.Open(@"C:\sdk", 1, 10);

                CurveMetricsConfig reference = YinXun2PathMeasurer.DefaultReferenceMetrics();
                CurveMetricsConfig native = YinXun2PathMeasurer.ToNativeConfig(reference, calibration);
                var runner = new MetricTableRunner(
                    new YinXun2CurveSource(client, calibration), ocs1x8, ocs8x8,
                    native, new YinXun2MetricsMapper(calibration));

                MetricTableResult result = runner.Run(1, 2, 1, 1,
                    new MetricTableOptions { SamplesPerPath = 1, SettleMs = 0 },
                    null, CancellationToken.None);

                Check(ref failures, "YinXun_2 指标表：2 行", result.Rows.Count == 2);
                Check(ref failures, "YinXun_2 指标表：两行均有效", result.ValidPaths == 2, Describe(result));
                Check(ref failures, "YinXun_2 指标表：每路只取一帧",
                    fake.AcquireCalls == 2, "调用 " + fake.AcquireCalls + " 次");

                if (result.Rows.Count != 2) return;

                MetricTableRow row = result.Rows[1];
                Check(ref failures, "YinXun_2 指标表：第二行 IN2/OUT1",
                    row.InChannel == 2 && row.OutChannel == 1);
                Check(ref failures, "YinXun_2 指标表：曲线 160000 点（原生轴）",
                    row.LevelsDb != null && row.LevelsDb.Length == 160000);
                Check(ref failures, "YinXun_2 指标表：电平叠加 GUI 偏移 −90.0 dB",
                    Math.Abs(CurveMetrics.NoiseFloorMedian(row.LevelsDb) - (-200.0)) < 0.6,
                    CurveMetrics.NoiseFloorMedian(row.LevelsDb).ToString("F2", CultureInfo.InvariantCulture));
                Check(ref failures, "YinXun_2 指标表：链路长度取 P8",
                    Math.Abs(row.LinkLengthM - 11.1414) < 0.02,
                    row.LinkLengthM.ToString("F4", CultureInfo.InvariantCulture));
                Check(ref failures, "YinXun_2 指标表：DUT 光程取 P7−P4",
                    Math.Abs(row.DutPathM - 4.5701) < 0.02,
                    row.DutPathM.ToString("F4", CultureInfo.InvariantCulture));
                Check(ref failures, "YinXun_2 指标表：行距取原生步距",
                    Math.Abs(row.MetersPerRow - calibration.YinXunPitchM) < 1e-12);
                Check(ref failures, "YinXun_2 指标表：峰合并间距按原生行",
                    row.PeakMergeGapRows == native.RowsOfMeters(native.PeakMergeGapM));

                // 曲线显示轴：从设备连出口起算，与参考系同一刻度
                Check(ref failures, "YinXun_2 曲线轴：步距 = 原生步距 / AxisA",
                    Math.Abs(row.AxisScaleM - calibration.YinXunPitchM / calibration.AxisA) < 1e-12);
                Check(ref failures, "YinXun_2 曲线轴：原点 = −AxisBM / AxisA",
                    Math.Abs(row.AxisOffsetM + calibration.AxisBM / calibration.AxisA) < 1e-12);
                var displayPeaks = CurveMetrics.FindPeaks(row.LevelsDb, row.PeakThresholdDb, row.PeakMergeGapRows);
                if (displayPeaks.Count == 8)
                {
                    double first = row.AxisOffsetM + displayPeaks[0].Centroid * row.AxisScaleM;
                    Check(ref failures, "YinXun_2 曲线轴：首峰落在参考系 1.017 m",
                        Math.Abs(first - YinXun2Fixtures.LgaPeaks[0]) < 0.02,
                        first.ToString("F4", CultureInfo.InvariantCulture));
                }
            }
        }

        static void RunFailureIsolation(ref int failures)
        {
            // 测量类错误：本行失败，整批继续
            var measurementFake = new FakeYinXun2Sdk
            {
                AcquireError = new YinXun2Exception(ErrorClass.Measurement, "等待平均帧就绪超时（测试）")
            };
            MetricTableResult result;
            using (var client = new YinXun2Client(measurementFake))
            using (var ocs1x8 = new Ocs1x8Client(Transport1x8(), 300))
            using (var ocs8x8 = new Ocs8x8Client(Transport8x8(), 300))
            {
                client.Open(@"C:\sdk");
                var runner = new MetricTableRunner(
                    new YinXun2CurveSource(client, YinXun2Fixtures.Calibration()), ocs1x8, ocs8x8,
                    YinXun2PathMeasurer.DefaultNativeMetrics(YinXun2Fixtures.Calibration()));
                result = runner.Run(1, 1, 1, 1,
                    new MetricTableOptions { SamplesPerPath = 1, SettleMs = 0 },
                    null, CancellationToken.None);
            }

            Check(ref failures, "YinXun_2 失败隔离：测量错误只废本行",
                result.Rows.Count == 1 && result.FailedPaths == 1);
            Check(ref failures, "YinXun_2 失败隔离：保留厂商原因",
                result.Rows.Count == 1 && result.Rows[0].Failure != null
                && result.Rows[0].Failure.Contains("就绪超时"));

            // 设备类错误：中止整批（不吞）
            var deviceFake = new FakeYinXun2Sdk
            {
                AcquireError = new YinXun2Exception(ErrorClass.Device, "设备断连（测试）")
            };
            bool aborted = false;
            try
            {
                using (var client = new YinXun2Client(deviceFake))
                using (var ocs1x8 = new Ocs1x8Client(Transport1x8(), 300))
                using (var ocs8x8 = new Ocs8x8Client(Transport8x8(), 300))
                {
                    client.Open(@"C:\sdk");
                    var runner = new MetricTableRunner(
                        new YinXun2CurveSource(client, YinXun2Fixtures.Calibration()), ocs1x8, ocs8x8,
                        YinXun2PathMeasurer.DefaultNativeMetrics(YinXun2Fixtures.Calibration()));
                    runner.Run(1, 2, 1, 1,
                        new MetricTableOptions { SamplesPerPath = 1, SettleMs = 0 },
                        null, CancellationToken.None);
                }
            }
            catch (YinXun2Exception ex)
            {
                aborted = ex.ErrorClass == ErrorClass.Device;
            }
            Check(ref failures, "YinXun_2 失败隔离：设备错误中止整批", aborted);
        }

        static MemoryOcsTransport Transport1x8()
        {
            var transport = new MemoryOcsTransport();
            transport.Open();
            transport.Map("<SETSW1-01>", "<1-01>")
                     .Map("<SETSW1-02>", "<1-02>");
            return transport;
        }

        static MemoryOcsTransport Transport8x8()
        {
            var transport = new MemoryOcsTransport();
            transport.Open();
            transport.Map("<SETSW10000000>", "<OK>")
                     .Map("<SETSW01000000>", "<OK>");
            return transport;
        }

        static string Describe(MetricTableResult result)
        {
            if (result.Rows.Count == 0) return "无行";
            string text = null;
            foreach (MetricTableRow row in result.Rows)
            {
                if (row.Succeeded) continue;
                text = "IN" + row.InChannel + "/OUT" + row.OutChannel + " " + row.Failure;
                break;
            }
            return text;
        }
    }
}
