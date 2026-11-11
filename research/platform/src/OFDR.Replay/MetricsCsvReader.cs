using System.Globalization;

namespace OFDR.Replay;

/// <summary>历史 ofdr_metrics_*.csv 中的一行（回归基准数据，只读）。</summary>
public sealed record RecordedMeasurement(
    long SampleIndex,
    DateTime Timestamp,
    int ChannelIn,
    int ChannelOut,
    double IlIn,
    double IlDut,
    double IlOut,
    double RlIn,
    double RlDut,
    double RlOut,
    double DutPathM,
    double LinkLengthM,
    int PeakCount,
    double SroBase,
    double SriIn,
    double SroOut,
    double SrbIn,
    double SrcOut);

/// <summary>
/// 读取历史指标 CSV。列顺序与现状一致，允许有/无表头；无法解析的行跳过并计数。
/// 该文件不含 80,619 点全谱，故只作“结果级基准数据”，不能反算 IL/RL。
/// </summary>
public static class MetricsCsvReader
{
    public const int MinColumns = 18;

    public static IReadOnlyList<RecordedMeasurement> Read(string path, int maxRows = int.MaxValue)
    {
        var rows = new List<RecordedMeasurement>();
        foreach (var line in File.ReadLines(path))
        {
            if (rows.Count >= maxRows) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("采样序号", StringComparison.Ordinal)) continue;

            var parts = line.Split(',');
            if (parts.Length < MinColumns) continue;

            if (!long.TryParse(parts[0], out long sample)) continue;
            if (!DateTime.TryParse(parts[1], CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var ts)) continue;
            if (!int.TryParse(parts[2], out int inCh) || !int.TryParse(parts[3], out int outCh)) continue;

            double[] nums = new double[14];
            bool ok = true;
            for (int i = 0; i < nums.Length; i++)
            {
                if (!double.TryParse(parts[4 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out nums[i]))
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;

            int peakCount = (int)Math.Round(nums[8]);
            rows.Add(new RecordedMeasurement(
                sample, ts, inCh, outCh,
                nums[0], nums[1], nums[2],
                nums[3], nums[4], nums[5],
                nums[6], nums[7],
                peakCount,
                nums[9], nums[10], nums[11], nums[12], nums[13]));
        }
        return rows;
    }
}
