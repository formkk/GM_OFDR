using System.Globalization;
using System.Text;
using OFDR.Contracts;

namespace OFDR.Infrastructure.Persistence;

/// <summary>
/// 逐帧结果 CSV。前 18 列与现状 ofdr_metrics_*.csv 完全一致，末尾追加状态与配置快照，
/// 便于旧脚本继续读取、又能追溯本次算法配置。
/// </summary>
public sealed class ResultCsvWriter : IDisposable
{
    public const string Header =
        "采样序号,Timestamp,IN,OUT,IL-IN(dB),IL-DUT(dB),IL-OUT(dB),RL-IN(dBm),RL-DUT(dBm),RL-OUT(dBm)," +
        "DUT光程,链路长度,峰值数,SRO,SRI-IN,SRO-OUT,SRB-IN,SRC-OUT,状态,配置快照";

    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public ResultCsvWriter(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = false };
        _writer.WriteLine(Header);
        _writer.Flush();
    }

    public void Write(MeasurementResult r, DateTime timestamp)
    {
        lock (_lock)
        {
            _writer.WriteLine(string.Join(',',
                r.Sequence,
                timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                r.ChannelIn, r.ChannelOut,
                F3(r.IlIn), F3(r.IlDut), F3(r.IlOut),
                F3(r.RlIn), F3(r.RlDut), F3(r.RlOut),
                F6(r.DutPathM), F6(r.LinkLengthM),
                r.PeakCount,
                F3(r.SroBase), F3(r.SriIn), F3(r.SroOut), F3(r.SrbIn), F3(r.SrcOut),
                r.Status,
                r.ConfigId));
        }
    }

    private static string F3(double v) =>
        double.IsNaN(v) ? "" : v.ToString("F3", CultureInfo.InvariantCulture);

    private static string F6(double v) =>
        double.IsNaN(v) ? "" : v.ToString("F6", CultureInfo.InvariantCulture);

    public void Flush()
    {
        lock (_lock) _writer.Flush();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer.Flush(); } catch { }
            try { _writer.Dispose(); } catch { }
        }
    }
}

/// <summary>带时间戳的日志文件（线程安全，逐行自动落盘）。</summary>
public sealed class LogFileWriter : IDisposable
{
    private StreamWriter? _writer;
    private readonly object _lock = new();

    public LogFileWriter(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Write(string message)
    {
        lock (_lock)
        {
            var w = _writer;
            if (w == null) return;
            try { w.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}"); }
            catch { _writer = null; }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
