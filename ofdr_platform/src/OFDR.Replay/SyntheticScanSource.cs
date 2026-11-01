using System.Diagnostics;
using OFDR.Contracts;

namespace OFDR.Replay;

public sealed record SyntheticSourceOptions
{
    public SyntheticSpectrumOptions Spectrum { get; init; } = new();
    public int FrameIntervalMs { get; init; } = 50;
    public string DeviceId { get; init; } = "SYNTH-01";
}

/// <summary>
/// 离线合成扫描源：按固定帧间隔生成 8 峰合成谱，与 TCP 源共用 IScanSource，
/// 从而让编排、指标、UI、落盘在主流程上完全一致（无硬件闭环）。
/// </summary>
public sealed class SyntheticScanSource : IScanSource
{
    private readonly SyntheticSourceOptions _options;
    private readonly Action<string>? _log;

    private long _epoch = 1;
    private long _channelsPacked = Pack(1, 1);
    private long _sequence;
    private long _nextFrameTicks;
    private bool _streaming;

    public SyntheticScanSource(SyntheticSourceOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log;
    }

    public string Name => $"离线合成（{_options.DeviceId}）";

    public long DroppedFrames => 0;

    private static long Pack(int inCh, int outCh) => ((long)inCh << 32) | (uint)outCh;

    public void Open() => _log?.Invoke("离线合成源就绪（无需连接设备）");

    public void StartStream()
    {
        _nextFrameTicks = Stopwatch.GetTimestamp();
        _streaming = true;
        _log?.Invoke($"离线合成源开始出帧（间隔 {_options.FrameIntervalMs} ms）");
    }

    public long BeginChannel(int channelIn, int channelOut)
    {
        Interlocked.Exchange(ref _channelsPacked, Pack(channelIn, channelOut));
        return Interlocked.Increment(ref _epoch);
    }

    public bool TryTake(TimeSpan timeout, out StandardScan? scan)
    {
        scan = null;
        if (!_streaming) return false;

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

        double[] values = SyntheticSpectrum.Create((int)(seq - 1), _options.Spectrum);
        var axis = new ScanAxis(AxisKind.Distance, 0, 0.0002, "m", values.Length);
        var metadata = new ScanMetadata(
            _options.DeviceId, "SYNTH", inCh, outCh, epoch, DateTimeOffset.Now, SegmentIndex: 0);

        scan = new StandardScan(
            $"SYNTH#{seq}", seq, axis, values, "dBm", metadata);
        return true;
    }

    public void Stop()
    {
        _streaming = false;
        _log?.Invoke("离线合成源已停止");
    }

    public void Dispose() => Stop();
}
