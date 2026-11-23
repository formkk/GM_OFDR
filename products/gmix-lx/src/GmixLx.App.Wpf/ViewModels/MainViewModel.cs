using System;
using System.ComponentModel;
using System.Globalization;
using GmixLx.Orchestration;

namespace GmixLx.App.Wpf.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        string _statusText = "就绪";
        StatusKind _statusTone = StatusKind.Normal;

        public MainViewModel()
        {
            MetricTable = new MetricTableViewModel(AppPaths.SdkDirectory, AppPaths.CalibrationPath);
            MetricTable.RowActivated += OnRowActivated;
            MetricTable.StatusChanged += OnStatusChanged;
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

        /// <summary>状态栏语气（普通/进行中/失败/成功），决定文字颜色。</summary>
        public StatusKind StatusTone
        {
            get { return _statusTone; }
            set
            {
                if (_statusTone == value) return;
                _statusTone = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusTone)));
            }
        }

        void OnRowActivated(MetricTableRowViewModel row)
        {
            MetricTableRow data = row.Source;
            if (data == null)
            {
                SetStatus("该行尚未采集，无曲线可显示", StatusKind.Normal);
                return;
            }
            if (data.LevelsDb == null || data.LevelsDb.Length == 0)
            {
                SetStatus(string.Format(CultureInfo.InvariantCulture,
                    "IN{0}/OUT{1} 无曲线数据：{2}",
                    data.InChannel, data.OutChannel,
                    data.Failure ?? "本次采集未保留曲线"), StatusKind.Danger);
                return;
            }

            CurveRequested?.Invoke(data);
        }

        void OnStatusChanged(string text, StatusKind kind)
        {
            SetStatus(text, kind);
        }

        void SetStatus(string text, StatusKind kind)
        {
            StatusText = text;
            StatusTone = kind;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
