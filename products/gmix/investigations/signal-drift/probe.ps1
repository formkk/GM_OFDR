param(
    [string]$RecGain = "true",
    [int]$Frames = 20,
    [string]$OutCsv = "frames.csv",
    [uint32]$DelayMs = 1
)

$ErrorActionPreference = 'Stop'
$sdkDir = 'C:\Mycode\GM-OFDR\.tmp\probe_run'
$cfgPath = Join-Path $sdkDir 'config\QT2144\FunctionalConfiguration.json'
[Environment]::CurrentDirectory = $sdkDir
$env:PATH = $sdkDir + ';' + $env:PATH

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class SignalProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string p);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int qt_acq_open(uint card, out IntPtr h);
    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int qt_acq_configure(IntPtr h, uint rate, uint ms, uint seg, int trig, uint delay);
    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int qt_acq_calc_buffer_bytes(uint rate, uint ms, uint seg, out uint bytes);
    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int qt_acq_start(IntPtr h);
    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int qt_acq_read(IntPtr h, byte[] buf, uint cap, out uint outLen);
    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int qt_acq_stop(IntPtr h);
    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int qt_acq_close(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    public struct OfdrConfig { public double fs; public double dist_scale; }
    [StructLayout(LayoutKind.Sequential)]
    public struct OfdrResult
    {
        public int error; public IntPtr error_msg; public IntPtr dist;
        public IntPtr freq; public IntPtr ps; public UIntPtr len;
    }
    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern OfdrConfig ofdr_config_default();
    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern OfdrResult ofdr_process(IntPtr p, IntPtr s, IntPtr aux, UIntPtr len, ref OfdrConfig cfg);
    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ofdr_result_free(ref OfdrResult result);

    /// <summary>Per frame 7 values: distLast, psMax, distAtPsMax, ch0Mean, ch0Std, ch0Min, ch0Max.</summary>
    public static double[] Run(uint delayMs, int frames, double fs, double scale)
    {
        var output = new double[frames * 7];
        IntPtr h = IntPtr.Zero;
        if (qt_acq_open(0, out h) != 0) return output;
        uint required = 0;
        qt_acq_calc_buffer_bytes(50, 8, 1, out required);
        qt_acq_configure(h, 50, 8, 1, 0, delayMs);

        for (int f = 0; f < frames; f++)
        {
            for (int k = 0; k < 7; k++) output[f * 7 + k] = double.NaN;
            if (qt_acq_start(h) != 0) continue;
            byte[] buf = new byte[required];
            uint got = 0;
            int rc = qt_acq_read(h, buf, required, out got);
            qt_acq_stop(h);
            if (rc != 0 || got != required) continue;

            int n = (int)(got / 8);
            double[] p = new double[n], s = new double[n], a = new double[n];
            double sum = 0, sumSq = 0;
            short cmin = short.MaxValue, cmax = short.MinValue;
            for (int i = 0; i < n; i++)
            {
                short v0 = BitConverter.ToInt16(buf, i * 8);
                p[i] = v0;
                s[i] = BitConverter.ToInt16(buf, i * 8 + 2);
                a[i] = BitConverter.ToInt16(buf, i * 8 + 4);
                sum += v0; sumSq += (double)v0 * v0;
                if (v0 < cmin) cmin = v0;
                if (v0 > cmax) cmax = v0;
            }
            double mean = sum / n;
            output[f * 7 + 3] = mean;
            output[f * 7 + 4] = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
            output[f * 7 + 5] = cmin;
            output[f * 7 + 6] = cmax;

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
                    if (res.error != 0) continue;
                    int len = (int)res.len.ToUInt64();
                    double[] dist = new double[len];
                    double[] ps = new double[len];
                    Marshal.Copy(res.dist, dist, 0, len);
                    Marshal.Copy(res.ps, ps, 0, len);
                    int argmax = 0;
                    double max = double.NegativeInfinity;
                    for (int k = 0; k < len; k++)
                    {
                        if (ps[k] > max) { max = ps[k]; argmax = k; }
                    }
                    output[f * 7] = dist[len - 1];
                    output[f * 7 + 1] = max;
                    output[f * 7 + 2] = dist[argmax];
                }
                finally
                {
                    ofdr_result_free(ref res);
                }
            }
            finally
            {
                if (hp != IntPtr.Zero) Marshal.FreeHGlobal(hp);
                if (hs != IntPtr.Zero) Marshal.FreeHGlobal(hs);
                if (ha != IntPtr.Zero) Marshal.FreeHGlobal(ha);
            }
        }
        qt_acq_close(h);
        return output;
    }
}
"@

if (-not [SignalProbe]::SetDllDirectory($sdkDir)) { throw 'SetDllDirectory failed' }

$originalText = Get-Content $cfgPath -Raw
try {
    $text = $originalText -replace '"is_RecGain":\s*(true|false)', ('"is_RecGain": ' + $RecGain)
    Set-Content $cfgPath $text -Encoding UTF8 -NoNewline
    Write-Host ("is_RecGain set to {0}; capturing {1} frames (delay={2} ms)" -f $RecGain, $Frames, $DelayMs)

    $raw = [SignalProbe]::Run($DelayMs, $Frames, 50e6, 1.0)
    $rows = @()
    for ($i = 0; $i -lt $Frames; $i++) {
        $o = $i * 7
        $rows += [pscustomobject]@{
            frame      = $i + 1
            distLast   = [math]::Round($raw[$o], 6)
            psMax      = [math]::Round($raw[$o + 1], 0)
            distAtMax  = [math]::Round($raw[$o + 2], 6)
            ch0Mean    = [math]::Round($raw[$o + 3], 3)
            ch0Std     = [math]::Round($raw[$o + 4], 3)
            ch0Min     = [math]::Round($raw[$o + 5], 0)
            ch0Max     = [math]::Round($raw[$o + 6], 0)
        }
    }
    $rows | Export-Csv -Path $OutCsv -NoTypeInformation -Encoding UTF8

    $valid = $rows | Where-Object { -not [double]::IsNaN($_.distLast) }
    Write-Host ("valid frames {0}/{1} -> {2}" -f $valid.Count, $Frames, (Resolve-Path $OutCsv))
}
finally {
    Set-Content $cfgPath $originalText -Encoding UTF8 -NoNewline
    Write-Host "config restored"
}
