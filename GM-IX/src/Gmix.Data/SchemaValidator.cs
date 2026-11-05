using System;
using System.Collections.Generic;
using System.IO;

namespace Gmix.Data
{
    /// <summary>Schema v0.2 关键规则校验。</summary>
    public static class SchemaValidator
    {
        public static List<string> ValidateAll(string scanFolder, ScanDocument scan,
            OfdrDocument ofdr, EventsDocument events, SummaryDocument summary)
        {
            var errors = new List<string>();
            if (scan == null) { errors.Add("scan.json 为空"); return errors; }
            if (ofdr == null) errors.Add("ofdr.json 为空");
            if (events == null) errors.Add("events.json 为空");

            if (scan.schemaVersion != "0.2" && scan.schemaVersion != "1.0")
                errors.Add("scan.schemaVersion 应为 0.2（兼容 1.0）");
            if (string.IsNullOrEmpty(scan.scanId)) errors.Add("scan.scanId 为空");
            if (scan.acquisition == null) errors.Add("scan.acquisition 为空");
            if (scan.data == null) errors.Add("scan.data 为空");

            if (scan.acquisition != null)
            {
                var a = scan.acquisition;
                if (a.segCount != 1) errors.Add("MVP 只允许 segCount=1");
                if (a.channelMap == null || a.channelMap.p != 0 || a.channelMap.s != 1 || a.channelMap.aux != 2)
                    errors.Add("channelMap 必须为 p=0,s=1,aux=2");
                long expectedBytes = (long)a.sampleRateMhz * a.acqTimeMs * 8000L * a.segCount;
                if (a.bufferBytes != expectedBytes)
                    errors.Add("bufferBytes 与公式不符: " + a.bufferBytes + " != " + expectedBytes);
                long expectedFrames = (long)a.sampleRateMhz * a.acqTimeMs * 1000L * a.segCount;
                if (a.expectedFrames != expectedFrames)
                    errors.Add("expectedFrames 与公式不符: " + a.expectedFrames + " != " + expectedFrames);
            }

            if (ofdr != null)
            {
                if (ofdr.config == null) errors.Add("ofdr.config 为空");
                else
                {
                    if (ofdr.config.inputLength <= 0) errors.Add("ofdr.inputLength <= 0");
                    if (ofdr.config.outputLength != ofdr.config.inputLength / 2 + 1)
                        errors.Add("outputLength 必须等于 inputLength/2+1");
                    if (ofdr.config.distScale <= 0) errors.Add("distScale 必须为正数");
                }
                if (ofdr.arrays == null) errors.Add("ofdr.arrays 为空");
            }

            if (events != null)
            {
                int np = events.peaks == null ? 0 : events.peaks.Length;
                int ns = events.segments == null ? 0 : events.segments.Length;
                int ne = events.events == null ? 0 : events.events.Length;
                if (events.counts == null) errors.Add("events.counts 为空");
                else if (events.counts.peaks != np || events.counts.segments != ns || events.counts.events != ne)
                    errors.Add("events.counts 与数组长度不符");
                if (events.units == null || events.units.level != "dB_rel")
                    errors.Add("events.units.level 必须为 dB_rel");
            }

            if (summary != null)
            {
                if (summary.analysisRange != null && summary.analysisRange.endM <= summary.analysisRange.startM)
                    errors.Add("summary 分析区间无效");
                if (summary.rlMode != "peak_only" && summary.rlMode != "baseline_plus_peak")
                    errors.Add("summary.rlMode 无效");
            }

            if (scanFolder != null && scan != null && ofdr != null && events != null)
            {
                string raw = Path.Combine(scanFolder, "raw.bin");
                if (scan.data != null && scan.data.rawFile != null)
                    raw = Path.Combine(scanFolder, scan.data.rawFile);
                if (File.Exists(raw) && scan.acquisition != null)
                {
                    try { RawStore.ValidateLength(raw, scan.acquisition.bufferBytes); }
                    catch (Exception ex) { errors.Add(ex.Message); }
                }

                if (ofdr.arrays != null && ofdr.config != null)
                {
                    CheckArray(scanFolder, ofdr.arrays.dist, ofdr.config.outputLength, errors);
                    CheckArray(scanFolder, ofdr.arrays.freq, ofdr.config.outputLength, errors);
                    CheckArray(scanFolder, ofdr.arrays.ps, ofdr.config.outputLength, errors);
                    if (events.arrays != null)
                        CheckArray(scanFolder, events.arrays.baseline, ofdr.config.outputLength, errors);
                }
            }
            return errors;
        }

        static void CheckArray(string folder, ArrayInfo info, long expectedCount, List<string> errors)
        {
            if (info == null || string.IsNullOrEmpty(info.file)) return;
            string path = Path.Combine(folder, info.file);
            if (!File.Exists(path)) { errors.Add("缺少数组文件: " + info.file); return; }
            try
            {
                var a = ArrayStore.ReadDoubles(path, expectedCount);
                if (info.count > 0 && a.Length != info.count)
                    errors.Add("数组点数与元数据不符: " + info.file);
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
    }
}
