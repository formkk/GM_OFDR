using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GmixLx.Core;
using GmixLx.Orchestration;
using GmixLx.Vendor;

namespace GmixLx.Tests
{
    /// <summary>
    /// 映讯_2 长时间稳定性采集：按轮执行 64 路 × N 帧，逐帧落盘 double 电平并同步写指标 CSV。
    /// 手动运行：<c>--stability-yinxun2 [hours] [framesPerPath] [outDir] [com1x8] [com8x8]
    /// [m] [n] [roiStartM] [roiEndM] [maxRounds] [minFreeGb]</c>
    /// 单帧最多试 3 次，仍失败则记 failed 并继续；设备整体不可恢复时中止。
    /// </summary>
    internal static class YinXun2Stability
    {
        const string SdkRelativePath = "references/YinXun_2/customer";
        const string CalibrationRelativePath = "products/gmix-lx/config/yinxun2_reference_calibration.json";
        const long BytesPerFrame = 160000L * 8;
        const int AcquireAttempts = 3;

        public static int Run(double hours, int framesPerPath, string outDir,
            string port1x8, string port8x8, uint m, uint n,
            double roiStartM, double roiEndM, int maxRounds, double minFreeGb)
        {
            string sdk = YinXun2LiveSmoke.Resolve(SdkRelativePath);
            string calibrationPath = YinXun2LiveSmoke.Resolve(CalibrationRelativePath);
            if (sdk == null || !Directory.Exists(sdk))
            {
                Console.WriteLine("STABILITY FATAL: 未找到 SDK 目录 " + SdkRelativePath);
                return 2;
            }
            if (calibrationPath == null)
            {
                Console.WriteLine("STABILITY FATAL: 未找到 " + CalibrationRelativePath);
                return 2;
            }

            if (framesPerPath <= 0) framesPerPath = 8;
            if (hours <= 0 && maxRounds <= 0) maxRounds = 1;
            if (minFreeGb <= 0) minFreeGb = 150;

            YinXun2ReferenceCalibration calibration = YinXun2ReferenceCalibration.Load(calibrationPath);
            double roiStart = roiStartM >= 0 ? roiStartM : calibration.ToYinXunDistance(4.44);
            double roiEnd = roiEndM >= 0 ? roiEndM : calibration.ToYinXunDistance(10.6005);

            if (string.IsNullOrEmpty(outDir))
            {
                string captureRoot = YinXun2LiveSmoke.Resolve("devices/yinxun2/capture");
                outDir = Path.Combine(captureRoot ?? "devices/yinxun2/capture",
                    "stability_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            }
            Directory.CreateDirectory(outDir);
            outDir = Path.GetFullPath(outDir);

            long roundBytes = 64L * framesPerPath * BytesPerFrame;
            double freeGb = FreeGb(outDir);
            if (freeGb < minFreeGb)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "STABILITY FATAL: 可用空间 {0:F1} GB < 要求 {1:F1} GB", freeGb, minFreeGb));
                return 1;
            }

            CurveMetricsConfig reference = YinXun2PathMeasurer.DefaultReferenceMetrics();
            reference.NoiseFloorMarginDb = YinXun2PathMeasurer.DefaultReferenceMarginDb;
            double runtimeFloorGb = Math.Min(20.0, minFreeGb / 2.0);
            string calibrationSha = Sha256(calibrationPath);

            var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("STABILITY: 收到中断，处理完当前帧后停止");
            };
            Console.CancelKeyPress += onCancel;

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "STABILITY  64 路 × {0} 帧/轮  hours={1}  maxRounds={2}  m={3} n={4}  ROI(native)=[{5:F3},{6:F3}] m",
                framesPerPath, hours, maxRounds, m, n, roiStart, roiEnd));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "输出 {0}  每轮 {1:F1} MB  可用 {2:F1} GB  运行下限 {3:F1} GB",
                outDir, roundBytes / (1024.0 * 1024.0), freeGb, runtimeFloorGb));

            var csv = new StreamWriter(Path.Combine(outDir, "frames.csv"), false, new UTF8Encoding(false));
            csv.AutoFlush = true;
            WriteCsvHeader(csv, hours, framesPerPath, m, n, roiStart, roiEnd, maxRounds,
                minFreeGb, calibrationPath, calibrationSha);

            YinXun2Client client = null;
            Ocs1x8Client ocs1x8 = null;
            Ocs8x8Client ocs8x8 = null;
            long started = Environment.TickCount64;
            long bytesWritten = 0;
            int framesOk = 0;
            int framesFailed = 0;
            int pathFailures = 0;
            int roundsDone = 0;
            string stopReason = "duration";

            try
            {
                client = new YinXun2Client(new YinXun2NativeSdk());
                client.Open(sdk, m, n);
                client.SetAnalysisRoi(roiStart, roiEnd);
                ocs1x8 = new Ocs1x8Client(
                    new OcsSerialTransport(port1x8, OcsProtocolConstants.BaudRate1x8), 300);
                ocs8x8 = new Ocs8x8Client(
                    new OcsSerialTransport(port8x8, OcsProtocolConstants.BaudRate8x8), 300);
                ocs1x8.Open();
                ocs8x8.Open();

                for (int round = 1; ; round++)
                {
                    if (maxRounds > 0 && round > maxRounds)
                    {
                        stopReason = "maxRounds";
                        break;
                    }
                    double elapsedHours = (Environment.TickCount64 - started) / 3600000.0;
                    if (hours > 0 && elapsedHours >= hours)
                    {
                        stopReason = "duration";
                        break;
                    }
                    if (FreeGb(outDir) < runtimeFloorGb)
                    {
                        stopReason = "disk_low";
                        break;
                    }
                    if (cts.IsCancellationRequested)
                    {
                        stopReason = "cancelled";
                        break;
                    }

                    string roundDir = Path.Combine(outDir, "round_" + round.ToString("0000", CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(roundDir);
                    long roundStarted = Environment.TickCount64;
                    int roundOk = 0;
                    int roundFailed = 0;
                    int lastIn = -1;
                    bool stop = false;

                    for (int inChannel = 1; inChannel <= 8 && !stop; inChannel++)
                    {
                        for (int outChannel = 1; outChannel <= 8 && !stop; outChannel++)
                        {
                            if (cts.IsCancellationRequested)
                            {
                                stop = true;
                                break;
                            }

                            string pathDir = Path.Combine(roundDir,
                                "in" + inChannel.ToString("00", CultureInfo.InvariantCulture) +
                                "_out" + outChannel.ToString("00", CultureInfo.InvariantCulture));
                            Directory.CreateDirectory(pathDir);
                            long pathStarted = Environment.TickCount64;
                            int pathOk = 0;
                            int pathBad = 0;

                            if (!TrySwitch(ocs1x8, ocs8x8, inChannel, outChannel, lastIn))
                            {
                                pathFailures++;
                                roundFailed++;
                                WriteCsvRow(csv, started, round, inChannel, outChannel, -1,
                                    0, null, null, "OCS 切路失败（重试 1 次后放弃）");
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "R{0:0000} IN{1}/OUT{2}  切路失败，跳过该路", round, inChannel, outChannel));
                                continue;
                            }
                            lastIn = inChannel;
                            Thread.Sleep(50);

                            for (int f = 0; f < framesPerPath && !stop; f++)
                            {
                                if (cts.IsCancellationRequested)
                                {
                                    stop = true;
                                    break;
                                }

                                YinXun2Measurement frame = null;
                                string error = null;
                                long watch = Environment.TickCount64;

                                for (int attempt = 1; attempt <= AcquireAttempts && frame == null; attempt++)
                                {
                                    try
                                    {
                                        frame = client.AcquireReady(30000);
                                    }
                                    catch (YinXun2Exception ex)
                                    {
                                        error = ex.Message;
                                        if (ex.ErrorClass != ErrorClass.Measurement)
                                        {
                                            Console.WriteLine("STABILITY: 设备错误，尝试重连 — " + ex.Message);
                                            if (!TryReopen(ref client, sdk, m, n, roiStart, roiEnd))
                                            {
                                                stop = true;
                                                stopReason = "device_lost";
                                                break;
                                            }
                                        }
                                        else if (attempt < AcquireAttempts)
                                        {
                                            Thread.Sleep(200);
                                        }
                                    }
                                }

                                long acquireMs = Environment.TickCount64 - watch;

                                if (frame == null)
                                {
                                    framesFailed++;
                                    roundFailed++;
                                    pathBad++;
                                    WriteCsvRow(csv, started, round, inChannel, outChannel, f,
                                        acquireMs, null, null, error ?? "采集失败");
                                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                        "R{0:0000} IN{1}/OUT{2} frame {3} FAIL: {4}",
                                        round, inChannel, outChannel, f, error));
                                    continue;
                                }

                                string framePath = Path.Combine(pathDir,
                                    "frame_" + f.ToString("000", CultureInfo.InvariantCulture) + ".bin");
                                WriteFrame(framePath, frame.LevelsDb);
                                bytesWritten += BytesPerFrame;

                                PathMetrics metrics = null;
                                string metricError = null;
                                try
                                {
                                    metrics = YinXun2PathMeasurer.Measure(frame, calibration, reference,
                                        inChannel, outChannel);
                                }
                                catch (Exception ex)
                                {
                                    metricError = ex.Message;
                                }

                                if (metrics != null && metrics.Succeeded)
                                {
                                    framesOk++;
                                    roundOk++;
                                    pathOk++;
                                }
                                else
                                {
                                    framesFailed++;
                                    roundFailed++;
                                    pathBad++;
                                    if (metricError == null) metricError = metrics == null ? "指标未计算" : metrics.Failure;
                                }

                                WriteCsvRow(csv, started, round, inChannel, outChannel, f,
                                    acquireMs, frame, metrics, metricError);
                            }

                            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                "R{0:0000} IN{1}/OUT{2}  ok={3} bad={4}  {5:F1} s",
                                round, inChannel, outChannel, pathOk, pathBad,
                                (Environment.TickCount64 - pathStarted) / 1000.0));
                        }
                    }

                    if (stop)
                    {
                        if (stopReason != "device_lost") stopReason = "cancelled";
                        break;
                    }

                    roundsDone = round;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "ROUND {0:0000}  ok={1} failed={2}  {3:F0} s  total={4:F2} GB  free={5:F1} GB",
                        round, roundOk, roundFailed, (Environment.TickCount64 - roundStarted) / 1000.0,
                        bytesWritten / (1024.0 * 1024.0 * 1024.0), FreeGb(outDir)));
                }
            }
            catch (Exception ex)
            {
                stopReason = "fatal";
                Console.WriteLine("STABILITY FATAL: " + ex);
            }
            finally
            {
                csv.Dispose();
                if (ocs8x8 != null) ocs8x8.Dispose();
                if (ocs1x8 != null) ocs1x8.Dispose();
                if (client != null) client.Dispose();
                Console.CancelKeyPress -= onCancel;
                cts.Dispose();
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "STABILITY DONE  reason={0} rounds={1} frames_ok={2} frames_failed={3} path_failures={4} " +
                "bytes={5:F2} GB elapsed={6:F0} s dir={7}",
                stopReason, roundsDone, framesOk, framesFailed, pathFailures,
                bytesWritten / (1024.0 * 1024.0 * 1024.0), (Environment.TickCount64 - started) / 1000.0, outDir));

            if (stopReason == "fatal" || stopReason == "device_lost") return 2;
            return framesOk > 0 ? 0 : 1;
        }

        static bool TrySwitch(Ocs1x8Client ocs1x8, Ocs8x8Client ocs8x8, int inChannel, int outChannel, int lastIn)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    if (inChannel != lastIn) ocs1x8.SetOutput(inChannel);
                    var matrix = new int[8];
                    matrix[inChannel - 1] = outChannel;
                    ocs8x8.SetMatrix(matrix);
                    return true;
                }
                catch (OcsException)
                {
                    if (attempt == 1) Thread.Sleep(100);
                }
            }
            return false;
        }

        static bool TryReopen(ref YinXun2Client client, string sdk, uint m, uint n,
            double roiStart, double roiEnd)
        {
            try
            {
                client.Dispose();
            }
            catch (Exception)
            {
            }

            try
            {
                client = new YinXun2Client(new YinXun2NativeSdk());
                client.Open(sdk, m, n);
                client.SetAnalysisRoi(roiStart, roiEnd);
                Console.WriteLine("STABILITY: 设备重连成功");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("STABILITY: 设备重连失败 — " + ex.Message);
                client = null;
                return false;
            }
        }

        static void WriteFrame(string path, double[] values)
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                for (int i = 0; i < values.Length; i++) writer.Write(values[i]);
            }
        }

        static void WriteCsvHeader(TextWriter csv, double hours, int framesPerPath, uint m, uint n,
            double roiStart, double roiEnd, int maxRounds, double minFreeGb,
            string calibrationPath, string calibrationSha)
        {
            csv.WriteLine("# device=YinXun_2(GM-IX)");
            csv.WriteLine("# started_utc=" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "# hours={0} frames_per_path={1} m={2} n={3} roi_native_m=[{4:R},{5:R}] max_rounds={6} min_free_gb={7}",
                hours, framesPerPath, m, n, roiStart, roiEnd, maxRounds, minFreeGb));
            csv.WriteLine("# calibration=" + calibrationPath + " sha256=" + calibrationSha);
            csv.WriteLine("# frame=-1 表示该路切路失败，未采到帧");
            csv.WriteLine("elapsed_s,utc,round,in,out,frame,acquire_ms,points,avg_count,ready," +
                "metric_ok,peak_count,il_in_db,il_dut_db,il_out_db,rl_in_db,rl_dut_db,rl_out_db," +
                "link_m,dut_m,vendor_peaks,loss_status,error");
        }

        static void WriteCsvRow(TextWriter csv, long started, int round, int inChannel, int outChannel,
            int frame, long acquireMs, YinXun2Measurement measurement, PathMetrics metrics, string error)
        {
            int points = measurement == null ? 0 : measurement.LevelsDb.Length;
            double elapsed = (Environment.TickCount64 - started) / 1000.0;
            csv.WriteLine(string.Join(",", new[]
            {
                elapsed.ToString("F1", CultureInfo.InvariantCulture),
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                round.ToString(CultureInfo.InvariantCulture),
                inChannel.ToString(CultureInfo.InvariantCulture),
                outChannel.ToString(CultureInfo.InvariantCulture),
                frame.ToString(CultureInfo.InvariantCulture),
                acquireMs.ToString(CultureInfo.InvariantCulture),
                points.ToString(CultureInfo.InvariantCulture),
                measurement == null ? string.Empty : measurement.AverageCount.ToString(CultureInfo.InvariantCulture),
                measurement == null ? string.Empty : (measurement.AverageReady ? "1" : "0"),
                metrics != null && metrics.Succeeded ? "1" : "0",
                metrics == null ? string.Empty : metrics.PeakCount.ToString(CultureInfo.InvariantCulture),
                Num(metrics == null ? double.NaN : metrics.IlIn, "F3"),
                Num(metrics == null ? double.NaN : metrics.IlDut, "F3"),
                Num(metrics == null ? double.NaN : metrics.IlOut, "F3"),
                Num(metrics == null ? double.NaN : metrics.RlIn, "F3"),
                Num(metrics == null ? double.NaN : metrics.RlDut, "F3"),
                Num(metrics == null ? double.NaN : metrics.RlOut, "F3"),
                Num(metrics == null ? double.NaN : metrics.LinkLengthM, "F6"),
                Num(metrics == null ? double.NaN : metrics.DutPathM, "F6"),
                measurement == null ? string.Empty : measurement.Peaks.Length.ToString(CultureInfo.InvariantCulture),
                measurement == null ? string.Empty : measurement.LossStatus.ToString(CultureInfo.InvariantCulture),
                Csv(error)
            }));
        }

        static string Num(double value, string format)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return string.Empty;
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        static double FreeGb(string dir)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(dir));
            return new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
        }

        static string Sha256(string path)
        {
            byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
            return Convert.ToHexString(hash);
        }
    }
}
