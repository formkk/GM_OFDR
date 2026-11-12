using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Detection.Core;
using Detection.Orchestration;
using Gmix.Data;

namespace Detection.Tests
{
    /// <summary>
    /// 测试替身：按 L2 布局写出一次路径的扫描目录。真实实现要接 LGA50 采集与峰值检测
    /// （TBD-D10），本类只负责让编排层的校验、提交与指标读取可被验证。
    /// </summary>
    internal sealed class FakeGroupProducer : IGroupProducer
    {
        /// <summary>这些路径的产出直接失败（模拟扫描失败）。</summary>
        public readonly HashSet<string> FailPaths = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>是否为 N&gt;1 生成 `AVG/`（默认生成）。</summary>
        public bool ProduceAverage = true;

        public int ProduceCalls;

        public GroupProduction Produce(string stagingGroupDir, PathPlanEntry entry, int plannedCount)
        {
            ProduceCalls++;
            if (FailPaths.Contains(entry.pathId))
                return GroupProduction.Failure(ErrorClass.Measurement, "注入的扫描失败");

            Directory.CreateDirectory(stagingGroupDir);

            var scanIds = new string[plannedCount];
            for (int k = 1; k <= plannedCount; k++)
            {
                string scanId = entry.pathId + "-S" + k.ToString("00", CultureInfo.InvariantCulture);
                scanIds[k - 1] = scanId;
                WriteScan(Path.Combine(stagingGroupDir, DetectionNaming.ScanFolder(k)), scanId, k);
            }

            string avgScanId = null;
            string alignment = null;
            if (plannedCount > 1 && ProduceAverage)
            {
                avgScanId = "AV-20261111-153000-001";
                alignment = GroupAssembler.AlignmentSameDist;
                WriteAverage(Path.Combine(stagingGroupDir, "AVG"), avgScanId);
            }

            return new GroupProduction
            {
                ValidCount = plannedCount,
                ScanIds = scanIds,
                AvgScanId = avgScanId,
                Alignment = alignment,
                Rejected = new string[0],
                Acquisition = new AcquisitionInfo { sampleRateHz = 1000, expectedFrames = 1, bytesPerFrame = 8 }
            };
        }

        static void WriteScan(string dir, string scanId, int repeatIndex)
        {
            Directory.CreateDirectory(dir);

            JsonStore.Write(Path.Combine(dir, "scan.json"), new ScanDocument
            {
                schemaVersion = "1.0",
                scanId = scanId,
                session = new SessionInfo()
            });

            var dist = new double[] { 0.0, 0.001, 0.002 };
            var ps = new double[] { 1.0, 1.0, 1.0 };
            ArrayStore.WriteDoubles(Path.Combine(dir, "dist.f64"), dist);
            ArrayStore.WriteDoubles(Path.Combine(dir, "ps.f64"), ps);
            ArrayStore.WriteDoubles(Path.Combine(dir, "ps_db.f64"), new double[] { 0.0, 0.0, 0.0 });
            File.WriteAllText(Path.Combine(dir, "calibration.json"), "{}");

            JsonStore.Write(Path.Combine(dir, "events.json"), new EventsDocument
            {
                schemaVersion = "1.0",
                scanId = scanId,
                peaks = new[]
                {
                    new PeakInfo { locM = 6.850, peakNo = 1, zeroPeak = false }
                },
                events = new EventInfo[0],
                segments = new SegmentInfo[0]
            });

            JsonStore.Write(Path.Combine(dir, "summary.json"), new SummaryDocument
            {
                schemaVersion = "1.0",
                scanId = scanId,
                totalIlDb = -1.25,
                totalRlDb = -61.5,
                averageCount = 1
            });

            string[] names = { "scan.json", "dist.f64", "ps.f64", "ps_db.f64", "calibration.json", "events.json", "summary.json" };
            var files = new ReadyFileInfo[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                string path = Path.Combine(dir, names[i]);
                files[i] = new ReadyFileInfo
                {
                    path = names[i],
                    bytes = new FileInfo(path).Length,
                    sha256 = HashUtil.Sha256File(path)
                };
            }

            JsonStore.Write(Path.Combine(dir, GroupStore.ReadyFileName), new ReadyDocument
            {
                schemaVersion = "1.0",
                scanId = scanId,
                repeatIndex = repeatIndex,
                folder = Path.GetFileName(dir),
                files = files,
                acquirerVersion = "tests",
                finishedAtUtc = "2026-11-11T15:30:00Z"
            });
        }

        static void WriteAverage(string dir, string avgScanId)
        {
            Directory.CreateDirectory(dir);
            JsonStore.Write(Path.Combine(dir, "summary.json"), new SummaryDocument
            {
                schemaVersion = "1.0",
                scanId = avgScanId,
                totalIlDb = -1.20,
                totalRlDb = -61.0,
                averageCount = 3
            });
            JsonStore.Write(Path.Combine(dir, "events.json"), new EventsDocument
            {
                schemaVersion = "1.0",
                scanId = avgScanId,
                peaks = new[] { new PeakInfo { locM = 6.850, peakNo = 1, zeroPeak = false } },
                events = new EventInfo[0],
                segments = new SegmentInfo[0]
            });
        }
    }
}
