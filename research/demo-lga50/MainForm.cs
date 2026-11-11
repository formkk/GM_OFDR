using System.Drawing;
using System.Globalization;
using System.IO.Ports;
using System.Threading;

namespace OFDRDataReceiver;

/// <summary>
/// 光博会 OFDR 扫描样机系统 —— 扫描测试界面。
///
/// 布局（自上而下）：
///   1) 标题栏 _topPanel（56px）
///   2) 控制参数区 _ctrlPanel（78px）：测试所有通道 / 无限循环 / 通道范围 / 测试次数 / 采样数 / RL门限
///   3) 指标表格 _grid（Dock.Fill）：8 组 × 8 行 = 64 通道组合，三级表头，组别列垂直合并
///   4) 底栏 _bottomPanel（AutoSize）：连接设置（设备地址 / 端口 / 浏览 / 开始测试 / 停止）+ 状态 + 日志
///
/// 扫描流程（后台线程）：连接设备并持续收帧 → 按 IN×OUT 依次采集，
/// 每个通道采集「采样数」帧，逐帧更新该行的 Cur 并累计 Max / Min / Avg，采满后切换下一通道。
/// </summary>
// 注意：必须是 partial 且不能 sealed —— WinForms 设计器需要继承窗体类来创建设计宿主。
// 所有布局代码在 MainForm.Designer.cs 的 InitializeComponent() 中，可在 VS 设计器里可视化编辑。
public partial class MainForm : Form
{
    private const string DefaultHost = "192.168.3.111";
    private const int DefaultPort = 4300;

    // 注意：所有 UI 控件字段（_grid / _hostBox / _startBtn …）声明在 MainForm.Designer.cs 中，
    // 此处仅声明非控件的业务字段，避免重复定义。

    private readonly DataReceiver _receiver;

    // 串口下拉：_allPorts 为枚举到的全部串口；两个下拉各自独立，互不限制
    private readonly List<string> _allPorts = new();

    // 光开关 / 矩阵开关串口：开始测试时打开并保持，供双层循环反复发送切换命令；结束时关闭
    private SerialPort? _opticalSp;
    private SerialPort? _matrixSp;
    private readonly object _spLock = new();

    // 扫描状态（仅 UI/后台线程通过 volatile + lock 协调）
    private Thread? _worker;
    private volatile bool _stopRequested;
    private readonly object _scanLock = new();
    private int _curIn;                 // 当前通道 IN（1~8）
    private int _curOut;                // 当前通道 OUT（1~8）
    private int _framesInChannel;       // 当前通道已收到帧数（每解析一帧 +1，用于判断采满采样数）
    private int _metricsInChannel;      // 当前通道已产出有效指标的帧数（峰数≥8、成功算出 IL/RL 才 +1）
    private bool _channelReady;         // 当前通道串口已切换、可开始计数
    private int _samplesPerChannel;     // 每通道采样帧数
    private StreamWriter? _saveWriter;      // 由后台线程创建/销毁，UI 线程经 SaveLine 加锁访问
    private readonly object _saveLock = new();
    private StreamWriter? _logWriter;       // 独立日志文件（与原始数据分开）
    private StreamWriter? _badLogWriter;     // 检测失败（检测不到 8 峰）帧的单独日志文件（文件名带 badpeaks 标识）
    private readonly object _logLock = new();
    private string? _logFilePath;

    private long _frameCount;
    private long _lastBytes;
    private DateTime _startTime = DateTime.Now;
    private DateTime _lastTick = DateTime.Now;

    private readonly System.Windows.Forms.Timer _timer;

    public MainForm()
    {
        InitializeComponent();

        // 设计尺寸为 1920×1000；若当前屏幕工作区更小（小屏或高 DPI 导致逻辑分辨率变小），
        // 自动按比例缩小到能放下，避免窗口超出屏幕。内部改用 FlowLayoutPanel，
        // 变窄后字段会自动换行，不会被裁掉。
        FitToScreen();

        _receiver = new DataReceiver(Log, OnFrame, OnMetrics, AppendBadLog);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshStat();
        _timer.Start();

        FormClosing += (_, _) =>
        {
            _timer.Stop();
            _stopRequested = true;
            try { _receiver.Dispose(); } catch { }
            CloseLogFile();
            // 退出时保存当前串口选择，下次启动自动恢复
            SavePortSettings();
        };

        AppendLog("就绪。填写连接设置后点击【开始测试】。");

        // 窗口尺寸/分辨率变化（含高 DPI 缩放）时，重新计算底栏高度，
        // 避免 AutoSize 容器在高 DPI 下缓存过小的尺寸、把「浏览」按钮裁掉一半。
        Resize += (_, _) => RelayoutBottom();

        RefreshComPorts();
        // 恢复上一次使用的两个串口选择（光开关地址 / 光开关矩阵地址）
        ApplySavedPortSelection();
    }

    /// <summary>
    /// 自适应屏幕：设计尺寸为 1920×1000，若当前屏幕工作区放不下则缩小窗口。
    /// 关键点：this.Width/Height 是「设计参考(96 DPI)下的逻辑尺寸」，实际设备像素 =
    /// 逻辑尺寸 × (dpi / 96)；而 Screen.WorkingArea 是设备像素。必须先把屏幕可用尺寸按
    /// 当前设备实际 DPI 换算回逻辑尺寸再比较，否则高 DPI 下会漏判、窗口被放大到超出屏幕，
    /// 导致右下角的「浏览 / 开始测试 / 停止」按钮被裁掉。
    /// 注意：不使用 this.DeviceDpi（OnLoad 时可能尚未就绪、仍返回 96），而用
    /// Graphics.FromHwnd(this.Handle).DpiX 取设备真实 DPI，更可靠。
    /// </summary>
    private void FitToScreen()
    {
        try
        {
            var area = System.Windows.Forms.Screen.FromControl(this).WorkingArea;
            float dpiX = 96f, dpiY = 96f;
            if (this.Handle != IntPtr.Zero)
            {
                using var g = Graphics.FromHwnd(this.Handle);
                dpiX = g.DpiX;
                dpiY = g.DpiY;
            }
            double sx = dpiX / 96.0, sy = dpiY / 96.0;
            if (sx <= 0) sx = 1;
            if (sy <= 0) sy = 1;
            int maxW = (int)(area.Width / sx);
            int maxH = (int)(area.Height / sy);
            // 允许在必要时把窗口缩到比原 MinimumSize 更小（下限 900×600），
            // 否则高 DPI 下 MinimumSize 的限制会让缩放后的窗口仍超出屏幕、裁掉按钮。
            int fitW = Math.Max(900, maxW - 40);
            int fitH = Math.Max(600, maxH - 40);
            MinimumSize = new Size(fitW, fitH);
            if (Width > fitW) Width = fitW;
            if (Height > fitH) Height = fitH;
        }
        catch
        {
            // 取屏幕信息失败时保持设计尺寸，不影响启动
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // DPI 解析完成后再做一次适配：用设备真实 DPI 正确换算，
        // 保证高 DPI 显示器上窗口不超出屏幕、按钮完整可见。
        FitToScreen();
        // 关键：DPI 缩放完成后，AutoSize 容器（连接面板 / 底栏）可能仍沿用缩放前的过小尺寸，
        // 导致底栏高度算小、把「浏览」按钮底部裁掉。强制按当前宽度重测并显式设定高度。
        RelayoutBottom();
        // 容器高度变化后再适配一次窗口（避免窗口被内容顶得超出屏幕）
        FitToScreen();
        // 临时诊断：排查「浏览」按钮被裁问题，确认修复后删除
        DumpDiagnostics();
    }

    /// <summary>
    /// 重新计算底栏高度。连接面板（FlowLayoutPanel）改为固定单行高度（Designer 中 Height=52，
    /// 随 DPI 自动缩放），此处直接读取其已缩放后的高度，再加上状态标签与日志框高度与内边距，
    /// 显式设定底栏高度，彻底消除高 DPI 下 AutoSize + Dock 的测距歧义（此前会把「浏览」按钮裁掉一半）。
    /// </summary>
    private void RelayoutBottom()
    {
        if (_connPanel == null || _bottomPanel == null || _statusLabel == null || _logBox == null) return;
        try
        {
            int connH = _connPanel.Height;            // 已在 Designer 设定，并随 DPI 缩放
            int statusH = _statusLabel.Height;
            int logH = _logBox.Height;
            _bottomPanel.AutoSize = false;
            _bottomPanel.Height = connH + statusH + logH + _bottomPanel.Padding.Vertical;
        }
        catch
        {
            // 布局异常不影响启动
        }
    }

    /// <summary>
    /// 临时诊断：把关键布局尺寸写入 exe 目录下的 layout_diag.txt，便于排查「浏览」按钮被裁问题。
    /// 确认修复后可删除本方法与 OnLoad 中的调用。
    /// </summary>
    private void DumpDiagnostics()
    {
        try
        {
            float dx = 96f, dy = 96f;
            if (this.Handle != IntPtr.Zero)
            {
                using var g = Graphics.FromHwnd(this.Handle);
                dx = g.DpiX; dy = g.DpiY;
            }
            var area = System.Windows.Forms.Screen.FromControl(this).WorkingArea;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"DPI={dx}x{dy}");
            sb.AppendLine($"Screen.WorkingArea={area.Width}x{area.Height}");
            sb.AppendLine($"Form.ClientSize={ClientSize} Bounds={Bounds}");
            sb.AppendLine($"connPanel.Location={_connPanel.Location} Size={_connPanel.Size} ClientSize={_connPanel.ClientSize}");
            sb.AppendLine($"browseBtn.Location={_browseBtn.Location} Size={_browseBtn.Size} Right={_browseBtn.Right} Bottom={_browseBtn.Bottom}");
            sb.AppendLine($"bottomPanel.Size={_bottomPanel.Size} Height={_bottomPanel.Height}");
            var pt = _connPanel.PointToScreen(_browseBtn.Location);
            sb.AppendLine($"browseBtn.ScreenPos={pt}");
            string path = Path.Combine(Application.StartupPath, "layout_diag.txt");
            File.WriteAllText(path, sb.ToString());
            AppendLog($"[诊断] 已写入 {path}");
        }
        catch (Exception ex)
        {
            AppendLog($"[诊断错误] {ex.Message}");
        }
    }

    /// <summary>
    /// 刷新「光开关地址 / 光开关矩阵地址」两个串口下拉框：
    /// 枚举当前系统所有可用串口（经 SerialHelper.GetPortNames 读取注册表并自然排序，
    /// COM2 排在 COM10 之前），两个下拉各自独立列出全部串口、自由选择，互不限制。
    /// </summary>
    private void RefreshComPorts()
    {
        List<string> ports;
        try
        {
            ports = SerialHelper.GetPortNames();
        }
        catch (Exception ex)
        {
            AppendLog($"[警告] 枚举串口失败：{ex.Message}");
            ports = new List<string>();
        }

        _allPorts.Clear();
        _allPorts.AddRange(ports);
        RebuildPortCombos();

        AppendLog(ports.Count == 0
            ? "未找到任何串口（光开关/矩阵地址下拉为空）"
            : $"已枚举到 {ports.Count} 个串口：{string.Join("、", ports)}");
    }

    /// <summary>
    /// 重建两个串口下拉：各自独立列出全部串口并尽量保留原选择。
    /// 两者互不限制，可以选到同一个串口。
    /// </summary>
    private void RebuildPortCombos()
    {
        FillPorts(_opticalPort);
        FillPorts(_matrixPort);
    }

    /// <summary>用全部串口填充下拉并尽量保留原选择（无串口时显示占位项）。</summary>
    private void FillPorts(ComboBox self)
    {
        string? prev = self.SelectedItem as string;
        bool hasPrev = !string.IsNullOrEmpty(prev) && prev != NoComPort;
        self.Items.Clear();
        if (_allPorts.Count == 0)
        {
            // 无可用串口时仍保留上一次的选择（即便当前没插该设备），便于恢复
            if (hasPrev) self.Items.Add(prev!);
            self.Items.Add(NoComPort);
            self.SelectedIndex = self.Items.Count - 1;
            return;
        }
        foreach (var p in _allPorts) self.Items.Add(p);

        // 优先保留上一次的选择；若该串口当前未枚举到，也追加进列表以保留原值，不回退到第一个
        if (hasPrev)
        {
            int idx = self.Items.IndexOf(prev);
            if (idx < 0) { self.Items.Add(prev!); idx = self.Items.Count - 1; }
            self.SelectedIndex = idx;
        }
        else
        {
            self.SelectedIndex = 0;
        }
    }

    /// <summary>无可用串口时的占位显示文本。</summary>
    private const string NoComPort = "（无可用串口）";

    // ---------------- 串口选择持久化（记住上一次使用的光开关 / 矩阵开关串口） ----------------
    // 串口选择持久化到程序的 config.txt（与峰值检测参数同一文件），按 key 更新、保留其余内容
    private const string PortSettingsFile = "config.txt";
    private static readonly string PortSettingsPath = Path.Combine(Application.StartupPath, PortSettingsFile);

    /// <summary>读取上次保存的两个串口选择（光开关地址 / 光开关矩阵地址）。</summary>
    private (string? Optical, string? Matrix) LoadPortSettings()
    {
        string? opt = null, mat = null;
        try
        {
            if (File.Exists(PortSettingsPath))
            {
                foreach (var raw in File.ReadAllLines(PortSettingsPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var key = line[..eq].Trim();
                    var val = line[(eq + 1)..].Trim();
                    if (key == "OpticalPort") opt = val;
                    else if (key == "MatrixPort") mat = val;
                }
            }
        }
        catch { /* 读取失败则视为无保存值 */ }
        return (opt, mat);
    }

    /// <summary>保存当前两个串口的选择到 config.txt（占位项「无可用串口」不写入，以免冲掉已保存的值）。</summary>
    private void SavePortSettings()
    {
        try
        {
            string opt = SelectedComPort(_opticalPort) ?? "";
            string mat = SelectedComPort(_matrixPort) ?? "";
            if (!string.IsNullOrEmpty(opt)) SaveConfigValue("OpticalPort", opt);
            if (!string.IsNullOrEmpty(mat)) SaveConfigValue("MatrixPort", mat);
        }
        catch { /* 写入失败不影响使用 */ }
    }

    /// <summary>在 config.txt 中更新（若存在）或追加（若不存在）指定 key，保留其余行与注释。</summary>
    private static void SaveConfigValue(string key, string value)
    {
        var path = PortSettingsPath;
        var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
        int idx = lines.FindIndex(l =>
        {
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith("#")) return false;
            int eq = t.IndexOf('=');
            return eq >= 0 && t[..eq].Trim() == key;
        });
        var entry = $"{key} = {value}";
        if (idx >= 0) lines[idx] = entry;
        else lines.Add(entry);
        File.WriteAllLines(path, lines);
    }

    /// <summary>用上次保存的值恢复两个串口下拉选择；若设备当前未枚举到该串口，也把它加进列表以保留原值。</summary>
    private void ApplySavedPortSelection()
    {
        var (opt, mat) = LoadPortSettings();
        TrySelectPort(_opticalPort, opt);
        TrySelectPort(_matrixPort, mat);
    }

    /// <summary>在下拉框中选中指定串口值；不存在则追加该项后再选中（保留上一次的值）。</summary>
    private static void TrySelectPort(ComboBox cb, string? val)
    {
        if (string.IsNullOrEmpty(val)) return;
        int idx = cb.Items.IndexOf(val);
        if (idx < 0)
        {
            cb.Items.Add(val);
            idx = cb.Items.Count - 1;
        }
        cb.SelectedIndex = idx;
    }

    // ---------------- 测试控制 ----------------
    private void StartTest()
    {
        if (_worker is { IsAlive: true }) return;

        RefreshComPorts();   // 确保「光开关地址 / 光开关矩阵地址」下拉为当前可用串口

        if (!int.TryParse(_portBox.Text.Trim(), out int port)) port = DefaultPort;
        if (!int.TryParse(_sampleCount.Text.Trim(), out int samples) || samples < 1) samples = 10;
        if (!int.TryParse(_testTimes.Text.Trim(), out int times) || times < 1) times = 1;
        // 单通道等待采满帧的超时（ms）：超时则跳过该通道，防止设备停发时永久卡住
        int channelTimeoutMs = DataReceiver.ChannelTimeoutMs;   // 取自 config.txt（GUI 输入框已移除）

        string host = _hostBox.Text.Trim();
        string saveDir = _savePathBox.Text.Trim();
        bool testAll = _testAllChannels.Checked;
        bool infinite = _infiniteLoop.Checked;
        bool enablePeak = _enablePeak.Checked;

        // 在 UI 线程读取两个串口下拉的当前选择（控件只能由创建它的线程访问），
        // 再交给后台线程去打开；占位项「（无可用串口）」视为未选择。
        string? opticalCom = SelectedComPort(_opticalPort);
        string? matrixCom = SelectedComPort(_matrixPort);
        // 保存本次使用的串口选择，下次启动自动恢复
        SavePortSettings();

        // 点击「开始测试」时检查：两个开关串口不可选择同一个 COM（选择时不做占用判断，此处统一校验）
        if (!string.IsNullOrEmpty(opticalCom) && opticalCom == matrixCom)
        {
            MessageBox.Show($"光开关地址与光开关矩阵地址不能选择同一个串口（当前均为 {opticalCom}），请重新选择。",
                "串口选择重复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            AppendLog($"[错误] 串口选择重复：光开关与矩阵均为 {opticalCom}，已取消开始测试");
            return;
        }

        // 解析通道范围（如 "1-8"），失败回退 1~8
        ParseRange(_channelRange.Text, out int inFrom, out int inTo);
        int outFrom = 1, outTo = 8;
        if (!testAll) { outFrom = inFrom; outTo = inTo; }
        int inEnd = inTo;
        int outEnd = outTo;

        _samplesPerChannel = samples;
        _grid.ShowAverage = samples > 1;   // 采样数>1 时，指标表格四列显示平均值
        _stopRequested = false;
        lock (_scanLock)
        {
            _curIn = inFrom;
            _curOut = outFrom;
            _framesInChannel = 0;
            _metricsInChannel = 0;
            _channelReady = false;
        }
        _frameCount = 0;
        SetStatus("已就绪，等待数据帧… 帧数 0", Color.DarkGreen);   // 立即刷新，确保每轮开始帧数归零可见
        _startTime = DateTime.Now;
        _lastTick = DateTime.Now;
        _lastBytes = 0;

        // 保存目录留空 = 原始 .dat 不落盘；日志与指标 CSV 仍会保存（放在日志文件所在目录）
        string? saveFile = null;      // 指标 CSV（与日志同目录）
        string? dataFile = null;      // 网口原始数据（留空=不保存）
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // 原始 .dat：按「保存目录」落盘（留空则不保存）
        if (!string.IsNullOrEmpty(saveDir))
        {
            try
            {
                if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);
                dataFile = Path.Combine(saveDir, $"ofdr_raw_{stamp}.dat");
            }
            catch (Exception ex)
            {
                AppendLog($"[警告] 保存路径不可用（{ex.Message}），本次不保存原始数据");
                dataFile = null;
            }
        }

        // 日志文件：填了保存目录则与之同目录，否则放程序目录下 logs 子目录。
        // 必须先于指标 CSV 创建：CSV 与日志同目录，且「指标保存至」这行要写进日志文件。
        OpenLogFile(saveDir);

        // 指标 CSV：与日志文件同目录（不再跟随 .dat 的保存目录），无论是否填写保存目录都保存
        if (DataReceiver.SaveMetrics)
        {
            string? logDir = string.IsNullOrEmpty(_logFilePath) ? null : Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(logDir))
            {
                try
                {
                    saveFile = Path.Combine(logDir, $"ofdr_metrics_{stamp}.csv");
                    File.WriteAllText(saveFile,
                        "采样序号,Timestamp,IN,OUT,IL-IN(dB),IL-DUT(dB),IL-OUT(dB),RL-IN(dBm),RL-DUT(dBm),RL-OUT(dBm),DUT光程,链路长度,峰值数,SRO,SRI-IN,SRO-OUT,SRB-IN,SRC-OUT" + Environment.NewLine);
                    AppendLog($"指标保存至：{saveFile}");
                }
                catch (Exception ex)
                {
                    AppendLog($"[警告] 指标 CSV 不可写（{ex.Message}），本次不保存指标");
                    saveFile = null;
                }
            }
        }
        else
        {
            AppendLog("config 已禁用 ofdr_metrics 指标保存（SaveMetrics=false）");
        }

        _grid.ClearAllMetrics();
        _startBtn.Enabled = false;
        _stopBtn.Enabled = true;
        SetStatus("运行中…", Color.DarkRed);
        AppendLog($"开始测试：{host}:{port}，通道 IN {inFrom}~{inTo} × OUT {outFrom}~{outTo}，每通道 {samples} 帧");
        if (!enablePeak) AppendLog("[配置] 已禁用峰值检测：仅做接收与逐行解析，不计算 IL/RL、不填指标表格");

        _worker = new Thread(() =>
        {
            // 保存流：saveFile 非空时按行追加（仅在后台线程内写入，UI 线程不碰）
            StreamWriter? writer = null;
            if (saveFile != null)
            {
                try
                {
                    writer = new StreamWriter(saveFile, append: true) { AutoFlush = false };
                    _saveWriter = writer;
                }
                catch (Exception ex) { AppendLog($"[警告] 无法写入保存文件：{ex.Message}"); }
            }

            try
            {
                // 连接设备（阻塞：含 Connect + OSI 握手），之后持续收帧，由 OnMetrics 驱动填表
                // dataFile：网口原始数据落盘（null = 不保存）
                _receiver.EnablePeakDetection = enablePeak;
                // 阈值取 GUI「RL门限」；解析失败则沿用 config.txt 的 PeakThresholdDb 默认值
                if (double.TryParse(_rlThreshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double rlTh))
                    _receiver.PeakThresholdDbm = rlTh;
                else
                    AppendLog($"[警告] RL门限「{_rlThreshold.Text}」无法解析为数值，使用默认值 {_receiver.PeakThresholdDbm} dBm");

                // 标记点：GUI「开始位置(A m)」「终止位置(D m)」输入单位为米（范围 1~16），需除以 0.0002 换算为行号
                const double MetersToRow = 0.0002;   // 米→行号：行号 = 米 / 0.0002（与链路长度/DUT光程系数一致）
                const double PosMinM = 1.0, PosMaxM = 16.0;
                bool InputError(string msg) { AppendLog("[错误] " + msg); MessageBox.Show(msg, "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return true; }
                double startM, endM, halfWin;
                if (!double.TryParse(_startPos.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out startM))
                { if (InputError($"开始位置「{_startPos.Text}」无法解析为数值")) return; }
                if (!double.TryParse(_endPos.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out endM))
                { if (InputError($"终止位置「{_endPos.Text}」无法解析为数值")) return; }
                if (startM < PosMinM || startM > PosMaxM)
                { if (InputError($"开始位置 {startM} m 超界（须 {PosMinM}~{PosMaxM} m）")) return; }
                if (endM < PosMinM || endM > PosMaxM)
                { if (InputError($"终止位置 {endM} m 超界（须 {PosMinM}~{PosMaxM} m）")) return; }
                if (startM > endM)
                { if (InputError($"开始位置 {startM} m 大于 终止位置 {endM} m")) return; }
                _receiver.MarkARow = startM / MetersToRow;
                _receiver.MarkDRow = endM / MetersToRow;
                AppendLog($"标记点：A={_receiver.MarkARow:F0} 行（{startM} m），D={_receiver.MarkDRow:F0} 行（{endM} m）");
                // 局部平均半窗（AvgHalfWindow）：GUI「积分窗口宽度（cm）」框以 cm 为单位输入；
                // 转换关系 行 = cm / 0.04（用户指定：10cm → 半窗 250 行；即 1 行 = 0.04 cm）。
                // 若框为空/非法，则回退用 config.txt 的 IntegralWindowCm（同样单位 cm）。
                // 校验范围取自 config.txt（IntegralWindowCmMin/IntegralWindowCmMax），运行时单独校验。
                const double CmPerRow = 0.04;   // 积分窗口专用换算：1 行 = CmPerRow cm（与位置换算 0.0002 m/行 不同）
                double winMinCm = DataReceiver.IntegralWindowCmMin;
                double winMaxCm = DataReceiver.IntegralWindowCmMax;
                int winRows;
                string srcDesc;
                bool fromBox = double.TryParse(_avgHalfWindow.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out halfWin)
                               && halfWin >= winMinCm && halfWin <= winMaxCm;
                if (fromBox)
                {
                    // halfWin 此处为 cm；换算为半窗行数（1 行 = CmPerRow cm）
                    winRows = (int)Math.Round(halfWin / CmPerRow);
                    srcDesc = $"由「积分窗口宽度」框 {halfWin} cm 换算";
                }
                else
                {
                    // 回退：config 积分窗口宽度(cm) → 行数（1 行 = CmPerRow cm）
                    double winCm = DataReceiver.IntegralWindowCm;
                    if (winCm < winMinCm || winCm > winMaxCm)
                    { if (InputError($"config 积分窗口宽度 {winCm} cm 超界（须 {winMinCm}~{winMaxCm} cm）")) return; }
                    winRows = (int)Math.Round(winCm / CmPerRow);
                    srcDesc = $"由 config 积分窗口宽度 {winCm} cm 换算";
                }
                _receiver.AvgHalfWindow = winRows;
                AppendLog($"局部平均半窗 AvgHalfWindow = {winRows} 行（{srcDesc}，中心 ±{winRows} 行，窗口 {2 * winRows + 1} 行）");

                // 波峰检查窗口（旁瓣过滤）：取自 config.txt 的 WindowWidthCm，运行时单独校验范围
                double peakWinCm = DataReceiver.WindowWidthCm;
                if (peakWinCm < DataReceiver.WindowWidthCmMin || peakWinCm > DataReceiver.WindowWidthCmMax)
                { if (InputError($"config 波峰检查窗口宽度 {peakWinCm} cm 超界（须 {DataReceiver.WindowWidthCmMin}~{DataReceiver.WindowWidthCmMax} cm）")) return; }
                AppendLog($"波峰检查窗口 WindowWidthCm = {peakWinCm} cm（→ PeakLocalWindow 由解析线程换算，1cm → 50 行）");

                // 打开光开关 / 矩阵开关两个串口并保持打开（双层循环中反复发送切换命令，结束时关闭）
                _opticalSp = SerialHelper.OpenPort(opticalCom, Log);
                _matrixSp = SerialHelper.OpenPort(matrixCom, Log);
                if (_opticalSp == null && _matrixSp == null)
                    AppendLog("[串口] 两个开关串口均不可用，本次不发送切换命令（光路保持当前状态）");

                // 连接设备前：两个串口并行发送 <SETSW1-01>（同时发出），统计各自回显耗时
                PingSwitchesParallel();

                _receiver.Start(host, port, dataFile);

                int round = 0;
                while (!_stopRequested)
                {
                    round++;
                    if (!infinite && round > times) break;


               
                    // IN × OUT 双层扫描：
                    //   - 外层 i：光开关切到 i、矩阵切到 (i,1)（两者并行），随后进入内层
                    //   - 内层 j：仅 j>1 时再切矩阵到 (i,j)（光开关已在 i，无需重切）；
                    //             j==1 时矩阵已在外层切好，不再切换。
                    //   - START 仅在首个通道 (i==1 且 j==1) 发送一次，其余通道不再发 START。
                    for (int i = inFrom; i <= inEnd && !_stopRequested; i++)
                    {
                        // 外层：光开关切到 i、矩阵切到 (i,1)，两者并行发送
                        lock (_scanLock) { _channelReady = false; }
                        SwitchBothToFirst(i);

                        for (int j = outFrom; j <= outTo && !_stopRequested; j++)
                        {
                            // 切换光路期间先置为「未就绪」，丢弃切换过程中到达的帧，避免计入本通道
                            lock (_scanLock) { _channelReady = false; }

                            if (j > 1)
                            {
                                // 仅切换矩阵到 (i,j)，光开关已在 i，无需再切
                                SwitchMatrix(i, j);
                            }
                            // j==1：矩阵已在外部切到 (i,1)，无需再切

                            // （START 已统一在握手阶段、DELAY 延时完成后由 DataReceiver 发送，此处不再发送）

                            // 光路切换已完成：开启新的通道代次。
                            // 此后入队的帧才计入本通道；队列里积压的旧帧（采集于旧光路）代次不符，
                            // 会被解析线程丢弃，避免它们顶着新通道的名义写成 IN1/OUT1。
                            _receiver.NewEpoch();

                            lock (_scanLock)
                            {
                                _curIn = i;
                                _curOut = j;
                                _framesInChannel = 0;
                                _metricsInChannel = 0;
                                // 同步当前 IN/OUT 通道给 DataReceiver，供 IL 计算取对应通道的
                                // 基准修正值 SRI/SRO/SRB/SRC（各 8 路，对应通道 1~8）。
                                // 必须与 _curIn/_curOut/_channelReady 在同一锁内一并更新：
                                // 放到锁外会让解析线程在新通道生效前取到旧值，
                                // 造成「CSV 里通道是 OUT2、SRO 却是 OUT1 的」错位。
                                _receiver.SetCurChannels(i, j);
                                _channelReady = true;
                            }

                            // 等待本通道采满「采样数」帧（带超时保护：
                            // 设备停发 / 帧数不足时若无超时，会永久卡在此处，
                            // 导致本轮无法结束、取消勾选「无限循环」后也不会自动停止）
                            int waitedMs = 0;
                            while (!_stopRequested)
                            {
                                lock (_scanLock)
                                {
                                    if (_framesInChannel >= _samplesPerChannel) break;
                                }
                                Thread.Sleep(10);
                                waitedMs += 10;
                                if (waitedMs >= channelTimeoutMs)
                                {
                                    lock (_scanLock)
                                    {
                                        AppendLog($"[警告] 通道 IN={i} OUT={j} 等待 {channelTimeoutMs} ms " +
                                                 $"仅收到 {_framesInChannel}/{_samplesPerChannel} 帧" +
                                                 $"（其中有效指标 {_metricsInChannel} 帧），跳过该通道");
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[异常] {ex.Message}");
            }
            finally
            {
                try { _receiver.Stop(); } catch { }
                try { writer?.Flush(); writer?.Dispose(); } catch { }
                _saveWriter = null;
                CloseSerialPorts();
                CloseLogFile();

                BeginInvoke(new MethodInvoker(() =>
                {
                    _startBtn.Enabled = true;
                    _stopBtn.Enabled = false;
                    _grid.SetActiveRow(-1);
                    SetStatus("空闲", Color.DarkGreen);
                }));
                AppendLog("====== 采集结束 ======");
            }
        })
        { IsBackground = true };
        _worker.Start();
    }

    private void StopTest()
    {
        _stopRequested = true;
        _stopBtn.Enabled = false;
        // 停止时立即关闭两个开关串口（worker 的 finally 会再关一次，已有 try/catch 保护）
        CloseSerialPorts();
        SetStatus("正在停止…", Color.DarkOrange);
        AppendLog("已发送停止指令，正在中断采集…");
    }

    // ---------------- 光开关 / 矩阵开关 ----------------

    /// <summary>取下拉框当前选中的串口名；为占位项「（无可用串口）」时视为未选择，返回 null。</summary>
    private static string? SelectedComPort(ComboBox cb) =>
        cb.SelectedItem is string s && s != NoComPort ? s : null;

    /// <summary>切换光开关到 IN 通道 i（&lt;SETSW1-0i&gt;），仅光开关串口。</summary>
    private void SwitchOptical(int i)
    {
        var opt = _opticalSp;
        if (opt == null) { AppendLog($"[切换] 光开关串口未打开，跳过切到 IN={i}"); return; }
        string optCmd = $"<SETSW1-0{i}>";
        AppendLog($"[切换] 光开关 → {optCmd}");
        try { lock (_spLock) SerialHelper.SendQuery(opt, optCmd, Log, "光开关"); }
        catch (Exception ex) { AppendLog($"[串口] 切换光开关异常：{ex.Message}"); }
    }

    /// <summary>切换矩阵开关 i→j（&lt;SETSW i-0j&gt;，i=IN，j=OUT），仅矩阵串口。</summary>
    private void SwitchMatrix(int i, int j)
    {
        var mat = _matrixSp;
        if (mat == null) { AppendLog($"[切换] 矩阵串口未打开，跳过切到 IN={i}/OUT={j}"); return; }
        string matCmd = $"<SETSW{i}-0{j}>";
        AppendLog($"[切换] 矩阵开关 → {matCmd}");
        try { lock (_spLock) SerialHelper.SendQuery(mat, matCmd, Log, "矩阵开关"); }
        catch (Exception ex) { AppendLog($"[串口] 切换矩阵异常：{ex.Message}"); }
    }

    /// <summary>
    /// 并行切换光开关到 i、矩阵开关到 (i,1)：两个串口各起一个任务并发发送（仅外层首通道用）。
    /// </summary>
    private void SwitchBothToFirst(int i)
    {
        var tOpt = Task.Run(() => SwitchOptical(i));
        var tMat = Task.Run(() => SwitchMatrix(i, 1));
        try { Task.WaitAll(tOpt, tMat); }
        catch (Exception ex) { AppendLog($"[串口] 外层切换异常：{ex.Message}"); }
    }

    /// <summary>开机自检/计时用的查询命令（光开关与矩阵开关均发同一条，用于统计各自回显耗时）。</summary>
    private const string SwitchPingCommand = "<IDN?>";

    /// <summary>
    /// 两个开关串口**并行**发送 &lt;IDN?&gt;：光开关与矩阵开关各起一个任务并发发送，
    /// 一次同时发两个串口，等到两者回显（或超时）。两个串口**分开计时**（各自独立的 Stopwatch），
    /// 分别统计光开关、矩阵开关各自的回显耗时，并给出并行总耗时（≈ 两者中的较大值）。
    /// 结果同时记入界面日志与日志文件。
    /// </summary>
    private void PingSwitchesParallel()
    {
        lock (_spLock)
        {
            var opt = _opticalSp;
            var mat = _matrixSp;
            if (opt == null && mat == null)
            {
                AppendLog($"[计时] 无可用开关串口，跳过 {SwitchPingCommand}");
                return;
            }

            AppendLog($"[计时] 并行发送 {SwitchPingCommand}：光开关与矩阵开关同时发出 …");

            SerialHelper.PingResult? rOpt = null, rMat = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 并行：两个串口各起一个任务同时发送，Ping 内部会等到回显或读超时才返回
                var tOpt = opt != null
                    ? Task.Run(() => rOpt = SerialHelper.Ping(opt, SwitchPingCommand, Log, "光开关"))
                    : Task.CompletedTask;
                var tMat = mat != null
                    ? Task.Run(() => rMat = SerialHelper.Ping(mat, SwitchPingCommand, Log, "矩阵开关"))
                    : Task.CompletedTask;
                Task.WaitAll(tOpt, tMat);
            }
            catch (Exception ex)
            {
                AppendLog($"[计时] {SwitchPingCommand} 异常：{ex.Message}");
                return;
            }
            sw.Stop();

            ReportPing("光开关", rOpt);
            ReportPing("矩阵开关", rMat);

            double? t1 = rOpt?.ElapsedMs;
            double? t2 = rMat?.ElapsedMs;
            if (t1.HasValue && t2.HasValue)
            {
                AppendLog($"[计时] 并行合计：max(光开关 {t1.Value:F1} ms, 矩阵开关 {t2.Value:F1} ms) = " +
                          $"{Math.Max(t1.Value, t2.Value):F1} ms（整体耗时 {sw.Elapsed.TotalMilliseconds:F1} ms）");
            }
            else
            {
                // 有一路超时无回显时，仍给出整体耗时，便于判断串口是否连通
                AppendLog($"[计时] 并行总耗时 {sw.Elapsed.TotalMilliseconds:F1} ms（至少一路无回显，单次耗时不完整）");
            }
        }
    }

    /// <summary>把单个开关的计时结果写入日志。</summary>
    private void ReportPing(string label, SerialHelper.PingResult? r)
    {
        if (r == null) { AppendLog($"[计时] {label}：未选择串口，跳过"); return; }
        var p = r.Value;
        if (!p.Sent) { AppendLog($"[计时] {label}：发送失败（{p.Error}）"); return; }
        AppendLog(p.HasEcho
            ? $"[计时] {label}：{SwitchPingCommand} 回显耗时 {p.ElapsedMs!.Value:F1} ms，回显「{p.Response}」"
            : $"[计时] {label}：{SwitchPingCommand} 超时无回显（{SerialHelper.EchoTimeoutMs} ms 内未收到数据）");
    }

    /// <summary>关闭光开关 / 矩阵开关串口并释放资源（线程安全，可重复调用）。</summary>
    private void CloseSerialPorts()
    {
        lock (_spLock)
        {
            foreach (var sp in new[] { _opticalSp, _matrixSp })
            {
                if (sp == null) continue;
                try { if (sp.IsOpen) sp.Close(); } catch { }
                try { sp.Dispose(); } catch { }
            }
            _opticalSp = null;
            _matrixSp = null;
        }
    }

    /// <summary>
    /// 写一行 CSV 记录（线程安全）。_saveWriter 由后台线程创建/销毁，
    /// 而本方法在 UI 线程（BeginInvoke 回调）中执行，故必须加锁。
    /// 写入失败时置空 writer 并提示一次，避免每帧刷屏。
    /// </summary>
    private void SaveLine(int inPort, int outPort, FrameMetrics m)
    {
        lock (_saveLock)
        {
            var w = _saveWriter;
            if (w == null) return;
            try
            {
                w.WriteLine(string.Join(',',
                    m.FrameIndex,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    inPort, outPort,
                    m.IlIn.ToString("F3"), m.IlDut.ToString("F3"), m.IlOut.ToString("F3"),
                    m.RlIn.ToString("F3"), m.RlDut.ToString("F3"), m.RlOut.ToString("F3"),
                    m.DutPath.ToString("F6"), m.LinkLen.ToString("F6"),
                    m.PeakCount,
                    // 本行 IN/OUT 通道对应的基准值（IL 修正项）：
                    // IL-DUT = 原始值 + SRO + SRI-IN + SRO-OUT；IL-IN = 原始值 + SRB-IN；IL-OUT = 原始值 + SRC-OUT
                    m.SroBase.ToString("F3"), m.SriIn.ToString("F3"), m.SroOut.ToString("F3"),
                    m.SrbIn.ToString("F3"), m.SrcOut.ToString("F3")));
                // 每 50 行刷一次盘，兼顾性能与掉电安全
                if (++_saveLineCount % 50 == 0) w.Flush();
            }
            catch (Exception ex)
            {
                _saveWriter = null;
                AppendLog($"[警告] 写入保存文件失败：{ex.Message}");
            }
        }
    }

    private int _saveLineCount;

    /// <summary>
    /// 建立独立的日志文件（与原始数据分开保存）。
    /// 优先放保存目录；未填保存路径时放在程序目录下的 logs 子目录。
    /// </summary>
    private void OpenLogFile(string? saveDir)
    {
        CloseLogFile();
        try
        {
            string dir = !string.IsNullOrEmpty(saveDir) && Directory.Exists(saveDir)
                ? saveDir
                : Path.Combine(Application.StartupPath, "logs");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, $"ofdr_log_{stamp}.txt");
            // AutoFlush=true：每行立即落盘，避免点「停止」或关闭窗口时缓冲中的尾部日志丢失
            _logWriter = new StreamWriter(path, append: false) { AutoFlush = true };
            _logFilePath = path;
            AppendLog($"日志文件：{path}");

            // 检测失败帧的单独日志：文件名带 badpeaks 标识，与主日志同目录、同时间戳、同格式
            string badPath = Path.Combine(dir, $"ofdr_log_badpeaks_{stamp}.txt");
            try
            {
                _badLogWriter = new StreamWriter(badPath, append: false) { AutoFlush = true };
                AppendLog($"检测失败日志（badpeaks）：{badPath}");
            }
            catch (Exception ex)
            {
                AppendLog($"[警告] 无法创建检测失败日志：{ex.Message}");
                _badLogWriter = null;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[警告] 无法创建日志文件：{ex.Message}");
            _logWriter = null;
            _badLogWriter = null;
        }
    }

    /// <summary>关闭日志文件（测试结束时调用）。</summary>
    private void CloseLogFile()
    {
        lock (_logLock)
        {
            try { _logWriter?.Flush(); _logWriter?.Dispose(); } catch { }
            try { _badLogWriter?.Flush(); _badLogWriter?.Dispose(); } catch { }
            _logWriter = null;
            _badLogWriter = null;
        }
    }

    /// <summary>「浏览」按钮：选择数据保存目录。</summary>
    private void BrowseSave()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择数据保存目录" };
        if (dlg.ShowDialog() == DialogResult.OK)
            _savePathBox.Text = dlg.SelectedPath;
    }

    /// <summary>解析形如 "1-8" 的区间文本，失败时回退 1~8。</summary>
    private static void ParseRange(string text, out int from, out int to)
    {
        from = 1; to = 8;
        if (string.IsNullOrWhiteSpace(text)) return;
        var parts = text.Split('-', '~', ',');
        if (parts.Length >= 2)
        {
            if (int.TryParse(parts[0].Trim(), out int a) && int.TryParse(parts[1].Trim(), out int b))
            {
                from = Math.Clamp(Math.Min(a, b), 1, 8);
                to = Math.Clamp(Math.Max(a, b), 1, 8);
                return;
            }
        }
        if (int.TryParse(text.Trim(), out int single))
        {
            from = to = Math.Clamp(single, 1, 8);
        }
    }

    // ---------------- 数据回调 ----------------
    /// <summary>解析线程每帧指标回调 → 更新当前通道行（marshal 到 UI 线程）。</summary>
    private void OnMetrics(FrameMetrics m)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() => ApplyMetrics(m)));
        }
        catch { /* 窗体已关闭 */ }
    }

    private void ApplyMetrics(FrameMetrics m)
    {
        // 通道号取自「解析线程计算本帧指标那一刻的通道快照」，而不是 UI 线程当前的 _curIn/_curOut。
        // 用后者会在切换通道的瞬间读到一个已被翻新的通道，而本帧的基准值仍是旧通道的，
        // 于是 CSV 出现「显示 OUT2、SRO 却是 OUT1 的」错位。改用同一份快照，二者必然严格对应。
        int inPort = m.InCh;
        int outPort = m.OutCh;
        if (inPort < 1 || inPort > MetricTable.GroupCount ||
            outPort < 1 || outPort > MetricTable.RowsPerGroup) return;

        bool firstOfChannel;
        int curIn, curOut;
        lock (_scanLock)
        {
            // 通道未就绪（切换延时内）：丢弃该帧，不计入统计
            if (!_channelReady) return;
            curIn = _curIn;
            curOut = _curOut;
            // 是否「本通道首条有效指标」：仅当本帧就是当前正在扫描的通道时才计入。
            // 上一通道的迟到帧不参与计数、也不重置累加器 —— 否则会把那一行已累计的数据清掉。
            bool isCurrent = inPort == curIn && outPort == curOut;
            firstOfChannel = isCurrent && _metricsInChannel == 0;
            if (isCurrent) _metricsInChannel++;
        }

        // 诊断：本帧所属通道 ≠ 当前正在扫描的通道时提示，用于定位「通道显示不对」的根因
        if (inPort != curIn || outPort != curOut)
            AppendLog($"[提示] 帧通道与当前扫描通道不一致：本帧 IN={inPort} OUT={outPort}，" +
                      $"当前已扫描到 IN={curIn} OUT={curOut}（按收帧时刻的通道记录）");

        _grid.SetActiveRow(MetricTable.RowOf(inPort, outPort));
        _grid.UpdateRow(inPort, outPort,
            m.IlIn, m.IlDut, m.IlOut,
            m.RlIn, m.RlDut, m.RlOut,
            m.DutPath, m.LinkLen,
            reset: firstOfChannel);

        SaveLine(inPort, outPort, m);
    }

    /// <summary>
    /// 解析线程每帧回调（每解析出一帧就触发一次，不要求峰值检测成功）→ 累加帧数。
    /// 通道"采满采样数"的判定以本计数为准，即"收到对应的一帧数据"即计 1 帧。
    /// </summary>
    private void OnFrame(FrameResult r)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() => CountFrame()));
        }
        catch { /* 窗体已关闭 */ }
    }

    /// <summary>UI 线程执行：本通道帧数 +1（切换延时内的帧已由 _channelReady 闸门外丢弃）。</summary>
    private void CountFrame()
    {
        lock (_scanLock)
        {
            if (!_channelReady) return;
            _framesInChannel++;
            _frameCount++;
        }
    }

    private void RefreshStat()
    {
        var now = DateTime.Now;
        double secs = (now - _lastTick).TotalSeconds;
        long bytes = Interlocked.Read(ref _receiver.TotalBytes);
        double curKBs = secs > 0 ? (bytes - _lastBytes) / secs / 1024.0 : 0;
        _lastBytes = bytes;
        _lastTick = now;

        if (_stopBtn.Enabled)
        {
            SetStatus($"运行中… 帧数 {_frameCount} | 当前 {curKBs:F0} KB/s", Color.DarkRed);
        }
    }

    private void SetStatus(string text, Color color)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, color))); return; }
        _statusLabel.Text = "状态：" + text;
        _statusLabel.ForeColor = color;
    }

    /// <summary>工作线程日志 → UI（BeginInvoke 保证线程安全）。</summary>
    private void Log(string msg) => AppendLog(msg);

    /// <summary>检测失败（检测不到 8 峰）帧的单独日志回调：内容与 ofdr_log 一致（每行都带时间戳），
    /// 写入带 badpeaks 标识的日志文件；UI 界面不重复显示，避免刷屏。
    /// 入参可能是整块多行诊断信息（本帧峰值检测详情），需按行拆分后逐行加时间戳。</summary>
    private void AppendBadLog(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        if (msg.IndexOf('\n') < 0)
        {
            WriteBadLogFile(msg);
            return;
        }
        foreach (var line in msg.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length > 0) WriteBadLogFile(t);
        }
    }

    /// <summary>日志入口（任意线程可调用）。首次调用即落盘，再 marshal 到 UI 更新界面。</summary>
    private void AppendLog(string msg) => AppendLogInternal(msg, writeFile: true);

    /// <summary>
    /// 日志内部实现。writeFile=false 用于 BeginInvoke 回调，避免同一条日志被写入文件两次
    /// （此前回调仍调用 AppendLog，导致跨线程日志在文件中重复出现两行）。
    /// </summary>
    private void AppendLogInternal(string msg, bool writeFile)
    {
        // 先写日志文件（任何线程都可调用，内部加锁；越早落盘越不易丢尾部日志）
        if (writeFile) WriteLogFile(msg);

        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => AppendLogInternal(msg, false))); return; }
            if (_logBox.TextLength > 40000) _logBox.Clear();
            _logBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { /* 窗体已关闭 */ }
    }

    /// <summary>把一行日志写入独立日志文件（线程安全；无日志文件时静默跳过）。</summary>
    private void WriteLogFile(string msg)
    {
        lock (_logLock)
        {
            var w = _logWriter;
            if (w == null) return;
            try
            {
                w.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
            }
            catch
            {
                _logWriter = null;   // 写入失败则停用，避免每帧抛异常
            }
        }
    }

    /// <summary>把一行检测失败信息写入 badpeaks 日志文件（与主日志同格式；无文件时静默跳过）。</summary>
    private void WriteBadLogFile(string msg)
    {
        lock (_logLock)
        {
            var w = _badLogWriter;
            if (w == null) return;
            try
            {
                w.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
            }
            catch
            {
                _badLogWriter = null;   // 写入失败则停用，避免每帧抛异常
            }
        }
    }

    /// <summary>
    /// 输入框失焦（Leave）时即时校验：解析失败或超出 [min,max] 立即弹窗报错，
    /// 弹窗关闭后焦点回到原输入框并全选文本，便于直接修改。
    /// </summary>
    private void CheckRangeInput(TextBox box, double min, double max, string name)
    {
        bool bad = false;
        string? msg = null;
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            bad = true;
            msg = $"{name}「{box.Text}」无法解析为数值（须为 {min}~{max}）";
        }
        else if (v < min || v > max)
        {
            bad = true;
            msg = $"{name} {v} 超界（须 {min}~{max}）";
        }

        if (bad)
        {
            MessageBox.Show(msg, "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            // 弹窗关闭后焦点拉回原输入框并全选，方便直接修改
            BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }));
        }
    }
}
