using System;

using Gmix.Data;

namespace Gmix.Vendor
{
    /// <summary>
    /// 厂商检测结果 → 平台数据模型（ICD Part A §4）。
    /// 采集进程与编排进程共用本映射，避免两处各写一份。
    /// </summary>
    public static class VendorMapper
    {
        public static EventsDocument ToEventsDocument(Detector.Output detect, string scanId, string schemaVersion)
        {
            return ToEventsDocument(detect, scanId, schemaVersion, 0.0);
        }

        /// <summary>
        /// 映射检测结果。分析起点之前的峰按零距/伪峰处理（`zeroPeak=true`，不编号），
        /// 其余峰按显示顺序从 1 编号；detect 本身不提供零距峰标志（TBD-D04）。
        /// </summary>
        public static EventsDocument ToEventsDocument(Detector.Output detect, string scanId, string schemaVersion,
            double analysisStartM)
        {
            if (detect == null) throw new ArgumentNullException(nameof(detect));

            var peaks = new PeakInfo[detect.Peaks.Length];
            int peakNo = 0;
            int zeroCount = 0;
            for (int i = 0; i < peaks.Length; i++)
            {
                Detector.Peak peak = detect.Peaks[i];
                bool zero = analysisStartM > 0 && peak.LocM < analysisStartM;
                if (zero) zeroCount++;
                peaks[i] = new PeakInfo
                {
                    locM = peak.LocM,
                    heightDbRel = peak.HeightDb,
                    baseDbRel = peak.BaseDb,
                    prominenceDb = peak.ProminenceDb,
                    peakNo = zero ? (int?)null : ++peakNo,
                    distanceM = peak.LocM,
                    zeroPeak = zero
                };
            }

            var segments = new SegmentInfo[detect.Segments.Length];
            for (int i = 0; i < segments.Length; i++)
            {
                segments[i] = new SegmentInfo
                {
                    startIndex = (long)detect.Segments[i].StartIndex,
                    endIndex = (long)detect.Segments[i].EndIndex,
                    noiseFloorDbRel = detect.Segments[i].NoiseFloorDb
                };
            }

            var events = new EventInfo[detect.Events.Length];
            for (int i = 0; i < events.Length; i++)
            {
                Detector.Event src = detect.Events[i];
                events[i] = new EventInfo
                {
                    locM = src.LocM,
                    peakDbRel = src.PeakDb,
                    ilDb = src.IlDb,
                    rlDbRel = src.RlDb,
                    rlDb = null,
                    baseBeforeDbRel = src.BaseBeforeDb,
                    baseAfterDbRel = src.BaseAfterDb,
                    stepLossDb = src.IlDb.HasValue ? -src.IlDb.Value : (double?)null,
                    zeroPeak = peaks[i].zeroPeak
                };
            }

            int baselineLength = detect.BaselineDb == null ? 0 : detect.BaselineDb.Length;
            int psDbLength = detect.PsDb == null ? 0 : detect.PsDb.Length;
            return new EventsDocument
            {
                schemaVersion = schemaVersion,
                scanId = scanId,
                detector = new DetectorInfo { name = "detect.dll" },
                config = new DetectConfigInfo(),
                counts = new EventCountsInfo
                {
                    peaks = peaks.Length,
                    segments = segments.Length,
                    events = events.Length
                },
                peaks = peaks,
                segments = segments,
                events = events,
                zeroPeakIncluded = zeroCount > 0,
                units = new UnitsInfo { level = "dB_rel", distance = "m" },
                display = new DisplayInfo { peakCount = peaks.Length - zeroCount, zeroPeakExcluded = zeroCount > 0 },
                arrays = new EventsArraysInfo
                {
                    baseline = new ArrayInfo
                    {
                        file = "baseline.f64",
                        dtype = "float64_le",
                        count = baselineLength,
                        unit = "dB_rel"
                    },
                    psDb = new ArrayInfo
                    {
                        file = "ps_db.f64",
                        dtype = "float64_le",
                        count = psDbLength,
                        unit = "dB_rel"
                    }
                }
            };
        }
    }
}
