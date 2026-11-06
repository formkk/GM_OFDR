param(
    [int]$Frames = 20,
    [string]$RawCsv = "ch0_raw_20frames.csv",
    [string]$SummaryCsv = "ch0_summary_20frames.csv",
    [uint32]$DelayMs = 1
)

$ErrorActionPreference = 'Stop'
$sdkDir = 'C:\Mycode\GM-OFDR\.tmp\probe_run'
[Environment]::CurrentDirectory = $sdkDir
$env:PATH = $sdkDir + ';' + $env:PATH

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class Ch0Capture
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

    /// <summary>采集 N 帧并导出 P 通道原始样本；返回有效帧数。</summary>
    public static int Capture(string rawCsv, string summaryCsv, int frames, uint delayMs)
    {
        IntPtr h = IntPtr.Zero;
        if (qt_acq_open(0, out h) != 0) return 0;
        uint required = 0;
        qt_acq_calc_buffer_bytes(50, 8, 1, out required);
        if (qt_acq_configure(h, 50, 8, 1, 0, delayMs) != 0) { qt_acq_close(h); return 0; }

        int perFrame = (int)(required / 8);
        short[][] data = new short[frames][];
        int valid = 0;

        for (int f = 0; f < frames; f++)
        {
            if (qt_acq_start(h) != 0) continue;
            byte[] buf = new byte[required];
            uint got = 0;
            int rc = qt_acq_read(h, buf, required, out got);
            qt_acq_stop(h);
            if (rc != 0 || got != required) continue;

            short[] ch0 = new short[perFrame];
            for (int i = 0; i < perFrame; i++)
                ch0[i] = BitConverter.ToInt16(buf, i * 8);
            data[f] = ch0;
            valid++;
        }
        qt_acq_close(h);
        if (valid == 0) return 0;

        var utf8 = new UTF8Encoding(false);
        using (var sw = new StreamWriter(rawCsv, false, utf8))
        {
            sw.Write("index");
            for (int f = 0; f < frames; f++) sw.Write(",frame_" + (f + 1));
            sw.Write("\n");
            for (int i = 0; i < perFrame; i++)
            {
                sw.Write(i);
                for (int f = 0; f < frames; f++)
                {
                    sw.Write(',');
                    short[] ch0 = data[f];
                    if (ch0 != null) sw.Write(ch0[i]);
                }
                sw.Write("\n");
            }
        }

        using (var sw = new StreamWriter(summaryCsv, false, utf8))
        {
            sw.Write("frame,valid,count,mean,std,min,max\n");
            for (int f = 0; f < frames; f++)
            {
                short[] ch0 = data[f];
                if (ch0 == null) { sw.Write((f + 1) + ",0,0,NA,NA,NA,NA\n"); continue; }
                double sum = 0, sumSq = 0;
                short min = short.MaxValue, max = short.MinValue;
                for (int i = 0; i < ch0.Length; i++)
                {
                    sum += ch0[i]; sumSq += (double)ch0[i] * ch0[i];
                    if (ch0[i] < min) min = ch0[i];
                    if (ch0[i] > max) max = ch0[i];
                }
                double mean = sum / ch0.Length;
                double std = Math.Sqrt(Math.Max(0, sumSq / ch0.Length - mean * mean));
                sw.Write(string.Format("{0},1,{1},{2:F3},{3:F3},{4},{5}\n",
                    f + 1, ch0.Length, mean, std, min, max));
            }
        }
        return valid;
    }
}
"@

if (-not [Ch0Capture]::SetDllDirectory($sdkDir)) { throw 'SetDllDirectory failed' }

Write-Host ("capturing {0} frames, delay={1} ms" -f $Frames, $DelayMs)
$valid = [Ch0Capture]::Capture($RawCsv, $SummaryCsv, $Frames, $DelayMs)
Write-Host ("valid frames {0}/{1}" -f $valid, $Frames)
foreach ($f in @($RawCsv, $SummaryCsv)) {
    if (Test-Path $f) {
        $size = (Get-Item $f).Length / 1MB
        Write-Host ("{0}  {1:F1} MB" -f (Resolve-Path $f), $size)
    }
}
