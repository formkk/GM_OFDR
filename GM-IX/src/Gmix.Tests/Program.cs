using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Gmix.Data;

namespace Gmix.Data.Test
{
    static class Program
    {
        static int Main(string[] args)
        {
            string root = args.Length > 0 ? args[0] : DefaultFixtureRoot();
            int failures = 0;
            try
            {
                RunFileRoundTrip(root, ref failures);
                RunAggregation(ref failures);
                RunDisplayAndSampler(ref failures);
                RunConfigHash(ref failures);
                RunGroupStore(ref failures);
                RunGroupAssembly(ref failures);
                RunLegacyCompatibility(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 2;
            }
            Console.WriteLine(failures == 0 ? "PASS schema v1.0" : "FAIL schema v1.0: " + failures);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>夹具根目录按仓库相对位置解析，不依赖开发者本机绝对路径。</summary>
        static string DefaultFixtureRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (string.Equals(dir.Name, "GM-IX", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(dir.FullName, "tests", "fixtures", "schema_v02_sample");
                dir = dir.Parent;
            }
            return Path.Combine(AppContext.BaseDirectory, "fixtures", "schema_v02_sample");
        }

        static void RunFileRoundTrip(string root, ref int failures)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);

            const int rateMhz = AcquisitionDefaults.SampleRateMhz;
            const int ms = AcquisitionDefaults.AcqTimeMs;
            int frames = rateMhz * ms * 1000;
            int bytes = rateMhz * ms * 8000;
            int outLen = frames / 2 + 1;
            double distEnd = 20.0;

            // raw.bin：int16 LE，4 通道交织
            var shorts = new short[frames * 4];
            for (int i = 0; i < shorts.Length; i++) shorts[i] = (short)((i % 200) - 100);
            var raw = new byte[bytes];
            Buffer.BlockCopy(shorts, 0, raw, 0, bytes);
            File.WriteAllBytes(Path.Combine(root, "raw.bin"), raw);

            var dist = new double[outLen];
            var freq = new double[outLen];
            var ps = new double[outLen];
            var baseline = new double[outLen];
            for (int i = 0; i < outLen; i++)
            {
                dist[i] = distEnd * i / (outLen - 1);
                freq[i] = 5e6 * i / (outLen - 1);
                ps[i] = 1000.0;
                baseline[i] = 100.0;
            }

            ArrayStore.WriteDoubles(Path.Combine(root, "dist.f64"), dist);
            ArrayStore.WriteDoubles(Path.Combine(root, "freq.f64"), freq);
            ArrayStore.WriteDoubles(Path.Combine(root, "ps.f64"), ps);
            ArrayStore.WriteDoubles(Path.Combine(root, "baseline.f64"), baseline);

            var scan = new ScanDocument();
            scan.schemaVersion = "0.2";
            scan.scanId = "20261104_153000_0001";
            scan.sequence = 1;
            scan.capturedAtUtc = "2026-11-04T15:30:00.123Z";
            scan.dataOrigin = "device";
            scan.appVersion = "0.1.0";
            scan.device = new DeviceInfo();
            scan.device.name = "GM-IX"; scan.device.model = "GM-IX";
            scan.device.sdkVersion = "0.1.0"; scan.device.hostIp = "192.168.5.55"; scan.device.deviceIp = "192.168.5.20";
            scan.acquisition = AcquisitionDefaults.Create();
            scan.configSnapshot = new ConfigSnapshotInfo();
            scan.configSnapshot.@interface = new JsonObject();
            scan.configSnapshot.afe = new JsonObject();
            scan.configSnapshot.range = new JsonObject();
            scan.configSnapshot.functional = new JsonObject();
            scan.configSnapshot.hash = "sha256:test";
            scan.data = new DataFileInfo();
            scan.data.rawFile = "raw.bin"; scan.data.dtype = "int16_le";
            scan.data.byteOrder = "little"; scan.data.interleavedChannels = 4;
            scan.quality = new QualityInfo();
            scan.quality.shortRead = false; scan.quality.frameAligned = true;
            scan.quality.saturation = new SaturationInfo();
            scan.quality.zeroCounts = new ZeroCountsInfo();
            scan.timing = new TimingInfo();
            scan.calibration = new CalibrationRefInfo();
            scan.calibration.distScale = 1.0; scan.calibration.state = "uncalibrated";
            scan.checksums = new ChecksumsInfo();
            scan.checksums.rawSha256 = HashUtil.Sha256File(Path.Combine(root, "raw.bin"));
            scan.session = new SessionInfo();
            scan.session.averageCount = 1; scan.session.runMode = "single";
            scan.session.analysisRange = new AnalysisRangeInfo();
            scan.session.analysisRange.startM = 0.05; scan.session.analysisRange.endM = 20.0; scan.session.analysisRange.guardM = 0.1;
            scan.session.rlMode = "peak_only"; scan.session.zeroPeakExcluded = true;
            scan.calibrationId = null; scan.calibrationState = "uncalibrated";

            var ofdr = new OfdrDocument();
            ofdr.schemaVersion = "0.2"; ofdr.scanId = scan.scanId;
            ofdr.processor = new ProcessorInfo(); ofdr.processor.name = "ofdr"; ofdr.processor.sha256 = "sha256:test";
            ofdr.config = new OfdrConfigInfo();
            ofdr.config.fsHz = rateMhz * 1000000.0; ofdr.config.distScale = 1.0;
            ofdr.config.inputLength = frames; ofdr.config.outputLength = outLen;
            ofdr.arrays = new OfdrArraysInfo();
            ofdr.arrays.dist = new ArrayInfo { file = "dist.f64", dtype = "float64_le", count = outLen, unit = "m" };
            ofdr.arrays.freq = new ArrayInfo { file = "freq.f64", dtype = "float64_le", count = outLen, unit = "Hz" };
            ofdr.arrays.ps = new ArrayInfo { file = "ps.f64", dtype = "float64_le", count = outLen, unit = "linear_amplitude" };
            ofdr.quality = new OfdrQualityInfo();
            ofdr.quality.distMonotonic = true; ofdr.quality.distStepM = distEnd / (outLen - 1);
            ofdr.quality.freqLastHz = rateMhz * 1000000.0 / 2.0; ofdr.quality.psPositive = true;

            var events = new EventsDocument();
            events.schemaVersion = "0.2"; events.scanId = scan.scanId;
            events.detector = new DetectorInfo { name = "detect", sha256 = "sha256:test" };
            events.config = new DetectConfigInfo();
            events.config.nfQ = 85; events.config.nfRadiusM = 0.1; events.config.pkBaselineWin = 100;
            events.config.pkMinProminenceDb = 10; events.config.pkMinDistanceM = 0.5;
            events.config.pkExcludeRadiusM = 0.1; events.config.pkSegPercentile = 95; events.config.pkLastSegTailM = 5;
            events.counts = new EventCountsInfo { peaks = 4, segments = 5, events = 4 };
            events.peaks = new PeakInfo[4];
            double[] locs = { 1.1128, 3.1768, 6.1904, 8.2707 };
            double[] heights = { 130.0, 120.0, 142.0, 133.0 };
            for (int i = 0; i < 4; i++)
            {
                events.peaks[i] = new PeakInfo();
                events.peaks[i].locM = locs[i]; events.peaks[i].heightDbRel = heights[i];
                events.peaks[i].baseDbRel = 100.0; events.peaks[i].prominenceDb = heights[i] - 100.0;
                events.peaks[i].peakNo = i + 1; events.peaks[i].distanceM = locs[i]; events.peaks[i].zeroPeak = false;
            }
            events.segments = new SegmentInfo[5];
            for (int i = 0; i < 5; i++)
            {
                events.segments[i] = new SegmentInfo();
                events.segments[i].startIndex = i * outLen / 5;
                events.segments[i].endIndex = (i + 1) * outLen / 5;
                events.segments[i].noiseFloorDbRel = 100.0 - i;
            }
            events.events = new EventInfo[4];
            for (int i = 0; i < 4; i++)
            {
                events.events[i] = new EventInfo();
                events.events[i].locM = locs[i]; events.events[i].peakDbRel = heights[i];
                events.events[i].ilDb = -0.5 - i * 0.1; events.events[i].stepLossDb = 0.5 + i * 0.1;
                events.events[i].rlDbRel = heights[i]; events.events[i].rlDb = null;
                events.events[i].baseBeforeDbRel = 100.0; events.events[i].baseAfterDbRel = 99.5 - i * 0.1;
                events.events[i].zeroPeak = false;
            }
            events.zeroPeakIncluded = false;
            events.units = new UnitsInfo { level = "dB_rel", distance = "m" };
            events.display = new DisplayInfo { peakCount = 4, zeroPeakExcluded = true };
            events.arrays = new EventsArraysInfo();
            events.arrays.baseline = new ArrayInfo { file = "baseline.f64", dtype = "float64_le", count = outLen };

            var calDoc = new CalibrationDocument();
            calDoc.schemaVersion = "0.2"; calDoc.calibrationId = "CAL-TEST-0001";
            calDoc.createdAtUtc = "2026-11-04T15:00:00Z"; calDoc.@operator = "test";
            calDoc.configHash = "sha256:test"; calDoc.method = "attenuator";
            calDoc.distScale = 0.9515; calDoc.rlOffsetK = -192.0;
            calDoc.ilSlope = 0.5361; calDoc.ilIntercept = -0.1; calDoc.triggerThresholdDb = 35.0;
            calDoc.reference = new ReferenceInfo { type = "attenuator", value = 3.0, serial = "REF-001" };
            calDoc.validationPoints = new ValidationPoint[0];

            var cal = CalibrationModel.FromDocument(calDoc);
            var range = scan.session.analysisRange;
            var summary = MetricsAggregator.Compute(scan.scanId, events, dist, baseline, range,
                "peak_only", 1, cal, null, null);

            JsonStore.Write(Path.Combine(root, "scan.json"), scan);
            JsonStore.Write(Path.Combine(root, "ofdr.json"), ofdr);
            JsonStore.Write(Path.Combine(root, "events.json"), events);
            JsonStore.Write(Path.Combine(root, "summary.json"), summary);
            JsonStore.Write(Path.Combine(root, "calibration.json"), calDoc);

            var scan2 = JsonStore.Read<ScanDocument>(Path.Combine(root, "scan.json"));
            var ofdr2 = JsonStore.Read<OfdrDocument>(Path.Combine(root, "ofdr.json"));
            var events2 = JsonStore.Read<EventsDocument>(Path.Combine(root, "events.json"));
            var summary2 = JsonStore.Read<SummaryDocument>(Path.Combine(root, "summary.json"));
            var cal2 = JsonStore.Read<CalibrationDocument>(Path.Combine(root, "calibration.json"));

            var errors = SchemaValidator.ValidateAll(root, scan2, ofdr2, events2, summary2);
            Check(errors.Count == 0, "schema validation: " + string.Join("; ", errors.ToArray()), ref failures);
            Check(cal2.calibrationId == calDoc.calibrationId && Math.Abs(cal2.distScale - 0.9515) < 1e-12, "calibration round-trip", ref failures);
            Check(summary2.totalRlRawDb.HasValue && summary2.totalRlDb.HasValue, "summary rl fields", ref failures);
            Console.WriteLine(string.Format("sample summary: totalRlRaw={0:F4} totalRl={1:F4} totalIlRaw={2:F4} totalIl={3:F4}",
                summary2.totalRlRawDb, summary2.totalRlDb, summary2.totalIlRawDb, summary2.totalIlDb));
        }

        static void RunAggregation(ref int failures)
        {
            // 用 50 MHz 实测峰位置构造聚合测试
            int outLen = 200001;
            double step = 4.430645e-4;
            var dist = new double[outLen];
            var baseline = new double[outLen];
            for (int i = 0; i < outLen; i++)
            {
                dist[i] = step * i;
                baseline[i] = i < outLen / 2 ? 102.0 : 90.0;
            }

            var events = new EventsDocument();
            events.display = new DisplayInfo { peakCount = 4, zeroPeakExcluded = true };
            events.peaks = new PeakInfo[4];
            double[] locs = { 1.16924785, 3.33760668, 6.51393779, 8.68628421 };
            double[] heights = { 130.113264, 120.327878, 142.355969, 133.348064 };
            for (int i = 0; i < 4; i++)
            {
                events.peaks[i] = new PeakInfo();
                events.peaks[i].locM = locs[i]; events.peaks[i].heightDbRel = heights[i];
                events.peaks[i].baseDbRel = 100.0; events.peaks[i].prominenceDb = heights[i] - 100.0;
                events.peaks[i].zeroPeak = false;
            }
            events.segments = new SegmentInfo[2];
            events.segments[0] = new SegmentInfo { startIndex = 0, endIndex = 100000, noiseFloorDbRel = 102.0 };
            events.segments[1] = new SegmentInfo { startIndex = 100001, endIndex = 200000, noiseFloorDbRel = 90.0 };

            var range = new AnalysisRangeInfo { startM = 0.05, endM = step * (outLen - 1), guardM = 0.1 };
            var cal = new CalibrationModel();
            cal.calibrationId = "CAL-TEST"; cal.distScale = 0.9515;
            cal.rlOffsetK = -192.0; cal.ilSlope = 0.5361; cal.ilIntercept = -0.1;

            var peakOnly = MetricsAggregator.Compute("S1", events, dist, baseline, range, "peak_only", 1, cal, null, null);
            var basePlus = MetricsAggregator.Compute("S1", events, dist, baseline, range, "baseline_plus_peak", 1, cal, null, null);

            Check(peakOnly.totalRlRawDb.HasValue, "peak_only totalRlRaw", ref failures);
            Check(basePlus.totalRlRawDb.HasValue && basePlus.totalRlRawDb.Value > peakOnly.totalRlRawDb.Value,
                "baseline_plus_peak 应大于 peak_only", ref failures);
            Check(Math.Abs(peakOnly.totalIlRawDb.Value - 12.0) < 1e-9, "区间插损 raw=12dB", ref failures);
            Check(Math.Abs(peakOnly.totalIlDb.Value - (0.5361 * 12.0 - 0.1)) < 1e-9, "插损标定公式", ref failures);
            Check(Math.Abs(peakOnly.totalRlDb.Value - (peakOnly.totalRlRawDb.Value - 192.0)) < 1e-9, "绝对回损公式", ref failures);

            // RL 符号口径裁定（2026-11-05）回归：全程不取负；反射增强 → raw 增大、标定后 RL 更接近 0
            Check(peakOnly.totalRlRawDb.Value > 0, "RL 裁定：未标定 totalRlRaw 为正电平刻度", ref failures);
            Check(peakOnly.totalRlDb.Value < 0, "RL 裁定：标定后 totalRl 为负值", ref failures);
            for (int i = 0; i < events.peaks.Length; i++) events.peaks[i].heightDbRel += 3.0;
            var stronger = MetricsAggregator.Compute("S3", events, dist, baseline, range, "peak_only", 1, cal, null, null);
            // 全部峰 +3dB → 每峰功率 ×10^0.3，求和后仍 ×10^0.3 → totalRlRaw 精确 +3dB（正电平刻度直通；若实现取负则增量为 -3dB）
            const double deltaDb = 3.0;
            Check(Math.Abs(stronger.totalRlRawDb.Value - (peakOnly.totalRlRawDb.Value + deltaDb)) < 1e-9,
                "RL 裁定：全部峰 +3dB → totalRlRaw 增大 3dB（正刻度，不取负）", ref failures);
            Check(stronger.totalRlDb.Value > peakOnly.totalRlDb.Value,
                "RL 裁定：反射增强 → 标定后 RL 更接近 0", ref failures);

            var repeats = new List<double> { 10.0, 10.2, 9.8 };
            var withPrecision = MetricsAggregator.Compute("S2", events, dist, baseline, range, "peak_only", 3, cal, repeats, repeats);
            Check(withPrecision.ilPrecisionDb.HasValue && Math.Abs(withPrecision.ilPrecisionDb.Value - 0.2) < 1e-9,
                "精度 sample std", ref failures);

            Console.WriteLine(string.Format("aggregation: peakOnlyRaw={0:F4} peakOnlyAbs={1:F4} basePlusRaw={2:F4} ILraw={3:F4} IL={4:F4}",
                peakOnly.totalRlRawDb, peakOnly.totalRlDb, basePlus.totalRlRawDb, peakOnly.totalIlRawDb, peakOnly.totalIlDb));
        }

        static void RunDisplayAndSampler(ref int failures)
        {
            Check(Math.Abs(DisplayTransform.Level(147.0, DisplayTransform.DefaultOffsetK) - (-45.0)) < 1e-9,
                "DS-01：+147 加偏移 -192 → -45", ref failures);
            Check(DisplayTransform.TotalIlText(12.0) == "-12.00",
                "DS-04：总 IL 取负显示（+12 → -12）", ref failures);
            Check(DisplayTransform.TotalIlText(null) == "—",
                "DS-04：null 显示 —", ref failures);
            Check(DisplayTransform.PrecisionText(null) == "—",
                "DS-05：精度 null 显示 —", ref failures);

            string rl = DisplayTransform.TotalRlText(null, 142.7747, DisplayTransform.DefaultOffsetK, false);
            Check(rl.EndsWith("（未标定）", StringComparison.Ordinal),
                "DS-02：未标定总回损带标注", ref failures);
            Check(Math.Abs(DisplayTransform.Level(142.7747, DisplayTransform.DefaultOffsetK) - (-49.2253)) < 1e-4,
                "DS-02：raw + 默认偏移落回负刻度", ref failures);

            int n = 10000;
            var dist = new double[n];
            var values = new double[n];
            for (int i = 0; i < n; i++)
            {
                dist[i] = 20.0 * i / (n - 1);
                values[i] = 0.1;
            }
            int spike = 5123;
            values[spike] = 99.0;

            var columns = EnvelopeSampler.Reduce(dist, values, 0.0, 20.0, 800);
            bool spikeKept = false;
            double reducedMax = double.NegativeInfinity;
            foreach (var col in columns)
            {
                if (col.MaxIndex == spike) spikeKept = true;
                if (col.MaxIndex >= 0 && values[col.MaxIndex] > reducedMax) reducedMax = values[col.MaxIndex];
            }
            Check(spikeKept, "FR-CHT-07：窄峰在 800 列抽稀后仍保留", ref failures);

            double min, max;
            EnvelopeSampler.GlobalExtremes(values, out min, out max);
            Check(Math.Abs(reducedMax - max) < 1e-12, "抽稀极值与全量极值一致", ref failures);
        }

        static void RunConfigHash(ref int failures)
        {
            var orderedA = new JsonObject();
            orderedA["b"] = JsonValue.Create(2);
            orderedA["a"] = JsonValue.Create(1);

            var orderedB = new JsonObject();
            orderedB["a"] = JsonValue.Create(1);
            orderedB["b"] = JsonValue.Create(2);

            Check(ConfigSnapshot.Canon(orderedA) == ConfigSnapshot.Canon(orderedB),
                "configHash：键序不影响规范化结果", ref failures);

            var acq1 = AcquisitionDefaults.Create();
            var acq2 = AcquisitionDefaults.Create();
            acq2.acqTimeMs = 10;

            JsonObject map = ConfigSnapshot.BuildChannelMap(acq1);
            JsonObject seg1 = ConfigSnapshot.BuildAcquisition(acq1);
            JsonObject seg2 = ConfigSnapshot.BuildAcquisition(acq2);

            string h1 = ConfigSnapshot.ComputeHash(orderedA, map, seg1, null, null, null);
            string h2 = ConfigSnapshot.ComputeHash(orderedB, map, seg1, null, null, null);
            string h3 = ConfigSnapshot.ComputeHash(orderedA, map, seg2, null, null, null);

            Check(h1 == h2, "configHash：同输入同值（键序无关）", ref failures);
            Check(h1 != h3, "configHash：acqTimeMs 变化 → hash 变化", ref failures);
            Check(h1.StartsWith("sha256:", StringComparison.Ordinal) && h1.Length == 71,
                "configHash 格式：sha256: + 64 位小写 hex", ref failures);
            Check(ConfigSnapshot.Canon(null) == "null", "configHash：缺失段规范化为 null", ref failures);
        }

        static void RunGroupStore(ref int failures)
        {
            string root = Path.Combine(Path.GetTempPath(), "gmix_groupstore_" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new GroupStore(root);
                string groupId = "GR-20261105-1530-001";
                string staging = store.StagingGroupDir(groupId);
                string scan = Path.Combine(staging, "R001");
                Directory.CreateDirectory(scan);

                string scanPath = Path.Combine(scan, "scan.json");
                File.WriteAllText(scanPath, "{\"scanId\":\"SC-1\"}", new UTF8Encoding(false));

                var ready = new ReadyDocument
                {
                    schemaVersion = "1.0",
                    scanId = "SC-1",
                    repeatIndex = 1,
                    folder = "R001",
                    files = new[]
                    {
                        new ReadyFileInfo
                        {
                            path = "scan.json",
                            bytes = new FileInfo(scanPath).Length,
                            sha256 = HashUtil.Sha256File(scanPath)
                        }
                    },
                    acquirerVersion = "1.0.0",
                    finishedAtUtc = "2026-11-05T00:00:00Z"
                };
                JsonStore.Write(Path.Combine(scan, GroupStore.ReadyFileName), ready);

                Check(GroupStore.ValidateScan(scan).Count == 0, "T-IPC-01：.ready 清单校验通过", ref failures);

                ready.files[0].bytes = 999999;
                JsonStore.Write(Path.Combine(scan, GroupStore.ReadyFileName), ready);
                Check(GroupStore.ValidateScan(scan).Count > 0, "T-IPC-01：字节数不符被拒绝", ref failures);

                ready.files[0].bytes = new FileInfo(scanPath).Length;
                JsonStore.Write(Path.Combine(scan, GroupStore.ReadyFileName), ready);

                store.Commit(groupId);
                string finalDir = store.FinalGroupDir(groupId);
                Check(Directory.Exists(finalDir), "T-IPC-01：组提交到最终目录", ref failures);
                Check(Directory.GetFiles(finalDir, GroupStore.ReadyFileName, SearchOption.AllDirectories).Length == 0,
                    "T-IPC-01：.ready 不进入最终组目录", ref failures);
                Check(!Directory.Exists(staging), "T-IPC-01：staging 已移走", ref failures);

                Directory.CreateDirectory(Path.Combine(store.StagingGroupDir(groupId), "R001"));
                bool threw = false;
                try { store.Commit(groupId); }
                catch (GroupConsistencyException) { threw = true; }
                Check(threw, "T-IPC-03：目标组已存在时拒绝覆盖", ref failures);

                int removed = store.CleanupStaging();
                Check(removed >= 1, "T-IPC-02：无 .ready 的 staging 残留被清理", ref failures);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        static void RunGroupAssembly(ref int failures)
        {
            string root = Path.Combine(Path.GetTempPath(), "gmix_groupasm_" + Guid.NewGuid().ToString("N"));
            try
            {
                int n = 100;
                var dist = new double[n];
                for (int i = 0; i < n; i++) dist[i] = 20.0 * i / (n - 1);
                var freq = new double[n];
                for (int i = 0; i < n; i++) freq[i] = 1000.0 * i;

                var dirs = new List<string>();
                for (int k = 1; k <= 3; k++)
                {
                    string dir = Path.Combine(root, "R00" + k);
                    Directory.CreateDirectory(dir);
                    ArrayStore.WriteDoubles(Path.Combine(dir, "dist.f64"), dist);
                    ArrayStore.WriteDoubles(Path.Combine(dir, "freq.f64"), freq);
                    var ps = new double[n];
                    for (int i = 0; i < n; i++) ps[i] = k;
                    ArrayStore.WriteDoubles(Path.Combine(dir, "ps.f64"), ps);
                    dirs.Add(dir);
                }

                var sameDist = GroupAssembler.Average(dirs);
                Check(sameDist.Alignment == GroupAssembler.AlignmentSameDist, "组平均：同轴 → same_dist", ref failures);
                Check(sameDist.UsedCount == 3, "组平均：三帧全参与", ref failures);
                Check(sameDist.Ps != null && Math.Abs(sameDist.Ps[0] - 2.0) < 1e-9, "组平均：常数 1/2/3 的平均为 2", ref failures);

                string resampledDir = Path.Combine(root, "R004");
                Directory.CreateDirectory(resampledDir);
                var distShift = new double[n];
                for (int i = 0; i < n; i++) distShift[i] = dist[i] * 1.03;
                var psConst = new double[n];
                for (int i = 0; i < n; i++) psConst[i] = 3.0;
                ArrayStore.WriteDoubles(Path.Combine(resampledDir, "dist.f64"), distShift);
                ArrayStore.WriteDoubles(Path.Combine(resampledDir, "freq.f64"), freq);
                ArrayStore.WriteDoubles(Path.Combine(resampledDir, "ps.f64"), psConst);

                var resampled = GroupAssembler.Average(new List<string> { dirs[0], dirs[1], resampledDir });
                Check(resampled.Alignment == GroupAssembler.AlignmentResampled, "组平均：3% 轴偏差 → resampled", ref failures);
                Check(resampled.Ps != null && Math.Abs(resampled.Ps[50] - 2.0) < 1e-6, "组平均：重采样后均值仍为 2", ref failures);

                string badDir = Path.Combine(root, "R005");
                Directory.CreateDirectory(badDir);
                var distBad = new double[n];
                for (int i = 0; i < n; i++) distBad[i] = dist[i] * 1.30;
                ArrayStore.WriteDoubles(Path.Combine(badDir, "dist.f64"), distBad);
                ArrayStore.WriteDoubles(Path.Combine(badDir, "freq.f64"), freq);
                ArrayStore.WriteDoubles(Path.Combine(badDir, "ps.f64"), psConst);

                var invalid = GroupAssembler.Average(new List<string> { dirs[0], dirs[1], badDir });
                Check(invalid.Alignment == GroupAssembler.AlignmentInvalid, "组平均：30% 轴偏差 → invalid", ref failures);
                Check(invalid.Ps == null, "组平均：invalid 不产出平均谱", ref failures);

                AcquisitionInfo acquisition = AcquisitionDefaults.Create();
                var complete = GroupAssembler.BuildDocument("GR-1", "2026-11-06T00:00:00Z", 5, acquisition,
                    "CAL-1", "sha256:x", new List<string> { "a", "b", "c", "d", "e" }, new List<string>(),
                    GroupAssembler.AlignmentSameDist, "AV-1");
                Check(complete.status == "complete" && complete.validCount == 5, "group.json：全部有效 → complete", ref failures);

                var incomplete = GroupAssembler.BuildDocument("GR-2", "2026-11-06T00:00:00Z", 5, acquisition,
                    "CAL-1", "sha256:x", new List<string> { "a", "b", "c" }, new List<string> { "R004", "R005" },
                    GroupAssembler.AlignmentSameDist, "AV-1");
                Check(incomplete.status == "incomplete" && incomplete.validCount == 3, "group.json：部分失败 → incomplete", ref failures);

                var failed = GroupAssembler.BuildDocument("GR-3", "2026-11-06T00:00:00Z", 5, acquisition,
                    "CAL-1", "sha256:x", new List<string>(),
                    new List<string> { "R001", "R002", "R003", "R004", "R005" },
                    GroupAssembler.AlignmentInvalid, null);
                Check(failed.status == "failed" && failed.validCount == 0, "group.json：全失败 → failed", ref failures);
                Check(failed.schemaVersion == "1.0", "group.json：schemaVersion 1.0", ref failures);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        static void RunLegacyCompatibility(ref int failures)
        {
            const string legacyScan =
                "{\"schemaVersion\":\"0.2\",\"scanId\":\"SC-OLD\",\"sequence\":1," +
                "\"acquisition\":{\"sampleRateMhz\":50,\"acqTimeMs\":8}," +
                "\"session\":{\"averageCount\":1,\"runMode\":\"single\"," +
                "\"analysisRange\":{\"startM\":0.05,\"endM\":20.0,\"guardM\":0.1}}}";

            ScanDocument scan = JsonStore.ReadText<ScanDocument>(legacyScan);
            Check(scan != null && scan.schemaVersion == "0.2" && scan.scanId == "SC-OLD",
                "兼容：0.2 旧 scan.json 可读", ref failures);
            Check(scan.session != null && scan.session.analysisRange != null
                  && Math.Abs(scan.session.analysisRange.endM - 20.0) < 1e-9,
                "兼容：旧 session/analysisRange 字段可读", ref failures);

            const string legacySummary =
                "{\"schemaVersion\":\"0.2\",\"scanId\":\"SC-OLD\",\"totalIlRawDb\":12.0," +
                "\"totalIlDb\":null,\"totalRlRawDb\":140.0,\"totalRlDb\":null}";
            SummaryDocument summary = JsonStore.ReadText<SummaryDocument>(legacySummary);
            Check(summary != null && summary.totalIlRawDb.HasValue && !summary.totalIlDb.HasValue,
                "兼容：0.2 旧 summary.json 可读，缺失标定值为 null", ref failures);
            Check(summary.precisionBasis == null, "兼容：旧数据无 precisionBasis 时为 null", ref failures);
        }

        static void Check(bool condition, string message, ref int failures)
        {
            if (condition) Console.WriteLine("PASS " + message);
            else { Console.WriteLine("FAIL " + message); failures++; }
        }
    }
}
