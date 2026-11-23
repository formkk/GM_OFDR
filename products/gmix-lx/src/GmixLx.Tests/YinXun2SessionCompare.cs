using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GmixLx.Core;
using GmixLx.Orchestration;
using GmixLx.Vendor;

namespace GmixLx.Tests
{
    /// <summary>
    /// 两次映讯_2 采集会话的稳定性对比：逐帧 8 峰峰位/电平、噪声底、IL/RL，
    /// 输出会话内跨帧 σ、会话间均值差，以及缺峰发生在第几峰。
    /// 手动运行：<c>--compare-yinxun2-sessions [dirA] [dirB] [framesPerPath]</c>
    /// 只读原始 bin，不修改任何采集数据。
    /// </summary>
    internal static class YinXun2SessionCompare
    {
        const string CalibrationRelativePath = "products/gmix-lx/config/yinxun2_reference_calibration.json";
        const double MissingToleranceM = 0.05;

        sealed class PeakStats
        {
            public int Count;
            public double SumPos, SumPosSq, SumLvl, SumLvlSq;

            public double MeanPos { get { return Count > 0 ? SumPos / Count : double.NaN; } }
            public double MeanLvl { get { return Count > 0 ? SumLvl / Count : double.NaN; } }
            public double SigmaPos { get { return SqrtOf(SumPosSq, SumPos, Count); } }
            public double SigmaLvl { get { return SqrtOf(SumLvlSq, SumLvl, Count); } }
        }

        sealed class PathStats
        {
            public readonly PeakStats[] Peaks = new PeakStats[9];
            public readonly int[] Missing = new int[9];
            public readonly List<double[]> PartialPositions = new List<double[]>();
            public int Frames, Frames8, NoiseCount;
            public double NoiseSum, NoiseSqSum;
            public int MetricCount;
            public double IlSum, IlSqSum, RlSum, RlSqSum;

            public PathStats()
            {
                for (int i = 1; i <= 8; i++) Peaks[i] = new PeakStats();
            }

            public double SigmaNoise { get { return SqrtOf(NoiseSqSum, NoiseSum, NoiseCount); } }
            public double MeanNoise { get { return NoiseCount > 0 ? NoiseSum / NoiseCount : double.NaN; } }
        }

        public static int Run(string dirA, string dirB, int framesPerPath)
        {
            string calibrationPath = YinXun2LiveSmoke.Resolve(CalibrationRelativePath);
            if (calibrationPath == null)
            {
                Console.WriteLine("COMPARE FATAL: 未找到 " + CalibrationRelativePath);
                return 2;
            }
            if (string.IsNullOrEmpty(dirA) || string.IsNullOrEmpty(dirB) ||
                !Directory.Exists(dirA) || !Directory.Exists(dirB))
            {
                Console.WriteLine("COMPARE FATAL: 目录不存在 dirA=" + dirA + " dirB=" + dirB);
                return 2;
            }
            if (framesPerPath <= 0) framesPerPath = 8;

            YinXun2ReferenceCalibration calibration = YinXun2ReferenceCalibration.Load(calibrationPath);
            CurveMetricsConfig native = YinXun2PathMeasurer.DefaultNativeMetrics(calibration);
            int mergeGap = native.RowsOfMeters(native.PeakMergeGapM);
            double[] axis = BuildAxis(calibration);

            List<string> roundsA = CompleteRounds(dirA, framesPerPath);
            List<string> roundsB = CompleteRounds(dirB, framesPerPath);
            if (roundsA.Count == 0 || roundsB.Count == 0)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "COMPARE FATAL: 完整轮次不足 A={0} B={1}（每轮需 64 路 × {2} 帧）",
                    roundsA.Count, roundsB.Count, framesPerPath));
                return 1;
            }

            Console.WriteLine("A " + dirA + "  完整轮=" + roundsA.Count);
            Console.WriteLine("B " + dirB + "  完整轮=" + roundsB.Count);

            var statsA = NewStats();
            var statsB = NewStats();
            int framesA = 0;
            int framesB = 0;
            ProcessSession(roundsA, framesPerPath, calibration, native, mergeGap, axis, statsA, ref framesA);
            ProcessSession(roundsB, framesPerPath, calibration, native, mergeGap, axis, statsB, ref framesB);

            ClassifyMissing(statsA, statsB);
            PrintSummary(statsA, statsB, framesA, framesB, roundsA.Count, roundsB.Count);
            string csvPath = Path.Combine(dirB, "session_compare.csv");
            WriteCsv(csvPath, statsA, statsB);
            Console.WriteLine("明细 CSV: " + csvPath);
            return 0;
        }

        static PathStats[,] NewStats()
        {
            var stats = new PathStats[9, 9];
            for (int i = 1; i <= 8; i++)
                for (int j = 1; j <= 8; j++) stats[i, j] = new PathStats();
            return stats;
        }

        static double[] BuildAxis(YinXun2ReferenceCalibration calibration)
        {
            var axis = new double[YinXun2ProtocolConstants.PointCount];
            for (int i = 0; i < axis.Length; i++) axis[i] = i * calibration.YinXunPitchM;
            return axis;
        }

        static List<string> CompleteRounds(string root, int framesPerPath)
        {
            var rounds = new List<string>();
            string[] dirs = Directory.GetDirectories(root, "round_*");
            Array.Sort(dirs, StringComparer.Ordinal);
            foreach (string dir in dirs)
            {
                int complete = 0;
                for (int inCh = 1; inCh <= 8; inCh++)
                {
                    for (int outCh = 1; outCh <= 8; outCh++)
                    {
                        string pathDir = Path.Combine(dir, PathName(inCh, outCh));
                        bool ok = true;
                        for (int f = 0; f < framesPerPath; f++)
                        {
                            if (!File.Exists(Path.Combine(pathDir, FrameName(f)))) { ok = false; break; }
                        }
                        if (ok) complete++;
                    }
                }
                if (complete == 64) rounds.Add(dir);
            }
            return rounds;
        }

        static void ProcessSession(List<string> rounds, int framesPerPath,
            YinXun2ReferenceCalibration calibration, CurveMetricsConfig native, int mergeGap,
            double[] axis, PathStats[,] stats, ref int framesTotal)
        {
            for (int inCh = 1; inCh <= 8; inCh++)
            {
                for (int outCh = 1; outCh <= 8; outCh++)
                {
                    PathStats st = stats[inCh, outCh];
                    foreach (string round in rounds)
                    {
                        string pathDir = Path.Combine(round, PathName(inCh, outCh));
                        for (int f = 0; f < framesPerPath; f++)
                        {
                            double[] levels = ReadDoubles(Path.Combine(pathDir, FrameName(f)));
                            if (levels == null || levels.Length != axis.Length)
                            {
                                Console.WriteLine("SKIP " + pathDir + " frame " + f + "（长度不符）");
                                continue;
                            }

                            st.Frames++;
                            framesTotal++;
                            YinXun2CurvePreparation.MaskInternalSection(levels, axis, calibration);
                            YinXun2CurvePreparation.ApplyLevelOffset(levels, calibration.LevelOffsetDb);

                            double noise = CurveMetrics.NoiseFloorMedian(levels);
                            st.NoiseSum += noise;
                            st.NoiseSqSum += noise * noise;
                            st.NoiseCount++;

                            List<CurvePeak> peaks = CurveMetrics.FindPeaks(
                                levels, noise + native.NoiseFloorMarginDb, mergeGap);
                            if (peaks.Count == 8)
                            {
                                st.Frames8++;
                                for (int i = 0; i < 8; i++)
                                {
                                    PeakStats ps = st.Peaks[i + 1];
                                    double pos = peaks[i].Centroid * calibration.YinXunPitchM;
                                    ps.Count++;
                                    ps.SumPos += pos;
                                    ps.SumPosSq += pos * pos;
                                    ps.SumLvl += peaks[i].Db;
                                    ps.SumLvlSq += peaks[i].Db * peaks[i].Db;
                                }
                            }
                            else
                            {
                                var positions = new double[peaks.Count];
                                for (int i = 0; i < peaks.Count; i++)
                                    positions[i] = peaks[i].Centroid * calibration.YinXunPitchM;
                                st.PartialPositions.Add(positions);
                            }

                            if (peaks.Count == 8)
                            {
                                PathMetrics m = CurveMetrics.Compute(levels, inCh, outCh, native);
                                if (m.Succeeded)
                                {
                                    st.MetricCount++;
                                    st.IlSum += m.IlDut;
                                    st.IlSqSum += m.IlDut * m.IlDut;
                                    st.RlSum += m.RlDut;
                                    st.RlSqSum += m.RlDut * m.RlDut;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>用两次会话 8 峰均值作为模板，把非 8 峰帧缺失的位置归到第几峰。</summary>
        static void ClassifyMissing(PathStats[,] statsA, PathStats[,] statsB)
        {
            for (int inCh = 1; inCh <= 8; inCh++)
            {
                for (int outCh = 1; outCh <= 8; outCh++)
                {
                    PathStats a = statsA[inCh, outCh];
                    PathStats b = statsB[inCh, outCh];
                    var template = new double[9];
                    for (int i = 1; i <= 8; i++)
                    {
                        double sum = a.Peaks[i].SumPos + b.Peaks[i].SumPos;
                        int count = a.Peaks[i].Count + b.Peaks[i].Count;
                        template[i] = count > 0 ? sum / count : double.NaN;
                    }
                    ClassifyOne(a, template);
                    ClassifyOne(b, template);
                }
            }
        }

        static void ClassifyOne(PathStats st, double[] template)
        {
            foreach (double[] positions in st.PartialPositions)
            {
                if (positions.Length >= 8) continue;
                for (int i = 1; i <= 8; i++)
                {
                    if (double.IsNaN(template[i])) continue;
                    bool matched = false;
                    foreach (double pos in positions)
                    {
                        if (Math.Abs(pos - template[i]) <= MissingToleranceM) { matched = true; break; }
                    }
                    if (!matched) st.Missing[i]++;
                }
            }
        }

        static void PrintSummary(PathStats[,] statsA, PathStats[,] statsB,
            int framesA, int framesB, int roundsA, int roundsB)
        {
            var sigmaPosA = new List<double>();
            var sigmaPosB = new List<double>();
            var sigmaLvlA = new List<double>();
            var sigmaLvlB = new List<double>();
            var deltaPos = new List<double>();
            var deltaLvl = new List<double>();
            var sigmaNoiseA = new List<double>();
            var sigmaNoiseB = new List<double>();
            var sigmaIlA = new List<double>();
            var sigmaIlB = new List<double>();
            var sigmaRlA = new List<double>();
            var sigmaRlB = new List<double>();
            var deltaIl = new List<double>();
            var deltaRl = new List<double>();
            int frames8A = 0;
            int frames8B = 0;
            int metricA = 0;
            int metricB = 0;
            var missingA = new int[9];
            var missingB = new int[9];

            for (int inCh = 1; inCh <= 8; inCh++)
            {
                for (int outCh = 1; outCh <= 8; outCh++)
                {
                    PathStats a = statsA[inCh, outCh];
                    PathStats b = statsB[inCh, outCh];
                    frames8A += a.Frames8;
                    frames8B += b.Frames8;
                    metricA += a.MetricCount;
                    metricB += b.MetricCount;
                    for (int i = 1; i <= 8; i++) { missingA[i] += a.Missing[i]; missingB[i] += b.Missing[i]; }

                    for (int i = 1; i <= 8; i++)
                    {
                        PeakStats pa = a.Peaks[i];
                        PeakStats pb = b.Peaks[i];
                        if (pa.Count >= 2 && pb.Count >= 2)
                        {
                            sigmaPosA.Add(pa.SigmaPos * 1000.0);
                            sigmaPosB.Add(pb.SigmaPos * 1000.0);
                            sigmaLvlA.Add(pa.SigmaLvl);
                            sigmaLvlB.Add(pb.SigmaLvl);
                            deltaPos.Add(Math.Abs(pb.MeanPos - pa.MeanPos) * 1000.0);
                            deltaLvl.Add(Math.Abs(pb.MeanLvl - pa.MeanLvl));
                        }
                    }

                    if (a.NoiseCount >= 2 && b.NoiseCount >= 2)
                    {
                        sigmaNoiseA.Add(a.SigmaNoise);
                        sigmaNoiseB.Add(b.SigmaNoise);
                    }
                    if (a.MetricCount >= 2 && b.MetricCount >= 2)
                    {
                        double meanIlA = a.IlSum / a.MetricCount;
                        double meanIlB = b.IlSum / b.MetricCount;
                        double meanRlA = a.RlSum / a.MetricCount;
                        double meanRlB = b.RlSum / b.MetricCount;
                        sigmaIlA.Add(Math.Sqrt(Math.Max(0.0,
                            a.IlSqSum / a.MetricCount - meanIlA * meanIlA)));
                        sigmaIlB.Add(Math.Sqrt(Math.Max(0.0,
                            b.IlSqSum / b.MetricCount - meanIlB * meanIlB)));
                        sigmaRlA.Add(Math.Sqrt(Math.Max(0.0,
                            a.RlSqSum / a.MetricCount - meanRlA * meanRlA)));
                        sigmaRlB.Add(Math.Sqrt(Math.Max(0.0,
                            b.RlSqSum / b.MetricCount - meanRlB * meanRlB)));
                        deltaIl.Add(meanIlB - meanIlA);
                        deltaRl.Add(meanRlB - meanRlA);
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "会话A: 完整轮={0} 帧={1} 8峰={2} ({3:P2}) 指标有效={4}",
                roundsA, framesA, frames8A, Ratio(frames8A, framesA), metricA));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "会话B: 完整轮={0} 帧={1} 8峰={2} ({3:P2}) 指标有效={4}",
                roundsB, framesB, frames8B, Ratio(frames8B, framesB), metricB));

            PrintLine("峰位跨帧 σ (mm)", sigmaPosA, sigmaPosB);
            PrintLine("峰电平跨帧 σ (dB)", sigmaLvlA, sigmaLvlB);
            PrintLine("噪声底跨帧 σ (dB)", sigmaNoiseA, sigmaNoiseB);
            PrintLine("IL-DUT 跨帧 σ (dB)", sigmaIlA, sigmaIlB);
            PrintLine("RL-DUT 跨帧 σ (dB)", sigmaRlA, sigmaRlB);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "会话间 |Δ峰位| (mm): 中位 {0:F3}  P95 {1:F3}", Median(deltaPos), Percentile(deltaPos, 0.95)));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "会话间 |Δ峰电平| (dB): 中位 {0:F3}  P95 {1:F3}", Median(deltaLvl), Percentile(deltaLvl, 0.95)));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "会话间 ΔIL-DUT (dB): 中位 {0:+0.000;-0.000;0.000}  P5 {1:+0.000;-0.000;0.000}  P95 {2:+0.000;-0.000;0.000}（B−A）",
                Median(deltaIl), Percentile(deltaIl, 0.05), Percentile(deltaIl, 0.95)));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "会话间 ΔRL-DUT (dB): 中位 {0:+0.000;-0.000;0.000}  P5 {1:+0.000;-0.000;0.000}  P95 {2:+0.000;-0.000;0.000}（B−A）",
                Median(deltaRl), Percentile(deltaRl, 0.05), Percentile(deltaRl, 0.95)));

            Console.WriteLine("缺峰归属（按峰序号，A / B）：");
            for (int i = 1; i <= 8; i++)
            {
                if (missingA[i] == 0 && missingB[i] == 0) continue;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  P{0}: {1} / {2}", i, missingA[i], missingB[i]));
            }
        }

        static void PrintLine(string label, List<double> a, List<double> b)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0}: A 中位 {1:F3} P95 {2:F3} | B 中位 {3:F3} P95 {4:F3}",
                label, Median(a), Percentile(a, 0.95), Median(b), Percentile(b, 0.95)));
        }

        static void WriteCsv(string path, PathStats[,] statsA, PathStats[,] statsB)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("in,out,peak,nA,posA_mm,sigmaA_mm,lvlA_db,sigmaA_db,nB,posB_mm,sigmaB_mm,lvlB_db,sigmaB_db," +
                "dPos_mm,dLvl_db,missingA,missingB");
            for (int inCh = 1; inCh <= 8; inCh++)
            {
                for (int outCh = 1; outCh <= 8; outCh++)
                {
                    PathStats a = statsA[inCh, outCh];
                    PathStats b = statsB[inCh, outCh];
                    for (int i = 1; i <= 8; i++)
                    {
                        PeakStats pa = a.Peaks[i];
                        PeakStats pb = b.Peaks[i];
                        sb.Append(inCh).Append(',').Append(outCh).Append(',').Append(i).Append(',')
                          .Append(pa.Count).Append(',')
                          .Append(Num(pa.MeanPos * 1000.0, "F3")).Append(',')
                          .Append(Num(pa.SigmaPos * 1000.0, "F3")).Append(',')
                          .Append(Num(pa.MeanLvl, "F3")).Append(',')
                          .Append(Num(pa.SigmaLvl, "F3")).Append(',')
                          .Append(pb.Count).Append(',')
                          .Append(Num(pb.MeanPos * 1000.0, "F3")).Append(',')
                          .Append(Num(pb.SigmaPos * 1000.0, "F3")).Append(',')
                          .Append(Num(pb.MeanLvl, "F3")).Append(',')
                          .Append(Num(pb.SigmaLvl, "F3")).Append(',')
                          .Append(Num((pb.MeanPos - pa.MeanPos) * 1000.0, "F3")).Append(',')
                          .Append(Num(pb.MeanLvl - pa.MeanLvl, "F3")).Append(',')
                          .Append(a.Missing[i]).Append(',')
                          .Append(b.Missing[i]).AppendLine();
                    }
                }
            }
            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
        }

        static double[] ReadDoubles(string path)
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length % 8 != 0) return null;
            var values = new double[bytes.Length / 8];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        static string PathName(int inCh, int outCh)
        {
            return "in" + inCh.ToString("00", CultureInfo.InvariantCulture) +
                   "_out" + outCh.ToString("00", CultureInfo.InvariantCulture);
        }

        static string FrameName(int frame)
        {
            return "frame_" + frame.ToString("000", CultureInfo.InvariantCulture) + ".bin";
        }

        static double SqrtOf(double sumSq, double sum, int count)
        {
            if (count < 2) return double.NaN;
            double mean = sum / count;
            return Math.Sqrt(Math.Max(0.0, (sumSq - count * mean * mean) / (count - 1)));
        }

        static double Ratio(int part, int total)
        {
            return total > 0 ? (double)part / total : double.NaN;
        }

        static double Median(List<double> values)
        {
            return Percentile(values, 0.5);
        }

        static double Percentile(List<double> values, double p)
        {
            if (values == null || values.Count == 0) return double.NaN;
            var copy = new List<double>(values);
            copy.Sort();
            int index = (int)Math.Round(p * (copy.Count - 1));
            if (index < 0) index = 0;
            if (index >= copy.Count) index = copy.Count - 1;
            return copy[index];
        }

        static string Num(double value, string format)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return string.Empty;
            return value.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
