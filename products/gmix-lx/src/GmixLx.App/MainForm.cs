using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GmixLx.Core;
using GmixLx.Orchestration;

namespace GmixLx.App
{
    /// <summary>
    /// GM-IX-LX 主界面：映讯 GM-IX 专用，11 列指标表（列名与顺序与 GM-IX 参考实现一致），
    /// 64 行 = 8 组 IN × 8 路 OUT；曲线在双击行后弹出，不落盘。
    /// 视觉统一为浅底 + 白色卡片的紧凑工业风，强调色用于主操作与状态。
    /// </summary>
    internal sealed class MainForm : Form
    {
        // ---- 配色 ----
        static readonly Color PageBack = Color.FromArgb(243, 245, 248);
        static readonly Color CardBack = Color.White;
        static readonly Color BorderColor = Color.FromArgb(219, 224, 231);
        static readonly Color Accent = Color.FromArgb(24, 90, 188);
        static readonly Color AccentDown = Color.FromArgb(18, 72, 152);
        static readonly Color TextMain = Color.FromArgb(33, 37, 41);
        static readonly Color TextMuted = Color.FromArgb(106, 114, 125);
        static readonly Color Danger = Color.FromArgb(190, 45, 45);
        static readonly Color Success = Color.FromArgb(22, 130, 93);
        static readonly Color RowFail = Color.FromArgb(242, 243, 245);
        // 组底色：奇数组白、偶数组浅蓝，8 行一组形成分隔
        static readonly Color GroupBandOdd = Color.White;
        static readonly Color GroupBandEven = Color.FromArgb(240, 245, 252);
        static readonly Color GroupTint = Color.FromArgb(226, 236, 250);

        const int Rows = 64;
        const int Cols = 11;

        /// <summary>参考标定文件（相对仓库根，按 exe 目录向上解析）。</summary>
        const string CalibrationRelativePath = "products/gmix-lx/config/yinxun2_reference_calibration.json";

        /// <summary>厂商 SDK 目录（相对仓库根）；固定解析，不占界面。</summary>
        const string SdkRelativePath = "references/YinXun_2/customer";

        /// <summary>映讯_2 等待一帧平均就绪的超时；n=4 时约 0.65 s 一帧，留足余量。</summary>
        const int ReadyTimeoutMs = 15000;

        // ---- 控件 ----
        readonly TextBox _com1x8 = Input("COM4", 60);
        readonly TextBox _com8x8 = Input("COM3", 60);
        readonly NumericUpDown _samples = new NumericUpDown
        {
            Minimum = 1, Maximum = 20, Value = 1, Width = 54, BorderStyle = BorderStyle.FixedSingle
        };

        // 映讯 GM-IX 参数：ROI 默认 5~11.5 m（起点取参考标定的 vendor_roi_start_m）
        readonly NumericUpDown _spatialM = new NumericUpDown
        {
            Minimum = 1, Maximum = 20, Value = 1, Width = 52, BorderStyle = BorderStyle.FixedSingle
        };
        readonly NumericUpDown _temporalN = new NumericUpDown
        {
            Minimum = 1, Maximum = 100,
            Value = YinXun2Station.DefaultTemporalAverageN,
            Width = 54, BorderStyle = BorderStyle.FixedSingle
        };
        Label _spatialLabel;
        Label _temporalLabel;
        // 判据参数：默认值 = 当前实测固化值（A/D 以米输入，按系数换算成行）
        readonly TextBox _markA = Input("4.440", 66);
        readonly TextBox _markD = Input("10.601", 66);
        readonly TextBox _margin = Input("22", 48);

        readonly Button _start = PrimaryButton("开始测试", 96);
        readonly Button _stop = SecondaryButton("停止", 72);
        readonly Button _save = SecondaryButton("导出 CSV", 96);
        readonly ToolStripStatusLabel _status = new ToolStripStatusLabel { Text = "就绪" };
        readonly DataGridView _grid = new DataGridView();
        DeviceDebugPanel _deviceDebug;

        CancellationTokenSource _cts;
        MetricTableResult _lastResult;
        readonly MetricTableRow[] _rowData = new MetricTableRow[Rows];

        public MainForm()
        {
            Text = "光脉芯测 GM-IX-LX OFDR";
            ClientSize = new Size(1360, 840);
            MinimumSize = new Size(1120, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = PageBack;
            Font = new Font("Microsoft YaHei", 9F);
            DoubleBuffered = true;
            UseSharedIcon();

            var connectCard = BuildConnectCard();
            var criteriaCard = BuildCriteriaCard();

            var gridHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = PageBack,
                Padding = new Padding(16, 4, 16, 8)
            };
            BuildGrid();
            gridHost.Controls.Add(_grid);

            // 指标表页：沿用原来的停靠顺序（先加 Fill，再加 Top）
            var tablePage = new TabPage("指标表") { BackColor = PageBack };
            tablePage.Controls.Add(gridHost);
            tablePage.Controls.Add(criteriaCard);
            tablePage.Controls.Add(connectCard);

            // 距离域曲线页：布局参照映讯 OCS_test，放在指标表前面并默认选中
            _deviceDebug = new DeviceDebugPanel(ResolvePath(SdkRelativePath),
                ResolvePath(CalibrationRelativePath));
            var debugPage = new TabPage("距离域曲线") { BackColor = PageBack };
            debugPage.Controls.Add(_deviceDebug);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(debugPage);
            tabs.TabPages.Add(tablePage);
            tabs.SelectedTab = debugPage;

            var statusStrip = new StatusStrip
            {
                SizingGrip = false,
                BackColor = CardBack,
                ForeColor = TextMuted,
                Padding = new Padding(10, 0, 10, 0)
            };
            statusStrip.Items.Add(_status);

            // 停靠顺序：最后添加的先靠边，保证状态栏在最下、页签填满其余
            Controls.Add(tabs);
            Controls.Add(statusStrip);

            _start.Click += (s, e) => StartRun();
            _stop.Click += (s, e) => StopRun();
            _save.Click += (s, e) => SaveCsv();
            _grid.CellDoubleClick += OnCellDoubleClick;
        }


        /// <summary>双击某行 → 打开该路径的距离域曲线（数据来自本次采集的内存副本）。</summary>
        void OnCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= Rows) return;

            MetricTableRow data = _rowData[e.RowIndex];
            if (data == null)
            {
                SetStatus("该行尚未采集，无曲线可显示", TextMuted);
                return;
            }
            if (data.LevelsDb == null || data.LevelsDb.Length == 0)
            {
                SetStatus(string.Format("IN{0}/OUT{1} 无曲线数据：{2}",
                    data.InChannel, data.OutChannel, data.Failure ?? "本次采集未保留曲线"), Danger);
                return;
            }

            using (var form = new CurveForm(data))
            {
                form.ShowDialog(this);
            }
        }

        // ---------------- 布局 ----------------

        Panel BuildConnectCard()
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = CardBack };
            row.Controls.Add(FieldLabel("1×8 串口"));
            row.Controls.Add(_com1x8);
            row.Controls.Add(FieldLabel("8×8 串口", 12));
            row.Controls.Add(_com8x8);
            row.Controls.Add(FieldLabel("采样次数", 12));
            row.Controls.Add(_samples);
            row.Controls.Add(Spacer(16));
            row.Controls.Add(_start);
            row.Controls.Add(_stop);
            row.Controls.Add(_save);
            return Card(row, 52);
        }

        Panel BuildCriteriaCard()
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = CardBack };
            _spatialLabel = FieldLabel("空间平均 m");
            row.Controls.Add(_spatialLabel);
            row.Controls.Add(_spatialM);
            _temporalLabel = FieldLabel("时间平均 n", 12);
            row.Controls.Add(_temporalLabel);
            row.Controls.Add(_temporalN);
            row.Controls.Add(FieldLabel("开始位置(m)", 12));
            row.Controls.Add(_markA);
            row.Controls.Add(FieldLabel("终止位置(m)", 12));
            row.Controls.Add(_markD);
            row.Controls.Add(FieldLabel("阈值余量(dB)", 12));
            row.Controls.Add(_margin);
            return Card(row, 52);
        }

        Panel Card(Control content, int innerHeight)
        {
            var holder = new Panel
            {
                Dock = DockStyle.Top,
                Height = innerHeight + 16,
                BackColor = PageBack,
                Padding = new Padding(16, 8, 16, 8)
            };
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBack,
                Padding = new Padding(14, 8, 14, 8)
            };
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(BorderColor))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                }
            };
            card.Controls.Add(content);
            holder.Controls.Add(card);
            return holder;
        }

        static Label FieldLabel(string text, int leftPad = 0)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = TextMuted,
                Font = new Font("Microsoft YaHei", 9F),
                Margin = new Padding(leftPad, 8, 6, 0)
            };
        }

        static Control Spacer(int width)
        {
            return new Panel { Width = width, Height = 1, BackColor = CardBack };
        }

        static TextBox Input(string text, int width)
        {
            return new TextBox
            {
                Text = text,
                Width = width,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9.5F),
                ForeColor = TextMain,
                Margin = new Padding(0, 5, 0, 0)
            };
        }

        static Button PrimaryButton(string text, int width)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Accent,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                Margin = new Padding(0, 4, 8, 0)
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = AccentDown;
            return button;
        }

        static Button SecondaryButton(string text, int width)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = CardBack,
                ForeColor = Accent,
                Font = new Font("Microsoft YaHei", 9F),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                Margin = new Padding(0, 4, 8, 0)
            };
            button.FlatAppearance.BorderColor = BorderColor;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 243, 251);
            return button;
        }

        void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.AllowUserToOrderColumns = false;
            _grid.RowHeadersVisible = false;
            _grid.BorderStyle = BorderStyle.None;
            _grid.BackgroundColor = CardBack;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.GridColor = Color.FromArgb(230, 234, 240);
            _grid.EnableHeadersVisualStyles = false;
            _grid.ScrollBars = ScrollBars.Both;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.MultiSelect = false;
            _grid.Font = new Font("Microsoft YaHei", 8.5F);
            _grid.RowTemplate.Height = 26;

            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = CardBack,   // 表头不填底色，与白色卡片一致
                ForeColor = Color.FromArgb(52, 64, 84),
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                WrapMode = DataGridViewTriState.True,
                Padding = new Padding(2, 4, 2, 4)
            };
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;

            _grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = CardBack,
                ForeColor = TextMain,
                SelectionBackColor = Color.FromArgb(219, 233, 251),
                SelectionForeColor = TextMain,
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            // 不用行级斑马纹，改由「8 行一组」的组底色（见 BuildGrid / ApplyRow）
            _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = CardBack,
                SelectionBackColor = Color.FromArgb(219, 233, 251),
                SelectionForeColor = TextMain
            };

            string[] headers =
            {
                "测试\n组别", "IN\n端口", "OUT\n端口",
                "IL-IN\ndB", "IL-DUT\ndB", "IL-OUT\ndB",
                "RL-IN\ndB_rel", "RL-DUT\ndB_rel", "RL-OUT\ndB_rel",
                "DUT\n光程(m)", "链路\n长度(m)"
            };
            float[] weights = { 62, 52, 52, 78, 78, 78, 88, 88, 88, 82, 82 };

            for (int i = 0; i < Cols; i++)
            {
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = headers[i],
                    FillWeight = weights[i],
                    MinimumWidth = i < 3 ? 62 : 78,
                    SortMode = DataGridViewColumnSortMode.NotSortable
                });
            }

            for (int group = 1; group <= 8; group++)
            {
                for (int outPort = 1; outPort <= 8; outPort++)
                {
                    int index = _grid.Rows.Add();
                    DataGridViewRow row = _grid.Rows[index];
                    row.Cells[0].Value = outPort == 1 ? group.ToString() : null;
                    row.Cells[1].Value = group.ToString();
                    row.Cells[2].Value = outPort.ToString();
                }

                // 8 行一组：整组底色交替（奇数白 / 偶数浅蓝），组别列再深一档并加粗
                for (int r = 0; r < 8; r++)
                {
                    DataGridViewRow row = _grid.Rows[(group - 1) * 8 + r];
                    row.DefaultCellStyle.BackColor = GroupBand(group);

                    DataGridViewCell cell = row.Cells[0];
                    cell.Style.BackColor = GroupTint;
                    cell.Style.Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold);
                }
            }
        }

        /// <summary>窗口图标与任务栏图标统一用 GM-IX 的 guangmai.ico（嵌入资源，不依赖外部文件）。</summary>
        void UseSharedIcon()
        {
            using (System.IO.Stream stream = GetType().Assembly
                .GetManifestResourceStream("GmixLx.App.guangmai.ico"))
            {
                if (stream != null) Icon = new Icon(stream);
            }
        }

        // ---------------- 运行 ----------------

        void StartRun()
        {
            string com1 = _com1x8.Text.Trim();
            string com8 = _com8x8.Text.Trim();
            int samples = (int)_samples.Value;

            string error;
            YinXun2Params yinXunParams;
            YinXun2ReferenceCalibration calibration;
            if (!TryReadYinXun2(out yinXunParams, out calibration, out error))
            {
                SetStatus("参数错误：" + error, Danger);
                return;
            }

            CurveMetricsConfig metrics;
            if (!TryReadCriteria(out metrics, out error))
            {
                SetStatus("参数错误：" + error, Danger);
                return;
            }

            // 厂商 ROI 与曲线视窗都由 A/D 锚点派生：参考系米 → 原生轴米（单一来源）
            double roiStartM = calibration.ToYinXunDistance(metrics.MarkAM);
            double roiEndM = calibration.ToYinXunDistance(metrics.MarkDM);

            _start.Enabled = false;
            _stop.Enabled = true;
            _save.Enabled = false;
            _lastResult = null;
            ResetValues();
            _cts = new CancellationTokenSource();

            Task.Run(() =>
            {
                try
                {
                    using (var station = YinXun2Station.Create(yinXunParams.SdkDirectory, com1, com8,
                        calibration, metrics, yinXunParams.SpatialM, yinXunParams.TemporalN,
                        roiStartM, roiEndM, ReadyTimeoutMs))
                    {
                        RunTable(station.Runner, samples, metrics.MarkAM, metrics.MarkDM,
                            "映讯 GM-IX：RL 为设备内部参考");
                    }
                }
                catch (OperationCanceledException)
                {
                    SetStatus("已停止（未完成）", Danger);
                }
                catch (Exception ex)
                {
                    SetStatus("失败：" + ex.Message, Danger);
                }
                finally
                {
                    BeginInvoke(new Action(() =>
                    {
                        _start.Enabled = true;
                        _stop.Enabled = false;
                        _save.Enabled = _lastResult != null;
                    }));
                }
            });
        }

        /// <summary>64 路指标表执行体；两种设备只有曲线源不同。</summary>
        void RunTable(MetricTableRunner runner, int samples, double viewStartM, double viewEndM,
            string note = null)
        {
            SetStatus("采集进行中…", Accent);
            var options = new MetricTableOptions
            {
                SamplesPerPath = samples,
                SettleMs = 50,
                ViewStartM = viewStartM,
                ViewEndM = viewEndM
            };
            MetricTableResult result = runner.Run(1, 8, 1, 8, options,
                row => BeginInvoke(new Action(() => ApplyRow(row))),
                _cts.Token);

            _lastResult = result;
            string text = string.Format("完成：有效 {0} 条，失败 {1} 条，用时 {2:F1} s",
                result.ValidPaths, result.FailedPaths, result.DurationMs / 1000.0);
            string failures = result.DescribeFailures();
            if (!string.IsNullOrEmpty(failures)) text += " · 失败原因：" + failures;
            if (!string.IsNullOrEmpty(note)) text += " · " + note;
            SetStatus(text, result.FailedPaths > 0 ? TextMuted : Success);
        }

        /// <summary>映讯_2 参数：SDK 目录、m/n、ROI 与参考标定，任一缺失都拒绝开批。</summary>
        bool TryReadYinXun2(out YinXun2Params parameters, out YinXun2ReferenceCalibration calibration,
            out string error)
        {
            parameters = null;
            calibration = null;
            error = null;

            string sdkDirectory = ResolvePath(SdkRelativePath);
            if (sdkDirectory == null)
            {
                error = "未找到 SDK 目录：" + SdkRelativePath +
                    "（应指向含 ocs_measurement.dll 的 customer 目录，缺失时请检查仓库部署）";
                return false;
            }

            string calibrationPath = ResolvePath(CalibrationRelativePath);
            if (calibrationPath == null)
            {
                error = "未找到参考标定文件：" + CalibrationRelativePath;
                return false;
            }

            try
            {
                calibration = YinXun2ReferenceCalibration.Load(calibrationPath);
            }
            catch (Exception ex)
            {
                error = "参考标定加载失败：" + ex.Message;
                return false;
            }

            parameters = new YinXun2Params
            {
                SdkDirectory = sdkDirectory,
                SpatialM = (uint)_spatialM.Value,
                TemporalN = (uint)_temporalN.Value
            };
            return true;
        }

        /// <summary>
        /// 相对路径按仓库根解析：从 exe 目录逐级向上找，命中的第一个存在项即结果。
        /// 绝对路径原样校验；找不到返回 null（由调用方报错，不做兜底）。
        /// </summary>
        static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim();

            if (System.IO.Path.IsPathRooted(path))
            {
                return System.IO.File.Exists(path) || System.IO.Directory.Exists(path) ? path : null;
            }

            var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string candidate = System.IO.Path.Combine(directory.FullName, path);
                if (System.IO.File.Exists(candidate) || System.IO.Directory.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
            return null;
        }

        /// <summary>映讯_2 运行参数（从界面读出后固定，运行中不再变）。</summary>
        sealed class YinXun2Params
        {
            public string SdkDirectory;
            public uint SpatialM;
            public uint TemporalN;
        }

        /// <summary>
        /// 读取并校验 4 个判据参数：A/D 以米输入，按「行→米系数」换算为行号；
        /// 任一项非法则拒绝开批（不做静默兜底）。
        /// </summary>
        bool TryReadCriteria(out CurveMetricsConfig config, out string error)
        {
            config = YinXun2PathMeasurer.DefaultReferenceMetrics();
            error = null;

            double markA, markD, margin;
            if (!TryParse(_markA.Text, out markA) || markA <= 0)
            {
                error = "开始位置须为正数（米）";
                return false;
            }
            if (!TryParse(_markD.Text, out markD) || markD <= markA)
            {
                error = "终止位置须大于开始位置（米）";
                return false;
            }
            if (!TryParse(_margin.Text, out margin) || margin < 0 || margin > 60)
            {
                error = "阈值余量须在 0~60 dB";
                return false;
            }

            config.MarkAM = markA;
            config.MarkDM = markD;
            config.NoiseFloorMarginDb = margin;
            return true;
        }

        static bool TryParse(string text, out double value)
        {
            return double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        void StopRun()
        {
            if (_cts != null) _cts.Cancel();
        }

        void ResetValues()
        {
            for (int r = 0; r < Rows; r++)
            {
                _rowData[r] = null;
                for (int c = 3; c < Cols; c++) _grid.Rows[r].Cells[c].Value = null;
                DataGridViewRow row = _grid.Rows[r];
                row.DefaultCellStyle.BackColor = GroupBand(r / 8 + 1);
                row.DefaultCellStyle.ForeColor = TextMain;
            }
        }

        /// <summary>按 8 行一组返回组底色（奇数组白、偶数组浅蓝）。</summary>
        static Color GroupBand(int group)
        {
            return group % 2 == 0 ? GroupBandEven : GroupBandOdd;
        }

        void ApplyRow(MetricTableRow row)
        {
            int index = (row.InChannel - 1) * 8 + (row.OutChannel - 1);
            if (index < 0 || index >= Rows) return;

            _rowData[index] = row;

            DataGridViewRow gridRow = _grid.Rows[index];
            gridRow.Cells[3].Value = Fmt(row.IlIn);
            gridRow.Cells[4].Value = Fmt(row.IlDut);
            gridRow.Cells[5].Value = Fmt(row.IlOut);
            gridRow.Cells[6].Value = Fmt(row.RlIn);
            gridRow.Cells[7].Value = Fmt(row.RlDut);
            gridRow.Cells[8].Value = Fmt(row.RlOut);
            gridRow.Cells[9].Value = Fmt(row.DutPathM);
            gridRow.Cells[10].Value = Fmt(row.LinkLengthM);

            // 不可计算的路径：指标格显示 "—"（不留空白），悬停给出具体原因，状态栏汇总原因
            if (!row.Succeeded)
            {
                gridRow.DefaultCellStyle.BackColor = RowFail;
                gridRow.DefaultCellStyle.ForeColor = Color.FromArgb(150, 157, 166);
                for (int c = 3; c < Cols; c++)
                {
                    gridRow.Cells[c].Value = "—";
                    gridRow.Cells[c].ToolTipText = row.Failure;
                }
            }
            else
            {
                gridRow.DefaultCellStyle.BackColor = GroupBand(row.InChannel);
                gridRow.DefaultCellStyle.ForeColor = TextMain;
            }
        }

        static object Fmt(double value)
        {
            return double.IsNaN(value) ? (object)null : value.ToString("F3");
        }

        void SaveCsv()
        {
            if (_lastResult == null) return;
            using (var dialog = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = "gmix_lx_metrics_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                MetricTableCsv.Write(dialog.FileName, _lastResult.Rows);
                SetStatus("已导出：" + dialog.FileName, Success);
            }
        }

        void SetStatus(string text)
        {
            SetStatus(text, TextMuted);
        }

        void SetStatus(string text, Color color)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, color))); return; }
            _status.Text = text;
            _status.ForeColor = color;
        }
    }
}
