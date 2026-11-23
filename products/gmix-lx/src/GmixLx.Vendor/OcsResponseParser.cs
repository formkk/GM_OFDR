using System;
using System.Collections.Generic;

namespace GmixLx.Vendor
{
    /// <summary>
    /// OCS 回包解析（纯函数）。所有方法接收含定界符的完整帧，失败返回 false 而不抛异常；
    /// 上层据返回值决定是否按协议/设备错误上报。
    /// </summary>
    public static class OcsResponseParser
    {
        /// <summary>去除 &lt; &gt; 定界符并裁剪首尾空白；输入非法返回 null。</summary>
        public static string Payload(string frame)
        {
            if (string.IsNullOrEmpty(frame)) return null;
            if (frame.Length < 2) return null;
            if (frame[0] != OcsProtocolConstants.FrameStart) return null;
            if (frame[frame.Length - 1] != OcsProtocolConstants.FrameEnd) return null;
            return frame.Substring(1, frame.Length - 2).Trim();
        }

        /// <summary>
        /// 解析设置回包：<c>&lt;1-03&gt;</c> → 输入 1、输出 3。
        /// 1×8 与 8×8 的 SETSW 回包同形。
        /// </summary>
        public static bool TryParseSetAck(string frame, out int input, out int output)
        {
            input = 0;
            output = 0;
            string payload = Payload(frame);
            if (payload == null) return false;

            int dash = payload.IndexOf('-');
            if (dash <= 0 || dash == payload.Length - 1) return false;

            return TryParseInt(payload.Substring(0, dash), out input)
                && TryParseInt(payload.Substring(dash + 1), out output);
        }

        /// <summary>
        /// 解析单路状态回包：<c>&lt;SW1-03&gt;</c>（1×8 全量查询）或 <c>&lt;SW4-02&gt;</c>（8×8 单路查询）。
        /// </summary>
        public static bool TryParseSwitchState(string frame, out int input, out int output)
        {
            input = 0;
            output = 0;
            string payload = Payload(frame);
            if (payload == null) return false;
            if (!payload.StartsWith("SW", StringComparison.Ordinal)) return false;

            string body = payload.Substring(2);
            int dash = body.IndexOf('-');
            if (dash <= 0 || dash == body.Length - 1) return false;

            return TryParseInt(body.Substring(0, dash), out input)
                && TryParseInt(body.Substring(dash + 1), out output);
        }

        /// <summary>
        /// 解析 8×8 全量查询回包：<c>&lt;SW01_02_03_04_05_06_07_08&gt;</c> → 8 个输出值。
        /// 分段为两位定宽，0 表示该路断开。
        /// </summary>
        public static bool TryParseMatrix(string frame, out int[] outputs)
        {
            outputs = null;
            string payload = Payload(frame);
            if (payload == null) return false;
            if (!payload.StartsWith("SW", StringComparison.Ordinal)) return false;

            string body = payload.Substring(2);
            string[] parts = body.Split('_');
            if (parts.Length != OcsProtocolConstants.InputCount8x8) return false;

            var values = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length != 2) return false;

                int value;
                if (!TryParseInt(part, out value)) return false;
                if (value < OcsProtocolConstants.MinChannel || value > OcsProtocolConstants.MaxChannel) return false;

                values[i] = value;
            }

            outputs = values;
            return true;
        }

        /// <summary>构造 8×8 整矩阵命令体（不含定界符）：SETSW + 8 个单数字。</summary>
        public static string BuildMatrixCommand(IReadOnlyList<int> outputs)
        {
            if (outputs == null) throw new ArgumentNullException(nameof(outputs));
            if (outputs.Count != OcsProtocolConstants.InputCount8x8)
                throw new ArgumentException("整矩阵命令需要 8 个输出值", nameof(outputs));

            var chars = new char[OcsProtocolConstants.InputCount8x8];
            for (int i = 0; i < chars.Length; i++)
            {
                int value = outputs[i];
                if (value < OcsProtocolConstants.MinChannel || value > OcsProtocolConstants.MaxChannel)
                    throw new ArgumentOutOfRangeException(nameof(outputs), value, "输出通道必须在 0..8");
                chars[i] = (char)('0' + value);
            }
            return OcsProtocolConstants.CmdSetSwitch + new string(chars);
        }

        /// <summary>构造单路设置命令体：1×8 为 SETSW1-yy，8×8 为 SETSWx-yy（输出两位定宽）。</summary>
        public static string BuildRouteCommand(int input, int output)
        {
            if (input < 1 || input > OcsProtocolConstants.InputCount8x8)
                throw new ArgumentOutOfRangeException(nameof(input), input, "输入通道必须在 1..8");
            if (output < OcsProtocolConstants.MinChannel || output > OcsProtocolConstants.MaxChannel)
                throw new ArgumentOutOfRangeException(nameof(output), output, "输出通道必须在 0..8");

            return OcsProtocolConstants.CmdSetSwitch + input + "-" + output.ToString("D2");
        }

        static bool TryParseInt(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Length > 3) return false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9') return false;
            }
            value = int.Parse(text);
            return true;
        }
    }
}
