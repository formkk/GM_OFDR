$ErrorActionPreference = 'Stop'
$sdkDir = 'C:\Mycode\GM-OFDR\.tmp\probe_run'
[Environment]::CurrentDirectory = $sdkDir
$env:PATH = $sdkDir + ';' + $env:PATH

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class PreprocessProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string p);

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

    /// <summary>Returns distLast; mode 0=raw,1=demean,2=trunc200kPow2,3=demean+trunc,4=zeropad,5=demean+zeropad.</summary>
    public static double RunDist(string rawPath, int mode, double fs, double scale)
    {
        byte[] raw = File.ReadAllBytes(rawPath);
        int total = raw.Length / 8;
        int n = 400000;
        if (mode == 2 || mode == 3) n = 262144;
        if (mode == 4 || mode == 5) n = 524288;

        double[] p = new double[n], s = new double[n], a = new double[n];
        for (int i = 0; i < total && i < n; i++)
        {
            p[i] = BitConverter.ToInt16(raw, i * 8);
            s[i] = BitConverter.ToInt16(raw, i * 8 + 2);
            a[i] = BitConverter.ToInt16(raw, i * 8 + 4);
        }

        bool demean = (mode == 1 || mode == 3 || mode == 5);
        if (demean)
        {
            double mp = 0, ms = 0, ma = 0;
            for (int i = 0; i < n; i++) { mp += p[i]; ms += s[i]; ma += a[i]; }
            mp /= n; ms /= n; ma /= n;
            for (int i = 0; i < n; i++) { p[i] -= mp; s[i] -= ms; a[i] -= ma; }
        }

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
                if (res.error != 0) return double.NaN;
                int len = (int)res.len.ToUInt64();
                var one = new double[1];
                Marshal.Copy(new IntPtr(res.dist.ToInt64() + (len - 1) * 8), one, 0, 1);
                return one[0];
            }
            finally { ofdr_result_free(ref res); }
        }
        finally
        {
            if (hp != IntPtr.Zero) Marshal.FreeHGlobal(hp);
            if (hs != IntPtr.Zero) Marshal.FreeHGlobal(hs);
            if (ha != IntPtr.Zero) Marshal.FreeHGlobal(ha);
        }
    }
}
"@

if (-not [PreprocessProbe]::SetDllDirectory($sdkDir)) { throw 'SetDllDirectory failed' }

$rawDir = 'C:\Mycode\GM-OFDR\products\gmix\investigations\signal-drift\raw_batch1'
$modes = @(
    @{ Id = 0; Name = 'raw 400000' },
    @{ Id = 1; Name = 'demean 400000' },
    @{ Id = 2; Name = 'trunc 262144' },
    @{ Id = 3; Name = 'demean+trunc 262144' },
    @{ Id = 4; Name = 'zeropad 524288' },
    @{ Id = 5; Name = 'demean+zeropad 524288' }
    )

foreach ($m in $modes) {
    $vals = @()
    foreach ($i in 1..20) {
        $p = Join-Path $rawDir ("frame_{0:D2}.bin" -f $i)
        $v = [PreprocessProbe]::RunDist($p, $m.Id, 50e6, 1.0)
        if (-not [double]::IsNaN($v)) { $vals += $v }
    }
    if ($vals.Count -gt 1) {
        $mn = ($vals | Measure-Object -Minimum).Minimum
        $mx = ($vals | Measure-Object -Maximum).Maximum
        Write-Host ("{0,-22} n={1,2} min={2,10:F3} max={3,10:F3} mean={4,10:F3} relDev={5,7:P2}" -f `
            $m.Name, $vals.Count, $mn, $mx, ($vals | Measure-Object -Average).Average, (($mx - $mn) / $mn))
    } else {
        Write-Host ("{0,-22} failed (n={1})" -f $m.Name, $vals.Count)
    }
}
