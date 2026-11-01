using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using OFDR.Contracts;
using OFDR.Metrics;

namespace OFDR.App;

/// <summary>
/// 距离域回损曲线：min/max 包络降采样 + 双光标 + 事件刻度。
/// 只读展示，数据由 UI 线程灌入；不持有扫描对象，只持有数值数组引用。
/// </summary>
public sealed class DistancePlot : Control
{
    private const int MarginLeft = 58;
    private const int MarginRight = 14;
    private const int MarginTop = 50;
    private const int MarginBottom = 32;

    private readonly Font _tickFont = new("Microsoft YaHei", 8F);
    private readonly Font _readoutFont = new("Microsoft YaHei", 9F, FontStyle.Bold);

    private ScanAxis? _axis;
    private double[]? _values;
    private IReadOnlyList<DistanceEvent>? _events;

    private double[] _min = Array.Empty<double>();
    private double[] _max = Array.Empty<double>();
    private int _envelopeBuckets;
    private double[]? _envelopeSource;

    private double? _cursorA;
    private double? _cursorB;
    private bool _cursorInit;
    private int _dragCursor = -1;

    private double _yMin = -75;
    private double _yMax = -40;

    public DistancePlot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Cursor = Cursors.Cross;
    }

    // ---------------- 数据灌入 ----------------

    /// <summary>灌入一帧曲线（数值统一为 dBm）。光标只在首次有事件时初始化，之后由用户控制。</summary>
    public void SetTrace(ScanAxis axis, double[] dbm)
    {
        _axis = axis;
        _values = dbm;
        _envelopeSource = null; // 强制重算包络
        Invalidate();
    }

    public void SetEvents(IReadOnlyList<DistanceEvent> events)
    {
        _events = events;
        if (!_cursorInit && _axis is { IsValid: true })
        {
            _cursorA = events.Count > 0 ? events[0].DistanceM : _axis.Start;
            _cursorB = events.Count > 0 ? events[^1].DistanceM : _axis.End;
            _cursorInit = events.Count > 0;
        }
        Invalidate();
    }

    /// <summary>把光标 A 移到指定距离（事件表双击用）。</summary>
    public void FocusOn(double distanceM)
    {
        if (_axis is not { IsValid: true }) return;
        _cursorA = Math.Clamp(distanceM, _axis.Start, _axis.End);
        _cursorInit = true;
        Invalidate();
    }

    public void Clear()
    {
        _axis = null;
        _values = null;
        _events = null;
        _min = Array.Empty<double>();
        _max = Array.Empty<double>();
        _envelopeSource = null;
        _envelopeBuckets = 0;
        _cursorA = null;
        _cursorB = null;
        _cursorInit = false;
        _dragCursor = -1;
        Invalidate();
    }

    // ---------------- 绘制 ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        var plot = PlotRect();
        if (plot.Width < 40 || plot.Height < 40) return;

        if (_axis is not { IsValid: true } || _values is not { Length: > 0 })
        {
            using var idle = new SolidBrush(Color.FromArgb(150, 150, 150));
            g.DrawString("等待数据…", Font, idle, plot.Left + 8, plot.Top + plot.Height / 2f);
            return;
        }

        // 光标初值兜底：没有事件时也给出边界读数
        _cursorA ??= _axis.Start;
        _cursorB ??= _axis.End;

        EnsureEnvelope(plot.Width);
        ComputeYRange();

        DrawGrid(g, plot, _axis);
        DrawCurve(g, plot);
        DrawEvents(g, plot, _axis);
        DrawCursors(g, plot, _axis);
        DrawReadout(g, plot);
    }

    private Rectangle PlotRect() => new(
        MarginLeft, MarginTop,
        Math.Max(0, Width - MarginLeft - MarginRight),
        Math.Max(0, Height - MarginTop - MarginBottom));

    private void EnsureEnvelope(int buckets)
    {
        if (_values == null) return;
        if (ReferenceEquals(_envelopeSource, _values) && _envelopeBuckets == buckets) return;
        TraceEnvelope.MinMax(_values, buckets, out _min, out _max);
        _envelopeSource = _values;
        _envelopeBuckets = buckets;
    }

    private void ComputeYRange()
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i < _min.Length; i++)
        {
            if (double.IsFinite(_min[i]) && _min[i] < lo) lo = _min[i];
            if (double.IsFinite(_max[i]) && _max[i] > hi) hi = _max[i];
        }
        if (!double.IsFinite(lo) || !double.IsFinite(hi))
        {
            _yMin = -75;
            _yMax = -40;
            return;
        }
        if (hi - lo < 10)
        {
            double c = (hi + lo) / 2;
            lo = c - 5;
            hi = c + 5;
        }
        _yMin = Math.Floor((lo - 2) / 5) * 5;
        _yMax = Math.Ceiling((hi + 2) / 5) * 5;
    }

    private void DrawGrid(Graphics g, Rectangle plot, ScanAxis axis)
    {
        using var gridPen = new Pen(Color.FromArgb(236, 239, 242));
        using var framePen = new Pen(Color.FromArgb(190, 196, 202));
        using var textBrush = new SolidBrush(Color.FromArgb(96, 96, 96));

        // 刻度步长随绘图区高度自适应，避免窗口变矮时标签互相重叠
        double yStep = NiceStep(_yMax - _yMin, Math.Max(2, plot.Height / 26));
        for (double y = _yMin; y <= _yMax + 1e-9; y += yStep)
        {
            int py = YOf(y, plot);
            g.DrawLine(gridPen, plot.Left, py, plot.Right - 1, py);
            g.DrawString(y.ToString("F0", CultureInfo.InvariantCulture), _tickFont, textBrush, plot.Left - 40, py - 8);
        }

        double span = axis.End - axis.Start;
        if (span > 0)
        {
            double xStep = NiceStep(span, 7);
            double x0 = Math.Ceiling(axis.Start / xStep) * xStep;
            for (double x = x0; x <= axis.End + 1e-9; x += xStep)
            {
                int px = XOf(x, plot, axis);
                g.DrawLine(gridPen, px, plot.Top, px, plot.Bottom - 1);
                string label = Math.Abs(xStep - Math.Round(xStep)) > 1e-9
                    ? x.ToString("0.#", CultureInfo.InvariantCulture)
                    : x.ToString("F0", CultureInfo.InvariantCulture);
                float w = g.MeasureString(label, _tickFont).Width;
                g.DrawString(label, _tickFont, textBrush, px - w / 2, plot.Bottom + 6);
            }
            g.DrawString("距离 (m)", _tickFont, textBrush, plot.Right - 52, plot.Top - 16);
        }

        g.DrawRectangle(framePen, plot);
        g.DrawString("回损 (dBm)", _tickFont, textBrush, plot.Left, plot.Top - 16);
    }

    private void DrawCurve(Graphics g, Rectangle plot)
    {
        int n = _max.Length;
        if (n < 2) return; // 多边形至少 3 点、折线至少 2 点

        var upper = new PointF[n];
        var lower = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            float x = plot.Left + (i + 0.5f) * plot.Width / n;
            double hi = double.IsFinite(_max[i]) ? _max[i] : _yMin;
            double lo = double.IsFinite(_min[i]) ? _min[i] : _yMin;
            upper[i] = new PointF(x, YOf(hi, plot));
            lower[i] = new PointF(x, YOf(lo, plot));
        }

        var state = g.Save();
        g.SetClip(plot);
        try
        {
            var band = new PointF[n * 2];
            Array.Copy(upper, 0, band, 0, n);
            for (int i = 0; i < n; i++) band[n + i] = lower[n - 1 - i];

            using var fill = new SolidBrush(Color.FromArgb(26, 31, 119, 180));
            g.FillPolygon(fill, band);

            using var pen = new Pen(Color.FromArgb(31, 119, 180), 1.4f);
            pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, upper);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private void DrawEvents(Graphics g, Rectangle plot, ScanAxis axis)
    {
        if (_events is not { Count: > 0 }) return;

        using var pen = new Pen(Color.FromArgb(150, 168, 172), 1f) { DashStyle = DashStyle.Dot };
        using var brush = new SolidBrush(Color.FromArgb(150, 168, 172));
        foreach (var ev in _events)
        {
            int px = XOf(ev.DistanceM, plot, axis);
            if (px < plot.Left || px > plot.Right) continue;
            g.DrawLine(pen, px, plot.Top, px, plot.Bottom - 1);
            g.FillPolygon(brush, new[]
            {
                new Point(px - 3, plot.Top - 6),
                new Point(px + 3, plot.Top - 6),
                new Point(px, plot.Top - 1),
            });
        }
    }

    private void DrawCursors(Graphics g, Rectangle plot, ScanAxis axis)
    {
        if (_cursorA is null && _cursorB is null) return;

        using var penA = new Pen(Color.FromArgb(192, 57, 43), 1.2f) { DashStyle = DashStyle.Dash };
        using var penB = new Pen(Color.FromArgb(39, 124, 74), 1.2f) { DashStyle = DashStyle.Dash };

        if (_cursorA is { } a) DrawCursor(g, plot, axis, penA, Color.FromArgb(192, 57, 43), a, "A");
        if (_cursorB is { } b) DrawCursor(g, plot, axis, penB, Color.FromArgb(39, 124, 74), b, "B");
    }

    private void DrawCursor(Graphics g, Rectangle plot, ScanAxis axis, Pen pen, Color color, double distance, string label)
    {
        int px = XOf(distance, plot, axis);
        if (px < plot.Left || px > plot.Right) return;

        g.DrawLine(pen, px, plot.Top, px, plot.Bottom - 1);
        using var chip = new SolidBrush(color);
        g.FillRectangle(chip, px - 7, plot.Top + 2, 14, 14);
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString(label, _tickFont, textBrush, px - 4, plot.Top + 1);
    }

    private void DrawReadout(Graphics g, Rectangle plot)
    {
        using var brush = new SolidBrush(Color.FromArgb(70, 70, 70));
        double aDb = ValueAt(_cursorA);
        double bDb = ValueAt(_cursorB);

        string a = _cursorA is { } av ? $"{av:F3} m / {FmtDb(aDb)}" : "—";
        string b = _cursorB is { } bv ? $"{bv:F3} m / {FmtDb(bDb)}" : "—";
        string delta = double.IsNaN(aDb) || double.IsNaN(bDb) ? "—" : $"{aDb - bDb:F1} dB";
        string gap = _cursorA is { } x && _cursorB is { } y ? $"{Math.Abs(y - x):F3} m" : "—";

        string text = $"A  {a}      B  {b}      ΔRL  {delta}      间距  {gap}";
        g.DrawString(text, _readoutFont, brush, plot.Left - 2, 12);
    }

    // ---------------- 坐标换算 ----------------

    private int YOf(double db, Rectangle plot) =>
        plot.Bottom - (int)Math.Round((db - _yMin) / (_yMax - _yMin) * plot.Height);

    private static int XOf(double distance, Rectangle plot, ScanAxis axis)
    {
        double span = axis.End - axis.Start;
        if (!(span > 0)) return plot.Left;
        double ratio = Math.Clamp((distance - axis.Start) / span, 0, 1);
        return plot.Left + (int)Math.Round(ratio * plot.Width);
    }

    private double ValueAt(double? distance)
    {
        if (_axis is not { IsValid: true } || _values is not { Length: > 0 } || distance is null)
            return double.NaN;

        int idx = (int)Math.Round((distance.Value - _axis.Start) / _axis.Step);
        idx = Math.Clamp(idx, 0, _values.Length - 1);
        return _values[idx];
    }

    private static string FmtDb(double v) =>
        double.IsNaN(v) ? "—" : v.ToString("F1", CultureInfo.InvariantCulture);

    private static double NiceStep(double span, int targetTicks)
    {
        if (!(span > 0) || targetTicks <= 0) return 1;
        double raw = span / targetTicks;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 2.5 : 5;
        return nice * mag;
    }

    // ---------------- 鼠标交互 ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_axis is not { IsValid: true } || _cursorA is null || _cursorB is null) return;

        var plot = PlotRect();
        if (plot.Width < 40) return;
        // 只在绘图区内起拖，避免点击读数条或轴标签时误移光标
        if (e.Y < plot.Top || e.Y > plot.Bottom) return;

        int ax = XOf(_cursorA.Value, plot, _axis);
        int bx = XOf(_cursorB.Value, plot, _axis);
        if (Math.Abs(e.X - ax) <= 8) _dragCursor = 0;
        else if (Math.Abs(e.X - bx) <= 8) _dragCursor = 1;
        else _dragCursor = Math.Abs(e.X - ax) <= Math.Abs(e.X - bx) ? 0 : 1;

        _cursorInit = true;
        SetCursorFromPixel(_dragCursor, e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragCursor >= 0 && e.Button == MouseButtons.Left)
        {
            SetCursorFromPixel(_dragCursor, e.X);
            return;
        }

        if (_axis is not { IsValid: true } || _cursorA is null || _cursorB is null)
        {
            Cursor = Cursors.Cross;
            return;
        }

        var plot = PlotRect();
        int ax = XOf(_cursorA.Value, plot, _axis);
        int bx = XOf(_cursorB.Value, plot, _axis);
        Cursor = Math.Min(Math.Abs(e.X - ax), Math.Abs(e.X - bx)) <= 8 ? Cursors.Hand : Cursors.Cross;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragCursor = -1;
    }

    private void SetCursorFromPixel(int which, int x)
    {
        if (_axis is not { IsValid: true }) return;
        var plot = PlotRect();
        if (plot.Width <= 0) return;

        double span = _axis.End - _axis.Start;
        if (!(span > 0)) return;

        double ratio = Math.Clamp((x - plot.Left) / (double)plot.Width, 0, 1);
        double d = _axis.Start + ratio * span;
        if (which == 0) _cursorA = d;
        else _cursorB = d;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tickFont.Dispose();
            _readoutFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
