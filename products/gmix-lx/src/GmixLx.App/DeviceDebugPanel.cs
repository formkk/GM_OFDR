using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GmixLx.Core;
using GmixLx.Orchestration;

namespace GmixLx.App
{
    /// <summary>
    /// 距离域曲线页：单路实时采集，布局参照映讯 OCS_test——
    /// 顶部 ROI IL/RL 两张卡片，中部距离域曲线，底部左侧采集参数、右侧峰表。
    /// 设备固定为映讯 GM-IX，走厂商 SDK。
    /// </summary>
    internal sealed class DeviceDebugPanel : UserControl
    {
        static readonly Color PageBack = Color.FromArgb(243, 245, 248);
        static readonly Color CardBack = Color.White;
        static readonly Color BorderColor = Color.FromArgb(219, 224, 231);
        static readonly Color Accent = Color.FromArgb(24, 90, 188);
        static readonly Color AccentDown = Color.FromArgb(18, 72, 152);
        static readonly Color TextMain = Color.FromArgb(33, 37, 41);
        static readonly Color TextMuted = Color.FromArgb(106, 114, 125);
        static readonly Color Danger = Color.FromArgb(190, 45, 45);
        static readonly Color Success = Color.FromArgb(22, 130, 93);

        const double PeakMarginDb = 22.0;
        const int PeakMergeGapRows = 22;

        /// <summary>右下角事件表只显示该距离之后的峰（0.05 m 之前为零距/连接器伪峰）。</summary>
        const double MinimumTablePositionM = 0.05;

        /// <summary>ROI 端点局部平均半窗（行），与 demo 的 AvgHalfWindow 一致。</summary>
        const int RoiAverageHalfWindowRows = 250;

        /// <summary>相邻峰最小间距（米）：小于该值合并为一个事件，保留较强者。</summary>
        const double MinPeakSeparationM = 0.010;

        /// <summary>旁瓣检查窗口（米）：窗口内存在高出 dropDb 的峰即判旁瓣。</summary>
        const double SidelobeWindowM = 0.020;

        /// <summary>旁瓣判定的电平差（dB）。</summary>
        const double SidelobeDropDb = 8.0;

        readonly string _sdkDirectory;
        readonly CurvePlotPanel _plot = new CurvePlotPanel();
        readonly Label _ilValue;
        readonly Label _rlValue;
        readonly DataGridView _table = new DataGridView();
        readonly TextBox _spatial = TextInput("1");
        readonly TextBox _temporal = TextInput(YinXun2Station.DefaultTemporalAverageN.ToString(CultureInfo.InvariantCulture));
        readonly TextBox _roiStart = TextInput("5.37");
        readonly TextBox _roiEnd = TextInput("11.31");
        readonly TextBox _displayMax = TextInput("12.00");
        readonly Button _single = PrimaryButton("单次采集");
        readonly Button _continuous = SecondaryButton("连续采集");
        readonly Button _save = SecondaryButton("保存数据");

        Panel _root;
        YinXun2SingleSession _session;
        YinXun2SingleFrame _lastFrame;
        CancellationTokenSource _cts;
        bool _deviceAvailable = true;
        bool _acquiring;

        readonly double _levelOffsetDb;

        public DeviceDebugPanel(string sdkDirectory, string calibrationPath)
        {
            _sdkDirectory = sdkDirectory;
            if (!string.IsNullOrEmpty(calibrationPath) && File.Exists(calibrationPath))
            {
                try
                {
                    _levelOffsetDb = YinXun2ReferenceCalibration.Load(calibrationPath).LevelOffsetDb;
                }
                catch (Exception)
                {
                    _levelOffsetDb = 0.0;
                }
            }
            Dock = DockStyle.Fill;
            BackColor = PageBack;
            Padding = new Padding(16, 12, 16, 12);
            Font = new Font("Microsoft YaHei", 9F);

            var metrics = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 8),
                BackColor = PageBack
            };
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            metrics.Controls.Add(MetricCard("ROI IL", out _ilValue), 0, 0);
            metrics.Controls.Add(MetricCard("ROI RL", out _rlValue), 1, 0);

            var plotCard = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBack,
                Padding = new Padding(10, 8, 10, 6),
                Margin = new Padding(0)
            };
            plotCard.Paint += PaintBorder;
            _plot.Dock = DockStyle.Fill;
            plotCard.Controls.Add(_plot);

            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 8, 0, 0),
                BackColor = PageBack
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.Controls.Add(BuildControls(), 0, 0);
            bottom.Controls.Add(BuildTableCard(), 1, 0);

            // 顶部卡片固定、曲线吃满、底部参数固定；窗口偏矮时整页可滚动
            AutoScroll = true;
            metrics.Dock = DockStyle.Top;
            metrics.Height = 76;
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 174;
            _root = new Panel
            {
                Dock = DockStyle.Top,
                Height = 620,
                BackColor = PageBack
            };
            _root.Controls.Add(plotCard);
            _root.Controls.Add(bottom);
            _root.Controls.Add(metrics);

            Controls.Add(_root);
            Load += (s, e) => FitRootHeight();
            Resize += (s, e) => FitRootHeight();

            _single.Click += async (s, e) => await RunOnceAsync();

            _continuous.Click += (s, e) =>
            {
                if (_cts == null) StartContinuous();
                else StopContinuous();
            };
            _save.Click += (s, e) => SaveFrame();

            // A/B 拖动结束后把该段设为 ROI 并重采一帧，卡片随厂商 ROI 汇总值刷新。
            _plot.CursorsChanged += (a, b) => _ = ApplyCursorsAsync(a, b);
            _roiStart.TextChanged += (s, e) => SyncCursors();
            _roiEnd.TextChanged += (s, e) => SyncCursors();
        }

        async Task ApplyCursorsAsync(double a, double b)
        {
            if (!_deviceAvailable || b <= a) return;
            _roiStart.Text = ClampMeters(a).ToString("F2", CultureInfo.InvariantCulture);
            _roiEnd.Text = ClampMeters(b).ToString("F2", CultureInfo.InvariantCulture);

            if (_cts != null)
            {
                StopContinuous();
                for (int i = 0; i < 40 && _cts != null; i++) await Task.Delay(50);
            }
            await RunOnceAsync();
        }

        void SyncCursors()
        {
            double a, b;
            if (!double.TryParse(_roiStart.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a)) return;
            if (!double.TryParse(_roiEnd.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return;
            if (b > a) _plot.SetCursors(a, b);
        }

        static decimal ClampMeters(double meters)
        {
            if (meters < 0) meters = 0;
            if (meters > 68.1334) meters = 68.1334;
            return Math.Round((decimal)meters, 2);
        }

        // ---------------- 采集 ----------------

        sealed class AcquireRequest
        {
            public uint SpatialM;
            public uint TemporalN;
            public double RoiStartM;
            public double RoiEndM;
        }

        bool TryReadRequest(out AcquireRequest request, out string error)
        {
            request = null;
            error = null;

            if (string.IsNullOrEmpty(_sdkDirectory))
            {
                error = "未找到映讯 SDK 目录（references/YinXun_2/customer）";
                return false;
            }

            uint spatial = 1, temporal = 1;
            double start, end;
            if (!TryReadInt(_spatial, 1, 20, "空间平均", out spatial, out error)) return false;
            if (!TryReadInt(_temporal, 1, 100, "采集次数", out temporal, out error)) return false;
            if (!TryReadMeters(_roiStart, "ROI 起点", out start, out error)) return false;
            if (!TryReadMeters(_roiEnd, "ROI 终点", out end, out error)) return false;
            if (end <= start)
            {
                error = "ROI 终点必须大于起点";
                return false;
            }
            request = new AcquireRequest
            {
                SpatialM = spatial,
                TemporalN = temporal,
                RoiStartM = start,
                RoiEndM = end
            };
            return true;
        }

        YinXun2SingleFrame AcquireFrame(AcquireRequest request)
        {
            if (_session == null
                || _session.SpatialAverageM != request.SpatialM
                || _session.TemporalAverageN != request.TemporalN)
            {
                if (_session != null)
                {
                    _session.Dispose();
                    _session = null;
                }
                _session = YinXun2SingleSession.Open(_sdkDirectory,
                    request.SpatialM, request.TemporalN, request.RoiStartM, request.RoiEndM,
                    _levelOffsetDb);
            }
            else if (Math.Abs(_session.RoiStartM - request.RoiStartM) > 1e-9
                || Math.Abs(_session.RoiEndM - request.RoiEndM) > 1e-9)
            {
                _session.SetRoi(request.RoiStartM, request.RoiEndM);
            }

            return _session.Acquire(PeakMarginDb, PeakMergeGapRows);
        }

        async Task RunOnceAsync()
        {
            if (!_deviceAvailable || _cts != null || _acquiring) return;
            _acquiring = true;

            AcquireRequest request;
            string error;
            if (!TryReadRequest(out request, out error))
            {
                _acquiring = false;
                ShowError(error);
                return;
            }

            _single.Enabled = false;
            try
            {
                YinXun2SingleFrame frame = await Task.Run(() => AcquireFrame(request));
                RenderFrame(frame);
            }
            catch (Exception ex)
            {
                ShowError("采集失败：" + ex.Message);
            }
            finally
            {
                _acquiring = false;
                _single.Enabled = _deviceAvailable;
            }
        }

        void StartContinuous()
        {
            if (!_deviceAvailable) return;

            AcquireRequest request;
            string error;
            if (!TryReadRequest(out request, out error))
            {
                ShowError(error);
                return;
            }

            _cts = new CancellationTokenSource();
            _continuous.Text = "停止";
            _single.Enabled = false;
            _ = ContinuousLoopAsync(request, _cts.Token);
        }

        void StopContinuous()
        {
            CancellationTokenSource cts = _cts;
            if (cts == null) return;
            cts.Cancel();
        }

        async Task ContinuousLoopAsync(AcquireRequest request, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    YinXun2SingleFrame frame;
                    try
                    {
                        frame = await Task.Run(() => AcquireFrame(request), token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        ShowError("采集失败：" + ex.Message);
                        break;
                    }

                    RenderFrame(frame);
                    try
                    {
                        await Task.Delay(50, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _continuous.Text = "连续采集";
                        _single.Enabled = _deviceAvailable;
                        CancellationTokenSource cts = _cts;
                        _cts = null;
                        if (cts != null) cts.Dispose();
                    }));
                }
            }
        }

        void RenderFrame(YinXun2SingleFrame frame)
        {
            _lastFrame = frame;

            _ilValue.Text = FormatDb(frame.RoiIlDb);
            // 厂商 SDK 的 ROI RL 是未加显示偏移的原始电平，需叠加曲线偏移才能与厂商 GUI 对上。
            _rlValue.Text = FormatDb(frame.RoiRlDb + _levelOffsetDb);

            // 峰事件后处理：旁瓣剔除 + 最小间距合并
            double pitch = frame.PitchM > 0 ? frame.PitchM : YinXun2Station.NativePitchM;
            List<PeakEvent> events = PeakEventFilter.Apply(frame.Peaks, pitch,
                MinPeakSeparationM, SidelobeWindowM, SidelobeDropDb);

            // 曲线只画有效事件（旁瓣不画），事件表则全部列出并标注状态
            var plotPeaks = new List<CurvePeak>();
            foreach (PeakEvent item in events)
            {
                if (item.Status != PeakEventStatus.Sidelobe) plotPeaks.Add(item.Peak);
            }
            frame.Peaks = plotPeaks;

            _plot.SetFrame(frame, ReadMetersOr(_displayMax, 12.0));

            _table.Rows.Clear();
            int number = 0;
            foreach (PeakEvent item in events)
            {
                CurvePeak peak = item.Peak;
                double positionM = peak.Centroid * pitch;
                // 0.05 m 之前的零距/连接器伪峰不进事件表
                if (positionM < MinimumTablePositionM) continue;

                string label;
                if (item.Status == PeakEventStatus.Sidelobe)
                {
                    label = "—";
                }
                else
                {
                    number++;
                    label = number.ToString(CultureInfo.InvariantCulture);
                }

                _table.Rows.Add(
                    label,
                    positionM.ToString("F5", CultureInfo.InvariantCulture),
                    peak.Db.ToString("F2", CultureInfo.InvariantCulture),
                    StatusText(item.Status));
            }

        }

        static string FormatDb(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "-- dB";
            return value.ToString("0.00", CultureInfo.InvariantCulture) + " dB";
        }

        static string StatusText(PeakEventStatus status)
        {
            switch (status)
            {
                case PeakEventStatus.Merged: return "合并";
                case PeakEventStatus.Sidelobe: return "旁瓣";
                default: return "";
            }
        }

        // ---------------- 保存 ----------------

        void SaveFrame()
        {
            YinXun2SingleFrame frame = _lastFrame;
            if (frame == null)
            {
                ShowError("当前没有可保存的曲线");
                return;
            }

            using (var dialog = new SaveFileDialog
            {
                Filter = "CSV|*.csv",
                FileName = "yinXun2_frame_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv",
                Title = "保存当前曲线"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    using (var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(false)))
                    {
                        writer.WriteLine("distance_m,reflection_db");
                        for (int i = 0; i < frame.LevelsDb.Length; i++)
                        {
                            writer.Write(frame.DistanceM[i].ToString("F9", CultureInfo.InvariantCulture));
                            writer.Write(',');
                            writer.WriteLine(frame.LevelsDb[i].ToString("F4", CultureInfo.InvariantCulture));
                        }
                    }
                }
                catch (Exception ex)
                {
                    ShowError("保存失败：" + ex.Message);
                }
            }
        }

        // ---------------- 布局 ----------------

        Panel MetricCard(string title, out Label value)
        {
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBack,
                Margin = new Padding(0, 0, 8, 0)
            };
            card.Paint += PaintBorder;

            var titleLabel = new Label
            {
                Text = title,
                AutoSize = true,
                Location = new Point(14, 6),
                ForeColor = TextMuted,
                Font = new Font("Microsoft YaHei", 9F)
            };
            value = new Label
            {
                Text = "-- dB",
                AutoSize = true,
                Location = new Point(14, 24),
                ForeColor = TextMain,
                Font = new Font("Microsoft YaHei", 20F, FontStyle.Bold)
            };
            card.Controls.Add(titleLabel);
            card.Controls.Add(value);
            return card;
        }

        Panel BuildControls()
        {
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBack,
                Padding = new Padding(12, 10, 12, 10),
                Margin = new Padding(0, 0, 8, 0)
            };
            card.Paint += PaintBorder;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 4,
                BackColor = CardBack
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

            AddPair(layout, 0, "空间平均", _spatial, "采集次数", _temporal);
            AddPair(layout, 1, "ROI 起点", _roiStart, "ROI 终点", _roiEnd);
            AddPair(layout, 2, "显示上限", _displayMax, null, null);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = CardBack,
                Margin = new Padding(0, 10, 0, 0)
            };
            _single.Width = 120;
            _continuous.Width = 120;
            _save.Width = 120;
            buttons.Controls.Add(_single);
            buttons.Controls.Add(_continuous);
            buttons.Controls.Add(_save);
            layout.Controls.Add(buttons, 0, 3);
            layout.SetColumnSpan(buttons, 4);

            card.Controls.Add(layout);
            return card;
        }

        static void AddPair(TableLayoutPanel layout, int row,
            string leftLabel, Control leftInput, string rightLabel, Control rightInput)
        {
            layout.Controls.Add(FieldLabel(leftLabel), 0, row);
            leftInput.Dock = DockStyle.Fill;
            leftInput.Margin = new Padding(0, 4, 8, 4);
            layout.Controls.Add(leftInput, 1, row);

            if (rightInput == null) return;
            layout.Controls.Add(FieldLabel(rightLabel), 2, row);
            rightInput.Dock = DockStyle.Fill;
            rightInput.Margin = new Padding(0, 4, 0, 4);
            layout.Controls.Add(rightInput, 3, row);
        }

        static Label FieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextMuted,
                Font = new Font("Microsoft YaHei", 9.5F),
                Margin = new Padding(0)
            };
        }

        Panel BuildTableCard()
        {
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBack,
                Padding = new Padding(10, 8, 10, 8),
                Margin = new Padding(0)
            };
            card.Paint += PaintBorder;

            _table.Dock = DockStyle.Fill;
            _table.BackgroundColor = CardBack;
            _table.BorderStyle = BorderStyle.None;
            _table.AllowUserToAddRows = false;
            _table.AllowUserToDeleteRows = false;
            _table.AllowUserToResizeRows = false;
            _table.ReadOnly = true;
            _table.RowHeadersVisible = false;
            _table.MultiSelect = false;
            _table.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _table.ColumnHeadersHeight = 34;
            _table.RowTemplate.Height = 26;
            _table.EnableHeadersVisualStyles = false;
            _table.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(220, 230, 241);
            _table.ColumnHeadersDefaultCellStyle.ForeColor = TextMain;
            _table.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 230, 241);
            _table.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextMain;
            _table.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            _table.DefaultCellStyle.Font = new Font("Microsoft YaHei", 9F);
            _table.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 236, 250);
            _table.DefaultCellStyle.SelectionForeColor = TextMain;

            _table.Columns.Add(Column("序号", 14, false));
            _table.Columns.Add(Column("位置 (m)", 38, true));
            _table.Columns.Add(Column("RL (dB)", 32, true));
            _table.Columns.Add(Column("状态", 16, false));

            card.Controls.Add(_table);
            return card;
        }

        static DataGridViewTextBoxColumn Column(string header, int weight, bool rightAlign)
        {
            var column = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                FillWeight = weight,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                ReadOnly = true
            };
            if (rightAlign)
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            return column;
        }

        static TextBox TextInput(string text)
        {
            return new TextBox
            {
                Text = text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 11F),
                TextAlign = HorizontalAlignment.Right,
                BackColor = Color.White
            };
        }

        static bool TryReadInt(TextBox box, int min, int max, string name, out uint value, out string error)
        {
            value = 0;
            error = null;
            int parsed;
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                || parsed < min || parsed > max)
            {
                error = name + " 需为 " + min + "~" + max + " 的整数";
                return false;
            }
            value = (uint)parsed;
            return true;
        }

        static bool TryReadMeters(TextBox box, string name, out double value, out string error)
        {
            value = 0;
            error = null;
            if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || value < 0 || value > 68.1334)
            {
                error = name + " 需为 0~68.13 之间的数";
                return false;
            }
            return true;
        }

        static double ReadMetersOr(TextBox box, double fallback)
        {
            double value;
            if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return fallback;
            return value > 0 ? value : fallback;
        }

        static Button PrimaryButton(string text)
        {
            var button = new Button
            {
                Text = text,
                Width = 300,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = Accent,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                Margin = new Padding(0, 0, 0, 6)
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = AccentDown;
            return button;
        }

        static Button SecondaryButton(string text)
        {
            var button = new Button
            {
                Text = text,
                Width = 300,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = CardBack,
                ForeColor = TextMain,
                Font = new Font("Microsoft YaHei", 10F),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                Margin = new Padding(0, 0, 0, 6)
            };
            button.FlatAppearance.BorderColor = BorderColor;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 244, 250);
            return button;
        }

        static void PaintBorder(object sender, PaintEventArgs e)
        {
            var control = (Control)sender;
            using (var pen = new Pen(BorderColor))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1);
            }
        }

        void ShowError(string text)
        {
            MessageBox.Show(this, text, "距离域曲线", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>窗口变高时填满可用高度，变矮时保持 620 并出现滚动条。</summary>
        void FitRootHeight()
        {
            if (_root == null) return;
            _root.Height = Math.Max(620, ClientSize.Height - Padding.Vertical);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancellationTokenSource cts = _cts;
                _cts = null;
                if (cts != null)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                if (_session != null)
                {
                    _session.Dispose();
                    _session = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
