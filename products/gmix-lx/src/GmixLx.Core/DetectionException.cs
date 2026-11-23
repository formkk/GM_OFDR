using System;

namespace GmixLx.Core
{
    /// <summary>
    /// 检测平台的统一异常基类：错误分类在设备/协议出口确定一次（SRS §4.14），
    /// 上层只按 <see cref="ErrorClass"/> 决定"废掉本行"还是"中止整批"。
    /// </summary>
    public class DetectionException : Exception
    {
        public DetectionException(ErrorClass errorClass, string message) : base(message)
        {
            ErrorClass = errorClass;
        }

        public DetectionException(ErrorClass errorClass, string message, Exception inner) : base(message, inner)
        {
            ErrorClass = errorClass;
        }

        public ErrorClass ErrorClass { get; private set; }
    }
}
