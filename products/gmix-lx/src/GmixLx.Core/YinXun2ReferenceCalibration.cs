using System;
using System.IO;
using System.Text.Json;

namespace GmixLx.Core
{
    /// <summary>
    /// YinXun_2 参考标定（与 LGA50 一致性的传递标定，非计量学绝对标定）。
    /// 来源：2026-11-16 LGA50 11-13 数据 + YinXun_2 IN1→OUT1/2/5/6 配对拟合。
    /// 轴：d_yx = AxisA × d_lga + AxisBM。
    /// dB：DbAlpha 为设备 dB 刻度（2026-11-17 用 FVA-3150 已知衰减验证）；
    /// 历史 LGA50 电平传递斜率 0.9414 不再用于 IL，仅保留在标定文件的 transfer_slope_to_lga50。
    /// </summary>
    public sealed class YinXun2ReferenceCalibration
    {
        public string Device { get; set; }

        public string Status { get; set; }

        public double AxisA { get; set; }

        public double AxisBM { get; set; }

        /// <summary>
        /// 设备 dB 刻度（原生 IL = DbAlpha × 真值 IL）。
        /// 2026-11-17 用 EXFO FVA-3150 单支路 0–10 dB 衰减扫描验证：α=0.9985±0.0051，
        /// R²=0.99974；不再使用试标定的 0.9414，也不再放大 6.2%。
        /// 运行期用于 IL 换算（÷alpha）与阈值余量折算。
        /// </summary>
        public double DbAlpha { get; set; }

        /// <summary>dB 传递截距。运行期不使用：IL 为差值时抵消，RL 保持设备内部参考（TBD-Y11）。</summary>
        public double DbBeta { get; set; }

        /// <summary>
        /// 厂商 GUI 刻度偏移（dB），叠加到 `reflection_db` 上，使曲线与 RL 与厂商 GUI 对齐。
        /// 2026-11-17 裁定采用厂商 OCS_test 的 `rl_offset_db` 默认 **−90.0 dB**；
        /// 实测同帧差 −89.96/−89.67/−89.85（均值 −89.83），差 0.17 dB。该偏移是显示/频谱平移，不是绝对 RL 标定（TBD-Y17）。
        /// IL 为差值，不受此偏移影响。
        /// </summary>
        public double LevelOffsetDb { get; set; }

        public double InternalReferencePeakM { get; set; }

        /// <summary>映讯_2 原生点距（m）；计算在其原生轴上完成，不做重采样。</summary>
        public double YinXunPitchM { get; set; } = 0.0004258363425773126;

        public int Lga50PointCount { get; set; } = 82152;

        public double Lga50PitchM { get; set; } = 0.000185;

        /// <summary>LGA50 网格上此距离之前的点用中位电平屏蔽，避免把设备内部峰当成第 9 个峰。</summary>
        public double MaskBeforeLga50M { get; set; } = 0.2;

        /// <summary>厂商 ROI 起点（仅用于厂商 ROI/IL-RL；8 峰传递不使用此值）。</summary>
        public double VendorRoiStartM { get; set; } = 3.5;

        public double ToYinXunDistance(double lga50DistanceM)
        {
            return AxisA * lga50DistanceM + AxisBM;
        }

        public double ToLga50Distance(double yinXunDistanceM)
        {
            return (yinXunDistanceM - AxisBM) / AxisA;
        }

        public double ToYinXunLevel(double lga50LevelDb)
        {
            return DbAlpha * lga50LevelDb + DbBeta;
        }

        public double ToLga50Level(double yinXunLevelDb)
        {
            return (yinXunLevelDb - DbBeta) / DbAlpha;
        }

        public void Validate()
        {
            if (double.IsNaN(AxisA) || double.IsInfinity(AxisA) || AxisA <= 0)
                throw new InvalidDataException("YinXun_2 标定 AxisA 必须为有限正数");
            if (double.IsNaN(AxisBM) || double.IsInfinity(AxisBM))
                throw new InvalidDataException("YinXun_2 标定 AxisBM 必须为有限数");
            if (double.IsNaN(DbAlpha) || double.IsInfinity(DbAlpha) || DbAlpha == 0)
                throw new InvalidDataException("YinXun_2 标定 DbAlpha 必须为非零有限数");
            if (double.IsNaN(DbBeta) || double.IsInfinity(DbBeta))
                throw new InvalidDataException("YinXun_2 标定 DbBeta 必须为有限数");
            if (double.IsNaN(LevelOffsetDb) || double.IsInfinity(LevelOffsetDb))
                throw new InvalidDataException("YinXun_2 标定 LevelOffsetDb 必须为有限数");
            if (double.IsNaN(InternalReferencePeakM) || double.IsInfinity(InternalReferencePeakM))
                throw new InvalidDataException("YinXun_2 标定 InternalReferencePeakM 必须为有限数");
            if (double.IsNaN(YinXunPitchM) || double.IsInfinity(YinXunPitchM) || YinXunPitchM <= 0)
                throw new InvalidDataException("YinXun_2 标定 YinXunPitchM 必须为有限正数");
            if (Lga50PointCount <= 1)
                throw new InvalidDataException("YinXun_2 标定 Lga50PointCount 必须大于 1");
            if (double.IsNaN(Lga50PitchM) || double.IsInfinity(Lga50PitchM) || Lga50PitchM <= 0)
                throw new InvalidDataException("YinXun_2 标定 Lga50PitchM 必须为有限正数");
            if (double.IsNaN(MaskBeforeLga50M) || double.IsInfinity(MaskBeforeLga50M) || MaskBeforeLga50M < 0)
                throw new InvalidDataException("YinXun_2 标定 MaskBeforeLga50M 必须为非负有限数");
        }

        public static YinXun2ReferenceCalibration Load(string path)
        {
            if (path == null) throw new ArgumentNullException("path");
            string json = File.ReadAllText(path);
            var root = JsonSerializer.Deserialize<JsonRoot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (root == null || root.axis == null || root.db == null)
                throw new InvalidDataException("YinXun_2 标定文件缺少 axis/db 节点");

            var calibration = new YinXun2ReferenceCalibration
            {
                Device = root.device,
                Status = root.status,
                AxisA = root.axis.a,
                AxisBM = root.axis.b_m,
                DbAlpha = root.db.alpha,
                DbBeta = root.db.beta,
                LevelOffsetDb = root.level_offset_db,
                InternalReferencePeakM = root.internal_reference_peak_m,
                YinXunPitchM = root.yin_pitch_m > 0 ? root.yin_pitch_m : 0.0004258363425773126,
                Lga50PointCount = root.lga50_point_count > 0 ? root.lga50_point_count : 82152,
                Lga50PitchM = root.lga50_pitch_m > 0 ? root.lga50_pitch_m : 0.000185,
                MaskBeforeLga50M = root.mask_before_lga50_m,
                VendorRoiStartM = root.vendor_roi_start_m
            };
            calibration.Validate();
            return calibration;
        }

        sealed class JsonRoot
        {
            public string device { get; set; }
            public string status { get; set; }
            public AxisJson axis { get; set; }
            public DbJson db { get; set; }
            public double internal_reference_peak_m { get; set; }
            public double level_offset_db { get; set; }
            public double yin_pitch_m { get; set; }
            public int lga50_point_count { get; set; }
            public double lga50_pitch_m { get; set; }
            public double mask_before_lga50_m { get; set; }
            public double vendor_roi_start_m { get; set; }
        }

        sealed class AxisJson
        {
            public double a { get; set; }
            public double b_m { get; set; }
        }

        sealed class DbJson
        {
            public double alpha { get; set; }
            public double beta { get; set; }
        }
    }
}
