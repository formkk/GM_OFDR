using System.Text;

namespace OFDR.Verify;

/// <summary>验证跳过（例如历史数据缺失），不计为失败。</summary>
internal sealed class SkipException : Exception
{
    public SkipException(string message) : base(message) { }
}

/// <summary>最小断言集：失败立即抛出，由 Run 汇总为 FAIL。</summary>
internal static class Check
{
    public static void True(bool condition, string label)
    {
        if (!condition) throw new AssertionException(label);
    }

    public static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{label}：期望 {expected}，实际 {actual}");
    }

    public static void Close(double expected, double actual, double tolerance, string label)
    {
        if (double.IsNaN(expected) || double.IsNaN(actual))
            throw new AssertionException($"{label}：存在 NaN（期望 {expected}，实际 {actual}）");
        if (Math.Abs(expected - actual) > tolerance)
            throw new AssertionException($"{label}：期望 {expected}±{tolerance}，实际 {actual}");
    }

    private sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }
}

internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static int _skip;
    private static readonly List<string> Failures = new();

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== OFDR 平台一期验证（测量闭环可演示） ===");
        Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"运行时：{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}" +
                          $"  架构：{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine();

        Run("契约 StandardScan：Validate / JSON / 校验和", Tests.ContractStandardScan);
        Run("契约 MetricsConfig：配置快照标识", Tests.ContractConfigId);
        Run("峰值检测：合并间隔边界与质心", Tests.PeakMergeBoundary);
        Run("包络降采样：min/max 保留窄峰", Tests.TraceEnvelopeKeepsPeaks);
        Run("事件抽取：3 峰位置/段插损/累积（不要求 8 峰）", Tests.EventExtractSynthetic);
        Run("指标公式：8 峰手算基准（IL/RL/光程/链路）", Tests.MetricGolden);
        Run("指标公式：非 8 峰判无效", Tests.MetricNotEightPeaks);
        Run("整机 TCP：握手 + 分片/粘包收帧 + 通道快照", Tests.TcpEndToEnd);
        Run("编排闭环：离线合成 IN×OUT 2×2、每通道 2 帧", Tests.EngineOfflineLoop);
        Run("迁移：config.txt 读取", Tests.LegacyConfigMigration);
        Run("历史基准数据：ofdr_metrics CSV 读取与表头一致", Tests.LegacyMetricsBaseline);

        Console.WriteLine();
        Console.WriteLine($"汇总：PASS {_pass}，FAIL {_fail}，SKIP {_skip}");
        if (_fail > 0)
        {
            Console.WriteLine("失败项：");
            foreach (var f in Failures) Console.WriteLine("  - " + f);
        }
        Console.WriteLine(_fail == 0 ? "结论：全部通过" : "结论：存在失败项");
        return _fail == 0 ? 0 : 1;
    }

    private static void Run(string name, Action body)
    {
        try
        {
            body();
            _pass++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (SkipException ex)
        {
            _skip++;
            Console.WriteLine($"[SKIP] {name}：{ex.Message}");
        }
        catch (Exception ex)
        {
            _fail++;
            Failures.Add($"{name}：{ex.Message}");
            Console.WriteLine($"[FAIL] {name}：{ex.Message}");
        }
    }
}
