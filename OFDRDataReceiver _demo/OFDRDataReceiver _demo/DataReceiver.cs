using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OFDRDataReceiver;

/// <summary>单帧解析结果（解析线程产出，回调给 UI）。</summary>
public readonly record struct FrameResult(
    long FrameIndex, long Lines, double SumMw, double MaxDbm, double MinDbm, double ElapsedMs);

/// <summary>
/// 单帧的完整测量指标（解析线程产出，回调给 UI 用于填表）。
/// 包含标记点行号、六项 IL/RL 指标、链路长度与 DUT 光程。
/// </summary>
public readonly record struct FrameMetrics(
    long FrameIndex,
    double A, double B, double BB, double CC, double C, double D, double E,   // 标记点行号（已取整）
    double IlIn, double IlDut, double IlOut,                                   // IL 三项（dB）
    double RlIn, double RlDut, double RlOut,                                   // RL 三项（dBm）
    double LinkLen, double DutPath,                                            // 链路长度 / DUT 光程
    int InCh, int OutCh,                                                       // 本帧指标所用的 IN/OUT 通道（与下方基准值取自同一次原子快照，保证严格对应）
    double SroBase, double SriIn, double SroOut, double SrbIn, double SrcOut,   // 本帧所用通道的基准值（IL 修正项，供 CSV 逐通道核对）
    int PeakCount,                                                             // 本帧检出的峰值数
    double PeakMs, double MetricMs);                                           // 峰值检测耗时 / 六项计算耗时

/// <summary>一帧原始数据 + 到达信息（接收线程产出，交给解析线程消费）。</summary>
internal sealed class FrameData
{
    public byte[] Bytes;
    public long Epoch;          // 本帧「入队时刻」的通道代次（用于丢弃切换前/切换中的残留帧）
    public int InCh, OutCh;     // 本帧「入队时刻」的通道（收帧瞬间生效的光路，比解析时刻更贴近该帧真实所属通道）
    public double IntervalMs;   // 距上一帧 '!' 的帧周期（ms），第一帧为 0
    public double GapMs;        // 帧间隙：上一帧 '!' → 本帧首字节（ms），第一帧为 0
    public double RecvMs;       // 本帧接收耗时：首字节 → '!'（ms），第一帧为 0
    public double ScanMs;       // 本帧所在那次缓冲扫描的耗时（ms，粘包多帧共享同一次扫描）
    public FrameData(byte[] bytes, long epoch, int inCh, int outCh,
                     double intervalMs, double gapMs, double recvMs, double scanMs)
    {
        Bytes = bytes;
        Epoch = epoch;
        InCh = inCh;
        OutCh = outCh;
        IntervalMs = intervalMs;
        GapMs = gapMs;
        RecvMs = recvMs;
        ScanMs = scanMs;
    }
}

/// <summary>
/// 接收线程 + 解析线程（生产者/消费者）：
///   - 接收线程：Read → 字节累积缓冲 → 按 '!' 切出完整帧（同时用 Stopwatch 记录到达时刻，计算帧间间隔）→ 入有界队列（背压，接收永不停顿）
///   - 解析线程：取帧 → 打印帧间间隔 → 整帧一次 ASCII 解码 → 逐行 double.TryParse(dBm) → 求 mW 总和 → 回调
///
/// 协议：连接 → 收 "OSI" → 发 "START" → 持续收帧（每帧以 '!' 结束）→ 停止时发 "STOP" 并断开。
/// 设备特性：约每 70ms 一帧、每帧约 800KB（5ms 内突发），故接收与解析必须解耦。
/// </summary>
public sealed class DataReceiver : IDisposable
{
    private const byte FrameEnd = (byte)'!';                      // 帧结束符
    private const int MaxFrameBytes = 900 * 1024;                 // 单帧上限（约 800KB + 余量）
    private const int QueueCapacity = 8;                          // 有界队列：8 帧 × 800KB ≈ 6.4MB 背压缓冲
    // 底噪按实测标定：设备实测帧内最低值约 -125 dBm（日志 dBm 范围下限 -122 ~ -131）。
    // 原先假设 -100 导致阈值 -80 高于信号峰值（-82 ~ -86），所有点过不了阈值 → 检出 0 个峰。
    // 以下 9 个峰值检测参数从外部 config.txt 读取（key=value，# 开头为注释），文件缺省时回落到下列默认值。
    private static double NoiseFloorDbm = -125.0;             // 实测底噪（帧内最低值，按设备标定）
    private static double PeakMarginDb = 20.0;                // 高于底噪的余量：阈值 = -105dBm（比底噪高 20dB ≈ 100 倍功率）
    private static int PeakMinDistance = 10;                  // 最小峰间距（行）：紧邻事件是否独立/旁瓣，配合幅差判断
    private static double PeakRelDropDb = 3.0;               // 紧邻峰幅差阈值：间距<10 内两峰幅差 >3dB → 弱峰视为强峰旁瓣，丢弃
    // 链路长度 / DUT 光程换算系数（每行对应的米数），原为固定常量，现改为可由 GUI 输入的实例属性（默认 0.0002）
    public double LinkLenPerRow { get; set; } = 0.0002;       // 链路长度系数：链路长度 = E 点行 × 此值
    public double DutPathPerRow { get; set; } = 0.0002;       // DUT 光程系数：DUT光程 = (C 点 - B 点) 行 × 此值
    // 以下三项原为固定常量，现改为可由 GUI「开始位置/终止位置/窗口宽度」覆盖的实例属性（默认值保持原值）
    public double MarkARow { get; set; } = 2500;             // A 点行号（开始位置），默认落第3、4峰之间
    public double MarkDRow { get; set; } = 32500;            // D 点行号（终止位置），默认落第7、8峰之间
    public int AvgHalfWindow { get; set; } = 250;           // 局部平均半窗（窗口宽度）：中心 ±N 行
    private static int PeakCentroidHalfWindow = 5;            // 质心计算半窗（行）：±5 行共 11 点参与能量重心（<最小峰间距 10，不会串到邻峰）
    private static int PeakLocalWindow = 100;                 // 局部旁瓣过滤窗口（行）：检查 ±100 行内是否有显著更高的峰
    private static double PeakLocalDropDb = 8.0;              // 局部幅差阈值：比窗口内最高峰低 >8dB → 判为旁瓣，丢弃
    private static int MinPeakWidth = 1;                      // 峰宽下限（行）：3dB 半高宽小于此值视为毛刺，剔除（1=不滤，点反射峰常仅 1~2 行宽）
    private static int MaxPeakWidth = 100;                    // 峰宽扩展上限（行）：防病态平台数据 O(n²)
    private static double PeakThresholdDb = -77.5;             // 简化峰值检测阈值：数值超过此值（dBm）的连续段即视为一个峰
    private static int PeakMergeGap = 50;                      // 相邻过阈段的间隔（未过阈行数）≤ 此值则合并为一个峰；> 此值视为两个独立峰

    // 以下各项原在 GUI 中配置，现统一移至 config.txt（GUI 输入框已移除）
    public static int ChannelTimeoutMs { get; private set; } = 10000;   // 单通道等待采满「采样数」帧的超时（ms），超时跳过该通道
    public static double IntegralWindowCm { get; private set; } = 1.0;     // 积分窗口宽度（cm），换算为 AvgHalfWindow 行数：行 = cm / 0.04（10cm → 半窗 250 行）
    public static double IntegralWindowCmMin { get; private set; } = 0.1;  // 积分窗口宽度下限（cm），运行时校验用（config 单独配置）
    public static double IntegralWindowCmMax { get; private set; } = 100.0; // 积分窗口宽度上限（cm），运行时校验用（config 单独配置）

    // 波峰检查（旁瓣过滤）窗口宽度（cm）：换算为 PeakLocalWindow 行数 = cm / 0.02（物理行距 0.0002 m/行 = 0.02 cm/行；1cm → 50 行）
    public static double WindowWidthCm { get; private set; } = 1.0;     // 波峰检查窗口宽度（cm），用于 PeakLocalWindow
    public static double WindowWidthCmMin { get; private set; } = 0.1;  // 波峰检查窗口下限（cm），运行时校验用（config 单独配置）
    public static double WindowWidthCmMax { get; private set; } = 5.0;  // 波峰检查窗口上限（cm），运行时校验用（config 单独配置）

    // 文件保存权限：保存路径非空 且 此处开启 才保存对应文件
    public static bool SaveLines { get; private set; } = true;     // 是否保存 ofdr_lines_{stamp}.txt（逐帧数值行）
    public static bool SaveMetrics { get; private set; } = true;   // 是否保存 ofdr_metrics_{stamp}.csv（指标）
    public static bool SaveRaw { get; private set; } = true;        // 是否保存 ofdr_raw_{stamp}.dat（原始数据）
    public static bool SaveBadPeaks { get; private set; } = true;   // 是否保存 ofdr_badpeaks_{stamp}.txt（未检出 8 峰的帧，单独记录便于排查）

    public static int DelayMs { get; private set; } = 100;          // 收到 OSI 后发送的 DELAY_xxx 延迟值（ms），xxx 可由 config 修改
    public static int StartSettleMs { get; private set; } = 5000;   // START 发送后的固定等待（ms），可由 config 的 StartSettleMs 配置

    // ---- 基准/参考值：SRI / SRO / SRB / SRC 各 8 路（通道 1~8），初值均为 0 ----
    // 数组长度 9，下标 1~8 对应通道 1~8（下标 0 未使用），便于直接用通道号访问：Sri[ch]。
    // 由 config.txt 的 SRI-1..8 / SRO-1..8 / SRB-1..8 / SRC-1..8 配置。
    public static double[] Sri { get; private set; } = new double[9];
    public static double[] Sro { get; private set; } = new double[9];
    public static double[] Srb { get; private set; } = new double[9];
    public static double[] Src { get; private set; } = new double[9];

    // 标量基准 SRO：config 键名为 "SRO"（不带通道后缀），初值 0。
    // 注意与上面的通道数组区分，避免混淆：
    //   SRO      → SroBase（标量）
    //   SRO-1..8 → Sro[1..8]（数组）
    // （C# 标识符区分大小写，若也命名为 SRO 会与数组 Sro 仅差大小写，极易写错，故用 SroBase。）
    public static double SroBase { get; private set; } = 0;

    private static bool _configLoaded;
    /// <summary>从程序目录下的 config.txt 读取峰值检测及 GUI 参数；格式 key=value（# 开头为注释）。解析失败的项保留默认值。</summary>
    private static void EnsureConfigLoaded()
    {
        if (_configLoaded) return;
        _configLoaded = true;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.txt");
            if (!File.Exists(path)) return;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();
                double d;
                int n;
                // 基准/参考值：SRI-1..8 / SRO-1..8 / SRB-1..8 / SRC-1..8（优先于下方 switch 处理）
                if (TrySetRefValue(key, val)) continue;
                switch (key)
                {
                    case "NoiseFloorDbm": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) NoiseFloorDbm = d; break;
                    case "PeakMarginDb": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) PeakMarginDb = d; break;
                    case "PeakMinDistance": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) PeakMinDistance = n; break;
                    case "PeakRelDropDb": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) PeakRelDropDb = d; break;
                    case "PeakCentroidHalfWindow": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) PeakCentroidHalfWindow = n; break;
                    case "PeakLocalWindow": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) PeakLocalWindow = n; break;
                    case "PeakLocalDropDb": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) PeakLocalDropDb = d; break;
                    case "MinPeakWidth": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) MinPeakWidth = n; break;
                    case "MaxPeakWidth": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) MaxPeakWidth = n; break;
                    case "PeakThresholdDb": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) PeakThresholdDb = d; break;
                    case "PeakMergeGap": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) PeakMergeGap = n; break;
                    case "ChannelTimeoutMs": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ChannelTimeoutMs = n; break;
                    case "IntegralWindowCm": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) IntegralWindowCm = d; break;
                    case "IntegralWindowCmMin": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) IntegralWindowCmMin = d; break;
                    case "IntegralWindowCmMax": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) IntegralWindowCmMax = d; break;
                    case "WindowWidthCm": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) WindowWidthCm = d; break;
                    case "WindowWidthCmMin": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) WindowWidthCmMin = d; break;
                    case "WindowWidthCmMax": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) WindowWidthCmMax = d; break;
                    case "SaveLines": if (bool.TryParse(val, out var sl)) SaveLines = sl; break;
                    case "SaveMetrics": if (bool.TryParse(val, out var sm)) SaveMetrics = sm; break;
                    case "SaveRaw": if (bool.TryParse(val, out var sr)) SaveRaw = sr; break;
                    case "SaveBadPeaks": if (bool.TryParse(val, out var sbp)) SaveBadPeaks = sbp; break;
                    case "DelayMs": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) DelayMs = n; break;
                    case "StartSettleMs": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) StartSettleMs = n; break;
                    // 标量基准 SRO（不带通道后缀，区别于 SRO-1..8，后者由 TrySetRefValue 处理）
                    case "SRO": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) SroBase = d; break;
                }
            }
        }
        catch
        {
            // 解析失败则全部使用默认值，不影响运行
        }
    }

    /// <summary>
    /// 强制重新读取 config.txt（清除已加载标志后调用 EnsureConfigLoaded）。
    /// 在每次测试开始时调用，使本次运行使用最新配置文件，而非沿用上次加载的缓存值。
    /// </summary>
    public static void ReloadConfig()
    {
        _configLoaded = false;
        EnsureConfigLoaded();
    }

    /// <summary>
    /// 解析基准/参考值配置项：键名形如 "SRI-3"（前缀 SRI / SRO / SRB / SRC + "-" + 通道号 1~8）。
    /// 写入对应数组的下标 [通道号]；解析成功返回 true（调用方应 continue，不再走通用 switch）。
    /// </summary>
    private static bool TrySetRefValue(string key, string val)
    {
        int dash = key.IndexOf('-');
        if (dash <= 0) return false;

        double[] arr;
        switch (key.Substring(0, dash).ToUpperInvariant())
        {
            case "SRI": arr = Sri; break;
            case "SRO": arr = Sro; break;
            case "SRB": arr = Srb; break;
            case "SRC": arr = Src; break;
            default: return false;
        }
        if (!int.TryParse(key.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch))
            return false;
        if (ch < 1 || ch > 8) return false;
        if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return false;

        arr[ch] = v;
        return true;
    }

    /// <summary>取某通道（1~8）的基准值；通道号越界或数组异常时返回 0（不抛异常、不影响计算）。</summary>
    private static double RefAt(double[] arr, int ch) =>
        (ch >= 1 && ch <= 8 && ch < arr.Length) ? arr[ch] : 0.0;

    private readonly Action<string> _log;                         // 日志回调（调用方负责 marshal 到 UI）
    private readonly Action<FrameResult>? _onFrame;               // 每帧解析完成回调（解析线程触发）
    private readonly Action<FrameMetrics>? _onMetrics;            // 每帧完整指标回调（解析线程触发，用于填表）
    private readonly Action<string>? _onBadLog;                  // 检测不到 8 峰的帧信息回调（仅失败帧，便于单独排查）
    private Stream? _rawWriter;                                   // 原始帧落盘流（仅解析线程访问；Stop 时关闭）
    private StreamWriter? _linesWriter;                            // 逐帧有效数值行落盘流（行号 == vals 的 Index，便于与波峰对照；Stop 时关闭）
    private StreamWriter? _badPeaksWriter;                          // 未检出 8 峰的帧落盘流（单独保存便于排查；Stop 时关闭）

    private TcpClient? _client;
    private NetworkStream? _stream;
    // 注意：BlockingCollection 一旦 CompleteAdding 便不能复用，每次 Start 必须重建（非 readonly）
    private BlockingCollection<FrameData> _frames =
        new(new ConcurrentQueue<FrameData>(), boundedCapacity: QueueCapacity);
    private CancellationTokenSource? _cts;
    private Thread? _recvThread;
    private Thread? _parseThread;

    private readonly byte[] _readBuf = new byte[256 * 1024];      // 网络读缓冲
    private readonly byte[] _pending = new byte[MaxFrameBytes * 3]; // 累积缓冲（≥3 帧容量）
    private int _pendingInitLen;                                  // 握手后遗留的初始字节数

    // 帧间隔统计（仅解析线程访问）
    private long _intervalCount;
    private double _minIntervalMs = double.MaxValue;
    private double _maxIntervalMs;
    private double _sumIntervalMs;

    // 帧间隙（上帧'!'→本帧首字节）统计（仅解析线程访问）
    private long _gapCount;
    private double _minGapMs = double.MaxValue;
    private double _maxGapMs;
    private double _sumGapMs;

    // 帧接收耗时（首字节→'!'）统计（仅解析线程访问）
    private long _recvCount;
    private double _minRecvMs = double.MaxValue;
    private double _maxRecvMs;
    private double _sumRecvMs;

    // 缓冲扫描耗时（逐字节找 '!'）统计（仅接收线程访问，结束汇总打印）
    private long _scanCount;
    private double _minScanMs = double.MaxValue;
    private double _maxScanMs;
    private double _sumScanMs;

    // 峰值统计（仅解析线程访问）
    private long _peakFrames;
    private long _peakSum;
    private double _minPeakMs = double.MaxValue;   // 峰值检测耗时统计
    private double _maxPeakMs;
    private double _sumPeakMs;
    private double _minMetricMs = double.MaxValue; // IL/RL 六项计算耗时统计
    private double _maxMetricMs;
    private double _sumMetricMs;
    private long _metricFrames;

    /// <summary>
    /// 是否启用峰值检测与 IL/RL 指标计算。关闭后只做接收 + 逐行 dBm→mW 解析
    /// （仍会打印每帧数据行数与 dBm 范围，并回调 onFrame），
    /// 跳过峰值/标记点/六项计算，不回调 onMetrics、不填指标表格。
    /// 用于单独验证数据链路，避免峰值算法参数未标定时干扰排查。
    /// </summary>
    public bool EnablePeakDetection { get; set; } = true;

    /// <summary>
    /// 简化峰值检测阈值（dBm）：数值超过此值的连续段即视为一个峰。
    /// 默认取 config.txt 的 PeakThresholdDb；运行时可由 GUI 的「RL门限」覆盖（在 Start 前设置即可）。
    /// </summary>
    public double PeakThresholdDbm { get; set; } = -77.5;

    // 当前 IN/OUT 通道打包为一个 long（高 32 位 = IN，低 32 位 = OUT），一次性原子读写。
    // 必须打包：解析线程要同时取 IN 与 OUT 来查基准值，若分两次读独立属性，
    // 可能读到「IN 已更新、OUT 仍是旧值」的半新半旧组合，导致本帧 SRI/SRO/SRB/SRC 取错通道。
    private long _curChPacked = PackChannels(1, 1);

    private static long PackChannels(int inCh, int outCh) => ((long)inCh << 32) | (uint)outCh;

    /// <summary>当前扫描的 IN 通道（1~8），由界面切换通道时设置。</summary>
    public int CurInCh => (int)(Interlocked.Read(ref _curChPacked) >> 32);

    /// <summary>当前扫描的 OUT 通道（1~8），由界面切换通道时设置。</summary>
    public int CurOutCh => (int)(Interlocked.Read(ref _curChPacked) & 0xFFFFFFFF);

    /// <summary>
    /// 原子设置当前 IN/OUT 通道。必须在 _scanLock 内、与 _curIn/_curOut/_channelReady 一并更新：
    /// 放到锁外会让解析线程在新通道生效前取到旧值，出现「CSV 显示 OUT2、SRO 却是 OUT1 的」错位。
    /// </summary>
    public void SetCurChannels(int inCh, int outCh) =>
        Interlocked.Exchange(ref _curChPacked, PackChannels(inCh, outCh));

    /// <summary>一次性原子读取当前 (IN, OUT) 通道快照，确保两者来自同一次更新。</summary>
    public (int InCh, int OutCh) GetCurChannels()
    {
        long p = Interlocked.Read(ref _curChPacked);
        return ((int)(p >> 32), (int)(p & 0xFFFFFFFF));
    }

    /// <summary>网络层累计接收字节数（多线程安全：Interlocked 读写）。</summary>
    public long TotalBytes;

    // ---- 通道「代次」：用于丢弃切换残留帧 ----
    // 设备连续发帧、接收队列可缓存多帧，切换通道时队列里往往还压着采集于旧光路的帧。
    // 这些残留帧若被解析，会带着「解析时刻」的通道号计入新通道，表现为第 2、3 个通道仍显示 IN1/OUT1。
    // 做法：接收线程给每帧打上入队时刻的代次；切换完成后递增代次；
    // 解析线程只处理代次与当前一致的帧，其余视为残留帧丢弃（不计入任何统计、不触发回调）。
    private long _epoch = 1;
    private long _lastDropEpoch = -1;

    /// <summary>开启新的通道代次：此后入队的帧才计入新通道。应在光路切换完成后调用。</summary>
    public long NewEpoch() => Interlocked.Increment(ref _epoch);

    /// <summary>当前通道代次。</summary>
    public long CurrentEpoch => Interlocked.Read(ref _epoch);

    /// <summary>已解析帧数（多线程安全：Interlocked 读写）。</summary>
    public long FrameCount;

    public bool IsRunning => _client is { Connected: true };

    public DataReceiver(Action<string> log, Action<FrameResult>? onFrame = null,
        Action<FrameMetrics>? onMetrics = null, Action<string>? onBadLog = null)
    {
        _log = log;
        _onFrame = onFrame;
        _onMetrics = onMetrics;
        _onBadLog = onBadLog;
        EnsureConfigLoaded();
        PeakThresholdDbm = PeakThresholdDb;   // 默认沿用 config.txt 的阈值
    }

    /// <summary>
    /// 连接 + 握手 OSI + 发 START，然后启动接收线程与解析线程。
    /// 该方法为同步阻塞式（含 Connect 与 OSI 等待），应放到后台任务中调用，避免卡 UI。
    /// rawFile：原始帧数据保存文件（null = 不保存）。写入的是网口收到的原始字节（每帧以 '!' 结尾，原样落盘）。
    /// </summary>
    public void Start(string host, int port, string? rawFile = null)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 测试开始：重新读取 config.txt，确保本次运行使用最新配置（而非上次加载的缓存）
        ReloadConfig();
        _log("已读取 config.txt 配置（本次参数以最新文件为准）");

        // 波峰检查窗口（旁瓣过滤）：config 的 WindowWidthCm(cm) → PeakLocalWindow(行)。
        // 物理行距 0.0002 m/行 = 0.02 cm/行，故 行 = cm / 0.02（1cm → 50 行）。
        double winCm = WindowWidthCm;
        if (winCm < WindowWidthCmMin || winCm > WindowWidthCmMax)
            _log($"[警告] 波峰检查窗口宽度 {winCm} cm 超界（须 {WindowWidthCmMin}~{WindowWidthCmMax} cm），已钳制");
        winCm = Math.Clamp(winCm, WindowWidthCmMin, WindowWidthCmMax);
        PeakLocalWindow = (int)Math.Round(winCm / 0.02);
        _log($"波峰检查窗口 PeakLocalWindow = {PeakLocalWindow} 行（由 config WindowWidthCm = {winCm:F2} cm 换算，1cm → 50 行）");

        // 原始数据落盘流（解析线程独占写入；Stop 时关闭）
        if (!string.IsNullOrEmpty(rawFile))
        {
            // 原始数据落盘流（解析线程独占写入；Stop 时关闭）。
            // 仅在保存路径非空（rawFile 有效）且 config 开启 SaveRaw 时保存。
            if (SaveRaw)
            {
                try
                {
                    _rawWriter = new FileStream(rawFile, FileMode.Create, FileAccess.Write, FileShare.Read,
                        bufferSize: 1 << 20);   // 1MB 缓冲，减少系统调用
                    _log($"原始数据保存至：{rawFile}");
                }
                catch (Exception ex)
                {
                    _log($"[警告] 原始数据文件不可写（{ex.Message}），本次不保存原始数据");
                    _rawWriter = null;
                }
            }
            else
            {
                _rawWriter = null;
                _log("config 已禁用 ofdr_raw 原始数据保存（SaveRaw=false）");
            }
            // 逐帧有效数值行对照文件：命名 ofdr_lines_{stamp}.txt（与 raw 同目录、同时间戳），
            // 每帧以 "#F=<帧序号>" 分隔，其后每行即该帧 vals 的一项，行号 == 波峰 Index，便于逐行核对。
            // 仅在保存路径非空（rawFile 有效）且 config 开启 SaveLines 时保存。
            if (SaveLines)
            {
                try
                {
                    string linesPath = rawFile!.Replace("_raw_", "_lines_").Replace(".dat", ".txt");
                    _linesWriter = new StreamWriter(linesPath, append: false) { AutoFlush = true };
                    _log($"逐帧数值行保存至：{linesPath}（行号即波峰 Index）");
                }
                catch (Exception ex)
                {
                    _log($"[警告] 数值行文件不可写（{ex.Message}），本次不保存数值行");
                    _linesWriter = null;
                }
            }
            else
            {
                _linesWriter = null;
                _log("config 已禁用 ofdr_lines 数值行保存（SaveLines=false）");
            }
            // 未检出 8 峰的帧：命名 ofdr_badpeaks_{stamp}.txt（与 raw 同目录、同时间戳），
            // 仅当保存路径非空（rawFile 有效）且 config 开启 SaveBadPeaks 时保存。
            if (SaveBadPeaks)
            {
                try
                {
                    string badPath = rawFile!.Replace("_raw_", "_badpeaks_").Replace(".dat", ".txt");
                    _badPeaksWriter = new StreamWriter(badPath, append: false) { AutoFlush = true };
                    _log($"未检出 8 峰的帧保存至：{badPath}");
                }
                catch (Exception ex)
                {
                    _log($"[警告] 未检出 8 峰帧文件不可写（{ex.Message}），本次不保存");
                    _badPeaksWriter = null;
                }
            }
            else
            {
                _badPeaksWriter = null;
                _log("config 已禁用 ofdr_badpeaks 帧保存（SaveBadPeaks=false）");
            }
        }

        _client = new TcpClient();
        _client.Connect(host, port);
        _stream = _client.GetStream();
        _log($"已连接 {host}:{port}");

        // ---- 等待握手包 OSI：字节直接进入累积缓冲，OSI 之后的数据不丢失 ----
        _log("等待接收 OSI ...");
        int pendingLen = 0;
        byte[] osi = Encoding.ASCII.GetBytes("OSI");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n = _stream.Read(_readBuf, 0, _readBuf.Length);
            if (n == 0) throw new IOException("连接被服务器关闭（未收到 OSI）");
            Buffer.BlockCopy(_readBuf, 0, _pending, pendingLen, n);
            pendingLen += n;
            int idx = IndexOf(_pending, pendingLen, osi);
            if (idx >= 0)
            {
                int keep = pendingLen - (idx + osi.Length);
                Buffer.BlockCopy(_pending, idx + osi.Length, _pending, 0, keep);
                _pendingInitLen = keep;
                break;
            }
        }
        _log("收到 OSI，发送 DELAY_" + DelayMs + " ...");
        SendAscii("DELAY_" + DelayMs);
        _log("已发送 DELAY_" + DelayMs);

        // 发送 DELAY 后再固定等待 2 秒：给设备留出应用该延时设置的时间，
        // 避免紧接着等待 SETOK / 发 START 时设备状态尚未同步。
        // （Start 在工作线程上调用，此处的 Sleep 不会卡住 UI）
        const int DelaySettleMs = 2000;
        _log($"DELAY 后固定等待 {DelaySettleMs} ms，等待设备应用延时设置 ...");
        Thread.Sleep(DelaySettleMs);
        _log($"DELAY 后等待结束（{DelaySettleMs} ms）");

        // ---- 等待 DELAY 的应答 SETOK：字节进入累积缓冲，SETOK 之后的数据不丢失 ----
        _log("等待接收 SETOK ...");
        byte[] setok = Encoding.ASCII.GetBytes("SETOK");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n = _stream.Read(_readBuf, 0, _readBuf.Length);
            if (n == 0) throw new IOException("连接被服务器关闭（未收到 SETOK）");
            Buffer.BlockCopy(_readBuf, 0, _pending, _pendingInitLen, n);
            _pendingInitLen += n;
            int idx = IndexOf(_pending, _pendingInitLen, setok);
            if (idx >= 0)
            {
                int keep = _pendingInitLen - (idx + setok.Length);
                Buffer.BlockCopy(_pending, idx + setok.Length, _pending, 0, keep);
                _pendingInitLen = keep;
                break;
            }
        }
        _log("收到 SETOK（DELAY 已应用）");

        // 重建帧队列：BlockingCollection 一旦 CompleteAdding 便不能复用，
        // 停止后再启动必须新建，否则 Add 抛 InvalidOperationException、解析线程空转立即结束
        _frames = new BlockingCollection<FrameData>(
            new ConcurrentQueue<FrameData>(), boundedCapacity: QueueCapacity);

        // 重置统计（Stop 后再次 Start 时清零）
        _intervalCount = 0;
        _minIntervalMs = double.MaxValue;
        _maxIntervalMs = 0;
        _sumIntervalMs = 0;
        _gapCount = 0;
        _minGapMs = double.MaxValue;
        _maxGapMs = 0;
        _sumGapMs = 0;
        _recvCount = 0;
        _minRecvMs = double.MaxValue;
        _maxRecvMs = 0;
        _sumRecvMs = 0;
        _scanCount = 0;
        _minScanMs = double.MaxValue;
        _maxScanMs = 0;
        _sumScanMs = 0;
        _peakFrames = 0;
        _peakSum = 0;
        _minPeakMs = double.MaxValue;
        _maxPeakMs = 0;
        _sumPeakMs = 0;
        _minMetricMs = double.MaxValue;
        _maxMetricMs = 0;
        _sumMetricMs = 0;
        _metricFrames = 0;

        _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "接收线程" };
        _parseThread = new Thread(ParseLoop) { IsBackground = true, Name = "解析线程" };
        _recvThread.Start(ct);
        _parseThread.Start(ct);
        _log("接收线程 / 解析线程已启动，每帧以 '!' 结束，等待数据帧 ...");

        // DELAY 延时（2s 等待）与握手均已完成，正式发送 START 启动数据流。
        // 放在握手阶段、DELAY 延时之后，确保设备已应用延时设置再开始采集；
        // 之后收到、但在首个通道「_channelReady=true」之前到达的帧会被 _channelReady 闸门与代次机制丢弃。
        try { SendStart(); _log("[START] 已发送 START（握手完成，开始接收数据帧）"); }
        catch (Exception ex) { _log($"[错误] 发送 START 失败：{ex.Message}"); }

        // START 之后再固定等待（时长取 config.txt 的 StartSettleMs，默认 5000ms）：
        // 留出设备进入稳定流模式的时间，让最初的过渡帧在首个通道「_channelReady=true」之前到达并被闸门丢弃。
        int startSettleMs = StartSettleMs;
        _log($"START 后固定等待 {startSettleMs} ms（config: StartSettleMs）...");
        Thread.Sleep(startSettleMs);
        _log($"START 后等待结束（{startSettleMs} ms），握手流程结束");
    }

    /// <summary>向服务器发送 START 命令（通常在扫描首个通道时由界面触发，而非握手阶段）。</summary>
    public void SendStart()
    {
        if (_stream == null) throw new InvalidOperationException("未连接，无法发送 START");
        SendAscii("START");
    }

    /// <summary>停止：取消令牌 → 发 STOP（连发两次，间隔 100ms）→ 关闭连接唤醒阻塞的 Read。</summary>
    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;
        try { cts.Cancel(); } catch { }

        try { SendAscii("STOP"); } catch { }
        try { Thread.Sleep(100); } catch { }   // 两次 STOP 间隔 100ms
        try { SendAscii("STOP"); } catch { }
        try { _client?.Close(); } catch { }

        // 关闭原始数据文件（解析线程可能仍持有引用，置空前先 Dispose）
        var raw = Interlocked.Exchange(ref _rawWriter, null);
        try
        {
            raw?.Flush();
            raw?.Dispose();
        }
        catch { }
        // 关闭逐帧数值行对照文件
        var linesW = Interlocked.Exchange(ref _linesWriter, null);
        try
        {
            linesW?.Flush();
            linesW?.Dispose();
        }
        catch { }
        // 关闭未检出 8 峰的帧文件
        var badW = Interlocked.Exchange(ref _badPeaksWriter, null);
        try
        {
            badW?.Flush();
            badW?.Dispose();
        }
        catch { }
    }

    public void Dispose() => Stop();

    // ---------------- 接收线程：只管 读 + 切帧 + 入队 ----------------
    private void ReceiveLoop(object? state)
    {
        var ct = (CancellationToken)state!;
        var stream = _stream!;
        var frames = _frames;   // 捕获本次会话的队列，旧线程 finally 不会影响新会话
        int pendingLen = _pendingInitLen;
        int scanned = 0;            // 已扫描过的字节数（下次扫描起点，避免每轮全量重扫）
        long lastFrameEndTs = 0;    // 上一帧 '!' 到达时刻
        long curFrameStartTs = -1;  // '!' 之后本帧首字节到达时刻（-1 表示未定，等待下一批 Read）
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = stream.Read(_readBuf, 0, _readBuf.Length);
                if (n == 0) { _log("连接被服务器关闭"); break; }
                Interlocked.Add(ref TotalBytes, n);

                // 上一帧恰以 '!' 结束（缓冲已清空）且已有上帧 '!' 时刻 → 本次 Read 首字节即下一帧首字节
                if (pendingLen == 0 && lastFrameEndTs > 0 && curFrameStartTs < 0)
                    curFrameStartTs = Stopwatch.GetTimestamp();

                Buffer.BlockCopy(_readBuf, 0, _pending, pendingLen, n);
                pendingLen += n;

                // 只扫描新增部分 [scanned, pendingLen)，一次 Read 可能包含多帧（粘包）
                int start = 0;
                long scanT0 = Stopwatch.GetTimestamp();   // 扫描计时开始
                for (int i = scanned; i < pendingLen; i++)
                {
                    if (_pending[i] == FrameEnd)
                    {
                        long ts = Stopwatch.GetTimestamp();   // 本帧 '!' 到达时刻
                        // 帧周期：上一帧 '!' → 本帧 '!'
                        double intervalMs = lastFrameEndTs > 0
                            ? (ts - lastFrameEndTs) * 1000.0 / Stopwatch.Frequency
                            : 0;
                        // 帧间隙：上一帧 '!' → 本帧首字节（用户关心的第一帧'!'到第二帧首字节，即每帧打印的 GapMs）
                        double gapMs = (curFrameStartTs > 0 && lastFrameEndTs > 0)
                            ? (curFrameStartTs - lastFrameEndTs) * 1000.0 / Stopwatch.Frequency
                            : 0;
                        // 本帧接收耗时：首字节 → '!'（第一帧无首字节参考，记为 0）
                        double recvMs = curFrameStartTs > 0
                            ? (ts - curFrameStartTs) * 1000.0 / Stopwatch.Frequency
                            : 0;
                        lastFrameEndTs = ts;

                        var frame = new byte[i - start];
                        Buffer.BlockCopy(_pending, start, frame, 0, frame.Length);
                        // 在收帧瞬间把「当前通道」与代次一起打进帧里。
                        // 帧的数据对应它被采集的那条光路，用收帧时刻的通道比用解析时刻的更准确：
                        // 解析线程可能滞后于通道切换，若按解析时刻取值，会把帧算到错误的通道上。
                        var (inCh, outCh) = GetCurChannels();
                        frames.Add(new FrameData(frame, Interlocked.Read(ref _epoch), inCh, outCh,
                            intervalMs, gapMs, recvMs,
                            (ts - scanT0) * 1000.0 / Stopwatch.Frequency), ct);  // 有界队列：满则阻塞形成背压（TCP 流控反压设备）
                        start = i + 1;
                        // '!' 后同一批还有数据 → 那即是下一帧首字节（同批近似）；否则留待下一批 Read 记录
                        curFrameStartTs = start < pendingLen ? ts : -1;
                    }
                }
                scanned = pendingLen;   // 本轮 [scanned, pendingLen) 全部扫描完毕
                // 扫描耗时统计（整次循环，含 '!' 处理；队列背压阻塞时会偏大）
                double scanMs = (Stopwatch.GetTimestamp() - scanT0) * 1000.0 / Stopwatch.Frequency;
                _scanCount++;
                _sumScanMs += scanMs;
                if (scanMs < _minScanMs) _minScanMs = scanMs;
                if (scanMs > _maxScanMs) _maxScanMs = scanMs;
                // 有帧切出才搬移剩余半截帧到头部（start==0 时跳过，避免自我拷贝）
                if (start > 0)
                {
                    Buffer.BlockCopy(_pending, start, _pending, 0, pendingLen - start);
                    pendingLen -= start;
                    scanned -= start;   // 剩余字节前移，扫描进度同步前移
                }

                // 安全上限：缓冲远超单帧仍无 '!'，视为异常丢弃
                if (pendingLen > MaxFrameBytes * 3)
                {
                    _log($"[警告] 累积缓冲超限（{pendingLen} 字节仍无 '!'），已丢弃，请检查帧格式");
                    pendingLen = 0;
                    scanned = 0;
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[接收线程异常] {ex.Message}"); }
        finally
        {
            try { frames.CompleteAdding(); } catch { }
            // 打印本次会话的缓冲扫描耗时统计（平均/最小/最大，按 Read 次数计）
            if (_scanCount > 0)
            {
                _log($"缓冲扫描耗时统计：共 {_scanCount} 次扫描，平均 {_sumScanMs / _scanCount:F3} ms，" +
                     $"最小 {(_minScanMs == double.MaxValue ? 0 : _minScanMs):F3} ms，最大 {_maxScanMs:F3} ms");
            }
            _log("接收线程结束");
        }
    }

    // ---------------- 解析线程：取帧 → 逐行解析 → 回调 ----------------
    private void ParseLoop(object? state)
    {
        var ct = (CancellationToken)state!;
        var frames = _frames;   // 捕获本次会话的队列，旧线程结束不影响新会话
        try
        {
            foreach (FrameData fd in frames.GetConsumingEnumerable(ct))
            {
                var sw = Stopwatch.StartNew();
                byte[] frame = fd.Bytes;
                // 本帧序号：解析线程每消费一帧递增，与日志、对照文件「#F=」保持一致。
                long fidx = Interlocked.Read(ref FrameCount) + 1;

                // 原始帧落盘：网口收到的原始字节原样写入（帧本身以 '!' 结尾，连续写入即可）。
                // 放在解析线程而非接收线程：写盘慢时会让有界队列积压 → 背压反压设备降速，不会丢帧。
                var raw = _rawWriter;
                if (raw != null)
                {
                    try { raw.Write(frame, 0, frame.Length); }
                    catch (Exception ex)
                    {
                        _rawWriter = null;
                        _log($"[警告] 原始帧写入失败：{ex.Message}");
                    }
                }

                // 通道切换残留帧：本帧入队时的代次与当前代次不一致，说明它采集于旧光路
                // （切换前 / 切换过程中收到，只是被积压在队列里、切换到新通道后才轮到解析）。
                // 直接丢弃：不解析、不出指标、不触发帧计数回调，
                // 否则会被算到新通道上（表现为第 2、3 个通道仍显示 IN1/OUT1）。
                long curEpoch = Interlocked.Read(ref _epoch);
                if (fd.Epoch != curEpoch)
                {
                    if (_lastDropEpoch != fd.Epoch)
                    {
                        _lastDropEpoch = fd.Epoch;
                        _log($"[丢弃] 通道切换残留帧（代次 {fd.Epoch} → 当前 {curEpoch}）：" +
                             $"采集于旧光路，不计入新通道");
                    }
                    continue;
                }

                // 打印本帧到达信息：帧周期 + 帧间隙 + 接收耗时 + 扫描耗时（第一帧无前帧/首字节参考，均为 0）
                _log($"收到第 {fidx} 帧[F={fidx}]（{frame.Length} 字节），" +
                     $"距上一帧 '!' 间隔 {fd.IntervalMs:F2} ms，" +
                     $"帧间隙(上帧'!'→本帧首字节) {fd.GapMs:F2} ms，" +
                     $"接收耗时(首字节→'!') {fd.RecvMs:F2} ms，" +
                     $"扫描耗时 {fd.ScanMs:F3} ms");

                // 汇总统计：帧周期
                _intervalCount++;
                _sumIntervalMs += fd.IntervalMs;
                if (fd.IntervalMs > 0)
                {
                    if (fd.IntervalMs < _minIntervalMs) _minIntervalMs = fd.IntervalMs;
                    if (fd.IntervalMs > _maxIntervalMs) _maxIntervalMs = fd.IntervalMs;
                }
                // 汇总统计：帧间隙
                _gapCount++;
                _sumGapMs += fd.GapMs;
                if (fd.GapMs > 0)
                {
                    if (fd.GapMs < _minGapMs) _minGapMs = fd.GapMs;
                    if (fd.GapMs > _maxGapMs) _maxGapMs = fd.GapMs;
                }
                // 汇总统计：帧接收耗时
                _recvCount++;
                _sumRecvMs += fd.RecvMs;
                if (fd.RecvMs > 0)
                {
                    if (fd.RecvMs < _minRecvMs) _minRecvMs = fd.RecvMs;
                    if (fd.RecvMs > _maxRecvMs) _maxRecvMs = fd.RecvMs;
                }

                long lines = 0;
                double sumMw = 0, maxDbm = double.MinValue, minDbm = double.MaxValue;
                var vals = new List<double>(200_000);   // 收集每行 dBm，供峰值检测（约 10 万点）
                var mws = new List<double>(200_000);    // 缓存对应 mW，供 RL 区间求和复用（避免重复 Math.Pow）

                string text = Encoding.ASCII.GetString(frame);   // 整帧一次解码，勿逐次 Read 解码
                foreach (string rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = rawLine.TrimEnd('\r', ' ');
                    if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double dbm))
                    {
                        double mw = Math.Pow(10, dbm / 10.0);    // dBm → mW
                        sumMw += mw;
                        lines++;
                        vals.Add(dbm);
                        mws.Add(mw);
                        if (dbm > maxDbm) maxDbm = dbm;
                        if (dbm < minDbm) minDbm = dbm;
                    }
                }

                // 本帧序号 fidx 已在循环顶部取出（与日志「收到第 K 帧」、对照文件「#F=」保持一致）。

                // 对照文件：将本帧解析出的有效数值行逐行写出，行号 == 波峰 Index（vals 下标），
                // 用 "#F=<帧序号>" 分段，便于与原始 .dat / 波峰打印逐行核对。AutoFlush 已开，停止即落盘。
                var lw = _linesWriter;
                if (lw != null)
                {
                    try
                    {
                        lw.WriteLine($"#F={fidx} lines={vals.Count}");
                        for (int v = 0; v < vals.Count; v++)
                            lw.WriteLine($"{v}\t{vals[v]:F4}");   // 行首加 Index 前缀，便于按 峰k 峰值行idx 直接定位，无需逐行计数
                    }
                    catch (Exception ex)
                    {
                        _linesWriter = null;
                        _log($"[警告] 数值行写入失败：{ex.Message}");
                    }
                }
                sw.Stop();

                // 峰值检测总开关：关闭时只保留「接收 + 逐行 dBm→mW 解析」，跳过后续所有计算，且不打印/不保存解析日志
                if (!EnablePeakDetection)
                {
                    // 不打印数据解析日志
                }
                else
                {

                // ---- 简化峰值检测：数值超过设定阈值（默认 -85 dBm）的连续段视为一个峰 ----
                var swPeak = Stopwatch.StartNew();
                double thresholdDb = PeakThresholdDbm;          // 设定阈值：超过即算峰值（来自 GUI「RL门限」或 config 默认）
                var peaks = FindPeaksByThreshold(vals, thresholdDb, PeakMergeGap, out int aboveThreshold);
                swPeak.Stop();
                double peakMs = swPeak.Elapsed.TotalMilliseconds;
                _peakFrames++;
                _peakSum += peaks.Count;
                _sumPeakMs += peakMs;
                if (peakMs < _minPeakMs) _minPeakMs = peakMs;
                if (peakMs > _maxPeakMs) _maxPeakMs = peakMs;

                // 本帧峰值检测的诊断信息缓冲区：成功（8 峰）时丢弃，
                // 失败（≠8 峰）时整块写入 badpeaks 日志，便于单独排查。
                var diag = new StringBuilder();

                // 诊断摘要：即使检出 0 个峰也打印，用于定位「有效指标 0 帧」的根因。
                // 区分两种情况：①数据未解析出数值行（格式/编码问题）；②信号幅度低于阈值（需调参）。
                string rangeText = lines > 0
                    ? $"dBm范围 [{minDbm:F2} ~ {maxDbm:F2}]"
                    : "未解析出有效数值行";
                string diagSummary = $"峰值检测[F={fidx}]: {peaks.Count} 个峰（阈值 {thresholdDb:F1} dBm，过阈值候选 {aboveThreshold}，" +
                                     $"数据行数 {lines}，{rangeText}，耗时 {peakMs:F3} ms）";
                _log(diagSummary);
                diag.AppendLine(diagSummary);

                if (peaks.Count > 0)
                {
                    var sb = new StringBuilder($"检测到 {peaks.Count} 个峰值[F={fidx}]:");
                    for (int k = 0; k < peaks.Count && k < 15; k++)
                        sb.Append($" 行{peaks[k].Index} @{peaks[k].Db:F4}dBm 质心{peaks[k].Centroid:F2} 宽{peaks[k].Width}");
                    _log(sb.ToString());
                    diag.AppendLine(sb.ToString());

                    // 逐峰打印原始数据（F4 高精度），含峰值行及前后一行，便于与 lines 文件逐行核对
                    for (int k = 0; k < peaks.Count && k < 15; k++)
                    {
                        int idx = peaks[k].Index;
                        int prev = idx - 1;
                        int nx = idx + 1;
                        var seg = new StringBuilder();
                        seg.Append($"峰{k + 1}[F={fidx}] 峰值行{idx} 原值={vals[idx]:F4} | ");
                        seg.Append(prev >= 0 ? $"前一行{prev}={vals[prev]:F4} " : "前一行(无) ");
                        seg.Append(nx < vals.Count ? $"后一行{nx}={vals[nx]:F4}" : "后一行(无)");
                        string segText = "  " + seg.ToString();
                        _log(segText);
                        diag.AppendLine(segText);
                    }
                }

                // ---- 标记点 A / B / BB / CC / C / D / E（由质心算出后四舍五入取整，消除亚行抖动）----
                // 严格限定：仅当恰好检出 8 个峰时才计算标记点与 IL/RL（峰数不符则跳过）
                if (peaks.Count == 8)
                {
                    // 质心 → 四舍五入取整（检测阶段用质心抗抖，输出与后续计算统一用整数行）
                    double b  = Math.Round(peaks[3].Centroid);                        // B  = 第4个波峰
                    double bb = Math.Round((peaks[3].Centroid + peaks[4].Centroid) / 2);  // BB = 第4、5个波峰中点
                    double cc = Math.Round((peaks[5].Centroid + peaks[6].Centroid) / 2);  // CC = 第6、7个波峰中点
                    double c  = Math.Round(peaks[6].Centroid);                        // C  = 第7个波峰
                    double e  = Math.Round(peaks[7].Centroid);                        // E  = 第8个波峰
                    _log($"标记点[F={fidx}]: A={MarkARow:F0} B={b:F0} BB={bb:F0} CC={cc:F0} C={c:F0} " +
                         $"D={MarkDRow:F0} E={e:F0}");

                    // 工程换算：链路长度 = E 行 × 0.0002；DUT 光程 = (C - B) 行 × 0.0002
                    double linkLen = e * LinkLenPerRow;
                    double dutPath = (c - b) * DutPathPerRow;
                    _log($"链路长度 = E({e:F0}) × {LinkLenPerRow} = {linkLen:F6}；" +
                         $"DUT光程 = (C({c:F0}) - B({b:F0}) = {c - b:F0}) × {DutPathPerRow} = {dutPath:F6}");

                    // ---- IL / RL 六项计算（计时范围：RL 三项区间求和 + 四点局部平均 + IL 换算）----
                    var swMetric = Stopwatch.StartNew();

                    // ---- RL 回波损耗：区间内 mW 求和 → dBm（闭区间，端点行四舍五入）----
                    double rlDut = ToDbm(SumMwRange(mws, MarkARow, MarkDRow));   // A → D
                    double rlIn = ToDbm(SumMwRange(mws, MarkARow, bb));          // A → BB
                    double rlOut = ToDbm(SumMwRange(mws, cc, MarkDRow));         // CC → D
                    // ---- 局部功率：各点 ±AvgHalfWindow 行，mW 求平均后再转 dBm ----
                    double aDbm  = AvgDbmAround(mws, MarkARow, AvgHalfWindow);
                    double dDbm  = AvgDbmAround(mws, MarkDRow, AvgHalfWindow);
                    double bbDbm = AvgDbmAround(mws, bb, AvgHalfWindow);
                    double ccDbm = AvgDbmAround(mws, cc, AvgHalfWindow);

                    // ---- 插入损耗 IL：功率差的一半（往返光程，故除以 2）----
                    // 再叠加 config 的基准修正值（SRI / SRO / SRB / SRC，各 8 路对应通道 1~8，缺省 0）：
                    //   IL-DUT：标量 SRO + 对应 IN 通道的 SRI + 对应 OUT 通道的 SRO
                    //   IL-IN ：对应 IN 通道的 SRB
                    //   IL-OUT：对应 OUT 通道的 SRC
                    // 用「收帧时刻」打在帧上的通道，而不是解析时刻的当前通道：
                    // 解析线程可能滞后于通道切换（也可能超前），按解析时刻取值会把帧算到错误的通道上，
                    // 表现为第 2、3 个通道仍显示 IN1/OUT1。收帧时刻的通道才是该帧数据真正所属的光路。
                    int refIn = fd.InCh, refOut = fd.OutCh;
                    // IL-DUT 原始值（未叠加任何基准修正）：往返功率差的一半
                    double ilDutRaw = (aDbm - dDbm) / 2;
                    double sriIn = RefAt(Sri, refIn);     // 对应 IN 通道的 SRI-i（config 键 SRI-<in>）
                    double sroOut = RefAt(Sro, refOut);   // 对应 OUT 通道的 SRO-i（config 键 SRO-<out>）
                    double ilDut = ilDutRaw + SroBase + sriIn + sroOut;
                    // IL-IN / IL-OUT 同样拆出原始值与该通道的基准修正（缺省 0）
                    double ilInRaw = (aDbm - bbDbm) / 2;
                    double srbIn = RefAt(Srb, refIn);     // 对应 IN 通道的 SRB-i（config 键 SRB-<in>）
                    double ilIn = ilInRaw + srbIn;
                    double ilOutRaw = (ccDbm - dDbm) / 2;
                    double srcOut = RefAt(Src, refOut);   // 对应 OUT 通道的 SRC-i（config 键 SRC-<out>）
                    double ilOut = ilOutRaw + srcOut;

                    swMetric.Stop();
                    double metricMs = swMetric.Elapsed.TotalMilliseconds;
                    _metricFrames++;
                    _sumMetricMs += metricMs;
                    if (metricMs < _minMetricMs) _minMetricMs = metricMs;
                    if (metricMs > _maxMetricMs) _maxMetricMs = metricMs;

                    // 每帧一行汇总：IL / RL 各三项（RL 单位 dBm，IL 单位 dB）
                    // IL 三项均拆解打印：原始值 + 各项基准修正（明确对应通道号与 config 键），便于逐通道核对
                    // （RL 三项为区间功率求和，无基准修正，故不拆解）
                    _log($"[F={fidx}] 通道 IN={refIn} OUT={refOut} | " +
                         $"IL-DUT = 原始值({ilDutRaw:F3}) + SRO({SroBase:F3}) + SRI-{refIn}({sriIn:F3}) + SRO-{refOut}({sroOut:F3}) = {ilDut:F3} dB | " +
                         $"IL-IN = 原始值({ilInRaw:F3}) + SRB-{refIn}({srbIn:F3}) = {ilIn:F3} dB | " +
                         $"IL-OUT = 原始值({ilOutRaw:F3}) + SRC-{refOut}({srcOut:F3}) = {ilOut:F3} dB | " +
                         $"RL-DUT={rlDut:F3}dBm RL-IN={rlIn:F3}dBm RL-OUT={rlOut:F3}dBm | 六项耗时 {metricMs:F3} ms");

                    // 回调 UI 填表（解析线程触发，调用方需自行 marshal 到 UI 线程）
                    _onMetrics?.Invoke(new FrameMetrics(
                        Interlocked.Read(ref FrameCount) + 1,
                        MarkARow, b, bb, cc, c, MarkDRow, e,
                        ilIn, ilDut, ilOut,
                        rlIn, rlDut, rlOut,
                        linkLen, dutPath,
                        refIn, refOut,
                        SroBase, sriIn, sroOut, srbIn, srcOut,
                        peaks.Count, peakMs, metricMs));
                }
                else
                {
                    // 未检出恰好 8 个峰（含 0 峰）：本帧无法计算标记点，单独保存原始数值行便于排查
                    string badMsg = peaks.Count > 0
                        ? $"[提示] 本帧检出 {peaks.Count} 个峰（需恰好 8 个），无法计算标记点 B/BB/CC/C/E"
                        : $"[提示] 本帧检出 0 个峰（阈值 {thresholdDb:F1} dBm），可能幅度低于阈值或未解析出数据行";
                    _log(badMsg);
                    // 整块（本帧峰值检测诊断 + 失败提示）写入 badpeaks 日志文件，与主日志内容一致
                    diag.AppendLine(badMsg);
                    _onBadLog?.Invoke(diag.ToString());

                    var bp = _badPeaksWriter;
                    if (bp != null)
                    {
                        try
                        {
                            bp.WriteLine($"#F={fidx} peaks={peaks.Count} need=8 threshold={thresholdDb:F1}dBm " +
                                         $"dbmRange=[{minDbm:F2}~{maxDbm:F2}] lines={vals.Count}");
                            for (int v = 0; v < vals.Count; v++)
                                bp.WriteLine($"{v}\t{vals[v]:F4}");   // 行号 == 波峰 Index，便于逐行核对
                        }
                        catch (Exception ex)
                        {
                            _badPeaksWriter = null;
                            _log($"[警告] 未检出 8 峰帧写入失败：{ex.Message}");
                        }
                    }
                }
                }   // end if (EnablePeakDetection)

                long frameIdx = Interlocked.Increment(ref FrameCount);
                _onFrame?.Invoke(new FrameResult(frameIdx, lines, sumMw, maxDbm, minDbm,
                    sw.Elapsed.TotalMilliseconds));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[解析线程异常] {ex.Message}"); }
        finally
        {
            // 打印本次会话的帧间隔统计汇总
            if (_intervalCount > 0)
            {
                _log($"帧间隔统计：共 {_intervalCount} 帧，平均 {_sumIntervalMs / _intervalCount:F2} ms，" +
                     $"最小 {(_minIntervalMs == double.MaxValue ? 0 : _minIntervalMs):F2} ms，最大 {_maxIntervalMs:F2} ms");
            }
            // 打印本次会话的帧间隙统计汇总（上帧'!'→本帧首字节）
            if (_gapCount > 0)
            {
                _log($"帧间隙统计：共 {_gapCount} 次，平均 {_sumGapMs / _gapCount:F3} ms，" +
                     $"最小 {(_minGapMs == double.MaxValue ? 0 : _minGapMs):F3} ms，最大 {_maxGapMs:F3} ms");
            }
            // 打印本次会话的帧接收耗时统计汇总（首字节→'!'）
            if (_recvCount > 0)
            {
                _log($"帧接收耗时统计：共 {_recvCount} 帧，平均 {_sumRecvMs / _recvCount:F3} ms，" +
                     $"最小 {(_minRecvMs == double.MaxValue ? 0 : _minRecvMs):F3} ms，最大 {_maxRecvMs:F3} ms");
            }
            // 打印本次会话的峰值统计汇总
            if (_peakFrames > 0)
            {
                _log($"峰值统计：共 {_peakFrames} 帧，平均每帧 {_peakSum / (double)_peakFrames:F2} 个峰");
                _log($"峰值检测耗时统计：共 {_peakFrames} 帧，平均 {_sumPeakMs / _peakFrames:F3} ms，" +
                     $"最小 {(_minPeakMs == double.MaxValue ? 0 : _minPeakMs):F3} ms，最大 {_maxPeakMs:F3} ms");
            }
            // 打印 IL/RL 六项耗时统计（峰数<8 的帧不计入，单独判断避免除零）
            if (_metricFrames > 0)
            {
                _log($"IL/RL 六项耗时统计：共 {_metricFrames} 帧，平均 {_sumMetricMs / _metricFrames:F3} ms，" +
                     $"最小 {(_minMetricMs == double.MaxValue ? 0 : _minMetricMs):F3} ms，最大 {_maxMetricMs:F3} ms");
            }
            _log("解析线程结束");
        }
    }

    private void SendAscii(string text)
    {
        byte[] data = Encoding.ASCII.GetBytes(text + "\r\n");
        _stream!.Write(data, 0, data.Length);
        _stream.Flush();
    }

    /// <summary>闭区间 [fromRow, toRow] 内的 mW 求和（行号四舍五入后钳制到有效范围）。</summary>
    private static double SumMwRange(List<double> mws, double fromRow, double toRow)
    {
        if (mws.Count == 0) return 0;
        int i0 = Math.Clamp((int)Math.Round(fromRow), 0, mws.Count - 1);
        int i1 = Math.Clamp((int)Math.Round(toRow), 0, mws.Count - 1);
        if (i1 < i0) (i0, i1) = (i1, i0);
        double sum = 0;
        for (int i = i0; i <= i1; i++) sum += mws[i];
        return sum;
    }

    /// <summary>
    /// 以 centerRow 为中心、左右各 halfWindow 行（共 2*halfWindow+1 点）的 mW 求平均，再转 dBm。
    /// 注意：必须先转 mW 再平均（线性域平均），不能对 dBm 直接求平均（对数域平均无物理意义）。
    /// </summary>
    private static double AvgDbmAround(List<double> mws, double centerRow, int halfWindow)
    {
        if (mws.Count == 0) return -999;
        int c = Math.Clamp((int)Math.Round(centerRow), 0, mws.Count - 1);
        int i0 = Math.Max(0, c - halfWindow);
        int i1 = Math.Min(mws.Count - 1, c + halfWindow);
        double sum = 0;
        for (int i = i0; i <= i1; i++) sum += mws[i];
        return ToDbm(sum / (i1 - i0 + 1));
    }

    /// <summary>mW → dBm（0 或负值时返回 -999，表示无效）。</summary>
    private static double ToDbm(double mw) => mw > 0 ? 10 * Math.Log10(mw) : -999;

    /// <summary>在 haystack[0..len) 中查找子串 needle，找不到返回 -1。</summary>
    private static int IndexOf(byte[] haystack, int len, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= len; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    /// <summary>
    /// 峰值检测：找同时满足以下条件的点（"准确"的五要素）：
    /// <summary>
    /// 简化峰值检测（取代 FindPeaks 的局部极大+半高宽+旁瓣抑制算法）：
    /// 数值超过阈值（默认 -85 dBm）的连续段视为「一个峰」。
    /// 每个连续过阈段输出一个峰：Index=段内最大 dBm 行，Db=段内最大 dBm，Width=段长度（行），Centroid=段内 mW 加权质心。
    /// aboveThreshold 返回过阈值的总点数（诊断用）。
    /// </summary>
    private static List<(int Index, double Db, int Width, double Centroid)> FindPeaksByThreshold(
        List<double> data, double thresholdDb, int mergeGap, out int aboveThreshold)
    {
        var peaks = new List<(int Index, double Db, int Width, double Centroid)>();
        aboveThreshold = 0;
        if (data.Count == 0) return peaks;

        // 先收集所有「连续过阈段」[segStart, segEnd]，每段记录内部最大值点
        var segs = new List<(int start, int end, int maxIdx, double maxDb)>();
        int i = 0;
        while (i < data.Count)
        {
            if (data[i] <= thresholdDb) { i++; continue; }   // 未过阈，跳过

            int segStart = i;
            double maxDb = data[i];
            int maxIdx = i;
            int segEnd = i;
            while (segEnd + 1 < data.Count && data[segEnd + 1] > thresholdDb)
            {
                segEnd++;
                if (data[segEnd] > maxDb) { maxDb = data[segEnd]; maxIdx = segEnd; }
            }
            segs.Add((segStart, segEnd, maxIdx, maxDb));
            i = segEnd + 1;
        }

        // 相邻过阈段的「间隔」（中间未过阈行数）≤ mergeGap 则合并为一个峰；> mergeGap 视为独立峰
        if (segs.Count == 0) return peaks;
        int curStart = segs[0].start, curEnd = segs[0].end, curMaxIdx = segs[0].maxIdx;
        double curMaxDb = segs[0].maxDb;
        for (int s = 1; s < segs.Count; s++)
        {
            int gap = segs[s].start - curEnd - 1;   // 两段之间的未过阈行数
            if (gap <= mergeGap)
            {
                // 合并：扩展区间，重新比较峰值
                curEnd = segs[s].end;
                if (segs[s].maxDb > curMaxDb) { curMaxDb = segs[s].maxDb; curMaxIdx = segs[s].maxIdx; }
            }
            else
            {
                peaks.Add(MakePeak(data, ref curStart, ref curEnd, curMaxIdx, curMaxDb, ref aboveThreshold));
                curStart = segs[s].start; curEnd = segs[s].end; curMaxIdx = segs[s].maxIdx; curMaxDb = segs[s].maxDb;
            }
        }
        peaks.Add(MakePeak(data, ref curStart, ref curEnd, curMaxIdx, curMaxDb, ref aboveThreshold));
        return peaks;
    }

    // 由合并后的 [segStart, segEnd] 区间计算峰值（最大值点 + mW 加权质心 + 宽度），并累加到 aboveThreshold
    private static (int Index, double Db, int Width, double Centroid) MakePeak(
        List<double> data, ref int segStart, ref int segEnd, int maxIdx, double maxDb, ref int aboveThreshold)
    {
        int width = segEnd - segStart + 1;
        aboveThreshold += width;
        double wsum = 0, wpos = 0;
        for (int j = segStart; j <= segEnd; j++)
        {
            double mw = Math.Pow(10, data[j] / 10.0);
            wsum += mw;
            wpos += j * mw;
        }
        double centroid = wsum > 0 ? wpos / wsum : (segStart + segEnd) / 2.0;
        return (maxIdx, maxDb, width, centroid);
    }

    ///   1) 阈值滤噪：值 ≥ thresholdDb（低于噪声底的点直接忽略）；
    ///   2) 局部极大：值 ≥ 左右相邻点（毛刺/斜坡上的非极大值被剔除）；
    ///   3) 峰宽过滤：3dB 半高宽 ≥ MinPeakWidth（窄毛刺剔除）；
    ///   4) 局部旁瓣过滤：±PeakLocalWindow 行内存在比自身高 PeakLocalDropDb 以上的峰 → 判为旁瓣剔除；
    ///   5) 最小峰间距：与已确认峰间隔 < minPeakDistance 时，幅差 > PeakRelDropDb 只保留更强者，幅值相当则视为真实紧邻双事件都保留。
    /// 返回 (Index, Db, Width, Centroid)：Index 为峰顶行号（0 基，可换算距离），Width 为 3dB 半高宽（行），
    /// Centroid 为能量重心行号（线性功率 mW 加权，亚行精度，比峰顶 Index 更稳定）。
    /// </summary>
    private static List<(int Index, double Db, int Width, double Centroid)> FindPeaks(
        List<double> data, double thresholdDb, int minPeakDistance, out int aboveThreshold)
    {
        var peaks = new List<(int Index, double Db, int Width, double Centroid)>();
        aboveThreshold = 0;   // 过阈值且局部极大的候选数（诊断：若远大于峰值数，说明有峰被后续条件滤掉）
        if (data.Count < 3) return peaks;

        // 质心（能量重心）：必须用线性功率 mW 加权，不能对 dBm 直接加权（dBm 是对数量，加权无物理意义）
        double CentroidOf(int idx)
        {
            int c0 = Math.Max(0, idx - PeakCentroidHalfWindow);
            int c1 = Math.Min(data.Count - 1, idx + PeakCentroidHalfWindow);
            double wsum = 0, wpos = 0;
            for (int j = c0; j <= c1; j++)
            {
                double mw = Math.Pow(10, data[j] / 10.0);   // dBm → mW
                wsum += mw;
                wpos += j * mw;
            }
            return wsum > 0 ? wpos / wsum : idx;
        }

        int lastPeakIdx = -minPeakDistance;
        for (int i = 1; i < data.Count - 1; i++)
        {
            double v = data[i];
            if (v < thresholdDb) continue;                      // ① 阈值滤噪
            if (v < data[i - 1] || v < data[i + 1]) continue;   // ② 非局部极大
            aboveThreshold++;

            // ③ 3dB 半高宽：从峰顶向两侧扩展，直到降到 (v-3dB) 线以下或达上限
            double halfLevel = v - 3.0;
            int left = i;
            while (left > 0 && data[left - 1] >= halfLevel && i - left < MaxPeakWidth) left--;
            int right = i;
            while (right < data.Count - 1 && data[right + 1] >= halfLevel && right - i < MaxPeakWidth) right++;
            int width = right - left + 1;
            if (width < MinPeakWidth) continue;                 // ③b 窄毛刺剔除

            // ③c 局部旁瓣过滤：±PeakLocalWindow 行内存在显著更高的峰 → 自己是旁瓣（候选点少，直接扫窗口 O(候选×窗口) 可忽略）
            double localMax = double.MinValue;
            int w0 = Math.Max(0, i - PeakLocalWindow);
            int w1 = Math.Min(data.Count - 1, i + PeakLocalWindow);
            for (int j = w0; j <= w1; j++)
                if (data[j] > localMax) localMax = data[j];
            if (v < localMax - PeakLocalDropDb) continue;

            if (i - lastPeakIdx < minPeakDistance)              // ④ 与上一峰太近：幅差判断旁瓣 or 独立事件
            {
                if (Math.Abs(v - peaks[^1].Db) > PeakRelDropDb)
                {
                    if (v > peaks[^1].Db)                       // 当前显著更高 → 上一峰是旁瓣，替换掉
                    {
                        peaks[^1] = (i, v, width, CentroidOf(i));
                        lastPeakIdx = i;
                    }
                    // 否则当前显著更弱 → 丢弃当前峰
                }
                else                                            // 幅值相当 → 真实紧邻双事件，都保留
                {
                    peaks.Add((i, v, width, CentroidOf(i)));
                    lastPeakIdx = i;
                }
            }
            else
            {
                peaks.Add((i, v, width, CentroidOf(i)));
                lastPeakIdx = i;
            }
        }
        return peaks;
    }
}
