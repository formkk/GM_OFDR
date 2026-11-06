using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using Gmix.App.Orchestration;
using Gmix.Data;

namespace Gmix.App.UI
{
    /// <summary>
    /// 距离域曲线（SRS FR-CHT-01~09 / SDD §8.3）：按像素列 min/max 包络抽稀，保峰；
    /// 显示电平 = dB_rel + rlOffsetK，只在本控件内应用，不改数据字段。
    /// </summary>
    public sealed class CurveCanvas : Control
    {
        const int MarginLeft = 64;
        const int MarginRight = 18;
        const int MarginTop = 14;
        const int MarginBottom = 34;

        double[] _dist = new double[0];
        double[] _ps = new double[0];
        double[] _baseline = new double[0];
        PeakInfo[] _peaks = new PeakInfo[0];
        AnalysisRangeInfo _range;
        double _offsetK = DisplayTransform.DefaultOffsetK;
        bool _showZeroPeak;

        double _xMin;
        double _xMax = 20;
        readonly double _yMin = -150;
        readonly double _yMax = -40;

        bool _dragging;
        int _dragX;
        double _dragXMin;
        double _dragXMax;
        Point _cursor = Point.Empty;
        bool _hasCursor;
        int? _selectedPeakNo;

        public CurveCanvas()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public void ShowSnapshot(MeasurementSnapshot snapshot)
        {
            if (snapshot == null)
            {
                _dist = new double[0];
                _ps = new double[0];
                _baseline = new double[0];
                _peaks = new PeakInfo[0];
                _range = null;
            }
            else
            {
                _dist = snapshot.Dist ?? new double[0];
                _ps = snapshot.Ps ?? new double[0];
                _baseline = snapshot.Baseline ?? new double[0];
                _peaks = snapshot.Events != null && snapshot.Events.peaks != null ? snapshot.Events.peaks : new PeakInfo[0];
                _range = snapshot.Range;
                _offsetK = snapshot.OffsetK;
                _xMin = 0;
                _xMax = _range != null && _range.endM > _range.startM
                    ? _range.endM
                    : (_dist.Length > 0 ? _dist[_dist.Length - 1] : 20);
            }
            Invalidate();
        }

        public void SetSelectedPeak(int? peakNo)
        {
            _selectedPeakNo = peakNo;
            Invalidate();
        }

        public void SetZeroPeakVisible(bool visible)
        {
            _showZeroPeak = visible;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;

            Rectangle plot = new Rectangle(MarginLeft, MarginTop,
                Math.Max(10, Width - MarginLeft - MarginRight), Math.Max(10, Height - MarginTop - MarginBottom));

            DrawGrid(g, plot);
            if (_dist.Length < 2 || _ps.Length != _dist.Length)
            {
                DrawNoData(g, plot);
                return;
            }

            DrawAnalysisMask(g, plot);
            DrawEnvelope(g, plot, _baseline, Color.SeaGreen, 1);
            DrawEnvelope(g, plot, _ps, Color.RoyalBlue, 1);
            DrawPeaks(g, plot);
            DrawCursor(g, plot);
            DrawLegend(g, plot);
        }

        void DrawGrid(Graphics g, Rectangle plot)
        {
            using (var axis = new Pen(Color.DimGray))
            using (var grid = new Pen(Color.Gainsboro))
            using (var font = new Font("Segoe UI", 8))
            using (var brush = new SolidBrush(Color.DimGray))
            {
                g.DrawRectangle(axis, plot);

                for (int i = 0; i <= 5; i++)
                {
                    int x = plot.Left + plot.Width * i / 5;
                    if (i > 0 && i < 5) g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    double value = _xMin + (_xMax - _xMin) * i / 5.0;
                    string text = value.ToString("0.##") + " m";
                    SizeF size = g.MeasureString(text, font);
                    g.DrawString(text, font, brush, x - size.Width / 2, plot.Bottom + 4);
                }

                for (int i = 0; i <= 5; i++)
                {
                    int y = plot.Bottom - plot.Height * i / 5;
                    if (i > 0 && i < 5) g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    double value = _yMin + (_yMax - _yMin) * i / 5.0;
                    string text = value.ToString("0");
                    SizeF size = g.MeasureString(text, font);
                    g.DrawString(text, font, brush, plot.Left - size.Width - 6, y - size.Height / 2);
                }

                g.DrawString("dB", font, brush, 6, plot.Top - 2);
            }
        }

        void DrawNoData(Graphics g, Rectangle plot)
        {
            using (var font = new Font("Segoe UI", 10))
            using (var brush = new SolidBrush(Color.Gray))
            {
                const string text = "暂无数据：完成一次采集或从历史组加载";
                SizeF size = g.MeasureString(text, font);
                g.DrawString(text, font, brush, plot.Left + (plot.Width - size.Width) / 2,
                    plot.Top + (plot.Height - size.Height) / 2);
            }
        }

        void DrawAnalysisMask(Graphics g, Rectangle plot)
        {
            if (_range == null || !(_range.endM > _range.startM)) return;
            int startX = ToX(_range.startM, plot);
            int endX = ToX(_range.endM, plot);
            using (var mask = new SolidBrush(Color.FromArgb(24, Color.Gray)))
            {
                g.FillRectangle(mask, plot.Left, plot.Top, Math.Max(0, startX - plot.Left), plot.Height);
                g.FillRectangle(mask, endX, plot.Top, Math.Max(0, plot.Right - endX), plot.Height);
            }
            using (var pen = new Pen(Color.DarkOrange) { DashStyle = DashStyle.Dash })
            {
                g.DrawLine(pen, startX, plot.Top, startX, plot.Bottom);
                g.DrawLine(pen, endX, plot.Top, endX, plot.Bottom);
                double guard = _range.guardM > 0 ? _range.guardM : 0.1;
                int guardIn = ToX(_range.startM + guard, plot);
                int guardOut = ToX(_range.endM - guard, plot);
                if (guardIn > startX) g.DrawLine(pen, guardIn, plot.Top, guardIn, plot.Bottom);
                if (guardOut < endX) g.DrawLine(pen, guardOut, plot.Top, guardOut, plot.Bottom);
            }
        }

        void DrawEnvelope(Graphics g, Rectangle plot, double[] values, Color color, float width)
        {
            if (values == null || values.Length != _dist.Length || values.Length == 0) return;
            EnvelopeSampler.Column[] columns = EnvelopeSampler.Reduce(_dist, values, _xMin, _xMax, plot.Width);
            using (var pen = new Pen(color, width))
            {
                for (int i = 0; i < columns.Length; i++)
                {
                    if (columns[i].MinIndex < 0) continue;
                    int x = plot.Left + i;
                    int y1 = ToY(_dist.Length > 0 ? values[columns[i].MinIndex] + _offsetK : 0, plot);
                    int y2 = ToY(values[columns[i].MaxIndex] + _offsetK, plot);
                    g.DrawLine(pen, x, y1, x, y2);
                }
            }
        }

        void DrawPeaks(Graphics g, Rectangle plot)
        {
            using (var font = new Font("Segoe UI", 8))
            using (var brush = new SolidBrush(Color.Firebrick))
            using (var selected = new SolidBrush(Color.Red))
            {
                foreach (PeakInfo peak in _peaks)
                {
                    bool isZero = peak.zeroPeak.HasValue && peak.zeroPeak.Value;
                    if (isZero && !_showZeroPeak) continue;
                    double loc = peak.distanceM.HasValue ? peak.distanceM.Value : peak.locM;
                    if (loc < _xMin || loc > _xMax) continue;

                    int x = ToX(loc, plot);
                    int y = ToY(peak.heightDbRel + _offsetK, plot);
                    Point[] triangle = { new Point(x - 4, y - 8), new Point(x + 4, y - 8), new Point(x, y - 2) };
                    g.FillPolygon(peak.peakNo.HasValue && _selectedPeakNo.HasValue
                        && peak.peakNo.Value == _selectedPeakNo.Value ? selected : brush, triangle);
                    if (peak.peakNo.HasValue)
                        g.DrawString(peak.peakNo.Value.ToString(), font, brush, x - 3, y - 20);
                }
            }
        }

        void DrawCursor(Graphics g, Rectangle plot)
        {
            if (!_hasCursor || !plot.Contains(_cursor)) return;
            using (var pen = new Pen(Color.Gray) { DashStyle = DashStyle.Dot })
            using (var font = new Font("Segoe UI", 8))
            using (var brush = new SolidBrush(Color.Black))
            {
                g.DrawLine(pen, _cursor.X, plot.Top, _cursor.X, plot.Bottom);
                double distance = XToValue(_cursor.X, plot);
                double level = YToValue(_cursor.Y, plot);
                string text = distance.ToString("0.000") + " m / " + level.ToString("0.0") + " dB";
                SizeF size = g.MeasureString(text, font);
                float boxX = Math.Min(_cursor.X + 6, plot.Right - size.Width - 4);
                float boxY = Math.Max(plot.Top + 2, _cursor.Y - size.Height - 4);
                g.FillRectangle(Brushes.WhiteSmoke, boxX - 2, boxY - 1, size.Width + 4, size.Height + 2);
                g.DrawString(text, font, brush, boxX, boxY);
            }
        }

        void DrawLegend(Graphics g, Rectangle plot)
        {
            using (var font = new Font("Segoe UI", 8))
            using (var blue = new SolidBrush(Color.RoyalBlue))
            using (var green = new SolidBrush(Color.SeaGreen))
            {
                g.DrawString("— ps", font, blue, plot.Right - 110, plot.Top + 4);
                g.DrawString("— baseline", font, green, plot.Right - 66, plot.Top + 4);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragX = e.X;
                _dragXMin = _xMin;
                _dragXMax = _xMax;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) _dragging = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _cursor = e.Location;
            _hasCursor = true;
            if (_dragging)
            {
                Rectangle plot = PlotRect();
                if (plot.Width > 0)
                {
                    double span = _dragXMax - _dragXMin;
                    double shift = (_dragX - e.X) * span / plot.Width;
                    _xMin = _dragXMin + shift;
                    _xMax = _dragXMax + shift;
                }
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hasCursor = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_xMax <= _xMin) return;
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            Rectangle plot = PlotRect();
            double anchor = plot.Width > 0 ? XToValue(e.X, plot) : (_xMin + _xMax) / 2;
            double newMin = anchor - (anchor - _xMin) * factor;
            double newMax = anchor + (_xMax - anchor) * factor;
            if (newMax - newMin < 0.01) return;
            _xMin = newMin;
            _xMax = newMax;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            _xMin = 0;
            _xMax = _range != null && _range.endM > _range.startM
                ? _range.endM
                : (_dist.Length > 0 ? _dist[_dist.Length - 1] : 20);
            Invalidate();
        }

        Rectangle PlotRect()
        {
            return new Rectangle(MarginLeft, MarginTop,
                Math.Max(10, Width - MarginLeft - MarginRight), Math.Max(10, Height - MarginTop - MarginBottom));
        }

        int ToX(double distance, Rectangle plot)
        {
            double span = _xMax - _xMin;
            if (span <= 0) return plot.Left;
            return plot.Left + (int)((distance - _xMin) / span * plot.Width);
        }

        int ToY(double level, Rectangle plot)
        {
            double span = _yMax - _yMin;
            if (span <= 0) return plot.Bottom;
            return plot.Bottom - (int)((level - _yMin) / span * plot.Height);
        }

        double XToValue(int x, Rectangle plot)
        {
            if (plot.Width <= 0) return _xMin;
            return _xMin + (x - plot.Left) * (_xMax - _xMin) / plot.Width;
        }

        double YToValue(int y, Rectangle plot)
        {
            if (plot.Height <= 0) return _yMin;
            return _yMin + (plot.Bottom - y) * (_yMax - _yMin) / plot.Height;
        }
    }
}
