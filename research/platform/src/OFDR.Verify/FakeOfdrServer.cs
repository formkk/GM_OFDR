using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OFDR.Verify;

/// <summary>
/// 本地假 OFDR 整机服务端：按真实协议回应（OSI → 等 DELAY_x → SETOK → 等 START → 持续发帧）。
/// 帧以 '!' 结束；写入按小分片进行，用于覆盖客户端“粘包/分片”读帧逻辑。
/// </summary>
internal sealed class FakeOfdrServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Thread _thread;
    private readonly int _frames;
    private readonly int _points;
    private readonly int _chunkSize;
    private readonly int _frameStartDelayMs;

    private TcpClient? _client;
    private volatile bool _disposed;
    private string _pending = string.Empty;

    public int Port { get; }
    public List<string> Commands { get; } = new();
    public string? Error { get; private set; }

    public FakeOfdrServer(int frames = 3, int points = 64, int chunkSize = 64, int frameStartDelayMs = 300)
    {
        _frames = frames;
        _points = points;
        _chunkSize = chunkSize;
        _frameStartDelayMs = frameStartDelayMs;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _thread = new Thread(Run) { IsBackground = true, Name = "FakeOfdrServer" };
        _thread.Start();
    }

    public IReadOnlyList<string> CommandSnapshot()
    {
        lock (Commands) return Commands.ToArray();
    }

    private void Run()
    {
        try
        {
            _client = _listener.AcceptTcpClient();
            _client.NoDelay = true;
            var stream = _client.GetStream();

            WriteChunked(stream, Encoding.ASCII.GetBytes("OSI\r\n"));
            WaitCommand(stream, "DELAY_");
            WriteChunked(stream, Encoding.ASCII.GetBytes("SETOK\r\n"));
            WaitCommand(stream, "START");

            Thread.Sleep(_frameStartDelayMs);
            for (int f = 0; f < _frames; f++)
                WriteChunked(stream, BuildFrame(f));

            try { WaitCommand(stream, "STOP"); } catch { }
        }
        catch (Exception ex)
        {
            if (!_disposed) Error = ex.Message;
        }
    }

    private byte[] BuildFrame(int frameIndex)
    {
        var sb = new StringBuilder(_points * 8);
        for (int i = 0; i < _points; i++)
            sb.Append((frameIndex + i * 0.1).ToString("F2", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("!\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private void WriteChunked(NetworkStream stream, byte[] data)
    {
        for (int off = 0; off < data.Length; off += _chunkSize)
        {
            int len = Math.Min(_chunkSize, data.Length - off);
            stream.Write(data, off, len);
            stream.Flush();
        }
    }

    private void WaitCommand(NetworkStream stream, string prefix)
    {
        var buf = new byte[4096];
        while (true)
        {
            while (true)
            {
                int nl = _pending.IndexOf('\n');
                if (nl < 0) break;
                string line = _pending[..nl].TrimEnd('\r').Trim();
                _pending = _pending[(nl + 1)..];
                if (line.Length == 0) continue;
                lock (Commands) Commands.Add(line);
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            }

            int n = stream.Read(buf, 0, buf.Length);
            if (n == 0) throw new IOException("客户端已断开");
            _pending += Encoding.ASCII.GetString(buf, 0, n);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _client?.Close(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _thread.Join(1500); } catch { }
    }
}
