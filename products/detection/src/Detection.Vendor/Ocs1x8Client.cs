using System;
using Detection.Core;

namespace Detection.Vendor
{
    /// <summary>
    /// 1×8 OCS 客户端（单输入 COM，8 选 1）。
    /// 命令集最小：无逐路查询、无整矩阵设置（实测确认不支持）。
    /// 串口参数固定 9600 8N1（实测）。
    /// </summary>
    public sealed class Ocs1x8Client : OcsClientBase
    {
        /// <summary>1×8 的输入通道恒为 1（COM）。</summary>
        public const int InputChannel = 1;

        public Ocs1x8Client(IOcsTransport transport, int defaultTimeoutMs)
            : base(transport, defaultTimeoutMs)
        {
        }

        /// <summary>校验设备型号是否为 1×8，用于自检防错接。</summary>
        public bool VerifyModel()
        {
            return VerifyModel(DefaultTimeoutMs);
        }

        public bool VerifyModel(int timeoutMs)
        {
            OcsDeviceInfo info = Identify(timeoutMs);
            return info.ModelMatches(OcsProtocolConstants.ModelToken1x8);
        }

        /// <summary>查询当前路由（QURSW? → &lt;SW1-yy&gt;）。</summary>
        public Ocs1x8State QueryState()
        {
            return QueryState(DefaultTimeoutMs);
        }

        public Ocs1x8State QueryState(int timeoutMs)
        {
            string command = OcsProtocolConstants.CmdQuerySwitch + "?";
            string response = Exchange(command, timeoutMs);

            int input;
            int output;
            if (!OcsResponseParser.TryParseSwitchState(response, out input, out output))
                throw new OcsProtocolException("QURSW? 回包无法解析", command, response, "检查回包格式是否被干扰；重试");

            if (input != InputChannel)
                throw new OcsProtocolException(
                    "QURSW? 返回了非预期输入通道 " + input,
                    command, response,
                    "确认连接的是 1×8 设备（8×8 的输入通道为 1..8）");

            return new Ocs1x8State(output);
        }

        /// <summary>
        /// 设置路由（SETSW1-yy，yy=0 表示断开），返回设置后的状态。
        /// 发送前校验取值域；发送后校验回包与请求一致，不一致按设备错误上报（相当于命令级回读）。
        /// </summary>
        public Ocs1x8State SetOutput(int output)
        {
            return SetOutput(output, DefaultTimeoutMs);
        }

        public Ocs1x8State SetOutput(int output, int timeoutMs)
        {
            if (output < OcsProtocolConstants.MinChannel || output > OcsProtocolConstants.MaxChannel)
                throw new ArgumentOutOfRangeException(nameof(output), output, "输出通道必须在 0..8（0 = 断开）");

            string command = OcsResponseParser.BuildRouteCommand(InputChannel, output);
            string response = Exchange(command, timeoutMs);

            int ackInput;
            int ackOutput;
            if (!OcsResponseParser.TryParseSetAck(response, out ackInput, out ackOutput))
                throw new OcsProtocolException("SETSW 回包无法解析", command, response, "重试；持续异常按设备故障处理");

            if (ackInput != InputChannel || ackOutput != output)
            {
                throw new OcsDeviceException(
                    "SETSW 回包与请求不一致：请求 " + InputChannel + "-" + output + "，回包 " + ackInput + "-" + ackOutput,
                    command, response,
                    "按设备错误重试；持续不一致需检查设备固件");
            }

            return new Ocs1x8State(ackOutput);
        }

        /// <summary>断开当前光路（SETSW1-00）。</summary>
        public Ocs1x8State Disconnect(int timeoutMs)
        {
            return SetOutput(Ocs1x8State.Disconnected, timeoutMs);
        }
    }
}
