using System.Security.Cryptography;
using System.Text;

namespace OFDR.Contracts;

/// <summary>
/// 指标算法配置快照。所有字段在一次测试开始时固化，运行中不再读取外部文件。
/// 默认值为现状 demo 的代码默认值；其中阈值取 config.txt 的 -85 dBm 以收敛“三源冲突”。
/// </summary>
public sealed record MetricsConfig
{
    /// <summary>过阈峰值检测阈值（dBm）。</summary>
    public double PeakThresholdDbm { get; init; } = -85.0;

    /// <summary>相邻过阈段的未过阈行数 ≤ 此值则合并为一个峰。</summary>
    public int PeakMergeGap { get; init; } = 50;

    /// <summary>A 点行号（开始位置）。</summary>
    public double MarkARow { get; init; } = 2500;

    /// <summary>D 点行号（终止位置）。</summary>
    public double MarkDRow { get; init; } = 32500;

    /// <summary>局部平均半窗（行）：中心 ±N 行。</summary>
    public int AvgHalfWindow { get; init; } = 250;

    /// <summary>链路长度系数（米/行）。</summary>
    public double LinkLenPerRow { get; init; } = 0.0002;

    /// <summary>DUT 光程系数（米/行）。</summary>
    public double DutPathPerRow { get; init; } = 0.0002;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!double.IsFinite(PeakThresholdDbm)) errors.Add("PeakThresholdDbm 非法");
        if (PeakMergeGap < 0) errors.Add("PeakMergeGap 不能为负");
        if (!double.IsFinite(MarkARow) || MarkARow < 0) errors.Add("MarkARow 非法");
        if (!double.IsFinite(MarkDRow) || MarkDRow < 0) errors.Add("MarkDRow 非法");
        if (MarkARow > MarkDRow) errors.Add("MarkARow 大于 MarkDRow");
        if (AvgHalfWindow < 0) errors.Add("AvgHalfWindow 不能为负");
        if (LinkLenPerRow <= 0 || DutPathPerRow <= 0) errors.Add("长度/光程系数必须大于 0");
        return errors;
    }

    /// <summary>配置快照标识：字段全集的稳定哈希，结果落盘时携带。</summary>
    public string ComputeId()
    {
        var text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{PeakThresholdDbm:R}|{PeakMergeGap}|{MarkARow:R}|{MarkDRow:R}|{AvgHalfWindow}|{LinkLenPerRow:R}|{DutPathPerRow:R}");
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(text));
        return Convert.ToHexString(hash, 0, 6);
    }
}

/// <summary>
/// 通道基准修正值（SRI/SRO/SRB/SRC 各 8 路），下标 1~8 对应通道 1~8，下标 0 未使用。
/// </summary>
public sealed record ReferenceValues
{
    /// <summary>标量基准 SRO（config 键 "SRO"，不带通道后缀）。</summary>
    public double SroBase { get; init; }

    public double[] Sri { get; init; } = new double[9];
    public double[] Sro { get; init; } = new double[9];
    public double[] Srb { get; init; } = new double[9];
    public double[] Src { get; init; } = new double[9];

    public static ReferenceValues Zero => new();

    public static double At(double[] arr, int channel) =>
        channel is >= 1 and <= 8 && channel < arr.Length ? arr[channel] : 0.0;

    public double SriAt(int channel) => At(Sri, channel);
    public double SroAt(int channel) => At(Sro, channel);
    public double SrbAt(int channel) => At(Srb, channel);
    public double SrcAt(int channel) => At(Src, channel);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (name, arr) in new[] { ("Sri", Sri), ("Sro", Sro), ("Srb", Srb), ("Src", Src) })
        {
            if (arr.Length != 9) errors.Add($"{name} 长度应为 9（下标 1~8），实际 {arr.Length}");
            foreach (var v in arr)
                if (!double.IsFinite(v)) errors.Add($"{name} 含非有限值");
        }
        if (!double.IsFinite(SroBase)) errors.Add("SroBase 非法");
        return errors;
    }
}
