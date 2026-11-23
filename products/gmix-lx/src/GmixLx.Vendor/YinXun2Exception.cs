using System;
using GmixLx.Core;

namespace GmixLx.Vendor
{
    /// <summary>
    /// YinXun_2 SDK 调用异常。返回码在这里一次性映射为 <see cref="ErrorClass"/>，
    /// 上层不再解析厂商 rc（规则 30：错误必须可分类）。
    /// </summary>
    public sealed class YinXun2Exception : DetectionException
    {
        public YinXun2Exception(ErrorClass errorClass, string message) : base(errorClass, message)
        {
        }

        public YinXun2Exception(ErrorClass errorClass, string message, Exception inner) : base(errorClass, message, inner)
        {
        }

        /// <summary>
        /// 厂商返回码 → 错误分类：
        /// 0 成功；−1 参数非法（本地/配置）；−2 测量失败；−3 采集失败；其余一律按设备错误。
        /// </summary>
        public static ErrorClass Classify(int resultCode)
        {
            switch (resultCode)
            {
                case YinXun2ProtocolConstants.StatusOk:
                    return ErrorClass.None;
                case YinXun2ProtocolConstants.StatusInvalidArgument:
                    return ErrorClass.Configuration;
                case YinXun2ProtocolConstants.StatusMeasurementFailed:
                    return ErrorClass.Measurement;
                case YinXun2ProtocolConstants.StatusAcquisitionFailed:
                    return ErrorClass.Device;
                default:
                    return ErrorClass.Device;
            }
        }

        /// <summary>rc 非 0 时抛出带操作名与厂商错误文本的异常。</summary>
        public static void ThrowIfFailed(int resultCode, string operation, string vendorError)
        {
            if (resultCode == YinXun2ProtocolConstants.StatusOk) return;

            string detail = string.IsNullOrEmpty(vendorError) ? "厂商未返回错误文本" : vendorError;
            throw new YinXun2Exception(Classify(resultCode),
                operation + " 失败（rc=" + resultCode + "）：" + detail);
        }
    }
}
