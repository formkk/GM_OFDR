using System.Diagnostics;
using OFDR.Contracts;

namespace OFDR.Infrastructure.Switches;

/// <summary>
/// 模拟光开关：无硬件时按固定延时返回成功，并生成与真实设备一致的命令文本。
/// 一期整机 TCP 路径用它保证 64 通道遍历可离线演示。
/// </summary>
public sealed class SimulatedOpticalSwitch : IOpticalSwitch
{
    private readonly int _delayMs;

    public SimulatedOpticalSwitch(int delayMs = 5) => _delayMs = delayMs;

    public string Name => "模拟光开关";

    public void Open() { }

    public SwitchReply Select(int channelIn)
    {
        var command = $"<SETSW1-{channelIn:00}>";
        var sw = Stopwatch.StartNew();
        if (_delayMs > 0) Thread.Sleep(_delayMs);
        sw.Stop();
        return new SwitchReply(true, command, command, sw.Elapsed.TotalMilliseconds, null);
    }

    public void Dispose() { }
}

/// <summary>模拟矩阵开关：命令格式与真实 GK-OSW 8×8 一致。</summary>
public sealed class SimulatedMatrixSwitch : IMatrixSwitch
{
    private readonly int _delayMs;

    public SimulatedMatrixSwitch(int delayMs = 5) => _delayMs = delayMs;

    public string Name => "模拟矩阵开关";

    public void Open() { }

    public SwitchReply Select(int channelIn, int channelOut)
    {
        var command = $"<SETSW{channelIn}-{channelOut:00}>";
        var sw = Stopwatch.StartNew();
        if (_delayMs > 0) Thread.Sleep(_delayMs);
        sw.Stop();
        return new SwitchReply(true, command, command, sw.Elapsed.TotalMilliseconds, null);
    }

    public void Dispose() { }
}
