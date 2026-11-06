using System;
using System.Runtime.InteropServices;

namespace Gmix.Vendor
{
    /// <summary>
    /// 厂商 C ABI 的唯一 P/Invoke 位置（ICD Part B §2 分层红线：其它程序集不得出现 DllImport）。
    /// 结构体字段顺序与 qt_acq.h / ofdr.h / detect.h 严格一致；size_t 映射为 UIntPtr。
    /// </summary>
    internal static class NativeApi
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
        public static extern int qt_acq_last_error(System.Text.StringBuilder buf, UIntPtr cap);

        [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int qt_acq_version(System.Text.StringBuilder buf, UIntPtr len);

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
}
