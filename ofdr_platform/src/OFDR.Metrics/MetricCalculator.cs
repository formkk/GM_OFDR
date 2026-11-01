using System.Diagnostics;
using OFDR.Contracts;

namespace OFDR.Metrics;

/// <summary>
/// 指标计算（纯函数）：输入 StandardScan + 配置快照 + 基准值，输出 MeasurementResult。
/// 公式、取整与默认值逐段对齐现状 DataReceiver.ParseLoop，保证回归可比。
/// </summary>
public static class MetricCalculator
{
    public static MeasurementResult Compute(
        StandardScan scan, MetricsConfig config, ReferenceValues references)
        => Compute(scan, config, references, config.ComputeId());

    public static MeasurementResult Compute(
        StandardScan scan, MetricsConfig config, ReferenceValues references, string configId)
    {
        if (scan.Count == 0)
            return MeasurementResult.Invalid(scan, MeasurementError.EmptyScan, "扫描数据为空", 0, configId);

        double[] dbm;
        switch (scan.ValueUnit.Trim().ToLowerInvariant())
        {
            case "dbm":
                dbm = scan.Values;
                break;
            case "mw":
                dbm = new double[scan.Values.Length];
                for (int i = 0; i < dbm.Length; i++)
                    dbm[i] = PowerUnits.MwToDbm(scan.Values[i]);
                break;
            default:
                return MeasurementResult.Invalid(
                    scan, MeasurementError.UnsupportedUnit,
                    $"不支持的数值单位：{scan.ValueUnit}", 0, configId);
        }

        var swPeak = Stopwatch.StartNew();
        var detection = PeakDetector.FindByThreshold(dbm, config.PeakThresholdDbm, config.PeakMergeGap);
        swPeak.Stop();
        double peakMs = swPeak.Elapsed.TotalMilliseconds;
        var peaks = detection.Peaks;

        if (peaks.Count != 8)
        {
            string msg = peaks.Count == 0
                ? $"未检出峰（阈值 {config.PeakThresholdDbm:F1} dBm）"
                : $"检出 {peaks.Count} 个峰（需恰好 8 个，阈值 {config.PeakThresholdDbm:F1} dBm）";
            return MeasurementResult.Invalid(
                scan, MeasurementError.NotEightPeaks, msg, peaks.Count, configId, peakMs);
        }

        // 质心 → 四舍五入取整（检测阶段用质心抗抖，输出与后续计算统一用整数行）
        double b = Math.Round(peaks[3].Centroid);                       // B  = 第 4 峰
        double bb = Math.Round((peaks[3].Centroid + peaks[4].Centroid) / 2); // BB = 第 4、5 峰中点
        double cc = Math.Round((peaks[5].Centroid + peaks[6].Centroid) / 2); // CC = 第 6、7 峰中点
        double c = Math.Round(peaks[6].Centroid);                       // C  = 第 7 峰
        double e = Math.Round(peaks[7].Centroid);                       // E  = 第 8 峰

        // 工程换算：链路长度 = E 行 × 系数；DUT 光程 = (C - B) 行 × 系数
        double linkLen = e * config.LinkLenPerRow;
        double dutPath = (c - b) * config.DutPathPerRow;

        var swMetric = Stopwatch.StartNew();

        // mW 缓存：RL 区间求和与局部平均复用，避免重复 Math.Pow
        double[] mws = new double[dbm.Length];
        for (int i = 0; i < dbm.Length; i++)
            mws[i] = PowerUnits.DbmToMw(dbm[i]);

        // RL：区间内 mW 求和 → dBm（闭区间，端点行四舍五入）
        double rlDut = PowerUnits.MwToDbm(SumMwRange(mws, config.MarkARow, config.MarkDRow));
        double rlIn = PowerUnits.MwToDbm(SumMwRange(mws, config.MarkARow, bb));
        double rlOut = PowerUnits.MwToDbm(SumMwRange(mws, cc, config.MarkDRow));

        // 局部功率：各点 ±AvgHalfWindow 行，mW 求平均后再转 dBm（线性域平均）
        double aDbm = AvgDbmAround(mws, config.MarkARow, config.AvgHalfWindow);
        double dDbm = AvgDbmAround(mws, config.MarkDRow, config.AvgHalfWindow);
        double bbDbm = AvgDbmAround(mws, bb, config.AvgHalfWindow);
        double ccDbm = AvgDbmAround(mws, cc, config.AvgHalfWindow);

        // IL：功率差的一半（往返光程），再叠加通道基准修正
        int refIn = scan.Metadata.ChannelIn;
        int refOut = scan.Metadata.ChannelOut;
        double ilDutRaw = (aDbm - dDbm) / 2;
        double sriIn = references.SriAt(refIn);
        double sroOut = references.SroAt(refOut);
        double ilDut = ilDutRaw + references.SroBase + sriIn + sroOut;

        double ilInRaw = (aDbm - bbDbm) / 2;
        double srbIn = references.SrbAt(refIn);
        double ilIn = ilInRaw + srbIn;

        double ilOutRaw = (ccDbm - dDbm) / 2;
        double srcOut = references.SrcAt(refOut);
        double ilOut = ilOutRaw + srcOut;

        swMetric.Stop();
        double metricMs = swMetric.Elapsed.TotalMilliseconds;

        return new MeasurementResult(
            scan.ScanId, scan.Sequence, refIn, refOut,
            MeasurementStatus.Ok, MeasurementError.None, "",
            new MarkerPoints(config.MarkARow, b, bb, cc, c, config.MarkDRow, e),
            ilIn, ilDut, ilOut,
            rlIn, rlDut, rlOut,
            dutPath, linkLen,
            peaks.Count, peakMs, metricMs, configId,
            references.SroBase, sriIn, sroOut, srbIn, srcOut);
    }

    /// <summary>闭区间 [fromRow, toRow] 内的 mW 求和（行号四舍五入后钳制到有效范围）。</summary>
    private static double SumMwRange(double[] mws, double fromRow, double toRow)
    {
        if (mws.Length == 0) return 0;
        int i0 = Math.Clamp((int)Math.Round(fromRow), 0, mws.Length - 1);
        int i1 = Math.Clamp((int)Math.Round(toRow), 0, mws.Length - 1);
        if (i1 < i0) (i0, i1) = (i1, i0);
        double sum = 0;
        for (int i = i0; i <= i1; i++) sum += mws[i];
        return sum;
    }

    /// <summary>以 centerRow 为中心、左右各 halfWindow 行（共 2*halfWindow+1 点）的 mW 平均，再转 dBm。</summary>
    private static double AvgDbmAround(double[] mws, double centerRow, int halfWindow)
    {
        if (mws.Length == 0) return PowerUnits.InvalidDbm;
        int c = Math.Clamp((int)Math.Round(centerRow), 0, mws.Length - 1);
        int i0 = Math.Max(0, c - halfWindow);
        int i1 = Math.Min(mws.Length - 1, c + halfWindow);
        double sum = 0;
        for (int i = i0; i <= i1; i++) sum += mws[i];
        return PowerUnits.MwToDbm(sum / (i1 - i0 + 1));
    }
}
