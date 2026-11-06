using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

using Gmix.App.Export;
using Gmix.App.Orchestration;

namespace Gmix.App.UI
{
    /// <summary>
    /// 结果与历史（合并页）：左侧组列表（检索/刷新），右侧曲线 + 指标 + 事件，导出共用。
    /// 实时测量结果与历史组共用同一 SnapshotView（SRS FR-TBL / FR-HIS）。
    /// </summary>
    public sealed class ResultsHistoryPage : UserControl
    {
        readonly AppEnvironment _env;
        readonly SnapshotView _view = new SnapshotView();
        readonly DataGridView _grid = new DataGridView();
        readonly TextBox _search = new TextBox();
        readonly Label _status = new Label();

        MeasurementSnapshot _snapshot;

        public ResultsHistoryPage(AppEnvironment env)
        {
            _env = env;
            Dock = DockStyle.Fill;
            BuildLayout();
            RefreshList();
        }

        void BuildLayout()
        {
            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = 40;
            bar.Padding = new Padding(6);

            bar.Controls.Add(new Label { Text = "组号搜索", AutoSize = true, Padding = new Padding(0, 8, 6, 0) });
            _search.Width = 180;
            _search.TextChanged += delegate { RefreshList(); };
            bar.Controls.Add(_search);

            var refresh = new Button();
            refresh.Text = "刷新";
            refresh.Width = 64;
            refresh.Click += delegate { RefreshList(); };
            bar.Controls.Add(refresh);

            var open = new Button();
            open.Text = "打开";
            open.Width = 64;
            open.Click += delegate { LoadSelected(); };
            bar.Controls.Add(open);

            var export = new Button();
            export.Text = "导出 CSV";
            export.Width = 92;
            export.Click += delegate { ExportCurrent(); };
            bar.Controls.Add(export);

            _status.AutoSize = true;
            _status.ForeColor = Color.DimGray;
            _status.Padding = new Padding(10, 9, 0, 0);
            _status.Text = "双击组或点『打开』加载历史；导出对应当前显示结果";
            bar.Controls.Add(_status);

            ConfigureGrid();
            _grid.Dock = DockStyle.Fill;
            _grid.CellDoubleClick += delegate { LoadSelected(); };

            var left = new Panel();
            left.Dock = DockStyle.Fill;
            left.Padding = new Padding(0, 0, 6, 0);
            left.Controls.Add(_grid);

            var split = new TableLayoutPanel();
            split.Dock = DockStyle.Fill;
            split.ColumnCount = 2;
            split.RowCount = 1;
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            split.Controls.Add(left, 0, 0);
            split.Controls.Add(_view, 1, 0);

            Controls.Add(split);
            Controls.Add(bar);
        }

        void ConfigureGrid()
        {
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.Columns.Add("groupId", "组号");
            _grid.Columns.Add("createdAt", "时间");
            _grid.Columns.Add("status", "状态");
            _grid.Columns.Add("valid", "有效/总数");
            _grid.Columns[0].FillWeight = 44;
            _grid.Columns[1].FillWeight = 26;
            _grid.Columns[2].FillWeight = 17;
            _grid.Columns[3].FillWeight = 13;
        }

        /// <summary>显示一次测量或历史组结果（不影响左侧列表选择）。</summary>
        public void ShowSnapshot(MeasurementSnapshot snapshot)
        {
            _snapshot = snapshot;
            _view.ShowSnapshot(snapshot);
        }

        public void RefreshList()
        {
            string filter = _search.Text.Trim();
            List<GroupSummary> groups = GroupReader.ListGroups(_env.DataRoot);
            _grid.Rows.Clear();
            foreach (GroupSummary group in groups)
            {
                if (filter.Length > 0 && group.GroupId.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int index = _grid.Rows.Add(group.GroupId, FormatTime(group.CreatedAtUtc),
                    GroupStatusText.Describe(group.Status),
                    group.ValidCount + "/" + group.AverageCount);
                _grid.Rows[index].Tag = group.Dir;
            }
            _status.Text = "共 " + _grid.Rows.Count + " 个组；双击组或点『打开』加载历史；导出对应当前显示结果";
        }

        /// <summary>UTC ISO 时间 → 本地「MM-dd HH:mm」，避免列表列宽被长字符串撑爆。</summary>
        static string FormatTime(string utc)
        {
            if (string.IsNullOrEmpty(utc)) return "—";
            DateTime value;
            if (DateTime.TryParse(utc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
                return value.ToLocalTime().ToString("MM-dd HH:mm");
            return utc.Length > 16 ? utc.Substring(0, 16) : utc;
        }

        void LoadSelected()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先选择一个组", "历史", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string dir = _grid.SelectedRows[0].Tag as string;
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                ShowSnapshot(GroupReader.LoadGroup(dir));
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开历史组失败：" + ex.Message, "历史", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void ExportCurrent()
        {
            if (_snapshot == null || _snapshot.Events == null)
            {
                MessageBox.Show("当前没有可导出的结果", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "CSV 文件|*.csv";
                dialog.FileName = (_snapshot.GroupId ?? "GMIX") + "_events.csv";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    CsvExporter.ExportEvents(dialog.FileName, _snapshot.Events);
                    string summaryPath = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(dialog.FileName) + "_summary.csv");
                    CsvExporter.ExportSummary(summaryPath, _snapshot.Summary);
                    MessageBox.Show("已导出：\r\n" + dialog.FileName + "\r\n" + summaryPath, "导出",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("导出失败：" + ex.Message, "导出", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
