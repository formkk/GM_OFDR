using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

using Gmix.Data;
using Gmix.Vendor;

namespace Gmix.Acquirer
{
    /// <summary>
    /// 采集进程（ICD Part B §7 / §7.1 的生产者）：
    /// 只写 _staging/&lt;groupId&gt;/Rxxx/ 并写 .ready，不做最终提交，不写 group.json。
    /// 用法：Gmix.Acquirer.exe --groupId GR-... --repeat N --root D:\GMIX_DATA [--runtime dir] [--card 0] [--synthetic]
    ///                            [--start 0.05] [--end 20] [--rl-mode peak_only] [--calibration file]
    /// </summary>
    static class Program
    {
        const string AppVersion = "0.1.0";
        const string SchemaVersion = "1.0";

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Options opt;
            try
            {
                opt = Options.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("参数错误: " + ex.Message);
                Console.Error.WriteLine(Usage());
                return 64;
            }

            var store = new GroupStore(opt.Root);
            string staging = store.StagingGroupDir(opt.GroupId);
            Directory.CreateDirectory(staging);

            VendorRuntime.Initialize(opt.Runtime);

            int exitCode = 0;
            var acquired = new List<string>();

            try
            {
                AcquisitionInfo acquisition = AcquisitionDefaults.Create();
                string configDir = Path.Combine(opt.Runtime, "config");
                ConfigSnapshot config = ConfigSnapshot.Load(configDir, opt.Card, acquisition);
                CalibrationModel calibration = LoadCalibration(opt.Calibration);
                double fs = acquisition.sampleRateHz;

                QtAcquisition device = null;
                uint requiredBytes = 0;
                if (!opt.Synthetic)
                {
                    device = new QtAcquisition(opt.Runtime);
                    Console.WriteLine("SDK 版本: " + device.Version());
                    device.Open(opt.Card);
                    device.Configure((uint)acquisition.sampleRateMhz, (uint)acquisition.acqTimeMs,
                        (uint)acquisition.segCount, acquisition.triggerMode, (uint)acquisition.triggerDelayMs);
                    requiredBytes = device.CalcBufferBytes((uint)acquisition.sampleRateMhz,
                        (uint)acquisition.acqTimeMs, (uint)acquisition.segCount);
                    Console.WriteLine("设备已打开，缓冲需求 " + requiredBytes + " 字节");
                }

                try
                {
                    DateTime startLocal = DateTime.Now;
                    for (int repeat = 1; repeat <= opt.Repeat; repeat++)
                    {
                        string scanId = MakeScanId(startLocal, repeat);
                        string scanDir = Path.Combine(staging, "R" + repeat.ToString("D3"));
                        Directory.CreateDirectory(scanDir);
                        var watch = new Stopwatch();
                        watch.Start();

                        double[] p, s, aux;
                        byte[] raw = null;
                        if (opt.Synthetic)
                        {
                            Synthetic(acquisition, out p, out s, out aux);
                        }
                        else
                        {
                            device.Start();
                            raw = device.Read(requiredBytes, out uint got);
                            device.Stop();
                            if (got != requiredBytes)
                                throw new VendorException(VendorErrorKind.Data, -6,
                                    "短读: " + got + " != " + requiredBytes);
                            short[] samples = RawStore.ReadFramesFrom(raw, out int frames);
                            p = RawStore.Channel(samples, acquisition.channelMap.p);
                            s = RawStore.Channel(samples, acquisition.channelMap.s);
                            aux = RawStore.Channel(samples, acquisition.channelMap.aux);
                        }

                        long acquireMs = watch.ElapsedMilliseconds;
                        var seg = Stopwatch.StartNew();
                        OfdrProcessor.Output ofdr = OfdrProcessor.Process(p, s, aux, fs, calibration.distScale);
                        long ofdrMs = seg.ElapsedMilliseconds; seg.Restart();
                        Detector.Output detect = Detector.Process(ofdr.Dist, ofdr.Ps);
                        long detectMs = seg.ElapsedMilliseconds; seg.Restart();
                        EventsDocument events = BuildEvents(scanId, detect, ofdr.Length);

                        AnalysisRangeInfo range = new AnalysisRangeInfo
                        {
                            startM = opt.StartM,
                            endM = opt.EndM,
                            guardM = 0.1
                        };
                        var empty = new List<double>();
                        SummaryDocument summary = MetricsAggregator.Compute(scanId, events, ofdr.Dist, detect.BaselineDb,
                            range, opt.RlMode, opt.Repeat, calibration, empty, empty, config.Hash);

                        long aggMs = seg.ElapsedMilliseconds; seg.Restart();
                        int totalMs = (int)watch.ElapsedMilliseconds;
                        WriteBundle(scanDir, scanId, repeat, opt, acquisition, config, calibration,
                            ofdr, detect, events, summary, raw, totalMs);
                        long writeMs = seg.ElapsedMilliseconds;

                        acquired.Add(scanId);
                        Console.WriteLine(string.Format(
                            "{0}: 峰 {1}，事件 {2}，总IL raw={3}；采集={4} 处理={5} 检测={6} 聚合={7} 落盘={8} 总计={9} ms",
                            scanId, detect.Peaks.Length, detect.Events.Length, summary.totalIlRawDb,
                            acquireMs, ofdrMs, detectMs, aggMs, writeMs, totalMs + writeMs));
                    }
                }
                finally
                {
                    if (device != null)
                    {
                        device.Stop();
                        device.Close();
                    }
                }
            }
            catch (VendorException ex)
            {
                Console.Error.WriteLine("采集失败: " + VendorErrorText.Describe(ex));
                exitCode = 3;
            }
            catch (GroupConsistencyException ex)
            {
                Console.Error.WriteLine("组一致性错误: " + ex.Message);
                exitCode = 4;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL: " + ex);
                exitCode = 70;
            }

            if (exitCode != 0)
            {
                try
                {
                    store.MarkFailed(opt.GroupId);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("标记 .failed 失败: " + ex.Message);
                }
            }

            Console.WriteLine("完成: " + acquired.Count + "/" + opt.Repeat + " 次采集");
            return exitCode;
        }

        static void Synthetic(AcquisitionInfo acquisition, out double[] p, out double[] s, out double[] aux)
        {
            long frames = acquisition.expectedFrames;
            int n = (int)frames;
            p = new double[n];
            s = new double[n];
            aux = new double[n];
            for (int i = 0; i < n; i++)
            {
                double t = i;
                p[i] = Math.Sin(2 * Math.PI * 0.03 * t);
                s[i] = Math.Sin(2 * Math.PI * 0.03 * t + 0.5);
                aux[i] = Math.Sin(2 * Math.PI * 0.02 * t);
            }
        }

        static EventsDocument BuildEvents(string scanId, Detector.Output detect, int outputLength)
        {
            // 映射集中于 Gmix.Vendor.VendorMapper，采集与编排共用（ICD Part A §4）
            return VendorMapper.ToEventsDocument(detect, scanId, SchemaVersion);
        }

        static void WriteBundle(string scanDir, string scanId, int repeatIndex, Options opt,
            AcquisitionInfo acquisition, ConfigSnapshot config, CalibrationModel calibration,
            OfdrProcessor.Output ofdr, Detector.Output detect, EventsDocument events, SummaryDocument summary,
            byte[] raw, int totalMs)
        {
            var files = new List<ReadyFileInfo>();

            string rawHash = null;
            if (raw != null)
            {
                string rawPath = Path.Combine(scanDir, "raw.bin");
                using (var fs = new FileStream(rawPath, FileMode.Create, FileAccess.Write))
                    fs.Write(raw, 0, raw.Length);
                rawHash = HashUtil.Sha256File(rawPath);
                files.Add(Describe(scanDir, "raw.bin"));
            }

            JsonStore.Write(Path.Combine(scanDir, "scan.json"), BuildScan(scanId, repeatIndex, opt,
                acquisition, config, calibration, rawHash, totalMs));
            files.Add(Describe(scanDir, "scan.json"));

            JsonStore.Write(Path.Combine(scanDir, "ofdr.json"), BuildOfdr(scanId, opt, calibration, ofdr));
            files.Add(Describe(scanDir, "ofdr.json"));

            files.Add(WriteArray(scanDir, "dist.f64", ofdr.Dist));
            files.Add(WriteArray(scanDir, "freq.f64", ofdr.Freq));
            files.Add(WriteArray(scanDir, "ps.f64", ofdr.Ps));
            files.Add(WriteArray(scanDir, "ps_db.f64", detect.PsDb));
            files.Add(WriteArray(scanDir, "baseline.f64", detect.BaselineDb));

            JsonStore.Write(Path.Combine(scanDir, "events.json"), events);
            files.Add(Describe(scanDir, "events.json"));

            JsonStore.Write(Path.Combine(scanDir, "summary.json"), summary);
            files.Add(Describe(scanDir, "summary.json"));

            WriteCalibrationSnapshot(scanDir, calibration, config.Hash);
            files.Add(Describe(scanDir, "calibration.json"));

            var ready = new ReadyDocument
            {
                schemaVersion = "1.0",
                scanId = scanId,
                repeatIndex = repeatIndex,
                folder = Path.GetFileName(scanDir),
                files = files.ToArray(),
                acquirerVersion = AppVersion,
                finishedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
            JsonStore.Write(Path.Combine(scanDir, GroupStore.ReadyTempFileName), ready);
            File.Move(Path.Combine(scanDir, GroupStore.ReadyTempFileName), Path.Combine(scanDir, GroupStore.ReadyFileName));
        }

        static ScanDocument BuildScan(string scanId, int repeatIndex, Options opt, AcquisitionInfo acquisition,
            ConfigSnapshot config, CalibrationModel calibration, string rawHash, int totalMs)
        {
            var scan = new ScanDocument();
            scan.schemaVersion = SchemaVersion;
            scan.scanId = scanId;
            scan.sequence = repeatIndex;
            scan.capturedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            scan.dataOrigin = opt.Synthetic ? "synthetic" : "device";
            scan.appVersion = AppVersion;
            scan.device = new DeviceInfo
            {
                name = "GM-IX",
                model = "GM-IX",
                sdkVersion = "0.1.0",
                hostIp = "192.168.5.55",
                deviceIp = "192.168.5.20"
            };
            scan.acquisition = acquisition;
            scan.configSnapshot = new ConfigSnapshotInfo
            {
                @interface = config.Interface,
                afe = config.Afe,
                range = config.Range,
                functional = config.Functional,
                hash = config.Hash
            };
            scan.data = new DataFileInfo
            {
                rawFile = rawHash == null ? null : "raw.bin",
                dtype = "int16_le",
                byteOrder = "little",
                interleavedChannels = 4
            };
            scan.quality = new QualityInfo { shortRead = false, frameAligned = true };
            scan.timing = new TimingInfo { totalMs = totalMs };
            scan.calibration = new CalibrationRefInfo
            {
                distScale = calibration.distScale,
                state = calibration.rlOffsetK.HasValue ? "calibrated" : "uncalibrated",
                calibrationId = calibration.calibrationId
            };
            scan.checksums = new ChecksumsInfo { rawSha256 = rawHash };
            scan.session = new SessionInfo
            {
                groupId = opt.GroupId,
                repeatIndex = repeatIndex,
                isAverage = false,
                avgMode = "linear_ps",
                averageCount = opt.Repeat,
                runMode = "single",
                analysisRange = new AnalysisRangeInfo { startM = opt.StartM, endM = opt.EndM, guardM = 0.1 },
                rlMode = opt.RlMode,
                zeroPeakExcluded = true
            };
            scan.calibrationId = calibration.calibrationId;
            scan.calibrationState = calibration.rlOffsetK.HasValue ? "calibrated" : "uncalibrated";
            return scan;
        }

        static OfdrDocument BuildOfdr(string scanId, Options opt, CalibrationModel calibration, OfdrProcessor.Output ofdr)
        {
            double step = ofdr.Dist.Length > 1 ? ofdr.Dist[1] - ofdr.Dist[0] : 0;
            bool monotonic = true;
            for (int i = 1; i < ofdr.Dist.Length; i++)
            {
                if (!(ofdr.Dist[i] > ofdr.Dist[i - 1])) { monotonic = false; break; }
            }
            bool positive = true;
            foreach (double v in ofdr.Ps)
            {
                if (!(v > 0)) { positive = false; break; }
            }

            var doc = new OfdrDocument();
            doc.schemaVersion = SchemaVersion;
            doc.scanId = scanId;
            doc.processor = new ProcessorInfo { name = "ofdr.dll" };
            doc.config = new OfdrConfigInfo
            {
                fsHz = 50e6,
                distScale = calibration.distScale,
                inputLength = ofdr.Length * 2,
                outputLength = ofdr.Length
            };
            doc.arrays = new OfdrArraysInfo
            {
                dist = new ArrayInfo { file = "dist.f64", dtype = "float64_le", count = ofdr.Length, unit = "m" },
                freq = new ArrayInfo { file = "freq.f64", dtype = "float64_le", count = ofdr.Length, unit = "Hz" },
                ps = new ArrayInfo { file = "ps.f64", dtype = "float64_le", count = ofdr.Length, unit = "linear" }
            };
            doc.quality = new OfdrQualityInfo
            {
                distMonotonic = monotonic,
                distStepM = step,
                freqLastHz = ofdr.Freq.Length > 0 ? ofdr.Freq[ofdr.Freq.Length - 1] : 0,
                psPositive = positive
            };
            return doc;
        }

        static ReadyFileInfo WriteArray(string scanDir, string name, double[] values)
        {
            ArrayStore.WriteDoubles(Path.Combine(scanDir, name), values);
            return Describe(scanDir, name);
        }

        static void WriteCalibrationSnapshot(string scanDir, CalibrationModel calibration, string configHash)
        {
            var doc = new CalibrationDocument();
            doc.schemaVersion = SchemaVersion;
            doc.calibrationId = calibration.calibrationId;
            doc.createdAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            doc.configHash = configHash;
            doc.method = "vendor";
            doc.distScale = calibration.distScale;
            doc.rlOffsetK = calibration.rlOffsetK;
            doc.ilSlope = calibration.ilSlope;
            doc.ilIntercept = calibration.ilIntercept;
            doc.triggerThresholdDb = calibration.triggerThresholdDb;
            JsonStore.Write(Path.Combine(scanDir, "calibration.json"), doc);
        }

        static ReadyFileInfo Describe(string scanDir, string name)
        {
            string path = Path.Combine(scanDir, name);
            return new ReadyFileInfo
            {
                path = name,
                bytes = new FileInfo(path).Length,
                sha256 = HashUtil.Sha256File(path)
            };
        }

        static CalibrationModel LoadCalibration(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new CalibrationModel();
            return CalibrationModel.FromDocument(JsonStore.Read<CalibrationDocument>(path));
        }

        static string MakeScanId(DateTime startLocal, int repeatIndex)
        {
            return "SC-" + startLocal.ToString("yyyyMMdd-HHmmss") + "-" + repeatIndex.ToString("D3");
        }

        static string Usage()
        {
            return "用法: Gmix.Acquirer --groupId GR-YYYYMMDD-HHMM-NNN --repeat N --root <数据根目录> " +
                   "[--runtime <SDK 目录>] [--card 0] [--synthetic] [--start 0.05] [--end 20] " +
                   "[--rl-mode peak_only|baseline_plus_peak] [--calibration <calibration.json>]";
        }

        sealed class Options
        {
            public string GroupId;
            public int Repeat = 1;
            public string Root;
            public string Runtime;
            public uint Card;
            public bool Synthetic;
            public double StartM = 0.05;
            public double EndM = 20.0;
            public string RlMode = "peak_only";
            public string Calibration;

            public static Options Parse(string[] args)
            {
                var opt = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--groupId":
                            opt.GroupId = args[++i];
                            break;
                        case "--repeat":
                            opt.Repeat = int.Parse(args[++i]);
                            break;
                        case "--root":
                            opt.Root = args[++i];
                            break;
                        case "--runtime":
                            opt.Runtime = args[++i];
                            break;
                        case "--card":
                            opt.Card = uint.Parse(args[++i]);
                            break;
                        case "--synthetic":
                            opt.Synthetic = true;
                            break;
                        case "--start":
                            opt.StartM = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        case "--end":
                            opt.EndM = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        case "--rl-mode":
                            opt.RlMode = args[++i];
                            break;
                        case "--calibration":
                            opt.Calibration = args[++i];
                            break;
                        default:
                            throw new ArgumentException("未知参数: " + args[i]);
                    }
                }

                if (string.IsNullOrEmpty(opt.GroupId))
                    throw new ArgumentException("缺少 --groupId");
                if (string.IsNullOrEmpty(opt.Root))
                    throw new ArgumentException("缺少 --root");
                if (opt.Repeat <= 0)
                    throw new ArgumentException("--repeat 必须大于 0");
                if (!(opt.EndM > opt.StartM))
                    throw new ArgumentException("--end 必须大于 --start");

                opt.Root = Path.GetFullPath(opt.Root);
                if (string.IsNullOrEmpty(opt.Runtime))
                    opt.Runtime = Path.GetDirectoryName(typeof(Program).Assembly.Location);
                opt.Runtime = Path.GetFullPath(opt.Runtime);
                return opt;
            }
        }
    }
}
