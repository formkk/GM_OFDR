namespace OFDR.Metrics;

/// <summary>
/// dBm 与 mW 的唯一换算点（SRS FR-PROC-04：dB 转换在平台内只允许一个实现）。
/// 无效值沿用现状 demo 的 -999 哨兵，便于与历史结果对照。
/// </summary>
public static class PowerUnits
{
    public const double InvalidDbm = -999.0;

    public static double DbmToMw(double dbm) => Math.Pow(10, dbm / 10.0);

    public static double MwToDbm(double mw) => mw > 0 ? 10 * Math.Log10(mw) : InvalidDbm;
}
