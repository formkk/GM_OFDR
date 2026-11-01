using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using OFDR.Contracts;

namespace OFDR.Infrastructure.Tcp;

public sealed record TcpScanOptions(
    string Host = "192.168.3.111",
    int Port = 4300,
    int DelayMs = 420,
    int DelaySettleMs = 2000,
    int StartSettleMs = 7000,
    double RowSpacingM = 0.0002,
    int QueueCapacity = 8,
    string SdkVersion = "TCP")
{
    public string DeviceId { get; init; } = Host;
}

/// <summary>
/// 整机 TCP 扫描源：连接 → 收 OSI → DELAY_&lt;ms&gt; → SETOK → START → 持续收帧（帧以 '!' 结束）。
/// 接收线程只做“读 + 按 '!' 切帧 + 打代次/通道戳 + 入有界队列”；ASCII 行解析在消费方线程，
/// 与现状 demo 的生产者/消费者结构一致，避免 800KB 帧解码阻塞收帧。
/// </summary>
public sealed class OfdrTcpSource : IScanSource
{
    private const byte FrameEnd = (byte)'!';
    private const int MaxFrameBytes = 900 * 1024;
    private const int PendingCapacity = MaxFrameBytes * 3;

    private readonly TcpScanOptions _options;
    private readonly Action<string>? _log;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private BlockingCollection<RawFrame>? _frames;
    private CancellationTokenSource? _cts;
    private Thread? _receiveThread;
    private byte[] _carry = Array.Empty<byte>();

    private readonly byte[] _readBuf = new byte[256 * 1024];
    private long _epoch = 1;
    private long _channelsPacked = Pack(1, 1);
    private long _sequence;
    private long _dropped;
    private bool _opened;

    public OfdrTcpSource(TcpScanOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log;
    }

    public string Name => $"TCP {_options.Host}:{_options.Port}";

    public long DroppedFrames => Interlocked.Read(ref _dropped);

    private static long Pack(int inCh, int outCh) => ((long)inCh << 32) | (uint)outCh;

    private static int PackedIn(long packed) => (int)(packed >> 32);

    private static int PackedOut(long packed) => (int)(packed & 0xFFFFFFFF);

    public void Open()
    {
        if (_opened) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _client = new TcpClient();
        _client.NoDelay = true;
        _client.Connect(_options.Host, _options.Port);
        _stream = _client.GetStream();
        _log?.Invoke($"已连接 {_options.Host}:{_options.Port}");

        var pending = new PendingBuffer(PendingCapacity);

        _log?.Invoke("等待接收 OSI ...");
        ReadUntil(pending, Encoding.ASCII.GetBytes("OSI"), "OSI", ct);
        _log?.Invoke("收到 OSI，发送 DELAY_" + _options.DelayMs + " ...");
        SendAscii("DELAY_" + _options.DelayMs);

        // 给设备留出应用延时设置的时间（与现状一致）
        if (_options.DelaySettleMs > 0)
        {
            _log?.Invoke($"DELAY 后等待 {_options.DelaySettleMs} ms ...");
            if (_cts.Token.WaitHandle.WaitOne(_options.DelaySettleMs)) return;
        }

        _log?.Invoke("等待接收 SETOK ...");
        ReadUntil(pending, Encoding.ASCII.GetBytes("SETOK"), "SETOK", ct);
        _log?.Invoke("收到 SETOK（DELAY 已应用）");

        _carry = pending.Drain();
        _opened = true;
    }

    public void StartStream()
    {
        if (!_opened || _stream == null || _cts == null)
            throw new InvalidOperationException("未连接，无法开始数据流");

        _frames = new BlockingCollection<RawFrame>(
            new ConcurrentQueue<RawFrame>(), boundedCapacity: Math.Max(1, _options.QueueCapacity));

        var ct = _cts.Token;
        _receiveThread = new Thread(() => ReceiveLoop(ct)) { IsBackground = true, Name = "OFDR-TCP接收" };
        _receiveThread.Start();

        SendAscii("START");
        _log?.Invoke("[START] 已发送 START（开始接收数据帧）");

        if (_options.StartSettleMs > 0)
        {
            _log?.Invoke($"START 后等待 {_options.StartSettleMs} ms ...");
            if (_cts.Token.WaitHandle.WaitOne(_options.StartSettleMs)) return;
        }
        _log?.Invoke("握手流程结束，进入持续采集");
    }

    public long BeginChannel(int channelIn, int channelOut)
    {
        Interlocked.Exchange(ref _channelsPacked, Pack(channelIn, channelOut));
        return Interlocked.Increment(ref _epoch);
    }

    public bool TryTake(TimeSpan timeout, out StandardScan? scan)
    {
        scan = null;
        var frames = _frames;
        if (frames == null) return false;

        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return false;
            var remaining = TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);

            RawFrame? raw;
            try
            {
                if (!frames.TryTake(out raw, remaining)) return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            if (raw.Epoch != Interlocked.Read(ref _epoch))
            {
                Interlocked.Increment(ref _dropped);
                continue;   // 通道切换残留帧：不计入新通道
            }

            scan = Decode(raw);
            return true;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }

        if (_stream != null)
        {
            try { SendAscii("STOP"); } catch { }
            try { Thread.Sleep(100); } catch { }
            try { SendAscii("STOP"); } catch { }
        }
        try { _client?.Close(); } catch { }

        try { _frames?.CompleteAdding(); } catch { }
        try { _receiveThread?.Join(2000); } catch { }

        _receiveThread = null;
        _frames = null;
        _stream = null;
        _client = null;
        _opened = false;
        _log?.Invoke("TCP 扫描源已停止");
    }

    public void Dispose() => Stop();

    private StandardScan Decode(RawFrame raw)
    {
        var values = ParseValues(raw.Bytes);
        long seq = Interlocked.Increment(ref _sequence);
        var axis = new ScanAxis(AxisKind.Distance, 0, _options.RowSpacingM, "m", values.Length);
        var metadata = new ScanMetadata(
            _options.DeviceId, _options.SdkVersion, raw.InCh, raw.OutCh, raw.Epoch,
            DateTimeOffset.Now, RequestId: null, SegmentIndex: 0,
            TriggerDelayMs: _options.DelayMs);
        return new StandardScan(
            $"{_options.Host}:{_options.Port}#{seq}", seq, axis, values, "dBm", metadata);
    }

    /// <summary>整帧一次 ASCII 解码，逐行按 InvariantCulture 解析为 dBm；空行/非法行跳过。</summary>
    internal static double[] ParseValues(byte[] frame)
    {
        var values = new List<double>(200_000);
        string text = Encoding.ASCII.GetString(frame);
        foreach (string rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.TrimEnd('\r', ' ');
            if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double dbm))
                values.Add(dbm);
        }
        return values.ToArray();
    }

    private void ReceiveLoop(CancellationToken ct)
    {
        var stream = _stream!;
        var frames = _frames!;
        var pending = new byte[PendingCapacity];
        int pendingLen = 0;
        int scanned = 0;

        // 握手期间已收到、且排在 SETOK 之后的数据
        if (_carry.Length > 0)
        {
            Buffer.BlockCopy(_carry, 0, pending, 0, _carry.Length);
            pendingLen = _carry.Length;
            _carry = Array.Empty<byte>();
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = stream.Read(_readBuf, 0, _readBuf.Length);
                }
                catch (ObjectDisposedException) { break; }
                catch (IOException) { break; }

                if (n == 0)
                {
                    _log?.Invoke("连接被服务器关闭");
                    break;
                }

                if (pendingLen + n > pending.Length)
                {
                    _log?.Invoke($"[警告] 累积缓冲超限（仍无 '!'），丢弃当前缓冲");
                    pendingLen = 0;
                    scanned = 0;
                }

                Buffer.BlockCopy(_readBuf, 0, pending, pendingLen, n);
                pendingLen += n;

                int start = 0;
                for (int i = scanned; i < pendingLen; i++)
                {
                    if (pending[i] != FrameEnd) continue;

                    var bytes = new byte[i - start + 1];
                    Buffer.BlockCopy(pending, start, bytes, 0, bytes.Length);

                    long packed = Interlocked.Read(ref _channelsPacked);
                    frames.Add(new RawFrame(
                        bytes,
                        Interlocked.Read(ref _epoch),
                        PackedIn(packed),
                        PackedOut(packed)), ct);

                    start = i + 1;
                }
                scanned = pendingLen;

                if (start > 0)
                {
                    Buffer.BlockCopy(pending, start, pending, 0, pendingLen - start);
                    pendingLen -= start;
                    scanned -= start;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Invoke($"[接收线程异常] {ex.Message}");
        }
        finally
        {
            try { frames.CompleteAdding(); } catch { }
            _log?.Invoke("接收线程结束");
        }
    }

    private void ReadUntil(PendingBuffer pending, byte[] token, string what, CancellationToken ct)
    {
        while (true)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            int n = _stream!.Read(_readBuf, 0, _readBuf.Length);
            if (n == 0) throw new IOException($"连接被服务器关闭（未收到 {what}）");

            pending.Append(_readBuf, n);
            int idx = pending.IndexOf(token);
            if (idx < 0) continue;

            pending.Consume(idx + token.Length);
            return;
        }
    }

    private void SendAscii(string text)
    {
        var data = Encoding.ASCII.GetBytes(text + "\r\n");
        _stream!.Write(data, 0, data.Length);
        _stream.Flush();
    }

    private sealed class RawFrame
    {
        public RawFrame(byte[] bytes, long epoch, int inCh, int outCh)
        {
            Bytes = bytes;
            Epoch = epoch;
            InCh = inCh;
            OutCh = outCh;
        }

        public byte[] Bytes { get; }
        public long Epoch { get; }
        public int InCh { get; }
        public int OutCh { get; }
    }

    /// <summary>握手用的字节累积缓冲：支持查找子串、消费前缀、取剩余。</summary>
    private sealed class PendingBuffer
    {
        private readonly byte[] _buffer;
        private int _length;

        public PendingBuffer(int capacity) => _buffer = new byte[capacity];

        public void Append(byte[] source, int count)
        {
            if (_length + count > _buffer.Length)
                throw new IOException("握手缓冲超限");
            Buffer.BlockCopy(source, 0, _buffer, _length, count);
            _length += count;
        }

        public int IndexOf(byte[] token)
        {
            for (int i = 0; i + token.Length <= _length; i++)
            {
                int j = 0;
                while (j < token.Length && _buffer[i + j] == token[j]) j++;
                if (j == token.Length) return i;
            }
            return -1;
        }

        public void Consume(int count)
        {
            Buffer.BlockCopy(_buffer, count, _buffer, 0, _length - count);
            _length -= count;
        }

        public byte[] Drain()
        {
            var rest = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, rest, 0, _length);
            _length = 0;
            return rest;
        }
    }
}
