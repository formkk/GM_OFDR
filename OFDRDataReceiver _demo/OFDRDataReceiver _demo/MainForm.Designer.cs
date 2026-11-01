namespace OFDRDataReceiver;

partial class MainForm
{
    /// <summary>设计器变量（由 Windows 窗体设计器维护）。</summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>清理所有正在使用的资源。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
            components.Dispose();
        base.Dispose(disposing);
    }

    #region Windows 窗体设计器生成的代码

    /// <summary>
    /// 设计器支持所需的方法 —— 布局由本方法维护，可在 VS 设计器中可视化编辑。
    /// 布局约定：
    ///   _topPanel(56) → _ctrlPanel(78) → _grid(Dock.Fill) → _bottomPanel(220)
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        // ---------- 1) 标题栏 ----------
        _topPanel = new Panel { Dock = DockStyle.Top, Size = new Size(1920, 56), Padding = new Padding(8) };
        _titleLabel = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei", 14F, FontStyle.Bold),
            Text = "OFDR 扫描测试界面【新布局v9】",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _topPanel.Controls.Add(_titleLabel);

        // ---------- 2) 控制参数区 ----------
        // 改为 FlowLayoutPanel：控件位置由面板在运行时计算，不再硬编码 X 坐标。
        // 窗口变窄时自动换行（WrapContents），字段宽度按标签实际宽度自适应（见 Field），
        // 因此在 1366×768 / 1920×1080 / 2560×1600 及任意 DPI 缩放下都不会重叠。
        _ctrlPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8),
        };

        _testAllChannels = new CheckBox { AutoSize = true, Checked = true, Text = "测试所有通道", Margin = new Padding(0, 8, 12, 4) };
        _infiniteLoop = new CheckBox { AutoSize = true, Checked = false, Text = "无限循环", Margin = new Padding(0, 8, 12, 4) };
        _enablePeak = new CheckBox { AutoSize = true, Checked = true, Text = "峰值检测", Margin = new Padding(0, 8, 12, 4) };

        _channelRange = new TextBox { Size = new Size(110, 23), Text = "1-8" };
        _testTimes = new TextBox { Size = new Size(80, 23), Text = "1" };
        _sampleCount = new TextBox { Size = new Size(80, 23), Text = "1" };
        _rlThreshold = new TextBox { Size = new Size(90, 23), Text = "-77.5" };
        // 标记点 / 窗口（浮点，行号量纲）：开始位置=A点行号、终止位置=D点行号、窗口宽度=局部平均半窗
        _startPos = new TextBox { Size = new Size(100, 23), Text = "4.5" };
        _endPos = new TextBox { Size = new Size(100, 23), Text = "10.5" };

        // 链路长度系数（每行对应的米数，默认 0.0002）：影响链路长度与 DUT 光程换算
        // 注：该控件不加入面板（界面不显示），仅保留配置与失焦校验逻辑
        _linkLenCoeff = new TextBox { Size = new Size(90, 23), Text = "0.0002" };
        // 失焦即时校验：超出范围立即弹窗，无需点击「开始测试」
        _startPos.Leave += (_, _) => CheckRangeInput(_startPos, 1.0, 16.0, "开始位置");
        _endPos.Leave += (_, _) => CheckRangeInput(_endPos, 1.0, 16.0, "终止位置");

        // 积分窗口宽度（cm）：以 cm 输入，换算为 AvgHalfWindow 行数（行 = cm / 0.04，10cm → 半窗 250 行）
        _avgHalfWindow = new TextBox { Size = new Size(110, 23), Text = "10" };
        _avgHalfWindow.Leave += (_, _) => CheckRangeInput(_avgHalfWindow, DataReceiver.IntegralWindowCmMin, DataReceiver.IntegralWindowCmMax, "积分窗口宽度");
        _linkLenCoeff.Leave += (_, _) => CheckRangeInput(_linkLenCoeff, 0.00001, 0.01, "链路长度系数");

        _ctrlPanel.Controls.AddRange(new Control[]
        {
            _testAllChannels, _infiniteLoop, _enablePeak,
            Field("通道范围", _channelRange, 110),
            Field("测试次数", _testTimes, 80),
            Field("采样数", _sampleCount, 80),
            Field("RL门限", _rlThreshold, 90),
            Field("开始位置(A m)", _startPos, 100),
            Field("终止位置(D m)", _endPos, 100),
            Field("积分窗口宽度（cm）", _avgHalfWindow, 110),
        });

        // ---------- 3) 指标表格 ----------
        _grid = new MetricTable();

        // ---------- 4) 底栏 ----------
        // 改为 AutoSize：底栏高度随内部连接面板（含按钮行）自动伸缩，
        // 即便高 DPI 下字段换行导致连接面板变高，也不会裁掉「浏览 / 开始测试 / 停止」按钮。
        _bottomPanel = new Panel { Dock = DockStyle.Bottom, Padding = new Padding(8), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        // 连接面板同样改为流式布局：位置运行时计算，宽度不足自动换行
        _connPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 52,               // 固定单行高度（随 DPI 缩放），避免高 DPI 行高测距错误裁掉按钮
            WrapContents = false,      // 不自动换行：字段过多时横向滚动，而不是把按钮挤掉/裁掉
            FlowDirection = FlowDirection.LeftToRight,
            AutoScroll = true,
            Padding = new Padding(8, 4, 8, 4),
        };

        _hostBox = new TextBox { Size = new Size(106, 23), Text = DefaultHost };
        _portBox = new TextBox { Size = new Size(70, 23), Text = DefaultPort.ToString() };
        // 保存路径：端口右侧（留空 = 不保存）
        _savePathBox = new TextBox { Size = new Size(330, 23) };
        // 按钮先创建（Margin 清零，统一交给 ButtonField 包进同高面板，
        // 避免直接用 Margin.Top 下移对齐输入框导致 FlowLayoutPanel 高度算错、按钮底部被裁一半）
        _browseBtn = new Button { Size = new Size(70, 26), Text = "浏览", Margin = new Padding(0) };
        _browseBtn.Click += (_, _) => BrowseSave();

        _startBtn = new Button { Size = new Size(92, 26), Text = "开始测试", Margin = new Padding(0) };
        _stopBtn = new Button { Size = new Size(92, 26), Text = "停止", Enabled = false, Margin = new Padding(0) };
        _startBtn.Click += (_, _) => StartTest();
        _stopBtn.Click += (_, _) => StopTest();

        // ---- 光开关地址 / 光开关矩阵地址：列出所有可用串口供选择 ----
        _opticalPort = new ComboBox { Size = new Size(120, 23), DropDownStyle = ComboBoxStyle.DropDownList };
        _matrixPort = new ComboBox { Size = new Size(140, 23), DropDownStyle = ComboBoxStyle.DropDownList };

        _connPanel.Controls.AddRange(new Control[]
        {
            Field("设备地址", _hostBox, 106),
            Field("端口", _portBox, 70),
            Field("保存路径(空=不保存)", _savePathBox, 330),
            ButtonField(_browseBtn),
            Field("光开关地址", _opticalPort, 120),
            Field("光开关矩阵地址", _matrixPort, 140),
            ButtonField(_startBtn), ButtonField(_stopBtn),
        });

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
            ForeColor = Color.DarkGreen,
            Text = "状态：空闲",
        };
        _logBox = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 160,
            Font = new Font("Consolas", 9F),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
        };

        // 注意 Controls.Add 顺序：WinForms 中多个 Dock.Top 控件按「后添加→先布局」排列，
        // 即最后加入 Controls 的在最上面。期望顺序（自上而下）：连接面板（含按钮）→ 状态 → 日志。
        // 所以日志要先加、连接面板要最后加，否则日志会被顶到最上面、按钮反而被挤到底部。
        _bottomPanel.Controls.Add(_logBox);
        _bottomPanel.Controls.Add(_statusLabel);
        _bottomPanel.Controls.Add(_connPanel);

        // ---------- 窗体 ----------
        Text = "OFDR 扫描测试界面【新布局v9】";
        // AutoScaleMode.Dpi：配合 PerMonitorV2，控件与字体随系统 DPI 等比缩放（文字清晰、不错位）
        AutoScaleMode = AutoScaleMode.Dpi;
        // AutoScroll：兜底，窗口被拖到比最小布局还小时可滚动，控件不会被裁掉
        AutoScroll = true;
        ClientSize = new Size(1920, 1000);
        // 最小尺寸放宽到 1100×700：窄屏/高 DPI（逻辑分辨率变小）下字段自动换行仍可用
        MinimumSize = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei", 9F);

        // 注意 Controls.Add 顺序：后添加的先布局。Dock.Fill 的 _grid 必须先添加，
        // 否则会被 Top/Bottom 面板挤占（WinForms  docking 按 Z 序反向填充）。
        Controls.Add(_grid);
        Controls.Add(_ctrlPanel);
        Controls.Add(_topPanel);
        Controls.Add(_bottomPanel);
    }

    /// <summary>在指定位置创建一个自动尺寸的标签（表头说明文字）。</summary>
    private static Label Lbl(int x, int y, string text) =>
        new() { Location = new Point(x, y), AutoSize = true, Text = text };

    /// <summary>
    /// 生成「标签在上、输入控件在下」的组合面板，供 FlowLayoutPanel 自动排布。
    /// 关键点：面板宽度取「输入框宽度」与「标签实际宽度」的<b>较大者</b> ——
    /// 高 DPI 下标签会被放大，若仍按固定宽度排布，标签会压到右侧相邻字段上（此前重叠的根因）；
    /// 取较大值后每个字段都容得下自己的标签，配合面板 Margin 即可保证任意缩放下都不重叠。
    /// </summary>
    private static Panel Field(string label, Control input, int width)
    {
        var lab = new Label { Text = label, AutoSize = true, Location = new Point(0, 0) };
        int w = Math.Max(width, lab.PreferredWidth);   // 容纳标签，避免压到相邻字段
        input.Location = new Point(0, lab.PreferredHeight + 2);
        input.Width = width;
        var p = new Panel
        {
            Size = new Size(w, input.Bottom + 2),
            Margin = new Padding(0, 0, 20, 4),   // Right=20 即字段之间的水平间距
        };
        p.Controls.Add(lab);
        p.Controls.Add(input);
        return p;
    }

    /// <summary>
    /// 把按钮包进与 Field 同高（约 45px）的面板，使连接面板内所有项高度一致，
    /// FlowLayoutPanel 才能正确算出总高、AutoSize 不再裁切按钮。
    /// 按钮在面板内与左侧字段的输入框顶边对齐（y≈17），保持视觉一致。
    /// （早期用 Margin.Top 直接下移按钮，会因 FlowLayoutPanel 高度计算不含顶部留白而把按钮底部裁掉一半。）
    /// </summary>
    private static Panel ButtonField(Button btn)
    {
        btn.Margin = new Padding(0);
        var p = new Panel
        {
            Size = new Size(btn.Width + 8, 52),  // 与连接面板等高，按钮顶部对齐，避免被裁底
            Margin = new Padding(0, 0, 20, 4),   // 与 Field 一致的水平间距
        };
        btn.Location = new Point(4, 4);          // 顶部对齐：即使行高偶尔算小也不会裁到按钮底部
        p.Controls.Add(btn);
        return p;
    }

    #endregion

    // 布局容器
    private Panel _topPanel;
    private Label _titleLabel;
    private Panel _ctrlPanel;
    private Panel _bottomPanel;
    private Panel _connPanel;

    // 表格 / 状态 / 日志
    private MetricTable _grid;
    private Label _statusLabel;
    private TextBox _logBox;

    // 连接设置
    private TextBox _hostBox;
    private TextBox _portBox;
    private TextBox _savePathBox;
    private Button _browseBtn;
    private Button _startBtn;
    private Button _stopBtn;
    private ComboBox _opticalPort;      // 光开关地址（串口）
    private ComboBox _matrixPort;       // 光开关矩阵地址（串口）

    // 控制参数（顶部）
    private CheckBox _testAllChannels;
    private CheckBox _infiniteLoop;
    private CheckBox _enablePeak;
    private TextBox _channelRange;
    private TextBox _testTimes;
    private TextBox _sampleCount;
    private TextBox _rlThreshold;

    private TextBox _startPos;
    private TextBox _endPos;

    private TextBox _avgHalfWindow;
    private TextBox _linkLenCoeff;
}
