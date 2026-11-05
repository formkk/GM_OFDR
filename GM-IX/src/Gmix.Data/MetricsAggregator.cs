using System;
using System.Collections.Generic;

namespace Gmix.Data
{
    /// <summary>
    /// 自有口径的汇总计算：总回损（仅峰值/基线+峰值）、区间插损、精度。
    /// 电平 L 为 20log10(ps)；功率 P = 10^(L/10)。
    /// </summary>
    public static class MetricsAggregator
    {
        public static SummaryDocument Compute(
            string scanId,
            EventsDocument events,
            double[] dist,
            double[] baselineDb,
            AnalysisRangeInfo range,
            string rlMode,
            int averageCount,
            CalibrationModel calibration,
            IList<double> repeatRlDb,
            IList<double> repeatIlDb)
        {
            return Compute(scanId, events, dist, baselineDb, range, rlMode, averageCount,
                calibration, repeatRlDb, repeatIlDb, null);
        }

        public static SummaryDocument Compute(
            string scanId,
            EventsDocument events,
            double[] dist,
            double[] baselineDb,
            AnalysisRangeInfo range,
            string rlMode,
            int averageCount,
            CalibrationModel calibration,
            IList<double> repeatRlDb,
            IList<double> repeatIlDb,
            string currentConfigHash)
        {
            if (events == null) throw new ArgumentNullException("events");
            if (dist == null) throw new ArgumentNullException("dist");

            var warnings = new List<string>();
            if (range == null)
            {
                range = new AnalysisRangeInfo();
                range.startM = dist[0];
                range.endM = dist[dist.Length - 1];
                range.guardM = 0.0;
                warnings.Add("未提供分析区间，使用全量程");
            }
            if (range.endM <= range.startM)
                throw new ArgumentException("分析区间 endM 必须大于 startM");

            double guard = range.guardM > 0 ? range.guardM : 0.1;

            // 峰过滤：零距峰按 display 开关处理
            bool excludeZero = events.display != null && events.display.zeroPeakExcluded;
            var peaks = new List<PeakInfo>();
            if (events.peaks != null)
            {
                foreach (var p in events.peaks)
                {
                    if (excludeZero && p.zeroPeak.HasValue && p.zeroPeak.Value) continue;
                    if (p.locM < range.startM || p.locM > range.endM) continue;
                    peaks.Add(p);
                }
            }
            if (peaks.Count == 0) warnings.Add("分析区间内没有峰");

            // 仅峰值总回损：全部已检出峰参与功率求和（heightDbRel 为 dB 相对电平，可为负）
            double peakPower = 0.0;
            foreach (var p in peaks)
            {
                peakPower += Math.Pow(10.0, p.heightDbRel / 10.0);
            }

            // 基线功率（可选）
            double basePower = 0.0;
            if (baselineDb != null && baselineDb.Length == dist.Length)
            {
                for (int i = 0; i < baselineDb.Length; i++)
                {
                    double d = dist[i];
                    if (d < range.startM || d > range.endM) continue;
                    if (baselineDb[i] >= 0) continue; // dB 基线典型为负；>= 0 视为无效/哨兵值
                    basePower += Math.Pow(10.0, baselineDb[i] / 10.0);
                }
            }
            else
            {
                warnings.Add("缺少 baseline.f64，基线+峰值模式退化为仅峰值");
            }

            double totalPower = peakPower;
            if (string.Equals(rlMode, "baseline_plus_peak", StringComparison.OrdinalIgnoreCase))
                totalPower = peakPower + basePower;
            else if (!string.Equals(rlMode, "peak_only", StringComparison.OrdinalIgnoreCase))
                warnings.Add("未知 rlMode，按 peak_only 处理");

            double? totalRlRaw = totalPower > 0 ? 10.0 * Math.Log10(totalPower) : (double?)null;
            double? totalRl = totalRlRaw.HasValue && calibration != null
                ? calibration.ApplyRl(totalRlRaw.Value) : null;

            // 区间总插损：起止保护带基线中值之差
            double? startLevel = MedianInWindow(baselineDb, dist, range.startM, range.startM + guard);
            double? endLevel = MedianInWindow(baselineDb, dist, range.endM - guard, range.endM);
            if ((!startLevel.HasValue || !endLevel.HasValue) && events.segments != null && events.segments.Length > 0)
            {
                warnings.Add("baseline.f64 缺失或不足，插损改用段噪声顶");
                SegmentInfo first = null, last = null;
                foreach (var s in events.segments)
                {
                    if (s.endIndex >= 0 && s.startIndex < dist.Length && dist[(int)s.startIndex] >= range.startM && first == null) first = s;
                    if (s.startIndex < dist.Length && dist[(int)s.startIndex] <= range.endM) last = s;
                }
                if (first != null) startLevel = first.noiseFloorDbRel;
                if (last != null) endLevel = last.noiseFloorDbRel;
            }

            double? totalIlRaw = null;
            if (startLevel.HasValue && endLevel.HasValue)
                totalIlRaw = startLevel.Value - endLevel.Value;
            else
                warnings.Add("无法计算区间插损");

            double? totalIl = totalIlRaw.HasValue && calibration != null
                ? calibration.ApplyIlTotal(totalIlRaw.Value) : null;

            if (calibration != null && !string.IsNullOrEmpty(currentConfigHash)
                && !calibration.MatchesConfig(currentConfigHash))
            {
                warnings.Add("校准 configHash 与当前配置不匹配，绝对标定不应用");
                totalRl = null;
                totalIl = null;
            }

            int eventCount = 0;
            if (events.events != null)
            {
                foreach (var e in events.events)
                {
                    if (e.locM >= range.startM && e.locM <= range.endM) eventCount++;
                }
            }

            var summary = new SummaryDocument();
            summary.schemaVersion = "0.2";
            summary.scanId = scanId;
            summary.analysisRange = range;
            summary.rlMode = rlMode;
            summary.averageCount = averageCount;
            summary.totalIlRawDb = totalIlRaw;
            summary.totalIlDb = totalIl;
            summary.totalRlRawDb = totalRlRaw;
            summary.totalRlDb = totalRl;
            summary.ilPrecisionDb = SampleStd(repeatIlDb);
            summary.rlPrecisionDb = SampleStd(repeatRlDb);
            summary.peakCount = peaks.Count;
            summary.eventCount = eventCount;
            summary.warnings = warnings.ToArray();
            return summary;
        }

        static double? MedianInWindow(double[] baselineDb, double[] dist, double fromM, double toM)
        {
            if (baselineDb == null || baselineDb.Length != dist.Length) return null;
            var vals = new List<double>();
            for (int i = 0; i < dist.Length; i++)
            {
                if (dist[i] >= fromM && dist[i] <= toM && baselineDb[i] < 0)
                    vals.Add(baselineDb[i]);
            }
            if (vals.Count == 0) return null;
            vals.Sort();
            int mid = vals.Count / 2;
            if (vals.Count % 2 == 0) return (vals[mid - 1] + vals[mid]) / 2.0;
            return vals[mid];
        }

        static double? SampleStd(IList<double> values)
        {
            if (values == null || values.Count < 2) return null;
            double sum = 0, sumSq = 0;
            foreach (var v in values) { sum += v; sumSq += v * v; }
            double mean = sum / values.Count;
            double var = (sumSq - values.Count * mean * mean) / (values.Count - 1);
            if (var < 0) var = 0;
            return Math.Sqrt(var);
        }
    }
}
