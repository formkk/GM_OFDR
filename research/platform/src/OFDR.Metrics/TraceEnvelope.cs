namespace OFDR.Metrics;

/// <summary>
/// 曲线显示用的 min/max 包络降采样：每个像素桶保留区间极值，
/// 避免简单抽点把窄反射峰抹平。纯函数，供 UI 与验证共用。
/// </summary>
public static class TraceEnvelope
{
    /// <summary>
    /// 把 <paramref name="values"/> 降采样为 <paramref name="buckets"/> 个桶的最小/最大值序列。
    /// 桶数不超过点数；空输入或非法桶数返回空数组。
    /// </summary>
    public static void MinMax(double[] values, int buckets, out double[] min, out double[] max)
    {
        if (values.Length == 0 || buckets <= 0)
        {
            min = Array.Empty<double>();
            max = Array.Empty<double>();
            return;
        }

        int n = Math.Min(buckets, values.Length);
        min = new double[n];
        max = new double[n];

        for (int b = 0; b < n; b++)
        {
            int from = (int)((long)b * values.Length / n);
            int to = (int)((long)(b + 1) * values.Length / n) - 1;
            if (to < from) to = from;

            double lo = double.PositiveInfinity;
            double hi = double.NegativeInfinity;
            for (int i = from; i <= to; i++)
            {
                double v = values[i];
                if (!double.IsFinite(v)) continue;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }

            min[b] = double.IsFinite(lo) ? lo : double.NaN;
            max[b] = double.IsFinite(hi) ? hi : double.NaN;
        }
    }
}
