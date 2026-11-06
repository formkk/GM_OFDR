using System;
using System.Drawing;
using System.Windows.Forms;

using Gmix.App.Orchestration;
using Gmix.Data;

namespace Gmix.App.UI
{
    /// <summary>测量页与历史页共用的只读视图：曲线 + 指标表 + 事件表（SRS FR-TBL / FR-CHT）。</summary>
    public sealed class SnapshotView : UserControl
    {
        readonly CurveCanvas _curve = new CurveCanvas();
        readonly DataGridView _metrics = new DataGridView();
        readonly DataGridView _events = new DataGridView();
        readonly Label _title = new Label();
        readonly CheckBox _showZeroPeak = new CheckBox();

        public MeasurementSnapshot Snapshot { get; private set; }

        public SnapshotView()
        {
            Dock = DockStyle.Fill;
            BuildLayout();
        }

        void BuildLayout()
        {
            _title.Dock = DockStyle.Top;
            _title.Height = 22;
            _title.TextAlign = ContentAlignment.MiddleLeft;
            _title.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            _title.Text = "当前结果：—";

            var right = new TableLayoutPanel();
            right.Dock = DockStyle.Fill;
            right.ColumnCount = 1;
            right.RowCount = 3;
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _showZeroPeak.Text = "显示零距峰";
            _showZeroPeak.AutoSize = true;
            _showZeroPeak.CheckedChanged += delegate { _curve.SetZeroPeakVisible(_showZeroPeak.Checked); };

            ConfigureMetricsGrid(_metrics);
            ConfigureEventsGrid(_events);

            right.Controls.Add(_showZeroPeak, 0, 0);
            right.Controls.Add(_metrics, 0, 1);
            right.Controls.Add(_events, 0, 2);

            _curve.Dock = DockStyle.Fill;

            var split = new TableLayoutPanel();
            split.Dock = DockStyle.Fill;
            split.ColumnCount = 2;
            split.RowCount = 1;
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            split.Controls.Add(_curve, 0, 0);
            split.Controls.Add(right, 1, 0);

            Controls.Add(split);
            Controls.Add(_title);
        }

        static void ConfigureMetricsGrid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add("name", "指标");
            grid.Columns.Add("value", "数值");
            grid.Columns[0].FillWeight = 40;
            grid.Columns[1].FillWeight = 60;
        }

        static void ConfigureEventsGrid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            AddColumn(grid, "peakNo", "峰号", 10);
            AddColumn(grid, "distanceM", "距离(m)", 16);
            AddColumn(grid, "rlDb", "回损(dB)", 14);
            AddColumn(grid, "rlDbRel", "相对回损(dB)", 16);
            AddColumn(grid, "stepLossDb", "台阶(dB)", 13);
            AddColumn(grid, "baseBefore", "前基线(dB)", 14);
            AddColumn(grid, "baseAfter", "后基线(dB)", 14);
            AddColumn(grid, "zeroPeak", "零距", 8);
        }

        static void AddColumn(DataGridView grid, string name, string header, int weight)
        {
            grid.Columns.Add(name, header);
            grid.Columns[name].FillWeight = weight;
        }

        public void ShowSnapshot(MeasurementSnapshot snapshot)
        {
            Snapshot = snapshot;
            _curve.ShowSnapshot(snapshot);

            string text = snapshot == null
                ? "当前结果：—"
                : string.Format("组 {0} · 扫描 {1} · 状态 {2} · {3} · {4}",
                    snapshot.GroupId ?? "—", snapshot.ScanId ?? "—", snapshot.Status ?? "—",
                    snapshot.IsAverage ? "平均结果" : "单次", snapshot.Calibrated ? "已标定" : "未标定");
            _title.Text = text;

            FillMetrics(snapshot);
            FillEvents(snapshot);
        }

        void FillMetrics(MeasurementSnapshot snapshot)
        {
            _metrics.Rows.Clear();
            SummaryDocument summary = snapshot == null ? null : snapshot.Summary;
            if (summary == null)
            {
                _metrics.Rows.Add("插入损耗", "—");
                _metrics.Rows.Add("插损精度", "—");
                _metrics.Rows.Add("回波损耗", "—");
                _metrics.Rows.Add("回损精度", "—");
                _metrics.Rows.Add("平均次数", "—");
                _metrics.Rows.Add("分析区间", "—");
                _metrics.Rows.Add("回损模式", "—");
                _metrics.Rows.Add("校准状态", snapshot != null && snapshot.Calibrated ? "已标定" : "未标定");
                return;
            }

            bool calibrated = snapshot.Calibrated;
            string il;
            if (summary.totalIlDb.HasValue) il = DisplayTransform.TotalIlText(summary.totalIlDb);
            else if (summary.totalIlRawDb.HasValue) il = (-summary.totalIlRawDb.Value).ToString("0.00") + "（未标定）";
            else il = "—";

            _metrics.Rows.Add("插入损耗", il);
            _metrics.Rows.Add("插损精度", DisplayTransform.PrecisionText(summary.ilPrecisionDb));
            _metrics.Rows.Add("回波损耗", DisplayTransform.TotalRlText(summary.totalRlDb, summary.totalRlRawDb, snapshot.OffsetK, calibrated));
            _metrics.Rows.Add("回损精度", DisplayTransform.PrecisionText(summary.rlPrecisionDb));

            AnalysisRangeInfo range = snapshot.Range;
            _metrics.Rows.Add("平均次数", summary.averageCount.ToString());
            _metrics.Rows.Add("分析区间", range == null ? "—" : string.Format("{0:0.###} ~ {1:0.###} m，保护带 {2:0.###} m",
                range.startM, range.endM, range.guardM > 0 ? range.guardM : 0.1));
            _metrics.Rows.Add("回损模式", summary.rlMode == "baseline_plus_peak" ? "基线+峰值" : "仅峰值");
            _metrics.Rows.Add("校准状态", calibrated ? "已标定" : "未标定（显示偏移 " + DisplayTransform.DefaultOffsetK.ToString("0") + "）");
        }

        void FillEvents(MeasurementSnapshot snapshot)
        {
            _events.Rows.Clear();
            if (snapshot == null || snapshot.Events == null || snapshot.Events.events == null) return;

            EventInfo[] events = snapshot.Events.events;
            PeakInfo[] peaks = snapshot.Events.peaks;
            for (int i = 0; i < events.Length; i++)
            {
                EventInfo item = events[i];
                PeakInfo peak = peaks != null && i < peaks.Length ? peaks[i] : null;
                bool isZero = item.zeroPeak.HasValue && item.zeroPeak.Value;
                int index = _events.Rows.Add(
                    peak != null && peak.peakNo.HasValue ? peak.peakNo.Value.ToString() : "—",
                    (peak != null && peak.distanceM.HasValue ? peak.distanceM.Value : item.locM).ToString("0.000"),
                    item.rlDb.HasValue ? item.rlDb.Value.ToString("0.00") : "—",
                    item.rlDbRel.HasValue ? DisplayTransform.Level(item.rlDbRel.Value, snapshot.OffsetK).ToString("0.00") : "—",
                    DisplayTransform.StepLossText(item.stepLossDb),
                    item.baseBeforeDbRel.HasValue ? DisplayTransform.Level(item.baseBeforeDbRel.Value, snapshot.OffsetK).ToString("0.00") : "—",
                    item.baseAfterDbRel.HasValue ? DisplayTransform.Level(item.baseAfterDbRel.Value, snapshot.OffsetK).ToString("0.00") : "—",
                    isZero ? "是" : "否");
                _events.Rows[index].Tag = peak != null ? peak.peakNo : null;
            }

            if (_events.Rows.Count > 0)
            {
                _events.SelectionChanged -= OnEventSelectionChanged;
                _events.SelectionChanged += OnEventSelectionChanged;
            }
        }

        void OnEventSelectionChanged(object sender, EventArgs e)
        {
            if (_events.SelectedRows.Count == 0) return;
            object tag = _events.SelectedRows[0].Tag;
            _curve.SetSelectedPeak(tag as int?);
        }
    }
}
