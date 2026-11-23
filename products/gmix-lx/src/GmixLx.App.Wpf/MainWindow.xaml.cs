using System.Windows;
using GmixLx.App.Wpf.ViewModels;

namespace GmixLx.App.Wpf
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}
