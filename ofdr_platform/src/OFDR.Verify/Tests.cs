using OFDR.Contracts;
using OFDR.Engine;
using OFDR.Infrastructure.Config;
using OFDR.Infrastructure.Persistence;
using OFDR.Infrastructure.Switches;
using OFDR.Infrastructure.Tcp;
using OFDR.Metrics;
using OFDR.Replay;

namespace OFDR.Verify;

internal static class Tests
{
    private const double RowSpacingM = 0.0002;
    private const double CmPerRow = 0.04;

    // ---------------- 契约 ----------------

    public static void ContractStandardScan()
    {
        double[] values = Enumerable.Range(0, 100).Select(i => -100.0 + i * 0.1).ToArray();
        var scan = MakeScan(values, inCh: 2, outCh: 3);

        Check.Equal(0, scan.Validate().Count, "合法扫描校验错误数");

        var countMismatch = scan with { Values = values.Take(50).ToArray() };
        Check.True(countMismatch.Validate().Any(e => e.Contains("不一致")), "轴点数与数值长度不一致应报错");

        var badChannel = MakeScan(values, inCh: 0, outCh: 3);
        Check.True(badChannel.Validate().Any(e => e.Contains("ChannelIn")), "IN 通道越界应报错");

        var back = StandardScanCodec.FromJson(StandardScanCodec.ToJson(scan));
        Check.Equal(scan.Count, back.Count, "JSON 往返点数");
        Check.Equal(scan.Values[42], back.Values[42], "JSON 往返数值");
        Check.Equal(scan.Metadata.ChannelIn, back.Metadata.ChannelIn, "JSON 往返通道");
        Check.Equal(scan.ValueUnit, back.ValueUnit, "JSON 往返单位");
        Check.True(
            StandardScanCodec.ComputeChecksum(scan) == StandardScanCodec.ComputeChecksum(back),
            "JSON 往返后校验和应一致");

        var metadataOnly = scan with { Metadata = scan.Metadata with { Epoch = 999 } };
        Check.True(
            StandardScanCodec.ComputeChecksum(scan) == StandardScanCodec.ComputeChecksum(metadataOnly),
            "校验和不应受元数据影响");

        var valueChanged = scan with { Values = values.Select(v => v + 1).ToArray() };
        Check.True(
            StandardScanCodec.ComputeChecksum(scan) != StandardScanCodec.ComputeChecksum(valueChanged),
            "数值变化应改变校验和");
    }

    public static void ContractConfigId()
    {
        var a = new MetricsConfig();
        var b = new MetricsConfig();
        Check.Equal(a.ComputeId(), b.ComputeId(), "同配置快照标识");
        Check.Equal(12, a.ComputeId().Length, "快照标识长度（SHA256 前 6 字节十六进制）");

        var changed = new MetricsConfig { PeakThresholdDbm = -80 };
        Check.True(a.ComputeId() != changed.ComputeId(), "阈值变化应改变快照标识");
        Check.Equal(0, new MetricsConfig().Validate().Count, "默认配置应合法");
    }

    // ---------------- 峰值检测 ----------------

    public static void PeakMergeBoundary()
    {
        var data = new double[200];
        Array.Fill(data, -100.0);
        for (int i = 10; i <= 12; i++) data[i] = -20.0;
        for (int i = 20; i <= 22; i++) data[i] = -30.0;

        // 段间隔 = 20 - 12 - 1 = 7；gap <= mergeGap 合并（宽度覆盖合并后的整段 [10,22]，与 demo 一致）
        var merged = PeakDetector.FindByThreshold(data, -50.0, 7);
        Check.Equal(1, merged.Peaks.Count, "gap=7/mergeGap=7 应合并为 1 峰");
        Check.Equal(13, merged.Peaks[0].Width, "合并峰宽度（含中间未过阈行）");
        Check.Equal(10, merged.Peaks[0].Index, "合并峰最大值行");
        Check.Equal(13, merged.AboveThreshold, "过阈行计数（含合并间隔，与 demo 一致）");
        // 质心 = Σ(row·mW)/ΣmW：(10+11+12)·0.01 + (20+21+22)·0.001 = 0.393；权重 0.033（间隙 1e-10 mW 可忽略）
        Check.Close(0.393 / 0.033, merged.Peaks[0].Centroid, 1e-5, "合并峰质心");

        var split = PeakDetector.FindByThreshold(data, -50.0, 6);
        Check.Equal(2, split.Peaks.Count, "gap=7/mergeGap=6 应切分为 2 峰");
        Check.Equal(3, split.Peaks[0].Width, "第 1 峰宽度");
        Check.Equal(3, split.Peaks[1].Width, "第 2 峰宽度");
        Check.Equal(10, split.Peaks[0].Index, "第 1 峰行");
        Check.Equal(20, split.Peaks[1].Index, "第 2 峰行");
        Check.Equal(6, split.AboveThreshold, "过阈行数（切分）");
    }

    // ---------------- 包络降采样 ----------------

    public static void TraceEnvelopeKeepsPeaks()
    {
        var values = new double[4000];
        Array.Fill(values, -80.0);
        values[1999] = -10.0; // 单行窄峰

        TraceEnvelope.MinMax(values, 400, out var min, out var max);
        Check.Equal(400, max.Length, "包络桶数");
        Check.Equal(400, min.Length, "最小包络桶数");
        Check.True(max.Any(v => v == -10.0), "单行尖峰应保留在 max 包络");
        Check.True(min.All(v => v == -80.0), "底噪段 min 包络恒为底噪");
        Check.Equal(-80.0, max[0], "首桶");
        Check.Equal(-80.0, max[399], "末桶覆盖到末点");

        TraceEnvelope.MinMax(values, 8000, out var wide, out _);
        Check.Equal(values.Length, wide.Length, "桶数超过点数时应退化为逐点");

        TraceEnvelope.MinMax(values, 0, out var none, out _);
        Check.Equal(0, none.Length, "非法桶数返回空");

        TraceEnvelope.MinMax(Array.Empty<double>(), 10, out var empty, out _);
        Check.Equal(0, empty.Length, "空输入返回空");
    }

    // ---------------- 事件抽取 ----------------

    public static void EventExtractSynthetic()
    {
        // 3000 行：3 个单行反射峰，基线在两峰中点处逐段下降 0.5 dB
        var values = new double[3000];
        for (int i = 0; i < values.Length; i++)
            values[i] = i < 800 ? -80.0 : i < 1600 ? -80.5 : -81.0;
        values[400] = -20.0;
        values[1200] = -25.0;
        values[2000] = -30.0;

        var config = new MetricsConfig { PeakThresholdDbm = -50, PeakMergeGap = 50 };
        var events = EventExtractor.Extract(MakeScan(values), config);

        Check.Equal(3, events.Count, "事件数（不受 8 峰约束）");
        Check.Equal(400, events[0].Row, "第 1 事件行");
        Check.Close(0.08, events[0].DistanceM, 1e-9, "第 1 事件距离");
        Check.Close(0.24, events[1].DistanceM, 1e-9, "第 2 事件距离");
        Check.Close(0.40, events[2].DistanceM, 1e-9, "第 3 事件距离");
        Check.Close(0.2, events[0].WidthMm, 1e-9, "单行峰宽度（mm）");
        Check.True(double.IsNaN(events[0].SegmentLossDb), "首事件段插损为 NaN");
        Check.True(double.IsNaN(events[0].CumulativeLossDb), "首事件累积为 NaN");
        Check.Close(0.5, events[1].SegmentLossDb, 1e-6, "第 1 段插损");
        Check.Close(0.5, events[2].SegmentLossDb, 1e-6, "第 2 段插损");
        Check.Close(0.5, events[1].CumulativeLossDb, 1e-6, "累积到第 2 事件");
        Check.Close(1.0, events[2].CumulativeLossDb, 1e-6, "累积到第 3 事件");

        var quiet = Enumerable.Repeat(-90.0, 3000).ToArray();
        Check.Equal(0, EventExtractor.Extract(MakeScan(quiet), config).Count, "无峰时返回空列表");
        Check.Equal(0, EventExtractor.Extract(MakeScan(Array.Empty<double>()), config).Count, "空扫描返回空列表");
    }

    // ---------------- 指标公式手算基准 ----------------

    public static void MetricGolden()
    {
        // 2000 行，底噪恒定 -60 dBm（1e-6 mW），8 个单行峰 -10 dBm（0.1 mW）
        var values = new double[2000];
        Array.Fill(values, -60.0);
        foreach (int row in new[] { 100, 300, 500, 700, 900, 1100, 1300, 1500 })
            values[row] = -10.0;

        var config = new MetricsConfig
        {
            PeakThresholdDbm = -50,
            PeakMergeGap = 50,
            MarkARow = 800,
            MarkDRow = 1500,
            AvgHalfWindow = 0,
            LinkLenPerRow = RowSpacingM,
            DutPathPerRow = RowSpacingM,
        };

        var references = new ReferenceValues { SroBase = 1.0 };
        references.Sri[2] = 0.5;
        references.Sro[3] = 0.25;
        references.Srb[2] = 0.1;
        references.Src[3] = 0.2;

        var scan = MakeScan(values, inCh: 2, outCh: 3);
        var r = MetricCalculator.Compute(scan, config, references);

        Check.True(r.IsValid, $"8 峰输入应有效，实际 {r.Status}/{r.Error}：{r.Message}");
        Check.Equal(8, r.PeakCount, "峰数");
        Check.Equal(config.ComputeId(), r.ConfigId, "结果携带配置快照");

        var m = r.Markers ?? throw new InvalidOperationException("有效结果应包含标记点");
        Check.Close(800, m.A, 0, "A 行");
        Check.Close(700, m.B, 0, "B 行（第 4 峰）");
        Check.Close(800, m.Bb, 0, "BB 行（第 4/5 峰中点）");
        Check.Close(1200, m.Cc, 0, "CC 行（第 6/7 峰中点）");
        Check.Close(1300, m.C, 0, "C 行（第 7 峰）");
        Check.Close(1500, m.D, 0, "D 行");
        Check.Close(1500, m.E, 0, "E 行（第 8 峰）");

        Check.Close(0.12, r.DutPathM, 1e-12, "DUT 光程 = (C-B)×0.0002");
        Check.Close(0.30, r.LinkLengthM, 1e-12, "链路长度 = E×0.0002");

        // IL：aDbm=-60（行 800）、dDbm=-10（行 1500）、bbDbm=-60（行 800）、ccDbm=-60（行 1200）
        Check.Close(-23.25, r.IlDut, 1e-9, "IL-DUT");
        Check.Close(0.10, r.IlIn, 1e-9, "IL-IN");
        Check.Close(-24.80, r.IlOut, 1e-9, "IL-OUT");

        Check.Close(1.0, r.SroBase, 1e-12, "SRO 基准");
        Check.Close(0.5, r.SriIn, 1e-12, "SRI-IN 基准");
        Check.Close(0.25, r.SroOut, 1e-12, "SRO-OUT 基准");
        Check.Close(0.1, r.SrbIn, 1e-12, "SRB-IN 基准");
        Check.Close(0.2, r.SrcOut, 1e-12, "SRC-OUT 基准");

        // RL：区间内 mW 求和后转 dBm（独立按定义重算，避免复用生产公式）
        Check.Close(-60.0, r.RlIn, 1e-9, "RL-IN（单行 800）");
        Check.Close(SumDbm(values, 800, 1500), r.RlDut, 1e-6, "RL-DUT（800~1500）");
        Check.Close(SumDbm(values, 1200, 1500), r.RlOut, 1e-6, "RL-OUT（1200~1500）");
    }

    public static void MetricNotEightPeaks()
    {
        var values = new double[2000];
        Array.Fill(values, -60.0);
        foreach (int row in new[] { 100, 300, 500, 700, 900, 1100, 1300 })
            values[row] = -10.0;   // 仅 7 峰

        var config = new MetricsConfig
        {
            PeakThresholdDbm = -50,
            MarkARow = 800,
            MarkDRow = 1500,
            AvgHalfWindow = 0,
        };

        var r = MetricCalculator.Compute(MakeScan(values, inCh: 2, outCh: 3), config, ReferenceValues.Zero);
        Check.Equal(MeasurementStatus.Invalid, r.Status, "状态");
        Check.Equal(MeasurementError.NotEightPeaks, r.Error, "错误码");
        Check.Equal(7, r.PeakCount, "检出峰数");
        Check.True(r.Markers == null, "无效结果不应给出标记点");
        Check.True(double.IsNaN(r.IlDut), "无效结果 IL-DUT 应为 NaN");
    }

    // ---------------- 整机 TCP ----------------

    public static void TcpEndToEnd()
    {
        using var server = new FakeOfdrServer(frames: 3, points: 64, chunkSize: 64, frameStartDelayMs: 500);
        using var source = new OfdrTcpSource(new TcpScanOptions(
            Host: "127.0.0.1",
            Port: server.Port,
            DelayMs: 420,
            DelaySettleMs: 20,
            StartSettleMs: 50,
            QueueCapacity: 8));

        source.Open();
        source.StartStream();
        long epoch = source.BeginChannel(2, 3);

        var frames = new List<StandardScan>();
        while (frames.Count < 3)
        {
            if (!source.TryTake(TimeSpan.FromSeconds(3), out var scan) || scan == null) break;
            frames.Add(scan);
        }

        Check.Equal(3, frames.Count, "应收到 3 帧");
        foreach (var f in frames)
        {
            Check.Equal(64, f.Count, "帧点数");
            Check.Equal("dBm", f.ValueUnit, "数值单位");
            Check.Equal(2, f.Metadata.ChannelIn, "通道 IN 快照");
            Check.Equal(3, f.Metadata.ChannelOut, "通道 OUT 快照");
            Check.Equal(epoch, f.Metadata.Epoch, "代次与 BeginChannel 一致");
            Check.Close(0.0002, f.Axis.Step, 1e-15, "轴步距");
            Check.Equal(0, f.Validate().Count, "在线帧应通过契约校验");
        }

        Check.Close(0.1, frames[0].Values[1], 1e-9, "首帧第 2 行数值 0.10");

        source.Stop();

        var commands = server.CommandSnapshot();
        Check.True(commands.Any(c => c.StartsWith("DELAY_420", StringComparison.Ordinal)), "应发送 DELAY_420");
        Check.True(commands.Any(c => c.StartsWith("START", StringComparison.Ordinal)), "应发送 START");
        Check.True(server.Error == null, $"假服务端不应有异常：{server.Error}");
    }

    // ---------------- 编排闭环（离线） ----------------

    public static void EngineOfflineLoop()
    {
        using var source = new SyntheticScanSource(new SyntheticSourceOptions { FrameIntervalMs = 10 });
        var engine = new TestEngine(
            source,
            new MetricsConfig(),
            ReferenceValues.Zero,
            new SimulatedOpticalSwitch(),
            new SimulatedMatrixSwitch(),
            new EngineOptions
            {
                InFrom = 1,
                InTo = 2,
                OutFrom = 1,
                OutTo = 2,
                SamplesPerChannel = 2,
                ChannelTimeoutMs = 1000,
            });

        var results = new List<MeasurementResult>();
        var aggregates = new List<ChannelAggregate>();
        var states = new List<string>();
        engine.ResultProduced += r => results.Add(r);
        engine.ChannelCompleted += a => aggregates.Add(a);
        engine.StateChanged += s => states.Add(s);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        engine.Run(cts.Token);

        Check.Equal(8, results.Count, "2×2 通道 × 2 帧 = 8 个结果");
        Check.True(results.All(r => r.IsValid), "合成谱应全部有效");
        Check.True(results.All(r => r.PeakCount == 8), "合成谱应恰好 8 峰");
        Check.True(results.All(r => r.ChannelIn is 1 or 2 && r.ChannelOut is 1 or 2), "通道范围");

        Check.Equal(4, aggregates.Count, "通道聚合数");
        Check.True(aggregates.All(a => a.Status == ChannelRunStatus.Completed), "通道状态应为完成");
        Check.True(aggregates.All(a => a.Frames == 2 && a.ValidFrames == 2), "每通道 2 帧且全部有效");
        Check.True(aggregates.All(a => double.IsFinite(a.IlDut.Avg)), "合成谱 IL-DUT 均值应为有限值");

        Check.Equal(8L, engine.TotalFrames, "引擎总帧数");
        Check.Equal(0L, engine.InvalidFrames, "引擎无效帧数");
        Check.True(states.Contains("运行中") && states.Contains("已完成"), "状态流转应包含 运行中/已完成");
    }

    // ---------------- 历史迁移/基准数据 ----------------

    public static void LegacyConfigMigration()
    {
        var configPath = Path.Combine(FindDemoDirectory() ?? "", "config.txt");
        if (!File.Exists(configPath)) throw new SkipException($"未找到 demo config.txt：{configPath}");

        var settings = LegacyConfigReader.Load(configPath);
        Check.Close(-85.0, settings.Metrics.PeakThresholdDbm, 1e-9, "PeakThresholdDb");
        Check.Equal(50, settings.Metrics.PeakMergeGap, "PeakMergeGap");
        Check.Equal((int)Math.Round(10 / CmPerRow), settings.Metrics.AvgHalfWindow, "IntegralWindowCm→半窗行");
        Check.Equal(420, settings.DelayMs, "DelayMs");
        Check.Equal(7000, settings.StartSettleMs, "StartSettleMs");
        Check.Equal(10000, settings.ChannelTimeoutMs, "ChannelTimeoutMs");

        Check.Close(1.0, settings.References.SroBase, 1e-9, "SRO");
        Check.Close(8.0, settings.References.SriAt(1), 1e-9, "SRI-1");
        Check.Close(1.0, settings.References.SriAt(8), 1e-9, "SRI-8");
        Check.Close(1.0, settings.References.SroAt(1), 1e-9, "SRO-1");
        Check.Close(8.0, settings.References.SroAt(8), 1e-9, "SRO-8");
        Check.Close(8.0, settings.References.SrbAt(1), 1e-9, "SRB-1");
        Check.Close(1.0, settings.References.SrcAt(1), 1e-9, "SRC-1");
        Check.Close(8.0, settings.References.SrcAt(8), 1e-9, "SRC-8");
    }

    public static void LegacyMetricsBaseline()
    {
        var fixtures = Path.Combine(FindProductDirectory() ?? "", "tests", "fixtures");
        if (!Directory.Exists(fixtures))
            throw new DirectoryNotFoundException($"缺少测试夹具目录：{fixtures}");

        var files = Directory.EnumerateFiles(fixtures, "ofdr_metrics_*.csv").ToList();
        if (files.Count == 0)
            throw new FileNotFoundException($"缺少结果级基准数据（ofdr_metrics_*.csv）：{fixtures}");

        var file = files.OrderByDescending(f => new FileInfo(f).Length).First();
        var rows = MetricsCsvReader.Read(file, maxRows: 50);
        Check.True(rows.Count > 0, $"历史 CSV 应可解析出记录：{file}");
        Check.True(rows.All(r => r.ChannelIn is >= 1 and <= 8 && r.ChannelOut is >= 1 and <= 8), "通道范围 1~8");
        Check.True(rows.All(r => double.IsFinite(r.IlDut) && double.IsFinite(r.RlDut)), "指标应为有限值");
        Check.True(rows.Count(r => r.PeakCount == 8) >= rows.Count / 2, "多数记录应检出 8 峰");

        string header;
        using (var reader = new StreamReader(file))
            header = reader.ReadLine() ?? string.Empty;

        string platform18 = string.Join(',', ResultCsvWriter.Header.Split(',').Take(18));
        string history18 = string.Join(',', header.Split(',').Take(18));
        Check.Equal(history18, platform18, "历史 CSV 前 18 列表头应与平台一致");
    }

    // ---------------- 辅助 ----------------

    private static StandardScan MakeScan(double[] values, int inCh = 1, int outCh = 1, string unit = "dBm") =>
        new(
            ScanId: "TEST#1",
            Sequence: 1,
            Axis: new ScanAxis(AxisKind.Distance, 0, RowSpacingM, "m", values.Length),
            Values: values,
            ValueUnit: unit,
            Metadata: new ScanMetadata("TEST", "TEST", inCh, outCh, 1, DateTimeOffset.Now));

    /// <summary>按定义独立重算 RL（闭区间、线性域求和），用于手算基准对照。</summary>
    private static double SumDbm(double[] dbm, int from, int to)
    {
        double sum = 0;
        for (int i = from; i <= to; i++) sum += Math.Pow(10, dbm[i] / 10.0);
        return 10 * Math.Log10(sum);
    }

    private static string? FindDemoDirectory()
    {
        var productDir = FindProductDirectory();
        return productDir == null
            ? null
            : Path.Combine(productDir, "OFDRDataReceiver _demo", "OFDRDataReceiver _demo");
    }

    private static string? FindProductDirectory()
    {
        var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        while (dir.Length > 0)
        {
            if (string.Equals(Path.GetFileName(dir), "ofdr_platform", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(dir);
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        return null;
    }
}
