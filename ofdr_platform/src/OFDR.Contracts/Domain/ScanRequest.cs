namespace OFDR.Contracts;

/// <summary>触发方式（对应 SDD 4.1 的 TriggerMode）。</summary>
public enum TriggerMode
{
    Software,
    ExtRising,
    ExtFalling,
}

/// <summary>
/// 一次扫描请求。平台对采集侧的唯一“下单”描述，不含任何厂商句柄或缓冲区。
/// </summary>
public sealed record ScanRequest(
    string RequestId,
    int ChannelIn,
    int ChannelOut,
    int SampleRateMHz,
    int Points,
    double ScanLengthM,
    double ResolutionM,
    TriggerMode TriggerMode = TriggerMode.Software,
    double TriggerDelayMs = 0,
    int SegmentCount = 1,
    int UploadFrom = 0,
    int UploadTo = -1,
    string CalibrationId = "")
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(RequestId)) errors.Add("RequestId 不能为空");
        if (ChannelIn is < 1 or > 8) errors.Add($"ChannelIn={ChannelIn} 超出 1~8");
        if (ChannelOut is < 1 or > 8) errors.Add($"ChannelOut={ChannelOut} 超出 1~8");
        if (Points <= 0) errors.Add($"Points={Points} 必须大于 0");
        if (!double.IsFinite(ScanLengthM) || ScanLengthM <= 0) errors.Add($"ScanLengthM={ScanLengthM} 非法");
        if (!double.IsFinite(ResolutionM) || ResolutionM <= 0) errors.Add($"ResolutionM={ResolutionM} 非法");
        if (!double.IsFinite(TriggerDelayMs) || TriggerDelayMs < 0) errors.Add($"TriggerDelayMs={TriggerDelayMs} 非法");
        if (SegmentCount <= 0) errors.Add($"SegmentCount={SegmentCount} 必须大于 0");
        return errors;
    }
}
