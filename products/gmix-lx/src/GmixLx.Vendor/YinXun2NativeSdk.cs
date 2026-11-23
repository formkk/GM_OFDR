using System;
using System.Runtime.InteropServices;
using System.Text;
using GmixLx.Core;

namespace GmixLx.Vendor
{
    /// <summary>
    /// YinXun_2 原生 SDK 会话：唯一持有 ocs_* 句柄的地方（规则 30）。
    /// 只做调用、错误映射与结果复制；释放、句柄与线程安全都在这里收口，上层不碰原生内存。
    /// 主 DLL 与依赖库加载后常驻进程，不做卸载（避免卸载时仍有句柄在用）。
    /// </summary>
    public sealed class YinXun2NativeSdk : IYinXun2Sdk
    {
        readonly object _gate = new object();
        readonly byte[] _error = new byte[YinXun2ProtocolConstants.ErrorCapacity];

        IntPtr _library;
        IntPtr _device;
        YinXun2OpenDeviceFn _openDevice;
        YinXun2SetRoiFn _setRoi;
        YinXun2AcquireV4Fn _acquire;
        YinXun2ReleaseV4Fn _release;
        YinXun2CloseDeviceFn _closeDevice;

        public void Open(string sdkDirectory, uint spatialAverageM, uint temporalAverageN)
        {
            lock (_gate)
            {
                if (_device != IntPtr.Zero)
                {
                    throw new YinXun2Exception(ErrorClass.Configuration, "YinXun_2 设备已打开，禁止重复 Open");
                }

                EnsureBound(sdkDirectory);
                string binDirectory = YinXun2NativeApi.ResolveBinDirectory(sdkDirectory);

                IntPtr pathUtf8 = Marshal.StringToCoTaskMemUTF8(binDirectory);
                try
                {
                    ClearError();
                    IntPtr device;
                    int rc = _openDevice(pathUtf8, spatialAverageM, temporalAverageN,
                        out device, _error, new UIntPtr((uint)_error.Length));
                    if (rc != YinXun2ProtocolConstants.StatusOk)
                    {
                        YinXun2Exception.ThrowIfFailed(rc, "ocs_open_device", ErrorText());
                    }
                    if (device == IntPtr.Zero)
                    {
                        throw new YinXun2Exception(ErrorClass.Device,
                            "ocs_open_device 返回成功但设备句柄为空（SDK 状态异常）");
                    }
                    _device = device;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathUtf8);
                }
            }
        }

        public void SetAnalysisRoi(double startM, double endM)
        {
            lock (_gate)
            {
                EnsureDevice();
                ClearError();
                int rc = _setRoi(_device, startM, endM, _error, new UIntPtr((uint)_error.Length));
                YinXun2Exception.ThrowIfFailed(rc, "ocs_set_analysis_roi", ErrorText());
            }
        }

        public YinXun2Measurement Acquire()
        {
            lock (_gate)
            {
                EnsureDevice();
                ClearError();
                YinXun2OcsResultV4 native;
                int rc = _acquire(_device, out native, _error, new UIntPtr((uint)_error.Length));
                if (rc != YinXun2ProtocolConstants.StatusOk)
                {
                    YinXun2Exception.ThrowIfFailed(rc, "ocs_acquire_measurement_v4", ErrorText());
                }

                try
                {
                    return Copy(native);
                }
                finally
                {
                    _release(ref native);
                }
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                if (_device == IntPtr.Zero) return;
                _closeDevice(ref _device);
                _device = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Close();
        }

        // ---------- 内部 ----------

        void EnsureBound(string sdkDirectory)
        {
            if (_library != IntPtr.Zero) return;

            IntPtr library = YinXun2NativeApi.Load(sdkDirectory);
            _openDevice = YinXun2NativeApi.Bind<YinXun2OpenDeviceFn>(library, "ocs_open_device");
            _setRoi = YinXun2NativeApi.Bind<YinXun2SetRoiFn>(library, "ocs_set_analysis_roi");
            _acquire = YinXun2NativeApi.Bind<YinXun2AcquireV4Fn>(library, "ocs_acquire_measurement_v4");
            _release = YinXun2NativeApi.Bind<YinXun2ReleaseV4Fn>(library, "ocs_release_result_v4");
            _closeDevice = YinXun2NativeApi.Bind<YinXun2CloseDeviceFn>(library, "ocs_close_device");
            _library = library;
        }

        void EnsureDevice()
        {
            if (_device == IntPtr.Zero)
            {
                throw new YinXun2Exception(ErrorClass.Configuration, "YinXun_2 设备未打开，请先 Open");
            }
        }

        void ClearError()
        {
            Array.Clear(_error, 0, _error.Length);
        }

        string ErrorText()
        {
            int end = Array.IndexOf(_error, (byte)0);
            if (end < 0) end = _error.Length;
            return Encoding.UTF8.GetString(_error, 0, end).Trim();
        }

        static YinXun2Measurement Copy(YinXun2OcsResultV4 native)
        {
            int count = CheckedCount(native.Summary.Count, "点数");
            var distance = new double[count];
            var levels = new double[count];
            if (count > 0)
            {
                Marshal.Copy(native.Summary.DistanceM, distance, 0, count);
                Marshal.Copy(native.Summary.ReflectionDb, levels, 0, count);
            }

            int peakCount = CheckedCount(native.PeakCount, "峰数");
            var peaks = new YinXun2Peak[peakCount];
            int peakSize = Marshal.SizeOf<YinXun2OcsPeakResult>();
            for (int i = 0; i < peakCount; i++)
            {
                YinXun2OcsPeakResult item = Marshal.PtrToStructure<YinXun2OcsPeakResult>(
                    IntPtr.Add(native.Peaks, i * peakSize));
                peaks[i] = new YinXun2Peak
                {
                    DistanceM = item.DistanceM,
                    PeakLevelDb = item.PeakLevelDb,
                    InsertionLossDb = item.InsertionLossDb,
                    ReturnLossDb = item.ReturnLossDb,
                    LossStatus = item.LossStatus,
                    IlStatus = item.IlStatus,
                    RlStatus = item.RlStatus
                };
            }

            return new YinXun2Measurement
            {
                DistanceM = distance,
                LevelsDb = levels,
                Peaks = peaks,
                PeakDistanceM = native.Summary.PeakDistanceM,
                InsertionLossDb = native.Summary.InsertionLossDb,
                ReturnLossDb = native.Summary.ReturnLossDb,
                LossStatus = native.Summary.LossStatus,
                RoiIlStatus = native.RoiIlStatus,
                RoiRlStatus = native.RoiRlStatus,
                AverageCount = native.SpectrumAverageCount,
                AverageReady = native.SpectrumAverageReady != 0
            };
        }

        static int CheckedCount(UIntPtr value, string label)
        {
            ulong raw = value.ToUInt64();
            if (raw > int.MaxValue)
            {
                throw new YinXun2Exception(ErrorClass.Measurement,
                    "YinXun_2 " + label + "异常：" + raw);
            }
            return (int)raw;
        }
    }
}
