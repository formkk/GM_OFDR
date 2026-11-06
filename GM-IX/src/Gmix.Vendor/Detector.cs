using System;
using System.Runtime.InteropServices;

namespace Gmix.Vendor
{
    /// <summary>
    /// detect.dll 封装：输入线性 ps 谱，输出相对电平 dB_rel（20log10 只在库内发生一次）。
    /// 厂商以 NAN 表示"无值"（首事件 Il/BaseBefore、段数不足的 BaseAfter），此处统一映射为 null（ICD §0.3 #3）。
    /// </summary>
    public static class Detector
    {
        public sealed class Output
        {
            public double[] PsDb { get; set; }
            public double[] BaselineDb { get; set; }
            public Peak[] Peaks { get; set; }
            public Segment[] Segments { get; set; }
            public Event[] Events { get; set; }
        }

        public struct Peak
        {
            public double LocM;
            public double HeightDb;
            public double ProminenceDb;
            public double BaseDb;
        }

        public struct Segment
        {
            public ulong StartIndex;
            public ulong EndIndex;
            public double NoiseFloorDb;
        }

        public struct Event
        {
            public double LocM;
            public double PeakDb;
            public double? IlDb;
            public double RlDb;
            public double? BaseBeforeDb;
            public double? BaseAfterDb;
        }

        public static Output Process(double[] dist, double[] ps)
        {
            if (dist == null || ps == null)
                throw new ArgumentNullException(nameof(dist), "dist/ps 不可为空");
            if (dist.Length != ps.Length)
                throw new VendorException(VendorErrorKind.Data, -3,
                    "dist/ps 长度不一致: " + dist.Length + "/" + ps.Length);
            int n = dist.Length;
            if (n <= 0)
                throw new VendorException(VendorErrorKind.Data, -3, "输入长度必须大于 0");

            IntPtr hd = IntPtr.Zero, hp = IntPtr.Zero;
            try
            {
                hd = Marshal.AllocHGlobal(n * sizeof(double));
                hp = Marshal.AllocHGlobal(n * sizeof(double));
                Marshal.Copy(dist, 0, hd, n);
                Marshal.Copy(ps, 0, hp, n);

                var cfg = NativeApi.detect_config_default();
                var res = NativeApi.detect_process(hd, hp, new UIntPtr((uint)n), ref cfg);
                try
                {
                    if (res.error != 0)
                        throw new VendorException(VendorErrorKind.Data, res.error,
                            "事件检测失败: error=" + res.error + ", " + Message(res.error_msg));

                    return new Output
                    {
                        PsDb = CopyDoubles(res.ps_db, res.len),
                        BaselineDb = CopyDoubles(res.baseline_db, res.len),
                        Peaks = CopyPeaks(res),
                        Segments = CopySegments(res),
                        Events = CopyEvents(res)
                    };
                }
                finally
                {
                    NativeApi.detect_result_free(ref res);
                }
            }
            finally
            {
                if (hd != IntPtr.Zero) Marshal.FreeHGlobal(hd);
                if (hp != IntPtr.Zero) Marshal.FreeHGlobal(hp);
            }
        }

        static Peak[] CopyPeaks(NativeApi.DetectResult res)
        {
            int count = Count(res.n_peaks);
            var loc = CopyDoubles(res.peak_loc, res.n_peaks);
            var height = CopyDoubles(res.peak_height, res.n_peaks);
            var prom = CopyDoubles(res.peak_prominence, res.n_peaks);
            var baseDb = CopyDoubles(res.peak_base, res.n_peaks);
            var peaks = new Peak[count];
            for (int i = 0; i < count; i++)
            {
                peaks[i].LocM = loc[i];
                peaks[i].HeightDb = height[i];
                peaks[i].ProminenceDb = prom[i];
                peaks[i].BaseDb = baseDb[i];
            }
            return peaks;
        }

        static Segment[] CopySegments(NativeApi.DetectResult res)
        {
            int count = Count(res.n_segments);
            var start = CopySizes(res.seg_start, res.n_segments);
            var end = CopySizes(res.seg_end, res.n_segments);
            var floor = CopyDoubles(res.seg_noise_floor, res.n_segments);
            var segments = new Segment[count];
            for (int i = 0; i < count; i++)
            {
                segments[i].StartIndex = start[i];
                segments[i].EndIndex = end[i];
                segments[i].NoiseFloorDb = floor[i];
            }
            return segments;
        }

        static Event[] CopyEvents(NativeApi.DetectResult res)
        {
            int count = Count(res.n_events);
            var loc = CopyDoubles(res.event_loc, res.n_events);
            var peak = CopyDoubles(res.event_peak, res.n_events);
            var il = CopyDoubles(res.event_il, res.n_events);
            var rl = CopyDoubles(res.event_rl, res.n_events);
            var before = CopyDoubles(res.event_base_before, res.n_events);
            var after = CopyDoubles(res.event_base_after, res.n_events);
            var events = new Event[count];
            for (int i = 0; i < count; i++)
            {
                events[i].LocM = loc[i];
                events[i].PeakDb = peak[i];
                events[i].IlDb = NullIfNaN(il[i]);
                events[i].RlDb = rl[i];
                events[i].BaseBeforeDb = NullIfNaN(before[i]);
                events[i].BaseAfterDb = NullIfNaN(after[i]);
            }
            return events;
        }

        static double? NullIfNaN(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;
        }

        static int Count(UIntPtr value)
        {
            return (int)value.ToUInt64();
        }

        static double[] CopyDoubles(IntPtr p, UIntPtr count)
        {
            int len = Count(count);
            if (len <= 0) return new double[0];
            var values = new double[len];
            Marshal.Copy(p, values, 0, len);
            return values;
        }

        static ulong[] CopySizes(IntPtr p, UIntPtr count)
        {
            int len = Count(count);
            if (len <= 0) return new ulong[0];
            var raw = new long[len];
            Marshal.Copy(p, raw, 0, len);
            var values = new ulong[len];
            for (int i = 0; i < len; i++) values[i] = (ulong)raw[i];
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
