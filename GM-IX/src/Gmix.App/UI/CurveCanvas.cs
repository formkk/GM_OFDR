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
        const int MarginTop = 40;
        const int MarginBottom = 34;

        double[] _dist = new double[0];
        double[] _psDb = new double[0];
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

        double? _cursorA;
        double? _cursorB;
        int _dragCursor = -1;

        // 参照厂商 HRAS-9000：绘图区浅米绿底、绘图区外浅灰底、网格浅灰绿
        static readonly Color PlotBackground = Color.FromArgb(247, 249, 236);
        static readonly Color PanelBackground = Color.FromArgb(242, 242, 240);
        static readonly Color GridLine = Color.FromArgb(214, 219, 198);
        static readonly Color AxisLine = Color.FromArgb(140, 140, 132);
        static readonly Color AxisText = Color.FromArgb(70, 70, 66);
        static readonly Color CursorAColor = Color.FromArgb(192, 57, 43);
        static readonly Color CursorBColor = Color.FromArgb(39, 124, 74);
        // 基线与蓝曲线、浅米绿背景、橙色区间虚线都要区分开，用深紫
        static readonly Color BaselineColor = Color.FromArgb(106, 27, 154);

        public CurveCanvas()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = PanelBackground;
        }

        public void ShowSnapshot(MeasurementSnapshot snapshot)
        {
            if (snapshot == null)
            {
                _dist = new double[0];
                _psDb = new double[0];
                _baseline = new double[0];
                _peaks = new PeakInfo[0];
                _range = null;
                _cursorA = null;
                _cursorB = null;
            }
            else
            {
                _dist = snapshot.Dist ?? new double[0];
                _psDb = snapshot.PsDb ?? new double[0];
                _baseline = snapshot.Baseline ?? new double[0];
                _peaks = snapshot.Events != null && snapshot.Events.peaks != null ? snapshot.Events.peaks : new PeakInfo[0];
                _range = snapshot.Range;
                _offsetK = snapshot.OffsetK;
                _xMin = 0;
                _xMax = _range != null && _range.endM > _range.startM
                    ? _range.endM
                    : (_dist.Length > 0 ? _dist[_dist.Length - 1] : 20);

                // A/B 游标初始位置内缩，避免压住左右边框导致难以抓取
                double span = _xMax - _xMin;
                double a = _range != null && _range.startM > 0 ? _range.startM : _xMin;
                if (a < _xMin + span * 0.02) a = _xMin + span * 0.02;
                _cursorA = a;
                _cursorB = _xMin + span * 0.95;
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

            using (var plotBrush = new SolidBrush(PlotBackground))
                g.FillRectangle(plotBrush, plot);

            DrawGrid(g, plot);
            if (_dist.Length < 2 || _psDb.Length != _dist.Length)
            {
                DrawNoData(g, plot);
                return;
            }

            DrawAnalysisMask(g, plot);
            // 反射强度先画、基线后画：基线是平滑中线，后画且略粗才不会被密集的蓝色包络盖住
            DrawEnvelope(g, plot, _psDb, Color.RoyalBlue, 1);
            DrawEnvelope(g, plot, _baseline, BaselineColor, 2f);
            DrawPeaks(g, plot);
            DrawCursors(g, plot);
            RectangleF readout = DrawReadout(g, plot);
            DrawCursor(g, plot);
            DrawLegend(g, plot, readout);
        }

        void DrawGrid(Graphics g, Rectangle plot)
        {
            using (var axis = new Pen(AxisLine))
            using (var grid = new Pen(GridLine))
            using (var font = new Font("Segoe UI", 8))
            using (var brush = new SolidBrush(AxisText))
            {
                g.DrawRectangle(axis, plot);

                // X：取整刻度（1/2/2.5/5×10ⁿ），避免出现 -2.86 这类非整数标签
                double xStep = NiceStep(_xMax - _xMin, 6);
                double xStart = Math.Ceiling(_xMin / xStep) * xStep;
                for (double v = xStart; v <= _xMax + 1e-9; v += xStep)
                {
                    int x = ToX(v, plot);
                    if (x <= plot.Left || x >= plot.Right)
                    {
                        x = Math.Max(plot.Left, Math.Min(x, plot.Right));
                    }
                    else
                    {
                        g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    }
                    string text = v.ToString("0.##") + " m";
                    SizeF size = g.MeasureString(text, font);
                    g.DrawString(text, font, brush, x - size.Width / 2, plot.Bottom + 4);
                }

                // Y：固定 10 dB 整数刻度（-40、-50 … -150），与原厂一致
                const double yStep = 10.0;
                for (double v = Math.Ceiling(_yMin / yStep) * yStep; v <= _yMax + 1e-9; v += yStep)
                {
                    int y = ToY(v, plot);
                    if (y < plot.Top || y > plot.Bottom) continue;
                    if (y > plot.Top && y < plot.Bottom) g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    string text = v.ToString("0");
                    SizeF size = g.MeasureString(text, font);
                    g.DrawString(text, font, brush, plot.Left - size.Width - 6, y - size.Height / 2);
                }

                var state = g.Save();
                g.TranslateTransform(12, plot.Bottom);
                g.RotateTransform(-90);
                g.DrawString("反射强度(dB)", font, brush, 0, 0);
                g.Restore(state);
            }
        }

        /// <summary>取 1/2/2.5/5×10ⁿ 的“好看”步长，使刻度落在整数或半整数上。</summary>
        static double NiceStep(double span, int targetTicks)
        {
            if (!(span > 0) || targetTicks <= 0) return 1;
            double raw = span / targetTicks;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double normalized = raw / magnitude;
            double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10;
            return nice * magnitude;
        }

        void DrawNoData(Graphics g, Rectangle plot)
        {
            string text = _dist.Length < 2
                ? "暂无数据：完成一次采集或从历史组加载"
                : "该组缺少 ps_db.f64，无法绘制反射强度曲线";
            using (var font = new Font("Segoe UI", 10))
            using (var brush = new SolidBrush(Color.Gray))
            {
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
            using (var font = new Font("Segoe UI", 8.5f))
            using (var brush = new SolidBrush(Color.Firebrick))
            using (var selected = new SolidBrush(Color.Red))
            using (var textBrush = new SolidBrush(Color.Black))
            {
                int displayNo = 0;
                foreach (PeakInfo peak in _peaks)
                {
                    bool isZero = (peak.zeroPeak.HasValue && peak.zeroPeak.Value)
                        || (_range != null && _range.startM > 0 && peak.locM < _range.startM);
                    if (!isZero) displayNo++;
                    if (isZero && !_showZeroPeak) continue;
                    double loc = peak.distanceM.HasValue ? peak.distanceM.Value : peak.locM;
                    if (loc < _xMin || loc > _xMax) continue;

                    int x = ToX(loc, plot);
                    double level = peak.heightDbRel + _offsetK;
                    int y = ToY(level, plot);
                    Point[] triangle = { new Point(x - 4, y - 8), new Point(x + 4, y - 8), new Point(x, y - 2) };
                    g.FillPolygon(!isZero && _selectedPeakNo.HasValue && displayNo == _selectedPeakNo.Value
                        ? selected : brush, triangle);
                    if (isZero) continue;

                    // 与厂商 UI 一致：峰上方两行标注「P编号 距离」与电平；编号按显示顺序从 1 起
                    string head = "P" + displayNo + " " + loc.ToString("0.0000") + " m";
                    string tail = level.ToString("0.00") + " dB";
                    SizeF headSize = g.MeasureString(head, font);
                    SizeF tailSize = g.MeasureString(tail, font);
                    float textX = x - Math.Max(headSize.Width, tailSize.Width) / 2;
                    textX = Math.Max(plot.Left + 1, Math.Min(textX, plot.Right - Math.Max(headSize.Width, tailSize.Width) - 1));
                    float textY = Math.Max(plot.Top + 1, y - 36);
                    g.DrawString(head, font, textBrush, textX, textY);
                    g.DrawString(tail, font, textBrush, textX, textY + 13);
                }
            }
        }

        void DrawCursors(Graphics g, Rectangle plot)
        {
            using (var penA = new Pen(CursorAColor, 1.2f) { DashStyle = DashStyle.Dash })
            using (var penB = new Pen(CursorBColor, 1.2f) { DashStyle = DashStyle.Dash })
            using (var font = new Font("Segoe UI", 7.5f))
            using (var white = new SolidBrush(Color.White))
            {
                if (_cursorA.HasValue) DrawCursorLine(g, plot, penA, CursorAColor, white, font, _cursorA.Value, "A");
                if (_cursorB.HasValue) DrawCursorLine(g, plot, penB, CursorBColor, white, font, _cursorB.Value, "B");
            }
        }

        void DrawCursorLine(Graphics g, Rectangle plot, Pen pen, Color color, Brush white, Font font,
            double distance, string label)
        {
            int px = ToX(distance, plot);
            if (px < plot.Left || px > plot.Right) return;
            g.DrawLine(pen, px, plot.Top, px, plot.Bottom - 1);
            int chipX = Math.Max(plot.Left + 1, Math.Min(px - 7, plot.Right - 15));
            using (var chip = new SolidBrush(color))
            {
                g.FillRectangle(chip, chipX, plot.Top + 2, 14, 14);
            }
            g.DrawString(label, font, white, chipX + 3, plot.Top + 1);
        }

        RectangleF DrawReadout(Graphics g, Rectangle plot)
        {
            if (!_cursorA.HasValue && !_cursorB.HasValue) return RectangleF.Empty;
            using (var font = new Font("Segoe UI", 9f, FontStyle.Regular))
            using (var dark = new SolidBrush(Color.FromArgb(32, 32, 32)))
            using (var brushA = new SolidBrush(CursorAColor))
            using (var brushB = new SolidBrush(CursorBColor))
            using (var background = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            using (var border = new Pen(Color.FromArgb(200, 204, 188)))
            {
                double aLevel = _cursorA.HasValue ? LevelAt(_cursorA.Value) : double.NaN;
                double bLevel = _cursorB.HasValue ? LevelAt(_cursorB.Value) : double.NaN;
                string a = _cursorA.HasValue ? _cursorA.Value.ToString("0.000") + " m / " + Fmt(aLevel) : "—";
                string b = _cursorB.HasValue ? _cursorB.Value.ToString("0.000") + " m / " + Fmt(bLevel) : "—";
                string gap = _cursorA.HasValue && _cursorB.HasValue
                    ? Math.Abs(_cursorB.Value - _cursorA.Value).ToString("0.000") + " m" : "—";
                string delta = double.IsNaN(aLevel) || double.IsNaN(bLevel) ? "—" : (bLevel - aLevel).ToString("0.00") + " dB";

                // A 用红色、B 用绿色，与两条游标虚线同色；Δ 部分保持深灰
                string textA = "A " + a;
                string textB = "B " + b;
                const string gapText = "     ";
                string textRest = "     Δ间距 " + gap + "     Δ电平 " + delta;

                SizeF sizeA = g.MeasureString(textA, font);
                SizeF sizeGap = g.MeasureString(gapText, font);
                SizeF sizeB = g.MeasureString(textB, font);
                SizeF sizeRest = g.MeasureString(textRest, font);
                float width = sizeA.Width + sizeGap.Width + sizeB.Width + sizeRest.Width;

                var box = new RectangleF(plot.Left - 4, 1, width + 10, sizeA.Height + 4);
                g.FillRectangle(background, box);
                g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);

                float textX = box.X + 5;
                float textY = box.Y + 2;
                g.DrawString(textA, font, brushA, textX, textY);
                textX += sizeA.Width;
                g.DrawString(gapText, font, dark, textX, textY);
                textX += sizeGap.Width;
                g.DrawString(textB, font, brushB, textX, textY);
                textX += sizeB.Width;
                g.DrawString(textRest, font, dark, textX, textY);
                return box;
            }
        }

        static string Fmt(double value)
        {
            return double.IsNaN(value) ? "—" : value.ToString("0.0") + " dB";
        }

        /// <summary>游标处的显示电平（ps_db + 显示偏移）；按距离二分查找最近点。</summary>
        double LevelAt(double distance)
        {
            if (_dist.Length == 0 || _psDb.Length != _dist.Length) return double.NaN;
            int lo = 0, hi = _dist.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_dist[mid] < distance) lo = mid + 1; else hi = mid;
            }
            if (lo > 0 && Math.Abs(_dist[lo - 1] - distance) <= Math.Abs(_dist[lo] - distance)) lo--;
            return _psDb[lo] + _offsetK;
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

        /// <summary>图例默认右上角；与 A/B 读数条重叠时（窄窗口）自动下移一行。</summary>
        void DrawLegend(Graphics g, Rectangle plot, RectangleF readout)
        {
            using (var font = new Font("Segoe UI", 8))
            using (var blue = new SolidBrush(Color.RoyalBlue))
            using (var baseline = new SolidBrush(BaselineColor))
            {
                float y = 4;
                if (!readout.IsEmpty && readout.Right > plot.Right - 170)
                    y = Math.Min(readout.Bottom + 2, plot.Top - 14);
                g.DrawString("— 反射强度", font, blue, plot.Right - 164, y);
                g.DrawString("— 基线", font, baseline, plot.Right - 84, y);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            Rectangle plot = PlotRect();
            if (plot.Contains(e.Location))
            {
                int ax = _cursorA.HasValue ? ToX(_cursorA.Value, plot) : int.MinValue;
                int bx = _cursorB.HasValue ? ToX(_cursorB.Value, plot) : int.MinValue;
                if (Math.Abs(e.X - ax) <= 8) _dragCursor = 0;
                else if (Math.Abs(e.X - bx) <= 8) _dragCursor = 1;

                if (_dragCursor >= 0)
                {
                    MoveCursorTo(e.X, plot);
                    Cursor = Cursors.Hand;
                    return;
                }
            }

            _dragging = true;
            _dragX = e.X;
            _dragXMin = _xMin;
            _dragXMax = _xMax;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragCursor = -1;
            if (e.Button == MouseButtons.Left) _dragging = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _cursor = e.Location;
            _hasCursor = true;

            Rectangle plot = PlotRect();
            if (_dragCursor >= 0 && e.Button == MouseButtons.Left)
            {
                MoveCursorTo(e.X, plot);
                return;
            }

            if (_dragging && plot.Width > 0)
            {
                double span = _dragXMax - _dragXMin;
                double shift = (_dragX - e.X) * span / plot.Width;
                _xMin = _dragXMin + shift;
                _xMax = _dragXMax + shift;
            }
            else if (plot.Width > 0)
            {
                int ax = _cursorA.HasValue ? ToX(_cursorA.Value, plot) : int.MinValue;
                int bx = _cursorB.HasValue ? ToX(_cursorB.Value, plot) : int.MinValue;
                Cursor = Math.Min(Math.Abs(e.X - ax), Math.Abs(e.X - bx)) <= 8 ? Cursors.Hand : Cursors.Cross;
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

        void MoveCursorTo(int x, Rectangle plot)
        {
            if (plot.Width <= 0) return;
            double ratio = (x - plot.Left) / (double)plot.Width;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            double distance = _xMin + ratio * (_xMax - _xMin);
            if (_dragCursor == 0) _cursorA = distance;
            else _cursorB = distance;
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
