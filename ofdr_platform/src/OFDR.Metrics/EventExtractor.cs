using OFDR.Contracts;

namespace OFDR.Metrics;

/// <summary>
/// 距离域事件：峰位置 / 回损 / 宽度 / 段插损 / 累积插损。
/// 距离沿用扫描轴单位（一期固定为 m），宽度换算为 mm。
/// </summary>
public sealed record DistanceEvent(
    int Row,
    double DistanceM,
    double PeakDbm,
    double WidthMm,
    double SegmentLossDb,
    double CumulativeLossDb);

/// <summary>
/// 事件抽取（纯函数）：过阈峰值 → 事件列表。与 <see cref="MetricCalculator"/> 解耦，
/// 不要求恰好 8 峰，也不改动指标计算口径与配置快照。
/// </summary>
public static class EventExtractor
{
    /// <summary>段插损局部均值半窗上限（行）。独立于 AvgHalfWindow，避免窗口跨过相邻事件。</summary>
    public const int SegmentHalfWindowMax = 200;

    public static IReadOnlyList<DistanceEvent> Extract(StandardScan scan, MetricsConfig config)
    {
        if (scan.Count == 0 || !scan.Axis.IsValid) return Array.Empty<DistanceEvent>();
        if (!TryToDbm(scan, out var dbm)) return Array.Empty<DistanceEvent>();

        var peaks = PeakDetector.FindByThreshold(dbm, config.PeakThresholdDbm, config.PeakMergeGap).Peaks;
        if (peaks.Count == 0) return Array.Empty<DistanceEvent>();

        double[] mws = new double[dbm.Length];
        for (int i = 0; i < mws.Length; i++) mws[i] = PowerUnits.DbmToMw(dbm[i]);

        var events = new List<DistanceEvent>(peaks.Count);
        double cumulative = 0;

        for (int k = 0; k < peaks.Count; k++)
        {
            var peak = peaks[k];
            double segmentLoss = double.NaN;
            double cumulativeLoss = double.NaN;

            if (k > 0)
            {
                // 沿用上一个可测的累积值；本段不可测时保持该值
                cumulativeLoss = cumulative;

                var prev = peaks[k - 1];
                // 两侧留出 Width 行护栏，只取两峰之间的平直段
                int firstRow = prev.Index + Math.Max(1, prev.Width);
                int lastRow = peak.Index - Math.Max(1, peak.Width);
                int span = lastRow - firstRow + 1;

                if (span >= 2)
                {
                    int half = Math.Min(SegmentHalfWindowMax, Math.Max(1, (peak.Index - prev.Index) / 4));
                    int w = Math.Min(span / 2, half);
                    double afterPrev = MeanDbm(mws, firstRow, firstRow + w - 1);
                    double beforeCur = MeanDbm(mws, lastRow - w + 1, lastRow);
                    if (double.IsFinite(afterPrev) && double.IsFinite(beforeCur))
                    {
                        segmentLoss = afterPrev - beforeCur;
                        cumulative += segmentLoss;
                        cumulativeLoss = cumulative;
                    }
                }
            }

            events.Add(new DistanceEvent(
                Row: peak.Index,
                DistanceM: scan.Axis.At(peak.Index),
                PeakDbm: peak.Db,
                WidthMm: peak.Width * scan.Axis.Step * 1000.0,
                SegmentLossDb: segmentLoss,
                CumulativeLossDb: cumulativeLoss));
        }

        return events;
    }

    /// <summary>线性域平均后转 dBm；区间越界时钳制，无有效行返回 NaN。</summary>
    private static double MeanDbm(double[] mws, int fromRow, int toRow)
    {
        if (mws.Length == 0) return double.NaN;
        int i0 = Math.Max(0, fromRow);
        int i1 = Math.Min(mws.Length - 1, toRow);
        if (i1 < i0) return double.NaN;

        double sum = 0;
        for (int i = i0; i <= i1; i++) sum += mws[i];
        return PowerUnits.MwToDbm(sum / (i1 - i0 + 1));
    }

    /// <summary>把扫描数值统一转为 dBm（支持 dBm / mW）；其他单位返回 false（不静默按 dBm 解释）。</summary>
    public static bool TryToDbm(StandardScan scan, out double[] dbm)
    {
        switch (scan.ValueUnit.Trim().ToLowerInvariant())
        {
            case "dbm":
                dbm = scan.Values;
                return true;
            case "mw":
                dbm = new double[scan.Values.Length];
                for (int i = 0; i < dbm.Length; i++) dbm[i] = PowerUnits.MwToDbm(scan.Values[i]);
                return true;
            default:
                dbm = Array.Empty<double>();
                return false;
        }
    }
}
