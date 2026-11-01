using System.Drawing;
using System.Windows.Forms;

namespace OFDRDataReceiver;

/// <summary>
/// 测试指标表格（基于 DataGridView）—— 8 组 × 每组 8 行 = 64 通道组合（IN 1~8 × OUT 1~8）。
///
/// 为避免 WinForms 多级列头（ColumnHeaders）自绘时高度被裁剪、第 2/3 级被挤出可见区等已知问题，
/// 本实现隐藏原生列头，改用前 3 行“冻结行”充当表头，并在 CellPainting 中合并绘制。
///
/// 表头三行（Frozen，不参与滚动）：
///   Row0（第1级）：测试组别 | 测试通道(跨2列) | 测试指标(跨6列) | 光程 | 链路长度
///   Row1（第2级）：IN端口(向下合并2行) | OUT端口(向下合并2行) | IL-IN | IL-DUT | IL-OUT | RL-IN | RL-DUT | RL-OUT | 光程 | 链路长度
///   Row2（第3级）：(空，被 Row1 的 IN端口/OUT端口 及各指标列纵向合并覆盖)
///
/// 共 11 列；数据区 64 行，从 Row3 开始。组别列每 8 行垂直合并。
///
/// 【重要】组别列（col0）的垂直合并绘制必须放在 RowPostPaint 中完成，绝不可移到 CellPainting ——
/// 冻结列/滚动时 CellPainting 行为异常会导致组号重复、偏移或消失。
/// </summary>
// 注意：不要加 sealed —— WinForms 设计器需要继承控件来创建设计宿主，
// sealed 会导致包含本控件的窗体在设计器中无法显示。
internal class MetricTable : DataGridView
{
    public const int GroupCount = 8;      // IN 端口数（测试组别数）
    public const int RowsPerGroup = 8;    // OUT 端口数（每组行数）
    public const int HeaderRows = 3;      // 前 3 行作为表头
    public const int TotalDataRows = GroupCount * RowsPerGroup;   // 64

    // 列索引常量（供外部按名写入，避免魔法数字散落）
    public const int ColGroup = 0;
    public const int ColInPort = 1;
    public const int ColOutPort = 2;
    public const int ColIlIn = 3;
    public const int ColIlDut = 4;             // IL-DUT：单列（与 IL-IN 同，显示最新值或采样均值）
    public const int ColIlOut = 5;
    public const int ColRlIn = 6;
    public const int ColRlDut = 7;             // RL-DUT：单列（与 RL-IN 同）
    public const int ColRlOut = 8;
    public const int ColPathLen = 9;
    public const int ColLinkLen = 10;          // 链路长度（单列，与光程列类似直接显示计算值）

    /// <summary>第 1 级合并段（Row0）：起始列、跨列数、标题。</summary>
    private static readonly (int Start, int Span, string Text)[] TopSpans = new[]
    {
        (0, 1, "测试组别"),
        (1, 2, "测试通道"),
        (3, 6, "测试指标"),         // IL-IN / IL-DUT / IL-OUT / RL-IN / RL-DUT / RL-OUT
        (9, 1, "光程"),            // 光程独立列
        (10, 1, "链路长度"),        // 链路长度独立列（与光程列类似）
    };

    /// <summary>第 2 级合并段（Row1）：起始列、跨列数、标题。</summary>
    private static readonly (int Start, int Span, string Text)[] MidSpans = new[]
    {
        (1, 1, "IN端口"),      // 1  与上方 Row0「测试通道」对应，向下合并两行（Row1+Row2）
        (2, 1, "OUT端口"),     // 2  同上
        (3, 1, "IL-IN"),      // 3
        (4, 1, "IL-DUT"),     // 4
        (5, 1, "IL-OUT"),     // 5
        (6, 1, "RL-IN"),      // 6
        (7, 1, "RL-DUT"),     // 7
        (8, 1, "RL-OUT"),     // 8
        (9, 1, "光程"),       // 9
        (10, 1, "链路长度"),  // 10
    };

    /// <summary>
    /// 第 3 级子项名（Row2，11 列）。空串表示该列第 3 级无独立子项，
    /// 由第 2 级标题纵向合并覆盖两行显示（IN端口/OUT端口/各指标列均如此）。
    /// IL-DUT / RL-DUT 已改为单列（与 IL-IN 相同），不再有 Max/Min/Avg/Cur 子项。
    /// </summary>
    private static readonly string[] SubNames = new[]
    {
        "",                                      // 0  组别（第1级跨3行）
        "", "",                                  // 1, 2  IN端口 / OUT端口：由 Row1 的 MidSpan 向下合并覆盖，此处留空
        "", "", "", "", "", "",                   // 3-8   IL-IN / IL-DUT / IL-OUT / RL-IN / RL-DUT / RL-OUT（均第2级合并覆盖）
        "",                                      // 9  光程（第2级已显示）
        "",                                      // 10 链路长度（第2级已显示）
    };

    /// <summary>
    /// 11 列「基准宽度」。它同时承担两个作用：
    ///   1) 作为 MinimumWidth —— 窄窗口下列不会被压到看不清（改为出现横向滚动条）；
    ///   2) 作为 FillWeight  —— AutoSizeColumnsMode.Fill 会按其比例把当前可用宽度
    ///      分配到各列，使表格随分辨率/窗口大小自动铺满，
    ///      不会像固定像素那样在宽屏上留大片空白。
    /// </summary>
    private static readonly int[] ColWidths = new[]
    {
        70,                    // 0  测试组别
        55, 55,                // 1  IN端口, 2 OUT端口
        85,                    // 3  IL-IN
        85,                    // 4  IL-DUT
        85,                    // 5  IL-OUT
        85,                    // 6  RL-IN
        85,                    // 7  RL-DUT
        85,                    // 8  RL-OUT
        70,                    // 9  光程（独立列，宽度稍大）
        70,                    // 10 链路长度（独立列，宽度稍大）
    };

    private readonly Color _headerBack = Color.FromArgb(222, 235, 247);
    private readonly Color _groupBack = Color.FromArgb(218, 230, 245);
    private readonly Color _lineColor = Color.FromArgb(160, 170, 185);
    private readonly Color _activeBack = Color.FromArgb(255, 249, 219);   // 当前正在测试的行

    /// <summary>
    /// 表头行高：不再用固定 28px，改由构造函数按字体推导（随 DPI 缩放自适应），
    /// 避免高 DPI 下字体变大后表头文字被裁切。
    /// </summary>
    private int _headerRowHeight;

    /// <summary>每行的 IL-DUT / RL-DUT 时序统计累加器（64 行）。</summary>
    private readonly RowAcc[] _acc = new RowAcc[TotalDataRows];

    /// <summary>当前正在测试的数据行（0~63），-1 表示无；用于高亮。</summary>
    private int _activeRow = -1;

    /// <summary>采样数&gt;1 时为 true：IL-IN/IL-OUT/RL-IN/RL-OUT 四列显示多次采样的平均值，否则显示最新值。</summary>
    public bool ShowAverage { get; set; }

    public MetricTable()
    {
        Dock = DockStyle.Fill;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        AllowUserToResizeColumns = false;
        AllowUserToOrderColumns = false;
        ReadOnly = true;
        RowHeadersVisible = false;
        ColumnHeadersVisible = false;                 // 关键：隐藏原生列头，改用冻结行
        MultiSelect = false;
        SelectionMode = DataGridViewSelectionMode.CellSelect;

        ScrollBars = ScrollBars.Both;
        // Fill：各列按 FillWeight（= ColWidths）比例分配可用宽度，
        // 随分辨率/窗口大小自动铺满；可用宽度不足合计 MinimumWidth 时出现横向滚动条。
        // 这样表格在 1366 / 1920 / 2560 宽及任意 DPI 下都能自适应。
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        DefaultCellStyle.BackColor = Color.White;
        BackgroundColor = Color.White;
        // 关闭默认网格线，全部边框由 CellPainting 自绘（否则合并单元格中间会被控件横线穿过）
        GridColor = _lineColor;
        CellBorderStyle = DataGridViewCellBorderStyle.None;
        BorderStyle = BorderStyle.FixedSingle;

        // 字体必须先赋值：下面的行高 / 表头行高都由 Font.Height 推导（随 DPI 缩放）。
        // 数据单元格不单独指定 DefaultCellStyle.Font，直接继承本控件的 Font，
        // 这样 DPI 缩放时单元格字体能跟着一起放大。
        Font = new Font("Microsoft YaHei", 9F);

        // 行高由字体推导（随 DPI 缩放），不再写死 26px
        RowTemplate.Height = Font.Height + 10;
        // 表头行高同样按字体推导，略高于数据行
        _headerRowHeight = Font.Height + 14;

        BuildColumnsAndRows();

        // 冻结前 3 行表头（多级表头随垂直滚动应保持可见）。
        // 注意：不要冻结 col0 —— 冻结列的绘制行为异常会导致组号重复/偏移/消失。
        for (int h = 0; h < HeaderRows; h++)
        {
            Rows[h].Frozen = true;
            Rows[h].Height = _headerRowHeight;
            Rows[h].MinimumHeight = _headerRowHeight;   // 硬性最小高度，避免父容器挤压时文字被裁切
        }

        CellPainting += OnCellPainting;
        RowPostPaint += OnRowPostPaint;
        // 滚动时强制重绘 col0，确保组号随滚动位置正确刷新
        Scroll += (_, __) => InvalidateColumn(ColGroup);
    }

    private void BuildColumnsAndRows()
    {
        for (int i = 0; i < ColWidths.Length; i++)
        {
            Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "col" + i,
                Width = ColWidths[i],
                MinimumWidth = ColWidths[i],
                // FillWeight = 基准宽度：配合 AutoSizeColumnsMode.Fill，
                // 各列按此比例分摊可用宽度，实现随分辨率自动铺满
                FillWeight = ColWidths[i],
                SortMode = DataGridViewColumnSortMode.NotSortable,
                Resizable = DataGridViewTriState.False,
            });
        }

        // 表头 3 行：文字由 OnCellPainting 自绘，此处仅建立空行
        for (int h = 0; h < HeaderRows; h++)
        {
            int idx = Rows.Add();
            Rows[idx].ReadOnly = true;
            Rows[idx].DefaultCellStyle.BackColor = _headerBack;
            Rows[idx].DefaultCellStyle.ForeColor = Color.Black;
            // 表头字体基于控件当前 Font（已按 DPI 缩放）派生，而非写死 9F
            Rows[idx].DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            Rows[idx].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }

        // 数据区：8 组 × 8 行
        for (int g = 1; g <= GroupCount; g++)
        {
            for (int r = 0; r < RowsPerGroup; r++)
            {
                int idx = Rows.Add();
                Rows[idx].Cells[ColGroup].Value = (r == 0) ? g.ToString() : null;   // 组别列：仅首行写组号
                Rows[idx].Cells[ColInPort].Value = g.ToString();                     // IN端口 = 测试组别
                Rows[idx].Cells[ColOutPort].Value = (r + 1).ToString();              // OUT端口 1~8
            }
        }
    }

    // ---------------- 对外 API ----------------

    /// <summary>数据区相对行号：IN 端口 inPort(1~8)、OUT 端口 outPort(1~8) → 0~63。</summary>
    public static int RowOf(int inPort, int outPort) => (inPort - 1) * RowsPerGroup + (outPort - 1);

    /// <summary>设置当前正在测试的行（高亮），传 -1 取消高亮。</summary>
    public void SetActiveRow(int dataRow)
    {
        if (dataRow == _activeRow) return;
        int old = _activeRow;
        _activeRow = dataRow;
        InvalidateDataRow(old);
        InvalidateDataRow(_activeRow);
    }

    /// <summary>
    /// 用一帧计算结果更新指定通道行：
    ///   - IL-IN / IL-OUT / RL-IN / RL-OUT / IL-DUT / RL-DUT：均为单列，
    ///     采样数>1 时显示均值（ShowAverage），否则显示最新值（与 IL-IN 同语义）。
    /// reset=true 时清空该行累加器（切换到新通道时调用）。
    /// </summary>
    public void UpdateRow(int inPort, int outPort,
        double ilIn, double ilDut, double ilOut,
        double rlIn, double rlDut, double rlOut,
        double pathLen, double linkLen, bool reset = false)
    {
        int dataRow = RowOf(inPort, outPort);
        if (dataRow < 0 || dataRow >= TotalDataRows) return;
        int rowIndex = HeaderRows + dataRow;
        if (rowIndex >= Rows.Count) return;

        if (reset) _acc[dataRow] = default;

        ref RowAcc a = ref _acc[dataRow];
        a.Push(ilIn, ilOut, ilDut, rlIn, rlDut, rlOut);

        var row = Rows[rowIndex];
        // IL-IN/IL-OUT/RL-IN/RL-OUT：采样数>1 时显示平均值（ShowAverage），否则显示最新值
        row.Cells[ColIlIn].Value = Fmt(ShowAverage ? a.IlInAvg : ilIn);
        row.Cells[ColIlOut].Value = Fmt(ShowAverage ? a.IlOutAvg : ilOut);
        row.Cells[ColRlIn].Value = Fmt(ShowAverage ? a.RlInAvg : rlIn);
        row.Cells[ColRlOut].Value = Fmt(ShowAverage ? a.RlOutAvg : rlOut);

        // IL-DUT / RL-DUT：单列，与 IL-IN 同语义（采样数>1 时显示均值，否则最新值）
        row.Cells[ColIlDut].Value = Fmt(ShowAverage ? a.IlAvg : ilDut);
        row.Cells[ColRlDut].Value = Fmt(ShowAverage ? a.RlAvg : rlDut);

        row.Cells[ColPathLen].Value = Fmt(pathLen);
        row.Cells[ColLinkLen].Value = Fmt(linkLen);
    }

    /// <summary>清空所有指标单元格与统计累加器（新一轮测试开始时调用）。</summary>
    public void ClearAllMetrics()
    {
        for (int d = 0; d < TotalDataRows; d++)
        {
            _acc[d] = default;
            int rowIndex = HeaderRows + d;
            if (rowIndex >= Rows.Count) continue;
            var row = Rows[rowIndex];
            for (int c = ColIlIn; c <= ColLinkLen; c++)
                row.Cells[c].Value = null;
        }
        _activeRow = -1;
        Invalidate();
    }

    private void InvalidateDataRow(int dataRow)
    {
        if (dataRow < 0 || dataRow >= TotalDataRows) return;
        int rowIndex = HeaderRows + dataRow;
        if (rowIndex >= Rows.Count) return;
        InvalidateRow(rowIndex);
    }

    private static string Fmt(double v) => double.IsNaN(v) ? "" : v.ToString("F3");

    // ---------------- 绘制 ----------------

    private void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.ColumnIndex < 0) return;

        var g = e.Graphics;
        using var pen = new Pen(_lineColor);
        using var headerBrush = new SolidBrush(_headerBack);

        // ---- 表头三行：合并段绘制 ----
        if (e.RowIndex >= 0 && e.RowIndex < HeaderRows)
        {
            // 表头文字字体：基于控件当前 Font 派生（已随 DPI 缩放），而非写死 9F
            using var font = new Font(Font, FontStyle.Bold);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None,
            };

            var rect = e.CellBounds;
            var spans = e.RowIndex == 0 ? TopSpans : (e.RowIndex == 1 ? MidSpans : null);

            if (spans != null)
            {
                // 当前列是否落在某段的“内部”（非起始列）
                bool inBody = false;
                foreach (var (start, span, text) in spans)
                    if (!string.IsNullOrEmpty(text) && e.ColumnIndex > start && e.ColumnIndex < start + span)
                    { inBody = true; break; }

                if (inBody)
                {
                    // 内部列：完全跳过，保留起始列已绘制的跨列内容（避免覆盖跨列文字）
                    e.Handled = true;
                    return;
                }

                // 起始列：绘制覆盖整段的合并矩形 + 居中文字
                bool drewAny = false;
                bool wholeBlock = false;   // 是否绘制了跨两行的整块（需跳过末尾统一边框，避免中间横线）
                foreach (var (start, span, text) in spans)
                {
                    if (e.ColumnIndex != start || string.IsNullOrEmpty(text)) continue;
                    int w = 0;
                    for (int k = 0; k < span; k++)
                        if (start + k < Columns.Count) w += Columns[start + k].Width;

                    // 若该列第 3 级（Row2）无子项，则纵向合并两行：矩形向下延伸覆盖 Row2
                    int h = rect.Height;
                    bool mergeDown = e.RowIndex == 1
                                     && e.ColumnIndex < SubNames.Length
                                     && string.IsNullOrEmpty(SubNames[e.ColumnIndex]);
                    if (mergeDown && e.RowIndex + 1 < Rows.Count)
                        h += Rows[e.RowIndex + 1].Height;

                    var big = new Rectangle(rect.X, rect.Y, w, h);

                    // 关键：DataGridView 会把 Graphics 裁剪到当前单元格内，
                    // 合并块跨两行时必须临时解除裁剪，否则下半部分被裁掉（只显示一半）。
                    var oldClip = g.Clip;
                    g.ResetClip();
                    g.FillRectangle(headerBrush, big);
                    g.DrawRectangle(pen, big);
                    g.DrawString(text, font, Brushes.Black, big, sf);
                    g.Clip = oldClip;

                    if (mergeDown) wholeBlock = true;
                    drewAny = true;
                }

                // 不是任何段起始、也不在段内部的列（如 Row1 的 col1/col2）：填充表头底色，避免显示为白色
                if (!drewAny) g.FillRectangle(headerBrush, rect);

                // 统一画当前单元格边框（跨两行的整块已由 big 画好外框，跳过以免中间出现横线）
                if (!wholeBlock) g.DrawRectangle(pen, rect);
                e.Handled = true;
                return;
            }
            else
            {
                // 第 3 级：若上方第 2 级（Row1）该列无子项（已被纵向合并），
                // 本格完全交给 Row1 的合并块绘制，此处直接跳过，避免覆盖已画好的合并块下半部分。
                if (e.ColumnIndex < 0 || e.ColumnIndex >= SubNames.Length)
                {
                    g.FillRectangle(headerBrush, rect);
                    e.Handled = true;
                    return;
                }
                if (string.IsNullOrEmpty(SubNames[e.ColumnIndex]))
                {
                    e.Handled = true;
                    return;
                }
                g.FillRectangle(headerBrush, rect);
                g.DrawString(SubNames[e.ColumnIndex], font, Brushes.Black, rect, sf);
            }

            g.DrawRectangle(pen, rect);
            e.Handled = true;
            return;
        }

        // ---- 数据区 ----
        int dataRow = e.RowIndex - HeaderRows;

        // 组别列（col0）：每 8 行垂直合并 —— 所有绘制交由 RowPostPaint 完成（底色 + 组号），
        // 避免冻结列/滚动时 CellPainting 行为异常导致组号丢失或位置错乱。
        if (dataRow >= 0 && e.ColumnIndex == ColGroup)
        {
            e.Handled = true;
            return;
        }

        // 其余数据单元格：填充背景 + 画边框（默认网格线已关闭，必须自绘）
        if (dataRow >= 0)
        {
            Color cellBack = dataRow == _activeRow
                ? _activeBack
                : (e.RowIndex % 2 == 0 ? Color.White : Color.FromArgb(248, 250, 252));
            using var brush = new SolidBrush(cellBack);
            g.FillRectangle(brush, e.CellBounds);

            var val = e.Value;
            if (val != null)
            {
                // 数据单元格直接用控件 Font（随 DPI 缩放；不要 dispose 它）
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                g.DrawString(val.ToString(), Font, Brushes.Black, e.CellBounds, sf);
            }
            g.DrawRectangle(pen, e.CellBounds);
            e.Handled = true;
        }
    }

    /// <summary>
    /// RowPostPaint 在每行 CellPainting 完成后触发（不受冻结列 CellPainting 行为异常影响）。
    /// 仅在合并块首行绘制：底色 + 上下加粗边线 + 组号（精确居中）。
    /// </summary>
    private void OnRowPostPaint(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        if (e.RowBounds.Width <= 0 || e.RowBounds.Height <= 0) return;

        int rowIndex = e.RowIndex;
        if (rowIndex < HeaderRows || rowIndex >= Rows.Count) return;

        // 只有合并块首行（dataRow % RowsPerGroup == 0）才进入绘制
        int dataRow = rowIndex - HeaderRows;
        if (dataRow < 0) return;
        if (dataRow % RowsPerGroup != 0) return;

        // 首行不在可见区则不画（其所属合并块当前未完整露出，交给滚入后的那次绘制）
        if (!Rows[rowIndex].Displayed) return;

        var g = e.Graphics;
        var colRect = GetColumnDisplayRectangle(ColGroup, false);
        if (colRect.Width <= 0 || colRect.Height <= 0) return;

        int blockFirst = rowIndex;
        int blockLast = Math.Min(blockFirst + RowsPerGroup - 1, Rows.Count - 1);

        // 合并块几何 —— 顶部 Y 直接用 e.RowBounds.Y（不累加前面所有行的 Height）
        int blockTopY = e.RowBounds.Y;
        int blockH = 0;
        for (int i = blockFirst; i <= blockLast; i++) blockH += Rows[i].Height;

        var rrect = new Rectangle(colRect.X, blockTopY, colRect.Width, blockH);
        var oldClip = g.Clip;
        g.ResetClip();

        using (var backBrush = new SolidBrush(_groupBack))
            g.FillRectangle(backBrush, rrect);
        using (var boldPen = new Pen(Color.FromArgb(120, 80, 80, 80), 1.5f))
        {
            g.DrawLine(boldPen, rrect.Left, rrect.Top, rrect.Right, rrect.Top);
            g.DrawLine(boldPen, rrect.Left, rrect.Bottom - 1, rrect.Right, rrect.Bottom - 1);
        }

        // 组号居中于合并块中央：用 TextRenderer.DrawText 精确居中
        // （不要用 MeasureString + DrawString(Point) 自行算偏移，基线问题会导致偏上）
        int groupNo = dataRow / RowsPerGroup + 1;
        // 组号字体同样基于控件当前 Font 派生（随 DPI 缩放）
        using var font = new Font(Font, FontStyle.Bold);
        var textRect = new Rectangle(colRect.X, blockTopY, colRect.Width, blockH);
        TextRenderer.DrawText(g, groupNo.ToString(), font, textRect,
            Color.Black,
            Color.Transparent,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix);

        g.Clip = oldClip;
    }

    /// <summary>单行 IL-DUT / RL-DUT 的时序统计累加器。</summary>
    private struct RowAcc
    {
        public int Count;
        public double IlSum, IlMax, IlMin;
        public double RlSum, RlMax, RlMin;
        public double IlInSum, IlOutSum, RlInSum, RlOutSum;
        public double IlInAvg => Count > 0 ? IlInSum / Count : double.NaN;
        public double IlOutAvg => Count > 0 ? IlOutSum / Count : double.NaN;
        public double RlInAvg => Count > 0 ? RlInSum / Count : double.NaN;
        public double RlOutAvg => Count > 0 ? RlOutSum / Count : double.NaN;

        public double IlAvg => Count > 0 ? IlSum / Count : double.NaN;
        public double RlAvg => Count > 0 ? RlSum / Count : double.NaN;

        public void Push(double ilIn, double ilOut, double ilDut, double rlIn, double rlDut, double rlOut)
        {
            bool any = false;
            if (!double.IsNaN(ilIn)) { IlInSum += ilIn; any = true; }
            if (!double.IsNaN(ilOut)) { IlOutSum += ilOut; any = true; }
            if (!double.IsNaN(ilDut))
            {
                if (Count == 0) { IlMax = IlMin = ilDut; }
                else
                {
                    if (ilDut > IlMax) IlMax = ilDut;
                    if (ilDut < IlMin) IlMin = ilDut;
                }
                IlSum += ilDut;
                any = true;
            }
            if (!double.IsNaN(rlIn)) { RlInSum += rlIn; any = true; }
            if (!double.IsNaN(rlDut))
            {
                if (Count == 0) { RlMax = RlMin = rlDut; }
                else
                {
                    if (rlDut > RlMax) RlMax = rlDut;
                    if (rlDut < RlMin) RlMin = rlDut;
                }
                RlSum += rlDut;
                any = true;
            }
            if (!double.IsNaN(rlOut)) { RlOutSum += rlOut; any = true; }
            if (any) Count++;
        }
    }
}
