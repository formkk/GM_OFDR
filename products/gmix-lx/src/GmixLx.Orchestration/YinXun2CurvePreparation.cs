using System;
using GmixLx.Core;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 映讯_2 原生曲线的计算前处理：把设备内部段（标定 mask 边界之前）置为后段中位电平，
    /// 避免内部反射被当成第 9 个峰。只改传入数组（调用方自有的副本），不做重采样。
    /// </summary>
    public static class YinXun2CurvePreparation
    {
        public static void MaskInternalSection(double[] levelsDb, double[] distanceM,
            YinXun2ReferenceCalibration calibration)
        {
            if (levelsDb == null) throw new ArgumentNullException("levelsDb");
            if (distanceM == null) throw new ArgumentNullException("distanceM");
            if (calibration == null) throw new ArgumentNullException("calibration");
            if (levelsDb.Length != distanceM.Length)
                throw new ArgumentException("电平与距离数组长度不一致");

            // 标定里的 mask 边界定义在 LGA50 参考系（0.2 m），换算成本设备的原生距离
            double maskBeforeM = calibration.ToYinXunDistance(calibration.MaskBeforeLga50M);

            int maskCount = 0;
            while (maskCount < distanceM.Length && distanceM[maskCount] < maskBeforeM) maskCount++;
            if (maskCount <= 0 || maskCount >= levelsDb.Length) return;

            double median = Median(levelsDb, maskCount);
            for (int i = 0; i < maskCount; i++) levelsDb[i] = median;
        }

        /// <summary>叠加厂商 GUI 刻度偏移（dB）；0 表示不变。</summary>
        public static void ApplyLevelOffset(double[] levelsDb, double offsetDb)
        {
            if (levelsDb == null) throw new ArgumentNullException("levelsDb");
            if (offsetDb == 0.0) return;
            for (int i = 0; i < levelsDb.Length; i++) levelsDb[i] += offsetDb;
        }

        static double Median(double[] values, int start)
        {
            int length = values.Length - start;
            var copy = new double[length];
            Array.Copy(values, start, copy, 0, length);
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }
    }
}
