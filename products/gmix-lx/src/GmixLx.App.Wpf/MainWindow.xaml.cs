using System.Windows;
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
        }

        void ShowCurveWindow(MetricTableRow row)
        {
            var window = new CurveWindow(row) { Owner = this };
            window.ShowDialog();
        }
    }
}
