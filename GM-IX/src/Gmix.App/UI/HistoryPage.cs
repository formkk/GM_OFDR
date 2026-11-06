using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

using Gmix.App.Export;
using Gmix.App.Orchestration;
using Gmix.Data;

namespace Gmix.App.UI
{
    /// <summary>历史检索与导出：组列表 + 回读 + CSV 导出（SRS FR-HIS-01~03/05）。</summary>
    public sealed class HistoryPage : UserControl
    {
        readonly AppEnvironment _env;
        readonly DataGridView _grid = new DataGridView();
        readonly TextBox _search = new TextBox();

        public event Action<string> GroupOpened;

        public HistoryPage(AppEnvironment env)
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

            bar.Controls.Add(new Label { Text = "组 ID 搜索", AutoSize = true, Padding = new Padding(0, 8, 6, 0) });
            _search.Width = 200;
            _search.TextChanged += delegate { RefreshList(); };
            bar.Controls.Add(_search);

            var refresh = new Button();
            refresh.Text = "刷新";
            refresh.Width = 72;
            refresh.Click += delegate { RefreshList(); };
            bar.Controls.Add(refresh);

            var open = new Button();
            open.Text = "打开";
            open.Width = 72;
            open.Click += delegate { OpenSelected(); };
            bar.Controls.Add(open);

            var export = new Button();
            export.Text = "导出 CSV";
            export.Width = 100;
            export.Click += delegate { ExportSelected(); };
            bar.Controls.Add(export);

            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.Columns.Add("groupId", "组 ID");
            _grid.Columns.Add("createdAtUtc", "采集时间 (UTC)");
            _grid.Columns.Add("status", "状态");
            _grid.Columns.Add("valid", "有效/计划");
            _grid.Columns[0].FillWeight = 30;
            _grid.Columns[1].FillWeight = 34;
            _grid.Columns[2].FillWeight = 16;
            _grid.Columns[3].FillWeight = 20;
            _grid.CellDoubleClick += delegate { OpenSelected(); };

            Controls.Add(_grid);
            Controls.Add(bar);
        }

        public void RefreshList()
        {
            string filter = _search.Text.Trim();
            List<GroupSummary> groups = GroupReader.ListGroups(_env.DataRoot);
            _grid.Rows.Clear();
            foreach (GroupSummary group in groups)
            {
                if (filter.Length > 0 && group.GroupId.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int index = _grid.Rows.Add(group.GroupId, group.CreatedAtUtc, group.Status,
                    group.ValidCount + "/" + group.AverageCount);
                _grid.Rows[index].Tag = group.Dir;
            }
        }

        void OpenSelected()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先选择一个组", "历史", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string dir = _grid.SelectedRows[0].Tag as string;
            if (string.IsNullOrEmpty(dir)) return;
            Action<string> handler = GroupOpened;
            if (handler != null) handler(dir);
        }

        void ExportSelected()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先选择一个组", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string dir = _grid.SelectedRows[0].Tag as string;
            if (string.IsNullOrEmpty(dir)) return;

            try
            {
                MeasurementSnapshot snapshot = GroupReader.LoadGroup(dir);
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Filter = "CSV 文件|*.csv";
                    dialog.FileName = snapshot.GroupId + "_events.csv";
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;

                    CsvExporter.ExportEvents(dialog.FileName, snapshot.Events);
                    string summaryPath = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(dialog.FileName) + "_summary.csv");
                    CsvExporter.ExportSummary(summaryPath, snapshot.Summary);
                    MessageBox.Show("已导出：\r\n" + dialog.FileName + "\r\n" + summaryPath, "导出",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "导出", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
