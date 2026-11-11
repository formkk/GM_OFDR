using System.Diagnostics;
using OFDR.Contracts;
using OFDR.Metrics;

namespace OFDR.Engine;

/// <summary>通道遍历参数（对应 SRS FR-ORC-01/02/05）。</summary>
public sealed record EngineOptions
{
    public int InFrom { get; init; } = 1;
    public int InTo { get; init; } = 8;
    public int OutFrom { get; init; } = 1;
    public int OutTo { get; init; } = 8;
    public int SamplesPerChannel { get; init; } = 5;
    public int ChannelTimeoutMs { get; init; } = 10000;
    public int Rounds { get; init; } = 1;
    public bool Infinite { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (InFrom is < 1 or > 8 || InTo is < 1 or > 8 || InFrom > InTo)
            errors.Add($"IN 范围非法：{InFrom}~{InTo}");
        if (OutFrom is < 1 or > 8 || OutTo is < 1 or > 8 || OutFrom > OutTo)
            errors.Add($"OUT 范围非法：{OutFrom}~{OutTo}");
        if (SamplesPerChannel < 1) errors.Add("SamplesPerChannel 必须 ≥ 1");
        if (ChannelTimeoutMs < 0) errors.Add("ChannelTimeoutMs 不能为负");
        if (!Infinite && Rounds < 1) errors.Add("Rounds 必须 ≥ 1");
        return errors;
    }
}

/// <summary>
/// 测试编排（应用层）：状态机 + IN×OUT 遍历 + 代次 + 超时 + 取消 + 逐通道聚合。
/// 只依赖契约与纯函数指标，不碰 UI、不碰文件、不引用任何厂商类型。
/// </summary>
public sealed class TestEngine
{
    private readonly IScanSource _source;
    private readonly MetricsConfig _config;
    private readonly ReferenceValues _references;
    private readonly IOpticalSwitch? _optical;
    private readonly IMatrixSwitch? _matrix;
    private readonly EngineOptions _options;
    private readonly string _configId;

    private long _totalFrames;
    private long _invalidFrames;

    public TestEngine(
        IScanSource source,
        MetricsConfig config,
        ReferenceValues references,
        IOpticalSwitch? optical = null,
        IMatrixSwitch? matrix = null,
        EngineOptions? options = null)
    {
        _source = source;
        _config = config;
        _references = references;
        _optical = optical;
        _matrix = matrix;
        _options = options ?? new EngineOptions();
        _configId = config.ComputeId();
    }

    public event Action<string>? Log;

    /// <summary>每帧原始扫描（曲线/事件表用）。订阅者只读，禁止修改 Values 数组。</summary>
    public event Action<StandardScan>? ScanProduced;

    public event Action<MeasurementResult>? ResultProduced;
    public event Action<ChannelAggregate>? ChannelCompleted;
    public event Action<string>? StateChanged;

    public long TotalFrames => Interlocked.Read(ref _totalFrames);
    public long InvalidFrames => Interlocked.Read(ref _invalidFrames);
    public string ConfigId => _configId;
    public EngineOptions Options => _options;

    /// <summary>阻塞执行整轮编排；取消令牌用于停止。异常向上抛出（调用方负责收尾提示）。</summary>
    public void Run(CancellationToken cancellationToken)
    {
        var errors = _options.Validate();
        if (errors.Count > 0)
            throw new ArgumentException("编排参数非法：" + string.Join("；", errors));

        SetState("连接中");
        try
        {
            _source.Open();
            _optical?.Open();
            _matrix?.Open();
            SetState("运行中");
            _source.StartStream();

            int rounds = _options.Infinite ? int.MaxValue : _options.Rounds;
            for (int round = 0; round < rounds && !cancellationToken.IsCancellationRequested; round++)
            {
                Log?.Invoke($"—— 第 {round + 1} 轮：IN {_options.InFrom}~{_options.InTo} × OUT {_options.OutFrom}~{_options.OutTo} ——");
                RunRound(cancellationToken);
            }
            SetState(cancellationToken.IsCancellationRequested ? "已停止" : "已完成");
        }
        finally
        {
            try { _source.Stop(); } catch { }
            try { _optical?.Dispose(); } catch { }
            try { _matrix?.Dispose(); } catch { }
        }
    }

    private void RunRound(CancellationToken ct)
    {
        for (int i = _options.InFrom; i <= _options.InTo && !ct.IsCancellationRequested; i++)
        {
            int firstOut = _options.OutFrom;
            SelectBoth(i, firstOut);

            for (int j = firstOut; j <= _options.OutTo && !ct.IsCancellationRequested; j++)
            {
                if (j > firstOut) SelectMatrix(i, j);

                long epoch = _source.BeginChannel(i, j);
                Log?.Invoke($"开始通道 IN={i} OUT={j}（代次 {epoch}），每通道采样 {_options.SamplesPerChannel} 帧");

                var aggregator = new Aggregator(i, j, _options.SamplesPerChannel);
                bool timedOut = false;
                var clock = Stopwatch.StartNew();

                while (aggregator.Frames < _options.SamplesPerChannel && !ct.IsCancellationRequested)
                {
                    if (_source.TryTake(TimeSpan.FromMilliseconds(10), out var scan) && scan != null)
                    {
                        // 防御性代次校验（源实现通常已丢弃旧代次帧）
                        if (scan.Metadata.Epoch != epoch) continue;

                        var result = MetricCalculator.Compute(scan, _config, _references, _configId);
                        aggregator.Add(result);
                        ScanProduced?.Invoke(scan);
                        Interlocked.Increment(ref _totalFrames);
                        if (!result.IsValid) Interlocked.Increment(ref _invalidFrames);
                        ResultProduced?.Invoke(result);
                    }

                    if (clock.ElapsedMilliseconds >= _options.ChannelTimeoutMs)
                    {
                        timedOut = true;
                        Log?.Invoke($"[警告] 通道 IN={i} OUT={j} 等待 {_options.ChannelTimeoutMs} ms " +
                                    $"仅收到 {aggregator.Frames}/{_options.SamplesPerChannel} 帧，跳过");
                        break;
                    }
                }

                var aggregate = aggregator.Build(timedOut);
                ChannelCompleted?.Invoke(aggregate);
                Log?.Invoke($"通道 IN={i} OUT={j} 结束：{aggregate.Status}，" +
                            $"帧 {aggregate.Frames}/{aggregate.ExpectedFrames}，有效 {aggregate.ValidFrames}");
            }
        }
    }

    private void SelectBoth(int channelIn, int channelOut)
    {
        if (_optical == null && _matrix == null) return;

        var opticalTask = _optical != null ? Task.Run(() => SelectOptical(channelIn)) : Task.CompletedTask;
        var matrixTask = _matrix != null ? Task.Run(() => SelectMatrix(channelIn, channelOut)) : Task.CompletedTask;
        Task.WaitAll(opticalTask, matrixTask);
    }

    private void SelectOptical(int channelIn)
    {
        if (_optical == null) return;
        try
        {
            var reply = _optical.Select(channelIn);
            Log?.Invoke($"[切换] 光开关 → {reply.Command}：{(reply.Ok ? "OK" : "失败")}" +
                        (string.IsNullOrEmpty(reply.Response) ? "" : $" 回显「{reply.Response}」") +
                        $"（{reply.ElapsedMs:F1} ms）");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[切换] 光开关异常：{ex.Message}");
        }
    }

    private void SelectMatrix(int channelIn, int channelOut)
    {
        if (_matrix == null) return;
        try
        {
            var reply = _matrix.Select(channelIn, channelOut);
            Log?.Invoke($"[切换] 矩阵开关 → {reply.Command}：{(reply.Ok ? "OK" : "失败")}" +
                        (string.IsNullOrEmpty(reply.Response) ? "" : $" 回显「{reply.Response}」") +
                        $"（{reply.ElapsedMs:F1} ms）");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[切换] 矩阵开关异常：{ex.Message}");
        }
    }

    private void SetState(string state) => StateChanged?.Invoke(state);

    /// <summary>逐通道统计累加器：Cur/Max/Min/Avg 与有效帧数。</summary>
    private sealed class Aggregator
    {
        private readonly int _inCh;
        private readonly int _outCh;
        private readonly int _expected;

        private int _frames;
        private int _valid;
        private int _lastPeakCount;

        private MetricStats _ilIn = MetricStats.Empty;
        private MetricStats _ilDut = MetricStats.Empty;
        private MetricStats _ilOut = MetricStats.Empty;
        private MetricStats _rlIn = MetricStats.Empty;
        private MetricStats _rlDut = MetricStats.Empty;
        private MetricStats _rlOut = MetricStats.Empty;
        private MetricStats _dutPath = MetricStats.Empty;
        private MetricStats _linkLength = MetricStats.Empty;

        public Aggregator(int inCh, int outCh, int expected)
        {
            _inCh = inCh;
            _outCh = outCh;
            _expected = expected;
        }

        public int Frames => _frames;

        public void Add(MeasurementResult result)
        {
            _frames++;
            _lastPeakCount = result.PeakCount;
            if (!result.IsValid) return;

            _valid++;
            _ilIn = _ilIn.Add(result.IlIn);
            _ilDut = _ilDut.Add(result.IlDut);
            _ilOut = _ilOut.Add(result.IlOut);
            _rlIn = _rlIn.Add(result.RlIn);
            _rlDut = _rlDut.Add(result.RlDut);
            _rlOut = _rlOut.Add(result.RlOut);
            _dutPath = _dutPath.Add(result.DutPathM);
            _linkLength = _linkLength.Add(result.LinkLengthM);
        }

        public ChannelAggregate Build(bool timedOut)
        {
            var status = _frames == 0
                ? ChannelRunStatus.NoData
                : timedOut ? ChannelRunStatus.TimedOut : ChannelRunStatus.Completed;

            string message = status switch
            {
                ChannelRunStatus.Completed => $"完成 {_frames}/{_expected} 帧（有效 {_valid}）",
                ChannelRunStatus.TimedOut => $"超时：仅 {_frames}/{_expected} 帧（有效 {_valid}）",
                _ => "超时且未收到任何帧",
            };

            return new ChannelAggregate(
                _inCh, _outCh, _frames, _valid, _expected, status,
                _ilIn, _ilDut, _ilOut, _rlIn, _rlDut, _rlOut, _dutPath, _linkLength,
                _lastPeakCount, message);
        }
    }
}
