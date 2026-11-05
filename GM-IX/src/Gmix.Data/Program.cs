using System;
using System.Collections.Generic;
using System.IO;
using Gmix.Data;

namespace Gmix.Data.Test
{
    static class Program
    {
        static int Main(string[] args)
        {
            string root = args.Length > 0 ? args[0] : @"C:\Mycode\GM-OFDR\GM-IX\tests\fixtures\schema_v02_sample";
            int failures = 0;
            try
            {
                RunFileRoundTrip(root, ref failures);
                RunAggregation(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 2;
            }
            Console.WriteLine(failures == 0 ? "PASS schema v0.2" : "FAIL schema v0.2: " + failures);
            return failures == 0 ? 0 : 1;
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
                ps[i] = 1e-5;
                baseline[i] = -100.0;
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
            scan.configSnapshot.@interface = new Dictionary<string, object>();
            scan.configSnapshot.afe = new Dictionary<string, object>();
            scan.configSnapshot.range = new Dictionary<string, object>();
            scan.configSnapshot.functional = new Dictionary<string, object>();
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
            double[] heights = { -70.0, -80.0, -58.0, -67.0 };
            for (int i = 0; i < 4; i++)
            {
                events.peaks[i] = new PeakInfo();
                events.peaks[i].locM = locs[i]; events.peaks[i].heightDbRel = heights[i];
                events.peaks[i].baseDbRel = -100.0; events.peaks[i].prominenceDb = heights[i] + 100.0;
                events.peaks[i].peakNo = i + 1; events.peaks[i].distanceM = locs[i]; events.peaks[i].zeroPeak = false;
            }
            events.segments = new SegmentInfo[5];
            for (int i = 0; i < 5; i++)
            {
                events.segments[i] = new SegmentInfo();
                events.segments[i].startIndex = i * outLen / 5;
                events.segments[i].endIndex = (i + 1) * outLen / 5;
                events.segments[i].noiseFloorDbRel = -100.0 - i;
            }
            events.events = new EventInfo[4];
            for (int i = 0; i < 4; i++)
            {
                events.events[i] = new EventInfo();
                events.events[i].locM = locs[i]; events.events[i].peakDbRel = heights[i];
                events.events[i].ilDb = -0.5 - i * 0.1; events.events[i].stepLossDb = 0.5 + i * 0.1;
                events.events[i].rlDbRel = heights[i]; events.events[i].rlDb = null;
                events.events[i].baseBeforeDbRel = -100.0; events.events[i].baseAfterDbRel = -100.5 - i * 0.1;
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
                baseline[i] = i < outLen / 2 ? -90.0 : -102.0;
            }

            var events = new EventsDocument();
            events.display = new DisplayInfo { peakCount = 4, zeroPeakExcluded = true };
            events.peaks = new PeakInfo[4];
            double[] locs = { 1.16924785, 3.33760668, 6.51393779, 8.68628421 };
            double[] heights = { -69.886736, -79.672122, -57.644031, -66.651936 };
            for (int i = 0; i < 4; i++)
            {
                events.peaks[i] = new PeakInfo();
                events.peaks[i].locM = locs[i]; events.peaks[i].heightDbRel = heights[i];
                events.peaks[i].baseDbRel = -100.0; events.peaks[i].prominenceDb = heights[i] + 100.0;
                events.peaks[i].zeroPeak = false;
            }
            events.segments = new SegmentInfo[2];
            events.segments[0] = new SegmentInfo { startIndex = 0, endIndex = 100000, noiseFloorDbRel = -90.0 };
            events.segments[1] = new SegmentInfo { startIndex = 100001, endIndex = 200000, noiseFloorDbRel = -102.0 };

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

            var repeats = new List<double> { 10.0, 10.2, 9.8 };
            var withPrecision = MetricsAggregator.Compute("S2", events, dist, baseline, range, "peak_only", 3, cal, repeats, repeats);
            Check(withPrecision.ilPrecisionDb.HasValue && Math.Abs(withPrecision.ilPrecisionDb.Value - 0.2) < 1e-9,
                "精度 sample std", ref failures);

            Console.WriteLine(string.Format("aggregation: peakOnlyRaw={0:F4} peakOnlyAbs={1:F4} basePlusRaw={2:F4} ILraw={3:F4} IL={4:F4}",
                peakOnly.totalRlRawDb, peakOnly.totalRlDb, basePlus.totalRlRawDb, peakOnly.totalIlRawDb, peakOnly.totalIlDb));
        }

        static void Check(bool condition, string message, ref int failures)
        {
            if (condition) Console.WriteLine("PASS " + message);
            else { Console.WriteLine("FAIL " + message); failures++; }
        }
    }
}
