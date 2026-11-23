namespace GmixLx.Vendor
{
    /// <summary>
    /// YinXun_2（映讯_2）OCS SDK 常量唯一来源（规则 30：其他文件不得出现协议字面量）。
    /// 依据：references/YinXun_2/customer/ocs_measurement.h、docs/detection/映讯_2_硬件与SDK接入核查_2026-11-16.md。
    /// 注意：**就绪超时不在此文件**——它是运行参数，由调用方注入。
    /// </summary>
    public static class YinXun2ProtocolConstants
    {
        // ---- SDK 文件布局 ----

        /// <summary>主 DLL 名（位于厂商 customer 目录根）。</summary>
        public const string DllFileName = "ocs_measurement.dll";

        /// <summary>依赖运行库相对目录；ocs_open_device 的参数就是该目录的绝对路径。</summary>
        public const string SdkBinRelativePath = "acquisition/bin";

        /// <summary>错误文本缓冲容量（与探针 tools/yx2_probe.py 一致）。</summary>
        public const int ErrorCapacity = 512;

        // ---- 采集参数 ----

        /// <summary>默认空间平均 m。</summary>
        public const uint DefaultSpatialAverageM = 1;

        /// <summary>
        /// 默认时间平均 n（非重叠分组：每 n 次扫频在功率域平均后输出 1 帧 ready）。
        /// 2026-11-16 实测 n=4：IN1→OUT1~8 仍 8/8 有效，距离与 n=10 一致到毫米，单路耗时约 0.83 s。
        /// </summary>
        public const uint DefaultTemporalAverageN = 4;

        // ---- 距离轴（厂商固定，不由应用换算）----

        /// <summary>每帧点数。</summary>
        public const int PointCount = 160000;

        /// <summary>点距（m）；实测等步距，勿四舍五入。</summary>
        public const double PitchM = 0.0004258363425773126;

        /// <summary>轴末端距离（m）=（点数 − 1）× 点距 ≈ 68.133389 m。</summary>
        public const double MaxDistanceM = (PointCount - 1) * PitchM;

        // ---- 返回码（enum OCS_*）----

        public const int StatusOk = 0;
        public const int StatusInvalidArgument = -1;
        public const int StatusMeasurementFailed = -2;
        public const int StatusAcquisitionFailed = -3;

        // ---- loss_status（enum OCS_LOSS_*）----

        public const int LossValid = 0;
        public const int LossNoPeak = 1;
        public const int LossNoPeakWidth = 2;
        public const int LossNoValidStep = 3;
        public const int LossRoiOutsideAxis = 5;

        /// <summary>ROI 是否可用：只有被测量覆盖且检出峰时才算有效。</summary>
        public static bool IsLossValid(int lossStatus)
        {
            return lossStatus == LossValid;
        }
    }
}
