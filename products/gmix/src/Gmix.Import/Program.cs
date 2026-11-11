using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Gmix.Data;

namespace Gmix.Import
{
    /// <summary>
    /// 昊衡 LGA50-D 远程控制抓包 → GM-IX 组目录（ICD Part A 组结构）。
    ///
    /// 只产出曲线（dist / ps / ps_db）与基于 dB 曲线的事件表；不写 summary、不写 ofdr.json，
    /// 也不冒充 detect.dll 的结果：数据源只有 dB 曲线，没有 P/S/A 原始样本。
    /// 产物直接落在数据根目录下，GM-IX「结果与历史」页可打开。
    /// </summary>
    public static class Program
    {
        // 输出含 v1.0 会话组字段（session.groupId/repeatIndex/isAverage）与 group.json 组结构，按 ICD Part A §1 标 1.0
        const string SchemaVersion = "1.0";
        const string AppVersion = "0.1.0-import";

        /// <summary>供契约测试与自动化调用：解析参数并执行导入；参数错误抛 <see cref="ArgumentException"/>。</summary>
        public static void Import(string[] args)
        {
            Options opt = Options.Parse(args);
            if (opt == null) throw new ArgumentException("缺少 --capture 参数", nameof(args));
            Run(opt);
        }

        static int Main(string[] args)
        {
            Options opt;
            try
            {
                opt = Options.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("[错误] " + ex.Message);
                Console.WriteLine(Options.Usage);
                return 2;
            }

            if (opt == null)
            {
                Console.WriteLine(Options.Usage);
                return 0;
            }

            try
            {
                Run(opt);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[失败] " + ex.Message);
                return 1;
            }
        }

        static void Run(Options opt)
        {
            if (!File.Exists(opt.CapturePath))
                throw new FileNotFoundException("抓包文件不存在", opt.CapturePath);

            List<double[]> available = ParseFrames(File.ReadAllText(opt.CapturePath, Encoding.ASCII),
                opt.PointsPerFrame, out int skipped);
            if (available.Count == 0)
                throw new InvalidDataException("L-01: 抓包文件中未解析到完整帧（单帧点数不足）");

            int frames = opt.Frames > 0 ? Math.Min(opt.Frames, available.Count) : available.Count;

            string groupId = string.IsNullOrEmpty(opt.GroupId) ? NextGroupId(opt.Root) : opt.GroupId;
            string groupDir = Path.Combine(opt.Root, groupId);
            if (Directory.Exists(groupDir))
                throw new IOException("组目录已存在，拒绝覆盖：" + groupDir);

            string createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string captureHash = HashUtil.Sha256File(opt.CapturePath);
            var scanIds = new List<string>();

            for (int i = 0; i < frames; i++)
                scanIds.Add(WriteScan(groupDir, groupId, i, available[i], opt, captureHash));

            WriteGroup(groupDir, groupId, createdAt, scanIds, frames, opt);

            Console.WriteLine("已生成组：" + groupDir);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  扫描 {0} 帧 × {1} 点，点距 {2:F4} mm（可用帧 {3}，跳过 {4}）",
                frames, available[0].Length, opt.PointSpacingM * 1000.0, available.Count, skipped));
            Console.WriteLine("  数据根：" + opt.Root);
            Console.WriteLine("  在 GM-IX「结果与历史」中点『刷新』即可看到该组。");
        }

        // ---------------- 单次扫描 ----------------

        static string WriteScan(string groupDir, string groupId, int index, double[] db, Options opt, string captureHash)
        {
            string scanDir = Path.Combine(groupDir, "R" + index.ToString("D3"));
            Directory.CreateDirectory(scanDir);

            string scanId = "SC-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + index.ToString("D3");

            var dist = new double[db.Length];
            var ps = new double[db.Length];
            for (int i = 0; i < db.Length; i++)
            {
                dist[i] = i * opt.PointSpacingM;
                // GM-IX 的 ps 是线性幅值，曲线走 20log10；这里做同一口径的反变换，保证显示值与抓包 dB 一致
                ps[i] = Math.Pow(10.0, db[i] / 20.0);
            }

            ArrayStore.WriteDoubles(Path.Combine(scanDir, "dist.f64"), dist);
            ArrayStore.WriteDoubles(Path.Combine(scanDir, "ps.f64"), ps);
            ArrayStore.WriteDoubles(Path.Combine(scanDir, "ps_db.f64"), db);

            double endM = dist[dist.Length - 1];
            double startM = Math.Min(opt.StartM, endM);

            JsonStore.Write(Path.Combine(scanDir, "scan.json"),
                BuildScan(scanId, groupId, index, captureHash, startM, endM));

            JsonStore.Write(Path.Combine(scanDir, "events.json"),
                BuildEvents(scanId, db, startM, opt));

            return scanId;
        }

        static ScanDocument BuildScan(string scanId, string groupId, int repeatIndex, string captureHash,
            double startM, double endM)
        {
            var scan = new ScanDocument();
            scan.schemaVersion = SchemaVersion;
            scan.scanId = scanId;
            scan.sequence = repeatIndex;
            scan.capturedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            scan.dataOrigin = "replay";
            scan.appVersion = AppVersion;
            scan.device = new DeviceInfo
            {
                name = "Hengwei",
                model = "LGA50-D",
                sdkVersion = "remote-capture",
                deviceIp = "192.168.2.26"
            };
            scan.acquisition = new AcquisitionInfo
            {
                sampleRateHz = 0,
                sampleRateMhz = 0,
                acqTimeMs = 0,
                segCount = 1,
                triggerMode = 0,
                triggerDelayMs = 0,
                channelMap = new ChannelMapInfo { p = 0, s = 1, aux = 2, ignored = new int[0] },
                expectedFrames = 0,
                bufferBytes = 0,
                bytesPerFrame = 8
            };
            scan.data = new DataFileInfo
            {
                rawFile = null,
                dtype = "float64_le",
                byteOrder = "little",
                interleavedChannels = 1
            };
            scan.quality = new QualityInfo { shortRead = false, frameAligned = true };
            scan.timing = new TimingInfo();
            scan.calibration = new CalibrationRefInfo
            {
                distScale = 1.0,
                state = "uncalibrated",
                calibrationId = ""
            };
            scan.checksums = new ChecksumsInfo { rawSha256 = captureHash };
            scan.session = new SessionInfo
            {
                groupId = groupId,
                repeatIndex = repeatIndex,
                isAverage = false,
                avgMode = "linear_ps",
                averageCount = 1,
                runMode = "single",
                analysisRange = new AnalysisRangeInfo
                {
                    startM = startM,
                    endM = endM,
                    guardM = AcquisitionDefaults.GuardM
                },
                rlMode = "peak_only",
                zeroPeakExcluded = true
            };
            scan.calibrationId = "";
            scan.calibrationState = "uncalibrated";
            return scan;
        }

        // ---------------- 事件表 ----------------

        static EventsDocument BuildEvents(string scanId, double[] db, double startM, Options opt)
        {
            List<PeakHit> hits = CurvePeaks.Find(db, opt.PointSpacingM, opt.ThresholdDb, opt.MinDistanceM);
            int reportHalf = Math.Max(1, (int)Math.Round(opt.WindowM / opt.PointSpacingM));
            double noise = CurvePeaks.Median(db);

            var peaks = new PeakInfo[hits.Count];
            var events = new EventInfo[hits.Count];
            int peakNo = 0;
            int zeroCount = 0;
            double prevAfter = double.NaN;

            for (int k = 0; k < hits.Count; k++)
            {
                PeakHit hit = hits[k];
                bool zero = hit.LocM < startM;
                if (zero) zeroCount++;

                peaks[k] = new PeakInfo
                {
                    locM = hit.LocM,
                    heightDbRel = hit.HeightDb,
                    baseDbRel = hit.BaseDb,
                    prominenceDb = hit.ProminenceDb,
                    peakNo = zero ? (int?)null : ++peakNo,
                    distanceM = hit.LocM,
                    zeroPeak = zero
                };

                int beforeEnd = Math.Max(0, hit.Index - reportHalf - 1);
                int afterStart = Math.Min(db.Length - 1, hit.Index + reportHalf + 1);
                double before = CurvePeaks.MedianRange(db, 0, beforeEnd);
                double after = CurvePeaks.MedianRange(db, afterStart, db.Length - 1);
                double? step = double.IsNaN(prevAfter) ? (double?)null : prevAfter - before;
                prevAfter = after;

                events[k] = new EventInfo
                {
                    locM = hit.LocM,
                    peakDbRel = hit.HeightDb,
                    ilDb = null,
                    rlDb = null,
                    rlDbRel = CurvePeaks.WindowPowerDb(db, hit.Index, reportHalf),
                    baseBeforeDbRel = before,
                    baseAfterDbRel = after,
                    stepLossDb = step,
                    zeroPeak = zero
                };
            }

            var segments = new[]
            {
                new SegmentInfo { startIndex = 0, endIndex = db.Length - 1, noiseFloorDbRel = noise }
            };

            return new EventsDocument
            {
                schemaVersion = SchemaVersion,
                scanId = scanId,
                detector = new DetectorInfo { name = "Gmix.Import", version = AppVersion },
                config = new DetectConfigInfo
                {
                    pkBaselineWin = reportHalf,
                    pkMinProminenceDb = opt.ThresholdDb,
                    pkMinDistanceM = opt.MinDistanceM,
                    pkExcludeRadiusM = 0
                },
                counts = new EventCountsInfo
                {
                    peaks = peaks.Length,
                    segments = segments.Length,
                    events = events.Length
                },
                peaks = peaks,
                segments = segments,
                events = events,
                zeroPeakIncluded = zeroCount > 0,
                units = new UnitsInfo { level = "dB_rel", distance = "m" },
                display = new DisplayInfo
                {
                    peakCount = peaks.Length - zeroCount,
                    zeroPeakExcluded = zeroCount > 0
                },
                arrays = null
            };
        }

        // ---------------- 组元数据 ----------------

        static void WriteGroup(string groupDir, string groupId, string createdAt,
            List<string> scanIds, int frames, Options opt)
        {
            var group = new GroupDocument();
            group.contractVersion = GroupDocument.ContractVersion;
            group.schemaVersion = SchemaVersion;
            group.groupId = groupId;
            group.createdAtUtc = createdAt;
            group.status = "complete";
            group.averageCount = 1;
            group.scanIds = scanIds.ToArray();
            group.validCount = frames;
            group.rejected = new string[0];
            group.avgMode = "linear_ps";
            group.alignment = "same_dist";
            group.calibrationId = "";
            group.configHash = "";
            group.acquisition = new AcquisitionInfo
            {
                segCount = 1,
                bytesPerFrame = 8,
                channelMap = new ChannelMapInfo { p = 0, s = 1, aux = 2, ignored = new int[0] }
            };
            JsonStore.Write(Path.Combine(groupDir, "group.json"), group);
        }

        // ---------------- 抓包解析 ----------------

        /// <summary>
        /// 按帧结束符 <c>!</c> 切帧（协议：单帧 82153 个 <c>%9.2f</c> 值，末值后紧跟 <c>!</c>，无换行）。
        /// 不能按行切：末值与 <c>!</c>、下一帧首值同行（<c>-121.01!   -75.15</c>），按行解析会一次丢掉两个值，
        /// 导致帧错位、曲线右端出现下一帧起点的假峰。
        /// 无 <c>!</c> 时退化为按 <paramref name="pointsPerFrame"/> 等长切分。
        /// 首帧前缀 <c>OSISETOK</c> 与首值同行，由 <see cref="ParseLines"/> 剥离。
        /// </summary>
        internal static List<double[]> ParseFrames(string text, int pointsPerFrame, out int skipped)
        {
            skipped = 0;
            var frames = new List<double[]>();
            string[] segments = text.Split('!');

            if (segments.Length > 1)
            {
                for (int s = 0; s < segments.Length; s++)
                {
                    double[] values = ParseLines(segments[s]);
                    if (values.Length == 0) continue;
                    if (pointsPerFrame > 0 && values.Length != pointsPerFrame)
                    {
                        // 末段通常是截断的半帧；中间段不匹配则按坏帧跳过并计数
                        skipped++;
                        continue;
                    }
                    frames.Add(values);
                }
                return frames;
            }

            double[] all = ParseLines(text);
            if (all.Length == 0) return frames;
            int length = pointsPerFrame > 0 ? Math.Min(pointsPerFrame, all.Length) : all.Length;
            for (int offset = 0; offset + length <= all.Length; offset += length)
            {
                var slice = new double[length];
                Array.Copy(all, offset, slice, 0, length);
                frames.Add(slice);
            }
            if (frames.Count == 0) frames.Add(all);
            return frames;
        }

        /// <summary>
        /// 逐行取首个数字起始字符之后的子串。抓包首行为 <c>OSISETOK   -75.59</c>（控制字与首个数值同行、无分隔），
        /// 其余行为 <c>%9.2f</c> 纯数值。
        /// </summary>
        internal static double[] ParseLines(string text)
        {
            var values = new List<double>(text.Length / 8 + 1);
            foreach (string raw in text.Split('\n'))
            {
                ReadOnlySpan<char> line = raw.AsSpan().Trim();
                if (line.IsEmpty) continue;

                int start = 0;
                while (start < line.Length && !IsNumberStart(line[start])) start++;
                if (start >= line.Length) continue;

                double v;
                if (double.TryParse(line.Slice(start), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    values.Add(v);
            }
            return values.ToArray();
        }

        static bool IsNumberStart(char c)
        {
            return (c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.';
        }

        // ---------------- 路径与标识 ----------------

        static string NextGroupId(string dataRoot)
        {
            string prefix = "GR-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + "-";
            int max = MaxSequence(Path.Combine(dataRoot, "_staging"), prefix);
            if (max < 0) max = 0;
            max = Math.Max(max, MaxSequence(dataRoot, prefix));
            return prefix + (max + 1).ToString("D3");
        }

        /// <summary>返回已用最大序号，无匹配目录时返回 -1。</summary>
        static int MaxSequence(string root, string prefix)
        {
            if (!Directory.Exists(root)) return -1;
            int max = -1;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (name.EndsWith(GroupStore.FailedSuffix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - GroupStore.FailedSuffix.Length);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                int seq;
                if (int.TryParse(name.Substring(prefix.Length), out seq) && seq > max) max = seq;
            }
            return max;
        }

        static string DefaultDataRoot()
        {
            string root = Environment.GetEnvironmentVariable("GMIX_DATA_ROOT");
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GMIX_DATA");
            return Path.GetFullPath(root);
        }

        // ---------------- 命令行 ----------------

        sealed class Options
        {
            public string CapturePath;
            public string Root = DefaultDataRoot();
            public string GroupId;
            public int PointsPerFrame = 82153;
            public int Frames = 1;
            public double PointSpacingM = 0.000185;
            public double StartM = 0.05;
            public double ThresholdDb = 20.0;
            public double MinDistanceM = 0.5;
            public double WindowM = 0.05;

            public const string Usage =
                "用法: Gmix.Import --capture <抓包文件> [--root <数据根>] [--groupId GR-...]\n" +
                "       [--frames N=1] [--points-per-frame 82153] [--point-spacing 0.000185]\n" +
                "       [--start 0.05] [--threshold-db 20] [--min-distance-m 0.5] [--window-m 0.05]\n" +
                "说明: --root 默认取环境变量 GMIX_DATA_ROOT，未设置则用 我的文档\\GMIX_DATA。";

            public static Options Parse(string[] args)
            {
                if (args == null || args.Length == 0) return null;

                var opt = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string key = args[i];
                    string value = i + 1 < args.Length ? args[i + 1] : null;
                    switch (key)
                    {
                        case "--capture": opt.CapturePath = Require(value, key); i++; break;
                        case "--root": opt.Root = Path.GetFullPath(Require(value, key)); i++; break;
                        case "--groupId": opt.GroupId = Require(value, key); i++; break;
                        case "--frames": opt.Frames = ParseInt(value, key); i++; break;
                        case "--points-per-frame": opt.PointsPerFrame = ParseInt(value, key); i++; break;
                        case "--point-spacing": opt.PointSpacingM = ParseDouble(value, key); i++; break;
                        case "--start": opt.StartM = ParseDouble(value, key); i++; break;
                        case "--threshold-db": opt.ThresholdDb = ParseDouble(value, key); i++; break;
                        case "--min-distance-m": opt.MinDistanceM = ParseDouble(value, key); i++; break;
                        case "--window-m": opt.WindowM = ParseDouble(value, key); i++; break;
                        default:
                            throw new ArgumentException("未知参数：" + key);
                    }
                }

                if (string.IsNullOrEmpty(opt.CapturePath))
                    throw new ArgumentException("缺少 --capture");
                if (!(opt.PointSpacingM > 0))
                    throw new ArgumentException("--point-spacing 必须为正数");
                if (!(opt.WindowM > 0))
                    throw new ArgumentException("--window-m 必须为正数");
                if (opt.Frames < 0)
                    throw new ArgumentException("--frames 不能为负");
                return opt;
            }

            static string Require(string value, string key)
            {
                if (string.IsNullOrEmpty(value)) throw new ArgumentException(key + " 缺少取值");
                return value;
            }

            static int ParseInt(string value, string key)
            {
                int v;
                if (!int.TryParse(Require(value, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                    throw new ArgumentException(key + " 不是整数");
                return v;
            }

            static double ParseDouble(string value, string key)
            {
                double v;
                if (!double.TryParse(Require(value, key), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    throw new ArgumentException(key + " 不是数值");
                return v;
            }
        }
    }
}
