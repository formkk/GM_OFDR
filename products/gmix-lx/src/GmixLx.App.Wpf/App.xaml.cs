using System.Windows;

namespace GmixLx.App.Wpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Visualization.CurvePlotControl.Prewarm();
        }
    }
}
