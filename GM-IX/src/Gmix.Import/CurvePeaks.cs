using System;
using System.Collections.Generic;

namespace Gmix.Import
{
    /// <summary>dB 曲线上的一个峰。</summary>
    internal sealed class PeakHit
    {
        public int Index;
        public double LocM;
        public double HeightDb;
        public double ProminenceDb;
        public double BaseDb;
    }

    /// <summary>
    /// 基于 dB 曲线的峰值检测（不依赖 detect.dll，也不需要 P/S/A 原始样本）。
    /// 口径：局部极大 + 左右谷底突出度 + 最小间距；只做"哪里有明显反射"，不冒充厂商检测结果。
    /// </summary>
    internal static class CurvePeaks
    {
        public static List<PeakHit> Find(double[] db, double stepM, double minProminenceDb, double minDistanceM)
        {
            var hits = new List<PeakHit>();
            if (db == null || db.Length < 3 || !(stepM > 0)) return hits;

            int halfWindow = Math.Max(1, (int)Math.Round(minDistanceM / stepM));
            double floorDb = Percentile(db, 0.2);
            double heightGate = floorDb + minProminenceDb;

            for (int i = 1; i < db.Length - 1; i++)
            {
                if (!double.IsFinite(db[i])) continue;
                if (!(db[i] > db[i - 1]) || !(db[i] >= db[i + 1])) continue;
                // 绝对高度门限：噪声底之上至少 minProminenceDb，避免把瑞利衰落造成的局部尖峰当事件
                if (db[i] < heightGate) continue;

                int lo = Math.Max(0, i - halfWindow);
                int hi = Math.Min(db.Length - 1, i + halfWindow);
                double leftMin = double.PositiveInfinity;
                double rightMin = double.PositiveInfinity;
                for (int k = lo; k < i; k++) if (db[k] < leftMin) leftMin = db[k];
                for (int k = i + 1; k <= hi; k++) if (db[k] < rightMin) rightMin = db[k];
                if (double.IsPositiveInfinity(leftMin) || double.IsPositiveInfinity(rightMin)) continue;

                double baseLevel = Math.Max(leftMin, rightMin);
                double prominence = db[i] - baseLevel;
                if (prominence < minProminenceDb) continue;

                hits.Add(new PeakHit
                {
                    Index = i,
                    LocM = i * stepM,
                    HeightDb = db[i],
                    ProminenceDb = prominence,
                    BaseDb = baseLevel
                });
            }

            // 最小间距：同一反射簇只保留最高峰
            var kept = new List<PeakHit>();
            foreach (PeakHit hit in hits)
            {
                if (kept.Count == 0 || hit.Index - kept[kept.Count - 1].Index >= halfWindow)
                    kept.Add(hit);
                else if (hit.HeightDb > kept[kept.Count - 1].HeightDb)
                    kept[kept.Count - 1] = hit;
            }
            return kept;
        }

        /// <summary>窗口功率积分转 dB（10·log10 功率域，与平台数值域一致）。</summary>
        public static double WindowPowerDb(double[] db, int center, int half)
        {
            int lo = Math.Max(0, center - half);
            int hi = Math.Min(db.Length - 1, center + half);
            double sum = 0;
            for (int i = lo; i <= hi; i++) sum += Math.Pow(10.0, db[i] / 10.0);
            return sum > 0 ? 10.0 * Math.Log10(sum) : double.NaN;
        }

        public static double Median(double[] values)
        {
            return Percentile(values, 0.5);
        }

        /// <summary>分位数（0..1），用于噪声底估计：0.2 分位比中位数更抗信号占位。</summary>
        public static double Percentile(double[] values, double q)
        {
            if (values == null || values.Length == 0) return double.NaN;
            var copy = (double[])values.Clone();
            Array.Sort(copy);
            int index = (int)Math.Round((copy.Length - 1) * q, MidpointRounding.AwayFromZero);
            if (index < 0) index = 0;
            if (index >= copy.Length) index = copy.Length - 1;
            return copy[index];
        }

        public static double MedianRange(double[] values, int from, int to)
        {
            int lo = Math.Max(0, from);
            int hi = Math.Min(values.Length - 1, to);
            if (hi < lo) return double.NaN;
            var copy = new double[hi - lo + 1];
            Array.Copy(values, lo, copy, 0, copy.Length);
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }
    }
}
