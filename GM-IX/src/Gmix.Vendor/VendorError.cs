using System;

namespace Gmix.Vendor
{
    /// <summary>错误分类，与 ICD Part B §4 / SRS FR-UIX-03 的五分类对齐（组一致性由编排层负责）。</summary>
    public enum VendorErrorKind
    {
        Connection,
        Protocol,
        Data,
        Timeout
    }

    /// <summary>厂商调用失败：保留厂商码与分类，禁止静默吞掉（ICD Part B §4）。</summary>
    public sealed class VendorException : Exception
    {
        public VendorErrorKind Kind { get; }
        public int Code { get; }

        public VendorException(VendorErrorKind kind, int code, string message)
            : base(message)
        {
            Kind = kind;
            Code = code;
        }

        /// <summary>qt_acq 状态码映射（qt_acq.h 宏定义）。</summary>
        public static VendorException FromQtAcq(int code, string lastError)
        {
            VendorErrorKind kind;
            switch (code)
            {
                case -3: // INVALID_ARGUMENT
                case -7: // NOT_CONFIGURED
                    kind = VendorErrorKind.Data;
                    break;
                case -4: // INVALID_HANDLE
                case -5: // NULL_POINTER
                    kind = VendorErrorKind.Protocol;
                    break;
                case -6: // BUFFER_TOO_SMALL
                    kind = VendorErrorKind.Data;
                    break;
                default: // -1 UNKNOWN / -2 SDK
                    kind = VendorErrorKind.Connection;
                    break;
            }
            string detail = string.IsNullOrEmpty(lastError) ? "(no message)" : lastError;
            return new VendorException(kind, code, "qt_acq 调用失败: code=" + code + ", " + detail);
        }
    }

    /// <summary>错误文案（SRS FR-UIX-03 要求消息可读）。</summary>
    public static class VendorErrorText
    {
        public static string Describe(VendorException ex)
        {
            switch (ex.Kind)
            {
                case VendorErrorKind.Connection:
                    return "设备连接失败：" + ex.Message + "（检查 IP/网线/厂商软件是否占用设备）";
                case VendorErrorKind.Protocol:
                    return "设备会话失效：" + ex.Message + "（请重新连接）";
                case VendorErrorKind.Data:
                    return "数据或参数错误：" + ex.Message;
                case VendorErrorKind.Timeout:
                    return "设备响应超时：" + ex.Message;
                default:
                    return ex.Message;
            }
        }
    }
}
