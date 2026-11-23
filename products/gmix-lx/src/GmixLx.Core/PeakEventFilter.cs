using System;
using System.Collections.Generic;

namespace GmixLx.Core
{
    /// <summary>事件表中每个峰的处理状态。</summary>
    public enum PeakEventStatus
    {
        /// <summary>正常独立事件。</summary>
        Ok = 0,

        /// <summary>与相邻峰间距小于最小间距，已合并（保留较强者）。</summary>
        Merged = 1,

        /// <summary>旁瓣/伪峰：窗口内存在显著更高的峰，不参与计数，但仍在表中标注。</summary>
        Sidelobe = 2
    }

    /// <summary>峰 + 处理状态。</summary>
    public sealed class PeakEvent
    {
        public CurvePeak Peak;
        public PeakEventStatus Status;
    }

    /// <summary>
    /// 峰事件后处理（商用设备通用做法，两种设备共用）：
    /// 1) 旁瓣抑制：±window 内存在高出 dropDb 的峰 → 判为旁瓣；
    /// 2) 最小间距合并：间距 &lt; minSeparation 的相邻峰合并，保留较强者并标记 Merged。
    /// 旁瓣不删除，保留在结果里供表中标注（可追溯）。
    /// </summary>
    public static class PeakEventFilter
    {
        public static List<PeakEvent> Apply(IList<CurvePeak> peaks, double pitchM,
            double minSeparationM, double sidelobeWindowM, double sidelobeDropDb)
        {
            var result = new List<PeakEvent>();
            if (peaks == null || peaks.Count == 0) return result;
            if (pitchM <= 0) pitchM = CurveMapping.DefaultPitchM;

            int windowRows = sidelobeWindowM > 0
                ? (int)Math.Round(sidelobeWindowM / pitchM)
                : 100;

            // 1) 标旁瓣：与任意更高峰的距离在窗口内且高出 dropDb
            var candidates = new List<CurvePeak>();
            var sidelobes = new List<CurvePeak>();
            for (int i = 0; i < peaks.Count; i++)
            {
                CurvePeak current = peaks[i];
                bool isSidelobe = false;
                for (int j = 0; j < peaks.Count; j++)
                {
                    if (i == j) continue;
                    if (Math.Abs(current.Centroid - peaks[j].Centroid) <= windowRows
                        && peaks[j].Db > current.Db + sidelobeDropDb)
                    {
                        isSidelobe = true;
                        break;
                    }
                }
                if (isSidelobe) sidelobes.Add(current);
                else candidates.Add(current);
            }

            // 全部被判旁瓣时保留原列表，避免输出空表
            if (candidates.Count == 0)
            {
                candidates.AddRange(peaks);
                sidelobes.Clear();
            }

            // 2) 最小间距合并（按位置升序，保留较强者）
            candidates.Sort((a, b) => a.Centroid.CompareTo(b.Centroid));
            var merged = new List<PeakEvent>();
            for (int i = 0; i < candidates.Count; i++)
            {
                CurvePeak peak = candidates[i];
                if (merged.Count == 0)
                {
                    merged.Add(new PeakEvent { Peak = peak, Status = PeakEventStatus.Ok });
                    continue;
                }

                PeakEvent last = merged[merged.Count - 1];
                double gapM = (peak.Centroid - last.Peak.Centroid) * pitchM;
                if (gapM < minSeparationM)
                {
                    if (peak.Db > last.Peak.Db) last.Peak = peak;
                    last.Status = PeakEventStatus.Merged;
                }
                else
                {
                    merged.Add(new PeakEvent { Peak = peak, Status = PeakEventStatus.Ok });
                }
            }

            // 3) 合并结果 + 旁瓣按位置排序输出
            result.AddRange(merged);
            foreach (CurvePeak sidelobe in sidelobes)
            {
                result.Add(new PeakEvent { Peak = sidelobe, Status = PeakEventStatus.Sidelobe });
            }
            result.Sort((a, b) => a.Peak.Centroid.CompareTo(b.Peak.Centroid));
            return result;
        }
    }
}
