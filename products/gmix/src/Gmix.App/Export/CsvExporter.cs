using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Gmix.Data;

namespace Gmix.App.Export
{
    /// <summary>CSV 导出：列序固定，导出数据字段原值（正刻度），不得导出显示值（SRS FR-HIS-03）。</summary>
    public static class CsvExporter
    {
        public static void ExportEvents(string path, EventsDocument events)
        {
            var sb = new StringBuilder();
            sb.AppendLine("peakNo,distanceM,rlDb,rlDbRel,stepLossDb,baseBeforeDbRel,baseAfterDbRel,zeroPeak");
            if (events != null && events.events != null)
            {
                for (int i = 0; i < events.events.Length; i++)
                {
                    EventInfo item = events.events[i];
                    PeakInfo peak = events.peaks != null && i < events.peaks.Length ? events.peaks[i] : null;
                    sb.Append(Num(peak == null ? (int?)null : peak.peakNo));
                    sb.Append(',').Append(Num(peak != null && peak.distanceM.HasValue ? peak.distanceM : item.locM));
                    sb.Append(',').Append(Num(item.rlDb));
                    sb.Append(',').Append(Num(item.rlDbRel));
                    sb.Append(',').Append(Num(item.stepLossDb));
                    sb.Append(',').Append(Num(item.baseBeforeDbRel));
                    sb.Append(',').Append(Num(item.baseAfterDbRel));
                    sb.Append(',').Append(item.zeroPeak.HasValue ? (item.zeroPeak.Value ? "true" : "false") : string.Empty);
                    sb.AppendLine();
                }
            }
            Write(path, sb);
        }

        public static void ExportSummary(string path, SummaryDocument summary)
        {
            var sb = new StringBuilder();
            sb.AppendLine("field,value");
            if (summary != null)
            {
                Row(sb, "scanId", summary.scanId);
                Row(sb, "totalIlRawDb", Num(summary.totalIlRawDb));
                Row(sb, "totalIlDb", Num(summary.totalIlDb));
                Row(sb, "totalRlRawDb", Num(summary.totalRlRawDb));
                Row(sb, "totalRlDb", Num(summary.totalRlDb));
                Row(sb, "ilPrecisionDb", Num(summary.ilPrecisionDb));
                Row(sb, "rlPrecisionDb", Num(summary.rlPrecisionDb));
                Row(sb, "precisionBasis", summary.precisionBasis);
                Row(sb, "averageCount", summary.averageCount.ToString(CultureInfo.InvariantCulture));
                Row(sb, "rlMode", summary.rlMode);
            }
            Write(path, sb);
        }

        static void Row(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append(',').Append(value ?? string.Empty).AppendLine();
        }

        static string Num(double? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
        }

        static string Num(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        static void Write(string path, StringBuilder sb)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
