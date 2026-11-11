using System;
using System.Runtime.InteropServices;

namespace Gmix.Vendor
{
    /// <summary>
    /// ofdr.dll 封装：输出复制为托管数组后立即释放厂商结果（ICD Part B §5）。
    /// </summary>
    public static class OfdrProcessor
    {
        public sealed class Output
        {
            public double[] Dist { get; set; }
            public double[] Freq { get; set; }
            public double[] Ps { get; set; }
            public int Length => Dist == null ? 0 : Dist.Length;
        }

        public static Output Process(double[] p, double[] s, double[] aux, double fs, double distScale)
        {
            if (p == null || s == null || aux == null)
                throw new ArgumentNullException(nameof(p), "P/S/A 不可为空");
            if (p.Length != s.Length || p.Length != aux.Length)
                throw new VendorException(VendorErrorKind.Data, -3,
                    "P/S/A 长度不一致: " + p.Length + "/" + s.Length + "/" + aux.Length);
            int n = p.Length;
            if (n <= 0)
                throw new VendorException(VendorErrorKind.Data, -3, "输入长度必须大于 0");

            IntPtr hp = IntPtr.Zero, hs = IntPtr.Zero, ha = IntPtr.Zero;
            try
            {
                hp = Marshal.AllocHGlobal(n * sizeof(double));
                hs = Marshal.AllocHGlobal(n * sizeof(double));
                ha = Marshal.AllocHGlobal(n * sizeof(double));
                Marshal.Copy(p, 0, hp, n);
                Marshal.Copy(s, 0, hs, n);
                Marshal.Copy(aux, 0, ha, n);

                var cfg = NativeApi.ofdr_config_default();
                cfg.fs = fs;
                cfg.dist_scale = distScale;

                var res = NativeApi.ofdr_process(hp, hs, ha, new UIntPtr((uint)n), ref cfg);
                try
                {
                    if (res.error != 0)
                        throw new VendorException(VendorErrorKind.Data, res.error,
                            "OFDR 处理失败: error=" + res.error + ", " + Message(res.error_msg));

                    var output = new Output
                    {
                        Dist = CopyDoubles(res.dist, res.len),
                        Freq = CopyDoubles(res.freq, res.len),
                        Ps = CopyDoubles(res.ps, res.len)
                    };
                    return output;
                }
                finally
                {
                    NativeApi.ofdr_result_free(ref res);
                }
            }
            finally
            {
                if (hp != IntPtr.Zero) Marshal.FreeHGlobal(hp);
                if (hs != IntPtr.Zero) Marshal.FreeHGlobal(hs);
                if (ha != IntPtr.Zero) Marshal.FreeHGlobal(ha);
            }
        }

        static double[] CopyDoubles(IntPtr p, UIntPtr count)
        {
            int len = (int)count.ToUInt64();
            if (len <= 0) return new double[0];
            var values = new double[len];
            Marshal.Copy(p, values, 0, len);
            return values;
        }

        static string Message(IntPtr errorMsg)
        {
            if (errorMsg == IntPtr.Zero) return "(no message)";
            string text = Marshal.PtrToStringAnsi(errorMsg);
            return string.IsNullOrEmpty(text) ? "(empty message)" : text;
        }
    }
}
