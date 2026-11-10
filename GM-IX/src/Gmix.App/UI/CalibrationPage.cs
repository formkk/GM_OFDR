using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

using Gmix.App.Orchestration;
using Gmix.Data;

namespace Gmix.App.UI
{
    /// <summary>校准向导：录入 → 校验 → 版本化保存 → 重算（SRS FR-CAL-01~06 / SDD §8.6）。</summary>
    public sealed class CalibrationPage : UserControl
    {
        readonly AppEnvironment _env;
        readonly TextBox _psdRef = new TextBox();
        readonly TextBox _ilSlope = new TextBox();
        readonly TextBox _ilIntercept = new TextBox();
        readonly TextBox _triggerThreshold = new TextBox();
        readonly TextBox _rlOffsetK = new TextBox();
        readonly TextBox _distScale = new TextBox();
        readonly TextBox _deviceIp = new TextBox();
        readonly CheckBox _synthetic = new CheckBox();
        readonly NumericUpDown _retentionGb = new NumericUpDown();
        readonly Label _status = new Label();
        readonly Label _banner = new Label();

        public event Action<CalibrationModel, string> CalibrationSaved;

        /// <summary>合成数据（调试）开关变化，由主窗体转发给测量页。</summary>
        public event Action<bool> SyntheticChanged;

        /// <summary>数据根保存上限（GB）变化，由主窗体转发给测量页。</summary>
        public event Action<decimal> RetentionChanged;

        /// <summary>数据根保存上限（GB）。</summary>
        public decimal RetentionGb { get { return _retentionGb.Value; } }

        public CalibrationPage(AppEnvironment env)
        {
            _env = env;
            Dock = DockStyle.Fill;
            BuildLayout();
            LoadLatest();
        }

        void BuildLayout()
        {
            var table = new TableLayoutPanel();
            table.Dock = DockStyle.Top;
            table.AutoSize = true;
            table.ColumnCount = 2;
            table.Padding = new Padding(16, 12, 16, 12);

            AddSection(table, "设备参数");
            _deviceIp.Text = _env.DeviceIp;
            _deviceIp.ReadOnly = true;
            _deviceIp.TabStop = false;
            _deviceIp.BackColor = SystemColors.Control;
            _deviceIp.TextAlign = HorizontalAlignment.Center;
            AddRow(table, "设备 IP", _deviceIp);
            var spec = new Label();
            spec.AutoSize = true;
            spec.Padding = new Padding(0, 8, 0, 0);
            spec.Text = AcquisitionDefaults.SampleRateMhz + " MHz · " + AcquisitionDefaults.AcqTimeMs + " ms · 软件触发（只读）";
            AddRow(table, "采集规格", spec);
            _synthetic.Text = "合成数据（调试）";
            _synthetic.AutoSize = true;
            _synthetic.Padding = new Padding(0, 8, 0, 0);
            _synthetic.CheckedChanged += delegate
            {
                Action<bool> handler = SyntheticChanged;
                if (handler != null) handler(_synthetic.Checked);
            };
            AddRow(table, "合成数据（调试）", _synthetic);

            AddSection(table, "校准参数");
            AddRow(table, "功率谱密度", _psdRef);
            _ilSlope.Text = "0.5361";
            AddRow(table, "插损斜率", _ilSlope);
            _ilIntercept.Text = "-0.1";
            AddRow(table, "插损截距", _ilIntercept);
            AddRow(table, "触发阈值", _triggerThreshold);
            AddRow(table, "回损偏移", _rlOffsetK);
            _distScale.Text = "1";
            AddRow(table, "距离比例", _distScale);

            var buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            var validate = new Button();
            validate.Text = "校验";
            validate.Width = 88;
            validate.Click += delegate { ValidateInputs(true); };
            buttons.Controls.Add(validate);

            var save = new Button();
            save.Text = "保存校准";
            save.Width = 96;
            save.Click += OnSave;
            buttons.Controls.Add(save);

            int buttonRow = table.RowCount;
            table.RowCount = buttonRow + 1;
            table.Controls.Add(buttons, 1, buttonRow);

            AddSection(table, "存储与维护");
            _retentionGb.Minimum = 1;
            _retentionGb.Maximum = 500;
            _retentionGb.Value = AcquisitionDefaults.RetentionBytes / (1024L * 1024L * 1024L);
            _retentionGb.ValueChanged += delegate
            {
                Action<decimal> handler = RetentionChanged;
                if (handler != null) handler(_retentionGb.Value);
            };
            AddRow(table, "保存上限 GB", _retentionGb);

            _status.AutoSize = true;
            _status.ForeColor = Color.DimGray;
            _status.Padding = new Padding(16, 4, 0, 0);
            int statusRow = table.RowCount;
            table.RowCount = statusRow + 1;
            table.Controls.Add(new Label { Text = "状态", AutoSize = true, Padding = new Padding(0, 8, 6, 0) }, 0, statusRow);
            table.Controls.Add(_status, 1, statusRow);

            _banner.Dock = DockStyle.Top;
            _banner.Height = 54;
            _banner.Padding = new Padding(16, 8, 16, 0);
            _banner.Font = new Font("Segoe UI", 9, FontStyle.Bold);

            Controls.Add(table);
            Controls.Add(_banner);
        }

        void ShowCalibrationState(bool calibrated, string calibrationId)
        {
            if (calibrated)
            {
                _banner.ForeColor = Color.ForestGreen;
                _banner.Text = "当前状态：已标定" + (string.IsNullOrEmpty(calibrationId) ? "" : "（" + calibrationId + "）")
                    + " —— 回损按 rlOffsetK 计算为绝对回损。";
                return;
            }
            _banner.ForeColor = Color.Firebrick;
            _banner.Text = "当前状态：未标定 —— 回损按显示锚点 " + DisplayTransform.DefaultOffsetK.ToString("0")
                + " dB 预览（来自本机实测拟合），仅影响显示，不参与指标、不落盘、不进入导出。\r\n"
                + "完成下方『回损偏移』标定并保存后，自动切换为真实回损。";
        }

        static void AddSection(TableLayoutPanel table, string title)
        {
            int row = table.RowCount;
            table.RowCount = row + 1;
            table.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Padding = new Padding(0, 10, 0, 2)
            }, 0, row);
            table.SetColumnSpan(table.GetControlFromPosition(0, row), 2);
        }

        static void AddRow(TableLayoutPanel table, string label, Control box)
        {
            int row = table.RowCount;
            table.RowCount = row + 1;
            table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 8, 6, 0) }, 0, row);

            var panel = new FlowLayoutPanel();
            panel.AutoSize = true;
            var text = box as TextBox;
            if (text != null) text.Width = 120;
            panel.Controls.Add(box);
            table.Controls.Add(panel, 1, row);
        }

        void LoadLatest()
        {
            CalibrationModel latest = CalibrationStore.LoadLatest(_env.DataRoot);
            if (latest == null)
            {
                _status.Text = "未找到历史校准";
                ShowCalibrationState(false, null);
                return;
            }
            _psdRef.Text = Format(latest.psdRef);
            _ilSlope.Text = Format(latest.ilSlope);
            _ilIntercept.Text = Format(latest.ilIntercept);
            _triggerThreshold.Text = Format(latest.triggerThresholdDb);
            _rlOffsetK.Text = Format(latest.rlOffsetK);
            _distScale.Text = latest.distScale.ToString("R", CultureInfo.InvariantCulture);
            _status.Text = "已载入 " + latest.calibrationId + "（配置哈希 " + ShortHash(latest.configHash) + "）";
            ShowCalibrationState(latest.HasAbsoluteRl(), latest.calibrationId);
        }

        static string Format(double? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
        }

        static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "—";
            return hash.Length > 20 ? hash.Substring(0, 20) + "…" : hash;
        }

        bool ValidateInputs(bool showMessage)
        {
            string error = null;
            double? psdRef = ParseOptional(_psdRef.Text, out error);
            if (error != null) return Fail("psdRef " + error, showMessage);
            double? ilSlope = ParseOptional(_ilSlope.Text, out error);
            if (error != null) return Fail("ilSlope " + error, showMessage);
            double? ilIntercept = ParseOptional(_ilIntercept.Text, out error);
            if (error != null) return Fail("ilIntercept " + error, showMessage);
            double? threshold = ParseOptional(_triggerThreshold.Text, out error);
            if (error != null) return Fail("triggerThresholdDb " + error, showMessage);
            double? rlOffset = ParseOptional(_rlOffsetK.Text, out error);
            if (error != null) return Fail("rlOffsetK " + error, showMessage);
            double distScale = 1;
            if (!string.IsNullOrWhiteSpace(_distScale.Text)
                && (!double.TryParse(_distScale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out distScale) || distScale <= 0))
            {
                return Fail("distScale 必须为正数", showMessage);
            }

            var model = new CalibrationModel();
            model.psdRef = psdRef;
            model.ilSlope = ilSlope;
            model.ilIntercept = ilIntercept;
            model.triggerThresholdDb = threshold;
            model.rlOffsetK = rlOffset;
            model.distScale = distScale;

            var errors = model.Validate();
            if (errors.Count > 0) return Fail(string.Join("；", errors.ToArray()), showMessage);

            _status.Text = "校验通过" + (showMessage ? "（点『保存校准』写入 _calibration）" : string.Empty);
            _status.ForeColor = Color.ForestGreen;
            return true;
        }

        bool Fail(string message, bool showMessage)
        {
            _status.Text = "校验失败：" + message;
            _status.ForeColor = Color.Firebrick;
            if (showMessage) MessageBox.Show(message, "校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        static double? ParseOptional(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return null;
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                error = "不是数字";
                return null;
            }
            return value;
        }

        void OnSave(object sender, EventArgs e)
        {
            if (!ValidateInputs(false)) return;

            var model = new CalibrationModel();
            string ignored;
            model.psdRef = ParseOptional(_psdRef.Text, out ignored);
            model.ilSlope = ParseOptional(_ilSlope.Text, out ignored);
            model.ilIntercept = ParseOptional(_ilIntercept.Text, out ignored);
            model.triggerThresholdDb = ParseOptional(_triggerThreshold.Text, out ignored);
            model.rlOffsetK = ParseOptional(_rlOffsetK.Text, out ignored);
            double distScale;
            if (!double.TryParse(_distScale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out distScale)) distScale = 1;
            model.distScale = distScale;

            try
            {
                string configHash = CurrentConfigHash();
                string path = CalibrationStore.Save(_env.DataRoot, model, configHash);
                _status.Text = "已保存 " + model.calibrationId + " → " + path;
                _status.ForeColor = Color.ForestGreen;
                ShowCalibrationState(model.HasAbsoluteRl(), model.calibrationId);

                Action<CalibrationModel, string> handler = CalibrationSaved;
                if (handler != null) handler(model, path);
            }
            catch (Exception ex)
            {
                _status.Text = "保存失败：" + ex.Message;
                _status.ForeColor = Color.Firebrick;
            }
        }

        string CurrentConfigHash()
        {
            if (string.IsNullOrEmpty(_env.RuntimeDir)) return null;
            string configDir = System.IO.Path.Combine(_env.RuntimeDir, "config");
            ConfigSnapshot snapshot = ConfigSnapshot.Load(configDir, 0, AcquisitionDefaults.Create());
            return snapshot.Hash;
        }
    }
}
