using System.Text.Json.Serialization;

namespace OFDR.Contracts;

/// <summary>数据来源：决定采集参数如何解释（见统一数据契约 §4.4）。必填，缺失即视为契约违约。</summary>
public enum SourceType
{
    Lianxun,
    HengLga50,
    Replay,
}

/// <summary>标准扫描轴：起点 + 步距 + 点数。横轴恒为距离，单位恒为米，不设类型与单位字段。</summary>
public sealed record ScanAxis(double Start, double Step, int Count)
{
    /// <summary>派生值，不落盘、不参与比较、不进入构造参数。</summary>
    [JsonIgnore]
    public double End => Start + Step * Math.Max(0, Count - 1);

    [JsonIgnore]
    public bool IsValid =>
        Count > 0 &&
        double.IsFinite(Start) &&
        double.IsFinite(Step) &&
        Step > 0;

    /// <summary>单调轴在第 i 点的坐标。</summary>
    public double At(int index) => Start + Step * index;
}

/// <summary>标准扫描元数据。通道/代次在帧到达时刻即固化，避免解析滞后导致通道错位。</summary>
public sealed record ScanMetadata(
    SourceType SourceType,
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
/// 平台唯一跨层数据契约：轴 + 数值 + 元数据 + schema 版本。
/// 数值恒为 dB 相对功率电平（10·log10 功率域），绝对性由标定状态决定，不由数值换算体现。
/// 只表示有效帧：完整性判定由适配器在构造前完成，不携带状态字段。
/// </summary>
public sealed record StandardScan(
    string ScanId,
    long Sequence,
    ScanAxis Axis,
    double[] Values,
    ScanMetadata Metadata,
    int SchemaVersion = 1)
{
    public int Count => Values.Length;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ScanId)) errors.Add("ScanId 不能为空");
        if (Values.Length == 0) errors.Add("Values 为空");
        if (Axis.Count != Values.Length) errors.Add($"轴点数 {Axis.Count} 与数值长度 {Values.Length} 不一致");
        if (!Axis.IsValid) errors.Add("轴非法（点数/起点/步距）");
        if (!Enum.IsDefined(Metadata.SourceType)) errors.Add($"SourceType={Metadata.SourceType} 非法");
        if (Metadata.ChannelIn is < 1 or > 8) errors.Add($"ChannelIn={Metadata.ChannelIn} 超出 1~8");
        if (Metadata.ChannelOut is < 1 or > 8) errors.Add($"ChannelOut={Metadata.ChannelOut} 超出 1~8");
        int nonFinite = 0;
        foreach (var v in Values)
            if (!double.IsFinite(v)) nonFinite++;
        if (nonFinite > 0) errors.Add($"包含 {nonFinite} 个非有限数值");
        return errors;
    }
}
