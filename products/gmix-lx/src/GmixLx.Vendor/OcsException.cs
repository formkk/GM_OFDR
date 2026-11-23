using System;
using GmixLx.Core;

namespace GmixLx.Vendor
{
    /// <summary>
    /// OCS 通信异常基类。携带错误分类、原始请求/回包与建议动作（NFR-014：错误消息四要素）。
    /// </summary>
    public class OcsException : Exception
    {
        public ErrorClass Class { get; private set; }
        public string RawRequest { get; private set; }
        public string RawResponse { get; private set; }
        public string SuggestedAction { get; private set; }

        public OcsException(ErrorClass errorClass, string message, string rawRequest, string rawResponse, string suggestedAction, Exception inner)
            : base(message, inner)
        {
            Class = errorClass;
            RawRequest = rawRequest;
            RawResponse = rawResponse;
            SuggestedAction = suggestedAction;
        }

        /// <summary>字母数字十六进制摘要，便于日志与现场定位（不泄露控制字符）。</summary>
        public static string ToHexDigest(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var chars = new char[text.Length * 3];
            int pos = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (i > 0) chars[pos++] = ' ';
                byte b = (byte)text[i];
                chars[pos++] = HexDigit(b >> 4);
                chars[pos++] = HexDigit(b & 0x0F);
            }
            return new string(chars, 0, pos);
        }

        static char HexDigit(int value)
        {
            return (char)(value < 10 ? '0' + value : 'A' + (value - 10));
        }
    }

    /// <summary>传输类：串口打不开、写失败、读超时、断连。</summary>
    public sealed class OcsTransportException : OcsException
    {
        public OcsTransportException(string message, string rawRequest, string rawResponse, string suggestedAction, Exception inner)
            : base(ErrorClass.Transport, message, rawRequest, rawResponse, suggestedAction, inner) { }
    }

    /// <summary>协议类：回包不以 &lt; 起、不以 &gt; 止、超长、含问号、无法解析。</summary>
    public sealed class OcsProtocolException : OcsException
    {
        public OcsProtocolException(string message, string rawRequest, string rawResponse, string suggestedAction)
            : base(ErrorClass.Protocol, message, rawRequest, rawResponse, suggestedAction, null) { }
    }

    /// <summary>设备类：&lt;ERROR&gt; / &lt;ER_SetValue&gt;，或回读与设置不一致。</summary>
    public sealed class OcsDeviceException : OcsException
    {
        public OcsDeviceException(string message, string rawRequest, string rawResponse, string suggestedAction)
            : base(ErrorClass.Device, message, rawRequest, rawResponse, suggestedAction, null) { }
    }

    /// <summary>
    /// 计划冲突：上位机在发送前拒绝（SRS FR-025 / TC-17，不产生设备错误）。
    /// 归类为 Configuration，由编排层决定按路径失败还是阻止开批。
    /// </summary>
    public sealed class OcsRouteConflictException : OcsException
    {
        public OcsRouteConflictException(string message, string rawRequest)
            : base(ErrorClass.Configuration, message, rawRequest, null, "修正路径计划后再发送：非 0 输出不可重复", null) { }
    }
}
