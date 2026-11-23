using System;
using System.IO;
using System.IO.Ports;
using System.Text;

namespace GmixLx.Vendor
{
    /// <summary>
    /// 真实串口传输。串口参数按实测强制（SDD §3.2、实测报告 §7）：
    /// 8N1、<c>Handshake = None</c>（开流控即写超时）、DTR/RTS 关闭（对通信无影响）。
    /// 读帧按 &gt; 定界，逐字节读取，带总超时与最大长度保护。
    /// 本类型是全仓库唯一允许直接使用 System.IO.Ports 的位置（ADR-001）。
    /// </summary>
    public sealed class OcsSerialTransport : IOcsTransport
    {
        private readonly SerialPort _port;
        private bool _disposed;

        public string PortName { get; private set; }
        public int BaudRate { get; private set; }

        public OcsSerialTransport(string portName, int baudRate)
        {
            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("串口号不能为空", nameof(portName));
            if (baudRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(baudRate), baudRate, "波特率必须为正");

            PortName = portName;
            BaudRate = baudRate;
            _port = new SerialPort(portName, baudRate, Parity.None, OcsProtocolConstants.DataBits, StopBits.One);
            _port.Handshake = Handshake.None;
            _port.DtrEnable = false;
            _port.RtsEnable = false;
            _port.Encoding = Encoding.ASCII;
        }

        public bool IsOpen
        {
            get { return _port.IsOpen; }
        }

        public void Open()
        {
            if (_port.IsOpen) return;
            try
            {
                _port.Open();
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new OcsTransportException(
                    "串口被占用或无权限：" + PortName,
                    null, null,
                    "关闭占用该串口的程序后重试；确认没有第二个实例在运行", ex);
            }
            catch (IOException ex)
            {
                throw new OcsTransportException(
                    "串口打开失败：" + PortName,
                    null, null,
                    "确认设备已上电、串口号正确、线缆连接正常", ex);
            }
        }

        public void Close()
        {
            try
            {
                if (_port.IsOpen) _port.Close();
            }
            catch (IOException)
            {
                // 关闭阶段的 IO 异常不影响后续重连；下一次 Open 会重新校验
            }
        }

        public void Write(string frame)
        {
            if (string.IsNullOrEmpty(frame))
                throw new ArgumentException("帧不能为空", nameof(frame));
            if (!_port.IsOpen)
                throw new OcsTransportException("串口未打开：" + PortName, frame, null, "先执行 Open", null);

            try
            {
                _port.Write(frame);
            }
            catch (TimeoutException ex)
            {
                throw new OcsTransportException("串口写超时：" + PortName, frame, null,
                    "检查流控必须为 None；确认设备在线", ex);
            }
            catch (IOException ex)
            {
                throw new OcsTransportException("串口写失败：" + PortName, frame, null,
                    "检查 USB 转串口连接，必要时重新插拔后重连", ex);
            }
        }

        public string ReadFrame(int timeoutMs)
        {
            if (timeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs), timeoutMs, "超时必须为正");
            if (!_port.IsOpen)
                throw new OcsTransportException("串口未打开：" + PortName, null, null, "先执行 Open", null);

            var sb = new StringBuilder(64);
            long deadline = Environment.TickCount64 + timeoutMs;

            while (true)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    throw new OcsTransportException(
                        "等待回包超时（" + timeoutMs + " ms）：" + PortName,
                        null, sb.ToString(),
                        "确认设备在线；确认波特率正确（1×8=9600，8×8=115200）", null);
                }

                int value;
                try
                {
                    _port.ReadTimeout = (int)remaining;
                    value = _port.ReadByte();
                }
                catch (TimeoutException ex)
                {
                    throw new OcsTransportException(
                        "等待回包超时（" + timeoutMs + " ms）：" + PortName,
                        null, sb.ToString(),
                        "确认设备在线；确认波特率正确（1×8=9600，8×8=115200）", ex);
                }
                catch (IOException ex)
                {
                    throw new OcsTransportException("串口读失败：" + PortName, null, sb.ToString(),
                        "设备可能已断开，请重连", ex);
                }

                if (value < 0) continue;

                char c = (char)value;
                sb.Append(c);

                if (c == OcsProtocolConstants.FrameEnd) return sb.ToString();

                if (sb.Length > OcsProtocolConstants.MaxFrameLength)
                {
                    throw new OcsProtocolException(
                        "回包超长（>" + OcsProtocolConstants.MaxFrameLength + " 字节），疑似协议错乱",
                        null, sb.ToString(),
                        "检查串口参数与线缆干扰，重连后重试");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            _port.Dispose();
        }
    }
}
