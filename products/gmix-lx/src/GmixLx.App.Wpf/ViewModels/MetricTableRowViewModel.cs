using System;
using System.ComponentModel;
using System.Globalization;
using GmixLx.Orchestration;

namespace GmixLx.App.Wpf.ViewModels
{
    public sealed class MetricTableRowViewModel : INotifyPropertyChanged
    {
        double _ilIn;
        double _ilDut;
        double _ilOut;
        double _rlIn;
        double _rlDut;
        double _rlOut;
        double _dutPathM;
        double _linkLengthM;
        bool _hasData;
        bool _isFailure;
        string _failure;

        public MetricTableRowViewModel(int groupIndex, int inChannel, int outChannel)
        {
            GroupIndex = groupIndex;
            InChannel = inChannel;
            OutChannel = outChannel;
        }

        public int GroupIndex { get; private set; }

        public int InChannel { get; private set; }

        public int OutChannel { get; private set; }

        public bool IsEvenGroup
        {
            get { return GroupIndex % 2 == 0; }
        }

        public bool IsGroupStart
        {
            get { return OutChannel == 1; }
        }

        public string GroupText
        {
            get { return IsGroupStart ? GroupIndex.ToString(CultureInfo.InvariantCulture) : string.Empty; }
        }

        public string InText
        {
            get { return InChannel.ToString(CultureInfo.InvariantCulture); }
        }

        public string OutText
        {
            get { return OutChannel.ToString(CultureInfo.InvariantCulture); }
        }

        public string IlInText { get { return MetricText(_ilIn); } }
        public string IlDutText { get { return MetricText(_ilDut); } }
        public string IlOutText { get { return MetricText(_ilOut); } }
        public string RlInText { get { return MetricText(_rlIn); } }
        public string RlDutText { get { return MetricText(_rlDut); } }
        public string RlOutText { get { return MetricText(_rlOut); } }
        public string DutPathText { get { return MetricText(_dutPathM); } }
        public string LinkLengthText { get { return MetricText(_linkLengthM); } }

        public bool HasData
        {
            get { return _hasData; }
        }

        public bool IsFailure
        {
            get { return _isFailure; }
        }

        public string Failure
        {
            get { return _failure; }
        }

        /// <summary>把一条指标结果写入本行；由后续采集编排调用。</summary>
        public void Apply(MetricTableRow row)
        {
            if (row == null) throw new ArgumentNullException("row");
            _hasData = true;
            _isFailure = !row.Succeeded;
            _failure = row.Failure;
            _ilIn = row.IlIn;
            _ilDut = row.IlDut;
            _ilOut = row.IlOut;
            _rlIn = row.RlIn;
            _rlDut = row.RlDut;
            _rlOut = row.RlOut;
            _dutPathM = row.DutPathM;
            _linkLengthM = row.LinkLengthM;
            RaiseAll();
        }

        /// <summary>清空本行测量结果，保留 IN/OUT 结构。</summary>
        public void Clear()
        {
            _hasData = false;
            _isFailure = false;
            _failure = null;
            _ilIn = double.NaN;
            _ilDut = double.NaN;
            _ilOut = double.NaN;
            _rlIn = double.NaN;
            _rlDut = double.NaN;
            _rlOut = double.NaN;
            _dutPathM = double.NaN;
            _linkLengthM = double.NaN;
            RaiseAll();
        }

        string MetricText(double value)
        {
            if (!_hasData) return string.Empty;
            if (_isFailure) return "—";
            if (double.IsNaN(value) || double.IsInfinity(value)) return string.Empty;
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }

        void RaiseAll()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasData)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFailure)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Failure)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IlInText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IlDutText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IlOutText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RlInText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RlDutText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RlOutText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DutPathText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LinkLengthText)));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
