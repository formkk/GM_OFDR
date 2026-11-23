using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 指标表落盘（MVP 唯一产物）：一行一路径，列序与 demo 指标表一致。
    /// RL 为 dB_rel（LGA50 曲线是 dB 相对值，不是 dBm）。
    /// </summary>
    public static class MetricTableCsv
    {
        public const string Header =
            "组别,IN端口,OUT端口,IL-IN(dB),IL-DUT(dB),IL-OUT(dB),RL-IN(dB_rel),RL-DUT(dB_rel),RL-OUT(dB_rel),DUT光程(m),链路长度(m),有效采样,备注";

        public static void Write(string path, IEnumerable<MetricTableRow> rows)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("输出路径为空", "path");
            if (rows == null) throw new ArgumentNullException("rows");

            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine(Header);
                foreach (MetricTableRow row in rows)
                {
                    writer.WriteLine(string.Join(",", new string[]
                    {
                        row.InChannel.ToString(CultureInfo.InvariantCulture),
                        row.InChannel.ToString(CultureInfo.InvariantCulture),
                        row.OutChannel.ToString(CultureInfo.InvariantCulture),
                        Format(row.IlIn),
                        Format(row.IlDut),
                        Format(row.IlOut),
                        Format(row.RlIn),
                        Format(row.RlDut),
                        Format(row.RlOut),
                        Format(row.DutPathM),
                        Format(row.LinkLengthM),
                        row.ValidSamples.ToString(CultureInfo.InvariantCulture),
                        row.Failure == null ? "" : row.Failure.Replace(",", "，")
                    }));
                }
            }
        }

        static string Format(double value)
        {
            return double.IsNaN(value) ? "" : value.ToString("F3", CultureInfo.InvariantCulture);
        }
    }
}
