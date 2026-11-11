namespace OFDR.Metrics;

/// <summary>检出的峰：Index=段内最大 dBm 行，Db=峰值，Width=段长度（行），Centroid=段内 mW 加权质心。</summary>
public sealed record Peak(int Index, double Db, int Width, double Centroid);

public sealed record PeakDetectionResult(IReadOnlyList<Peak> Peaks, int AboveThreshold);

/// <summary>
/// 简化峰值检测（与现状 demo 的 FindPeaksByThreshold 逐行对齐）：
/// 数值超过阈值的连续段视为一个峰；相邻段的未过阈行数 ≤ mergeGap 时合并。
/// </summary>
public static class PeakDetector
{
    public static PeakDetectionResult FindByThreshold(
        IReadOnlyList<double> data, double thresholdDb, int mergeGap)
    {
        var peaks = new List<Peak>();
        int aboveThreshold = 0;
        if (data.Count == 0) return new PeakDetectionResult(peaks, aboveThreshold);

        // 1) 收集所有连续过阈段，并记录段内最大值点
        var segments = new List<Segment>();
        int i = 0;
        while (i < data.Count)
        {
            if (data[i] <= thresholdDb) { i++; continue; }

            int segStart = i;
            double maxDb = data[i];
            int maxIdx = i;
            int segEnd = i;
            while (segEnd + 1 < data.Count && data[segEnd + 1] > thresholdDb)
            {
                segEnd++;
                if (data[segEnd] > maxDb)
                {
                    maxDb = data[segEnd];
                    maxIdx = segEnd;
                }
            }
            segments.Add(new Segment(segStart, segEnd, maxIdx, maxDb));
            i = segEnd + 1;
        }
        if (segments.Count == 0) return new PeakDetectionResult(peaks, aboveThreshold);

        // 2) 相邻段间隔 ≤ mergeGap 合并，否则切分为独立峰
        int curStart = segments[0].Start;
        int curEnd = segments[0].End;
        int curMaxIdx = segments[0].MaxIdx;
        double curMaxDb = segments[0].MaxDb;

        for (int s = 1; s < segments.Count; s++)
        {
            int gap = segments[s].Start - curEnd - 1;
            if (gap <= mergeGap)
            {
                curEnd = segments[s].End;
                if (segments[s].MaxDb > curMaxDb)
                {
                    curMaxDb = segments[s].MaxDb;
                    curMaxIdx = segments[s].MaxIdx;
                }
            }
            else
            {
                peaks.Add(MakePeak(data, curStart, curEnd, curMaxIdx, curMaxDb, ref aboveThreshold));
                curStart = segments[s].Start;
                curEnd = segments[s].End;
                curMaxIdx = segments[s].MaxIdx;
                curMaxDb = segments[s].MaxDb;
            }
        }
        peaks.Add(MakePeak(data, curStart, curEnd, curMaxIdx, curMaxDb, ref aboveThreshold));
        return new PeakDetectionResult(peaks, aboveThreshold);
    }

    private sealed record Segment(int Start, int End, int MaxIdx, double MaxDb);

    private static Peak MakePeak(
        IReadOnlyList<double> data, int segStart, int segEnd, int maxIdx, double maxDb,
        ref int aboveThreshold)
    {
        int width = segEnd - segStart + 1;
        aboveThreshold += width;

        double wsum = 0;
        double wpos = 0;
        for (int j = segStart; j <= segEnd; j++)
        {
            double mw = PowerUnits.DbmToMw(data[j]);
            wsum += mw;
            wpos += j * mw;
        }
        double centroid = wsum > 0 ? wpos / wsum : (segStart + segEnd) / 2.0;
        return new Peak(maxIdx, maxDb, width, centroid);
    }
}
