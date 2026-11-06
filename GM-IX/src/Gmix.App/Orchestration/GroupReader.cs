using System;
using System.Collections.Generic;
using System.IO;

using Gmix.Data;

namespace Gmix.App.Orchestration
{
    /// <summary>一次测量（或一个组的平均结果）的只读视图，UI 只消费本对象。</summary>
    public sealed class MeasurementSnapshot
    {
        public string GroupId;
        public string ScanId;
        public string Status;
        public bool IsAverage;
        public string SourceDir;
        public double[] Dist;
        /// <summary>线性功率谱（ps.f64，仅作原始证据，不直接绘制）。</summary>
        public double[] Ps;
        /// <summary>dB 功率谱（ps_db.f64，检测出口 20log10 一次得到，供曲线使用）。</summary>
        public double[] PsDb;
        /// <summary>历史组缺 ps_db.f64 时由线性 ps 临时换算（只用于显示，不落盘、不参与指标）。</summary>
        public bool PsDbConvertedFromLinear;
        public double[] Baseline;
        public EventsDocument Events;
        public SummaryDocument Summary;
        public ScanDocument Scan;
        public CalibrationModel Calibration;

        public AnalysisRangeInfo Range
        {
            get
            {
                if (Scan != null && Scan.session != null && Scan.session.analysisRange != null)
                    return Scan.session.analysisRange;
                return new AnalysisRangeInfo { startM = 0.05, endM = Dist == null || Dist.Length == 0 ? 20 : Dist[Dist.Length - 1], guardM = 0.1 };
            }
        }

        public string RlMode
        {
            get { return Scan != null && Scan.session != null && !string.IsNullOrEmpty(Scan.session.rlMode) ? Scan.session.rlMode : "peak_only"; }
        }

        public int AverageCount
        {
            get { return Scan != null && Scan.session != null && Scan.session.averageCount.HasValue ? Scan.session.averageCount.Value : 1; }
        }

        public bool Calibrated
        {
            get { return Calibration != null && Calibration.HasAbsoluteRl(); }
        }

        public double OffsetK
        {
            get { return Calibrated ? Calibration.rlOffsetK.Value : DisplayTransform.DefaultOffsetK; }
        }
    }

    public sealed class GroupSummary
    {
        public string GroupId;
        public string CreatedAtUtc;
        public string Status;
        public int ValidCount;
        public int AverageCount;
        public string Dir;
    }

    /// <summary>组状态的中文显示（数据文件仍存英文枚举，只在界面翻译）。</summary>
    public static class GroupStatusText
    {
        public static string Describe(string status)
        {
            switch (status)
            {
                case "complete": return "完整";
                case "incomplete": return "部分完成";
                case "failed": return "失败";
                case "pending": return "待提交";
                default: return string.IsNullOrEmpty(status) ? "—" : status;
            }
        }
    }

    public static class GroupReader
    {
        public static MeasurementSnapshot LoadGroup(string groupDir)
        {
            string groupJson = Path.Combine(groupDir, "group.json");
            GroupDocument group = File.Exists(groupJson) ? JsonStore.Read<GroupDocument>(groupJson) : null;

            string avgDir = Path.Combine(groupDir, "AVG");
            string scanDir = Directory.Exists(avgDir) ? avgDir : FirstScanDir(groupDir);
            if (scanDir == null) throw new InvalidDataException("组内没有扫描目录：" + groupDir);

            MeasurementSnapshot snapshot = LoadScanDir(scanDir);
            snapshot.GroupId = group != null ? group.groupId : Path.GetFileName(groupDir);
            snapshot.Status = group != null ? group.status : "pending";
            snapshot.IsAverage = Directory.Exists(avgDir) && string.Equals(scanDir, avgDir, StringComparison.OrdinalIgnoreCase);
            return snapshot;
        }

        /// <summary>读取单次扫描目录（staging 预览与组内 Rxxx/AVG 通用）。</summary>
        public static MeasurementSnapshot LoadScanDir(string scanDir)
        {
            var snapshot = new MeasurementSnapshot();
            snapshot.SourceDir = scanDir;

            snapshot.Dist = ArrayStore.ReadDoubles(Path.Combine(scanDir, "dist.f64"), 0);
            snapshot.Ps = ArrayStore.ReadDoubles(Path.Combine(scanDir, "ps.f64"), 0);
            string psDbPath = Path.Combine(scanDir, "ps_db.f64");
            if (File.Exists(psDbPath))
            {
                snapshot.PsDb = ArrayStore.ReadDoubles(psDbPath, 0);
            }
            else if (snapshot.Ps != null && snapshot.Ps.Length > 0)
            {
                // 兼容 ps_db.f64 引入之前的历史组：仅用于显示，不写回、不参与指标计算
                var converted = new double[snapshot.Ps.Length];
                for (int i = 0; i < snapshot.Ps.Length; i++)
                {
                    double value = snapshot.Ps[i];
                    converted[i] = value > 0 ? 20.0 * Math.Log10(value) : double.NaN;
                }
                snapshot.PsDb = converted;
                snapshot.PsDbConvertedFromLinear = true;
            }
            string baselinePath = Path.Combine(scanDir, "baseline.f64");
            snapshot.Baseline = File.Exists(baselinePath) ? ArrayStore.ReadDoubles(baselinePath, 0) : null;

            string eventsPath = Path.Combine(scanDir, "events.json");
            if (File.Exists(eventsPath)) snapshot.Events = JsonStore.Read<EventsDocument>(eventsPath);
            string summaryPath = Path.Combine(scanDir, "summary.json");
            if (File.Exists(summaryPath)) snapshot.Summary = JsonStore.Read<SummaryDocument>(summaryPath);
            string scanPath = Path.Combine(scanDir, "scan.json");
            if (File.Exists(scanPath))
            {
                snapshot.Scan = JsonStore.Read<ScanDocument>(scanPath);
                snapshot.ScanId = snapshot.Scan.scanId;
            }

            string calibrationPath = Path.Combine(scanDir, "calibration.json");
            snapshot.Calibration = File.Exists(calibrationPath)
                ? CalibrationModel.FromDocument(JsonStore.Read<CalibrationDocument>(calibrationPath))
                : new CalibrationModel();

            if (snapshot.Dist.Length != snapshot.Ps.Length)
                throw new InvalidDataException("dist/ps 长度不一致：" + scanDir);
            return snapshot;
        }

        public static List<GroupSummary> ListGroups(string dataRoot)
        {
            var list = new List<GroupSummary>();
            if (!Directory.Exists(dataRoot)) return list;

            foreach (string dir in Directory.GetDirectories(dataRoot))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (name.EndsWith(GroupStore.FailedSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                string groupJson = Path.Combine(dir, "group.json");
                if (!File.Exists(groupJson)) continue;
                GroupDocument doc = JsonStore.Read<GroupDocument>(groupJson);
                list.Add(new GroupSummary
                {
                    GroupId = doc.groupId,
                    CreatedAtUtc = doc.createdAtUtc,
                    Status = doc.status,
                    ValidCount = doc.validCount,
                    AverageCount = doc.averageCount,
                    Dir = dir
                });
            }

            list.Sort(delegate (GroupSummary a, GroupSummary b)
            {
                return string.Compare(b.CreatedAtUtc, a.CreatedAtUtc, StringComparison.Ordinal);
            });
            return list;
        }

        static string FirstScanDir(string groupDir)
        {
            string[] dirs = Directory.GetDirectories(groupDir, "R*");
            Array.Sort(dirs, StringComparer.Ordinal);
            foreach (string dir in dirs)
            {
                if (Path.GetFileName(dir).Length != 4) continue;
                if (File.Exists(Path.Combine(dir, "scan.json"))) return dir;
            }
            return null;
        }
    }
}
