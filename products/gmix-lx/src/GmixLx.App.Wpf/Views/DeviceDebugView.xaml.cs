using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GmixLx.App.Wpf.ViewModels;
using GmixLx.Orchestration;

namespace GmixLx.App.Wpf.Views
{
    /// <summary>
    /// 距离域曲线页：把 ViewModel 的帧结果推给曲线控件，并承担路径解析与弹窗。
    /// 布局与交互对齐 WinForms `DeviceDebugPanel`。
    /// </summary>
    public partial class DeviceDebugView : UserControl
    {
        readonly DeviceDebugViewModel _viewModel;

        bool _viewInitialized;
        bool _cursorsInitialized;
        bool _closedHooked;
        double _displayMaxM;

        public DeviceDebugView()
        {
            InitializeComponent();
            _viewModel = new DeviceDebugViewModel(AppPaths.SdkDirectory, AppPaths.CalibrationPath);
            DataContext = _viewModel;

            _viewModel.FrameReady += OnFrameReady;
            _viewModel.ErrorRaised += OnError;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Plot.CursorsChanged += OnCursorsChanged;
            Loaded += OnLoaded;
        }

        async void OnSingleClick(object sender, RoutedEventArgs e)
        {
            await _viewModel.RunOnceAsync();
        }

        void OnContinuousClick(object sender, RoutedEventArgs e)
        {
            _viewModel.ToggleContinuous();
        }

        void OnSaveClick(object sender, RoutedEventArgs e)
        {
            _viewModel.SaveFrame();
        }

        void OnFrameReady(YinXun2SingleFrame frame, double displayMaxM)
        {
            Plot.Values = frame.LevelsDb;
            Plot.XScale = frame.PitchM;
            Plot.XOffset = 0.0;
            Plot.Peaks = frame.Peaks;
            Plot.PeakThresholdDb = frame.ThresholdDb;
            // 参考线默认落在阈值上；用户拖动后不再被后续帧覆盖。
            if (double.IsNaN(Plot.ReferenceLevelDb))
            {
                Plot.ReferenceLevelDb = frame.ThresholdDb;
            }
            Plot.RoiStartM = frame.RoiStartM;
            Plot.RoiEndM = frame.RoiEndM;

            double dataEndM = frame.LevelsDb.Length > 1 ? (frame.LevelsDb.Length - 1) * frame.PitchM : 0.0;
            bool displayChanged = Math.Abs(displayMaxM - _displayMaxM) > 1e-9;
            _displayMaxM = displayMaxM;
            if (!_viewInitialized || displayChanged)
            {
                Plot.ViewStartM = 0.0;
                Plot.ViewEndM = Math.Min(displayMaxM, dataEndM);
                _viewInitialized = true;
            }
            if (!_cursorsInitialized)
            {
                Plot.CursorAM = frame.RoiStartM;
                Plot.CursorBM = frame.RoiEndM;
                _cursorsInitialized = true;
            }
        }

        void OnCursorsChanged(double a, double b)
        {
            _ = _viewModel.ApplyCursorsAsync(a, b);
        }

        void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "RoiStartText" || e.PropertyName == "RoiEndText") SyncCursors();
        }

        /// <summary>外部（ROI 输入框）同步游标位置，不触发重采。</summary>
        void SyncCursors()
        {
            double a;
            double b;
            if (!double.TryParse((_viewModel.RoiStartText ?? "").Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out a)) return;
            if (!double.TryParse((_viewModel.RoiEndText ?? "").Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out b)) return;
            if (b <= a) return;
            Plot.CursorAM = a;
            Plot.CursorBM = b;
        }

        void OnError(string message)
        {
            Window owner = Window.GetWindow(this);
            if (owner != null)
            {
                MessageBox.Show(owner, message, "距离域曲线", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(message, "距离域曲线", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_closedHooked) return;
            Window window = Window.GetWindow(this);
            if (window == null) return;
            _closedHooked = true;
            window.Closed += delegate { _viewModel.Dispose(); };
        }
    }
}
