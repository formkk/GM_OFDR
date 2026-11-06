using System;
using System.Drawing;
using System.Windows.Forms;

using Gmix.App.Orchestration;
using Gmix.App.UI;
using Gmix.Data;

namespace Gmix.App
{
    /// <summary>主窗体：4 页承载 + 状态栏 + 启动自检（SDD §5.4 / §11.3）。</summary>
    public sealed class MainForm : Form
    {
        const string AppVersion = "0.1.0";

        readonly AppEnvironment _env;
        readonly MeasurementPage _measurement;
        readonly ResultsPage _results;
        readonly CalibrationPage _calibration;
        readonly HistoryPage _history;
        TabControl _tabs;

        readonly ToolStripStatusLabel _connection = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel _device = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel _version = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel _calibrationState = new ToolStripStatusLabel();

        public MainForm(AppEnvironment env)
        {
            _env = env;
            Text = "GM-IX 上位机 v" + AppVersion + " — " + env.DataRoot;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1280, 720);
            Size = new Size(1360, 800);

            _measurement = new MeasurementPage(env);
            _results = new ResultsPage();
            _calibration = new CalibrationPage(env);
            _history = new HistoryPage(env);

            BuildLayout();
            WireEvents();
            RunStartupChecks();
        }

        void BuildLayout()
        {
            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            _tabs.TabPages.Add(MakePage("测量主页", _measurement));
            _tabs.TabPages.Add(MakePage("事件表与结果", _results));
            _tabs.TabPages.Add(MakePage("校准向导", _calibration));
            _tabs.TabPages.Add(MakePage("历史检索", _history));
            var tabs = _tabs;

            var status = new StatusStrip();
            _connection.Text = "未连接";
            _device.Text = "设备 192.168.5.20";
            _version.Text = "v" + AppVersion;
            _calibrationState.Text = "校准：未标定";
            status.Items.Add(_connection);
            status.Items.Add(new ToolStripSeparator());
            status.Items.Add(_device);
            status.Items.Add(new ToolStripSeparator());
            status.Items.Add(_version);
            status.Items.Add(new ToolStripSeparator());
            status.Items.Add(_calibrationState);

            Controls.Add(tabs);
            Controls.Add(status);
        }

        static TabPage MakePage(string title, Control content)
        {
            var page = new TabPage(title);
            page.Controls.Add(content);
            return page;
        }

        void WireEvents()
        {
            _measurement.SnapshotChanged += delegate (MeasurementSnapshot snapshot)
            {
                _results.ShowSnapshot(snapshot);
            };

            _calibration.CalibrationSaved += delegate (CalibrationModel model, string path)
            {
                _measurement.SetCalibration(model, path);
                _measurement.AppendLog("校准已保存：" + model.calibrationId + " → " + path);
                UpdateCalibrationState(model);
            };

            _history.GroupOpened += delegate (string groupDir)
            {
                try
                {
                    MeasurementSnapshot snapshot = GroupReader.LoadGroup(groupDir);
                    _results.ShowSnapshot(snapshot);
                    _tabs.SelectedIndex = 1; // 双击历史组后直接切到结果页
                    _measurement.AppendLog("已打开历史组：" + snapshot.GroupId);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("打开历史组失败：" + ex.Message, "历史", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
        }

        void RunStartupChecks()
        {
            _version.Text = "v" + AppVersion;
            _connection.Text = _env.DeviceReady ? "设备就绪" : "设备未就绪";
            _device.Text = "设备 192.168.5.20";

            if (!_env.DeviceReady)
                _measurement.AppendLog("自检：未找到 qt_acq/ofdr/detect，实机采集不可用");

            if (string.IsNullOrEmpty(_env.AcquirerExe) || !System.IO.File.Exists(_env.AcquirerExe))
                _measurement.AppendLog("自检：未找到 Gmix.Acquirer.exe，无法采集");
            else
                _measurement.AppendLog("自检：采集进程 " + _env.AcquirerExe);

            try
            {
                var store = new GroupStore(_env.DataRoot);
                int removed = store.CleanupStaging();
                if (removed > 0) _measurement.AppendLog("自检：清理 staging 残留 " + removed + " 个");
            }
            catch (Exception ex)
            {
                _measurement.AppendLog("自检：清理 staging 失败：" + ex.Message);
            }

            try
            {
                string calibrationPath;
                CalibrationModel latest = CalibrationStore.LoadLatest(_env.DataRoot, out calibrationPath);
                _measurement.SetCalibration(latest, calibrationPath);
                UpdateCalibrationState(latest);
                if (latest != null) _measurement.AppendLog("自检：读取校准 " + latest.calibrationId);
            }
            catch (Exception ex)
            {
                _measurement.AppendLog("自检：读取校准失败：" + ex.Message);
                UpdateCalibrationState(null);
            }

            _history.RefreshList();
        }

        void UpdateCalibrationState(CalibrationModel model)
        {
            bool calibrated = model != null && model.HasAbsoluteRl();
            _calibrationState.Text = calibrated
                ? "校准：已标定（" + model.calibrationId + "）"
                : "校准：未标定（显示偏移 " + DisplayTransform.DefaultOffsetK.ToString("0") + "）";
        }
    }
}
