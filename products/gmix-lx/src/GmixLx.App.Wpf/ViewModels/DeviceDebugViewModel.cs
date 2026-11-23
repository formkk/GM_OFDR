using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GmixLx.Core;
using GmixLx.Orchestration;
using Microsoft.Win32;

namespace GmixLx.App.Wpf.ViewModels
{
    /// <summary>峰表一行：序号、位置、RL 与状态均为显示文本，避免 UI 再做格式化。</summary>
    public sealed class PeakRow
    {
        public string Number { get; set; }
        public string Position { get; set; }
        public string Level { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// 距离域曲线页：单路实时采集，卡片/参数/峰表逻辑对齐 WinForms `DeviceDebugPanel`。
    /// 只依赖 Orchestration 与 Core，不接触 Vendor 类型。
    /// </summary>
    public sealed class DeviceDebugViewModel : INotifyPropertyChanged, IDisposable
    {
        const double PeakMarginDb = 22.0;
        const int PeakMergeGapRows = 22;

        /// <summary>右下角事件表只显示该距离之后的峰（0.05 m 之前为零距/连接器伪峰）。</summary>
        const double MinimumTablePositionM = 0.05;

        /// <summary>相邻峰最小间距（米）：小于该值合并为一个事件，保留较强者。</summary>
        const double MinPeakSeparationM = 0.010;

        /// <summary>旁瓣检查窗口（米）：窗口内存在高出 dropDb 的峰即判旁瓣。</summary>
        const double SidelobeWindowM = 0.020;

        /// <summary>旁瓣判定的电平差（dB）。</summary>
        const double SidelobeDropDb = 8.0;

        const double MaxDistanceM = 68.1334;

        readonly string _sdkDirectory;
        readonly double _levelOffsetDb;

        string _spatialAverageText = "1";
        string _temporalAverageText = YinXun2Station.DefaultTemporalAverageN.ToString(CultureInfo.InvariantCulture);
        string _roiStartText = "5.37";
        string _roiEndText = "11.31";
        string _displayMaxText = "12.00";
        string _ilText = "-- dB";
        string _rlText = "-- dB";
        string _continuousButtonText = "连续采集";

        YinXun2SingleSession _session;
        CancellationTokenSource _cts;
        Task _continuousTask;
        bool _acquiring;
        bool _disposed;

        public DeviceDebugViewModel(string sdkDirectory, string calibrationPath)
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
        }

        /// <summary>帧就绪，由视图负责把曲线/峰/ROI 推给画布控件（附带显示上限，单位 m）。</summary>
        public event Action<YinXun2SingleFrame, double> FrameReady;

        /// <summary>可读错误消息，由视图弹窗展示。</summary>
        public event Action<string> ErrorRaised;

        public ObservableCollection<PeakRow> PeakRows { get; } = new ObservableCollection<PeakRow>();

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

        public string RoiStartText
        {
            get { return _roiStartText; }
            set { Set(ref _roiStartText, value, "RoiStartText"); }
        }

        public string RoiEndText
        {
            get { return _roiEndText; }
            set { Set(ref _roiEndText, value, "RoiEndText"); }
        }

        public string DisplayMaxText
        {
            get { return _displayMaxText; }
            set { Set(ref _displayMaxText, value, "DisplayMaxText"); }
        }

        public string IlText
        {
            get { return _ilText; }
            private set { Set(ref _ilText, value, "IlText"); }
        }

        public string RlText
        {
            get { return _rlText; }
            private set { Set(ref _rlText, value, "RlText"); }
        }

        public string ContinuousButtonText
        {
            get { return _continuousButtonText; }
            private set { Set(ref _continuousButtonText, value, "ContinuousButtonText"); }
        }

        /// <summary>单次采集可用：未在采集中、未连续采集。</summary>
        public bool CanRunOnce
        {
            get { return !_acquiring && _cts == null; }
        }

        public YinXun2SingleFrame LastFrame { get; private set; }

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

            uint spatial;
            uint temporal;
            double start;
            double end;
            if (!TryReadInt(SpatialAverageText, 1, 20, "空间平均", out spatial, out error)) return false;
            if (!TryReadInt(TemporalAverageText, 1, 100, "采集次数", out temporal, out error)) return false;
            if (!TryReadMeters(RoiStartText, "ROI 起点", out start, out error)) return false;
            if (!TryReadMeters(RoiEndText, "ROI 终点", out end, out error)) return false;
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

        public async Task RunOnceAsync()
        {
            if (_disposed || _cts != null || _acquiring) return;

            AcquireRequest request;
            string error;
            if (!TryReadRequest(out request, out error))
            {
                RaiseError(error);
                return;
            }

            _acquiring = true;
            RaiseStateChanged();
            try
            {
                YinXun2SingleFrame frame = await Task.Run(() => AcquireFrame(request));
                RenderFrame(frame);
            }
            catch (Exception ex)
            {
                RaiseError("采集失败：" + ex.Message);
            }
            finally
            {
                _acquiring = false;
                RaiseStateChanged();
            }
        }

        public void ToggleContinuous()
        {
            if (_disposed) return;
            if (_cts == null) StartContinuous();
            else StopContinuous();
        }

        void StartContinuous()
        {
            AcquireRequest request;
            string error;
            if (!TryReadRequest(out request, out error))
            {
                RaiseError(error);
                return;
            }

            _cts = new CancellationTokenSource();
            ContinuousButtonText = "停止";
            RaiseStateChanged();
            _continuousTask = ContinuousLoopAsync(request, _cts.Token);
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
                        RaiseError("采集失败：" + ex.Message);
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
                ContinuousButtonText = "连续采集";
                CancellationTokenSource cts = _cts;
                _cts = null;
                if (cts != null) cts.Dispose();
                RaiseStateChanged();
            }
        }

        /// <summary>A/B 拖动结束后把该段设为 ROI 并重采一帧；连续采集中先停止再采。</summary>
        public async Task ApplyCursorsAsync(double a, double b)
        {
            if (_disposed || b <= a) return;

            RoiStartText = ClampMeters(a).ToString("F2", CultureInfo.InvariantCulture);
            RoiEndText = ClampMeters(b).ToString("F2", CultureInfo.InvariantCulture);

            if (_cts != null)
            {
                StopContinuous();
                Task running = _continuousTask;
                if (running != null)
                {
                    try
                    {
                        await running;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
            await RunOnceAsync();
        }

        // ---------------- 渲染 ----------------

        void RenderFrame(YinXun2SingleFrame frame)
        {
            LastFrame = frame;

            IlText = FormatDb(frame.RoiIlDb);
            // 厂商 SDK 的 ROI RL 是未加显示偏移的原始电平，需叠加曲线偏移才能与厂商 GUI 对上。
            RlText = FormatDb(frame.RoiRlDb + _levelOffsetDb);

            double pitch = frame.PitchM > 0 ? frame.PitchM : YinXun2Station.NativePitchM;
            List<PeakEvent> events = PeakEventFilter.Apply(frame.Peaks, pitch,
                MinPeakSeparationM, SidelobeWindowM, SidelobeDropDb);

            // 曲线只画有效事件（旁瓣不画），事件表则全部列出并标注状态。
            var plotPeaks = new List<CurvePeak>();
            foreach (PeakEvent item in events)
            {
                if (item.Status != PeakEventStatus.Sidelobe) plotPeaks.Add(item.Peak);
            }
            frame.Peaks = plotPeaks;

            PeakRows.Clear();
            int number = 0;
            foreach (PeakEvent item in events)
            {
                CurvePeak peak = item.Peak;
                double positionM = peak.Centroid * pitch;
                // 0.05 m 之前的零距/连接器伪峰不进事件表。
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

                PeakRows.Add(new PeakRow
                {
                    Number = label,
                    Position = positionM.ToString("F5", CultureInfo.InvariantCulture),
                    Level = peak.Db.ToString("F2", CultureInfo.InvariantCulture),
                    Status = StatusText(item.Status)
                });
            }

            Action<YinXun2SingleFrame, double> handler = FrameReady;
            if (handler != null) handler(frame, ReadMetersOr(DisplayMaxText, 12.0));
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

        public void SaveFrame()
        {
            YinXun2SingleFrame frame = LastFrame;
            if (frame == null)
            {
                RaiseError("当前没有可保存的曲线");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV|*.csv",
                FileName = "yinXun2_frame_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv",
                Title = "保存当前曲线"
            };
            if (dialog.ShowDialog() != true) return;

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
                RaiseError("保存失败：" + ex.Message);
            }
        }

        // ---------------- 输入解析 ----------------

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

        static bool TryReadMeters(string text, string name, out double value, out string error)
        {
            value = 0;
            error = null;
            if (!double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || value < 0 || value > MaxDistanceM)
            {
                error = name + " 需为 0~68.13 之间的数";
                return false;
            }
            return true;
        }

        static double ReadMetersOr(string text, double fallback)
        {
            double value;
            if (!double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return fallback;
            return value > 0 ? value : fallback;
        }

        static decimal ClampMeters(double meters)
        {
            if (meters < 0) meters = 0;
            if (meters > MaxDistanceM) meters = MaxDistanceM;
            return Math.Round((decimal)meters, 2);
        }

        // ---------------- 状态与通知 ----------------

        void RaiseError(string message)
        {
            Action<string> handler = ErrorRaised;
            if (handler != null) handler(message);
        }

        void RaiseStateChanged()
        {
            OnPropertyChanged("CanRunOnce");
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

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
    }
}
