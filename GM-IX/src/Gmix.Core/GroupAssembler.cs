using System;
using System.Collections.Generic;
using System.IO;

namespace Gmix.Data
{
    /// <summary>
    /// 组装配（ICD Part A §2.2）：多处 `Rxxx/` 的线性 ps 平均、分布轴一致性判定、group.json 组装。
    /// 本类不做 detect（Core 不依赖厂商库）；AVG 的检测由编排进程完成。
    /// </summary>
    public static class GroupAssembler
    {
        public const string AlignmentSameDist = "same_dist";
        public const string AlignmentResampled = "resampled";
        public const string AlignmentInvalid = "invalid";

        public sealed class AveragedSpectrum
        {
            public double[] Dist { get; set; }
            public double[] Freq { get; set; }
            public double[] Ps { get; set; }
            public string Alignment { get; set; }
            public int UsedCount { get; set; }
            public List<string> Rejected { get; set; }
            public List<string> Warnings { get; set; }
        }

        sealed class Spectrum
        {
            public string Dir;
            public double[] Dist;
            public double[] Freq;
            public double[] Ps;
        }

        /// <summary>把多个扫描目录的 ps 平均成一条参考轴上的谱；不可对齐时 alignment=invalid。</summary>
        public static AveragedSpectrum Average(IList<string> scanDirs)
        {
            var result = new AveragedSpectrum
            {
                Alignment = AlignmentInvalid,
                Rejected = new List<string>(),
                Warnings = new List<string>()
            };
            if (scanDirs == null || scanDirs.Count == 0)
            {
                result.Warnings.Add("没有可用的扫描目录");
                return result;
            }

            var valid = new List<Spectrum>();
            foreach (string dir in scanDirs)
            {
                try
                {
                    double[] dist = ArrayStore.ReadDoubles(Path.Combine(dir, "dist.f64"), 0);
                    double[] freq = ArrayStore.ReadDoubles(Path.Combine(dir, "freq.f64"), 0);
                    double[] ps = ArrayStore.ReadDoubles(Path.Combine(dir, "ps.f64"), 0);
                    if (dist.Length == 0 || dist.Length != ps.Length || freq.Length != ps.Length)
                    {
                        result.Rejected.Add(dir);
                        result.Warnings.Add("数组长度不一致: " + dir);
                        continue;
                    }
                    valid.Add(new Spectrum { Dir = dir, Dist = dist, Freq = freq, Ps = ps });
                }
                catch (Exception ex)
                {
                    result.Rejected.Add(dir);
                    result.Warnings.Add("读取失败: " + dir + " — " + ex.Message);
                }
            }

            if (valid.Count == 0)
            {
                result.Warnings.Add("没有任何可平均的有效扫描");
                return result;
            }

            Spectrum reference = valid[0];
            double bin = reference.Dist.Length > 1 ? reference.Dist[1] - reference.Dist[0] : 0.0;
            if (!(bin > 0))
            {
                result.Warnings.Add("参考轴步距无效，拒绝平均");
                return result;
            }

            bool sameDist = true;
            bool invalidAxis = false;
            var aligned = new List<double[]>(valid.Count);
            aligned.Add(reference.Ps);
            for (int i = 1; i < valid.Count; i++)
            {
                Spectrum other = valid[i];
                string kind = Classify(reference.Dist, other.Dist, bin);
                if (kind == AlignmentInvalid)
                {
                    invalidAxis = true;
                    result.Rejected.Add(other.Dir);
                    result.Warnings.Add("分布轴超出容差，拒绝该次: " + other.Dir);
                    continue;
                }
                if (kind == AlignmentResampled)
                {
                    sameDist = false;
                    aligned.Add(Interpolate(other.Dist, other.Ps, reference.Dist));
                }
                else
                {
                    aligned.Add(other.Ps);
                }
            }

            if (invalidAxis)
            {
                // ICD §2.2：任一分布轴超出容差即拒绝整组平均，alignment=invalid，组标记 failed。
                result.Alignment = AlignmentInvalid;
                result.Warnings.Add("存在超出容差的分布轴，拒绝整组平均");
                return result;
            }

            int n = reference.Dist.Length;
            var psAvg = new double[n];
            for (int k = 0; k < n; k++)
            {
                double sum = 0;
                foreach (double[] values in aligned) sum += values[k];
                psAvg[k] = sum / aligned.Count;
            }

            result.Dist = reference.Dist;
            result.Freq = reference.Freq;
            result.Ps = psAvg;
            result.UsedCount = aligned.Count;
            result.Alignment = sameDist ? AlignmentSameDist : AlignmentResampled;
            return result;
        }

        /// <summary>分布轴分类：长度一致且逐点偏差 ≤ 1 bin → same_dist；首末覆盖相对偏差 ≤ 5% → resampled；否则 invalid。</summary>
        public static string Classify(double[] reference, double[] other, double bin)
        {
            if (reference == null || other == null || reference.Length < 2 || other.Length < 2)
                return AlignmentInvalid;

            if (other.Length == reference.Length)
            {
                double maxDeviation = 0;
                for (int i = 0; i < reference.Length; i++)
                {
                    double deviation = Math.Abs(reference[i] - other[i]);
                    if (deviation > maxDeviation) maxDeviation = deviation;
                }
                if (maxDeviation <= bin) return AlignmentSameDist;
            }

            double refSpan = reference[reference.Length - 1] - reference[0];
            double otherSpan = other[other.Length - 1] - other[0];
            if (!(refSpan > 0) || !(otherSpan > 0)) return AlignmentInvalid;
            double relative = Math.Abs(otherSpan - refSpan) / refSpan;
            return relative <= 0.05 ? AlignmentResampled : AlignmentInvalid;
        }

        /// <summary>线性插值到目标轴；目标点超出源范围时按端点钳位。</summary>
        static double[] Interpolate(double[] sourceX, double[] sourceY, double[] targetX)
        {
            var result = new double[targetX.Length];
            int j = 0;
            for (int i = 0; i < targetX.Length; i++)
            {
                double x = targetX[i];
                while (j < sourceX.Length - 2 && sourceX[j + 1] < x) j++;
                double x0 = sourceX[j];
                double x1 = sourceX[j + 1];
                double t = (x1 - x0) > 0 ? (x - x0) / (x1 - x0) : 0.0;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                result[i] = sourceY[j] + t * (sourceY[j + 1] - sourceY[j]);
            }
            return result;
        }

        /// <summary>组装 group.json；status 由 validCount 与计划次数决定。</summary>
        public static GroupDocument BuildDocument(string groupId, string createdAtUtc, int plannedCount,
            AcquisitionInfo acquisition, string calibrationId, string configHash,
            IList<string> scanIds, IList<string> rejected, string alignment, string avgScanId)
        {
            int planned = plannedCount > 0 ? plannedCount : (scanIds == null ? 0 : scanIds.Count);
            int rejectedCount = rejected == null ? 0 : rejected.Count;
            int valid = planned - rejectedCount;
            if (valid < 0) valid = 0;

            return new GroupDocument
            {
                schemaVersion = "1.0",
                groupId = groupId,
                createdAtUtc = createdAtUtc,
                status = valid == 0 ? "failed" : (valid < planned ? "incomplete" : "complete"),
                averageCount = planned,
                scanIds = scanIds == null ? new string[0] : ToArray(scanIds),
                validCount = valid,
                rejected = rejected == null ? new string[0] : ToArray(rejected),
                avgMode = "linear_ps",
                alignment = alignment,
                avgScanId = avgScanId,
                calibrationId = calibrationId,
                configHash = configHash,
                acquisition = acquisition
            };
        }

        static string[] ToArray(IList<string> values)
        {
            var array = new string[values.Count];
            for (int i = 0; i < values.Count; i++) array[i] = values[i];
            return array;
        }
    }
}
