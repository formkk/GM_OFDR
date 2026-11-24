using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GmixLx.Core;
using Microsoft.Win32;

namespace GmixLx.App.Wpf.Visualization
{
    /// <summary>
    /// 距离域曲线控件：16 万点按像素列做 min/max 聚合，支持缩放、平移、A/B 游标与悬停读数。
    /// 数据只来自内存；不读文件、不落盘。
    /// </summary>
    public sealed class CurvePlotControl : FrameworkElement
    {
        const int PlotLeft = 62;
        const int PlotRight = 14;
        const int PlotTop = 10;
        const int PlotBottom = 30;
        const double CursorHitPx = 6.0;

        public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
            "Values", typeof(double[]), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

        public static readonly DependencyProperty XScaleProperty = DependencyProperty.Register(
            "XScale", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty XOffsetProperty = DependencyProperty.Register(
            "XOffset", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ViewStartMProperty = DependencyProperty.Register(
            "ViewStartM", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ViewEndMProperty = DependencyProperty.Register(
            "ViewEndM", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>复位视图时使用的横轴宽度（米）；0 表示按全量程。</summary>
        public static readonly DependencyProperty ResetWidthMProperty = DependencyProperty.Register(
            "ResetWidthM", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(0.0));

        public static readonly DependencyProperty CursorAMProperty = DependencyProperty.Register(
            "CursorAM", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CursorBMProperty = DependencyProperty.Register(
            "CursorBM", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
            "Peaks", typeof(IReadOnlyList<CurvePeak>), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PeakThresholdDbProperty = DependencyProperty.Register(
            "PeakThresholdDb", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RoiStartMProperty = DependencyProperty.Register(
            "RoiStartM", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RoiEndMProperty = DependencyProperty.Register(
            "RoiEndM", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ShowPeakLabelsProperty = DependencyProperty.Register(
            "ShowPeakLabels", typeof(bool), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>可拖动的水平参考线电平（dB）；NaN 表示不显示。</summary>
        public static readonly DependencyProperty ReferenceLevelDbProperty = DependencyProperty.Register(
            "ReferenceLevelDb", typeof(double), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>是否显示峰标记与峰顶坐标。</summary>
        public static readonly DependencyProperty ShowPeaksProperty = DependencyProperty.Register(
            "ShowPeaks", typeof(bool), typeof(CurvePlotControl),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        static readonly Typeface _typeface = new Typeface(new FontFamily("Consolas"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static bool _prewarmed;
        static Brush _backgroundBrush;
        static Brush _textBrush;
        static Brush _primaryTextBrush;
        static Brush _peakBrush;
        static Brush _peakLabelBrush;
        static Brush _referenceLabelBrush;
        static Brush _roiBrush;
        static Pen _gridPen;
        static Pen _axisPen;
        static Pen _curvePen;
        static Pen _cursorAPen;
        static Pen _cursorBPen;
        static Pen _hoverPen;
        static Pen _referencePen;
        static Pen _referenceLabelPen;
        static Pen _thresholdPen;

        static readonly Dictionary<string, FormattedText> _textCache = new Dictionary<string, FormattedText>();

        MinMaxPyramid _pyramid;

        bool _panning;
        Point _panFrom;
        double _panStartM;
        double _panEndM;
        int _dragCursor;
        bool _hoverValid;
        Point _hoverPoint;
        double _hoverDistanceM;
        double _hoverLevelDb;

        bool _draggingReference;
        Rect _referenceLabelRect = Rect.Empty;
        double _levelMin;
        double _levelMax;

        public CurvePlotControl()
        {
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            MinWidth = 200;
            MinHeight = 120;
            Focusable = true;
            ContextMenu = BuildContextMenu();
        }

        public double[] Values
        {
            get { return (double[])GetValue(ValuesProperty); }
            set { SetValue(ValuesProperty, value); }
        }

        public double XScale
        {
            get { return (double)GetValue(XScaleProperty); }
            set { SetValue(XScaleProperty, value); }
        }

        public double XOffset
        {
            get { return (double)GetValue(XOffsetProperty); }
            set { SetValue(XOffsetProperty, value); }
        }

        public double ViewStartM
        {
            get { return (double)GetValue(ViewStartMProperty); }
            set { SetValue(ViewStartMProperty, value); }
        }

        public double ViewEndM
        {
            get { return (double)GetValue(ViewEndMProperty); }
            set { SetValue(ViewEndMProperty, value); }
        }

        public double ResetWidthM
        {
            get { return (double)GetValue(ResetWidthMProperty); }
            set { SetValue(ResetWidthMProperty, value); }
        }

        public double CursorAM
        {
            get { return (double)GetValue(CursorAMProperty); }
            set { SetValue(CursorAMProperty, value); }
        }

        public double CursorBM
        {
            get { return (double)GetValue(CursorBMProperty); }
            set { SetValue(CursorBMProperty, value); }
        }

        public IReadOnlyList<CurvePeak> Peaks
        {
            get { return (IReadOnlyList<CurvePeak>)GetValue(PeaksProperty); }
            set { SetValue(PeaksProperty, value); }
        }

        public double PeakThresholdDb
        {
            get { return (double)GetValue(PeakThresholdDbProperty); }
            set { SetValue(PeakThresholdDbProperty, value); }
        }

        public double RoiStartM
        {
            get { return (double)GetValue(RoiStartMProperty); }
            set { SetValue(RoiStartMProperty, value); }
        }

        public double RoiEndM
        {
            get { return (double)GetValue(RoiEndMProperty); }
            set { SetValue(RoiEndMProperty, value); }
        }

        public bool ShowPeakLabels
        {
            get { return (bool)GetValue(ShowPeakLabelsProperty); }
            set { SetValue(ShowPeakLabelsProperty, value); }
        }

        public double ReferenceLevelDb
        {
            get { return (double)GetValue(ReferenceLevelDbProperty); }
            set { SetValue(ReferenceLevelDbProperty, value); }
        }

        public bool ShowPeaks
        {
            get { return (bool)GetValue(ShowPeaksProperty); }
            set { SetValue(ShowPeaksProperty, value); }
        }

        public event Action<double, double> CursorsChanged;

        /// <summary>复位缩放：默认回到 [0, ResetWidthM]；未设定宽度时按全量程显示。</summary>
        public void ResetView()
        {
            if (Values == null || Values.Length < 2) return;
            SetCurrentValue(ViewStartMProperty, 0.0);
            SetCurrentValue(ViewEndMProperty, ResetWidthM > 0 ? ResetWidthM : 0.0);
        }

        /// <summary>
        /// 把当前曲线区域按屏幕 DPI 渲染为 PNG；返回 false 表示控件尚未布局。
        /// </summary>
        public bool SaveImage(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            int width = (int)Math.Round(ActualWidth);
            int height = (int)Math.Round(ActualHeight);
            if (width <= 0 || height <= 0) return false;

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap(
                (int)Math.Round(ActualWidth * dpi.DpiScaleX),
                (int)Math.Round(ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
            return true;
        }

        ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();
            Style menuStyle = TryFindResource("DarkContextMenuStyle") as Style;
            if (menuStyle != null) menu.Style = menuStyle;

            var export = new MenuItem { Header = "导出图片…" };
            ApplyMenuStyle(export);
            export.Click += delegate { ExportImageWithDialog(); };

            var reset = new MenuItem { Header = "复位视图" };
            ApplyMenuStyle(reset);
            reset.Click += delegate { ResetView(); };

            var peaks = new MenuItem { Header = "显示峰", IsCheckable = true, IsChecked = ShowPeaks };
            ApplyMenuStyle(peaks);
            peaks.Click += delegate { SetCurrentValue(ShowPeaksProperty, peaks.IsChecked); };

            menu.Items.Add(export);
            menu.Items.Add(reset);
            menu.Items.Add(peaks);
            return menu;
        }

        void ApplyMenuStyle(MenuItem item)
        {
            Style style = TryFindResource("DarkMenuItemStyle") as Style;
            if (style != null) item.Style = style;
        }

        void ExportImageWithDialog()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "PNG 图片|*.png",
                FileName = "curve_"
                    + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                SaveImage(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "导出图片");
            }
        }

        static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (CurvePlotControl)d;
            double[] values = e.NewValue as double[];
            control._pyramid = values == null ? null : new MinMaxPyramid(values);
            control.InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            EnsureResources();

            dc.DrawRectangle(_backgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

            double[] values = Values;
            double scale = XScale;
            Rect plot = PlotRect;
            if (plot.Width <= 10 || plot.Height <= 10)
            {
                return;
            }

            if (values == null || values.Length < 2 || scale <= 0)
            {
                DrawEmptyAxes(dc, plot);
                return;
            }

            double totalStart;
            double totalEnd;
            double viewStart;
            double viewEnd;
            GetView(out totalStart, out totalEnd, out viewStart, out viewEnd);
            if (viewEnd <= viewStart) return;

            int i0 = (int)Math.Floor((viewStart - XOffset) / scale);
            int i1 = (int)Math.Ceiling((viewEnd - XOffset) / scale);
            i0 = Math.Max(0, Math.Min(values.Length - 1, i0));
            i1 = Math.Max(0, Math.Min(values.Length - 1, i1));
            if (i1 <= i0) return;

            MinMaxPyramid pyramid = _pyramid ?? new MinMaxPyramid(values);
            double min;
            double max;
            pyramid.GetRange(i0, i1, out min, out max);
            if (min > max) return;

            double pad = Math.Max(2.0, (max - min) * 0.08);
            min -= pad;
            max += pad;
            if (max - min < 1) max = min + 1;
            _levelMin = min;
            _levelMax = max;

            var gridPen = _gridPen;
            var axisPen = _axisPen;
            var curvePen = _curvePen;
            var textBrush = _textBrush;
            var cursorAPen = _cursorAPen;
            var cursorBPen = _cursorBPen;

            for (int k = 0; k <= 5; k++)
            {
                double y = plot.Top + plot.Height * k / 5.0;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                double value = max - (max - min) * k / 5.0;
                DrawText(dc, value.ToString("F0", CultureInfo.InvariantCulture) + " dB",
                    new Point(6, y - 7), 10, textBrush);
            }

            for (int k = 0; k <= 6; k++)
            {
                double x = plot.Left + plot.Width * k / 6.0;
                dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                double distance = viewStart + (viewEnd - viewStart) * k / 6.0;
                FormattedText label = FormatCached(distance.ToString("F2", CultureInfo.InvariantCulture) + " m",
                    10, textBrush);
                dc.DrawText(label, new Point(x - label.Width / 2, plot.Bottom + 6));
            }

            // 阴影跟随 A/B：游标有效时以游标为准，保证拖动过程中底纹同步。
            double roiStart = RoiStartM;
            double roiEnd = RoiEndM;
            if (!double.IsNaN(CursorAM) && !double.IsNaN(CursorBM) && CursorBM > CursorAM)
            {
                roiStart = CursorAM;
                roiEnd = CursorBM;
            }
            DrawRoi(dc, plot, viewStart, viewEnd, roiStart, roiEnd);

            int width = (int)Math.Floor(plot.Width);
            double previousY = double.NaN;
            for (int x = 0; x < width; x++)
            {
                int from = i0 + (int)((double)(i1 - i0 + 1) * x / width);
                int to = i0 + (int)((double)(i1 - i0 + 1) * (x + 1) / width) - 1;
                if (to < from) to = from;
                if (from > i1) break;

                double lo;
                double hi;
                pyramid.GetRange(from, Math.Min(to, i1), out lo, out hi);
                if (hi < lo) continue;

                double yHi = ToY(hi, min, max, plot);
                double yLo = ToY(lo, min, max, plot);
                if (!double.IsNaN(previousY))
                {
                    dc.DrawLine(curvePen, new Point(plot.Left + x - 1, previousY), new Point(plot.Left + x, yHi));
                }
                if (yLo > yHi)
                {
                    dc.DrawLine(curvePen, new Point(plot.Left + x, yHi), new Point(plot.Left + x, yLo));
                }
                previousY = yLo;
            }

            if (ShowPeaks)
            {
                DrawPeaks(dc, plot, viewStart, viewEnd, min, max);
            }
            DrawThreshold(dc, plot, min, max);
            DrawReference(dc, plot, min, max);

            DrawCursor(dc, plot, CursorAM, cursorAPen, "A", viewStart, viewEnd);
            DrawCursor(dc, plot, CursorBM, cursorBPen, "B", viewStart, viewEnd);

            DrawHover(dc, plot, viewStart, viewEnd, min, max);

            dc.DrawRectangle(null, axisPen, plot);
        }

        /// <summary>尚无数据时按当前视窗画出横轴刻度与边框，让"显示上限"宽度在启动时就可见。</summary>
        void DrawEmptyAxes(DrawingContext dc, Rect plot)
        {
            double start = ViewStartM;
            double end = ViewEndM;
            if (end <= start) return;

            for (int k = 0; k <= 6; k++)
            {
                double x = plot.Left + plot.Width * k / 6.0;
                dc.DrawLine(_gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                double distance = start + (end - start) * k / 6.0;
                FormattedText label = FormatCached(
                    distance.ToString("F2", CultureInfo.InvariantCulture) + " m", 10, _textBrush);
                dc.DrawText(label, new Point(x - label.Width / 2, plot.Bottom + 6));
            }

            dc.DrawRectangle(null, _axisPen, plot);
        }

        /// <summary>悬停十字线 + 跟随指针的坐标提示框。</summary>
        void DrawHover(DrawingContext dc, Rect plot, double viewStart, double viewEnd, double min, double max)
        {
            if (!_hoverValid) return;
            if (_hoverDistanceM < viewStart || _hoverDistanceM > viewEnd) return;

            double hx = DistanceToX(_hoverDistanceM, viewStart, viewEnd, plot);
            if (double.IsNaN(hx)) return;
            Pen pen = _hoverPen;
            dc.DrawLine(pen, new Point(hx, plot.Top), new Point(hx, plot.Bottom));
            if (!double.IsNaN(_hoverLevelDb))
            {
                double hy = ToY(_hoverLevelDb, min, max, plot);
                if (hy >= plot.Top && hy <= plot.Bottom)
                {
                    dc.DrawLine(pen, new Point(plot.Left, hy), new Point(plot.Right, hy));
                }
            }

            string text = string.Format(CultureInfo.InvariantCulture,
                "x={0:F3} m  y={1:F2} dB", _hoverDistanceM, _hoverLevelDb);
            FormattedText ft = FormatCached(text, 10, _primaryTextBrush);

            double boxW = ft.Width + 10;
            double boxH = ft.Height + 6;
            double boxX = _hoverPoint.X + 14;
            if (boxX + boxW > plot.Right) boxX = _hoverPoint.X - 14 - boxW;
            if (boxX < plot.Left) boxX = plot.Left + 2;
            double boxY = _hoverPoint.Y + 14;
            if (boxY + boxH > plot.Bottom) boxY = _hoverPoint.Y - 14 - boxH;
            if (boxY < plot.Top) boxY = plot.Top + 2;

            dc.DrawRectangle(_backgroundBrush, _axisPen, new Rect(boxX, boxY, boxW, boxH));
            dc.DrawText(ft, new Point(boxX + 5, boxY + 3));
        }

        /// <summary>水平参考线：虚线 + 右侧可拖动标签；标签命中后才允许拖动，避免与平移冲突。</summary>
        void DrawReference(DrawingContext dc, Rect plot, double min, double max)
        {
            double level = ReferenceLevelDb;
            if (double.IsNaN(level) || level < min || level > max)
            {
                _referenceLabelRect = Rect.Empty;
                return;
            }

            double y = ToY(level, min, max, plot);
            dc.DrawLine(_referencePen, new Point(plot.Left, y), new Point(plot.Right, y));

            // 参考线标签用土黄色，与阈值灰、峰标签灰白区分。
            FormattedText text = FormatCached("参考 " + level.ToString("F1", CultureInfo.InvariantCulture) + " dB",
                9, _referenceLabelBrush);
            double boxW = text.Width + 10;
            double boxH = text.Height + 4;
            double boxX = plot.Right - boxW - 4;
            double boxY = y - boxH - 2;
            if (boxY < plot.Top) boxY = y + 2;

            _referenceLabelRect = new Rect(boxX, boxY, boxW, boxH);
            dc.DrawRectangle(_backgroundBrush, _referenceLabelPen, _referenceLabelRect);
            dc.DrawText(text, new Point(boxX + 5, boxY + 2));

            // 上下小箭头，提示标签可上下拖动
            Pen arrowPen = _referenceLabelPen;
            double cx = boxX + boxW / 2;
            dc.DrawLine(arrowPen, new Point(cx - 5, boxY - 1), new Point(cx, boxY - 7));
            dc.DrawLine(arrowPen, new Point(cx + 5, boxY - 1), new Point(cx, boxY - 7));
            double bottomY = boxY + boxH;
            dc.DrawLine(arrowPen, new Point(cx - 5, bottomY + 1), new Point(cx, bottomY + 7));
            dc.DrawLine(arrowPen, new Point(cx + 5, bottomY + 1), new Point(cx, bottomY + 7));
        }

        /// <summary>参考标签命中区：比标签框左右各放宽 4px、上下各放宽 8px，覆盖拖动箭头。</summary>
        bool HitReferenceLabel(Point point)
        {
            if (_referenceLabelRect.IsEmpty) return false;
            Rect hit = _referenceLabelRect;
            hit.Inflate(4, 8);
            return hit.Contains(point);
        }

        void DrawRoi(DrawingContext dc, Rect plot, double viewStart, double viewEnd,
            double roiStartM, double roiEndM)
        {
            if (double.IsNaN(roiStartM) || double.IsNaN(roiEndM) || roiEndM <= roiStartM) return;
            double left = Math.Max(viewStart, roiStartM);
            double right = Math.Min(viewEnd, roiEndM);
            if (right <= left) return;

            double x0 = DistanceToX(left, viewStart, viewEnd, plot);
            double x1 = DistanceToX(right, viewStart, viewEnd, plot);
            if (double.IsNaN(x0) || double.IsNaN(x1) || x1 <= x0) return;

            Brush brush = _roiBrush;
            dc.PushOpacity(0.7);
            dc.DrawRectangle(brush, null, new Rect(x0, plot.Top, x1 - x0, plot.Height));
            dc.Pop();
        }

        void DrawPeaks(DrawingContext dc, Rect plot, double viewStart, double viewEnd, double min, double max)
        {
            IReadOnlyList<CurvePeak> peaks = Peaks;
            if (peaks == null || peaks.Count == 0) return;

            Brush brush = _peakBrush;
            for (int i = 0; i < peaks.Count; i++)
            {
                CurvePeak peak = peaks[i];
                if (peak == null) continue;

                double position = PeakPosition(peak);
                if (position < viewStart || position > viewEnd) continue;

                double x = DistanceToX(position, viewStart, viewEnd, plot);
                double y = ToY(peak.Db, min, max, plot);
                if (double.IsNaN(x) || double.IsNaN(y)) continue;

                if (y >= plot.Top - 2 && y <= plot.Bottom + 2)
                {
                    dc.DrawEllipse(brush, null, new Point(x, y), 2.5, 2.5);
                }
                if (ShowPeakLabels && i < 16)
                {
                    DrawPeakLabel(dc, plot, x, y, i + 1, peak.Db);
                }
            }
        }

        void DrawThreshold(DrawingContext dc, Rect plot, double min, double max)
        {
            double threshold = PeakThresholdDb;
            if (double.IsNaN(threshold) || threshold < min || threshold > max) return;

            double y = ToY(threshold, min, max, plot);
            dc.DrawLine(_thresholdPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        /// <summary>峰顶标注：只显示峰号与电平（dB），不显示距离。</summary>
        void DrawPeakLabel(DrawingContext dc, Rect plot, double x, double y, int number, double levelDb)
        {
            string text = "P" + number.ToString(CultureInfo.InvariantCulture)
                + "  " + levelDb.ToString("F1", CultureInfo.InvariantCulture) + " dB";
            FormattedText ft = FormatCached(text, 9, _peakLabelBrush);

            double textX = x + 6;
            if (textX + ft.Width > plot.Right) textX = x - ft.Width - 6;
            if (textX < plot.Left) textX = plot.Left + 2;

            double textY = y - ft.Height - 4;
            if (textY < plot.Top) textY = y + 4;
            if (textY + ft.Height > plot.Bottom) textY = plot.Bottom - ft.Height - 2;

            dc.DrawText(ft, new Point(textX, textY));
        }

        double PeakPosition(CurvePeak peak)
        {
            double index = peak.Centroid > 0 ? peak.Centroid : peak.Index;
            return XOffset + index * XScale;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (Values == null || Values.Length < 2) return;

            double totalStart;
            double totalEnd;
            double viewStart;
            double viewEnd;
            GetView(out totalStart, out totalEnd, out viewStart, out viewEnd);
            double span = viewEnd - viewStart;
            if (span <= 0) return;

            Rect plot = PlotRect;
            if (plot.Width <= 0) return;
            double ratio = (e.GetPosition(this).X - plot.Left) / plot.Width;
            ratio = Math.Max(0.0, Math.Min(1.0, ratio));

            double factor = e.Delta > 0 ? 0.8 : 1.25;
            double totalSpan = totalEnd - totalStart;
            double newSpan = Math.Min(totalSpan, Math.Max(totalSpan / 100.0, span * factor));
            double center = viewStart + span * ratio;
            double start = center - newSpan * ratio;
            start = Math.Max(totalStart, Math.Min(totalEnd - newSpan, start));

            SetCurrentValue(ViewStartMProperty, start);
            SetCurrentValue(ViewEndMProperty, start + newSpan);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (Values == null) return;

            double totalStart;
            double totalEnd;
            double viewStart;
            double viewEnd;
            GetView(out totalStart, out totalEnd, out viewStart, out viewEnd);
            Rect plot = PlotRect;
            Point position = e.GetPosition(this);

            if (HitReferenceLabel(position))
            {
                _draggingReference = true;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            double xa = DistanceToX(CursorAM, viewStart, viewEnd, plot);
            double xb = DistanceToX(CursorBM, viewStart, viewEnd, plot);
            if (!double.IsNaN(CursorAM) && Math.Abs(position.X - xa) <= CursorHitPx)
            {
                _dragCursor = 1;
            }
            else if (!double.IsNaN(CursorBM) && Math.Abs(position.X - xb) <= CursorHitPx)
            {
                _dragCursor = 2;
            }
            else
            {
                _dragCursor = 0;
                _panning = true;
                _panFrom = position;
                _panStartM = viewStart;
                _panEndM = viewEnd;
            }

            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Values == null || Values.Length < 2) return;

            double totalStart;
            double totalEnd;
            double viewStart;
            double viewEnd;
            GetView(out totalStart, out totalEnd, out viewStart, out viewEnd);
            Rect plot = PlotRect;
            Point position = e.GetPosition(this);

            if (_draggingReference && e.LeftButton == MouseButtonState.Pressed)
            {
                double level = YToLevel(position.Y, _levelMin, _levelMax, plot);
                if (!double.IsNaN(level))
                {
                    level = Math.Max(_levelMin, Math.Min(_levelMax, level));
                    SetCurrentValue(ReferenceLevelDbProperty, level);
                }
                e.Handled = true;
                return;
            }

            if (_dragCursor != 0 && e.LeftButton == MouseButtonState.Pressed)
            {
                double distance = XToDistance(position.X, viewStart, viewEnd, plot);
                distance = Math.Max(viewStart, Math.Min(viewEnd, distance));
                if (_dragCursor == 1) SetCurrentValue(CursorAMProperty, distance);
                else SetCurrentValue(CursorBMProperty, distance);
                CursorsChanged?.Invoke(CursorAM, CursorBM);
                e.Handled = true;
                return;
            }

            if (_panning && e.LeftButton == MouseButtonState.Pressed)
            {
                double span = _panEndM - _panStartM;
                if (plot.Width > 0)
                {
                    double shift = -(position.X - _panFrom.X) / plot.Width * span;
                    double start = _panStartM + shift;
                    start = Math.Max(totalStart, Math.Min(totalEnd - span, start));
                    SetCurrentValue(ViewStartMProperty, start);
                    SetCurrentValue(ViewEndMProperty, start + span);
                }
                e.Handled = true;
                return;
            }

            UpdateHover(position, plot, viewStart, viewEnd);
            if (HitReferenceLabel(position))
            {
                Cursor = Cursors.SizeNS;
            }
            else if (!double.IsNaN(CursorAM) || !double.IsNaN(CursorBM))
            {
                double xa = DistanceToX(CursorAM, viewStart, viewEnd, plot);
                double xb = DistanceToX(CursorBM, viewStart, viewEnd, plot);
                Cursor = Math.Abs(position.X - xa) <= CursorHitPx || Math.Abs(position.X - xb) <= CursorHitPx
                    ? Cursors.SizeWE
                    : Cursors.Arrow;
            }
            else
            {
                Cursor = Cursors.Arrow;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _panning = false;
            _dragCursor = 0;
            _draggingReference = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverValid = false;
            InvalidateVisual();
        }

        void UpdateHover(Point position, Rect plot, double viewStart, double viewEnd)
        {
            if (position.X < plot.Left || position.X > plot.Right
                || position.Y < plot.Top || position.Y > plot.Bottom)
            {
                if (_hoverValid)
                {
                    _hoverValid = false;
                    InvalidateVisual();
                }
                return;
            }

            double distance = XToDistance(position.X, viewStart, viewEnd, plot);
            double scale = XScale;
            if (scale <= 0) return;
            int index = (int)Math.Round((distance - XOffset) / scale);
            double[] values = Values;
            if (index < 0 || index >= values.Length) return;
            double level = values[index];
            if (double.IsNaN(level) || double.IsInfinity(level)) return;

            _hoverDistanceM = distance;
            _hoverLevelDb = level;
            _hoverPoint = position;
            _hoverValid = true;
            InvalidateVisual();
        }

        void DrawCursor(DrawingContext dc, Rect plot, double distanceM, Pen pen, string name,
            double viewStart, double viewEnd)
        {
            if (double.IsNaN(distanceM) || distanceM < viewStart || distanceM > viewEnd) return;
            double x = DistanceToX(distanceM, viewStart, viewEnd, plot);
            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var brush = pen.Brush;
            FormattedText label = Format(name + " " + distanceM.ToString("F2", CultureInfo.InvariantCulture) + " m",
                9, brush);
            dc.DrawText(label, new Point(x + 2, plot.Top + 2));
        }

        void GetView(out double totalStart, out double totalEnd, out double viewStart, out double viewEnd)
        {
            double[] values = Values;
            double scale = XScale;
            totalStart = XOffset;
            totalEnd = XOffset + Math.Max(0, (values == null ? 0 : values.Length - 1)) * scale;
            double start = ViewStartM;
            double end = ViewEndM;
            viewStart = start > 0 ? Math.Max(totalStart, start) : totalStart;
            viewEnd = end > start ? Math.Min(totalEnd, end) : totalEnd;
            if (viewEnd <= viewStart)
            {
                viewStart = totalStart;
                viewEnd = totalEnd;
            }
        }

        Rect PlotRect
        {
            get
            {
                double width = Math.Max(0, ActualWidth - PlotLeft - PlotRight);
                double height = Math.Max(0, ActualHeight - PlotTop - PlotBottom);
                return new Rect(PlotLeft, PlotTop, width, height);
            }
        }

        static double ToY(double value, double min, double max, Rect plot)
        {
            return plot.Top + (max - value) / (max - min) * plot.Height;
        }

        static double YToLevel(double y, double min, double max, Rect plot)
        {
            if (plot.Height <= 0) return double.NaN;
            double ratio = (y - plot.Top) / plot.Height;
            ratio = Math.Max(0.0, Math.Min(1.0, ratio));
            return max - ratio * (max - min);
        }

        static double DistanceToX(double distance, double viewStart, double viewEnd, Rect plot)
        {
            if (double.IsNaN(distance) || viewEnd <= viewStart) return double.NaN;
            return plot.Left + (distance - viewStart) / (viewEnd - viewStart) * plot.Width;
        }

        static double XToDistance(double x, double viewStart, double viewEnd, Rect plot)
        {
            if (plot.Width <= 0) return viewStart;
            double ratio = (x - plot.Left) / plot.Width;
            ratio = Math.Max(0.0, Math.Min(1.0, ratio));
            return viewStart + (viewEnd - viewStart) * ratio;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _textCache.Clear();
        }

        /// <summary>在 UI 线程预热一次绘制路径，消除首帧 JIT/字体/资源解析开销。</summary>
        public static void Prewarm()
        {
            if (_prewarmed) return;
            _prewarmed = true;

            const int count = 160000;
            const double pitch = 0.0004258363425773126;
            var values = new double[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = 90.0 + 2.0 * Math.Sin(i * 0.00002);
            }

            double[] positions = { 0.0505, 1.1692, 3.3377, 6.5016, 8.6865 };
            double[] levels = { 113.5, 122.4, 126.0, 124.8, 128.0 };
            var peaks = new List<CurvePeak>();
            for (int p = 0; p < positions.Length; p++)
            {
                int center = (int)Math.Round(positions[p] / pitch);
                int half = 7;
                int from = Math.Max(0, center - half);
                int to = Math.Min(count - 1, center + half);
                for (int i = from; i <= to; i++)
                {
                    double d = (i - center) / (double)half;
                    values[i] += (levels[p] - 90.0) * Math.Exp(-2.2 * d * d);
                }
                peaks.Add(new CurvePeak { Index = center, Centroid = center, Db = levels[p], Width = half });
            }

            var warm = new CurvePlotControl
            {
                Width = 1360,
                Height = 700,
                Values = values,
                XScale = pitch,
                XOffset = 0.0,
                Peaks = peaks,
                PeakThresholdDb = 112.0,
                ReferenceLevelDb = 112.0,
                RoiStartM = 5.37,
                RoiEndM = 11.31,
                CursorAM = 5.37,
                CursorBM = 11.31
            };
            warm.Measure(new Size(1360, 700));
            warm.Arrange(new Rect(0, 0, 1360, 700));
            var bitmap = new RenderTargetBitmap(1360, 700, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(warm);
        }

        void EnsureResources()
        {
            if (_curvePen != null) return;

            _backgroundBrush = FindBrush("CardBackgroundBrush", Color.FromRgb(35, 37, 40));
            _textBrush = FindBrush("TextMutedBrush", Color.FromRgb(154, 160, 166));
            _primaryTextBrush = FindBrush("TextPrimaryBrush", Color.FromRgb(230, 232, 235));
            _peakBrush = FindBrush("PeakBrush", Color.FromRgb(255, 138, 101));
            _peakLabelBrush = FindBrush("PeakLabelBrush", Color.FromRgb(230, 232, 234));
            _referenceLabelBrush = FindBrush("ReferenceHighlightBrush", Color.FromRgb(219, 186, 107));
            _roiBrush = FindBrush("RoiBackgroundBrush", Color.FromArgb(0x1A, 0x5A, 0xA9, 0xFF));

            _gridPen = FindPen("GridLineBrush", Color.FromRgb(47, 50, 54), 1.0);
            _axisPen = FindPen("AxisBrush", Color.FromRgb(110, 115, 120), 1.0);
            _curvePen = FindPen("CurveBrush", Color.FromRgb(69, 192, 106), 1.0);
            _cursorAPen = DashPen("CursorABrush", Color.FromRgb(240, 160, 48), DashStyles.Dash);
            _cursorBPen = DashPen("CursorBBrush", Color.FromRgb(53, 193, 160), DashStyles.Dash);
            _hoverPen = DashPen("HoverBrush", Color.FromRgb(140, 146, 156), DashStyles.Dot);
            _referencePen = DashPen("ReferenceBrush", Color.FromRgb(201, 168, 92), DashStyles.Dash);
            _referenceLabelPen = FindPen("ReferenceHighlightBrush", Color.FromRgb(219, 186, 107), 1.0);
            _thresholdPen = DashPen("ThresholdBrush", Color.FromRgb(90, 96, 104), DashStyles.Dash);
        }

        FormattedText FormatCached(string text, double size, Brush brush)
        {
            string key = size.ToString(CultureInfo.InvariantCulture) + ":"
                + brush.GetHashCode().ToString(CultureInfo.InvariantCulture) + ":" + text;
            FormattedText cached;
            if (_textCache.TryGetValue(key, out cached)) return cached;
            if (_textCache.Count > 512) _textCache.Clear();
            cached = Format(text, size, brush);
            _textCache[key] = cached;
            return cached;
        }

        FormattedText Format(string text, double size, Brush brush)
        {
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        void DrawText(DrawingContext dc, string text, Point origin, double size, Brush brush)
        {
            dc.DrawText(FormatCached(text, size, brush), origin);
        }

        Brush FindBrush(string key, Color fallback)
        {
            Brush brush = TryFindResource(key) as Brush;
            if (brush != null) return brush;
            var solid = new SolidColorBrush(fallback);
            solid.Freeze();
            return solid;
        }

        Pen FindPen(string key, Color fallback, double thickness)
        {
            return new Pen(FindBrush(key, fallback), thickness);
        }

        Pen DashPen(string key, Color fallback, DashStyle dash)
        {
            return new Pen(FindBrush(key, fallback), 1.0) { DashStyle = dash };
        }
    }
}
