using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using OFDR.Contracts;
using OFDR.Engine;
using OFDR.Infrastructure.Config;
using OFDR.Infrastructure.Persistence;
using OFDR.Infrastructure.Switches;
using OFDR.Infrastructure.Tcp;
using OFDR.Metrics;
using OFDR.Replay;

namespace OFDR.App;

/// <summary>
/// 一期最小演示界面：数据源（离线合成/在线 TCP）→ 通道遍历 → 指标表 / 距离域（曲线 + 事件表）→ CSV/日志。
/// 业务规则全部在 OFDR.Metrics / OFDR.Engine，本窗体只做输入、绑定与展示。
/// </summary>
public sealed class MainForm : Form
{
    private const double RowSpacingM = 0.0002;
    private const double CmPerRow = 0.04;
    private const int GridColumns = 13;
    private const int DataRowOffset = 0;

    private readonly DataGridView _grid = new();
    private readonly DataGridView _eventGrid = new();
    private readonly Panel _plotHost = new();
    private readonly DistancePlot _plot = new();
    private readonly TabControl _tabs = new();
    private readonly TextBox _logBox = new();
    private readonly Label _statusLabel = new();
    private readonly ComboBox _sourceBox = new();

    private readonly TextBox _hostBox = new() { Text = "192.168.3.111" };
    private readonly TextBox _portBox = new() { Text = "4300" };
    private readonly TextBox _inRangeBox = new() { Text = "1-2" };
    private readonly TextBox _outRangeBox = new() { Text = "1-2" };
    private readonly TextBox _samplesBox = new() { Text = "5" };
    private readonly TextBox _timeoutBox = new() { Text = "10000" };
    private readonly TextBox _thresholdBox = new() { Text = "-85" };
    private readonly TextBox _aPosBox = new() { Text = "5.5" };
    private readonly TextBox _dPosBox = new() { Text = "12.5" };
    private readonly TextBox _windowBox = new() { Text = "80" };
    private readonly TextBox _saveDirBox = new() { Text = string.Empty };
    private readonly TextBox _configPathBox = new() { Text = string.Empty };

    private readonly Button _browseBtn = new() { Text = "浏览…", AutoSize = true };
    private readonly Button _startBtn = new() { Text = "开始测试", AutoSize = true };
    private readonly Button _stopBtn = new() { Text = "停止", AutoSize = true, Enabled = false };

    private CancellationTokenSource? _cts;
    private Thread? _worker;
    private TestEngine? _engine;
    private MetricsConfig? _config;
    private ResultCsvWriter? _csv;
    private LogFileWriter? _logFile;

    private int _activeRow = -1;
    private string _state = "空闲";

    public MainForm()
    {
        Text = "光脉OFDR测试平台 优化一期";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "guangmai.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1500, 900);
        MinimumSize = new Size(1180, 680);
        Font = new Font("Microsoft YaHei", 9F);

        BuildLayout();
        BuildGrid();
        BuildEventGrid();
        _plot.Dock = DockStyle.Fill;
        _plotHost.Controls.Add(_plot);

        _startBtn.Click += (_, _) => StartTest();
        _stopBtn.Click += (_, _) => StopTest();
        _browseBtn.Click += (_, _) => BrowseSaveDir();
        _eventGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _eventGrid.Rows.Count) return;
            if (_eventGrid.Rows[e.RowIndex].Tag is double distance) _plot.FocusOn(distance);
        };
        FormClosing += (_, _) => Shutdown();

        AppendLog("就绪。默认离线合成源，可直接点击【开始测试】跑通闭环；切换“在线 TCP 整机”后需填写设备地址。");
        AppendLog("提示：一期未接入真实串口开关，通道切换为模拟；整机 TCP 数据路径为真实实现。");
        RefreshStatus();
    }

    // ---------------- 界面构建 ----------------

    private void BuildLayout()
    {
        var settings = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(6),
            BackColor = Color.FromArgb(245, 248, 252),
        };

        _sourceBox.Items.Add("离线合成（无硬件）");
        _sourceBox.Items.Add("在线 TCP 整机");
        _sourceBox.SelectedIndex = 0;
        _sourceBox.DropDownStyle = ComboBoxStyle.DropDownList;

        AddField(settings, "数据源", _sourceBox, 170);
        AddField(settings, "设备地址", _hostBox, 130);
        AddField(settings, "端口", _portBox, 60);
        AddField(settings, "IN 范围", _inRangeBox, 60);
        AddField(settings, "OUT 范围", _outRangeBox, 60);
        AddField(settings, "采样数", _samplesBox, 55);
        AddField(settings, "通道超时(ms)", _timeoutBox, 70);
        AddField(settings, "RL 门限(dBm)", _thresholdBox, 70);
        AddField(settings, "A 位置(m)", _aPosBox, 65);
        AddField(settings, "D 位置(m)", _dPosBox, 65);
        AddField(settings, "窗口(cm)", _windowBox, 65);
        AddField(settings, "保存目录", _saveDirBox, 230);
        settings.Controls.Add(_browseBtn);
        AddField(settings, "参考配置(config.txt)", _configPathBox, 300);
        settings.Controls.Add(_startBtn);
        settings.Controls.Add(_stopBtn);

        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.FixedSingle;

        _eventGrid.Dock = DockStyle.Fill;
        _eventGrid.BackgroundColor = Color.White;
        _eventGrid.BorderStyle = BorderStyle.FixedSingle;
        _eventGrid.ReadOnly = true;
        _eventGrid.AllowUserToAddRows = false;
        _eventGrid.AllowUserToDeleteRows = false;
        _eventGrid.RowHeadersVisible = false;
        _eventGrid.MultiSelect = false;
        _eventGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

        // 曲线控件（DistancePlot）后续直接 Dock 进这个宿主面板
        _plotHost.Dock = DockStyle.Fill;
        _plotHost.BackColor = Color.White;
        _plotHost.BorderStyle = BorderStyle.FixedSingle;

        var metricsTab = new TabPage("指标表");
        metricsTab.Controls.Add(_grid);

        var distanceTab = new TabPage("距离域");
        var distanceLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(6),
        };
        distanceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        distanceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        distanceLayout.Controls.Add(_plotHost, 0, 0);
        distanceLayout.Controls.Add(_eventGrid, 0, 1);
        distanceTab.Controls.Add(distanceLayout);

        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(metricsTab);
        _tabs.TabPages.Add(distanceTab);

        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.BackColor = Color.White;
        _logBox.Font = new Font("Consolas", 9F);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = Color.DarkGreen;

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.Controls.Add(_statusLabel, 0, 0);
        bottom.Controls.Add(_logBox, 0, 1);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        root.Controls.Add(settings, 0, 0);
        root.Controls.Add(_tabs, 0, 1);
        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);
    }

    private static void AddField(FlowLayoutPanel panel, string text, Control control, int width)
    {
        control.Width = width;

        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(8, 9, 2, 0),
            ForeColor = Color.FromArgb(60, 60, 60),
        };

        // 标签与输入框成组，窄窗口换行时不会把两者拆到不同行
        var unit = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        unit.Controls.Add(label);
        unit.Controls.Add(control);
        panel.Controls.Add(unit);
    }

    private void BuildGrid()
    {
        string[] headers =
        {
            "组别", "IN", "OUT", "IL-IN", "IL-DUT", "IL-OUT", "RL-IN", "RL-DUT", "RL-OUT",
            "光程(m)", "链路长度(m)", "峰数", "状态",
        };
        int[] weights = { 40, 40, 40, 72, 78, 72, 78, 78, 72, 78, 88, 48, 170 };

        _grid.Columns.Clear();
        for (int i = 0; i < GridColumns; i++)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = headers[i],
                Name = "col" + i,
                FillWeight = weights[i],
                MinimumWidth = weights[i],
                SortMode = DataGridViewColumnSortMode.NotSortable,
                Resizable = DataGridViewTriState.False,
            });
        }

        _grid.Rows.Clear();
        for (int g = 1; g <= 8; g++)
        {
            for (int r = 1; r <= 8; r++)
            {
                int index = _grid.Rows.Add(g.ToString(), g.ToString(), r.ToString());
                _grid.Rows[index].Cells[3].Style.ForeColor = Color.DimGray;
            }
        }
    }

    private void BuildEventGrid()
    {
        string[] headers = { "序号", "距离(m)", "回损(dB)", "段插损(dB)", "累积(dB)", "宽度(mm)" };
        int[] weights = { 48, 96, 96, 96, 96, 96 };

        _eventGrid.Columns.Clear();
        for (int i = 0; i < headers.Length; i++)
        {
            _eventGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = headers[i],
                Name = "ev" + i,
                FillWeight = weights[i],
                MinimumWidth = weights[i],
                SortMode = DataGridViewColumnSortMode.NotSortable,
                Resizable = DataGridViewTriState.False,
            });
        }
    }

    // ---------------- 测试控制 ----------------

    private void StartTest()
    {
        if (_worker is { IsAlive: true }) return;

        double threshold = 0, aM = 0, dM = 0, windowCm = 0;
        int port = 0, samples = 0, timeout = 0;
        string? error = null;
        if (!TryParseDouble(_thresholdBox.Text, out threshold)) error = "RL 门限不是数值";
        else if (!TryParseDouble(_aPosBox.Text, out aM)) error = "A 位置不是数值";
        else if (!TryParseDouble(_dPosBox.Text, out dM)) error = "D 位置不是数值";
        else if (!TryParseDouble(_windowBox.Text, out windowCm) || windowCm <= 0) error = "窗口宽度不是有效数值";
        else if (!TryParseInt(_portBox.Text, out port) || port <= 0 || port > 65535) error = "端口非法";
        else if (!TryParseInt(_samplesBox.Text, out samples) || samples < 1) error = "采样数必须 ≥ 1";
        else if (!TryParseInt(_timeoutBox.Text, out timeout) || timeout < 0) error = "通道超时非法";
        else if (aM < 0 || dM < 0 || aM > dM) error = "A/D 位置非法（须 0 ≤ A ≤ D）";

        ParseRange(_inRangeBox.Text, out int inFrom, out int inTo);
        ParseRange(_outRangeBox.Text, out int outFrom, out int outTo);

        if (error != null)
        {
            MessageBox.Show(error, "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            AppendLog("[错误] " + error);
            return;
        }

        // 输入校验后直接使用已解析的本地变量

        var config = new MetricsConfig
        {
            PeakThresholdDbm = threshold,
            PeakMergeGap = 50,
            MarkARow = aM / RowSpacingM,
            MarkDRow = dM / RowSpacingM,
            AvgHalfWindow = (int)Math.Round(windowCm / CmPerRow),
            LinkLenPerRow = RowSpacingM,
            DutPathPerRow = RowSpacingM,
        };
        var configErrors = config.Validate();
        if (configErrors.Count > 0)
        {
            MessageBox.Show(string.Join("；", configErrors), "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _config = config;

        var references = ReferenceValues.Zero;
        if (!string.IsNullOrWhiteSpace(_configPathBox.Text))
        {
            var path = _configPathBox.Text.Trim();
            if (File.Exists(path))
            {
                var legacy = LegacyConfigReader.Load(path);
                references = legacy.References;
                AppendLog($"已读取参考配置：{path}（SRO={references.SroBase}）");
            }
            else
            {
                AppendLog($"[警告] 参考配置不存在：{path}，基准值按 0 处理");
            }
        }

        string saveDir = _saveDirBox.Text.Trim();
        string logDir = string.IsNullOrEmpty(saveDir)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : saveDir;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        try
        {
            Directory.CreateDirectory(logDir);
            _logFile = new LogFileWriter(Path.Combine(logDir, $"ofdr_log_{stamp}.txt"));
            _csv = new ResultCsvWriter(Path.Combine(logDir, $"ofdr_metrics_{stamp}.csv"));
            AppendLog($"日志：{Path.Combine(logDir, $"ofdr_log_{stamp}.txt")}");
            AppendLog($"指标：{Path.Combine(logDir, $"ofdr_metrics_{stamp}.csv")}");
        }
        catch (Exception ex)
        {
            AppendLog($"[警告] 输出文件不可写（{ex.Message}），本次不落盘");
            _logFile = null;
            _csv = null;
        }

        IScanSource source;
        if (_sourceBox.SelectedIndex == 1)
        {
            source = new OfdrTcpSource(
                new TcpScanOptions(Host: _hostBox.Text.Trim(), Port: port),
                msg => AppendLog(msg));
        }
        else
        {
            source = new SyntheticScanSource(
                new SyntheticSourceOptions { FrameIntervalMs = 40 },
                msg => AppendLog(msg));
        }

        var options = new EngineOptions
        {
            InFrom = inFrom,
            InTo = inTo,
            OutFrom = outFrom,
            OutTo = outTo,
            SamplesPerChannel = samples,
            ChannelTimeoutMs = timeout,
            Rounds = 1,
        };

        _engine = new TestEngine(
            source, config, references,
            new SimulatedOpticalSwitch(), new SimulatedMatrixSwitch(),
            options);
        _engine.Log += AppendLog;
        _engine.ScanProduced += scan => RunOnUi(() => ApplyScan(scan));
        _engine.StateChanged += state => RunOnUi(() => { _state = state; RefreshStatus(); });
        _engine.ResultProduced += result =>
        {
            try { _csv?.Write(result, DateTime.Now); } catch (Exception ex) { AppendLog($"[警告] CSV 写入失败：{ex.Message}"); }
            RunOnUi(() => ApplyResult(result));
        };
        _engine.ChannelCompleted += aggregate => RunOnUi(() => ApplyChannelAggregate(aggregate));

        _grid.ClearSelection();
        for (int i = 0; i < _grid.Rows.Count; i++)
            for (int c = 3; c <= 12; c++)
                _grid.Rows[i].Cells[c].Value = null;
        _eventGrid.Rows.Clear();
        _plot.Clear();
        SetActiveRow(-1);

        _cts = new CancellationTokenSource();
        _startBtn.Enabled = false;
        _stopBtn.Enabled = true;
        _state = "启动中";
        RefreshStatus();
        AppendLog($"开始测试：{source.Name}，IN {inFrom}~{inTo} × OUT {outFrom}~{outTo}，每通道 {samples} 帧");

        _worker = new Thread(() =>
        {
            try
            {
                _engine.Run(_cts.Token);
            }
            catch (Exception ex)
            {
                AppendLog($"[异常] {ex.Message}");
                RunOnUi(() => { _state = "故障"; RefreshStatus(); });
            }
            finally
            {
                try { _csv?.Dispose(); } catch { }
                _csv = null;
                try { _logFile?.Dispose(); } catch { }
                _logFile = null;
                AppendLog("====== 采集结束 ======");
                RunOnUi(() =>
                {
                    _startBtn.Enabled = true;
                    _stopBtn.Enabled = false;
                    SetActiveRow(-1);
                });
            }
        })
        { IsBackground = true, Name = "OFDR-测试编排" };
        _worker.Start();
    }

    private void StopTest()
    {
        _stopBtn.Enabled = false;
        _state = "正在停止";
        RefreshStatus();
        AppendLog("已请求停止，正在中断采集…");
        try { _cts?.Cancel(); } catch { }
    }

    private void Shutdown()
    {
        try { _cts?.Cancel(); } catch { }
        try { _worker?.Join(2000); } catch { }
        try { _csv?.Dispose(); } catch { }
        try { _logFile?.Dispose(); } catch { }
    }

    // ---------------- 结果展示 ----------------

    private void ApplyResult(MeasurementResult r)
    {
        int row = RowOf(r.ChannelIn, r.ChannelOut);
        if (row < 0) return;

        var cells = _grid.Rows[row].Cells;
        if (r.IsValid)
        {
            cells[3].Value = F3(r.IlIn);
            cells[4].Value = F3(r.IlDut);
            cells[5].Value = F3(r.IlOut);
            cells[6].Value = F3(r.RlIn);
            cells[7].Value = F3(r.RlDut);
            cells[8].Value = F3(r.RlOut);
            cells[9].Value = F6(r.DutPathM);
            cells[10].Value = F6(r.LinkLengthM);
            cells[11].Value = r.PeakCount.ToString();
            cells[12].Value = "有效";
            cells[12].Style.ForeColor = Color.DarkGreen;
        }
        else
        {
            cells[11].Value = r.PeakCount.ToString();
            cells[12].Value = r.Message;
            cells[12].Style.ForeColor = Color.Firebrick;
        }

        SetActiveRow(row);
        RefreshStatus();
    }

    /// <summary>每帧扫描 → 曲线 + 事件表（在 UI 线程执行）。</summary>
    private void ApplyScan(StandardScan scan)
    {
        if (_config is null || scan.Count == 0) return;
        if (!EventExtractor.TryToDbm(scan, out var dbm)) return;

        _plot.SetTrace(scan.Axis, dbm);
        var events = EventExtractor.Extract(scan, _config);
        _plot.SetEvents(events);
        FillEventGrid(events);
    }

    private void FillEventGrid(IReadOnlyList<DistanceEvent> events)
    {
        // 事件数不变时原地更新单元格：每帧重建会清掉选中行与滚动位置
        if (_eventGrid.Rows.Count != events.Count)
        {
            _eventGrid.Rows.Clear();
            for (int i = 0; i < events.Count; i++) _eventGrid.Rows.Add();
        }

        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            var cells = _eventGrid.Rows[i].Cells;
            cells[0].Value = (i + 1).ToString(CultureInfo.InvariantCulture);
            cells[1].Value = ev.DistanceM.ToString("F3", CultureInfo.InvariantCulture);
            cells[2].Value = ev.PeakDbm.ToString("F1", CultureInfo.InvariantCulture);
            cells[3].Value = Fmt(ev.SegmentLossDb, "F2");
            cells[4].Value = Fmt(ev.CumulativeLossDb, "F2");
            cells[5].Value = ev.WidthMm.ToString("F1", CultureInfo.InvariantCulture);
            _eventGrid.Rows[i].Tag = ev.DistanceM;
        }
    }

    private static string Fmt(double v, string format) =>
        double.IsNaN(v) ? "—" : v.ToString(format, CultureInfo.InvariantCulture);

    private void ApplyChannelAggregate(ChannelAggregate a)
    {
        int row = RowOf(a.ChannelIn, a.ChannelOut);
        if (row < 0) return;

        var cells = _grid.Rows[row].Cells;
        if (a.ValidFrames > 0)
        {
            // 采样数 > 1 时展示均值（与现状 demo 的 ShowAverage 语义一致）
            cells[3].Value = F3(a.IlIn.Avg);
            cells[4].Value = F3(a.IlDut.Avg);
            cells[5].Value = F3(a.IlOut.Avg);
            cells[6].Value = F3(a.RlIn.Avg);
            cells[7].Value = F3(a.RlDut.Avg);
            cells[8].Value = F3(a.RlOut.Avg);
            cells[9].Value = F6(a.DutPath.Avg);
            cells[10].Value = F6(a.LinkLength.Avg);
        }
        cells[11].Value = a.LastPeakCount.ToString();
        cells[12].Value = a.Message;
        cells[12].Style.ForeColor = a.Status == ChannelRunStatus.Completed ? Color.DarkGreen : Color.DarkOrange;
        RefreshStatus();
    }

    private void SetActiveRow(int row)
    {
        if (_activeRow == row) return;
        if (_activeRow >= 0 && _activeRow < _grid.Rows.Count)
            _grid.Rows[_activeRow].DefaultCellStyle.BackColor = Color.White;
        _activeRow = row;
        if (row >= 0 && row < _grid.Rows.Count)
            _grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(255, 249, 219);
    }

    private void RefreshStatus()
    {
        _statusLabel.Text =
            $"状态：{_state}  |  帧 {_engine?.TotalFrames ?? 0}  |  无效 {_engine?.InvalidFrames ?? 0}" +
            (_engine != null ? $"  |  配置快照 {_engine.ConfigId}" : "");
        _statusLabel.ForeColor = _state is "故障" ? Color.Firebrick
            : _state is "运行中" ? Color.DarkRed : Color.DarkGreen;
    }

    // ---------------- 日志 ----------------

    private void AppendLog(string message) => AppendLogInternal(message, writeFile: true);

    private void AppendLogInternal(string message, bool writeFile)
    {
        if (writeFile) _logFile?.Write(message);
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendLogInternal(message, false)));
                return;
            }
            if (_logBox.TextLength > 40_000) _logBox.Clear();
            _logBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch { }
    }

    // ---------------- 小工具 ----------------

    private static int RowOf(int channelIn, int channelOut)
    {
        if (channelIn is < 1 or > 8 || channelOut is < 1 or > 8) return -1;
        return (channelIn - 1) * 8 + (channelOut - 1);
    }

    private static string F3(double v) =>
        double.IsNaN(v) ? "" : v.ToString("F3", CultureInfo.InvariantCulture);

    private static string F6(double v) =>
        double.IsNaN(v) ? "" : v.ToString("F6", CultureInfo.InvariantCulture);

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static void ParseRange(string text, out int from, out int to)
    {
        from = 1;
        to = 8;
        if (string.IsNullOrWhiteSpace(text)) return;
        var parts = text.Split('-', '~', ',');
        if (parts.Length >= 2 &&
            int.TryParse(parts[0].Trim(), out int a) &&
            int.TryParse(parts[1].Trim(), out int b))
        {
            from = Math.Clamp(Math.Min(a, b), 1, 8);
            to = Math.Clamp(Math.Max(a, b), 1, 8);
            return;
        }
        if (int.TryParse(text.Trim(), out int single))
            from = to = Math.Clamp(single, 1, 8);
    }

    private void BrowseSaveDir()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择数据保存目录" };
        if (dialog.ShowDialog() == DialogResult.OK)
            _saveDirBox.Text = dialog.SelectedPath;
    }
}
