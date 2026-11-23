using System;
using GmixLx.Core;
using GmixLx.Vendor;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 映讯_2 台架装配：SDK 会话 + 两台 OCS + 指标表运行器。
    /// App 只依赖本类与 <see cref="MetricTableRunner"/>，不接触 Vendor（分层约束）。
    /// 设备差异只体现在曲线源：指标表、11 列与 8 峰判据与 LGA50 完全一致。
    /// </summary>
    public sealed class YinXun2Station : IDisposable
    {
        readonly YinXun2Client _client;
        readonly Ocs1x8Client _ocs1x8;
        readonly Ocs8x8Client _ocs8x8;
        readonly YinXun2CurveSource _source;

        YinXun2Station(YinXun2Client client, Ocs1x8Client ocs1x8, Ocs8x8Client ocs8x8,
            YinXun2CurveSource source, MetricTableRunner runner)
        {
            _client = client;
            _ocs1x8 = ocs1x8;
            _ocs8x8 = ocs8x8;
            _source = source;
            Runner = runner;
        }

        public MetricTableRunner Runner { get; private set; }

        /// <summary>产品默认时间平均 n（单一来源在 Vendor 常量，App 经这里读取，避免跨层引用）。</summary>
        public const uint DefaultTemporalAverageN = YinXun2ProtocolConstants.DefaultTemporalAverageN;

        /// <summary>厂商原生点距（单一来源在 Vendor 常量，App 经这里读取，避免跨层引用）。</summary>
        public const double NativePitchM = YinXun2ProtocolConstants.PitchM;

        /// <summary>
        /// 打开 SDK 并按给定 ROI 建立会话，再打开两台 OCS。
        /// m/n/ROI/超时全部由调用方给出：默认 ROI 未文档化（TBD-Y03），不得在此猜值。
        /// </summary>
        public static YinXun2Station Create(string sdkDirectory, string port1x8, string port8x8,
            YinXun2ReferenceCalibration calibration, CurveMetricsConfig metrics,
            uint spatialAverageM, uint temporalAverageN,
            double roiStartM, double roiEndM, int readyTimeoutMs)
        {
            if (calibration == null) throw new ArgumentNullException("calibration");
            if (metrics == null) throw new ArgumentNullException("metrics");
            calibration.Validate();

            var client = new YinXun2Client(new YinXun2NativeSdk());
            var ocs1x8 = new Ocs1x8Client(
                new OcsSerialTransport(port1x8, OcsProtocolConstants.BaudRate1x8), 300);
            var ocs8x8 = new Ocs8x8Client(
                new OcsSerialTransport(port8x8, OcsProtocolConstants.BaudRate8x8), 300);

            try
            {
                client.Open(sdkDirectory, spatialAverageM, temporalAverageN);
                client.SetAnalysisRoi(roiStartM, roiEndM);
                ocs1x8.Open();
                ocs8x8.Open();
            }
            catch
            {
                client.Dispose();
                ocs1x8.Dispose();
                ocs8x8.Dispose();
                throw;
            }

            var source = new YinXun2CurveSource(client, calibration, readyTimeoutMs);

            // 判据以参考系给出（与 LGA50 同一套锚点），这里换成本设备的原生轴；
            // 结果再由 YinXun2MetricsMapper 换回参考系，UI 看到的仍是同一套 11 列。
            CurveMetricsConfig native = YinXun2PathMeasurer.ToNativeConfig(metrics, calibration);
            return new YinXun2Station(client, ocs1x8, ocs8x8, source,
                new MetricTableRunner(source, ocs1x8, ocs8x8, native,
                    new YinXun2MetricsMapper(calibration)));
        }

        public void Dispose()
        {
            _source.Dispose();
            _client.Dispose();
            _ocs1x8.Dispose();
            _ocs8x8.Dispose();
        }
    }
}
