using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Gmix.App.Orchestration;
using Gmix.Data;

namespace Gmix.App.UI
{
    /// <summary>测量主页（SRS FR-ACQ-03/04/05、FR-TBL-06、SDD §8.2）。</summary>
    public sealed class MeasurementPage : UserControl
    {
        readonly AppEnvironment _env;
        readonly ScanRunner _runner;
        readonly SnapshotView _view = new SnapshotView();

        readonly NumericUpDown _repeat = new NumericUpDown();
        readonly ComboBox _runMode = new ComboBox();
        readonly TextBox _startM = new TextBox();
        readonly TextBox _endM = new TextBox();
        readonly TextBox _guardM = new TextBox();
        readonly ComboBox _rlMode = new ComboBox();
        readonly CheckBox _synthetic = new CheckBox();
        readonly Button _startButton = new Button();
        readonly Button _stopButton = new Button();
        readonly Button _saveButton = new Button();

        readonly RichTextBox _log = new RichTextBox();

        CancellationTokenSource _cts;
        bool _running;
        string _pendingGroupId;
        CalibrationModel _calibration;
        string _calibrationPath;

        public SnapshotView View { get { return _view; } }

        /// <summary>结果视图更新（测量完成、提交完成或校准重算）。</summary>
        public event Action<MeasurementSnapshot> SnapshotChanged;

        /// <summary>组提交成功，历史列表需要刷新。</summary>
        public event Action GroupCommitted;

        public MeasurementPage(AppEnvironment env)
        {
            _env = env;
            _runner = new ScanRunner(env);
            _runner.Log += OnRunnerLog;

            Dock = DockStyle.Fill;
            BuildLayout();
            AppendLog("数据根：" + env.DataRoot);
            AppendLog(env.DeviceReady ? "设备 SDK 已就绪：" + env.RuntimeDir : "未找到厂商 SDK，实机采集不可用（可用合成数据调试）");
        }

        public void SetCalibration(CalibrationModel model, string path)
        {
            _calibration = model;
            _calibrationPath = path;
            if (_view.Snapshot != null) Recompute();
        }

        void BuildLayout()
        {
            var parameters = new FlowLayoutPanel();
            parameters.Dock = DockStyle.Top;
            parameters.Height = 120;
            parameters.AutoSize = false;
            parameters.WrapContents = true;
            parameters.Padding = new Padding(6);

            parameters.Controls.Add(MakeLabel("采样率 50 MHz · 时长 8 ms · 软件触发（只读）"));
            parameters.Controls.Add(MakeLabel("组内平均次数"));
            _repeat.Minimum = 1;
            _repeat.Maximum = 100;
            _repeat.Value = 1;
            _repeat.Width = 56;
            parameters.Controls.Add(_repeat);

            parameters.Controls.Add(MakeLabel("连续采集"));
            _runMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _runMode.Items.AddRange(new object[] { "关闭", "开启" });
            _runMode.SelectedIndex = 0;
            _runMode.Width = 80;
            parameters.Controls.Add(_runMode);

            parameters.Controls.Add(MakeLabel("起点 m"));
            _startM.Text = "0.05";
            _startM.Width = 64;
            parameters.Controls.Add(_startM);

            parameters.Controls.Add(MakeLabel("终点 m"));
            _endM.Text = "20";
            _endM.Width = 64;
            parameters.Controls.Add(_endM);

            parameters.Controls.Add(MakeLabel("保护带 m"));
            _guardM.Text = "0.1";
            _guardM.Width = 48;
            _guardM.ReadOnly = true;
            _guardM.TabStop = false;
            _guardM.BackColor = SystemColors.Control;
            _guardM.TextAlign = HorizontalAlignment.Center;
            parameters.Controls.Add(_guardM);

            parameters.Controls.Add(MakeLabel("回损模式"));
            _rlMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _rlMode.Items.AddRange(new object[] { "仅峰值", "基线+峰值" });
            _rlMode.SelectedIndex = 0;
            _rlMode.Width = 150;
            parameters.Controls.Add(_rlMode);

            _synthetic.Text = "合成数据（调试）";
            _synthetic.AutoSize = true;
            parameters.Controls.Add(_synthetic);

            _startButton.Text = "开始";
            _startButton.Width = 72;
            _startButton.Click += OnStart;
            parameters.Controls.Add(_startButton);

            _stopButton.Text = "停止";
            _stopButton.Width = 72;
            _stopButton.Enabled = false;
            _stopButton.Click += OnStop;
            parameters.Controls.Add(_stopButton);

            _saveButton.Text = "保存本组";
            _saveButton.Width = 88;
            _saveButton.Enabled = false;
            _saveButton.Click += OnSave;
            parameters.Controls.Add(_saveButton);

            parameters.SetFlowBreak(_saveButton, true);
            var hint = new Label();
            hint.AutoSize = true;
            hint.ForeColor = Color.DimGray;
            hint.Font = new Font("Segoe UI", 8);
            hint.Padding = new Padding(0, 6, 0, 0);
            hint.Text = "组内平均次数：同一组内采集 N 次后在线性 ps 域平均（N>1 才生成平均结果）；"
                + "连续采集开启：每组提交后自动开始下一组，直到点『停止』";
            parameters.Controls.Add(hint);

            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BackColor = Color.White;
            _log.Font = new Font("Consolas", 8.5f);
            _log.HideSelection = false;

            var body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 1;
            body.RowCount = 2;
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            body.Controls.Add(_view, 0, 0);
            body.Controls.Add(_log, 0, 1);

            Controls.Add(body);
            Controls.Add(parameters);
        }

        static Label MakeLabel(string text)
        {
            var label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Padding = new Padding(0, 8, 4, 0);
            return label;
        }

        async void OnStart(object sender, EventArgs e)
        {
            if (_running) return;

            double startM, endM;
            if (!double.TryParse(_startM.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out startM)
                || !double.TryParse(_endM.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out endM))
            {
                AppendLog("参数错误：起点/终点必须是数字");
                return;
            }
            if (!(endM > startM))
            {
                AppendLog("参数错误：终点必须大于起点");
                return;
            }
            if (!_synthetic.Checked && !_env.DeviceReady)
            {
                AppendLog("设备未就绪：缺少 qt_acq/ofdr/detect 或 runtime 目录");
                return;
            }

            if (!string.IsNullOrEmpty(_pendingGroupId)) await CommitPendingAsync();

            bool loop = string.Equals(_runMode.SelectedItem as string, "开启", StringComparison.Ordinal);
            _cts = new CancellationTokenSource();
            _running = true;
            SetRunningState(true);

            try
            {
                do
                {
                    var request = new ScanRequest
                    {
                        Repeat = (int)_repeat.Value,
                        StartM = startM,
                        EndM = endM,
                        RlMode = string.Equals(_rlMode.SelectedItem as string, "基线+峰值", StringComparison.Ordinal)
                            ? "baseline_plus_peak" : "peak_only",
                        CalibrationPath = _calibrationPath,
                        Synthetic = _synthetic.Checked
                    };

                    AcquisitionOutcome outcome = await _runner.AcquireAsync(request, _cts.Token);
                    if (!outcome.Success)
                    {
                        AppendLog("采集未完成：" + outcome.Message);
                        break;
                    }

                    if (loop)
                    {
                        CommitOutcome commit = await _runner.CommitAsync(outcome.GroupId, _calibrationPath);
                        ShowGroup(commit.GroupDir);
                        if (!commit.Success) break;
                        RaiseGroupCommitted();
                        await Task.Delay(500, _cts.Token);
                    }
                    else
                    {
                        _pendingGroupId = outcome.GroupId;
                        _saveButton.Enabled = true;
                        ShowStaging(outcome.LastScanDir, outcome.GroupId);
                        AppendLog("采集完成，点击『保存本组』提交到数据根");
                        break;
                    }
                }
                while (!_cts.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
                AppendLog("已停止");
            }
            catch (Exception ex)
            {
                AppendLog("测量失败：" + ex.Message);
            }
            finally
            {
                _running = false;
                SetRunningState(false);
            }
        }

        void OnStop(object sender, EventArgs e)
        {
            if (_cts != null) _cts.Cancel();
        }

        async void OnSave(object sender, EventArgs e)
        {
            _saveButton.Enabled = false;
            await CommitPendingAsync();
        }

        void RaiseGroupCommitted()
        {
            Action handler = GroupCommitted;
            if (handler != null) handler();
        }

        async Task CommitPendingAsync()
        {
            if (string.IsNullOrEmpty(_pendingGroupId)) return;
            string groupId = _pendingGroupId;
            _pendingGroupId = null;
            CommitOutcome outcome = await _runner.CommitAsync(groupId, _calibrationPath);
            ShowGroup(outcome.GroupDir);
            if (outcome.Success) RaiseGroupCommitted();
        }

        void ShowStaging(string scanDir, string groupId)
        {
            if (scanDir == null)
            {
                AppendLog("没有可预览的扫描目录");
                return;
            }
            try
            {
                MeasurementSnapshot snapshot = GroupReader.LoadScanDir(scanDir);
                snapshot.GroupId = groupId;
                snapshot.Status = "pending";
                _view.ShowSnapshot(snapshot);
                RaiseSnapshotChanged();
            }
            catch (Exception ex)
            {
                AppendLog("读取扫描失败：" + ex.Message);
            }
        }

        void ShowGroup(string groupDir)
        {
            if (groupDir == null || !Directory.Exists(groupDir))
            {
                AppendLog("组目录不存在，无法显示结果");
                return;
            }
            try
            {
                _view.ShowSnapshot(GroupReader.LoadGroup(groupDir));
                RaiseSnapshotChanged();
            }
            catch (Exception ex)
            {
                AppendLog("读取组失败：" + ex.Message);
            }
        }

        void RaiseSnapshotChanged()
        {
            Action<MeasurementSnapshot> handler = SnapshotChanged;
            if (handler != null) handler(_view.Snapshot);
        }

        void Recompute()
        {
            MeasurementSnapshot snapshot = _view.Snapshot;
            if (snapshot == null || snapshot.Events == null || snapshot.Baseline == null) return;
            try
            {
                snapshot.Calibration = _calibration ?? new CalibrationModel();
                snapshot.Summary = MetricsAggregator.Compute(snapshot.ScanId, snapshot.Events, snapshot.Dist,
                    snapshot.Baseline, snapshot.Range, snapshot.RlMode, snapshot.AverageCount,
                    snapshot.Calibration, null, null, snapshot.Scan == null || snapshot.Scan.configSnapshot == null
                        ? null : snapshot.Scan.configSnapshot.hash);
                _view.ShowSnapshot(snapshot);
                RaiseSnapshotChanged();
                AppendLog("已按新校准重算当前结果");
            }
            catch (Exception ex)
            {
                AppendLog("重算失败：" + ex.Message);
            }
        }

        void SetRunningState(bool running)
        {
            _startButton.Enabled = !running;
            _stopButton.Enabled = running;
            _repeat.Enabled = !running;
            _runMode.Enabled = !running;
            _rlMode.Enabled = !running;
            _startM.Enabled = !running;
            _endM.Enabled = !running;
            _synthetic.Enabled = !running;
        }

        void OnRunnerLog(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(OnRunnerLog), message);
                return;
            }
            AppendLog(message);
        }

        public void AppendLog(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), message);
                return;
            }
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            if (_log.Lines.Length > 800)
            {
                var lines = _log.Lines;
                var kept = new string[400];
                Array.Copy(lines, lines.Length - 400, kept, 0, 400);
                _log.Lines = kept;
            }
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }
}
