using System;
using GmixLx.Core;
using GmixLx.Vendor;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 映讯_2 曲线源：取一帧已完成平均的厂商帧，直接在**原生轴**上交给指标表
    /// （160000 点 / 425.836 µm），只屏蔽内部段，不重采样、不改步距（契约 L-01/L-04）。
    /// 设备打开与 ROI 由台架负责，本类只负责"一次扫描 = 一帧 ready"。
    /// </summary>
    public sealed class YinXun2CurveSource : ICurveSource
    {
        readonly YinXun2Client _client;
        readonly YinXun2ReferenceCalibration _calibration;
        readonly int _readyTimeoutMs;

        public YinXun2CurveSource(YinXun2Client client, YinXun2ReferenceCalibration calibration,
            int readyTimeoutMs = 3000)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (calibration == null) throw new ArgumentNullException("calibration");
            if (readyTimeoutMs <= 0) throw new ArgumentOutOfRangeException("readyTimeoutMs");
            calibration.Validate();
            _client = client;
            _calibration = calibration;
            _readyTimeoutMs = readyTimeoutMs;
        }

        /// <summary>
        /// 原生索引 i 的显示位置：x = (i × 原生步距 − AxisBM) / AxisA。
        /// 即从**设备连出口**（AxisBM 对应的内部参考峰）起算，与 LGA50 参考系同一刻度。
        /// </summary>
        public double AxisScaleM
        {
            get { return _calibration.YinXunPitchM / _calibration.AxisA; }
        }

        public double AxisOffsetM
        {
            get { return -_calibration.AxisBM / _calibration.AxisA; }
        }

        /// <summary>会话状态由 <see cref="YinXun2Station"/> 管理（Open + SetAnalysisRoi），这里无需额外准备。</summary>
        public void Begin()
        {
        }

        public double[] TryNextCurve(out string failure)
        {
            failure = null;
            try
            {
                YinXun2Measurement frame = _client.AcquireReady(_readyTimeoutMs);
                if (frame.LevelsDb.Length != frame.DistanceM.Length || frame.LevelsDb.Length < 2)
                {
                    failure = "厂商帧点数不足（" + frame.LevelsDb.Length + " 点）";
                    return null;
                }

                // 复制一份再处理：厂商帧可能被复用，且屏蔽/偏移要就地写
                var levels = (double[])frame.LevelsDb.Clone();
                YinXun2CurvePreparation.MaskInternalSection(levels, frame.DistanceM, _calibration);
                YinXun2CurvePreparation.ApplyLevelOffset(levels, _calibration.LevelOffsetDb);
                return levels;
            }
            catch (YinXun2Exception ex)
            {
                if (ex.ErrorClass != ErrorClass.Measurement) throw;
                failure = ex.Message;
                return null;
            }
        }

        public void End()
        {
        }

        public void Dispose()
        {
        }
    }
}
