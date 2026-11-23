using System;
using System.Collections.Generic;

namespace GmixLx.Core
{
    /// <summary>峰值判定阈值模式。</summary>
    public enum PeakThresholdMode
    {
        /// <summary>绝对阈值（demo 原行为，适用于 dBm 曲线）。</summary>
        Absolute = 0,

        /// <summary>相对噪声底：阈值 = 曲线中位电平 + 余量（LGA50 为 dB_rel 曲线，推荐）。</summary>
        RelativeToNoiseFloor = 1
    }

    /// <summary>
    /// 64 路指标表的计算参数，默认值全部来自 demo 行为基线
    /// （research/demo-lga50/DataReceiver.cs，只读证据，规则 00）。
    /// </summary>
    public sealed class CurveMetricsConfig
    {
        /// <summary>阈值模式；LGA50 默认相对噪声底（2026-11-12 实测标定）。</summary>
        public PeakThresholdMode ThresholdMode = PeakThresholdMode.RelativeToNoiseFloor;

        /// <summary>峰值判定阈值（dB）：数值超过此值的连续段视为一个峰。demo 默认 −77.5。</summary>
        public double PeakThresholdDb = -77.5;

        /// <summary>
        /// 相对噪声底模式下的余量（dB）。2026-11-12 全 64 路标定：余量 25/30/35 dB 时
        /// 32 条正常路径全部稳定检出 8 峰；15/20 dB 会多出 1~2 个伪峰。
        /// 取 30 dB，等效阈值 ≈ 中位 −107.7 + 30 = −77.7，与 demo 的绝对 −77.5 dB 几乎相同。
        /// </summary>
        public double NoiseFloorMarginDb = 30.0;

        /// <summary>
        /// 相邻过阈段的未过阈长度 ≤ 此值（米）则合并为一个峰。
        /// 原 demo 以行计 50 行；在 185 µm 下恰为 9.25 mm，改用米后与曲线步距无关。
        /// </summary>
        public double PeakMergeGapM = 0.00925;

        /// <summary>A 点位置（米，契约参考系）。演示默认 2500 行 × 0.0002 m = 0.5 m。</summary>
        public double MarkAM = 0.5;

        /// <summary>D 点位置（米，契约参考系）。演示默认 32500 行 × 0.0002 m = 6.5 m。</summary>
        public double MarkDM = 6.5;

        /// <summary>
        /// 局部平均半窗（米）。原 demo 以行计 250 行；在 185 µm 下恰为 46.25 mm。
        /// </summary>
        public double AvgHalfWindowM = 0.04625;

        /// <summary>行→米换算系数。demo 默认 0.0002；LGA50 实测点距 185 µm（TBD-04），现场可调。</summary>
        public double MetersPerRow = 0.0002;

        /// <summary>米 → 行号（四舍五入）；本设备曲线的步距由 <see cref="MetersPerRow"/> 给出。</summary>
        public double RowOfMeters(double distanceM)
        {
            if (!(MetersPerRow > 0) || double.IsNaN(MetersPerRow) || double.IsInfinity(MetersPerRow))
                throw new InvalidOperationException("MetersPerRow 必须为有限正数");
            return Math.Round(distanceM / MetersPerRow);
        }

        /// <summary>按米给出的长度换算为行数（至少 0）。</summary>
        public int RowsOfMeters(double lengthM)
        {
            if (lengthM <= 0) return 0;
            return (int)RowOfMeters(lengthM);
        }

        /// <summary>深拷贝（数组逐份复制），供"由参考系判据派生本设备判据"时使用。</summary>
        public CurveMetricsConfig Clone()
        {
            var copy = (CurveMetricsConfig)MemberwiseClone();
            copy.SriByIn = (double[])SriByIn.Clone();
            copy.SroByOut = (double[])SroByOut.Clone();
            copy.SrbByIn = (double[])SrbByIn.Clone();
            copy.SrcByOut = (double[])SrcByOut.Clone();
            return copy;
        }

        /// <summary>IL-DUT 的标量基准修正（config 的 SRO）。</summary>
        public double SroBase = 0.0;

        /// <summary>IN 通道基准修正 SRI-1..8，缺省 0。</summary>
        public double[] SriByIn = new double[8];

        /// <summary>OUT 通道基准修正 SRO-1..8，缺省 0。</summary>
        public double[] SroByOut = new double[8];

        /// <summary>IN 通道基准修正 SRB-1..8，缺省 0。</summary>
        public double[] SrbByIn = new double[8];

        /// <summary>OUT 通道基准修正 SRC-1..8，缺省 0。</summary>
        public double[] SrcByOut = new double[8];
    }

    /// <summary>一条路径的检测峰。</summary>
    public sealed class CurvePeak
    {
        public int Index;
        public double Db;
        public int Width;
        public double Centroid;
    }

    /// <summary>一条路径的 11 列指标（对应 demo 的指标表）。</summary>
    public sealed class PathMetrics
    {
        public double IlIn;
        public double IlDut;
        public double IlOut;
        public double RlIn;
        public double RlDut;
        public double RlOut;
        public double LinkLengthM;
        public double DutPathM;

        /// <summary>检出峰数；demo 要求恰好 8 个才计算指标。</summary>
        public int PeakCount;

        /// <summary>为 null 表示计算成功；否则为不可计算的明确原因。</summary>
        public string Failure;

        public bool Succeeded
        {
            get { return Failure == null; }
        }

        public static PathMetrics Fail(string reason, int peakCount)
        {
            // 指标一律置 NaN，避免界面上把 0.000 误读为真实测量值
            return new PathMetrics
            {
                Failure = reason,
                PeakCount = peakCount,
                IlIn = double.NaN,
                IlDut = double.NaN,
                IlOut = double.NaN,
                RlIn = double.NaN,
                RlDut = double.NaN,
                RlOut = double.NaN,
                LinkLengthM = double.NaN,
                DutPathM = double.NaN
            };
        }
    }

    /// <summary>
    /// LGA50 路径指标计算（纯函数，无 IO）。
    /// 完全按 demo 行为基线实现（research/demo-lga50/DataReceiver.cs）：
    /// 阈值分段找峰 → 8 峰标记 A/B/BB/C/CC/D/E → 区间 mW 求和得 RL → 点功率差一半得 IL。
    /// 峰数不等于 8 时按 demo 规则直接判不可计算，不做兜底。
    /// </summary>
    public static class CurveMetrics
    {
        const int RequiredPeakCount = 8;

        public static PathMetrics Compute(double[] levelsDb, int inChannel, int outChannel, CurveMetricsConfig config)
        {
            if (levelsDb == null) throw new ArgumentNullException("levelsDb");
            if (config == null) throw new ArgumentNullException("config");
            if (inChannel < 1 || inChannel > 8) throw new ArgumentOutOfRangeException("inChannel");
            if (outChannel < 1 || outChannel > 8) throw new ArgumentOutOfRangeException("outChannel");
            if (config.AvgHalfWindowM < 0) throw new InvalidOperationException("AvgHalfWindowM 不能为负");
            if (levelsDb.Length == 0) return PathMetrics.Fail("空曲线", 0);

            int mergeGap = config.RowsOfMeters(config.PeakMergeGapM);
            int halfWindow = config.RowsOfMeters(config.AvgHalfWindowM);
            List<CurvePeak> peaks = FindPeaks(levelsDb, ResolveThreshold(levelsDb, config), mergeGap);
            if (peaks.Count != RequiredPeakCount)
            {
                return PathMetrics.Fail("峰数 " + peaks.Count + " ≠ " + RequiredPeakCount, peaks.Count);
            }

            double a = config.RowOfMeters(config.MarkAM);
            double b = Math.Round(peaks[3].Centroid);
            double bb = Math.Round((peaks[3].Centroid + peaks[4].Centroid) / 2);
            double cc = Math.Round((peaks[5].Centroid + peaks[6].Centroid) / 2);
            double c = Math.Round(peaks[6].Centroid);
            double d = config.RowOfMeters(config.MarkDM);
            double e = Math.Round(peaks[7].Centroid);

            int last = levelsDb.Length - 1;
            if (a < 0 || d > last || a >= d || bb < a || bb > d || cc < a || cc > d)
            {
                return PathMetrics.Fail(
                    "标记点越界（A=" + a + " BB=" + bb + " CC=" + cc + " D=" + d + "，点数=" + levelsDb.Length + "）",
                    peaks.Count);
            }

            double[] mw = ToLinearPower(levelsDb);

            double rlDut = ToDbm(SumRange(mw, a, d));
            double rlIn = ToDbm(SumRange(mw, a, bb));
            double rlOut = ToDbm(SumRange(mw, cc, d));

            double aDbm = AverageAround(mw, a, halfWindow);
            double dDbm = AverageAround(mw, d, halfWindow);
            double bbDbm = AverageAround(mw, bb, halfWindow);
            double ccDbm = AverageAround(mw, cc, halfWindow);

            double sriIn = ConfigAt(config.SriByIn, inChannel, "SriByIn");
            double sroOut = ConfigAt(config.SroByOut, outChannel, "SroByOut");
            double srbIn = ConfigAt(config.SrbByIn, inChannel, "SrbByIn");
            double srcOut = ConfigAt(config.SrcByOut, outChannel, "SrcByOut");

            return new PathMetrics
            {
                // 往返光程，功率差除以 2
                IlDut = (aDbm - dDbm) / 2 + config.SroBase + sriIn + sroOut,
                IlIn = (aDbm - bbDbm) / 2 + srbIn,
                IlOut = (ccDbm - dDbm) / 2 + srcOut,
                RlIn = rlIn,
                RlDut = rlDut,
                RlOut = rlOut,
                LinkLengthM = e * config.MetersPerRow,
                DutPathM = (c - b) * config.MetersPerRow,
                PeakCount = peaks.Count
            };
        }

        /// <summary>
        /// 解析实际阈值：绝对模式直接用配置值；相对模式取曲线中位电平 + 余量
        /// （中位数比均值更能抵抗强反射峰与开路端的抬升）。
        /// </summary>
        public static double ResolveThreshold(double[] levelsDb, CurveMetricsConfig config)
        {
            if (levelsDb == null) throw new ArgumentNullException("levelsDb");
            if (config == null) throw new ArgumentNullException("config");
            if (config.ThresholdMode == PeakThresholdMode.Absolute) return config.PeakThresholdDb;
            return NoiseFloorMedian(levelsDb) + config.NoiseFloorMarginDb;
        }

        /// <summary>曲线电平中位数（噪声底估计）。</summary>
        public static double NoiseFloorMedian(double[] levelsDb)
        {
            if (levelsDb == null) throw new ArgumentNullException("levelsDb");
            if (levelsDb.Length == 0) throw new InvalidOperationException("空曲线无噪声底");

            var copy = (double[])levelsDb.Clone();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }

        /// <summary>
        /// 阈值分段找峰（demo `FindPeaksByThreshold`）：超过阈值的连续段为一个峰，
        /// 段间未过阈行数 ≤ mergeGap 时合并；峰顶取段内最大点，质心按 mW 加权。
        /// </summary>
        public static List<CurvePeak> FindPeaks(double[] levelsDb, double thresholdDb, int mergeGap)
        {
            var peaks = new List<CurvePeak>();
            if (levelsDb == null || levelsDb.Length == 0) return peaks;
            if (mergeGap < 0) mergeGap = 0;

            var starts = new List<int>();
            var ends = new List<int>();
            var maxIdx = new List<int>();
            var maxDb = new List<double>();

            int i = 0;
            while (i < levelsDb.Length)
            {
                if (levelsDb[i] <= thresholdDb) { i++; continue; }

                int segStart = i;
                int segMaxIdx = i;
                double segMaxDb = levelsDb[i];
                int segEnd = i;
                while (segEnd + 1 < levelsDb.Length && levelsDb[segEnd + 1] > thresholdDb)
                {
                    segEnd++;
                    if (levelsDb[segEnd] > segMaxDb) { segMaxDb = levelsDb[segEnd]; segMaxIdx = segEnd; }
                }
                starts.Add(segStart); ends.Add(segEnd); maxIdx.Add(segMaxIdx); maxDb.Add(segMaxDb);
                i = segEnd + 1;
            }

            if (starts.Count == 0) return peaks;

            int curStart = starts[0], curEnd = ends[0], curMaxIdx = maxIdx[0];
            double curMaxDb = maxDb[0];

            for (int s = 1; s < starts.Count; s++)
            {
                int gap = starts[s] - curEnd - 1;
                if (gap <= mergeGap)
                {
                    curEnd = ends[s];
                    if (maxDb[s] > curMaxDb) { curMaxDb = maxDb[s]; curMaxIdx = maxIdx[s]; }
                }
                else
                {
                    peaks.Add(MakePeak(levelsDb, curStart, curEnd, curMaxIdx, curMaxDb));
                    curStart = starts[s]; curEnd = ends[s]; curMaxIdx = maxIdx[s]; curMaxDb = maxDb[s];
                }
            }
            peaks.Add(MakePeak(levelsDb, curStart, curEnd, curMaxIdx, curMaxDb));
            return peaks;
        }

        static CurvePeak MakePeak(double[] levelsDb, int segStart, int segEnd, int maxIdx, double maxDb)
        {
            double wsum = 0, wpos = 0;
            for (int j = segStart; j <= segEnd; j++)
            {
                double w = Math.Pow(10.0, levelsDb[j] / 10.0);
                wsum += w;
                wpos += j * w;
            }
            return new CurvePeak
            {
                Index = maxIdx,
                Db = maxDb,
                Width = segEnd - segStart + 1,
                Centroid = wsum > 0 ? wpos / wsum : (segStart + segEnd) / 2.0
            };
        }

        static double[] ToLinearPower(double[] levelsDb)
        {
            var mw = new double[levelsDb.Length];
            for (int i = 0; i < levelsDb.Length; i++)
            {
                double v = levelsDb[i];
                if (double.IsNaN(v) || double.IsInfinity(v))
                    throw new InvalidOperationException("曲线第 " + i + " 点电平非法：" + v);
                mw[i] = Math.Pow(10.0, v / 10.0);
            }
            return mw;
        }

        static double SumRange(double[] mw, double fromRow, double toRow)
        {
            int from = (int)Math.Round(fromRow);
            int to = (int)Math.Round(toRow);
            if (from < 0) from = 0;
            if (to > mw.Length - 1) to = mw.Length - 1;
            double sum = 0;
            for (int i = from; i <= to; i++) sum += mw[i];
            return sum;
        }

        static double AverageAround(double[] mw, double centerRow, int halfWindow)
        {
            int center = (int)Math.Round(centerRow);
            int from = Math.Max(0, center - halfWindow);
            int to = Math.Min(mw.Length - 1, center + halfWindow);
            double sum = 0;
            for (int i = from; i <= to; i++) sum += mw[i];
            return ToDbm(sum / (to - from + 1));
        }

        static double ToDbm(double mw)
        {
            return mw > 0 ? 10.0 * Math.Log10(mw) : double.NaN;
        }

        static double ConfigAt(double[] values, int channel, string name)
        {
            if (values == null) return 0.0;
            if (values.Length != 8)
                throw new InvalidOperationException(name + " 长度必须为 8");
            return values[channel - 1];
        }
    }
}
