// GM-IX（QT2144 板卡）硬件子项目测试探针（.NET Framework 4.x / x64 / C ABI P/Invoke）
// 用法: Qt2144Probe.exe <runtimeDir> stage0
//       Qt2144Probe.exe <runtimeDir> stage1 <rateMhz> <ms> <segs> <trigger> <delayMs> <binOut> <jsonOut>
//       Qt2144Probe.exe <runtimeDir> stage2 <binIn> <rateMhz> <jsonOut> <peaksCsv> <eventsCsv>
//
// 该程序只做测试与取证，不进入 OFDR.Platform 解决方案，也不是生产适配器。

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string lpPathName);

    // ---- qt_acq.dll ----
    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_open(uint cardIndex, out IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_close(IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_configure(IntPtr handle, uint sampleRateMhz,
        uint acqTimeMs, uint segCount, int trigger, uint triggerDelayMs);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_start(IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_read(IntPtr handle, byte[] buffer, uint capacity, out uint outLen);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_stop(IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_calc_buffer_bytes(uint sampleRateMhz, uint acqTimeMs,
        uint segCount, out uint outBytes);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int qt_acq_last_error(StringBuilder buf, UIntPtr cap);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int qt_acq_version(StringBuilder buf, UIntPtr len);

    // ---- ofdr.dll ----
    [StructLayout(LayoutKind.Sequential)]
    public struct OfdrConfig
    {
        public double fs;
        public double dist_scale;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OfdrResult
    {
        public int error;
        public IntPtr error_msg;
        public IntPtr dist;
        public IntPtr freq;
        public IntPtr ps;
        public UIntPtr len;
    }

    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern OfdrConfig ofdr_config_default();

    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern OfdrResult ofdr_process(IntPtr p, IntPtr s, IntPtr aux,
        UIntPtr len, ref OfdrConfig cfg);

    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ofdr_result_free(ref OfdrResult result);

    // ---- detect.dll ----
    [StructLayout(LayoutKind.Sequential)]
    public struct DetectConfig
    {
        public double nf_q;
        public double nf_radius_m;
        public UIntPtr pk_baseline_win;
        public double pk_min_prominence_db;
        public double pk_min_distance_m;
        public double pk_exclude_radius_m;
        public double pk_seg_percentile;
        public double pk_last_seg_tail_m;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DetectResult
    {
        public int error;
        public IntPtr error_msg;
        public IntPtr ps_db;
        public IntPtr baseline_db;
        public UIntPtr len;
        public UIntPtr n_peaks;
        public IntPtr peak_loc;
        public IntPtr peak_height;
        public IntPtr peak_prominence;
        public IntPtr peak_base;
        public UIntPtr n_segments;
        public IntPtr seg_start;
        public IntPtr seg_end;
        public IntPtr seg_noise_floor;
        public UIntPtr n_events;
        public IntPtr event_loc;
        public IntPtr event_peak;
        public IntPtr event_il;
        public IntPtr event_rl;
        public IntPtr event_base_before;
        public IntPtr event_base_after;
    }

    [DllImport("detect.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern DetectConfig detect_config_default();

    [DllImport("detect.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern DetectResult detect_process(IntPtr dist, IntPtr ps,
        UIntPtr len, ref DetectConfig cfg);

    [DllImport("detect.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void detect_result_free(ref DetectResult result);
}

static class Json
{
    public static string Num(double v)
    {
        if (double.IsNaN(v)) return "null";
        if (double.IsInfinity(v)) return "null";
        return v.ToString("G17", CultureInfo.InvariantCulture);
    }

    public static string Str(string s)
    {
        if (s == null) return "null";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }

    public static string Bool(bool b) { return b ? "true" : "false"; }

    public static void Write(string path, string text)
    {
        string dir = Path.GetDirectoryName(path);
        if (dir != null && dir.Length > 0) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}

static class Program
{
    static string RuntimeDir;

    static string LastError()
    {
        var sb = new StringBuilder(1024);
        int rc = Native.qt_acq_last_error(sb, (UIntPtr)sb.Capacity);
        string msg = sb.ToString();
        // 实测：返回值是消息字节长度，不是状态码；成功路径可能保留上一条消息
        return msg.Length > 0 ? msg : "<empty rc=" + rc + ">";
    }

    static string Version()
    {
        var sb = new StringBuilder(256);
        int rc = Native.qt_acq_version(sb, (UIntPtr)sb.Capacity);
        return rc == 0 ? sb.ToString() : "<version rc=" + rc + ">";
    }

    static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: Qt2144Probe.exe <runtimeDir> stage0|stage1|stage2 ...");
            Environment.Exit(64);
        }

        RuntimeDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(RuntimeDir);
        Directory.CreateDirectory(Path.Combine(RuntimeDir, "log"));
        Native.SetDllDirectory(RuntimeDir);
        Directory.SetCurrentDirectory(RuntimeDir);

        string mode = args[1];
        try
        {
            if (mode == "stage0") Stage0(args);
            else if (mode == "stage1") Stage1(args);
            else if (mode == "stage2") Stage2(args);
            else if (mode == "blocktest") BlockTest(args);
            else { Console.Error.WriteLine("unknown mode: " + mode); Environment.Exit(64); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            Environment.Exit(70);
        }
    }

    // ------------------------------------------------------------------
    // 阶段 0：无硬件，验证 DLL 可加载、版本、容量公式、参数拒绝
    // ------------------------------------------------------------------
    static void Stage0(string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"stage\": \"stage0\",");
        sb.AppendLine("  \"runtime_dir\": " + Json.Str(RuntimeDir) + ",");
        sb.AppendLine("  \"process_64bit\": " + Json.Bool(Environment.Is64BitProcess) + ",");
        sb.AppendLine("  \"version\": " + Json.Str(Version()) + ",");

        // 容量公式矩阵
        uint[] rates = { 10, 50, 125, 250 };
        uint[] times = { 1, 8, 10 };
        uint[] segs = { 1, 4 };
        sb.AppendLine("  \"calc_buffer_bytes\": [");
        bool firstRow = true;
        bool formulaOk = true;
        foreach (uint rate in rates)
        {
            foreach (uint ms in times)
            {
                foreach (uint seg in segs)
                {
                    uint got = 0;
                    int rc = Native.qt_acq_calc_buffer_bytes(rate, ms, seg, out got);
                    ulong expect = (ulong)rate * ms * 8000UL * seg;
                    bool ok = rc == 0 && (ulong)got == expect;
                    if (!ok) formulaOk = false;
                    if (!firstRow) sb.AppendLine(",");
                    firstRow = false;
                    sb.Append("    {\"rate_mhz\": " + rate + ", \"time_ms\": " + ms +
                              ", \"segs\": " + seg + ", \"rc\": " + rc +
                              ", \"bytes\": " + got + ", \"expect\": " + expect +
                              ", \"ok\": " + Json.Bool(ok) + "}");
                    Console.WriteLine("calc rate={0} ms={1} seg={2} rc={3} bytes={4} expect={5} ok={6}",
                        rate, ms, seg, rc, got, expect, ok);
                }
            }
        }
        sb.AppendLine();
        sb.AppendLine("  ],");
        sb.AppendLine("  \"formula_all_ok\": " + Json.Bool(formulaOk) + ",");

        // 参数拒绝矩阵（不打开硬件；句柄为 0）
        var rejects = new[]
        {
            new { Name = "rate=30", Rate = 30u, Ms = 1u, Seg = 1u, Trig = 0 },
            new { Name = "time=0", Rate = 50u, Ms = 0u, Seg = 1u, Trig = 0 },
            new { Name = "segs=0", Rate = 50u, Ms = 1u, Seg = 0u, Trig = 0 },
            new { Name = "trigger=99", Rate = 50u, Ms = 1u, Seg = 1u, Trig = 99 },
        };
        sb.AppendLine("  \"invalid_params\": [");
        bool firstRej = true;
        foreach (var r in rejects)
        {
            uint bytes = 0;
            int calcRc = Native.qt_acq_calc_buffer_bytes(r.Rate, r.Ms, r.Seg, out bytes);
            string calcErr = LastError();
            int cfgRc = Native.qt_acq_configure(IntPtr.Zero, r.Rate, r.Ms, r.Seg, r.Trig, 0);
            string cfgErr = LastError();
            if (!firstRej) sb.AppendLine(",");
            firstRej = false;
            sb.Append("    {\"case\": " + Json.Str(r.Name) + ", \"calc_rc\": " + calcRc +
                      ", \"calc_error\": " + Json.Str(calcErr) + ", \"configure_rc\": " + cfgRc +
                      ", \"configure_error\": " + Json.Str(cfgErr) + "}");
            Console.WriteLine("reject {0}: calc_rc={1} configure_rc={2} msg={3}", r.Name, calcRc, cfgRc, calcErr);
        }
        sb.AppendLine();
        sb.AppendLine("  ],");

        // NULL 句柄行为
        uint dummyLen = 0;
        int nullRead = Native.qt_acq_read(IntPtr.Zero, new byte[8], 8, out dummyLen);
        int nullStart = Native.qt_acq_start(IntPtr.Zero);
        int nullStop = Native.qt_acq_stop(IntPtr.Zero);
        int nullClose = Native.qt_acq_close(IntPtr.Zero);
        sb.AppendLine("  \"null_handle\": {\"read_rc\": " + nullRead + ", \"start_rc\": " + nullStart +
                      ", \"stop_rc\": " + nullStop + ", \"close_rc\": " + nullClose + "},");

        // 处理库离线自检（合成数据，不依赖硬件）
        int n = 8192;
        double[] p = new double[n], s = new double[n], a = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i;
            p[i] = Math.Sin(2 * Math.PI * 0.03 * t);
            s[i] = Math.Sin(2 * Math.PI * 0.03 * t + 0.5);
            a[i] = Math.Sin(2 * Math.PI * 0.02 * t);
        }
        IntPtr pp = Marshal.AllocHGlobal(n * 8), ps = Marshal.AllocHGlobal(n * 8), pa = Marshal.AllocHGlobal(n * 8);
        Marshal.Copy(p, 0, pp, n); Marshal.Copy(s, 0, ps, n); Marshal.Copy(a, 0, pa, n);
        var cfg = Native.ofdr_config_default();
        cfg.fs = 50e6;
        cfg.dist_scale = 1.0;
        var res = Native.ofdr_process(pp, ps, pa, (UIntPtr)n, ref cfg);
        string ofdrErr = res.error == 0 ? "" : Marshal.PtrToStringAnsi(res.error_msg);
        sb.AppendLine("  \"ofdr_synthetic\": {\"error\": " + res.error + ", \"error_msg\": " +
                      Json.Str(ofdrErr) + ", \"len\": " + res.len.ToString() + "},");
        Console.WriteLine("ofdr synthetic error={0} msg={1} len={2}", res.error, ofdrErr, res.len.ToString());
        if (res.error == 0)
        {
            double[] dist = ReadDoubles(res.dist, res.len);
            double[] psd = ReadDoubles(res.ps, res.len);
            sb.AppendLine("  \"ofdr_synthetic_ps_max\": " + Json.Num(MaxOf(psd)) + ",");
            sb.AppendLine("  \"ofdr_synthetic_dist\": " + Json.Num(dist[0]) + ",");
            sb.AppendLine("  \"dist_monotonic\": " + Json.Bool(IsMonotonic(dist)) + ",");

            // detect 离线自检：直接消费 ofdr 的合成输出（必须在 free 之前）
            var dcfg = Native.detect_config_default();
            var dres = Native.detect_process(res.dist, res.ps, res.len, ref dcfg);
            string dmsg = dres.error == 0 ? "" : Marshal.PtrToStringAnsi(dres.error_msg);
            sb.AppendLine("  \"detect_synthetic\": {\"error\": " + dres.error + ", \"error_msg\": " +
                          Json.Str(dmsg) + ", \"n_peaks\": " + dres.n_peaks.ToString() +
                          ", \"n_segments\": " + dres.n_segments.ToString() + "},");
            Console.WriteLine("detect synthetic error={0} msg={1} n_peaks={2} n_segments={3}",
                dres.error, dmsg, dres.n_peaks.ToString(), dres.n_segments.ToString());
            Native.detect_result_free(ref dres);
        }
        Native.ofdr_result_free(ref res);
        Marshal.FreeHGlobal(pp); Marshal.FreeHGlobal(ps); Marshal.FreeHGlobal(pa);

        sb.AppendLine("  \"ok\": " + Json.Bool(formulaOk));

        string jsonPath = Path.Combine(RuntimeDir, "..", "results", "stage0.json");
        sb.AppendLine("}");
        Json.Write(jsonPath, sb.ToString());
        Console.WriteLine("stage0 json -> " + Path.GetFullPath(jsonPath));
        Environment.Exit(formulaOk ? 0 : 2);
    }

    // ------------------------------------------------------------------
    // 阶段 1：实机采集
    // ------------------------------------------------------------------
    static void Stage1(string[] args)
    {
        if (args.Length < 9)
        {
            Console.Error.WriteLine("usage: stage1 <rateMhz> <ms> <segs> <trigger> <delayMs> <binOut> <jsonOut> [cardIndex]");
            Environment.Exit(64);
        }
        uint rate = uint.Parse(args[2]);
        uint ms = uint.Parse(args[3]);
        uint segs = uint.Parse(args[4]);
        int trig = int.Parse(args[5]);
        uint delay = uint.Parse(args[6]);
        string binOut = Path.GetFullPath(args[7]);
        string jsonOut = Path.GetFullPath(args[8]);
        uint card = args.Length > 9 ? uint.Parse(args[9]) : 0u;

        var sw = new Stopwatch();
        IntPtr h = IntPtr.Zero;
        int openRc = -99, cfgRc = -99, startRc = -99, readRc = -99, stopRc = -99, closeRc = -99;
        string openErr = "", cfgErr = "", startErr = "", readErr = "", stopErr = "", closeErr = "";
        uint required = 0, got = 0;
        long openMs = -1, readMs = -1;
        byte[] buf = new byte[0];

        try
        {
            sw.Restart();
            openRc = Native.qt_acq_open(card, out h);
            openMs = sw.ElapsedMilliseconds;
            openErr = LastError();
            Console.WriteLine("open rc={0} handle={1} elapsed={2}ms err={3}", openRc, h, openMs, openErr);
            if (openRc != 0) { WriteStage1(jsonOut, card, rate, ms, segs, trig, delay, openRc, openErr, openMs,
                cfgRc, cfgErr, startRc, startErr, readRc, readErr, stopRc, stopErr, closeRc, closeErr,
                required, got, readMs, null, binOut, "open_failed"); Environment.Exit(3); }

            int vRc = Native.qt_acq_configure(h, rate, ms, segs, trig, delay);
            cfgRc = vRc;
            cfgErr = LastError();
            int calcRc = Native.qt_acq_calc_buffer_bytes(rate, ms, segs, out required);
            Console.WriteLine("configure rc={0} calc_rc={1} required={2} err={3}", cfgRc, calcRc, required, cfgErr);

            if (cfgRc == 0 && calcRc == 0)
            {
                buf = new byte[required];
                startRc = Native.qt_acq_start(h);
                startErr = LastError();
                Console.WriteLine("start rc={0} err={1}", startRc, startErr);
                if (startRc == 0)
                {
                    sw.Restart();
                    readRc = Native.qt_acq_read(h, buf, required, out got);
                    readMs = sw.ElapsedMilliseconds;
                    readErr = LastError();
                    Console.WriteLine("read rc={0} got={1}/{2} elapsed={3}ms err={4}",
                        readRc, got, required, readMs, readErr);
                }
            }
        }
        finally
        {
            if (h != IntPtr.Zero)
            {
                stopRc = Native.qt_acq_stop(h);
                stopErr = LastError();
                closeRc = Native.qt_acq_close(h);
                closeErr = LastError();
                Console.WriteLine("stop rc={0} close rc={1}", stopRc, closeRc);
            }
        }

        Stats[] stats = null;
        string state = "read_failed";
        if (readRc == 0 && got >= 8)
        {
            try
            {
                stats = Analyze(buf, got);
                state = (got == required) ? "ok" : "length_mismatch";
                if (binOut.Length > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(binOut));
                    using (var fs = new FileStream(binOut, FileMode.Create, FileAccess.Write))
                        fs.Write(buf, 0, (int)got);
                    Console.WriteLine("raw -> " + binOut + " (" + got + " bytes)");
                }
            }
            catch (Exception ex)
            {
                state = "analyze_failed: " + ex.Message;
            }
        }

        WriteStage1(jsonOut, card, rate, ms, segs, trig, delay, openRc, openErr, openMs,
            cfgRc, cfgErr, startRc, startErr, readRc, readErr, stopRc, stopErr, closeRc, closeErr,
            required, got, readMs, stats, binOut, state);

        Environment.Exit(readRc == 0 ? 0 : 4);
    }

    static void WriteStage1(string path, uint card, uint rate, uint ms, uint segs, int trig, uint delay,
        int openRc, string openErr, long openMs, int cfgRc, string cfgErr, int startRc, string startErr,
        int readRc, string readErr, int stopRc, string stopErr, int closeRc, string closeErr,
        uint required, uint got, long readMs, Stats[] stats, string binOut, string state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"stage\": \"stage1\",");
        sb.AppendLine("  \"state\": " + Json.Str(state) + ",");
        sb.AppendLine("  \"device_ip\": \"192.168.5.20\",");
        sb.AppendLine("  \"local_ip\": \"192.168.5.55\",");
        sb.AppendLine("  \"card_index\": " + card + ",");
        sb.AppendLine("  \"params\": {\"rate_mhz\": " + rate + ", \"time_ms\": " + ms + ", \"segs\": " + segs +
                      ", \"trigger\": " + trig + ", \"delay_ms\": " + delay + "},");
        sb.AppendLine("  \"version\": " + Json.Str(Version()) + ",");
        sb.AppendLine("  \"open\": {\"rc\": " + openRc + ", \"elapsed_ms\": " + openMs + ", \"error\": " + Json.Str(openErr) + "},");
        sb.AppendLine("  \"configure\": {\"rc\": " + cfgRc + ", \"error\": " + Json.Str(cfgErr) + "},");
        sb.AppendLine("  \"start\": {\"rc\": " + startRc + ", \"error\": " + Json.Str(startErr) + "},");
        sb.AppendLine("  \"read\": {\"rc\": " + readRc + ", \"elapsed_ms\": " + readMs + ", \"error\": " + Json.Str(readErr) + "},");
        sb.AppendLine("  \"stop\": {\"rc\": " + stopRc + ", \"error\": " + Json.Str(stopErr) + "},");
        sb.AppendLine("  \"close\": {\"rc\": " + closeRc + ", \"error\": " + Json.Str(closeErr) + "},");
        sb.AppendLine("  \"required_bytes\": " + required + ",");
        sb.AppendLine("  \"out_len\": " + got + ",");
        sb.AppendLine("  \"frames\": " + (got / 8) + ",");
        sb.AppendLine("  \"frame_aligned_8\": " + Json.Bool(got > 0 && got % 8 == 0) + ",");
        sb.AppendLine("  \"length_matches_required\": " + Json.Bool(required > 0 && got == required) + ",");
        sb.AppendLine("  \"raw_file\": " + Json.Str(binOut));
        if (stats != null)
        {
            sb.AppendLine("  ,\"channels\": [");
            for (int c = 0; c < stats.Length; c++)
            {
                if (c > 0) sb.AppendLine(",");
                Stats st = stats[c];
                sb.Append("    {\"ch\": " + c + ", \"min\": " + st.Min + ", \"max\": " + st.Max +
                          ", \"mean\": " + Json.Num(st.Mean) + ", \"std\": " + Json.Num(st.Std) +
                          ", \"rms\": " + Json.Num(st.Rms) + ", \"zero\": " + st.Zero +
                          ", \"sat_low\": " + st.SatLow + ", \"sat_high\": " + st.SatHigh +
                          ", \"distinct\": " + st.Distinct +
                          ", \"first8\": [" + JoinInts(st.First8) + "]}");
            }
            sb.AppendLine();
            sb.AppendLine("  ],");
            sb.AppendLine("  \"correlation\": {\"ch0_ch1\": " + Json.Num(stats[1].CorrToCh0) +
                          ", \"ch0_ch2\": " + Json.Num(stats[2].CorrToCh0) +
                          ", \"ch0_ch3\": " + Json.Num(stats[3].CorrToCh0) + "},");
            sb.AppendLine("  \"note\": \"通道映射已确认为 ch0=P、ch1=S、ch2=A，ch3 不使用\"");
        }
        sb.AppendLine("}");
        Json.Write(path, sb.ToString());
        Console.WriteLine("json -> " + path);
    }

    static string JoinInts(int[] a)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(','); sb.Append(a[i]); }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // 阶段 2：ofdr + detect
    // ------------------------------------------------------------------
    static void Stage2(string[] args)
    {
        if (args.Length < 7)
        {
            Console.Error.WriteLine("usage: stage2 <binIn> <rateMhz> <jsonOut> <peaksCsv> <eventsCsv>");
            Environment.Exit(64);
        }
        string binIn = Path.GetFullPath(args[2]);
        uint rate = uint.Parse(args[3]);
        string jsonOut = Path.GetFullPath(args[4]);
        string peaksCsv = Path.GetFullPath(args[5]);
        string eventsCsv = Path.GetFullPath(args[6]);

        byte[] raw = File.ReadAllBytes(binIn);
        int frames = raw.Length / 8;
        short[] samples = new short[frames * 4];
        Buffer.BlockCopy(raw, 0, samples, 0, frames * 8);

        // 通道映射：厂商回复 1P 2S 3A，功能上固定为 ch0=P、ch1=S、ch2=A，ch3 不使用
        int pCh = 0, sCh = 1, aCh = 2;
        double distScale = 1.0;
        double fsOverride = 0;
        if (args.Length > 9)
        {
            pCh = int.Parse(args[7]);
            sCh = int.Parse(args[8]);
            aCh = int.Parse(args[9]);
        }
        if (args.Length > 10) distScale = double.Parse(args[10], CultureInfo.InvariantCulture);
        if (args.Length > 11) fsOverride = double.Parse(args[11], CultureInfo.InvariantCulture);
        if (pCh < 0 || pCh > 3 || sCh < 0 || sCh > 3 || aCh < 0 || aCh > 3 ||
            pCh == sCh || pCh == aCh || sCh == aCh)
        {
            Console.Error.WriteLine("invalid channel map");
            Environment.Exit(64);
        }
        double[] p = new double[frames], s = new double[frames], a = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            p[i] = samples[i * 4 + pCh];
            s[i] = samples[i * 4 + sCh];
            a[i] = samples[i * 4 + aCh];
        }

        int usedLen = frames;
        IntPtr hp = Marshal.AllocHGlobal(frames * 8), hs = Marshal.AllocHGlobal(frames * 8), ha = Marshal.AllocHGlobal(frames * 8);
        Marshal.Copy(p, 0, hp, frames); Marshal.Copy(s, 0, hs, frames); Marshal.Copy(a, 0, ha, frames);

        var cfg = Native.ofdr_config_default();
        cfg.fs = fsOverride > 0 ? fsOverride : rate * 1e6;
        cfg.dist_scale = distScale;

        var sw = new Stopwatch();
        sw.Start();
        var res = Native.ofdr_process(hp, hs, ha, (UIntPtr)frames, ref cfg);
        long ofdrMs = sw.ElapsedMilliseconds;
        string ofdrMsg = res.error == 0 ? "" : Marshal.PtrToStringAnsi(res.error_msg);

        bool retried = false;
        if (res.error != 0)
        {
            Native.ofdr_result_free(ref res);
            // 输入长度约束未确认（TBD-F）：尝试最大 2 的幂截断
            int n2 = 1;
            while (n2 * 2 <= frames) n2 *= 2;
            if (n2 >= 1024)
            {
                usedLen = n2;
                sw.Restart();
                res = Native.ofdr_process(hp, hs, ha, (UIntPtr)n2, ref cfg);
                ofdrMs = sw.ElapsedMilliseconds;
                ofdrMsg = res.error == 0 ? "" : Marshal.PtrToStringAnsi(res.error_msg);
                retried = true;
                Console.WriteLine("ofdr retry with power-of-two len={0} error={1} msg={2}", n2, res.error, ofdrMsg);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"stage\": \"stage2\",");
        sb.AppendLine("  \"input_bin\": " + Json.Str(binIn) + ",");
        sb.AppendLine("  \"frames_in\": " + frames + ",");
        sb.AppendLine("  \"frames_used\": " + usedLen + ",");
        sb.AppendLine("  \"channel_map_assumed\": {\"p\": " + pCh + ", \"s\": " + sCh + ", \"aux\": " + aCh + "},");
        sb.AppendLine("  \"fs_hz\": " + Json.Num(cfg.fs) + ",");
        sb.AppendLine("  \"dist_scale\": " + Json.Num(cfg.dist_scale) + ",");
        sb.AppendLine("  \"ofdr\": {\"error\": " + res.error + ", \"error_msg\": " + Json.Str(ofdrMsg) +
                      ", \"elapsed_ms\": " + ofdrMs + ", \"retried_pow2\": " + Json.Bool(retried) +
                      ", \"len\": " + res.len.ToString() + "},");

        double[] dist = null, freq = null, psd = null;
        if (res.error == 0)
        {
            dist = ReadDoubles(res.dist, res.len);
            freq = ReadDoubles(res.freq, res.len);
            psd = ReadDoubles(res.ps, res.len);
            sb.AppendLine("  \"dist\": {\"first\": " + Json.Num(dist[0]) + ", \"last\": " + Json.Num(dist[dist.Length - 1]) +
                          ", \"monotonic\": " + Json.Bool(IsMonotonic(dist)) + "},");
            int lastIdx = dist.Length - 1;
            double step1 = dist.Length > 1 ? dist[1] - dist[0] : 0;
            double avg = lastIdx > 0 ? dist[lastIdx] / lastIdx : 0;
            double maxDev = 0;
            int probeN = Math.Min(10000, dist.Length);
            for (int i = 2; i < probeN; i++)
            {
                double d = Math.Abs((dist[i] - dist[i - 1]) - step1);
                if (d > maxDev) maxDev = d;
            }
            sb.AppendLine("  \"dist_probe\": {\"d1\": " + Json.Num(dist.Length > 1 ? dist[1] : 0) +
                          ", \"d1000\": " + Json.Num(dist.Length > 1000 ? dist[1000] : 0) +
                          ", \"d100000\": " + Json.Num(dist.Length > 100000 ? dist[100000] : 0) +
                          ", \"d_last\": " + Json.Num(dist[lastIdx]) +
                          ", \"step1\": " + Json.Num(step1) + ", \"avg_step\": " + Json.Num(avg) +
                          ", \"max_step_dev_first_10000\": " + Json.Num(maxDev) + "},");
            sb.AppendLine("  \"freq\": {\"first\": " + Json.Num(freq[0]) + ", \"last\": " + Json.Num(freq[freq.Length - 1]) + "},");
            int argMax = ArgMax(psd);
            sb.AppendLine("  \"ps\": {\"min\": " + Json.Num(MinOf(psd)) + ", \"max\": " + Json.Num(MaxOf(psd)) +
                          ", \"mean\": " + Json.Num(MeanOf(psd)) + ", \"argmax\": " + argMax +
                          ", \"dist_at_argmax\": " + Json.Num(dist[argMax]) + "},");

            // detect
            var dcfg = Native.detect_config_default();
            sw.Restart();
            var dres = Native.detect_process(res.dist, res.ps, res.len, ref dcfg);
            long detMs = sw.ElapsedMilliseconds;
            string dmsg = dres.error == 0 ? "" : Marshal.PtrToStringAnsi(dres.error_msg);
            sb.AppendLine("  \"detect\": {\"error\": " + dres.error + ", \"error_msg\": " + Json.Str(dmsg) +
                          ", \"elapsed_ms\": " + detMs + ", \"n_peaks\": " + dres.n_peaks.ToString() +
                          ", \"n_segments\": " + dres.n_segments.ToString() +
                          ", \"n_events\": " + dres.n_events.ToString() +
                          ", \"config\": {\"nf_q\": " + Json.Num(dcfg.nf_q) + ", \"nf_radius_m\": " + Json.Num(dcfg.nf_radius_m) +
                          ", \"pk_min_prominence_db\": " + Json.Num(dcfg.pk_min_prominence_db) +
                          ", \"pk_min_distance_m\": " + Json.Num(dcfg.pk_min_distance_m) + "}},");

            if (dres.error == 0)
            {
                int np = (int)ToLong(dres.n_peaks);
                double[] ploc = ReadDoubles(dres.peak_loc, dres.n_peaks);
                double[] phgt = ReadDoubles(dres.peak_height, dres.n_peaks);
                double[] prom = ReadDoubles(dres.peak_prominence, dres.n_peaks);
                double[] pbase = ReadDoubles(dres.peak_base, dres.n_peaks);
                double step = dist.Length > 1 ? dist[1] - dist[0] : 0;
                var csv = new StringBuilder("index,dist_m,peak_height_db,peak_base_db,ps_linear,ps_db_calc,prominence_db\n");
                for (int i = 0; i < np; i++)
                {
                    int idx = step > 0 ? (int)Math.Round((ploc[i] - dist[0]) / step) : 0;
                    if (idx < 0) idx = 0;
                    if (idx >= psd.Length) idx = psd.Length - 1;
                    double psLin = psd[idx];
                    double psDb = psLin > 0 ? 20.0 * Math.Log10(psLin) : double.NaN;
                    csv.Append(i).Append(',').Append(ploc[i].ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                        .Append(phgt[i].ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                        .Append(pbase[i].ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                        .Append(psLin.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                        .Append(psDb.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                        .Append(prom[i].ToString("G9", CultureInfo.InvariantCulture)).Append('\n');
                }
                Json.Write(peaksCsv, csv.ToString());

                int ne = (int)ToLong(dres.n_events);
                double[] eloc = ReadDoubles(dres.event_loc, dres.n_events);
                double[] epeak = ReadDoubles(dres.event_peak, dres.n_events);
                double[] eil = ReadDoubles(dres.event_il, dres.n_events);
                double[] erl = ReadDoubles(dres.event_rl, dres.n_events);
                double[] ebb = ReadDoubles(dres.event_base_before, dres.n_events);
                double[] eba = ReadDoubles(dres.event_base_after, dres.n_events);
                var ecsv = new StringBuilder("index,dist_m,peak_db,il_db,rl_db,base_before_db,base_after_db\n");
                for (int i = 0; i < ne; i++)
                    ecsv.Append(i).Append(',').Append(eloc[i].ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                        .Append(epeak[i].ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                        .Append(eil[i].ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                        .Append(erl[i].ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                        .Append(ebb[i].ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                        .Append(eba[i].ToString("G9", CultureInfo.InvariantCulture)).Append('\n');
                Json.Write(eventsCsv, ecsv.ToString());

                // 基线段明细
                int ns = (int)ToLong(dres.n_segments);
                ulong[] sStart = ReadSizes(dres.seg_start, dres.n_segments);
                ulong[] sEnd = ReadSizes(dres.seg_end, dres.n_segments);
                double[] sFloor = ReadDoubles(dres.seg_noise_floor, dres.n_segments);
                sb.AppendLine("  \"segments\": [");
                for (int i = 0; i < ns; i++)
                {
                    if (i > 0) sb.AppendLine(",");
                    sb.Append("    {\"start\": " + sStart[i] + ", \"end\": " + sEnd[i] +
                              ", \"noise_floor_db\": " + Json.Num(sFloor[i]) + "}");
                }
                sb.AppendLine();
                sb.AppendLine("  ],");
            }
            Native.detect_result_free(ref dres);
        }
        sb.AppendLine("  \"status\": " + Json.Str(res.error == 0 ? "ok" : "ofdr_failed"));
        sb.AppendLine("}");
        Json.Write(jsonOut, sb.ToString());

        Console.WriteLine("ofdr error={0} msg={1} len={2} -> {3}", res.error, ofdrMsg, res.len.ToString(), jsonOut);
        if (res.error == 0 || retried) Native.ofdr_result_free(ref res);
        Marshal.FreeHGlobal(hp); Marshal.FreeHGlobal(hs); Marshal.FreeHGlobal(ha);
        Environment.Exit(res.error == 0 ? 0 : 5);
    }

    // ------------------------------------------------------------------
    // 阻塞 read 是否可被 stop 中断（TBD-E）
    // 用法: blocktest <rateMhz> <ms> <segs> <delayMs> <jsonOut>
    // ------------------------------------------------------------------
    static void BlockTest(string[] args)
    {
        uint rate = args.Length > 2 ? uint.Parse(args[2]) : 50u;
        uint ms = args.Length > 3 ? uint.Parse(args[3]) : 8u;
        uint segs = args.Length > 4 ? uint.Parse(args[4]) : 4u;
        uint delay = args.Length > 5 ? uint.Parse(args[5]) : 0u;
        string jsonOut = args.Length > 6 ? Path.GetFullPath(args[6]) : Path.GetFullPath("blocktest.json");

        IntPtr h = IntPtr.Zero;
        int openRc = Native.qt_acq_open(0, out h);
        string openErr = LastError();
        uint required = 0;
        int cfgRc = -99, startRc = -99, stopRc = -99, closeRc = -99;
        string cfgErr = "", startErr = "", stopErr = "";
        long stopMs = -1;
        bool returnedBeforeStop = false, joined = false, closeSkipped = false;
        int readRc = -99;
        uint got = 0;

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"stage\": \"blocktest\",");
        sb.AppendLine("  \"params\": {\"rate_mhz\": " + rate + ", \"time_ms\": " + ms + ", \"segs\": " + segs +
                      ", \"delay_ms\": " + delay + "},");
        sb.AppendLine("  \"open\": {\"rc\": " + openRc + ", \"error\": " + Json.Str(openErr) + "},");

        if (openRc != 0)
        {
            sb.AppendLine("  \"conclusion\": \"open_failed\"");
            sb.AppendLine("}");
            Json.Write(jsonOut, sb.ToString());
            Environment.Exit(3);
        }

        cfgRc = Native.qt_acq_configure(h, rate, ms, segs, 0, delay);
        cfgErr = LastError();
        int calcRc = Native.qt_acq_calc_buffer_bytes(rate, ms, segs, out required);
        Console.WriteLine("blocktest open rc={0} configure rc={1} required={2}", openRc, cfgRc, required);
        startRc = calcRc == 0 ? Native.qt_acq_start(h) : -99;
        startErr = LastError();
        Console.WriteLine("blocktest start rc={0} err={1}", startRc, startErr);
        sb.AppendLine("  \"configure\": {\"rc\": " + cfgRc + ", \"error\": " + Json.Str(cfgErr) + "},");
        sb.AppendLine("  \"start\": {\"rc\": " + startRc + ", \"error\": " + Json.Str(startErr) + "},");

        if (cfgRc == 0 && calcRc == 0 && startRc == 0)
        {
            byte[] buf = new byte[required];
            var th = new Thread(delegate()
            {
                readRc = Native.qt_acq_read(h, buf, required, out got);
            });
            th.IsBackground = true;
            th.Start();
            Console.WriteLine("blocktest read thread started; sleeping 2s");
            Thread.Sleep(2000);
            returnedBeforeStop = !th.IsAlive;
            Console.WriteLine("blocktest before stop: thread_alive={0}", th.IsAlive);

            var sw = Stopwatch.StartNew();
            stopRc = Native.qt_acq_stop(h);
            stopMs = sw.ElapsedMilliseconds;
            stopErr = LastError();
            Console.WriteLine("blocktest stop returned rc={0} after {1}ms", stopRc, stopMs);

            joined = th.Join(15000);
            if (!joined)
            {
                closeSkipped = true;
            }
            else
            {
                closeRc = Native.qt_acq_close(h);
            }
        }
        else
        {
            closeRc = Native.qt_acq_close(h);
        }

        sb.AppendLine("  \"thread_returned_before_stop\": " + Json.Bool(returnedBeforeStop) + ",");
        sb.AppendLine("  \"stop\": {\"rc\": " + stopRc + ", \"elapsed_ms\": " + stopMs + ", \"error\": " + Json.Str(stopErr) + "},");
        sb.AppendLine("  \"read_joined_within_15s\": " + Json.Bool(joined) + ",");
        sb.AppendLine("  \"read\": {\"rc\": " + readRc + ", \"out_len\": " + got + "},");
        sb.AppendLine("  \"close\": {\"rc\": " + closeRc + ", \"skipped\": " + Json.Bool(closeSkipped) + "},");
        string conclusion = returnedBeforeStop ? "read_finished_before_stop"
            : (joined ? "stop_interrupted_read" : "read_still_blocked_after_stop");
        sb.AppendLine("  \"conclusion\": " + Json.Str(conclusion));
        sb.AppendLine("}");
        Json.Write(jsonOut, sb.ToString());
        Console.WriteLine("blocktest conclusion={0} stop_rc={1} stop_ms={2} read_rc={3} got={4}",
            conclusion, stopRc, stopMs, readRc, got);
        Environment.Exit(joined ? 0 : 6);
    }

    // ------------------------------------------------------------------
    // 统计辅助
    // ------------------------------------------------------------------
    class Stats
    {
        public int Min = int.MaxValue, Max = int.MinValue;
        public double Mean, Std, Rms;
        public long Zero, SatLow, SatHigh, Distinct;
        public double CorrToCh0 = double.NaN;
        public int[] First8 = new int[8];
    }

    static Stats[] Analyze(byte[] buf, uint got)
    {
        int frames = (int)(got / 8);
        var s = new Stats[4];
        var sum = new double[4];
        var sumSq = new double[4];
        var cross0 = new double[4];
        var seen = new System.Collections.Generic.HashSet<int>[4];
        for (int c = 0; c < 4; c++) { s[c] = new Stats(); seen[c] = new System.Collections.Generic.HashSet<int>(); }

        for (int i = 0; i < frames; i++)
        {
            int off = i * 8;
            int v0 = BitConverter.ToInt16(buf, off);
            for (int c = 0; c < 4; c++)
            {
                int v = BitConverter.ToInt16(buf, off + c * 2);
                Stats st = s[c];
                if (i < 8) st.First8[i] = v;
                if (v < st.Min) st.Min = v;
                if (v > st.Max) st.Max = v;
                sum[c] += v;
                sumSq[c] += (double)v * v;
                if (c > 0) cross0[c] += (double)v0 * v;
                if (v == 0) st.Zero++;
                if (v <= -32768) st.SatLow++;
                if (v >= 32767) st.SatHigh++;
                seen[c].Add(v);
            }
        }
        for (int c = 0; c < 4; c++)
        {
            double mean = sum[c] / frames;
            s[c].Mean = mean;
            s[c].Rms = Math.Sqrt(sumSq[c] / frames);
            double var = sumSq[c] / frames - mean * mean;
            s[c].Std = var > 0 ? Math.Sqrt(var) : 0;
            s[c].Distinct = seen[c].Count;
        }
        for (int c = 1; c < 4; c++)
        {
            if (s[0].Std > 0 && s[c].Std > 0)
                s[c].CorrToCh0 = (cross0[c] / frames - s[0].Mean * s[c].Mean) / (s[0].Std * s[c].Std);
        }
        return s;
    }

    static long ToLong(UIntPtr v) { return (long)v.ToUInt64(); }

    static ulong[] ReadSizes(IntPtr p, UIntPtr n)
    {
        int len = (int)ToLong(n);
        if (len <= 0) return new ulong[0];
        var buf = new long[len];
        Marshal.Copy(p, buf, 0, len);
        var d = new ulong[len];
        for (int i = 0; i < len; i++) d[i] = (ulong)buf[i];
        return d;
    }

    static double[] ReadDoubles(IntPtr p, UIntPtr n)
    {
        int len = (int)ToLong(n);
        if (len <= 0) return new double[0];
        var d = new double[len];
        Marshal.Copy(p, d, 0, len);
        return d;
    }

    static double MaxOf(double[] a) { double m = double.NegativeInfinity; foreach (double v in a) if (v > m) m = v; return m; }
    static double MinOf(double[] a) { double m = double.PositiveInfinity; foreach (double v in a) if (v < m) m = v; return m; }
    static double MeanOf(double[] a) { double s = 0; foreach (double v in a) s += v; return a.Length > 0 ? s / a.Length : 0; }
    static int ArgMax(double[] a) { int k = 0; double m = double.NegativeInfinity; for (int i = 0; i < a.Length; i++) if (a[i] > m) { m = a[i]; k = i; } return k; }
    static bool IsMonotonic(double[] a) { for (int i = 1; i < a.Length; i++) if (!(a[i] > a[i - 1])) return false; return a.Length > 1; }
}
