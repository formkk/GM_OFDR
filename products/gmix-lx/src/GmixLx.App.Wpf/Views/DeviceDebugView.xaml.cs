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

        /// <summary>左侧参数面板折叠：收窄到按钮宽度，隐藏内容与按钮。</summary>
        void OnParamsToggle(object sender, RoutedEventArgs e)
        {
            bool collapsed = ParamsToggle.IsChecked == true;
            ParamsColumn.Width = new GridLength(collapsed ? 26 : 140);
            ParamsPanel.Padding = collapsed ? new Thickness(1, 6, 1, 8) : new Thickness(8, 6, 8, 8);
            ParamsTitle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            ParamsContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            ParamsButtons.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            ParamsToggle.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;
            ParamsToggle.Content = collapsed ? "▶" : "◀";
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

        void OnFrameReady(YinXun2SingleFrame frame, double displayMaxM, bool resetView)
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
            Plot.ResetWidthM = displayMaxM;
            if (!_viewInitialized || displayChanged || resetView)
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
            if (e.PropertyName == "RoiStartText" || e.PropertyName == "RoiEndText")
            {
                SyncCursors();
            }
            else if (e.PropertyName == "DisplayMaxText")
            {
                ApplyDisplayMaxToView();
            }
        }

        /// <summary>显示上限变化时立即刷新视窗（不等下一帧）；上限非法时不动。</summary>
        void ApplyDisplayMaxToView()
        {
            double width;
            if (!double.TryParse((_viewModel.DisplayMaxText ?? "").Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out width) || width <= 0)
            {
                return;
            }

            Plot.ResetWidthM = width;
            Plot.ViewStartM = 0.0;

            double[] values = Plot.Values;
            if (values == null || values.Length < 2 || Plot.XScale <= 0)
            {
                // 尚未采集：横轴先按显示上限铺开。
                Plot.ViewEndM = width;
                return;
            }

            double dataEndM = (values.Length - 1) * Plot.XScale;
            Plot.ViewEndM = Math.Min(width, dataEndM);
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

            // 启动即按显示上限铺开横轴，等首帧再画曲线。
            ApplyDisplayMaxToView();
        }
    }
}
