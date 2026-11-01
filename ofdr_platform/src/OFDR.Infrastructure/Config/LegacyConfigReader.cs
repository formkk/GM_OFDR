using System.Globalization;
using OFDR.Contracts;

namespace OFDR.Infrastructure.Config;

/// <summary>
/// 现状 config.txt 的迁移读取器：把 4 类参数分别映射到 MetricsConfig / ReferenceValues / 设备选项。
/// 仅读取平台实际使用的键；未知键忽略，解析失败保留默认值。
/// </summary>
public sealed record LegacySettings(
    MetricsConfig Metrics,
    ReferenceValues References,
    int ChannelTimeoutMs,
    int DelayMs,
    int StartSettleMs,
    int IntegralWindowCm,
    int WindowWidthCm,
    bool SaveRaw,
    bool SaveLines,
    bool SaveMetrics,
    bool SaveBadPeaks)
{
    public static LegacySettings Default { get; } = new(
        new MetricsConfig(), ReferenceValues.Zero,
        ChannelTimeoutMs: 10000, DelayMs: 100, StartSettleMs: 7000,
        IntegralWindowCm: 10, WindowWidthCm: 1,
        SaveRaw: true, SaveLines: true, SaveMetrics: true, SaveBadPeaks: true);
}

public static class LegacyConfigReader
{
    public static LegacySettings Load(string path)
    {
        var defaults = LegacySettings.Default;
        if (!File.Exists(path)) return defaults;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (key.Length > 0) values[key] = val;
        }

        double GetDouble(string key, double fallback) =>
            values.TryGetValue(key, out var s) &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : fallback;

        int GetInt(string key, int fallback) =>
            values.TryGetValue(key, out var s) &&
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n : fallback;

        bool GetBool(string key, bool fallback) =>
            values.TryGetValue(key, out var s) && bool.TryParse(s, out var b) ? b : fallback;

        double threshold = GetDouble("PeakThresholdDb", defaults.Metrics.PeakThresholdDbm);
        int mergeGap = GetInt("PeakMergeGap", defaults.Metrics.PeakMergeGap);
        int integralCm = GetInt("IntegralWindowCm", defaults.IntegralWindowCm);
        int windowCm = GetInt("WindowWidthCm", defaults.WindowWidthCm);
        int halfWindow = (int)Math.Round(integralCm / 0.04);   // 1 行 = 0.04 cm

        var metrics = new MetricsConfig
        {
            PeakThresholdDbm = threshold,
            PeakMergeGap = mergeGap,
            AvgHalfWindow = halfWindow,
            MarkARow = defaults.Metrics.MarkARow,
            MarkDRow = defaults.Metrics.MarkDRow,
            LinkLenPerRow = defaults.Metrics.LinkLenPerRow,
            DutPathPerRow = defaults.Metrics.DutPathPerRow,
        };

        var refs = new ReferenceValues
        {
            SroBase = GetDouble("SRO", 0),
            Sri = ReadChannelArray(values, "SRI"),
            Sro = ReadChannelArray(values, "SRO-"),
            Srb = ReadChannelArray(values, "SRB"),
            Src = ReadChannelArray(values, "SRC"),
        };

        return new LegacySettings(
            metrics, refs,
            GetInt("ChannelTimeoutMs", defaults.ChannelTimeoutMs),
            GetInt("DelayMs", defaults.DelayMs),
            GetInt("StartSettleMs", defaults.StartSettleMs),
            integralCm, windowCm,
            GetBool("SaveRaw", defaults.SaveRaw),
            GetBool("SaveLines", defaults.SaveLines),
            GetBool("SaveMetrics", defaults.SaveMetrics),
            GetBool("SaveBadPeaks", defaults.SaveBadPeaks));
    }

    private static double[] ReadChannelArray(Dictionary<string, string> values, string prefix)
    {
        var arr = new double[9];
        for (int ch = 1; ch <= 8; ch++)
        {
            var key = prefix.TrimEnd('-') + "-" + ch;
            if (values.TryGetValue(key, out var s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                arr[ch] = d;
        }
        return arr;
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOf('#');
        return hash >= 0 ? line[..hash] : line;
    }
}
