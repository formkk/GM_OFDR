using System;
using System.Diagnostics;
using Detection.Core;

namespace Detection.Vendor
{
    /// <summary>一次完整收发记录，供日志与故障复现（NFR-005、NFR-014）。</summary>
    public sealed class OcsFrameLog
    {
        public string Request { get; set; }
        public string Response { get; set; }
        public int TimeoutMs { get; set; }
        public long ElapsedMs { get; set; }
        public bool Succeeded { get; set; }
        public ErrorClass ErrorClass { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// OCS 客户端基类：命令串行化、完整帧发送、回包校验与错误映射（SDD §3.2、§6）。
    /// 纪律：
    /// <list type="bullet">
    /// <item>同一串口一次只允许一条在途命令（实测：连续发送会导致后续命令返回 &lt;ERROR&gt;）。</item>
    /// <item>每次发送完整帧；不发送半帧（实测：缺 &gt; 会使设备解析器挂起）。</item>
    /// <item>收到 &gt; 之后才发下一条。</item>
    /// <item>错误只抛，不吞（禁止 catch {} 静默）。</item>
    /// </list>
    /// </summary>
    public abstract class OcsClientBase : IDisposable
    {
        private readonly object _gate = new object();
        private bool _disposed;

        protected IOcsTransport Transport { get; private set; }

        /// <summary>默认超时（ms）。来自配置，默认值取自实测（详见 SDD §8.1）。</summary>
        public int DefaultTimeoutMs { get; private set; }

        /// <summary>每次收发后触发，用于记录原始 TX/RX 与耗时。</summary>
        public event Action<OcsFrameLog> FrameExchanged;

        protected OcsClientBase(IOcsTransport transport, int defaultTimeoutMs)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            if (defaultTimeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(defaultTimeoutMs), defaultTimeoutMs, "超时必须为正");

            Transport = transport;
            DefaultTimeoutMs = defaultTimeoutMs;
        }

        public bool IsOpen
        {
            get { return Transport.IsOpen; }
        }

        public void Open()
        {
            Transport.Open();
        }

        public void Close()
        {
            Transport.Close();
        }

        /// <summary>查询设备信息（IDN?）。</summary>
        public OcsDeviceInfo Identify()
        {
            return Identify(DefaultTimeoutMs);
        }

        public OcsDeviceInfo Identify(int timeoutMs)
        {
            string response = Exchange(OcsProtocolConstants.CmdIdentify, timeoutMs);
            string payload = OcsResponseParser.Payload(response);
            if (payload == null)
                throw new OcsProtocolException("IDN? 回包无法解析", OcsProtocolConstants.CmdIdentify, response, "检查串口参数与设备型号");

            return OcsDeviceInfo.ParseIdentity(payload);
        }

        /// <summary>查询设备编号（SN?）。</summary>
        public string ReadSerialNumber()
        {
            return ReadSerialNumber(DefaultTimeoutMs);
        }

        public string ReadSerialNumber(int timeoutMs)
        {
            string response = Exchange(OcsProtocolConstants.CmdSerialNumber, timeoutMs);
            string payload = OcsResponseParser.Payload(response);
            if (string.IsNullOrEmpty(payload))
                throw new OcsProtocolException("SN? 回包为空", OcsProtocolConstants.CmdSerialNumber, response, "重试或检查设备状态");
            return payload;
        }

        /// <summary>设备复位（RST）。调用方必须显式触发，且复位后应重新查询状态（NFR-008）。</summary>
        public void Reset(int timeoutMs)
        {
            string response = Exchange(OcsProtocolConstants.CmdReset, timeoutMs);
            string payload = OcsResponseParser.Payload(response);
            if (payload != "OK")
                throw new OcsDeviceException("RST 未返回 OK", OcsProtocolConstants.CmdReset, response, "设备可能未接受复位，检查设备状态");
            OnReset();
        }

        /// <summary>复位后的状态失效钩子，子类覆盖以丢弃缓存的矩阵状态。</summary>
        protected virtual void OnReset()
        {
        }

        /// <summary>
        /// 发送一条命令并读取回包，完成校验与错误映射。
        /// </summary>
        protected string Exchange(string command, int timeoutMs)
        {
            if (string.IsNullOrEmpty(command))
                throw new ArgumentException("命令不能为空", nameof(command));
            if (command.IndexOf(OcsProtocolConstants.FrameStart) >= 0 || command.IndexOf(OcsProtocolConstants.FrameEnd) >= 0)
                throw new ArgumentException("命令体内不得包含定界符", nameof(command));
            if (timeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs), timeoutMs, "超时必须为正");

            string frame = OcsProtocolConstants.FrameStart + command + OcsProtocolConstants.FrameEnd;

            lock (_gate)
            {
                var watch = Stopwatch.StartNew();
                string response = null;
                try
                {
                    Transport.Write(frame);
                    response = Transport.ReadFrame(timeoutMs);
                    ValidateResponse(response, command);
                    watch.Stop();
                    Raise(frame, response, timeoutMs, watch.ElapsedMilliseconds, true, ErrorClass.None, null);
                    return response;
                }
                catch (OcsException ex)
                {
                    watch.Stop();
                    Raise(frame, ex.RawResponse ?? response, timeoutMs, watch.ElapsedMilliseconds, false, ex.Class, ex.Message);
                    throw;
                }
            }
        }

        /// <summary>
        /// 回包合法性校验与错误映射（实测报告 §10 第 5 条）：
        /// 不以 &lt; 起、不以 &gt; 止、含问号、超长 → 协议错误；
        /// &lt;ERROR&gt; / &lt;ER_SetValue&gt; → 设备错误。
        /// </summary>
        protected static void ValidateResponse(string response, string command)
        {
            if (string.IsNullOrEmpty(response))
                throw new OcsProtocolException("回包为空", command, response, "重试；若持续为空检查设备供电与线缆");

            if (response[0] != OcsProtocolConstants.FrameStart
                || response[response.Length - 1] != OcsProtocolConstants.FrameEnd)
            {
                throw new OcsProtocolException(
                    "回包未按 < > 定界：" + OcsException.ToHexDigest(response),
                    command, response,
                    "检查波特率与校验位；重连后重试");
            }

            if (response.Length > OcsProtocolConstants.MaxFrameLength)
                throw new OcsProtocolException("回包超长，疑似协议错乱", command, response, "重连后重试");

            if (string.Equals(response, OcsProtocolConstants.ResponseError, StringComparison.Ordinal))
                throw new OcsDeviceException("设备返回 <ERROR>", command, response, "检查命令格式、通道取值域与设备型号是否匹配");

            if (string.Equals(response, OcsProtocolConstants.ResponseErrorSetValue, StringComparison.Ordinal))
                throw new OcsDeviceException("设备返回 <ER_SetValue>（整矩阵输出冲突）", command, response, "修正整矩阵计划：非 0 输出不可重复");

            if (response.IndexOf('?') >= 0)
            {
                throw new OcsProtocolException(
                    "回包含问号，疑似回显或协议错乱：" + OcsException.ToHexDigest(response),
                    command, response,
                    "确认串口为 8N1 且 Handshake=None；重连后重试");
            }
        }

        /// <summary>要求回包为 &lt;OK&gt;，否则按设备错误上报。</summary>
        protected static void RequireOk(string response, string command, string what)
        {
            if (string.Equals(response, OcsProtocolConstants.ResponseOk, StringComparison.Ordinal)) return;

            string payload = OcsResponseParser.Payload(response);
            if (payload == "OK") return;

            throw new OcsDeviceException(
                what + " 未返回 OK，实际回包：" + response,
                command, response,
                "重试该命令；持续失败按设备故障处理");
        }

        void Raise(string request, string response, int timeoutMs, long elapsedMs, bool ok, ErrorClass errorClass, string message)
        {
            var handler = FrameExchanged;
            if (handler == null) return;

            handler(new OcsFrameLog
            {
                Request = request,
                Response = response,
                TimeoutMs = timeoutMs,
                ElapsedMs = elapsedMs,
                Succeeded = ok,
                ErrorClass = errorClass,
                ErrorMessage = message
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Transport.Dispose();
        }
    }
}
