using Microsoft.Win32;
using System.IO.Ports;   // SerialPort / Parity / StopBits
using System.Text;       // StringBuilder

namespace OFDRDataReceiver;

/// <summary>
/// 串口助手：用于「开始测试」时打开光开关 / 矩阵开关两个串口，并发送切换命令。
/// 支持“打开并保持端口”，以便在 IN×OUT 双层循环中反复发送切换命令。
///
/// 端口枚举说明：.NET 6 中 System.IO.Ports 不再是内置程序集，且即使引用 NuGet 包，
/// SerialPort.GetPortNames() 在部分环境下也不可靠，因此改用读取注册表
/// HKEY_LOCAL_MACHINE\HARDWARE\DEVICEMAP\SERIALCOMM 枚举，零额外依赖。
/// </summary>
public static class SerialHelper
{
    /// <summary>串口波特率（8N1）。若设备实际波特率不同，改此常量即可。</summary>
    public static int BaudRate = 9600;

    /// <summary>读超时（毫秒）：IDN 等查询的默认读取等待。</summary>
    public static int ReadTimeoutMs = 2000;

    /// <summary>切换命令的回显读取超时（毫秒），较短以免无回显时久等。
    /// 实测设备响应约 410ms（发送→回显间隔），故取 800ms 留足余量，避免回显被截断。</summary>
    public static int EchoTimeoutMs = 800;

    /// <summary>占位符：命令模板中出现的 {ch} 会被替换为具体通道号（1~8）。</summary>
    public const string ChannelPlaceholder = "{ch}";

    /// <summary>补零占位符：命令模板中出现的 {ch:00} 会被替换为两位通道号（1 → "01"）。
    /// 设备（GK-OSW 系列）要求两位通道号，如 &lt;SETSW1-01&gt;。</summary>
    public const string ChannelPlaceholderPadded = "{ch:00}";

    /// <summary>
    /// 枚举本机串口（注册表方式），并按串口号自然排序（COM2 排在 COM10 之前）。
    /// </summary>
    public static List<string> GetPortNames()
    {
        var ports = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key != null)
            {
                foreach (var name in key.GetValueNames())
                {
                    var val = key.GetValue(name)?.ToString();
                    if (!string.IsNullOrWhiteSpace(val) && !ports.Contains(val))
                        ports.Add(val);
                }
            }
        }
        catch { }

        try { ports.Sort(CompareCom); } catch { }
        return ports;
    }

    /// <summary>串口号自然排序：COM2 排在 COM10 之前。</summary>
    public static int CompareCom(string? a, string? b)
    {
        if (a is null) return b is null ? 0 : -1;
        if (b is null) return 1;
        int na = ParseTailNumber(a), nb = ParseTailNumber(b);
        if (na >= 0 && nb >= 0 && na != nb) return na.CompareTo(nb);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseTailNumber(string s)
    {
        int i = s.Length;
        while (i > 0 && char.IsDigit(s[i - 1])) i--;
        if (i == s.Length) return -1;
        return int.TryParse(s[i..], out int n) ? n : -1;
    }

    /// <summary>
    /// 打开指定串口并保持打开，返回 SerialPort（调用方负责在用完后 Close/Dispose）。
    /// 未选择有效串口 / 打开失败返回 null。
    /// </summary>
    public static SerialPort? OpenPort(string? portName, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(portName) || portName == NoPort)
        {
            log("[串口] 未选择有效串口，跳过");
            return null;
        }
        try
        {
            var sp = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One);
            sp.NewLine = "\r";          // 命令以回车(\r)结尾
            sp.ReadTimeout = ReadTimeoutMs;
            sp.WriteTimeout = ReadTimeoutMs;
            sp.Open();
            log($"[串口] 已打开 {portName} ({BaudRate}, 8N1)");
            return sp;
        }
        catch (Exception ex)
        {
            log($"[串口] {portName} 打开失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>无可用串口时的占位显示文本。</summary>
    public const string NoPort = "(无串口)";

    /// <summary>
    /// 发送一条命令（模板中的 {ch} 会被替换为 channel）并读取回显写入日志。
    /// </summary>
    public static void Send(SerialPort? sp, string template, int channel, Action<string> log, string label)
    {
        if (sp == null) return;
        // 先替换补零形式 {ch:00}（1 → "01"），再替换普通 {ch}，
        // 确保 "<SETSW1-{ch:00}>" 展开为 "<SETSW1-01>" 而非 "<SETSW1-1>"
        string cmd = template
            .Replace(ChannelPlaceholderPadded, channel.ToString("00"))
            .Replace(ChannelPlaceholder, channel.ToString());
        log($"[串口] [{label}] 发送 {cmd}");
        try
        {
            sp.WriteLine(cmd);
            ReadEcho(sp, log, cmd, label);
        }
        catch (Exception ex)
        {
            log($"[串口] [{label}] 发送失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 一次查询的计时结果：从「发出命令」到「读回第一行回显」的耗时。
    /// 用于评估设备响应速度（如握手阶段探测 &lt;IDN?&gt; 的往返时间）。
    /// </summary>
    public readonly struct PingResult
    {
        /// <summary>是否成功发出命令（串口为 null / 发送异常时为 false）。</summary>
        public bool Sent { get; init; }

        /// <summary>往返耗时（毫秒）；未收到任何回显（超时）时为 null。</summary>
        public double? ElapsedMs { get; init; }

        /// <summary>回显内容（已 Trim）；无回显时为空串。</summary>
        public string Response { get; init; }

        /// <summary>错误信息；无异常时为 null。</summary>
        public string? Error { get; init; }

        /// <summary>是否收到回显（耗时有效）。</summary>
        public bool HasEcho => ElapsedMs.HasValue;
    }

    /// <summary>
    /// 发送查询命令并测量「发送 → 收到回显」的耗时（毫秒），如 &lt;IDN?&gt;。
    /// 与 SendQuery 的区别：本方法不改动日志语义，仅额外返回计时结果供调用方记录。
    /// 未收到任何回显（读超时）时 ElapsedMs 为 null。
    /// </summary>
    public static PingResult Ping(SerialPort? sp, string cmd, Action<string> log, string label)
    {
        if (sp == null)
            return new PingResult { Sent = false, Response = "", Error = "串口未打开" };

        log($"[串口] [{label}] 发送 {cmd}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sb = new StringBuilder();
        string? error = null;
        try
        {
            sp.WriteLine(cmd);
            sb.Append(ReadEchoRaw(sp));
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        sw.Stop();

        string resp = sb.ToString().Trim();
        // 只有真正读到内容才算「收到回显」，此时耗时才有意义
        double? elapsed = resp.Length > 0 ? sw.Elapsed.TotalMilliseconds : null;

        log($"[串口] [{label}] {cmd} 回显: {(resp.Length > 0 ? resp : "(无回显)")}" +
            (elapsed.HasValue ? $"  [耗时 {elapsed.Value:F1} ms]" : "  [超时无回显]"));
        if (error != null) log($"[串口] [{label}] 发送失败: {error}");

        return new PingResult
        {
            Sent = error == null,
            ElapsedMs = elapsed,
            Response = resp,
            Error = error,
        };
    }

    /// <summary>读取一串回显字节（读到结束符 '>' 或读超时即视为结束），返回原始文本。</summary>
    private static string ReadEchoRaw(SerialPort sp)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        int saved = sp.ReadTimeout;
        sp.ReadTimeout = EchoTimeoutMs;
        try
        {
            while (true)
            {
                int n = sp.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                // 收到设备结束符 '>'（提示符）即认为响应结束，不必再等超时
                if (sb.ToString().IndexOf('>') >= 0) break;
                if (sb.Length > 64 * 1024) break;
            }
        }
        catch (TimeoutException)
        {
            // 读超时：认为响应结束（或无响应）
        }
        finally
        {
            sp.ReadTimeout = saved;
        }
        return sb.ToString();
    }

    /// <summary>发送查询命令（无通道占位符）并读取回显，如 &lt;IDN?&gt;。</summary>
    public static void SendQuery(SerialPort? sp, string cmd, Action<string> log, string label)
    {
        if (sp == null) return;
        log($"[串口] [{label}] 发送 {cmd}");
        try
        {
            sp.WriteLine(cmd);
            ReadEcho(sp, log, cmd, label);
        }
        catch (Exception ex)
        {
            log($"[串口] [{label}] 发送失败: {ex.Message}");
        }
    }

    private static void ReadEcho(SerialPort sp, Action<string> log, string tag, string label)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        int saved = sp.ReadTimeout;
        sp.ReadTimeout = EchoTimeoutMs;
        try
        {
            while (true)
            {
                int n = sp.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                // 收到设备结束符 '>'（提示符）即认为响应结束，不必再等超时
                if (sb.ToString().IndexOf('>') >= 0) break;
                if (sb.Length > 64 * 1024) break;
            }
        }
        catch (TimeoutException)
        {
            // 读超时：认为响应结束（或无响应）
        }
        finally
        {
            sp.ReadTimeout = saved;
        }
        string resp = sb.ToString().Trim();
        log($"[串口] [{label}] {tag} 回显: {(resp.Length > 0 ? resp : "(无回显)")}");
    }
}
