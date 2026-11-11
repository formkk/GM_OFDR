namespace OFDR.Contracts;

/// <summary>
/// 扫描源：在线 TCP、离线合成/回放都实现同一接口。
/// 代次（Epoch）由 BeginChannel 递增；实现应丢弃旧代次帧，保证切换残留帧不污染新通道。
/// </summary>
public interface IScanSource : IDisposable
{
    string Name { get; }

    /// <summary>建立连接/握手（阻塞）。离线源为空实现。</summary>
    void Open();

    /// <summary>开始数据流（在线源在握手完成后发送 START）。</summary>
    void StartStream();

    /// <summary>切换到 (channelIn, channelOut) 并开启新代次，返回新代次号。</summary>
    long BeginChannel(int channelIn, int channelOut);

    /// <summary>在超时内取下一帧当前代次的标准扫描；取不到返回 false。</summary>
    bool TryTake(TimeSpan timeout, out StandardScan? scan);

    /// <summary>停止数据流（可重复调用）。</summary>
    void Stop();

    /// <summary>因代次不符而丢弃的残留帧数。</summary>
    long DroppedFrames { get; }
}

public readonly record struct SwitchReply(bool Ok, string Command, string? Response, double ElapsedMs, string? Error);

/// <summary>光开关：切到 IN 通道。</summary>
public interface IOpticalSwitch : IDisposable
{
    string Name { get; }
    void Open();
    SwitchReply Select(int channelIn);
}

/// <summary>矩阵开关：切到 (IN, OUT) 组合。</summary>
public interface IMatrixSwitch : IDisposable
{
    string Name { get; }
    void Open();
    SwitchReply Select(int channelIn, int channelOut);
}
