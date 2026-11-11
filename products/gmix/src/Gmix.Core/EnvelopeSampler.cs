using System;

namespace Gmix.Data
{
    /// <summary>
    /// 按屏幕像素列做 min/max 包络抽稀（SRS FR-CHT-07 / NFR-PERF-02）。
    /// 抽稀保留每列极值索引，单点窄峰不会因降采样被丢弃。
    /// </summary>
    public static class EnvelopeSampler
    {
        public struct Column
        {
            /// <summary>该列最小值所在索引；-1 表示该列无数据。</summary>
            public int MinIndex;
            /// <summary>该列最大值所在索引；-1 表示该列无数据。</summary>
            public int MaxIndex;
        }

        public static Column[] Reduce(double[] dist, double[] values, double xMin, double xMax, int columns)
        {
            if (dist == null || values == null)
                throw new ArgumentNullException(nameof(dist), "dist/values 不可为空");
            if (dist.Length != values.Length)
                throw new ArgumentException("dist/values 长度不一致", nameof(values));
            if (columns <= 0)
                throw new ArgumentOutOfRangeException(nameof(columns), "columns 必须大于 0");
            if (!(xMax > xMin))
                throw new ArgumentException("xMax 必须大于 xMin");

            var result = new Column[columns];
            for (int i = 0; i < columns; i++)
            {
                result[i].MinIndex = -1;
                result[i].MaxIndex = -1;
            }

            double span = xMax - xMin;
            for (int i = 0; i < dist.Length; i++)
            {
                double x = dist[i];
                if (x < xMin || x > xMax) continue;

                int col = (int)((x - xMin) / span * columns);
                if (col < 0) col = 0;
                if (col >= columns) col = columns - 1;

                double v = values[i];
                if (double.IsNaN(v)) continue;

                Column c = result[col];
                if (c.MinIndex < 0)
                {
                    c.MinIndex = i;
                    c.MaxIndex = i;
                }
                else
                {
                    if (v < values[c.MinIndex]) c.MinIndex = i;
                    if (v > values[c.MaxIndex]) c.MaxIndex = i;
                }
                result[col] = c;
            }

            return result;
        }

        /// <summary>抽稀等价性校验用：全量极值必须与逐列极值合并后一致。</summary>
        public static void GlobalExtremes(double[] values, out double min, out double max)
        {
            min = double.PositiveInfinity;
            max = double.NegativeInfinity;
            if (values == null || values.Length == 0) return;
            foreach (double v in values)
            {
                if (double.IsNaN(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }
    }
}
