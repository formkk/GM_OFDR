using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GmixLx.Core;

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

        static readonly Typeface _typeface = new Typeface(new FontFamily("Consolas"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static bool _prewarmed;
        static Brush _backgroundBrush;
        static Brush _textBrush;
        static Brush _peakBrush;
        static Brush _roiBrush;
        static Pen _gridPen;
        static Pen _axisPen;
        static Pen _curvePen;
        static Pen _cursorAPen;
        static Pen _cursorBPen;
        static Pen _hoverPen;
        static Pen _peakPen;
        static Pen _thresholdPen;

        static readonly Dictionary<string, FormattedText> _textCache = new Dictionary<string, FormattedText>();

        MinMaxPyramid _pyramid;

        bool _panning;
        Point _panFrom;
        double _panStartM;
        double _panEndM;
        int _dragCursor;
        bool _hoverValid;
        double _hoverDistanceM;
        double _hoverLevelDb;

        public CurvePlotControl()
        {
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            MinWidth = 200;
            MinHeight = 120;
            Focusable = true;
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

        public event Action<double, double> CursorsChanged;

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
            if (values == null || values.Length < 2 || scale <= 0 || plot.Width <= 10 || plot.Height <= 10)
            {
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

            var gridPen = _gridPen;
            var axisPen = _axisPen;
            var curvePen = _curvePen;
            var textBrush = _textBrush;
            var cursorAPen = _cursorAPen;
            var cursorBPen = _cursorBPen;
            var hoverPen = _hoverPen;

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

            DrawRoi(dc, plot, viewStart, viewEnd);

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

            DrawPeaks(dc, plot, viewStart, viewEnd, min, max);
            DrawThreshold(dc, plot, min, max);

            DrawCursor(dc, plot, CursorAM, cursorAPen, "A", viewStart, viewEnd);
            DrawCursor(dc, plot, CursorBM, cursorBPen, "B", viewStart, viewEnd);

            if (_hoverValid && _hoverDistanceM >= viewStart && _hoverDistanceM <= viewEnd)
            {
                double hx = DistanceToX(_hoverDistanceM, viewStart, viewEnd, plot);
                dc.DrawLine(hoverPen, new Point(hx, plot.Top), new Point(hx, plot.Bottom));
                string text = string.Format(CultureInfo.InvariantCulture,
                    "x={0:F3} m  y={1:F2} dB", _hoverDistanceM, _hoverLevelDb);
                FormattedText ft = FormatCached(text, 10, textBrush);
                dc.DrawText(ft, new Point(plot.Right - ft.Width - 6, plot.Top + 4));
            }

            dc.DrawRectangle(null, axisPen, plot);
        }

        void DrawRoi(DrawingContext dc, Rect plot, double viewStart, double viewEnd)
        {
            if (double.IsNaN(RoiStartM) || double.IsNaN(RoiEndM) || RoiEndM <= RoiStartM) return;
            double left = Math.Max(viewStart, RoiStartM);
            double right = Math.Min(viewEnd, RoiEndM);
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
            Pen pen = _peakPen;
            for (int i = 0; i < peaks.Count; i++)
            {
                CurvePeak peak = peaks[i];
                if (peak == null) continue;

                double position = PeakPosition(peak);
                if (position < viewStart || position > viewEnd) continue;

                double x = DistanceToX(position, viewStart, viewEnd, plot);
                double y = ToY(peak.Db, min, max, plot);
                if (double.IsNaN(x) || double.IsNaN(y)) continue;

                dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                if (y >= plot.Top - 2 && y <= plot.Bottom + 2)
                {
                    dc.DrawEllipse(brush, null, new Point(x, y), 2.5, 2.5);
                }
                if (ShowPeakLabels && i < 16)
                {
                    DrawPeakLabel(dc, plot, x, y, i + 1, position, peak.Db);
                }
            }
        }

        void DrawThreshold(DrawingContext dc, Rect plot, double min, double max)
        {
            double threshold = PeakThresholdDb;
            if (double.IsNaN(threshold) || threshold < min || threshold > max) return;

            double y = ToY(threshold, min, max, plot);
            Pen pen = _thresholdPen;
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));

            Brush brush = _textBrush;
            FormattedText text = FormatCached("阈值 " + threshold.ToString("F1", CultureInfo.InvariantCulture) + " dB",
                9, brush);
            double tx = plot.Right - text.Width - 6;
            double ty = y - 13;
            if (ty < plot.Top) ty = y + 4;
            dc.DrawText(text, new Point(tx, ty));
        }

        void DrawPeakLabel(DrawingContext dc, Rect plot, double x, double y, int number,
            double distanceM, double levelDb)
        {
            Brush brush = _peakBrush;
            string[] lines =
            {
                "P" + number.ToString(CultureInfo.InvariantCulture),
                distanceM.ToString("F3", CultureInfo.InvariantCulture) + " m",
                levelDb.ToString("F1", CultureInfo.InvariantCulture) + " dB"
            };

            double lineHeight = 12;
            double maxWidth = 0;
            foreach (string line in lines)
            {
                FormattedText measure = FormatCached(line, 9, brush);
                if (measure.Width > maxWidth) maxWidth = measure.Width;
            }

            double textX = x + 6;
            if (textX + maxWidth > plot.Right) textX = x - maxWidth - 6;
            if (textX < plot.Left) textX = plot.Left + 2;

            double textY = y - lineHeight * lines.Length - 6;
            if (textY < plot.Top) textY = y + 6;
            if (textY + lineHeight * lines.Length > plot.Bottom)
            {
                textY = plot.Bottom - lineHeight * lines.Length - 2;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                DrawText(dc, lines[i], new Point(textX, textY + i * lineHeight), 9, brush);
            }
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
            if (!double.IsNaN(CursorAM) || !double.IsNaN(CursorBM))
            {
                double xa = DistanceToX(CursorAM, viewStart, viewEnd, plot);
                double xb = DistanceToX(CursorBM, viewStart, viewEnd, plot);
                Cursor = Math.Abs(position.X - xa) <= CursorHitPx || Math.Abs(position.X - xb) <= CursorHitPx
                    ? Cursors.SizeWE
                    : Cursors.Arrow;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _panning = false;
            _dragCursor = 0;
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
            _peakBrush = FindBrush("PeakBrush", Color.FromRgb(255, 138, 101));
            _roiBrush = FindBrush("RoiBackgroundBrush", Color.FromArgb(0x2E, 0x5A, 0xA9, 0xFF));

            _gridPen = FindPen("GridLineBrush", Color.FromRgb(47, 50, 54), 1.0);
            _axisPen = FindPen("AxisBrush", Color.FromRgb(110, 115, 120), 1.0);
            _curvePen = FindPen("CurveBrush", Color.FromRgb(90, 169, 255), 1.0);
            _cursorAPen = DashPen("CursorABrush", Color.FromRgb(240, 160, 48), DashStyles.Dash);
            _cursorBPen = DashPen("CursorBBrush", Color.FromRgb(53, 193, 160), DashStyles.Dash);
            _hoverPen = DashPen("HoverBrush", Color.FromRgb(140, 146, 156), DashStyles.Dot);
            _peakPen = DashPen("PeakBrush", Color.FromRgb(255, 138, 101), DashStyles.Dot);
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
