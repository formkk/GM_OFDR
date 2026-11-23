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
    /// 单条路径的距离域曲线窗口（双击指标表某行打开）。
    /// 性能设计（与 GM-IX 曲线控件同思路）：
    /// 1) 峰检测在窗口创建时算一次，绘制只读缓存；
    /// 2) 构建 min/max 分块金字塔，任意缩放下每像素只聚合一个块 → 每帧 O(像素宽)；
    /// 3) 画布双缓冲，避免闪烁；
    /// 4) 滚轮/拖动重绘用 16 ms 计时器合并，避免事件排队。
    /// 曲线仅来自内存，不读文件、不落盘。
    /// </summary>
    internal sealed class CurveForm : Form
    {
        static readonly Color PageBack = Color.FromArgb(243, 245, 248);
        static readonly Color CardBack = Color.White;
        static readonly Color BorderColor = Color.FromArgb(219, 224, 231);
        static readonly Color AxisColor = Color.FromArgb(150, 158, 170);
        static readonly Color GridColor = Color.FromArgb(232, 236, 242);
        static readonly Color CurveColor = Color.FromArgb(24, 90, 188);
        static readonly Color PeakColor = Color.FromArgb(206, 84, 60);
        static readonly Color TextMuted = Color.FromArgb(106, 114, 125);
        static readonly Color ThresholdColor = Color.FromArgb(200, 208, 220);

        /// <summary>金字塔块大小：每块缓存 min/max。</summary>
        const int BlockSize = 512;

        const int PlotLeft = 68;
        const int PlotRight = 18;
        const int PlotTop = 16;
        const int PlotBottom = 34;

        readonly MetricTableRow _row;
        readonly double[] _levels;
        readonly double _metersPerRow;
        readonly double _axisScale;
        readonly double _axisOffset;
        readonly double _peakThreshold;
        readonly List<CurvePeak> _peaks;
        double[] _blockMin;
        double[] _blockMax;

        readonly BufferedPanel _canvas = new BufferedPanel { Dock = DockStyle.Fill, BackColor = CardBack };
        readonly Label _readout = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            ForeColor = TextMuted,
            Text = "移动鼠标查看坐标"
        };

        readonly Timer _repaintTimer = new Timer { Interval = 16 };
        bool _repaintPending;

        // 复用的绘图资源，避免每帧创建/销毁
        readonly Font _axisFont = new Font("Consolas", 8F);
        readonly Font _peakFont = new Font("Consolas", 7.5F);
        readonly Font _uiFont = new Font("Microsoft YaHei", 9F);
        readonly Font _uiSmallFont = new Font("Microsoft YaHei", 8.5F);
        readonly Font _uiTitleFont = new Font("Microsoft YaHei", 10.5F, FontStyle.Bold);
        readonly Pen _gridPen = new Pen(GridColor);
        readonly Pen _axisPen = new Pen(AxisColor);
        readonly Pen _curvePen = new Pen(CurveColor, 1.2f);
        readonly Pen _peakPen = new Pen(PeakColor, 1f) { DashStyle = DashStyle.Dot };
        readonly Pen _thresholdPen = new Pen(ThresholdColor) { DashStyle = DashStyle.Dash };
        readonly SolidBrush _curveBrush = new SolidBrush(CurveColor);
        readonly SolidBrush _peakBrush = new SolidBrush(PeakColor);
        readonly SolidBrush _mutedBrush = new SolidBrush(TextMuted);

        // 视图范围
        double _viewStartM;
        double _viewEndM;
        Point _dragFrom;
        double _dragStartM, _dragEndM;

        public CurveForm(MetricTableRow row)
        {
            _row = row;
            _levels = row.LevelsDb ?? new double[0];
            _metersPerRow = row.MetersPerRow > 0 ? row.MetersPerRow : 0.000185;
            // 参考系显示轴：映讯_2 从设备连出口起算（x = (i×步距 − b)/a），LGA50 即原生轴
            _axisScale = row.AxisScaleM > 0 ? row.AxisScaleM : _metersPerRow;
            _axisOffset = row.AxisOffsetM;
            _peakThreshold = row.PeakThresholdDb;

            // 一次性：峰检测 + min/max 金字塔
            _peaks = double.IsNaN(_peakThreshold)
                ? new List<CurvePeak>()
                : CurveMetrics.FindPeaks(_levels, _peakThreshold,
                    row.PeakMergeGapRows > 0 ? row.PeakMergeGapRows : 50);
            BuildPyramid();

            Text = string.Format("距离域曲线 · IN{0} → OUT{1}（{2} 点 · 参考系 · 步距 {3:F4} mm）",
                row.InChannel, row.OutChannel, _levels.Length, _axisScale * 1000.0);
            ClientSize = new Size(1360, 600);
            MinimumSize = new Size(900, 460);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = PageBack;
            Font = _uiFont;
            DoubleBuffered = true;

            _viewStartM = AxisStartM;
            _viewEndM = AxisEndM;
            // 采集时指定的视窗优先（参考系，米）；无效则按全览
            if (row.ViewEndM > row.ViewStartM)
            {
                double viewStart = Math.Max(_viewStartM, row.ViewStartM);
                double viewEnd = Math.Min(_viewEndM, row.ViewEndM);
                if (viewEnd > viewStart)
                {
                    _viewStartM = viewStart;
                    _viewEndM = viewEnd;
                }
            }

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(10, 8, 10, 4),
                BackColor = CardBack
            };
            var fit = new Button
            {
                Text = "全览",
                Width = 72,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = CardBack,
                ForeColor = CurveColor,
                Font = _uiSmallFont,
                UseVisualStyleBackColor = false
            };
            fit.FlatAppearance.BorderColor = BorderColor;
            fit.Click += (s, e) =>
            {
                _viewStartM = AxisStartM;
                _viewEndM = AxisEndM;
                RequestRepaint();
            };
            bar.Controls.Add(fit);
            bar.Controls.Add(new Label
            {
                Text = "滚轮缩放 · 按住左键拖动平移",
                AutoSize = true,
                ForeColor = TextMuted,
                Padding = new Padding(10, 8, 0, 0),
                Font = _uiSmallFont
            });

            _canvas.Paint += OnPaint;
            _canvas.Resize += (s, e) => RequestRepaint();
            _canvas.MouseWheel += OnWheel;
            _canvas.MouseDown += OnMouseDown;
            _canvas.MouseMove += OnMouseMove;
            _canvas.MouseUp += (s, e) => _dragFrom = Point.Empty;

            _repaintTimer.Tick += (s, e) =>
            {
                _repaintTimer.Stop();
                _repaintPending = false;
                _canvas.Invalidate();
            };

            Controls.Add(_canvas);
            Controls.Add(BuildMetricsPanel());
            Controls.Add(_readout);
            Controls.Add(bar);
        }

        /// <summary>构建 min/max 分块金字塔（一次构建，缩放/平移复用）。</summary>
        void BuildPyramid()
        {
            int blocks = (_levels.Length + BlockSize - 1) / BlockSize;
            _blockMin = new double[Math.Max(1, blocks)];
            _blockMax = new double[Math.Max(1, blocks)];

            for (int b = 0; b < blocks; b++)
            {
                int from = b * BlockSize;
                int to = Math.Min(_levels.Length - 1, from + BlockSize - 1);
                double lo = double.MaxValue, hi = double.MinValue;
                for (int i = from; i <= to; i++)
                {
                    double v = _levels[i];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                _blockMin[b] = lo;
                _blockMax[b] = hi;
            }
        }

        /// <summary>取 [from,to] 的 min/max：长区间走块缓存，边缘走逐点。</summary>
        void GetRange(int from, int to, out double lo, out double hi)
        {
            lo = double.MaxValue;
            hi = double.MinValue;
            if (to < from) return;

            if (to - from + 1 <= BlockSize * 2)
            {
                ScanDirect(from, to, ref lo, ref hi);
                return;
            }

            int firstFull = ((from + BlockSize - 1) / BlockSize) * BlockSize;
            int lastFull = ((to + 1) / BlockSize) * BlockSize;
            if (firstFull > to || lastFull <= firstFull)
            {
                ScanDirect(from, to, ref lo, ref hi);
                return;
            }

            ScanDirect(from, firstFull - 1, ref lo, ref hi);
            for (int b = firstFull / BlockSize; b <= lastFull / BlockSize - 1; b++)
            {
                if (b < 0 || b >= _blockMin.Length) continue;
                if (_blockMin[b] < lo) lo = _blockMin[b];
                if (_blockMax[b] > hi) hi = _blockMax[b];
            }
            ScanDirect(lastFull, to, ref lo, ref hi);
        }

        void ScanDirect(int from, int to, ref double lo, ref double hi)
        {
            int end = Math.Min(to, _levels.Length - 1);
            for (int i = Math.Max(0, from); i <= end; i++)
            {
                double v = _levels[i];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
        }

        /// <summary>合并 16 ms 内的多次重绘请求，避免拖动时事件排队。</summary>
        void RequestRepaint()
        {
            if (_repaintPending || IsDisposed) return;
            _repaintPending = true;
            _repaintTimer.Start();
        }

        /// <summary>右侧指标卡：显示该路径的 11 列指标（失败时显示原因）。</summary>
        Panel BuildMetricsPanel()
        {
            var host = new Panel
            {
                Dock = DockStyle.Right,
                Width = 276,
                BackColor = PageBack,
                Padding = new Padding(8, 8, 12, 8)
            };
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBack,
                Padding = new Padding(16, 12, 16, 12)
            };
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(BorderColor))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                }
            };

            var title = new Label
            {
                Text = "指标",
                Dock = DockStyle.Top,
                Height = 28,
                Font = _uiTitleFont,
                ForeColor = Color.FromArgb(52, 64, 84)
            };
            card.Controls.Add(title);

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

            AddMetricRow(table, "IL-IN", _row.IlIn, "dB");
            AddMetricRow(table, "IL-DUT", _row.IlDut, "dB");
            AddMetricRow(table, "IL-OUT", _row.IlOut, "dB");
            AddMetricRow(table, "RL-IN", _row.RlIn, "dB_rel");
            AddMetricRow(table, "RL-DUT", _row.RlDut, "dB_rel");
            AddMetricRow(table, "RL-OUT", _row.RlOut, "dB_rel");
            AddMetricRow(table, "DUT 光程", _row.DutPathM, "m");
            AddMetricRow(table, "链路长度", _row.LinkLengthM, "m");

            var state = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 46,
                Padding = new Padding(0, 10, 0, 0),
                ForeColor = _row.Succeeded ? Color.FromArgb(22, 130, 93) : Color.FromArgb(190, 45, 45),
                Font = _uiSmallFont,
                Text = _row.Succeeded
                    ? string.Format("峰数 {0} · 有效采样 {1}", _peaks.Count, _row.ValidSamples)
                    : "不可计算：" + (_row.Failure ?? "未知原因")
            };

            card.Controls.Add(state);
            card.Controls.Add(table);
            host.Controls.Add(card);
            return host;
        }

        static void AddMetricRow(TableLayoutPanel table, string name, double value, string unit)
        {
            int index = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            table.Controls.Add(new Label
            {
                Text = name,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextMuted,
                Font = new Font("Microsoft YaHei", 8.5F)
            }, 0, index);
            table.Controls.Add(new Label
            {
                Text = double.IsNaN(value) ? "—" : value.ToString("F3", CultureInfo.InvariantCulture) + " " + unit,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(33, 37, 41),
                Font = new Font("Consolas", 9F)
            }, 1, index);
        }

        // ---------------- 绘制 ----------------

        void OnPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;

            int w = _canvas.Width - PlotLeft - PlotRight;
            int h = _canvas.Height - PlotTop - PlotBottom;
            if (w <= 10 || h <= 10 || _levels.Length == 0) return;

            int i0 = Math.Max(0, (int)Math.Floor((_viewStartM - _axisOffset) / _axisScale));
            int i1 = Math.Min(_levels.Length - 1, (int)Math.Ceiling((_viewEndM - _axisOffset) / _axisScale));
            if (i1 <= i0) return;

            double min, max;
            GetRange(i0, i1, out min, out max);
            if (min > max) return;

            double pad = Math.Max(2.0, (max - min) * 0.08);
            min -= pad;
            max += pad;
            if (max - min < 1) max = min + 1;

            var plot = new Rectangle(PlotLeft, PlotTop, w, h);

            // 网格与刻度
            for (int k = 0; k <= 5; k++)
            {
                int y = plot.Top + h * k / 5;
                g.DrawLine(_gridPen, plot.Left, y, plot.Right, y);
                double v = max - (max - min) * k / 5.0;
                g.DrawString(v.ToString("F0", CultureInfo.InvariantCulture) + " dB", _axisFont, _mutedBrush, 6, y - 7);
            }
            for (int k = 0; k <= 6; k++)
            {
                int x = plot.Left + w * k / 6;
                g.DrawLine(_gridPen, x, plot.Top, x, plot.Bottom);
                double d = _viewStartM + (_viewEndM - _viewStartM) * k / 6.0;
                string label = d.ToString("F2", CultureInfo.InvariantCulture) + " m";
                SizeF size = g.MeasureString(label, _axisFont);
                g.DrawString(label, _axisFont, _mutedBrush, x - size.Width / 2, plot.Bottom + 6);
            }
            g.DrawRectangle(_axisPen, plot);

            // 曲线：每像素列聚合一次 min/max
            var previous = new PointF(float.NaN, float.NaN);
            for (int x = 0; x < w; x++)
            {
                int from = i0 + (int)((double)(i1 - i0 + 1) * x / w);
                int to = i0 + (int)((double)(i1 - i0 + 1) * (x + 1) / w) - 1;
                if (to < from) to = from;
                if (from > i1) break;

                double lo, hi;
                GetRange(from, Math.Min(to, i1), out lo, out hi);
                if (hi < lo) continue;

                float yHi = ToY(hi, min, max, plot);
                float yLo = ToY(lo, min, max, plot);
                var point = new PointF(plot.Left + x, yHi);
                if (!float.IsNaN(previous.X)) g.DrawLine(_curvePen, previous, point);
                if (yLo > yHi) g.DrawLine(_curvePen, plot.Left + x, yHi, plot.Left + x, yLo);
                previous = new PointF(plot.Left + x, yLo);
            }

            // 峰标记（缓存结果，绘制不再计算）
            for (int i = 0; i < _peaks.Count; i++)
            {
                int idx = _peaks[i].Index;
                if (idx < i0 || idx > i1) continue;
                float x = plot.Left + (float)((double)(idx - i0) / (i1 - i0) * w);
                float y = ToY(_peaks[i].Db, min, max, plot);
                g.DrawLine(_peakPen, x, plot.Top, x, plot.Bottom);
                g.FillEllipse(_peakBrush, x - 2.5f, y - 2.5f, 5, 5);
                if (i < 16)
                {
                    DrawPeakLabel(g, plot, x, y, i + 1,
                        _axisOffset + _peaks[i].Index * _axisScale, _peaks[i].Db);
                }
            }

            // 阈值线
            if (!double.IsNaN(_peakThreshold) && _peakThreshold > min && _peakThreshold < max)
            {
                float y = ToY(_peakThreshold, min, max, plot);
                g.DrawLine(_thresholdPen, plot.Left, y, plot.Right, y);
                g.DrawString("阈值 " + _peakThreshold.ToString("F1", CultureInfo.InvariantCulture) + " dB",
                    _peakFont, _mutedBrush, plot.Right - 96, y - 13);
            }
        }

        static float ToY(double value, double min, double max, Rectangle plot)
        {
            double ratio = (value - min) / (max - min);
            return (float)(plot.Bottom - ratio * plot.Height);
        }

        /// <summary>峰标签：P 编号 + 距离(m) + 电平(dB_rel)，纯文字，不加底色或边框。</summary>
        void DrawPeakLabel(Graphics g, Rectangle plot, float x, float y, int number, double distanceM, double levelDb)
        {
            string[] lines =
            {
                "P" + number.ToString(CultureInfo.InvariantCulture),
                distanceM.ToString("F3", CultureInfo.InvariantCulture) + " m",
                levelDb.ToString("F1", CultureInfo.InvariantCulture) + " dB"
            };

            float maxWidth = 0;
            float lineHeight = 0;
            foreach (string line in lines)
            {
                SizeF size = g.MeasureString(line, _peakFont);
                if (size.Width > maxWidth) maxWidth = size.Width;
                if (size.Height > lineHeight) lineHeight = size.Height;
            }

            float textX = x + 6;
            if (textX + maxWidth > plot.Right) textX = x - maxWidth - 6;
            if (textX < plot.Left) textX = plot.Left + 2;

            float textY = y - lineHeight * lines.Length - 6;
            if (textY < plot.Top) textY = y + 6;
            if (textY + lineHeight * lines.Length > plot.Bottom) textY = plot.Bottom - lineHeight * lines.Length - 2;

            for (int i = 0; i < lines.Length; i++)
            {
                g.DrawString(lines[i], _peakFont, _peakBrush, textX, textY + i * lineHeight);
            }
        }

        // ---------------- 交互 ----------------

        /// <summary>可显示的最左位置：参考系 0（设备连出口）；LGA50 亦为 0。</summary>
        double AxisStartM
        {
            get { return Math.Max(0.0, _axisOffset); }
        }

        /// <summary>可显示的最右位置：最后一个采样点对应的参考系距离。</summary>
        double AxisEndM
        {
            get
            {
                double end = _axisOffset + Math.Max(0, _levels.Length - 1) * _axisScale;
                return Math.Max(AxisStartM + 0.001, end);
            }
        }

        void OnWheel(object sender, MouseEventArgs e)
        {
            double span = _viewEndM - _viewStartM;
            if (span <= 0) return;

            double factor = e.Delta > 0 ? 0.8 : 1.25;
            double newSpan = span * factor;
            double axisStart = AxisStartM;
            double total = AxisEndM;
            newSpan = Math.Min(total - axisStart, Math.Max((total - axisStart) / 100.0, newSpan));

            double center = _viewStartM + span * ((double)e.X / Math.Max(1, _canvas.Width));
            double start = center - newSpan / 2;
            double end = start + newSpan;

            if (start < axisStart) { start = axisStart; end = start + newSpan; }
            if (end > total) { end = total; start = Math.Max(axisStart, total - newSpan); }

            _viewStartM = start;
            _viewEndM = end;
            RequestRepaint();
        }

        void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragFrom = e.Location;
            _dragStartM = _viewStartM;
            _dragEndM = _viewEndM;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragFrom != Point.Empty && e.Button == MouseButtons.Left)
            {
                double span = _dragEndM - _dragStartM;
                double shift = -(double)(e.X - _dragFrom.X) / Math.Max(1, _canvas.Width) * span;
                double axisStart = AxisStartM;
                double total = AxisEndM;
                double start = _dragStartM + shift;
                start = Math.Max(axisStart, Math.Min(total - span, start));
                _viewStartM = start;
                _viewEndM = start + span;
                RequestRepaint();
                return;
            }

            int w = _canvas.Width - PlotLeft - PlotRight;
            int h = _canvas.Height - PlotTop - PlotBottom;
            if (w <= 0 || h <= 0 || e.X < PlotLeft || e.X > PlotLeft + w || e.Y < PlotTop || e.Y > PlotTop + h) return;

            double d = _viewStartM + (_viewEndM - _viewStartM) * (double)(e.X - PlotLeft) / w;
            int index = (int)Math.Round((d - _axisOffset) / _axisScale);
            if (index < 0 || index >= _levels.Length) return;

            _readout.Text = string.Format(CultureInfo.InvariantCulture,
                "距离 {0:F3} m   点序号 {1}   电平 {2:F2} dB_rel", d, index, _levels[index]);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _repaintTimer.Dispose();
                _axisFont.Dispose();
                _peakFont.Dispose();
                _uiFont.Dispose();
                _uiSmallFont.Dispose();
                _uiTitleFont.Dispose();
                _gridPen.Dispose();
                _axisPen.Dispose();
                _curvePen.Dispose();
                _peakPen.Dispose();
                _thresholdPen.Dispose();
                _curveBrush.Dispose();
                _peakBrush.Dispose();
                _mutedBrush.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>开启双缓冲的子面板，避免重绘闪烁。</summary>
        sealed class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint, true);
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }
    }
}
