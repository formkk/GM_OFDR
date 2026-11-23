using System.ComponentModel;
using System.Globalization;

namespace GmixLx.App.Wpf.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        string _statusText = "就绪";

        public MainViewModel()
        {
            MetricTable = new MetricTableViewModel();
            MetricTable.RowActivated += OnRowActivated;
        }

        public MetricTableViewModel MetricTable { get; private set; }

        public string StatusText
        {
            get { return _statusText; }
            set
            {
                if (_statusText == value) return;
                _statusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }

        void OnRowActivated(MetricTableRowViewModel row)
        {
            StatusText = string.Format(CultureInfo.InvariantCulture,
                "已选择 IN{0} → OUT{1}", row.InChannel, row.OutChannel);
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
