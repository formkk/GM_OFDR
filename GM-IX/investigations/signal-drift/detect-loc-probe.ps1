$ErrorActionPreference = 'Stop'
$sdkDir = 'C:\Mycode\GM-OFDR\.tmp\probe_run'
[Environment]::CurrentDirectory = $sdkDir
$env:PATH = $sdkDir + ';' + $env:PATH

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class LocProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string p);

    [StructLayout(LayoutKind.Sequential)]
    public struct OfdrConfig { public double fs; public double dist_scale; }
    [StructLayout(LayoutKind.Sequential)]
    public struct OfdrResult
    {
        public int error; public IntPtr error_msg; public IntPtr dist; public IntPtr freq;
        public IntPtr ps; public UIntPtr len;
    }
    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern OfdrConfig ofdr_config_default();
    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern OfdrResult ofdr_process(IntPtr p, IntPtr s, IntPtr aux, UIntPtr len, ref OfdrConfig cfg);
    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ofdr_result_free(ref OfdrResult result);

    [StructLayout(LayoutKind.Sequential)]
    public struct DetectConfig
    {
        public double nf_q; public double nf_radius_m; public UIntPtr pk_baseline_win;
        public double pk_min_prominence_db; public double pk_min_distance_m;
        public double pk_exclude_radius_m; public double pk_seg_percentile; public double pk_last_seg_tail_m;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct DetectResult
    {
        public int error; public IntPtr error_msg; public IntPtr ps_db; public IntPtr baseline_db;
        public UIntPtr len; public UIntPtr n_peaks;
        public IntPtr peak_loc; public IntPtr peak_height; public IntPtr peak_prominence; public IntPtr peak_base;
        public UIntPtr n_segments; public IntPtr seg_start; public IntPtr seg_end; public IntPtr seg_noise_floor;
        public UIntPtr n_events; public IntPtr event_loc; public IntPtr event_peak; public IntPtr event_il;
        public IntPtr event_rl; public IntPtr event_base_before; public IntPtr event_base_after;
    }
    [DllImport("detect.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern DetectConfig detect_config_default();
    [DllImport("detect.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern DetectResult detect_process(IntPtr dist, IntPtr ps, UIntPtr len, ref DetectConfig cfg);
    [DllImport("detect.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void detect_result_free(ref DetectResult result);

    static double[] ReadDoubles(IntPtr p, UIntPtr n)
    {
        int len = (int)n.ToUInt64();
        if (len <= 0) return new double[0];
        var d = new double[len];
        Marshal.Copy(p, d, 0, len);
        return d;
    }

    public static string[] Run(string rawPath, double fs, double scale, int show)
    {
        byte[] raw = File.ReadAllBytes(rawPath);
        int n = raw.Length / 8;
        double[] p = new double[n], s = new double[n], a = new double[n];
        for (int i = 0; i < n; i++)
        {
            p[i] = BitConverter.ToInt16(raw, i * 8);
            s[i] = BitConverter.ToInt16(raw, i * 8 + 2);
            a[i] = BitConverter.ToInt16(raw, i * 8 + 4);
        }

        var lines = new System.Collections.Generic.List<string>();
        IntPtr hp = IntPtr.Zero, hs = IntPtr.Zero, ha = IntPtr.Zero;
        try
        {
            hp = Marshal.AllocHGlobal(n * sizeof(double));
            hs = Marshal.AllocHGlobal(n * sizeof(double));
            ha = Marshal.AllocHGlobal(n * sizeof(double));
            Marshal.Copy(p, 0, hp, n);
            Marshal.Copy(s, 0, hs, n);
            Marshal.Copy(a, 0, ha, n);

            var cfg = ofdr_config_default();
            cfg.fs = fs;
            cfg.dist_scale = scale;
            var res = ofdr_process(hp, hs, ha, new UIntPtr((uint)n), ref cfg);
            try
            {
                lines.Add(string.Format("ofdr: error={0} len={1}", res.error, res.len.ToUInt64()));
                if (res.error != 0) return lines.ToArray();

                double[] dist = ReadDoubles(res.dist, res.len);
                lines.Add(string.Format("dist: first={0:F6} last={1:F6} step={2:F9}",
                    dist[0], dist[dist.Length - 1], dist[1] - dist[0]));

                var dcfg = detect_config_default();

                // A: original dist
                var dr = detect_process(res.dist, res.ps, res.len, ref dcfg);
                double[] locA = null;
                try
                {
                    lines.Add(string.Format("detect(original dist): error={0} peaks={1}", dr.error, dr.n_peaks.ToUInt64()));
                    locA = ReadDoubles(dr.peak_loc, dr.n_peaks);
                }
                finally { detect_result_free(ref dr); }

                // B: dist scaled by 2
                IntPtr d2 = Marshal.AllocHGlobal(dist.Length * sizeof(double));
                try
                {
                    var dist2 = new double[dist.Length];
                    for (int i = 0; i < dist.Length; i++) dist2[i] = dist[i] * 2.0;
                    Marshal.Copy(dist2, 0, d2, dist.Length);
                    var dr2 = detect_process(d2, res.ps, res.len, ref dcfg);
                    try
                    {
                        lines.Add(string.Format("detect(dist*2): error={0} peaks={1}", dr2.error, dr2.n_peaks.ToUInt64()));
                        double[] locB = ReadDoubles(dr2.peak_loc, dr2.n_peaks);

                        if (locA != null && locB.Length > 0 && locA.Length > 0)
                        {
                            int m = Math.Min(show, Math.Min(locA.Length, locB.Length));
                            lines.Add("idx | locA(original) | nearest dist[i] | delta    | locB(dist*2) | locB/locA");
                            for (int k = 0; k < m; k++)
                            {
                                double best = double.MaxValue; int bi = -1;
                                for (int i = 0; i < dist.Length; i++)
                                {
                                    double d = Math.Abs(dist[i] - locA[k]);
                                    if (d < best) { best = d; bi = i; }
                                }
                                double ratio = locA[k] != 0 ? locB[k] / locA[k] : double.NaN;
                                lines.Add(string.Format("{0,3} | {1,13:F6} | {2,14:F6} | {3,8:F2e0} | {4,12:F6} | {5:F3}",
                                    k + 1, locA[k], bi >= 0 ? dist[bi] : double.NaN, best, locB[k], ratio));
                            }
                        }
                    }
                    finally { detect_result_free(ref dr2); }
                }
                finally { Marshal.FreeHGlobal(d2); }
            }
            finally { ofdr_result_free(ref res); }
        }
        finally
        {
            if (hp != IntPtr.Zero) Marshal.FreeHGlobal(hp);
            if (hs != IntPtr.Zero) Marshal.FreeHGlobal(hs);
            if (ha != IntPtr.Zero) Marshal.FreeHGlobal(ha);
        }
        return lines.ToArray();
    }
}
"@

if (-not [LocProbe]::SetDllDirectory($sdkDir)) { throw 'SetDllDirectory failed' }

$raw = 'C:\Mycode\GM-OFDR\GM-IX\investigations\signal-drift\raw_batch1\frame_01.bin'
Write-Host ("raw: " + $raw)
foreach ($line in [LocProbe]::Run($raw, 50e6, 1.0, 8)) { Write-Host $line }
