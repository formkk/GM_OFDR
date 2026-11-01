using OFDR.Metrics;

namespace OFDR.Replay;

/// <summary>合成谱中的一个峰（行号、峰值 dBm、高斯宽度 σ/行）。</summary>
public sealed record PeakSpec(int Row, double LevelDbm, double SigmaRows);

public sealed record SyntheticSpectrumOptions
{
    public int Points { get; init; } = 80_619;
    public double NoiseFloorDbm { get; init; } = -125.0;
    public double NoiseJitterDb { get; init; } = 10.0;
    public int Seed { get; init; } = 20260909;
    public IReadOnlyList<PeakSpec> Peaks { get; init; } = DefaultPeaks;

    /// <summary>与历史日志一致的单帧 8 峰（行号/幅值取自 ofdr_log_20260909_094043 的 F=1918）。</summary>
    public static IReadOnlyList<PeakSpec> DefaultPeaks { get; } = new[]
    {
        new PeakSpec(10311, -56.97, 3.0),
        new PeakSpec(16542, -72.25, 2.5),
        new PeakSpec(22392, -52.39, 2.0),
        new PeakSpec(32857, -54.39, 2.5),
        new PeakSpec(39245, -71.02, 2.5),
        new PeakSpec(48439, -66.55, 2.5),
        new PeakSpec(54868, -59.16, 2.5),
        new PeakSpec(65369, -55.68, 2.5),
    };
}

/// <summary>确定性合成谱：底噪（dBm）+ 若干高斯峰，在线性功率域叠加后转回 dBm。</summary>
public static class SyntheticSpectrum
{
    public static double[] Create(int frameIndex, SyntheticSpectrumOptions options)
    {
        if (options.Points <= 0) return Array.Empty<double>();

        var rng = new Random(unchecked(options.Seed * 1_000_003 + frameIndex * 7919));
        double[] mw = new double[options.Points];
        for (int i = 0; i < mw.Length; i++)
        {
            double noiseDbm = options.NoiseFloorDbm + (rng.NextDouble() * 2 - 1) * options.NoiseJitterDb;
            mw[i] = PowerUnits.DbmToMw(noiseDbm);
        }

        foreach (var peak in options.Peaks)
        {
            if (peak.SigmaRows <= 0) continue;
            double peakMw = PowerUnits.DbmToMw(peak.LevelDbm);
            int from = Math.Max(0, (int)Math.Floor(peak.Row - 6 * peak.SigmaRows));
            int to = Math.Min(mw.Length - 1, (int)Math.Ceiling(peak.Row + 6 * peak.SigmaRows));
            for (int i = from; i <= to; i++)
            {
                double d = (i - peak.Row) / peak.SigmaRows;
                mw[i] += peakMw * Math.Exp(-0.5 * d * d);
            }
        }

        double[] values = new double[mw.Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = PowerUnits.MwToDbm(mw[i]);
        return values;
    }
}
