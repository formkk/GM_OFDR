using System;
using System.Collections.ObjectModel;
using GmixLx.Orchestration;

namespace GmixLx.App.Wpf.ViewModels
{
    public sealed class MetricTableViewModel
    {
        public MetricTableViewModel()
        {
            Rows = new ObservableCollection<MetricTableRowViewModel>();
            for (int group = 1; group <= 8; group++)
            {
                for (int outChannel = 1; outChannel <= 8; outChannel++)
                {
                    Rows.Add(new MetricTableRowViewModel(group, group, outChannel));
                }
            }
        }

        public ObservableCollection<MetricTableRowViewModel> Rows { get; private set; }

        public event Action<MetricTableRowViewModel> RowActivated;

        /// <summary>按 IN/OUT 把一条指标结果写入对应行；供后续采集编排调用。</summary>
        public void ApplyRow(MetricTableRow row)
        {
            if (row == null) throw new ArgumentNullException("row");
            foreach (MetricTableRowViewModel item in Rows)
            {
                if (item.InChannel == row.InChannel && item.OutChannel == row.OutChannel)
                {
                    item.Apply(row);
                    return;
                }
            }
        }

        public void Clear()
        {
            foreach (MetricTableRowViewModel item in Rows) item.Clear();
        }

        public void Activate(MetricTableRowViewModel row)
        {
            if (row == null) return;
            RowActivated?.Invoke(row);
        }
    }
}
