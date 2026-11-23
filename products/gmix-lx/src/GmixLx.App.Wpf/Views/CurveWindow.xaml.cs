using System;
using System.Globalization;
using System.Windows;
using GmixLx.Core;
using GmixLx.Orchestration;

namespace GmixLx.App.Wpf.Views
{
    /// <summary>
    /// 指标表双击弹出的距离域曲线窗口：轴换算与峰标记口径对齐 WinForms `CurveForm`。
    /// </summary>
    public partial class CurveWindow : Window
    {
        /// <summary>LGA50 原生步距缺省值；行数据未带步距时使用。</summary>
        const double DefaultMetersPerRow = 0.000185;

        public CurveWindow(MetricTableRow row)
        {
            InitializeComponent();
            if (row == null) throw new ArgumentNullException("row");

            double[] levels = row.LevelsDb ?? new double[0];
            double metersPerRow = row.MetersPerRow > 0 ? row.MetersPerRow : DefaultMetersPerRow;
            // 参考系显示轴：映讯_2 从设备连出口起算（x = i×步距 + 偏移），LGA50 即原生轴。
            double axisScale = row.AxisScaleM > 0 ? row.AxisScaleM : metersPerRow;
            double axisOffset = row.AxisOffsetM;

            Title = string.Format(CultureInfo.InvariantCulture,
                "距离域曲线 · IN{0} → OUT{1}（{2} 点 · 参考系 · 步距 {3:F4} mm）",
                row.InChannel, row.OutChannel, levels.Length, axisScale * 1000.0);

            Plot.Values = levels;
            Plot.XScale = axisScale;
            Plot.XOffset = axisOffset;
            Plot.PeakThresholdDb = row.PeakThresholdDb;
            Plot.ReferenceLevelDb = row.PeakThresholdDb;
            if (!double.IsNaN(row.PeakThresholdDb))
            {
                Plot.Peaks = CurveMetrics.FindPeaks(levels, row.PeakThresholdDb,
                    row.PeakMergeGapRows > 0 ? row.PeakMergeGapRows : 50);
            }

            double axisStart = Math.Max(0.0, axisOffset);
            double axisEnd = levels.Length > 1
                ? axisOffset + (levels.Length - 1) * axisScale
                : axisStart + 0.001;
            axisEnd = Math.Max(axisStart + 0.001, axisEnd);

            double viewStart = axisStart;
            double viewEnd = axisEnd;
            if (row.ViewEndM > row.ViewStartM)
            {
                double start = Math.Max(axisStart, row.ViewStartM);
                double end = Math.Min(axisEnd, row.ViewEndM);
                if (end > start)
                {
                    viewStart = start;
                    viewEnd = end;
                }
            }

            Plot.ViewStartM = viewStart;
            Plot.ViewEndM = viewEnd;
        }
    }
}
