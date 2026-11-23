using System;
using System.Collections.Generic;
using GmixLx.Vendor;

namespace GmixLx.Tests
{
    /// <summary>
    /// 假 SDK：记录调用参数、按队列返回帧，可注入异常。原生 DLL 不在场时用它覆盖客户端与指标表逻辑。
    /// </summary>
    internal sealed class FakeYinXun2Sdk : IYinXun2Sdk
    {
        public readonly Queue<YinXun2Measurement> Frames = new Queue<YinXun2Measurement>();

        public YinXun2Measurement Fallback;
        public Exception AcquireError;

        public int OpenCalls;
        public int RoiCalls;
        public int AcquireCalls;
        public int CloseCalls;
        public int DisposeCalls;

        public string LastDirectory;
        public uint LastM;
        public uint LastN;
        public double LastRoiStart;
        public double LastRoiEnd;

        public void Open(string sdkDirectory, uint spatialAverageM, uint temporalAverageN)
        {
            OpenCalls++;
            LastDirectory = sdkDirectory;
            LastM = spatialAverageM;
            LastN = temporalAverageN;
        }

        public void SetAnalysisRoi(double startM, double endM)
        {
            RoiCalls++;
            LastRoiStart = startM;
            LastRoiEnd = endM;
        }

        public YinXun2Measurement Acquire()
        {
            AcquireCalls++;
            if (AcquireError != null) throw AcquireError;
            if (Frames.Count > 0) return Frames.Dequeue();
            if (Fallback != null) return Fallback;
            return new YinXun2Measurement { AverageReady = true, AverageCount = 1 };
        }

        public void Close()
        {
            CloseCalls++;
        }

        public void Dispose()
        {
            DisposeCalls++;
        }
    }
}
