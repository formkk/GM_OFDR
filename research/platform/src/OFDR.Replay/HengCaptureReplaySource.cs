using System.Diagnostics;
using System.Globalization;
using System.Text;
using OFDR.Contracts;

namespace OFDR.Replay;

public sealed record HengCaptureOptions
{
    /// <summary>远程控制抓包文件（ASCII，每行一个 dB 值，允许首行带控制字前缀）。</summary>
    public required string FilePath { get; init; }

    /// <summary>每帧点数。默认 82153 为 LGA50-D `LENGTH` 实测返回值，非猜测；可按文件实际情况调整。</summary>
    public int PointsPerFrame { get; init; } = 82153;

    /// <summary>点距（米）。185.0 µm 为 1 m / 2 m 尾纤双峰实测结果，待契约 T-02 复核。</summary>
    public double PointSpacingM { get; init; } = 0.000185;

    public int FrameIntervalMs { get; init; } = 100;

    public string DeviceId { get; init; } = "HENG-LGA50-D";
}

/// <summary>
/// 昊衡 LGA50-D 抓包回放：把远程控制抓到的 dB 文本流解析为等长帧，输出与在线适配器同构的 <see cref="StandardScan"/>。
/// 抓包文件不含 X 轴，点距取实测值；帧的 <see cref="SourceType"/> 标记为 <c>Replay</c>（契约 §4.7：重放不改变原始采集属性）。
/// 解析发生在 <see cref="Open"/>，由编排线程调用，避免 UI 线程卡顿。
/// </summary>
public sealed class HengCaptureReplaySource : IScanSource
{
    private readonly HengCaptureOptions _options;
    private readonly Action<string>? _log;

    private double[]? _values;
    private int _frameLength;
    private int _frameCount;
    private int _frameIndex;

    private long _epoch = 1;
    private long _channelsPacked = Pack(1, 1);
    private long _sequence;
    private long _nextFrameTicks;
    private bool _streaming;

    public HengCaptureReplaySource(HengCaptureOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log;
    }

    public string Name => $"昊衡抓包回放（{Path.GetFileName(_options.FilePath)}）";

    public long DroppedFrames => 0;

    private static long Pack(int inCh, int outCh) => ((long)inCh << 32) | (uint)outCh;

    public void Open()
    {
        if (_values != null) return;
        if (!File.Exists(_options.FilePath))
            throw new FileNotFoundException("抓包文件不存在", _options.FilePath);

        var parsed = Parse(File.ReadAllText(_options.FilePath, Encoding.ASCII));
        if (parsed.Length == 0)
            throw new InvalidDataException("抓包文件中未解析到数值");

        _frameLength = _options.PointsPerFrame > 0
            ? Math.Min(_options.PointsPerFrame, parsed.Length)
            : parsed.Length;
        _frameCount = parsed.Length / _frameLength;
        _values = parsed;

        int tail = parsed.Length - _frameCount * _frameLength;
        _log?.Invoke($"昊衡抓包已解析：{parsed.Length} 点，切分为 {_frameCount} 帧 × {_frameLength} 点" +
                     (tail > 0 ? $"，尾部 {tail} 点（不足一帧）忽略" : "") +
                     $"，点距 {_options.PointSpacingM * 1000:F1} mm");
    }

    public void StartStream()
    {
        _nextFrameTicks = Stopwatch.GetTimestamp();
        _streaming = true;
        _log?.Invoke($"昊衡抓包回放开始出帧（间隔 {_options.FrameIntervalMs} ms）");
    }

    public long BeginChannel(int channelIn, int channelOut)
    {
        Interlocked.Exchange(ref _channelsPacked, Pack(channelIn, channelOut));
        return Interlocked.Increment(ref _epoch);
    }

    public bool TryTake(TimeSpan timeout, out StandardScan? scan)
    {
        scan = null;
        if (!_streaming || _values == null) return false;

        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= _nextFrameTicks) break;

            double waitMs = (_nextFrameTicks - now) * 1000.0 / Stopwatch.Frequency;
            double remainMs = (deadline - now) * 1000.0 / Stopwatch.Frequency;
            if (remainMs <= 0) return false;
            Thread.Sleep((int)Math.Clamp(Math.Min(waitMs, remainMs), 1, 50));
        }

        long intervalTicks = (long)(_options.FrameIntervalMs / 1000.0 * Stopwatch.Frequency);
        _nextFrameTicks = Math.Max(_nextFrameTicks + intervalTicks, Stopwatch.GetTimestamp());

        long packed = Interlocked.Read(ref _channelsPacked);
        int inCh = (int)(packed >> 32);
        int outCh = (int)(packed & 0xFFFFFFFF);
        long epoch = Interlocked.Read(ref _epoch);
        long seq = Interlocked.Increment(ref _sequence);

        // 每帧独立数组：契约要求 Values 只读，回放不共享同一块内存
        var values = new double[_frameLength];
        Array.Copy(_values, (long)_frameIndex * _frameLength, values, 0, _frameLength);
        _frameIndex = (_frameIndex + 1) % _frameCount;

        var axis = new ScanAxis(0, _options.PointSpacingM, values.Length);
        var metadata = new ScanMetadata(
            SourceType.Replay, _options.DeviceId, "LGA50-D", inCh, outCh, epoch, DateTimeOffset.Now, SegmentIndex: 0);

        scan = new StandardScan($"HENG#{seq}", seq, axis, values, metadata);
        return true;
    }

    public void Stop()
    {
        _streaming = false;
        _log?.Invoke("昊衡抓包回放已停止");
    }

    public void Dispose() => Stop();

    /// <summary>
    /// 逐行解析：取每行第一个数字起始字符之后的子串。
    /// 抓包首行为 <c>OSISETOK   -75.59</c>（控制字与首个数值同行、无分隔），其余行为 <c>%9.2f</c> 纯数值。
    /// </summary>
    internal static double[] Parse(string text)
    {
        var values = new List<double>(text.Length / 8 + 1);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty) continue;

            int start = 0;
            while (start < line.Length && !IsNumberStart(line[start])) start++;
            if (start >= line.Length) continue;

            if (double.TryParse(line[start..], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                values.Add(v);
        }
        return values.ToArray();
    }

    private static bool IsNumberStart(char c) =>
        char.IsAsciiDigit(c) || c == '-' || c == '+' || c == '.';
}
