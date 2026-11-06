using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

using Gmix.App.Export;
using Gmix.App.Orchestration;

namespace Gmix.App.UI
{
    /// <summary>事件表与结果页：完整事件表 + 指标 + 单组导出（SRS FR-HIS-03）。</summary>
    public sealed class ResultsPage : UserControl
    {
        readonly SnapshotView _view = new SnapshotView();
        readonly Label _hint = new Label();

        public ResultsPage()
        {
            Dock = DockStyle.Fill;
            BuildLayout();
        }

        void BuildLayout()
        {
            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = 40;
            bar.Padding = new Padding(6);

            var exportButton = new Button();
            exportButton.Text = "导出事件表与指标 CSV";
            exportButton.Width = 190;
            exportButton.Click += OnExport;
            bar.Controls.Add(exportButton);

            _hint.AutoSize = true;
            _hint.Padding = new Padding(8);
            _hint.ForeColor = Color.DimGray;
            _hint.Text = "导出数据字段原值（正刻度），不含显示偏移";
            bar.Controls.Add(_hint);

            Controls.Add(_view);
            Controls.Add(bar);
        }

        public void ShowSnapshot(MeasurementSnapshot snapshot)
        {
            _view.ShowSnapshot(snapshot);
        }

        void OnExport(object sender, EventArgs e)
        {
            MeasurementSnapshot snapshot = _view.Snapshot;
            if (snapshot == null || snapshot.Events == null)
            {
                MessageBox.Show("当前没有可导出的结果", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "CSV 文件|*.csv";
                dialog.FileName = (snapshot.GroupId ?? "GMIX") + "_events.csv";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    CsvExporter.ExportEvents(dialog.FileName, snapshot.Events);
                    string summaryPath = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(dialog.FileName) + "_summary.csv");
                    CsvExporter.ExportSummary(summaryPath, snapshot.Summary);
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
