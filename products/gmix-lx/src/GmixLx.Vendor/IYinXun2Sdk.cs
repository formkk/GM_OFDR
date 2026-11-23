using System;

namespace GmixLx.Vendor
{
    /// <summary>
    /// YinXun_2 SDK 会话抽象：只为可测试性存在（与 IOfdrTransport 同一理由）。
    /// 真实实现见 <see cref="YinXun2NativeSdk"/>；上层只经 <see cref="YinXun2Client"/> 调用。
    /// </summary>
    public interface IYinXun2Sdk : IDisposable
    {
        /// <summary>打开设备。sdkDirectory 为含 ocs_measurement.dll 的厂商 customer 目录。</summary>
        void Open(string sdkDirectory, uint spatialAverageM, uint temporalAverageN);

        /// <summary>设置分析 ROI；厂商要求 0 &lt;= start &lt; end 且落在距离轴内。</summary>
        void SetAnalysisRoi(double startM, double endM);

        /// <summary>取一帧；内部完成结果释放，返回托管副本。</summary>
        YinXun2Measurement Acquire();

        /// <summary>关闭设备；可重复调用。</summary>
        void Close();
    }
}
