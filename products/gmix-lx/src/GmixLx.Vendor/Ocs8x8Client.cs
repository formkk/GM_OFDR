using System;
using System.Collections.Generic;
using GmixLx.Core;

namespace GmixLx.Vendor
{
    /// <summary>
    /// 8×8 OCS 客户端（8 输入 × 8 输出矩阵）。
    /// 关键实测结论（OCS_协议实测报告_2026-11-11.md §6）：
    /// <b>单路命令不校验输出唯一性</b>，设备会接受两输入争用同一输出；因此本客户端在发送前强制本地校验。
    /// 整矩阵命令由设备校验，冲突返回 &lt;ER_SetValue&gt;；本地仍先校验以便快速失败。
    /// 串口参数固定 115200 8N1（实测；厂商文档写 9600 与实物不符）。
    /// </summary>
    public sealed class Ocs8x8Client : OcsClientBase
    {
        private Ocs8x8State _known;

        public Ocs8x8Client(IOcsTransport transport, int defaultTimeoutMs)
            : base(transport, defaultTimeoutMs)
        {
        }

        /// <summary>已知矩阵状态；null 表示未知（需 QueryAll 或重连后 Invalidate）。</summary>
        public Ocs8x8State KnownMatrix
        {
            get { return _known; }
        }

        /// <summary>校验设备型号是否为 8×8，用于自检防错接。</summary>
        public bool VerifyModel()
        {
            return VerifyModel(DefaultTimeoutMs);
        }

        public bool VerifyModel(int timeoutMs)
        {
            OcsDeviceInfo info = Identify(timeoutMs);
            return info.ModelMatches(OcsProtocolConstants.ModelToken8x8);
        }

        /// <summary>丢弃缓存的矩阵状态。重连、复位或外部可能改动光路后必须调用。</summary>
        public void Invalidate()
        {
            _known = null;
        }

        protected override void OnReset()
        {
            Invalidate();
        }

        /// <summary>全量查询（QURSW? → &lt;SW01_02_..._08&gt;）。</summary>
        public Ocs8x8State QueryAll()
        {
            return QueryAll(DefaultTimeoutMs);
        }

        public Ocs8x8State QueryAll(int timeoutMs)
        {
            string command = OcsProtocolConstants.CmdQuerySwitch + "?";
            string response = Exchange(command, timeoutMs);

            int[] outputs;
            if (!OcsResponseParser.TryParseMatrix(response, out outputs))
                throw new OcsProtocolException("QURSW? 全量回包无法解析", command, response, "检查回包格式；确认连接的是 8×8 设备");

            _known = new Ocs8x8State(outputs);
            return _known;
        }

        /// <summary>单路查询（QURSWx? → &lt;SWx-yy&gt;）。返回该输入对应的输出，0 表示断开。</summary>
        public int QueryOutput(int input)
        {
            return QueryOutput(input, DefaultTimeoutMs);
        }

        public int QueryOutput(int input, int timeoutMs)
        {
            if (input < 1 || input > OcsProtocolConstants.InputCount8x8)
                throw new ArgumentOutOfRangeException(nameof(input), input, "输入通道必须在 1..8");

            string command = OcsProtocolConstants.CmdQuerySwitch + input + "?";
            string response = Exchange(command, timeoutMs);

            int ackInput;
            int output;
            if (!OcsResponseParser.TryParseSwitchState(response, out ackInput, out output))
                throw new OcsProtocolException("QURSWx? 回包无法解析", command, response, "重试；持续异常按设备故障处理");

            if (ackInput != input)
                throw new OcsProtocolException(
                    "QURSWx? 返回了非预期输入通道 " + ackInput + "（请求 " + input + "）",
                    command, response,
                    "确认命令拼接正确；重试");

            if (_known != null) _known = _known.With(input, output);
            return output;
        }

        /// <summary>
        /// 单路设置（SETSWx-yy）。发送前用已知矩阵校验非 0 输出唯一性；
        /// 未知时先全量查询。返回设置后的状态。
        /// </summary>
        public Ocs8x8State SetRoute(int input, int output)
        {
            return SetRoute(input, output, DefaultTimeoutMs);
        }

        public Ocs8x8State SetRoute(int input, int output, int timeoutMs)
        {
            if (input < 1 || input > OcsProtocolConstants.InputCount8x8)
                throw new ArgumentOutOfRangeException(nameof(input), input, "输入通道必须在 1..8");
            if (output < OcsProtocolConstants.MinChannel || output > OcsProtocolConstants.MaxChannel)
                throw new ArgumentOutOfRangeException(nameof(output), output, "输出通道必须在 0..8（0 = 断开）");

            if (_known == null) QueryAll(timeoutMs);

            int occupant = _known.FindOccupant(output, input);
            if (occupant != 0)
            {
                string commandPeek = OcsResponseParser.BuildRouteCommand(input, output);
                throw new OcsRouteConflictException(
                    "计划冲突：IN" + input + " → OUT" + output + " 与 IN" + occupant + " 冲突（输出已占用）",
                    commandPeek);
            }

            string command = OcsResponseParser.BuildRouteCommand(input, output);
            string response = Exchange(command, timeoutMs);

            int ackInput;
            int ackOutput;
            if (!OcsResponseParser.TryParseSetAck(response, out ackInput, out ackOutput))
                throw new OcsProtocolException("SETSW 回包无法解析", command, response, "重试；持续异常按设备故障处理");

            if (ackInput != input || ackOutput != output)
            {
                throw new OcsDeviceException(
                    "SETSW 回包与请求不一致：请求 " + input + "-" + output + "，回包 " + ackInput + "-" + ackOutput,
                    command, response,
                    "按设备错误重试；持续不一致需检查设备固件");
            }

            _known = _known.With(input, output);
            return _known;
        }

        /// <summary>
        /// 整矩阵设置（SETSWabcdefgh）。发送前校验取值域与非 0 唯一性；
        /// 设备要求 <c>&lt;OK&gt;</c> 回包。返回设置后的状态。
        /// </summary>
        public Ocs8x8State SetMatrix(IReadOnlyList<int> outputs)
        {
            return SetMatrix(outputs, DefaultTimeoutMs);
        }

        public Ocs8x8State SetMatrix(IReadOnlyList<int> outputs, int timeoutMs)
        {
            if (outputs == null) throw new ArgumentNullException(nameof(outputs));
            if (outputs.Count != OcsProtocolConstants.InputCount8x8)
                throw new ArgumentException("整矩阵命令需要 8 个输出值", nameof(outputs));

            var values = new int[outputs.Count];
            for (int i = 0; i < values.Length; i++) values[i] = outputs[i];

            var candidate = new Ocs8x8State(values);
            AssertUniqueOutputs(candidate);

            string command = OcsResponseParser.BuildMatrixCommand(values);
            string response = Exchange(command, timeoutMs);
            RequireOk(response, command, "整矩阵设置");

            _known = candidate;
            return _known;
        }

        /// <summary>整矩阵设置（数组形式）。</summary>
        public Ocs8x8State SetMatrix(int[] outputs, int timeoutMs)
        {
            if (outputs == null) throw new ArgumentNullException(nameof(outputs));
            return SetMatrix((IReadOnlyList<int>)outputs, timeoutMs);
        }

        /// <summary>断开全部通道（整矩阵全 0）。</summary>
        public Ocs8x8State DisconnectAll(int timeoutMs)
        {
            var zeros = new int[OcsProtocolConstants.InputCount8x8];
            return SetMatrix(zeros, timeoutMs);
        }

        static void AssertUniqueOutputs(Ocs8x8State state)
        {
            var used = new Dictionary<int, int>();
            for (int input = 1; input <= OcsProtocolConstants.InputCount8x8; input++)
            {
                int output = state.OutputOf(input);
                if (output == Ocs8x8State.Disconnected) continue;

                int existing;
                if (used.TryGetValue(output, out existing))
                {
                    throw new OcsRouteConflictException(
                        "计划冲突：IN" + existing + " 与 IN" + input + " 同时指向 OUT" + output,
                        OcsProtocolConstants.CmdSetSwitch + "<matrix>");
                }
                used[output] = input;
            }
        }
    }
}
