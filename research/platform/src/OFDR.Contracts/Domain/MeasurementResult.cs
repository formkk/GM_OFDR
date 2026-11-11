namespace OFDR.Contracts;

public enum MeasurementStatus
{
    Ok,
    Invalid,
    Failed,
}

public enum MeasurementError
{
    None,
    EmptyScan,
    UnsupportedUnit,
    NotEightPeaks,
    ComputationFailed,
}

/// <summary>标记点（单位：行）。A/D 为配置位置，B/BB/CC/C/E 由 8 峰质心定位。</summary>
public sealed record MarkerPoints(double A, double B, double Bb, double Cc, double C, double D, double E);

/// <summary>
/// 单帧测量结果。无效时 Status/Error 明确，禁止用 0 冒充有效值。
/// 字段命名与现状 CSV 列一一对应，便于回归对照。
/// </summary>
public sealed record MeasurementResult(
    string ScanId,
    long Sequence,
    int ChannelIn,
    int ChannelOut,
    MeasurementStatus Status,
    MeasurementError Error,
    string Message,
    MarkerPoints? Markers,
    double IlIn,
    double IlDut,
    double IlOut,
    double RlIn,
    double RlDut,
    double RlOut,
    double DutPathM,
    double LinkLengthM,
    int PeakCount,
    double PeakMs,
    double MetricMs,
    string ConfigId,
    double SroBase,
    double SriIn,
    double SroOut,
    double SrbIn,
    double SrcOut)
{
    public bool IsValid => Status == MeasurementStatus.Ok;

    public static MeasurementResult Invalid(
        StandardScan scan,
        MeasurementError error,
        string message,
        int peakCount,
        string configId,
        double peakMs = 0,
        double metricMs = 0) =>
        new(
            scan.ScanId, scan.Sequence, scan.Metadata.ChannelIn, scan.Metadata.ChannelOut,
            MeasurementStatus.Invalid, error, message, null,
            double.NaN, double.NaN, double.NaN,
            double.NaN, double.NaN, double.NaN,
            double.NaN, double.NaN,
            peakCount, peakMs, metricMs, configId,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
}
