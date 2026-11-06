param(
    [int]$Frames = 5,
    [string]$OutDir = "raw",
    [uint32]$DelayMs = 1
)

$ErrorActionPreference = 'Stop'
$sdkDir = 'C:\Mycode\GM-OFDR\.tmp\probe_run'
[Environment]::CurrentDirectory = $sdkDir
$env:PATH = $sdkDir + ';' + $env:PATH
New-Item -ItemType Directory -Force $OutDir | Out-Null
$outFull = (Resolve-Path $OutDir).Path

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class RawCapture
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

    /// <summary>Capture N frames and write frame_NN.bin only. Returns valid count; logs ch0 stats.</summary>
    public static int Capture(string outDir, int frames, uint delayMs)
    {
        IntPtr h = IntPtr.Zero;
        if (qt_acq_open(0, out h) != 0) return 0;
        uint required = 0;
        qt_acq_calc_buffer_bytes(50, 8, 1, out required);
        if (qt_acq_configure(h, 50, 8, 1, 0, delayMs) != 0) { qt_acq_close(h); return 0; }

        int valid = 0;
        for (int f = 0; f < frames; f++)
        {
            if (qt_acq_start(h) != 0) continue;
            byte[] buf = new byte[required];
            uint got = 0;
            int rc = qt_acq_read(h, buf, required, out got);
            qt_acq_stop(h);
            if (rc != 0 || got != required) continue;

            string path = Path.Combine(outDir, string.Format("frame_{0:D2}.bin", f + 1));
            File.WriteAllBytes(path, buf);
            valid++;

            int n = (int)(got / 8);
            double sum = 0, sumSq = 0;
            short min = short.MaxValue, max = short.MinValue;
            for (int i = 0; i < n; i++)
            {
                short v = BitConverter.ToInt16(buf, i * 8);
                sum += v; sumSq += (double)v * v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            double mean = sum / n;
            double std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
            Console.WriteLine(string.Format("  frame {0:D2}: bytes={1} ch0 mean={2:F2} std={3:F2} min={4} max={5}",
                f + 1, got, mean, std, min, max));
        }
        qt_acq_close(h);
        return valid;
    }
}
"@

if (-not [RawCapture]::SetDllDirectory($sdkDir)) { throw 'SetDllDirectory failed' }

Write-Host ("capturing {0} frames -> {1} (raw.bin only)" -f $Frames, $outFull)
$valid = [RawCapture]::Capture($outFull, $Frames, $DelayMs)
Write-Host ("valid frames {0}/{1}" -f $valid, $Frames)

Get-ChildItem $outFull -Filter *.bin | Select-Object Name, Length | Format-Table -AutoSize
$total = (Get-ChildItem $outFull -Filter *.bin | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("total {0:F1} MB" -f $total)
