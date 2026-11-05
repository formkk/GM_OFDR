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
