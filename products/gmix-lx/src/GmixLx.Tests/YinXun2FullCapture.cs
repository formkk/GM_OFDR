using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using GmixLx.Core;
using GmixLx.Orchestration;
using GmixLx.Vendor;

namespace GmixLx.Tests
{
    /// <summary>
    /// 映讯_2 全量原始数据采集：64 路 × N 帧，逐步保存 160000 点 double 原始电平。
    /// 手动运行：<c>--live-yinxun2-capture [frames] [outDir]</c>
    /// 输出目录默认在 devices/yinxun2/capture/（已 gitignore）。
    /// </summary>
    internal static class YinXun2FullCapture
    {
        public static int Run(int frames, string outDir)
        {
            if (frames <= 0) frames = 4;

            string sdk = YinXun2LiveSmoke.Resolve("references/YinXun_2/customer");
            string calibrationPath = YinXun2LiveSmoke.Resolve(
                "products/gmix-lx/config/yinxun2_reference_calibration.json");
            if (sdk == null || calibrationPath == null)
            {
                Console.WriteLine("YX2 CAPTURE FATAL: 找不到 SDK 或标定文件");
                return 2;
            }

            YinXun2ReferenceCalibration calibration = YinXun2ReferenceCalibration.Load(calibrationPath);
            if (string.IsNullOrEmpty(outDir))
            {
                outDir = YinXun2LiveSmoke.Resolve("devices/yinxun2/capture");
                outDir = Path.Combine(outDir, "full64_" + frames + "f_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            }
            Directory.CreateDirectory(outDir);

            double roiStart = calibration.ToYinXunDistance(4.44);
            double roiEnd = calibration.ToYinXunDistance(10.6005);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "YX2 CAPTURE  64 paths x {0} frames  roi(native)=[{1:F3},{2:F3}]  out={3}",
                frames, roiStart, roiEnd, outDir));

            var scans = new StreamWriter(Path.Combine(outDir, "scans.csv"));
            scans.WriteLine("IN,OUT,Frame,AcquireMs,Points,AverageCount,Ready");
            long started = Environment.TickCount64;
            int pointCount = 0;

            using (var client = new YinXun2Client(new YinXun2NativeSdk()))
            using (var ocs1x8 = new Ocs1x8Client(new OcsSerialTransport("COM4", 9600), 300))
            using (var ocs8x8 = new Ocs8x8Client(new OcsSerialTransport("COM3", 115200), 300))
            {
                client.Open(sdk, 1, YinXun2ProtocolConstants.DefaultTemporalAverageN);
                client.SetAnalysisRoi(roiStart, roiEnd);
                ocs1x8.Open();
                ocs8x8.Open();

                try
                {
                    for (int inChannel = 1; inChannel <= 8; inChannel++)
                    {
                        ocs1x8.SetOutput(inChannel);
                        for (int outChannel = 1; outChannel <= 8; outChannel++)
                        {
                            var matrix = new int[8];
                            matrix[inChannel - 1] = outChannel;
                            ocs8x8.SetMatrix(matrix);
                            System.Threading.Thread.Sleep(50);

                            string pathDir = Path.Combine(outDir,
                                "in" + inChannel.ToString("00") + "_out" + outChannel.ToString("00"));
                            Directory.CreateDirectory(pathDir);

                            for (int f = 0; f < frames; f++)
                            {
                                var watch = Stopwatch.StartNew();
                                YinXun2Measurement frame = client.AcquireReady(30000);
                                watch.Stop();
                                pointCount = frame.LevelsDb.Length;

                                using (var writer = new BinaryWriter(File.Create(
                                    Path.Combine(pathDir, "frame_" + f.ToString("000") + ".bin"))))
                                {
                                    for (int i = 0; i < frame.LevelsDb.Length; i++) writer.Write(frame.LevelsDb[i]);
                                }

                                scans.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "{0},{1},{2},{3:F1},{4},{5},{6}",
                                    inChannel, outChannel, f, watch.Elapsed.TotalMilliseconds,
                                    frame.LevelsDb.Length, frame.AverageCount, frame.AverageReady ? 1 : 0));
                                Console.Write('.');
                            }
                        }
                        Console.WriteLine("  IN" + inChannel + " done");
                    }
                }
                finally
                {
                    scans.Dispose();
                }
            }

            long elapsed = Environment.TickCount64 - started;
            string meta = string.Format(CultureInfo.InvariantCulture,
                "{{\n  \"device\": \"YinXun_2\",\n  \"captured_at\": \"{0}\",\n  \"points\": {1},\n" +
                "  \"step_m\": {2},\n  \"range_m\": {3},\n  \"frames\": {4},\n" +
                "  \"spatial_m\": 1,\n  \"temporal_n\": {5},\n  \"roi_native_m\": [{6}, {7}],\n" +
                "  \"calibration\": \"{8}\",\n  \"elapsed_ms\": {9}\n}}\n",
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), pointCount,
                YinXun2ProtocolConstants.PitchM.ToString("R", CultureInfo.InvariantCulture),
                ((pointCount - 1) * YinXun2ProtocolConstants.PitchM).ToString("R", CultureInfo.InvariantCulture),
                frames, YinXun2ProtocolConstants.DefaultTemporalAverageN,
                roiStart.ToString("R", CultureInfo.InvariantCulture),
                roiEnd.ToString("R", CultureInfo.InvariantCulture),
                calibrationPath.Replace("\\", "\\\\"), elapsed);
            File.WriteAllText(Path.Combine(outDir, "meta.json"), meta);

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "DONE  {0} frames x 64 paths, {1} points, elapsed {2:F1} s, dir={3}",
                frames, pointCount, elapsed / 1000.0, outDir));
            return 0;
        }
    }
}
