using System;
using System.Collections.Generic;
using System.IO;

using Gmix.Data;
using Gmix.Vendor;

namespace Gmix.Commit
{
    /// <summary>
    /// 组提交流水线（ICD Part B §7 的唯一提交者）：
    /// 校验 staging → 生成 AVG/ → 写 group.json → 原子提交。
    /// 由 Gmix.Commit.exe 与 Gmix.App 共用，避免两处提交逻辑漂移。
    /// </summary>
    public static class GroupCommitPipeline
    {
        const string SchemaVersion = "1.0";
        const string AppVersion = "0.1.0";

        /// <summary>执行提交；返回退出码（0 成功，2 staging 缺失，3 全失败，4 组一致性，5 元数据不一致，6 轴不可对齐，70 未知）。</summary>
        public static int Run(string root, string groupId, string runtime, string calibrationPath,
            Action<string> log, Action<string> error)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (groupId == null) throw new ArgumentNullException(nameof(groupId));
            if (log == null) log = delegate { };
            if (error == null) error = delegate { };

            var store = new GroupStore(root);
            string staging = store.StagingGroupDir(groupId);
            if (!Directory.Exists(staging))
            {
                error("staging 不存在: " + staging);
                return 2;
            }

            if (runtime != null) VendorRuntime.Initialize(runtime);

            try
            {
                return Run(store, staging, groupId, calibrationPath, log, error);
            }
            catch (GroupConsistencyException ex)
            {
                error("组一致性错误: " + ex.Message);
                return 4;
            }
            catch (Exception ex)
            {
                error("FATAL: " + ex);
                return 70;
            }
        }

        static int Run(GroupStore store, string staging, string groupId, string calibrationPath,
            Action<string> log, Action<string> error)
        {
            var scanDirs = new List<string>();
            foreach (string dir in Directory.GetDirectories(staging))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("R", StringComparison.Ordinal) && name.Length == 4) scanDirs.Add(dir);
            }
            scanDirs.Sort(StringComparer.Ordinal);
            if (scanDirs.Count == 0)
            {
                error("没有 Rxxx 扫描目录");
                store.MarkFailed(groupId);
                return 3;
            }

            var rejected = new List<string>();
            var validDirs = new List<string>();
            foreach (string dir in scanDirs)
            {
                var errors = GroupStore.ValidateScan(dir);
                if (errors.Count == 0)
                {
                    validDirs.Add(dir);
                }
                else
                {
                    rejected.Add(Path.GetFileName(dir));
                    foreach (string item in errors) error("  " + Path.GetFileName(dir) + ": " + item);
                }
            }
            if (validDirs.Count == 0)
            {
                error("全部扫描校验失败，组标记 failed");
                store.MarkFailed(groupId);
                return 3;
            }

            var scans = new List<ScanDocument>();
            foreach (string dir in validDirs)
                scans.Add(JsonStore.Read<ScanDocument>(Path.Combine(dir, "scan.json")));

            string configHash = scans[0].configSnapshot == null ? null : scans[0].configSnapshot.hash;
            string calibrationId = scans[0].calibrationId;
            for (int i = 1; i < scans.Count; i++)
            {
                string otherHash = scans[i].configSnapshot == null ? null : scans[i].configSnapshot.hash;
                if (!string.Equals(configHash, otherHash, StringComparison.Ordinal)
                    || !string.Equals(calibrationId, scans[i].calibrationId, StringComparison.Ordinal))
                {
                    error("组内 configHash/calibrationId 不一致，拒绝平均");
                    store.MarkFailed(groupId);
                    return 5;
                }
            }

            AcquisitionInfo acquisition = scans[0].acquisition;
            AnalysisRangeInfo range = scans[0].session != null && scans[0].session.analysisRange != null
                ? scans[0].session.analysisRange
                : new AnalysisRangeInfo { startM = 0.05, endM = 20.0, guardM = 0.1 };
            string rlMode = scans[0].session != null && !string.IsNullOrEmpty(scans[0].session.rlMode)
                ? scans[0].session.rlMode : "peak_only";

            var scanIds = new List<string>();
            foreach (ScanDocument scan in scans) scanIds.Add(scan.scanId);

            CalibrationModel calibration = LoadCalibration(calibrationPath, validDirs[0]);

            string groupCreatedAt = scans[0].capturedAtUtc ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            GroupAssembler.AveragedSpectrum average = null;
            string avgScanId = null;
            SummaryDocument avgSummary = null;
            Detector.Output detect = null;

            if (validDirs.Count > 1)
            {
                average = GroupAssembler.Average(validDirs);
                foreach (string warning in average.Warnings) log("  warning: " + warning);

                if (average.Alignment == GroupAssembler.AlignmentInvalid || average.Ps == null)
                {
                    error("分布轴不可对齐（alignment=invalid），拒绝生成 AVG");
                    var failedDoc = GroupAssembler.BuildDocument(groupId, groupCreatedAt, scanDirs.Count,
                        acquisition, calibrationId, configHash, scanIds, rejected,
                        GroupAssembler.AlignmentInvalid, null);
                    failedDoc.status = "failed";
                    JsonStore.Write(Path.Combine(staging, "group.json"), failedDoc);
                    store.MarkFailed(groupId);
                    return 6;
                }

                avgScanId = "AV-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + validDirs.Count.ToString("D3");
                detect = Detector.Process(average.Dist, average.Ps);
                EventsDocument avgEvents = VendorMapper.ToEventsDocument(detect, avgScanId, SchemaVersion);

                string basis;
                var repeatIl = new List<double>();
                var repeatRl = new List<double>();
                CollectPrecision(validDirs, calibration, out basis, repeatIl, repeatRl);

                avgSummary = MetricsAggregator.Compute(avgScanId, avgEvents, average.Dist, detect.BaselineDb,
                    range, rlMode, scanDirs.Count, calibration, repeatIl, repeatRl, configHash);
                avgSummary.precisionBasis = basis;
                avgSummary.precisionMode = "sample_std";
                avgSummary.precisionCount = repeatIl.Count;

                WriteAverage(validDirs[0], scans[0], average, detect, avgEvents, avgSummary, avgScanId,
                    groupId, scanIds, calibration, configHash, range, rlMode, scanDirs.Count);

                log(string.Format("AVG 已生成: {0}，alignment={1}，有效 {2}/{3}，精度 basis={4}",
                    avgScanId, average.Alignment, average.UsedCount, scanDirs.Count, basis));
            }

            var group = GroupAssembler.BuildDocument(groupId, groupCreatedAt, scanDirs.Count,
                acquisition, calibrationId, configHash, scanIds, rejected,
                average == null ? GroupAssembler.AlignmentSameDist : average.Alignment, avgScanId);
            JsonStore.Write(Path.Combine(staging, "group.json"), group);

            store.Commit(groupId);

            log(string.Format("组已提交: {0} → {1}（status={2}，valid={3}/{4}，rejected={5}）",
                groupId, store.FinalGroupDir(groupId), group.status, group.validCount,
                group.averageCount, group.rejected.Length));
            return 0;
        }

        static void CollectPrecision(List<string> dirs, CalibrationModel calibration,
            out string basis, List<double> ilValues, List<double> rlValues)
        {
            bool calibrated = calibration != null && calibration.HasIlCalibration() && calibration.HasAbsoluteRl();
            bool allHaveCalibrated = calibrated;

            var summaries = new List<SummaryDocument>();
            foreach (string dir in dirs)
            {
                string path = Path.Combine(dir, "summary.json");
                if (!File.Exists(path)) { allHaveCalibrated = false; continue; }
                SummaryDocument summary = JsonStore.Read<SummaryDocument>(path);
                summaries.Add(summary);
                if (!summary.totalIlDb.HasValue || !summary.totalRlDb.HasValue) allHaveCalibrated = false;
            }

            basis = allHaveCalibrated ? "calibrated" : "raw";
            foreach (SummaryDocument summary in summaries)
            {
                if (basis == "calibrated")
                {
                    if (summary.totalIlDb.HasValue) ilValues.Add(summary.totalIlDb.Value);
                    if (summary.totalRlDb.HasValue) rlValues.Add(summary.totalRlDb.Value);
                }
                else
                {
                    if (summary.totalIlRawDb.HasValue) ilValues.Add(summary.totalIlRawDb.Value);
                    if (summary.totalRlRawDb.HasValue) rlValues.Add(summary.totalRlRawDb.Value);
                }
            }
        }

        static CalibrationModel LoadCalibration(string explicitPath, string firstScanDir)
        {
            string path = explicitPath;
            if (string.IsNullOrEmpty(path))
            {
                string snapshot = Path.Combine(firstScanDir, "calibration.json");
                if (File.Exists(snapshot)) path = snapshot;
            }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new CalibrationModel();
            return CalibrationModel.FromDocument(JsonStore.Read<CalibrationDocument>(path));
        }

        static void WriteAverage(string firstScanDir, ScanDocument firstScan,
            GroupAssembler.AveragedSpectrum average, Detector.Output detect, EventsDocument events,
            SummaryDocument summary, string avgScanId, string groupId, List<string> scanIds,
            CalibrationModel calibration, string configHash, AnalysisRangeInfo range, string rlMode, int plannedCount)
        {
            string avgDir = Path.Combine(Path.GetDirectoryName(firstScanDir), "AVG");
            Directory.CreateDirectory(avgDir);

            ArrayStore.WriteDoubles(Path.Combine(avgDir, "dist.f64"), average.Dist);
            ArrayStore.WriteDoubles(Path.Combine(avgDir, "freq.f64"), average.Freq);
            ArrayStore.WriteDoubles(Path.Combine(avgDir, "ps.f64"), average.Ps);
            ArrayStore.WriteDoubles(Path.Combine(avgDir, "ps_db.f64"), detect.PsDb);
            ArrayStore.WriteDoubles(Path.Combine(avgDir, "baseline.f64"), detect.BaselineDb);

            JsonStore.Write(Path.Combine(avgDir, "events.json"), events);
            JsonStore.Write(Path.Combine(avgDir, "summary.json"), summary);
            JsonStore.Write(Path.Combine(avgDir, "scan.json"), BuildAverageScan(firstScan, avgScanId,
                groupId, scanIds, calibration, configHash, range, rlMode, plannedCount));
            JsonStore.Write(Path.Combine(avgDir, "ofdr.json"), BuildAverageOfdr(firstScan, avgScanId, average, calibration));

            string calibrationSnapshot = Path.Combine(firstScanDir, "calibration.json");
            if (File.Exists(calibrationSnapshot))
                File.Copy(calibrationSnapshot, Path.Combine(avgDir, "calibration.json"), true);
        }

        static ScanDocument BuildAverageScan(ScanDocument first, string avgScanId, string groupId,
            List<string> scanIds, CalibrationModel calibration, string configHash,
            AnalysisRangeInfo range, string rlMode, int plannedCount)
        {
            return new ScanDocument
            {
                schemaVersion = SchemaVersion,
                scanId = avgScanId,
                sequence = 0,
                capturedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                dataOrigin = first.dataOrigin,
                appVersion = AppVersion,
                device = first.device,
                acquisition = first.acquisition,
                configSnapshot = first.configSnapshot,
                data = new DataFileInfo { rawFile = null, dtype = "int16_le", byteOrder = "little", interleavedChannels = 4 },
                quality = new QualityInfo { shortRead = false, frameAligned = true },
                timing = new TimingInfo(),
                calibration = first.calibration,
                checksums = new ChecksumsInfo(),
                session = new SessionInfo
                {
                    groupId = groupId,
                    repeatIndex = null,
                    isAverage = true,
                    averageOf = ToArray(scanIds),
                    avgMode = "linear_ps",
                    averageCount = plannedCount,
                    runMode = "single",
                    analysisRange = range,
                    rlMode = rlMode,
                    zeroPeakExcluded = true
                },
                calibrationId = calibration.calibrationId,
                calibrationState = calibration.rlOffsetK.HasValue ? "calibrated" : "uncalibrated"
            };
        }

        static OfdrDocument BuildAverageOfdr(ScanDocument first, string avgScanId,
            GroupAssembler.AveragedSpectrum average, CalibrationModel calibration)
        {
            double step = average.Dist.Length > 1 ? average.Dist[1] - average.Dist[0] : 0;
            return new OfdrDocument
            {
                schemaVersion = SchemaVersion,
                scanId = avgScanId,
                processor = new ProcessorInfo { name = "ofdr.dll" },
                config = new OfdrConfigInfo
                {
                    fsHz = first.acquisition == null ? 50e6 : first.acquisition.sampleRateHz,
                    distScale = calibration.distScale,
                    inputLength = average.Dist.Length * 2,
                    outputLength = average.Dist.Length
                },
                arrays = new OfdrArraysInfo
                {
                    dist = new ArrayInfo { file = "dist.f64", dtype = "float64_le", count = average.Dist.Length, unit = "m" },
                    freq = new ArrayInfo { file = "freq.f64", dtype = "float64_le", count = average.Freq.Length, unit = "Hz" },
                    ps = new ArrayInfo { file = "ps.f64", dtype = "float64_le", count = average.Ps.Length, unit = "linear" }
                },
                quality = new OfdrQualityInfo
                {
                    distMonotonic = true,
                    distStepM = step,
                    freqLastHz = average.Freq.Length > 0 ? average.Freq[average.Freq.Length - 1] : 0,
                    psPositive = true
                }
            };
        }

        static string[] ToArray(List<string> values)
        {
            var array = new string[values.Count];
            for (int i = 0; i < values.Count; i++) array[i] = values[i];
            return array;
        }
    }
}
