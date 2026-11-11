namespace Detection.Vendor
{
    /// <summary>
    /// OCS 协议常量唯一来源（规则 30：其他文件不得出现协议字节或命令字面量）。
    /// 依据：docs/detection/OCS_控制协议分析.md 与 OCS_协议实测报告_2026-11-11.md。
    /// 注意：**超时与重试不在此文件**——它们是配置项，默认值来自实测，由调用方注入（SDD §3.2/§8.1）。
    /// </summary>
    public static class OcsProtocolConstants
    {
        /// <summary>帧起始定界符。</summary>
        public const char FrameStart = '<';

        /// <summary>帧结束定界符；同时是设备执行边界（缺结束符的设备会挂起解析器，实测）。</summary>
        public const char FrameEnd = '>';

        /// <summary>单帧最大长度（含定界符）。实测最长回包 53 字节，留足余量防失控。</summary>
        public const int MaxFrameLength = 256;

        // ---- 通用响应 ----
        public const string ResponseOk = "<OK>";
        public const string ResponseError = "<ERROR>";
        public const string ResponseErrorSetValue = "<ER_SetValue>";

        // ---- 命令字 ----
        public const string CmdIdentify = "IDN?";
        public const string CmdSerialNumber = "SN?";
        public const string CmdReset = "RST";
        public const string CmdSetSwitch = "SETSW";
        public const string CmdQuerySwitch = "QURSW";

        // ---- 串口物理参数（实测：8×8 为 115200，厂商文档写 9600 与实物不符）----
        public const int BaudRate1x8 = 9600;
        public const int BaudRate8x8 = 115200;
        public const int DataBits = 8;

        // ---- 通道取值域 ----
        public const int MinChannel = 0;   // 0 = 断开
        public const int MaxChannel = 8;
        public const int InputCount8x8 = 8;

        // ---- 型号识别 token（用于自检防错接）----
        public const string ModelToken1x8 = "1X8";
        public const string ModelToken8x8 = "8x8";
    }
}
