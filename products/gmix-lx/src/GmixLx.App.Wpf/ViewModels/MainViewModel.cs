using System;
using System.ComponentModel;
using System.Globalization;
using GmixLx.Orchestration;

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

        /// <summary>双击某行且该行有曲线数据时触发；由窗口负责打开曲线窗口。</summary>
        public event Action<MetricTableRow> CurveRequested;

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
            MetricTableRow data = row.Source;
            if (data == null)
            {
                StatusText = "该行尚未采集，无曲线可显示";
                return;
            }
            if (data.LevelsDb == null || data.LevelsDb.Length == 0)
            {
                StatusText = string.Format(CultureInfo.InvariantCulture,
                    "IN{0}/OUT{1} 无曲线数据：{2}",
                    data.InChannel, data.OutChannel,
                    data.Failure ?? "本次采集未保留曲线");
                return;
            }

            CurveRequested?.Invoke(data);
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
