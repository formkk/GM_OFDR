namespace GmixLx.Vendor
{
    /// <summary>厂商 ROI 内检出的一个峰（OcsPeakResult 的托管副本）。</summary>
    public sealed class YinXun2Peak
    {
        public double DistanceM;
        public double PeakLevelDb;
        public double InsertionLossDb;
        public double ReturnLossDb;
        public int LossStatus;
        public int IlStatus;
        public int RlStatus;
    }

    /// <summary>
    /// 一帧 SDK 测量结果的托管副本：轴 + 反射电平 + ROI 峰与厂商指标。
    /// 只做复制，不做换算；轴和电平保持厂商原值（标定变换在 GmixLx.Core 完成一次）。
    /// </summary>
    public sealed class YinXun2Measurement
    {
        public double[] DistanceM = new double[0];

        public double[] LevelsDb = new double[0];

        public YinXun2Peak[] Peaks = new YinXun2Peak[0];

        public double PeakDistanceM;
        public double InsertionLossDb;
        public double ReturnLossDb;

        /// <summary>ROI 汇总回损状态（OCS_LOSS_*）。</summary>
        public int LossStatus;

        /// <summary>ROI 插入损耗状态（OCS_LOSS_*）。</summary>
        public int RoiIlStatus;

        /// <summary>ROI 回损状态（OCS_LOSS_*）。</summary>
        public int RoiRlStatus;

        /// <summary>当前已累计的平均帧计数。</summary>
        public uint AverageCount;

        /// <summary>
        /// 本帧是否属于一个已完成的平均组。n&gt;1 时绝大多数 acquire 返回 false，
        /// 只有凑满 n 帧的那一次为 true；指标计算必须只用 ready 帧。
        /// </summary>
        public bool AverageReady;
    }
}
