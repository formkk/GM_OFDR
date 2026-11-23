using System;
using GmixLx.Core;

namespace GmixLx.Vendor
{
    /// <summary>
    /// YinXun_2 测量客户端：参数校验、会话状态与"只取 ready 帧"的语义收口在这里，
    /// 上层（Orchestration）不接触原生调用，也不判断平均帧计数。
    /// </summary>
    public sealed class YinXun2Client : IDisposable
    {
        readonly IYinXun2Sdk _sdk;
        bool _opened;

        public YinXun2Client(IYinXun2Sdk sdk)
        {
            if (sdk == null) throw new ArgumentNullException("sdk");
            _sdk = sdk;
        }

        public bool IsOpen
        {
            get { return _opened; }
        }

        /// <summary>打开设备；sdkDirectory 为含 ocs_measurement.dll 的厂商 customer 目录。</summary>
        public void Open(string sdkDirectory,
            uint spatialAverageM = YinXun2ProtocolConstants.DefaultSpatialAverageM,
            uint temporalAverageN = YinXun2ProtocolConstants.DefaultTemporalAverageN)
        {
            if (string.IsNullOrWhiteSpace(sdkDirectory))
                throw new ArgumentException("YinXun_2 SDK 目录不能为空", "sdkDirectory");
            if (spatialAverageM == 0)
                throw new ArgumentOutOfRangeException("spatialAverageM", "空间平均 m 必须 ≥ 1");
            if (temporalAverageN == 0)
                throw new ArgumentOutOfRangeException("temporalAverageN", "时间平均 n 必须 ≥ 1");

            _sdk.Open(sdkDirectory, spatialAverageM, temporalAverageN);
            _opened = true;
        }

        /// <summary>设置厂商分析 ROI；不设 ROI 时厂商会返回 0 峰与 NaN 指标。</summary>
        public void SetAnalysisRoi(double startM, double endM)
        {
            if (double.IsNaN(startM) || double.IsInfinity(startM) ||
                double.IsNaN(endM) || double.IsInfinity(endM))
            {
                throw new ArgumentException("ROI 必须为有限数");
            }
            if (startM < 0)
                throw new ArgumentOutOfRangeException("startM", "ROI 起点不能为负");
            if (startM >= endM)
                throw new ArgumentException("ROI 必须满足 start < end");
            if (endM > YinXun2ProtocolConstants.MaxDistanceM)
            {
                throw new ArgumentOutOfRangeException("endM",
                    "ROI 终点超出距离轴末端 " + YinXun2ProtocolConstants.MaxDistanceM.ToString("F6") + " m");
            }

            EnsureOpen();
            _sdk.SetAnalysisRoi(startM, endM);
        }

        /// <summary>取一帧（可能是未就绪的中间平均帧）。</summary>
        public YinXun2Measurement Acquire()
        {
            EnsureOpen();
            return _sdk.Acquire();
        }

        /// <summary>
        /// 取一帧已完成的平均帧：n&gt;1 时循环丢弃中间帧，直到 <see cref="YinXun2Measurement.AverageReady"/> 为真。
        /// 超时按测量错误上报，不做"用中间帧凑数"的兜底。
        /// </summary>
        public YinXun2Measurement AcquireReady(int timeoutMs = 3000)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");
            EnsureOpen();

            long deadline = Environment.TickCount64 + timeoutMs;
            YinXun2Measurement last = null;
            do
            {
                last = _sdk.Acquire();
                if (last.AverageReady) return last;
            }
            while (Environment.TickCount64 < deadline);

            throw new YinXun2Exception(ErrorClass.Measurement,
                "等待平均帧就绪超时（" + timeoutMs + " ms，最后 AverageCount=" + last.AverageCount +
                "）；请确认时间平均 n 与 ROI 设置");
        }

        public void Close()
        {
            _sdk.Close();
            _opened = false;
        }

        public void Dispose()
        {
            try
            {
                Close();
            }
            finally
            {
                _sdk.Dispose();
            }
        }

        void EnsureOpen()
        {
            if (!_opened)
            {
                throw new YinXun2Exception(ErrorClass.Configuration, "YinXun_2 设备未打开，请先 Open");
            }
        }
    }
}
