using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using GmixLx.Orchestration;
using Microsoft.Win32;

namespace GmixLx.App.Wpf.ViewModels
{
    public sealed class MetricTableViewModel : INotifyPropertyChanged
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

        /// <summary>导出结果等状态文本；由 MainViewModel 转接到状态栏。</summary>
        public event Action<string> StatusChanged;

        /// <summary>至少有一行指标结果时才允许导出。</summary>
        public bool CanExport
        {
            get
            {
                foreach (MetricTableRowViewModel item in Rows)
                {
                    if (item.Source != null) return true;
                }
                return false;
            }
        }

        /// <summary>按 IN/OUT 把一条指标结果写入对应行；供后续采集编排调用。</summary>
        public void ApplyRow(MetricTableRow row)
        {
            if (row == null) throw new ArgumentNullException("row");
            foreach (MetricTableRowViewModel item in Rows)
            {
                if (item.InChannel == row.InChannel && item.OutChannel == row.OutChannel)
                {
                    item.Apply(row);
                    RaiseCanExport();
                    return;
                }
            }
        }

        public void Clear()
        {
            foreach (MetricTableRowViewModel item in Rows) item.Clear();
            RaiseCanExport();
        }

        public void Activate(MetricTableRowViewModel row)
        {
            if (row == null) return;
            RowActivated?.Invoke(row);
        }

        /// <summary>导出当前指标结果为 CSV；列序与格式化由 MetricTableCsv 统一给出。</summary>
        public void SaveCsv()
        {
            var rows = new List<MetricTableRow>();
            foreach (MetricTableRowViewModel item in Rows)
            {
                if (item.Source != null) rows.Add(item.Source);
            }
            if (rows.Count == 0)
            {
                Report("当前没有可导出的指标结果");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = "gmix_lx_metrics_"
                    + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                MetricTableCsv.Write(dialog.FileName, rows);
                Report("已导出：" + dialog.FileName);
            }
            catch (Exception ex)
            {
                Report("导出失败：" + ex.Message);
            }
        }

        void Report(string text)
        {
            Action<string> handler = StatusChanged;
            if (handler != null) handler(text);
        }

        void RaiseCanExport()
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs("CanExport"));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
