using System;

namespace Gmix.Data
{
    /// <summary>
    /// 与厂商 UI 对齐的默认采集参数。
    /// 厂商确认：50 MHz 采样、光源上扫 10 ms、有效窗口 1–9 ms。
    /// </summary>
    public static class AcquisitionDefaults
    {
        public const int SampleRateMhz = 50;
        public const int AcqTimeMs = 8;
        public const int SegCount = 1;
        public const int TriggerMode = 0;       // 软件触发
        public const int TriggerDelayMs = 1;    // 从 1 ms 开始，采到 9 ms
        public const double SweepPeriodMs = 10.0;
        public const double WindowStartMs = 1.0;
        public const double WindowEndMs = 9.0;

        /// <summary>设备目标地址：qt_acq 网络层内置，厂商未提供修改接口（TBD-15），仅作展示与元数据。</summary>
        public const string DeviceIp = "192.168.5.20";

        /// <summary>数据根保留上限：循环自动提交后超限按最旧优先删除（SRS FR-HIS-06）。</summary>
        public const long RetentionBytes = 20L * 1024L * 1024L * 1024L;

        /// <summary>保留策略检查间隔（组数）：避免每次提交都全量统计目录大小。</summary>
        public const int RetentionCheckGroups = 20;

        /// <summary>可用磁盘空间低于该值即停止循环采集，避免写满系统盘。</summary>
        public const long MinFreeBytes = 5L * 1024L * 1024L * 1024L;

        /// <summary>分析区间保护带（m）：处理后固定参数，不向 UI 暴露，仅用于插损起止窗口。</summary>
        public const double GuardM = 0.1;

        public const int PChannel = 0;
        public const int SChannel = 1;
        public const int AuxChannel = 2;

        public static long ExpectedFrames()
        {
            return (long)SampleRateMhz * AcqTimeMs * 1000L * SegCount;
        }

        public static long BufferBytes()
        {
            return (long)SampleRateMhz * AcqTimeMs * 8000L * SegCount;
        }

        public static AcquisitionInfo Create()
        {
            var a = new AcquisitionInfo();
            a.sampleRateHz = SampleRateMhz * 1000000L;
            a.sampleRateMhz = SampleRateMhz;
            a.acqTimeMs = AcqTimeMs;
            a.segCount = SegCount;
            a.triggerMode = TriggerMode;
            a.triggerDelayMs = TriggerDelayMs;
            a.channelMap = new ChannelMapInfo();
            a.channelMap.p = PChannel;
            a.channelMap.s = SChannel;
            a.channelMap.aux = AuxChannel;
            a.channelMap.ignored = new int[] { 3 };
            a.expectedFrames = ExpectedFrames();
            a.bufferBytes = BufferBytes();
            a.bytesPerFrame = 8;
            a.sweepPeriodMs = SweepPeriodMs;
            a.windowStartMs = WindowStartMs;
            a.windowEndMs = WindowEndMs;
            return a;
        }
    }
}
