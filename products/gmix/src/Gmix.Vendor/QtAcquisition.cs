using System;
using System.IO;
using System.Text;

namespace Gmix.Vendor
{
    /// <summary>
    /// qt_acq.dll 会话封装：句柄只在内部持有，向上只交付托管 byte[]（SRS FR-ACQ-06）。
    /// 生命周期：Open → Configure → Start → Read → Stop → Close，Stop/Close 幂等。
    /// </summary>
    public sealed class QtAcquisition : IDisposable
    {
        readonly string _runtimeDir;
        IntPtr _handle = IntPtr.Zero;

        public QtAcquisition(string runtimeDir)
        {
            if (string.IsNullOrEmpty(runtimeDir))
                throw new ArgumentException("runtimeDir 不能为空", nameof(runtimeDir));
            _runtimeDir = Path.GetFullPath(runtimeDir);
            // SDK 以相对路径读取 config/<cardIndex>/，故把 DLL 目录设为搜索路径与工作目录。
            VendorRuntime.Initialize(_runtimeDir);
        }

        public bool IsOpen => _handle != IntPtr.Zero;

        public string RuntimeDir => _runtimeDir;

        public string Version()
        {
            var sb = new StringBuilder(256);
            int rc = NativeApi.qt_acq_version(sb, new UIntPtr((uint)sb.Capacity));
            if (rc != 0)
                throw new VendorException(VendorErrorKind.Protocol, rc, "qt_acq_version 失败 rc=" + rc);
            return sb.ToString();
        }

        public void Open(uint cardIndex)
        {
            if (IsOpen) return;
            int rc = NativeApi.qt_acq_open(cardIndex, out _handle);
            if (rc != 0)
            {
                _handle = IntPtr.Zero;
                throw VendorException.FromQtAcq(rc, LastError());
            }
        }

        /// <summary>按公式预算缓冲字节数：rate_mhz × acq_time_ms × 8000 × seg_count（ICD Part B §3.1）。</summary>
        public uint CalcBufferBytes(uint rateMhz, uint acqTimeMs, uint segCount)
        {
            uint bytes = 0;
            int rc = NativeApi.qt_acq_calc_buffer_bytes(rateMhz, acqTimeMs, segCount, out bytes);
            if (rc != 0)
                throw VendorException.FromQtAcq(rc, LastError());
            return bytes;
        }

        public void Configure(uint rateMhz, uint acqTimeMs, uint segCount, int trigger, uint triggerDelayMs)
        {
            RequireOpen();
            int rc = NativeApi.qt_acq_configure(_handle, rateMhz, acqTimeMs, segCount, trigger, triggerDelayMs);
            if (rc != 0)
                throw VendorException.FromQtAcq(rc, LastError());
        }

        public void Start()
        {
            RequireOpen();
            int rc = NativeApi.qt_acq_start(_handle);
            if (rc != 0)
                throw VendorException.FromQtAcq(rc, LastError());
        }

        /// <summary>读取一次采集；返回实际字节，短读或越界按数据错误拒绝（ICD Part B §4）。</summary>
        public byte[] Read(uint capacity, out uint outLen)
        {
            RequireOpen();
            if (capacity == 0)
                throw new VendorException(VendorErrorKind.Data, -3, "read capacity 必须大于 0");

            var buffer = new byte[capacity];
            int rc = NativeApi.qt_acq_read(_handle, buffer, capacity, out outLen);
            if (rc != 0)
                throw VendorException.FromQtAcq(rc, LastError());
            if (outLen == 0 || outLen > capacity)
                throw new VendorException(VendorErrorKind.Data, rc,
                    "读取长度异常: outLen=" + outLen + ", capacity=" + capacity);

            if (outLen == capacity) return buffer;
            var trimmed = new byte[outLen];
            Buffer.BlockCopy(buffer, 0, trimmed, 0, (int)outLen);
            return trimmed;
        }

        public void Stop()
        {
            if (!IsOpen) return;
            NativeApi.qt_acq_stop(_handle);
        }

        public void Close()
        {
            if (!IsOpen) return;
            NativeApi.qt_acq_close(_handle);
            _handle = IntPtr.Zero;
        }

        public void Dispose()
        {
            Close();
        }

        void RequireOpen()
        {
            if (!IsOpen)
                throw new VendorException(VendorErrorKind.Protocol, -4, "采集会话未打开");
        }

        /// <summary>qt_acq_last_error 的返回值是消息字节长度而非状态码（实测），故按消息是否为空判定。</summary>
        string LastError()
        {
            var sb = new StringBuilder(1024);
            NativeApi.qt_acq_last_error(sb, new UIntPtr((uint)sb.Capacity));
            return sb.ToString();
        }
    }
}
