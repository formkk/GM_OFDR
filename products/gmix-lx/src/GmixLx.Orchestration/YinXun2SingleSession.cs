using System;
using System.Collections.Generic;
using System.Diagnostics;
using GmixLx.Core;
using GmixLx.Vendor;

namespace GmixLx.Orchestration
{
    /// <summary>单帧调试结果：原生轴 + 参考系电平（已叠加显示偏移）+ ROI 汇总值 + ROI 内峰表。</summary>
    public sealed class YinXun2SingleFrame
    {
        public double[] DistanceM = new double[0];
        public double[] LevelsDb = new double[0];

        /// <summary>原生轴步距（m），用于把峰质心换算成距离。</summary>
        public double PitchM;

        public double RoiStartM;
        public double RoiEndM;

        /// <summary>厂商 ROI 汇总插入损耗（原始 reflection_db 口径，非计量量）。</summary>
        public double RoiIlDb = double.NaN;

        /// <summary>厂商 ROI 汇总回损（原始 reflection_db 口径，非计量量）。</summary>
        public double RoiRlDb = double.NaN;

        public uint AverageCount;
        public double AcquireMs;

        /// <summary>本帧使用的峰阈值（曲线中位 + 余量）。</summary>
        public double ThresholdDb;

        /// <summary>全量程的峰（位置 = Centroid × 步距，单位 m），不受 ROI 影响。</summary>
        public List<CurvePeak> Peaks = new List<CurvePeak>();
    }

    /// <summary>
    /// 映讯_2 单路调试会话：只打开设备并取 ready 帧，不涉及 OCS、标定变换与指标表。
    /// App 只依赖本类，不接触 Vendor 类型（分层约束）。
    /// </summary>
    public sealed class YinXun2SingleSession : IDisposable
    {
        /// <summary>单帧等待 ready 的超时；n=4 时约 0.65 s 一帧，留足余量。</summary>
        public const int ReadyTimeoutMs = 15000;

        /// <summary>事件表起点：0 m 附近的直流/内部伪峰不计入峰表。</summary>
        public const double PeakStartM = 0.05;

        readonly YinXun2Client _client;
        bool _disposed;

        readonly double _levelOffsetDb;

        YinXun2SingleSession(YinXun2Client client, uint spatialAverageM, uint temporalAverageN,
            double roiStartM, double roiEndM, double levelOffsetDb)
        {
            _client = client;
            SpatialAverageM = spatialAverageM;
            TemporalAverageN = temporalAverageN;
            RoiStartM = roiStartM;
            RoiEndM = roiEndM;
            _levelOffsetDb = levelOffsetDb;
        }

        public uint SpatialAverageM { get; private set; }

        public uint TemporalAverageN { get; private set; }

        public double RoiStartM { get; private set; }

        public double RoiEndM { get; private set; }

        /// <summary>打开设备并按给定 ROI 建立会话；m/n 由调用方显式给出，不猜默认值。</summary>
        public static YinXun2SingleSession Open(string sdkDirectory,
            uint spatialAverageM, uint temporalAverageN, double roiStartM, double roiEndM,
            double levelOffsetDb = 0.0)
        {
            if (string.IsNullOrWhiteSpace(sdkDirectory))
                throw new ArgumentException("映讯 SDK 目录不能为空", "sdkDirectory");

            var client = new YinXun2Client(new YinXun2NativeSdk());
            try
            {
                client.Open(sdkDirectory, spatialAverageM, temporalAverageN);
                client.SetAnalysisRoi(roiStartM, roiEndM);
            }
            catch
            {
                client.Dispose();
                throw;
            }
            return new YinXun2SingleSession(client, spatialAverageM, temporalAverageN,
                roiStartM, roiEndM, levelOffsetDb);
        }

        /// <summary>运行中修改 ROI；m/n 变更需要重新打开会话。</summary>
        public void SetRoi(double startM, double endM)
        {
            _client.SetAnalysisRoi(startM, endM);
            RoiStartM = startM;
            RoiEndM = endM;
        }

        /// <summary>
        /// 取一帧 ready 数据，在整条曲线上按“中位 + marginDb、合并间距 mergeGapRows”找峰。
        /// 峰表覆盖全量程，不随 ROI 变化；ROI 只影响厂商汇总值。
        /// 电平叠加参考标定的显示偏移，与厂商 GUI 和指标表同一参考系。
        /// </summary>
        public YinXun2SingleFrame Acquire(double marginDb, int mergeGapRows)
        {
            if (_disposed) throw new ObjectDisposedException("YinXun2SingleSession");
            if (mergeGapRows < 0) throw new ArgumentOutOfRangeException("mergeGapRows");

            var watch = Stopwatch.StartNew();
            YinXun2Measurement measurement = _client.AcquireReady(ReadyTimeoutMs);
            watch.Stop();

            double[] levels = (double[])measurement.LevelsDb.Clone();
            YinXun2CurvePreparation.ApplyLevelOffset(levels, _levelOffsetDb);
            double threshold = CurveMetrics.NoiseFloorMedian(levels) + marginDb;
            List<CurvePeak> all = CurveMetrics.FindPeaks(levels, threshold, mergeGapRows);

            var peaks = new List<CurvePeak>();
            foreach (CurvePeak peak in all)
            {
                if (peak.Centroid * YinXun2ProtocolConstants.PitchM >= PeakStartM) peaks.Add(peak);
            }

            var frame = new YinXun2SingleFrame
            {
                DistanceM = measurement.DistanceM,
                LevelsDb = levels,
                PitchM = YinXun2ProtocolConstants.PitchM,
                RoiStartM = RoiStartM,
                RoiEndM = RoiEndM,
                RoiIlDb = measurement.InsertionLossDb,
                RoiRlDb = measurement.ReturnLossDb,
                AverageCount = measurement.AverageCount,
                AcquireMs = watch.Elapsed.TotalMilliseconds,
                ThresholdDb = threshold,
                Peaks = peaks
            };
            return frame;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _client.Dispose();
        }
    }
}
