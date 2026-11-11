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
            _showZeroPeak.CheckedChanged += delegate
            {
                _curve.SetZeroPeakVisible(_showZeroPeak.Checked);
                FillEvents(Snapshot);
            };

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
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
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
            grid.Columns[0].FillWeight = 34;
            grid.Columns[1].FillWeight = 66;
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
            AddColumn(grid, "peakNo", "峰号", 12);
            AddColumn(grid, "distanceM", "距离(m)", 24);
            AddColumn(grid, "rlDb", "回损(dB)", 30);
            AddColumn(grid, "stepLossDb", "台阶(dB)", 24);
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
                : string.Format("组号 {0} · 扫描号 {1} · 状态 {2} · {3} · {4}{5}",
                    snapshot.GroupId ?? "—", snapshot.ScanId ?? "—", GroupStatusText.Describe(snapshot.Status),
                    snapshot.IsAverage ? "平均结果" : "单次", snapshot.Calibrated ? "已标定" : "未标定",
                    snapshot.PsDbConvertedFromLinear ? " · 历史组：曲线由线性 ps 换算" : "");
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
            else if (summary.totalIlRawDb.HasValue) il = summary.totalIlRawDb.Value.ToString("0.00") + " dB（未标定）";
            else il = "—";

            _metrics.Rows.Add("插入损耗", il);
            _metrics.Rows.Add("插损精度", PrecisionText(summary.ilPrecisionDb, snapshot));
            _metrics.Rows.Add("回波损耗", DisplayTransform.TotalRlText(summary.totalRlDb, summary.totalRlRawDb, snapshot.OffsetK, calibrated));
            _metrics.Rows.Add("回损精度", PrecisionText(summary.rlPrecisionDb, snapshot));

            AnalysisRangeInfo range = snapshot.Range;
            _metrics.Rows.Add("平均次数", summary.averageCount.ToString());
            _metrics.Rows.Add("分析区间", range == null ? "—" : string.Format("{0:0.###} ~ {1:0.###} m，保护带 {2:0.###} m",
                range.startM, range.endM, range.guardM > 0 ? range.guardM : AcquisitionDefaults.GuardM));
            _metrics.Rows.Add("回损模式", summary.rlMode == "baseline_plus_peak" ? "基线+峰值" : "仅峰值");
            _metrics.Rows.Add("校准状态", calibrated ? "已标定" : "未标定（回损为预览值）");
        }

        /// <summary>
        /// 精度只在提交生成的 AVG 上计算；N≥2 但尚未提交时显示「提交后计算」，
        /// 避免与 N=1 的「—」混淆。
        /// </summary>
        static string PrecisionText(double? value, MeasurementSnapshot snapshot)
        {
            if (value.HasValue) return DisplayTransform.PrecisionText(value);
            if (!snapshot.IsAverage && snapshot.AverageCount > 1) return "提交后计算";
            return "—";
        }

        void FillEvents(MeasurementSnapshot snapshot)
        {
            _events.Rows.Clear();
            if (snapshot == null || snapshot.Events == null || snapshot.Events.events == null) return;

            EventInfo[] events = snapshot.Events.events;
            PeakInfo[] peaks = snapshot.Events.peaks;
            int displayNo = 0;
            for (int i = 0; i < events.Length; i++)
            {
                EventInfo item = events[i];
                PeakInfo peak = peaks != null && i < peaks.Length ? peaks[i] : null;
                bool isZero = (item.zeroPeak.HasValue && item.zeroPeak.Value)
                    || (snapshot.Range != null && snapshot.Range.startM > 0 && item.locM < snapshot.Range.startM);
                if (!isZero) displayNo++;
                if (isZero && !_showZeroPeak.Checked) continue;
                int index = _events.Rows.Add(
                    isZero ? "—" : displayNo.ToString(),
                    (peak != null && peak.distanceM.HasValue ? peak.distanceM.Value : item.locM).ToString("0.000"),
                    RlText(item, snapshot),
                    DisplayTransform.StepLossText(item.stepLossDb));
                _events.Rows[index].Tag = isZero ? null : (int?)displayNo;
            }

            if (_events.Rows.Count > 0)
            {
                _events.SelectionChanged -= OnEventSelectionChanged;
                _events.SelectionChanged += OnEventSelectionChanged;
            }
        }

        /// <summary>
        /// 逐峰回损：标定后直接取数据字段 rlDb；未标定（或旧数据缺失）回退 rlDbRel + 显示偏移，
        /// 未标定时加「（未标定）」标注（SRS DS-02）。
        /// </summary>
        static string RlText(EventInfo item, MeasurementSnapshot snapshot)
        {
            if (item.rlDb.HasValue) return item.rlDb.Value.ToString("0.00");
            if (!item.rlDbRel.HasValue) return "—";
            string text = DisplayTransform.Level(item.rlDbRel.Value, snapshot.OffsetK).ToString("0.00");
            return snapshot.Calibrated ? text : text + "（未标定）";
        }

        void OnEventSelectionChanged(object sender, EventArgs e)
        {
            if (_events.SelectedRows.Count == 0) return;
            object tag = _events.SelectedRows[0].Tag;
            _curve.SetSelectedPeak(tag as int?);
        }
    }
}
