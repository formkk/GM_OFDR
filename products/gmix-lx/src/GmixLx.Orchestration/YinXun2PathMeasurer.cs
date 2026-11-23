using System;
using GmixLx.Core;
using GmixLx.Vendor;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 映讯_2 单路测量管线：厂商帧 → 原生轴屏蔽内部段 → 原生 8 峰指标 → 按标定翻译到契约参考系。
    /// 不做重采样；参考系换算只在 <see cref="YinXun2MetricsMapper"/> 里发生一次。
    /// </summary>
    public static class YinXun2PathMeasurer
    {
        /// <summary>
        /// 映讯_2 的默认阈值余量（参考系 dB）。
        /// 2026-11-16 真机实测：25 dB 时 IN1→OUT2/4/7 只有 7 峰，22 dB 时 8/8 有效且无伪峰（两次复测一致）。
        /// LGA50 的 30 dB 是它自己的噪声底标定，不能直接套用。
        /// </summary>
        public const double DefaultReferenceMarginDb = 22.0;

        /// <summary>
        /// 映讯_2 的原生轴判据：网格步距换原生步距，A/D 经轴传递换到原生轴；
        /// 峰合并间距与平均半窗是物理长度，与设备无关，保持参考系数值。
        /// </summary>
        public static CurveMetricsConfig DefaultNativeMetrics(YinXun2ReferenceCalibration calibration)
        {
            CurveMetricsConfig reference = DefaultReferenceMetrics();
            reference.NoiseFloorMarginDb = DefaultReferenceMarginDb;
            return ToNativeConfig(reference, calibration);
        }

        /// <summary>
        /// 参考系判据锚点（历史继承自 GM-IX 参考系基线，只作参考系坐标，不代表接 LGA50 设备）。
        /// </summary>
        public static CurveMetricsConfig DefaultReferenceMetrics()
        {
            return new CurveMetricsConfig
            {
                MetersPerRow = 0.000185,
                MarkAM = 4.44,
                MarkDM = 10.6005,
                NoiseFloorMarginDb = 30.0
            };
        }

        /// <summary>把参考系判据快照派生为本设备的原生轴判据（返回新对象，不改入参）。</summary>
        public static CurveMetricsConfig ToNativeConfig(CurveMetricsConfig reference,
            YinXun2ReferenceCalibration calibration)
        {
            if (reference == null) throw new ArgumentNullException("reference");
            if (calibration == null) throw new ArgumentNullException("calibration");
            calibration.Validate();

            CurveMetricsConfig config = reference.Clone();
            config.MetersPerRow = calibration.YinXunPitchM;
            config.MarkAM = calibration.ToYinXunDistance(reference.MarkAM);
            config.MarkDM = calibration.ToYinXunDistance(reference.MarkDM);
            // 阈值余量是 dB 对比度：参考系 M → 原生 α×M（截距在对比度中抵消）。
            // 例：LGA50 的 30 dB 在 α=0.9985 下对应原生 29.96 dB；旧的 28.24 dB 是 α=0.9414 时的折算值。
            config.NoiseFloorMarginDb = reference.NoiseFloorMarginDb * calibration.DbAlpha;
            return config;
        }

        public static PathMetrics Measure(YinXun2Measurement measurement,
            YinXun2ReferenceCalibration calibration, CurveMetricsConfig referenceConfig,
            int inChannel, int outChannel)
        {
            if (measurement == null) throw new ArgumentNullException("measurement");
            if (calibration == null) throw new ArgumentNullException("calibration");
            if (referenceConfig == null) throw new ArgumentNullException("referenceConfig");
            if (measurement.LevelsDb.Length != measurement.DistanceM.Length)
                throw new ArgumentException("YinXun_2 帧的距离与电平长度不一致");
            if (measurement.LevelsDb.Length < 2)
                throw new ArgumentException("YinXun_2 帧少于 2 个点");

            var levels = (double[])measurement.LevelsDb.Clone();
            YinXun2CurvePreparation.MaskInternalSection(levels, measurement.DistanceM, calibration);
            YinXun2CurvePreparation.ApplyLevelOffset(levels, calibration.LevelOffsetDb);

            CurveMetricsConfig native = ToNativeConfig(referenceConfig, calibration);
            PathMetrics metrics = CurveMetrics.Compute(levels, inChannel, outChannel, native);
            return new YinXun2MetricsMapper(calibration).ToReport(metrics);
        }
    }
}
