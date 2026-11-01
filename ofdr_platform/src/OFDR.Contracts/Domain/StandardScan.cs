namespace OFDR.Contracts;

public enum AxisKind
{
    Distance,
    Frequency,
}

/// <summary>标准扫描轴：类型 + 起点 + 步距 + 单位 + 点数。平台只认这一种轴描述。</summary>
public sealed record ScanAxis(AxisKind Kind, double Start, double Step, string Unit, int Count)
{
    public double End => Start + Step * Math.Max(0, Count - 1);

    public bool IsValid =>
        Count > 0 &&
        double.IsFinite(Start) &&
        double.IsFinite(Step) &&
        Step > 0 &&
        !string.IsNullOrWhiteSpace(Unit);

    /// <summary>单调轴在第 i 点的坐标。</summary>
    public double At(int index) => Start + Step * index;
}

public enum ScanQuality
{
    Ok,
    Empty,
    AxisInvalid,
    CountMismatch,
    ContainsNonFinite,
}

/// <summary>标准扫描元数据。通道/代次在帧到达时刻即固化，避免解析滞后导致通道错位。</summary>
public sealed record ScanMetadata(
    string DeviceId,
    string SdkVersion,
    int ChannelIn,
    int ChannelOut,
    long Epoch,
    DateTimeOffset Timestamp,
    string CalibrationId = "",
    string? RequestId = null,
    int SegmentIndex = 0,
    double? TriggerDelayMs = null);

/// <summary>
/// 平台唯一跨层数据契约：轴 + 数值 + 单位 + 元数据 + 质量标记 + schema 版本。
/// 数值语义由 ValueUnit 指明；整机 TCP 路径当前为逐点 dBm。
/// </summary>
public sealed record StandardScan(
    string ScanId,
    long Sequence,
    ScanAxis Axis,
    double[] Values,
    string ValueUnit,
    ScanMetadata Metadata,
    ScanQuality Quality = ScanQuality.Ok,
    int SchemaVersion = 1)
{
    public int Count => Values.Length;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ScanId)) errors.Add("ScanId 不能为空");
        if (Values.Length == 0) errors.Add("Values 为空");
        if (Axis.Count != Values.Length) errors.Add($"轴点数 {Axis.Count} 与数值长度 {Values.Length} 不一致");
        if (!Axis.IsValid) errors.Add("轴非法（点数/起点/步距/单位）");
        if (string.IsNullOrWhiteSpace(ValueUnit)) errors.Add("ValueUnit 不能为空");
        if (Metadata.ChannelIn is < 1 or > 8) errors.Add($"ChannelIn={Metadata.ChannelIn} 超出 1~8");
        if (Metadata.ChannelOut is < 1 or > 8) errors.Add($"ChannelOut={Metadata.ChannelOut} 超出 1~8");
        int nonFinite = 0;
        foreach (var v in Values)
            if (!double.IsFinite(v)) nonFinite++;
        if (nonFinite > 0) errors.Add($"包含 {nonFinite} 个非有限数值");
        return errors;
    }
}
