namespace OFDR.Contracts;

/// <summary>单个指标的帧级统计（Cur/Max/Min/Avg + 有效帧数）。</summary>
public sealed record MetricStats(int Count, double Sum, double Min, double Max, double Last)
{
    public static readonly MetricStats Empty = new(0, 0, double.NaN, double.NaN, double.NaN);

    public double Avg => Count > 0 ? Sum / Count : double.NaN;

    public MetricStats Add(double value)
    {
        if (double.IsNaN(value)) return this;
        if (Count == 0) return new MetricStats(1, value, value, value, value);
        return new MetricStats(Count + 1, Sum + value, Math.Min(Min, value), Math.Max(Max, value), value);
    }
}

public enum ChannelRunStatus
{
    Completed,
    TimedOut,
    NoData,
}

/// <summary>单通道（IN×OUT）聚合结果。</summary>
public sealed record ChannelAggregate(
    int ChannelIn,
    int ChannelOut,
    int Frames,
    int ValidFrames,
    int ExpectedFrames,
    ChannelRunStatus Status,
    MetricStats IlIn,
    MetricStats IlDut,
    MetricStats IlOut,
    MetricStats RlIn,
    MetricStats RlDut,
    MetricStats RlOut,
    MetricStats DutPath,
    MetricStats LinkLength,
    int LastPeakCount,
    string Message);
