using System;
using System.IO;
using Detection.Core;
using Gmix.Data;

namespace Detection.Orchestration
{
    /// <summary>组内指标与峰证据的读取结果。</summary>
    public sealed class GroupMetrics
    {
        /// <summary>`summary.json` 是否存在且可解析。</summary>
        public bool FrameValid { get; set; }

        public double? IlDb { get; set; }

        public double? RlDb { get; set; }

        /// <summary>组内相对路径，例如 `AVG/summary.json`。</summary>
        public string MetricSource { get; set; }

        public PathEvidence Evidence { get; set; }
    }

    /// <summary>
    /// 从 L2 组读取指标与峰证据（SRS §4.5.2：权威来源 `summary.json`，检测层不重算）。
    /// N&gt;1 取 `AVG/`，N=1 取 `R001/`。
    /// </summary>
    public static class MetricsReader
    {
        public static GroupMetrics ReadGroup(string groupDir, int averageCount)
        {
            var metrics = new GroupMetrics
            {
                Evidence = new PathEvidence()
            };

            if (string.IsNullOrEmpty(groupDir) || !Directory.Exists(groupDir)) return metrics;

            string scanFolder = averageCount > 1 ? "AVG" : DetectionNaming.ScanFolder(1);
            string scanDir = Path.Combine(groupDir, scanFolder);
            string summaryPath = Path.Combine(scanDir, "summary.json");
            if (!File.Exists(summaryPath)) return metrics;

            SummaryDocument summary;
            try
            {
                summary = JsonStore.Read<SummaryDocument>(summaryPath);
            }
            catch (Exception)
            {
                // 解析失败按"帧无效"上报，由调用方置 invalidFrame，不静默兜底
                return metrics;
            }

            if (summary == null) return metrics;

            metrics.FrameValid = true;
            metrics.IlDb = summary.totalIlDb;
            metrics.RlDb = summary.totalRlDb;
            metrics.MetricSource = scanFolder + "/summary.json";

            string eventsPath = Path.Combine(scanDir, "events.json");
            if (File.Exists(eventsPath))
            {
                try
                {
                    EventsDocument events = JsonStore.Read<EventsDocument>(eventsPath);
                    metrics.Evidence = ReadEvidence(events);
                }
                catch (Exception)
                {
                    // 峰证据缺失不影响指标；保持 null，由 UI 呈现"无证据"
                }
            }

            return metrics;
        }

        static PathEvidence ReadEvidence(EventsDocument events)
        {
            var evidence = new PathEvidence();
            if (events == null || events.peaks == null || events.peaks.Length == 0)
            {
                evidence.peakPresent = false;
                return evidence;
            }

            bool excludedZero = false;
            for (int i = 0; i < events.peaks.Length; i++)
            {
                PeakInfo peak = events.peaks[i];
                if (peak == null) continue;

                bool isZero = peak.zeroPeak.HasValue && peak.zeroPeak.Value;
                if (isZero)
                {
                    excludedZero = true;
                    continue;
                }

                evidence.peakPresent = true;
                evidence.peakPositionM = peak.locM;
                evidence.peakNo = peak.peakNo;
                evidence.zeroPeakExcluded = excludedZero;
                return evidence;
            }

            evidence.peakPresent = false;
            evidence.zeroPeakExcluded = excludedZero;
            return evidence;
        }
    }
}
