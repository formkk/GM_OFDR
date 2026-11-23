namespace GmixLx.Core
{
    /// <summary>
    /// 错误分类（SRS §4.14）。Vendor 出口完成前三类映射，上层按类别决定批次行为。
    /// </summary>
    public enum ErrorClass
    {
        None = 0,

        /// <summary>串口打不开、写失败、读超时、断连。</summary>
        Transport = 1,

        /// <summary>回包不以 &lt; 起、不以 &gt; 止、超长、含问号、无法解析。</summary>
        Protocol = 2,

        /// <summary>设备返回 &lt;ERROR&gt; / &lt;ER_SetValue&gt;，或回读与设置不一致。</summary>
        Device = 3,

        /// <summary>扫描失败、帧无效、指标不可算。</summary>
        Measurement = 4,

        /// <summary>标定缺失或 configHash 不一致。</summary>
        Calibration = 5,

        /// <summary>无适用基线或版本不匹配。</summary>
        Baseline = 6,

        /// <summary>配置非法或计划冲突，阻止开批/发送。</summary>
        Configuration = 7
    }
}
