using System;
using System.Windows;
using System.Windows.Threading;
using GmixLx.App.Wpf.ViewModels;
using GmixLx.App.Wpf.Views;
using GmixLx.Orchestration;

namespace GmixLx.App.Wpf
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            var viewModel = new MainViewModel();
            viewModel.CurveRequested += ShowCurveWindow;
            DataContext = viewModel;

            ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += delegate { ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); };
            timer.Start();
        }

        void ShowCurveWindow(MetricTableRow row)
        {
            var window = new CurveWindow(row) { Owner = this };
            window.ShowDialog();
        }
    }
}
