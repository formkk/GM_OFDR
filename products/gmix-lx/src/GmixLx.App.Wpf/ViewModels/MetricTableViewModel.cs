using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GmixLx.Core;
using GmixLx.Orchestration;
using Microsoft.Win32;

namespace GmixLx.App.Wpf.ViewModels
{
    public sealed class MetricTableViewModel : INotifyPropertyChanged
    {
        /// <summary>映讯_2 等待一帧平均就绪的超时；n=4 时约 0.65 s 一帧，留足余量。</summary>
        const int ReadyTimeoutMs = 15000;

        readonly string _sdkDirectory;
        readonly string _calibrationPath;

        string _com1x8Text = "COM4";
        string _com8x8Text = "COM3";
        string _samplesText = "1";
        string _spatialAverageText = "1";
        string _temporalAverageText = YinXun2Station.DefaultTemporalAverageN.ToString(CultureInfo.InvariantCulture);
        string _markAText = "4.440";
        string _markDText = "10.601";
        string _marginText = "22";

        CancellationTokenSource _cts;
        bool _running;
        readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();
        string _elapsedText = "—";
        string _overallState = "未开始";
        string _failuresSummary = "";

        public MetricTableViewModel(string sdkDirectory, string calibrationPath)
        {
            _sdkDirectory = sdkDirectory;
            _calibrationPath = calibrationPath;

            Rows = new ObservableCollection<MetricTableRowViewModel>();
            for (int group = 1; group <= 8; group++)
            {
                for (int outChannel = 1; outChannel <= 8; outChannel++)
                {
                    Rows.Add(new MetricTableRowViewModel(group, group, outChannel));
                }
            }
        }

        public ObservableCollection<MetricTableRowViewModel> Rows { get; private set; }

        public event Action<MetricTableRowViewModel> RowActivated;

        /// <summary>状态栏文本与语气；由 MainViewModel 转接到状态栏。</summary>
        public event Action<string, StatusKind> StatusChanged;

        public string Com1x8Text
        {
            get { return _com1x8Text; }
            set { Set(ref _com1x8Text, value, "Com1x8Text"); }
        }

        public string Com8x8Text
        {
            get { return _com8x8Text; }
            set { Set(ref _com8x8Text, value, "Com8x8Text"); }
        }

        public string SamplesText
        {
            get { return _samplesText; }
            set { Set(ref _samplesText, value, "SamplesText"); }
        }

        public string SpatialAverageText
        {
            get { return _spatialAverageText; }
            set { Set(ref _spatialAverageText, value, "SpatialAverageText"); }
        }

        public string TemporalAverageText
        {
            get { return _temporalAverageText; }
            set { Set(ref _temporalAverageText, value, "TemporalAverageText"); }
        }

        public string MarkAText
        {
            get { return _markAText; }
            set { Set(ref _markAText, value, "MarkAText"); }
        }

        public string MarkDText
        {
            get { return _markDText; }
            set { Set(ref _markDText, value, "MarkDText"); }
        }

        public string MarginText
        {
            get { return _marginText; }
            set { Set(ref _marginText, value, "MarginText"); }
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        public bool CanStart
        {
            get { return !_running; }
        }

        public bool CanStop
        {
            get { return _running; }
        }

        /// <summary>至少有一行指标结果且当前不在采集时才允许导出。</summary>
        public bool CanExport
        {
            get
            {
                if (_running) return false;
                foreach (MetricTableRowViewModel item in Rows)
                {
                    if (item.Source != null) return true;
                }
                return false;
            }
        }

        // ---------------- 顶部汇总 ----------------

        /// <summary>有效行数（PASS）。</summary>
        public int PassCount { get; private set; }

        /// <summary>失败行数（FAIL）。</summary>
        public int FailCount { get; private set; }

        /// <summary>尚未采集的行数。</summary>
        public int PendingCount { get; private set; }

        /// <summary>汇总状态：未开始 / 进行中 / PASS / FAIL / 部分完成。</summary>
        public string OverallState
        {
            get { return _overallState; }
        }

        /// <summary>失败原因汇总，作为 FAIL chip 的提示。</summary>
        public string FailuresSummary
        {
            get { return _failuresSummary; }
        }

        /// <summary>最近一次批量的用时；运行中或未开始为 —。</summary>
        public string ElapsedText
        {
            get { return _elapsedText; }
        }

        /// <summary>按 IN/OUT 把一条指标结果写入对应行；由采集回调调用。</summary>
        public void ApplyRow(MetricTableRow row)
        {
            if (row == null) throw new ArgumentNullException("row");
            foreach (MetricTableRowViewModel item in Rows)
            {
                if (item.InChannel == row.InChannel && item.OutChannel == row.OutChannel)
                {
                    item.Apply(row);
                    RaiseState();
                    return;
                }
            }
        }

        public void Clear()
        {
            foreach (MetricTableRowViewModel item in Rows) item.Clear();
            RaiseState();
        }

        public void Activate(MetricTableRowViewModel row)
        {
            if (row == null) return;
            RowActivated?.Invoke(row);
        }

        // ---------------- 运行 ----------------

        /// <summary>校验参数并启动 64 路指标表；错误按 WinForms 文案写入状态栏。</summary>
        public void Start()
        {
            if (_running) return;

            string error;
            YinXun2ReferenceCalibration calibration;
            if (!TryReadStation(out calibration, out error))
            {
                Report("参数错误：" + error, StatusKind.Danger);
                return;
            }

            CurveMetricsConfig metrics;
            if (!TryReadCriteria(out metrics, out error))
            {
                Report("参数错误：" + error, StatusKind.Danger);
                return;
            }

            uint spatial;
            uint temporal;
            uint samples;
            if (!TryReadInt(SpatialAverageText, 1, 20, "空间平均 m", out spatial, out error)
                || !TryReadInt(TemporalAverageText, 1, 100, "时间平均 n", out temporal, out error)
                || !TryReadInt(SamplesText, 1, 20, "采样次数", out samples, out error))
            {
                Report("参数错误：" + error, StatusKind.Danger);
                return;
            }

            // 厂商 ROI 与曲线视窗都由 A/D 锚点派生：参考系米 → 原生轴米（单一来源）。
            double roiStartM = calibration.ToYinXunDistance(metrics.MarkAM);
            double roiEndM = calibration.ToYinXunDistance(metrics.MarkDM);

            Clear();
            _watch.Restart();
            SetElapsed("—");
            _running = true;
            _cts = new CancellationTokenSource();
            RaiseState();
            Report("采集进行中…", StatusKind.Accent);

            string sdk = _sdkDirectory;
            string com1 = (Com1x8Text ?? "").Trim();
            string com8 = (Com8x8Text ?? "").Trim();
            CancellationToken token = _cts.Token;
            SynchronizationContext ui = SynchronizationContext.Current;

            Task.Run(delegate
            {
                try
                {
                    using (var station = YinXun2Station.Create(sdk, com1, com8, calibration, metrics,
                        spatial, temporal, roiStartM, roiEndM, ReadyTimeoutMs))
                    {
                        var options = new MetricTableOptions
                        {
                            SamplesPerPath = (int)samples,
                            SettleMs = 50,
                            ViewStartM = metrics.MarkAM,
                            ViewEndM = metrics.MarkDM
                        };

                        MetricTableResult result = station.Runner.Run(1, 8, 1, 8, options,
                            delegate(MetricTableRow row) { Post(ui, delegate { ApplyRow(row); }); }, token);

                        Post(ui, delegate
                        {
                            string text = string.Format(CultureInfo.InvariantCulture,
                                "完成：有效 {0} 条，失败 {1} 条，用时 {2:F1} s",
                                result.ValidPaths, result.FailedPaths, result.DurationMs / 1000.0);
                            string failures = result.DescribeFailures();
                            if (!string.IsNullOrEmpty(failures)) text += " · 失败原因：" + failures;
                            text += " · 映讯 GM-IX：RL 为设备内部参考";
                            SetElapsed((result.DurationMs / 1000.0)
                                .ToString("F1", CultureInfo.InvariantCulture) + " s");
                            Report(text, result.FailedPaths > 0 ? StatusKind.Normal : StatusKind.Success);
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    Post(ui, delegate { Report("已停止（未完成）", StatusKind.Danger); });
                }
                catch (Exception ex)
                {
                    Post(ui, delegate { Report("失败：" + ex.Message, StatusKind.Danger); });
                }
                finally
                {
                    Post(ui, delegate
                    {
                        _watch.Stop();
                        if (_elapsedText == "—")
                        {
                            SetElapsed(_watch.Elapsed.TotalSeconds
                                .ToString("F1", CultureInfo.InvariantCulture) + " s");
                        }
                        _running = false;
                        CancellationTokenSource cts = _cts;
                        _cts = null;
                        if (cts != null) cts.Dispose();
                        RaiseState();
                    });
                }
            });
        }

        public void Stop()
        {
            CancellationTokenSource cts = _cts;
            if (cts != null) cts.Cancel();
        }

        static void Post(SynchronizationContext context, Action action)
        {
            if (context != null) context.Post(delegate { action(); }, null);
            else action();
        }

        /// <summary>SDK 目录与参考标定：任一缺失或加载失败都拒绝开批。</summary>
        bool TryReadStation(out YinXun2ReferenceCalibration calibration, out string error)
        {
            calibration = null;
            error = null;

            if (string.IsNullOrEmpty(_sdkDirectory))
            {
                error = "未找到 SDK 目录：" + AppPaths.SdkRelativePath
                    + "（应指向含 ocs_measurement.dll 的 customer 目录，缺失时请检查仓库部署）";
                return false;
            }
            if (string.IsNullOrEmpty(_calibrationPath))
            {
                error = "未找到参考标定文件：" + AppPaths.CalibrationRelativePath;
                return false;
            }

            try
            {
                calibration = YinXun2ReferenceCalibration.Load(_calibrationPath);
            }
            catch (Exception ex)
            {
                error = "参考标定加载失败：" + ex.Message;
                return false;
            }
            return true;
        }

        /// <summary>读取并校验 A/D/余量：A/D 以米输入，任一项非法都拒绝开批。</summary>
        bool TryReadCriteria(out CurveMetricsConfig config, out string error)
        {
            config = YinXun2PathMeasurer.DefaultReferenceMetrics();
            error = null;

            double markA;
            double markD;
            double margin;
            if (!TryParse(MarkAText, out markA) || markA <= 0)
            {
                error = "开始位置须为正数（米）";
                return false;
            }
            if (!TryParse(MarkDText, out markD) || markD <= markA)
            {
                error = "终止位置须大于开始位置（米）";
                return false;
            }
            if (!TryParse(MarginText, out margin) || margin < 0 || margin > 60)
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
            return double.TryParse((text ?? "").Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        static bool TryReadInt(string text, int min, int max, string name, out uint value, out string error)
        {
            value = 0;
            error = null;
            int parsed;
            if (!int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                || parsed < min || parsed > max)
            {
                error = name + " 需为 " + min + "~" + max + " 的整数";
                return false;
            }
            value = (uint)parsed;
            return true;
        }

        // ---------------- 导出 ----------------

        /// <summary>导出当前指标结果为 CSV；列序与格式化由 MetricTableCsv 统一给出。</summary>
        public void SaveCsv()
        {
            var rows = new List<MetricTableRow>();
            foreach (MetricTableRowViewModel item in Rows)
            {
                if (item.Source != null) rows.Add(item.Source);
            }
            if (rows.Count == 0)
            {
                Report("当前没有可导出的指标结果", StatusKind.Normal);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = "gmix_lx_metrics_"
                    + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                MetricTableCsv.Write(dialog.FileName, rows);
                Report("已导出：" + dialog.FileName, StatusKind.Success);
            }
            catch (Exception ex)
            {
                Report("导出失败：" + ex.Message, StatusKind.Danger);
            }
        }

        // ---------------- 状态与通知 ----------------

        void Report(string text, StatusKind kind)
        {
            Action<string, StatusKind> handler = StatusChanged;
            if (handler != null) handler(text, kind);
        }

        void RaiseState()
        {
            OnPropertyChanged("IsRunning");
            OnPropertyChanged("CanStart");
            OnPropertyChanged("CanStop");
            OnPropertyChanged("CanExport");
            RaiseSummary();
        }

        /// <summary>重算 PASS/FAIL/待采集计数与汇总状态；行数固定 64，逐行扫描开销可忽略。</summary>
        void RaiseSummary()
        {
            int pass = 0;
            int fail = 0;
            var reasons = new List<string>();
            foreach (MetricTableRowViewModel item in Rows)
            {
                if (item.Source == null) continue;
                if (item.IsFailure)
                {
                    fail++;
                    if (!string.IsNullOrEmpty(item.Failure))
                    {
                        reasons.Add(item.InChannel.ToString(CultureInfo.InvariantCulture) + "/"
                            + item.OutChannel.ToString(CultureInfo.InvariantCulture) + " " + item.Failure);
                    }
                }
                else
                {
                    pass++;
                }
            }

            PassCount = pass;
            FailCount = fail;
            PendingCount = Rows.Count - pass - fail;

            string state;
            if (pass + fail == 0)
            {
                state = _running ? "进行中" : "未开始";
            }
            else if (fail > 0)
            {
                state = _running ? "进行中" : "FAIL";
            }
            else if (pass == Rows.Count)
            {
                state = "PASS";
            }
            else
            {
                state = _running ? "进行中" : "部分完成";
            }

            if (_overallState != state)
            {
                _overallState = state;
                OnPropertyChanged("OverallState");
            }
            _failuresSummary = reasons.Count == 0 ? "" : string.Join("；", reasons);
            OnPropertyChanged("PassCount");
            OnPropertyChanged("FailCount");
            OnPropertyChanged("PendingCount");
            OnPropertyChanged("FailuresSummary");
        }

        void SetElapsed(string text)
        {
            if (_elapsedText == text) return;
            _elapsedText = text;
            OnPropertyChanged("ElapsedText");
        }

        void Set(ref string field, string value, string name)
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged(name);
        }

        void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
