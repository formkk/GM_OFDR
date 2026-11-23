using System.Windows.Controls;
using System.Windows.Input;
using GmixLx.App.Wpf.ViewModels;

namespace GmixLx.App.Wpf.Views
{
    public partial class MetricTableView : UserControl
    {
        public MetricTableView()
        {
            InitializeComponent();
        }

        void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var grid = sender as DataGrid;
            if (grid == null) return;
            var row = grid.SelectedItem as MetricTableRowViewModel;
            var viewModel = DataContext as MetricTableViewModel;
            if (viewModel != null) viewModel.Activate(row);
        }
    }
}
