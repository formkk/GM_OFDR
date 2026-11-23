using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using GmixLx.Core;
using GmixLx.Orchestration;

namespace GmixLx.App
{
    /// <summary>
    /// 单帧距离域曲线绘制：ROI 底纹 + 逐像素 min/max 曲线 + 峰标记。
    /// 支持滚轮缩放、拖动平移、拖动 A/B 游标；只画内存数据，不读文件。
    /// </summary>
    internal sealed class CurvePlotPanel : Control
    {
        static readonly Color CardBack = Color.White;
        static readonly Color GridColor = Color.FromArgb(232, 236, 242);
        static readonly Color AxisColor = Color.FromArgb(150, 158, 170);
        static readonly Color CurveColor = Color.FromArgb(24, 90, 188);
        static readonly Color PeakColor = Color.FromArgb(206, 84, 60);
        static readonly Color RoiBack = Color.FromArgb(234, 242, 254);
        static readonly Color CursorAColor = Color.FromArgb(214, 122, 34);
        static readonly Color CursorBColor = Color.FromArgb(28, 132, 108);
        static readonly Color CursorBand = Color.FromArgb(22, 24, 90, 188);
        static readonly Color TextMuted = Color.FromArgb(106, 114, 125);

        const int PlotLeft = 62;
        const int PlotRight = 14;
        const int PlotTop = 10;
        const int PlotBottom = 30;
        const int CursorHitPx = 6;

        readonly Font _axisFont = new Font("Consolas", 7.5F);
        readonly Font _labelFont = new Font("Microsoft YaHei", 7.5F);
        readonly Font _emptyFont = new Font("Microsoft YaHei", 10F);
        readonly Pen _gridPen = new Pen(GridColor);
        readonly Pen _axisPen = new Pen(AxisColor);
        readonly Pen _curvePen = new Pen(CurveColor, 1f);
        readonly Pen _cursorAPen = new Pen(CursorAColor, 1f) { DashStyle = DashStyle.Dash };
        readonly Pen _cursorBPen = new Pen(CursorBColor, 1f) { DashStyle = DashStyle.Dash };
        readonly SolidBrush _cursorABrush = new SolidBrush(CursorAColor);
        readonly SolidBrush _cursorBBrush = new SolidBrush(CursorBColor);
        readonly Pen _hoverPen = new Pen(Color.FromArgb(140, 146, 156), 1f) { DashStyle = DashStyle.Dot };
        readonly SolidBrush _peakBrush = new SolidBrush(PeakColor);
        readonly SolidBrush _mutedBrush = new SolidBrush(TextMuted);

        double[] _levels = new double[0];
        List<CurvePeak> _peaks = new List<CurvePeak>();
        double _pitch;
        double _roiStartM;
        double _roiEndM;
        double _displayMaxM = 12.0;

        double _viewStartM;
        double _viewSpanM;
        bool _viewInit;
        double _cursorAM;
        double _cursorBM;
        bool _cursorInit;

        int _dragMode;          // 0 无，1 平移，2 拖 A，3 拖 B
        int _dragX;
        double _dragStartViewM;

        Point _hoverPoint;
        bool _hover;

        /// <summary>拖动 A/B 结束后通知外部（A 距离, B 距离），用于重设 ROI。</summary>
        public event Action<double, double> CursorsChanged;

        public CurvePlotPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable
                | ControlStyles.UserPaint, true);
            BackColor = CardBack;
            // 滚轮事件按焦点分发，鼠标进入绘图区时取得焦点才能缩放。
            MouseEnter += (s, e) => { if (!Focused) Focus(); };
        }

        public void SetFrame(YinXun2SingleFrame frame, double displayMaxM)
        {
            if (frame == null)
            {
                ClearFrame();
                return;
            }
            _levels = frame.LevelsDb ?? new double[0];
            _peaks = frame.Peaks ?? new List<CurvePeak>();
            _pitch = frame.PitchM;
            _roiStartM = frame.RoiStartM;
            _roiEndM = frame.RoiEndM;

            bool displayChanged = displayMaxM > 0 && Math.Abs(displayMaxM - _displayMaxM) > 1e-9;
            if (displayMaxM > 0) _displayMaxM = displayMaxM;
            if (!_viewInit || displayChanged)
            {
                _viewStartM = 0;
                _viewSpanM = Math.Min(_displayMaxM, DataEndM());
                _viewInit = true;
            }
            if (!_cursorInit)
            {
                _cursorAM = _roiStartM;
                _cursorBM = _roiEndM;
                _cursorInit = true;
            }
            ClampView();
            Invalidate();
        }

        public void ClearFrame()
        {
            _levels = new double[0];
            _peaks = new List<CurvePeak>();
            _viewInit = false;
            _cursorInit = false;
            Invalidate();
        }

        /// <summary>外部（ROI 输入框）同步游标位置，不触发 CursorsChanged。</summary>
        public void SetCursors(double a, double b)
        {
            if (b <= a) return;
            _cursorAM = a;
            _cursorBM = b;
            _cursorInit = true;
            Invalidate();
        }

        double DataEndM()
        {
            return _levels.Length > 1 ? (_levels.Length - 1) * _pitch : 0;
        }

        Rectangle PlotRect()
        {
            return new Rectangle(PlotLeft, PlotTop,
                Math.Max(10, Width - PlotLeft - PlotRight),
                Math.Max(10, Height - PlotTop - PlotBottom));
        }

        void ClampView()
        {
            double dataEnd = DataEndM();
            if (dataEnd <= 0) return;
            if (_viewSpanM < 0.05) _viewSpanM = 0.05;
            if (_viewSpanM > dataEnd) _viewSpanM = dataEnd;
            if (_viewStartM < 0) _viewStartM = 0;
            if (_viewStartM + _viewSpanM > dataEnd) _viewStartM = dataEnd - _viewSpanM;
            if (_viewStartM < 0) _viewStartM = 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(CardBack);

            Rectangle plot = PlotRect();

            if (_levels.Length < 2 || _pitch <= 0)
            {
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("点击「单次采集」获取曲线", _emptyFont, _mutedBrush, plot, format);
                }
                return;
            }

            ClampView();
            double xMin = _viewStartM;
            double xMax = _viewStartM + _viewSpanM;

            double yMin, yMax;
            FitY(xMin, xMax, out yMin, out yMax);

            DrawGrid(g, plot, xMin, xMax, yMin, yMax);
            DrawRoi(g, plot, xMin, xMax);
            DrawCurve(g, plot, xMin, xMax, yMin, yMax);
            DrawPeaks(g, plot, xMin, xMax, yMin, yMax);
            DrawCursors(g, plot, xMin, xMax, yMin, yMax);
            DrawHover(g, plot, xMin, xMax, yMin, yMax);
            DrawXAxisTitle(g, plot);

            g.DrawRectangle(_axisPen, plot);
        }

        void FitY(double xMin, double xMax, out double yMin, out double yMax)
        {
            int i0 = Math.Max(0, (int)Math.Floor(xMin / _pitch));
            int i1 = Math.Min(_levels.Length - 1, (int)Math.Ceiling(xMax / _pitch));

            yMin = double.MaxValue;
            yMax = double.MinValue;
            for (int i = i0; i <= i1; i++)
            {
                double v = _levels[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (v < yMin) yMin = v;
                if (v > yMax) yMax = v;
            }
            if (yMin > yMax)
            {
                yMin = -120; yMax = 0;
            }
            double span = yMax - yMin;
            if (span <= 0) span = 1;
            yMin -= span * 0.18;
            yMax += span * 0.06;
        }

        void DrawGrid(Graphics g, Rectangle plot, double xMin, double xMax, double yMin, double yMax)
        {
            double xSpan = xMax - xMin;
            double xStep = NiceStep(xSpan / 8.0);
            string xFormat = xStep >= 1 ? "0" : xStep >= 0.1 ? "0.0" : "0.00";
            double xStart = Math.Ceiling(xMin / xStep) * xStep;
            for (double x = xStart; x <= xMax + 1e-9; x += xStep)
            {
                int px = XToPixel(plot, x, xMin, xMax);
                g.DrawLine(_gridPen, px, plot.Top, px, plot.Bottom);
                SizeF size = g.MeasureString(x.ToString(xFormat, CultureInfo.InvariantCulture), _axisFont);
                g.DrawString(x.ToString(xFormat, CultureInfo.InvariantCulture), _axisFont, _mutedBrush,
                    px - size.Width / 2f, plot.Bottom + 4);
            }

            double ySpan = yMax - yMin;
            double yStep = NiceStep(ySpan / 6.0);
            double yStart = Math.Ceiling(yMin / yStep) * yStep;
            for (double y = yStart; y <= yMax; y += yStep)
            {
                int py = YToPixel(plot, y, yMin, yMax);
                g.DrawLine(_gridPen, plot.Left, py, plot.Right, py);
                string text = y.ToString("0", CultureInfo.InvariantCulture);
                SizeF size = g.MeasureString(text, _axisFont);
                g.DrawString(text, _axisFont, _mutedBrush, plot.Left - size.Width - 6, py - size.Height / 2);
            }

            var state = g.Save();
            g.TranslateTransform(12, plot.Top + plot.Height / 2f);
            g.RotateTransform(-90);
            using (var format = new StringFormat { Alignment = StringAlignment.Center })
            {
                g.DrawString("反射 (dB)", _labelFont, _mutedBrush,
                    new RectangleF(-plot.Height / 2f, -7, plot.Height, 14), format);
            }
            g.Restore(state);
        }

        /// <summary>横轴标题画在图内右下角，避免被底部参数区遮住。</summary>
        void DrawXAxisTitle(Graphics g, Rectangle plot)
        {
            string text = "距离 (m)";
            SizeF size = g.MeasureString(text, _labelFont);
            var rect = new RectangleF(plot.Right - size.Width - 10,
                plot.Bottom - size.Height - 4, size.Width + 8, size.Height + 2);
            using (var brush = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            {
                g.FillRectangle(brush, rect);
            }
            g.DrawString(text, _labelFont, _mutedBrush, rect.X + 4, rect.Y);
        }

        void DrawRoi(Graphics g, Rectangle plot, double xMin, double xMax)
        {
            if (_roiEndM <= _roiStartM) return;
            double left = Math.Max(xMin, _roiStartM);
            double right = Math.Min(xMax, _roiEndM);
            if (right <= left) return;
            int x0 = XToPixel(plot, left, xMin, xMax);
            int x1 = XToPixel(plot, right, xMin, xMax);
            if (x1 <= x0) return;
            using (var brush = new SolidBrush(RoiBack))
            {
                g.FillRectangle(brush, x0, plot.Top, x1 - x0, plot.Height);
            }
        }

        void DrawCurve(Graphics g, Rectangle plot, double xMin, double xMax, double yMin, double yMax)
        {
            int width = plot.Width;
            if (width <= 0) return;
            double xSpan = xMax - xMin;

            for (int px = 0; px < width; px++)
            {
                double m0 = xMin + px * xSpan / width;
                double m1 = xMin + (px + 1) * xSpan / width;
                int i0 = (int)Math.Floor(m0 / _pitch);
                int i1 = (int)Math.Ceiling(m1 / _pitch);
                if (i0 < 0) i0 = 0;
                if (i1 > _levels.Length - 1) i1 = _levels.Length - 1;
                if (i1 < i0) continue;

                double lo = double.MaxValue, hi = double.MinValue;
                for (int i = i0; i <= i1; i++)
                {
                    double v = _levels[i];
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                if (lo > hi) continue;

                int yLo = YToPixel(plot, lo, yMin, yMax);
                int yHi = YToPixel(plot, hi, yMin, yMax);
                g.DrawLine(_curvePen, plot.Left + px, yHi, plot.Left + px, yLo);
            }
        }

        void DrawPeaks(Graphics g, Rectangle plot, double xMin, double xMax, double yMin, double yMax)
        {
            for (int i = 0; i < _peaks.Count; i++)
            {
                CurvePeak peak = _peaks[i];
                double positionM = peak.Centroid * _pitch;
                if (positionM < xMin || positionM > xMax) continue;
                int px = XToPixel(plot, positionM, xMin, xMax);
                int py = YToPixel(plot, peak.Db, yMin, yMax);
                g.FillEllipse(_peakBrush, px - 3, py - 3, 6, 6);
            }
        }

        void DrawCursors(Graphics g, Rectangle plot, double xMin, double xMax, double yMin, double yMax)
        {
            if (_cursorBM <= _cursorAM) return;
            int xa = XToPixel(plot, Math.Max(xMin, _cursorAM), xMin, xMax);
            int xb = XToPixel(plot, Math.Min(xMax, _cursorBM), xMin, xMax);
            if (xb > xa)
            {
                using (var brush = new SolidBrush(CursorBand))
                {
                    g.FillRectangle(brush, xa, plot.Top, xb - xa, plot.Height);
                }
            }

            DrawCursorLine(g, plot, _cursorAM, xMin, xMax, "A", _cursorAPen, _cursorABrush);
            DrawCursorLine(g, plot, _cursorBM, xMin, xMax, "B", _cursorBPen, _cursorBBrush);

            string delta = "Δ " + (_cursorBM - _cursorAM).ToString("0.00", CultureInfo.InvariantCulture) + " m";
            SizeF size = g.MeasureString(delta, _labelFont);
            g.DrawString(delta, _labelFont, _mutedBrush, plot.Right - size.Width - 8, plot.Top + 2);
        }

        /// <summary>鼠标跟随十字线 + 距离/电平读数。</summary>
        void DrawHover(Graphics g, Rectangle plot, double xMin, double xMax, double yMin, double yMax)
        {
            if (!_hover || !plot.Contains(_hoverPoint)) return;

            double x = XToValue(plot, _hoverPoint.X, xMin, xMax);
            double y = YToValue(plot, _hoverPoint.Y, yMin, yMax);

            g.DrawLine(_hoverPen, _hoverPoint.X, plot.Top, _hoverPoint.X, plot.Bottom);
            g.DrawLine(_hoverPen, plot.Left, _hoverPoint.Y, plot.Right, _hoverPoint.Y);

            string text = x.ToString("0.000", CultureInfo.InvariantCulture) + " m   "
                + y.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
            SizeF size = g.MeasureString(text, _axisFont);
            float left = _hoverPoint.X + 12;
            float top = _hoverPoint.Y - size.Height - 8;
            if (left + size.Width + 10 > plot.Right) left = _hoverPoint.X - size.Width - 18;
            if (top < plot.Top) top = _hoverPoint.Y + 10;
            var rect = new RectangleF(left, top, size.Width + 10, size.Height + 6);
            using (var brush = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
            {
                g.FillRectangle(brush, rect);
            }
            using (var pen = new Pen(Color.FromArgb(190, 196, 204)))
            {
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            }
            g.DrawString(text, _axisFont, _mutedBrush, rect.X + 5, rect.Y + 3);
        }

        void DrawCursorLine(Graphics g, Rectangle plot, double positionM, double xMin, double xMax,
            string name, Pen pen, SolidBrush brush)
        {
            if (positionM < xMin || positionM > xMax) return;
            int px = XToPixel(plot, positionM, xMin, xMax);
            g.DrawLine(pen, px, plot.Top, px, plot.Bottom);

            string text = name + " " + positionM.ToString("0.00", CultureInfo.InvariantCulture);
            SizeF size = g.MeasureString(text, _labelFont);
            float x = px + 3;
            if (x + size.Width > plot.Right) x = px - size.Width - 3;
            var rect = new RectangleF(x, plot.Top + 2, size.Width + 4, size.Height);
            using (var back = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            {
                g.FillRectangle(back, rect);
            }
            g.DrawString(text, _labelFont, brush, rect.X + 2, rect.Y);
        }

        static int XToPixel(Rectangle plot, double x, double xMin, double xMax)
        {
            double span = xMax - xMin;
            if (span <= 0) return plot.Left;
            double ratio = (x - xMin) / span;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            return plot.Left + (int)Math.Round(ratio * plot.Width);
        }

        static double YToValue(Rectangle plot, int py, double yMin, double yMax)
        {
            double ratio = (plot.Bottom - py) / (double)plot.Height;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            return yMin + ratio * (yMax - yMin);
        }

        static double XToValue(Rectangle plot, int px, double xMin, double xMax)
        {
            double ratio = (px - plot.Left) / (double)plot.Width;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            return xMin + ratio * (xMax - xMin);
        }

        static int YToPixel(Rectangle plot, double y, double yMin, double yMax)
        {
            if (yMax <= yMin) return plot.Bottom;
            double ratio = (y - yMin) / (yMax - yMin);
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            return plot.Bottom - (int)Math.Round(ratio * plot.Height);
        }

        static double NiceStep(double raw)
        {
            if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw)) return 10;
            double exp = Math.Floor(Math.Log10(raw));
            double baseValue = Math.Pow(10, exp);
            double fraction = raw / baseValue;
            double nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
            return nice * baseValue;
        }

        // ---------------- 交互 ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || _levels.Length < 2) return;
            Rectangle plot = PlotRect();
            if (!plot.Contains(e.Location)) return;
            Focus();

            double xMin = _viewStartM;
            double xMax = _viewStartM + _viewSpanM;
            int xa = XToPixel(plot, _cursorAM, xMin, xMax);
            int xb = XToPixel(plot, _cursorBM, xMin, xMax);

            if (Math.Abs(e.X - xa) <= CursorHitPx) _dragMode = 2;
            else if (Math.Abs(e.X - xb) <= CursorHitPx) _dragMode = 3;
            else
            {
                _dragMode = 1;
                _dragX = e.X;
                _dragStartViewM = _viewStartM;
            }
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle plot = PlotRect();
            bool inside = _levels.Length >= 2 && plot.Contains(e.Location);
            if (inside != _hover || (inside && _hoverPoint != e.Location))
            {
                _hover = inside;
                _hoverPoint = e.Location;
                Invalidate();
            }

            if (_dragMode == 0)
            {
                if (!inside)
                {
                    Cursor = Cursors.Default;
                    return;
                }
                double xMin = _viewStartM;
                double xMax = _viewStartM + _viewSpanM;
                int xa = XToPixel(plot, _cursorAM, xMin, xMax);
                int xb = XToPixel(plot, _cursorBM, xMin, xMax);
                Cursor = Math.Abs(e.X - xa) <= CursorHitPx || Math.Abs(e.X - xb) <= CursorHitPx
                    ? Cursors.SizeWE : Cursors.Default;
                return;
            }

            if (_dragMode == 1)
            {
                double dataEnd = DataEndM();
                double dxM = (e.X - _dragX) * _viewSpanM / plot.Width;
                _viewStartM = _dragStartViewM - dxM;
                ClampView();
                Invalidate();
                return;
            }

            double m = XToValue(plot, e.X, _viewStartM, _viewStartM + _viewSpanM);
            m = Math.Max(0, Math.Min(DataEndM(), m));
            if (_dragMode == 2)
            {
                _cursorAM = Math.Min(m, _cursorBM - 0.01);
            }
            else if (_dragMode == 3)
            {
                _cursorBM = Math.Max(m, _cursorAM + 0.01);
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_hover) return;
            _hover = false;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int mode = _dragMode;
            _dragMode = 0;
            Capture = false;
            if ((mode == 2 || mode == 3) && _cursorBM > _cursorAM)
            {
                CursorsChanged?.Invoke(_cursorAM, _cursorBM);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_levels.Length < 2) return;
            Rectangle plot = PlotRect();
            if (!plot.Contains(e.Location)) return;

            double dataEnd = DataEndM();
            double xMin = _viewStartM;
            double xMax = _viewStartM + _viewSpanM;
            double anchor = XToValue(plot, e.X, xMin, xMax);
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            double newSpan = _viewSpanM * factor;
            newSpan = Math.Max(0.05, Math.Min(dataEnd, newSpan));

            double ratio = (anchor - _viewStartM) / _viewSpanM;
            _viewStartM = anchor - ratio * newSpan;
            _viewSpanM = newSpan;
            ClampView();
            Invalidate();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (_levels.Length < 2) return;
            _viewStartM = 0;
            _viewSpanM = Math.Min(_displayMaxM, DataEndM());
            ClampView();
            Invalidate();
        }
    }
}
