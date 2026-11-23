using System;
using System.Collections.Generic;
using GmixLx.Core;
using GmixLx.Vendor;

namespace GmixLx.Orchestration
{
    /// <summary>64 路指标表的运行参数。</summary>
    public sealed class MetricTableOptions
    {
        /// <summary>每条路径的采样次数（&gt;1 时按成功样本取平均，与 demo 一致）。</summary>
        public int SamplesPerPath = 1;

        /// <summary>切路后固定等待（ms）。实测 `STOP→切路→START` 下 0 ms 即得新状态，默认 50 ms 吸收抖动。</summary>
        public int SettleMs = 50;

        /// <summary>FIBER_START 对应的轴原点（m），默认 0。</summary>
        public double OriginM = 0.0;

        /// <summary>曲线窗口的缺省视窗（本设备轴，米）；EndM ≤ StartM 时按全览。</summary>
        public double ViewStartM = 0.0;

        public double ViewEndM = 0.0;
    }

    /// <summary>指标表的一行：IN/OUT 通道 + 11 列指标。</summary>
    public sealed class MetricTableRow
    {
        public int InChannel;
        public int OutChannel;

        public double IlIn = double.NaN;
        public double IlDut = double.NaN;
        public double IlOut = double.NaN;
        public double RlIn = double.NaN;
        public double RlDut = double.NaN;
        public double RlOut = double.NaN;

        /// <summary>DUT 光程（m）。</summary>
        public double DutPathM = double.NaN;

        /// <summary>链路长度（m）。</summary>
        public double LinkLengthM = double.NaN;

        public int ValidSamples;
        public long DurationMs;

        /// <summary>
        /// 该路径最后一次成功解析的电平曲线（dB_rel，长度 = 点数）。
        /// 仅驻留内存用于「双击查看距离域曲线」，不落盘、不导出。
        /// </summary>
        public double[] LevelsDb;

        /// <summary>采集时使用的行→米系数（画距离轴用）。</summary>
        public double MetersPerRow = 0.000185;

        /// <summary>采集时实际使用的峰值阈值（画峰标记用）；绝对值模式或相对模式解析后的结果。</summary>
        public double PeakThresholdDb = double.NaN;

        /// <summary>采集时实际使用的峰合并间距（行），保证曲线窗口的峰标记与指标表同口径。</summary>
        public int PeakMergeGapRows = 50;

        /// <summary>曲线窗口的缺省视窗（参考系，米）；EndM ≤ StartM 时按全览。</summary>
        public double ViewStartM;

        public double ViewEndM;

        /// <summary>参考系显示轴：x(i) = AxisOffsetM + i × AxisScaleM（米）。</summary>
        public double AxisScaleM;

        public double AxisOffsetM;

        /// <summary>null 表示该行有效；否则为不可计算/采集失败的原因。</summary>
        public string Failure;

        public bool Succeeded
        {
            get { return Failure == null; }
        }
    }

    /// <summary>整批指标表结果。</summary>
    public sealed class MetricTableResult
    {
        public List<MetricTableRow> Rows = new List<MetricTableRow>();
        public long DurationMs;

        public int ValidPaths
        {
            get
            {
                int n = 0;
                foreach (MetricTableRow row in Rows) if (row.Succeeded) n++;
                return n;
            }
        }

        public int FailedPaths
        {
            get { return Rows.Count - ValidPaths; }
        }

        /// <summary>
        /// 失败原因摘要（按条数降序，最多前 <paramref name="maxItems"/> 项），用于状态栏与日志。
        /// 全部有效时返回 null。
        /// </summary>
        public string DescribeFailures(int maxItems = 3)
        {
            if (FailedPaths == 0) return null;

            var counts = new Dictionary<string, int>();
            var order = new List<string>();
            foreach (MetricTableRow row in Rows)
            {
                if (row.Succeeded) continue;
                string key = row.Failure ?? "未注明原因";
                if (!counts.ContainsKey(key)) { counts[key] = 0; order.Add(key); }
                counts[key]++;
            }

            order.Sort(delegate(string a, string b) { return counts[b].CompareTo(counts[a]); });

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < order.Count && i < maxItems; i++)
            {
                if (i > 0) text.Append("；");
                text.Append(order[i]).Append(" ×").Append(counts[order[i]]);
            }
            if (order.Count > maxItems) text.Append("；等 ").Append(order.Count).Append(" 类原因");
            return text.ToString();
        }
    }

    /// <summary>
    /// 64 路指标表运行器（MVP）：逐路 `切路 → 稳定 → 取一条曲线 → 算 11 列`。
    /// 曲线只在内存中使用，不落盘；设备差异由 <see cref="ICurveSource"/> 吸收，
    /// 跨设备口径由 <see cref="IPathMetricsMapper"/> 翻译，本类不感知底层设备。
    /// </summary>
    public sealed class MetricTableRunner
    {
        readonly ICurveSource _source;
        readonly Ocs1x8Client _ocs1x8;
        readonly Ocs8x8Client _ocs8x8;
        readonly CurveMetricsConfig _metricsConfig;
        readonly IPathMetricsMapper _mapper;

        public MetricTableRunner(ICurveSource source, Ocs1x8Client ocs1x8, Ocs8x8Client ocs8x8,
            CurveMetricsConfig metricsConfig, IPathMetricsMapper mapper = null)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (ocs1x8 == null) throw new ArgumentNullException("ocs1x8");
            if (ocs8x8 == null) throw new ArgumentNullException("ocs8x8");
            if (metricsConfig == null) throw new ArgumentNullException("metricsConfig");

            _source = source;
            _ocs1x8 = ocs1x8;
            _ocs8x8 = ocs8x8;
            _metricsConfig = metricsConfig;
            _mapper = mapper;
        }

        public MetricTableResult Run(int inFrom, int inTo, int outFrom, int outTo,
            MetricTableOptions options, Action<MetricTableRow> onRow, System.Threading.CancellationToken cancel)
        {
            if (options == null) throw new ArgumentNullException("options");
            ValidateRange(inFrom, inTo, "in");
            ValidateRange(outFrom, outTo, "out");
            if (options.SamplesPerPath <= 0) throw new ArgumentOutOfRangeException("SamplesPerPath");
            if (options.SettleMs < 0 || options.SettleMs > 500) throw new ArgumentOutOfRangeException("SettleMs");

            var result = new MetricTableResult();
            long started = Environment.TickCount64;

            _source.Begin();

            int lastIn = -1;
            try
            {
                for (int inChannel = inFrom; inChannel <= inTo; inChannel++)
                {
                    for (int outChannel = outFrom; outChannel <= outTo; outChannel++)
                    {
                        cancel.ThrowIfCancellationRequested();

                        var row = new MetricTableRow { InChannel = inChannel, OutChannel = outChannel };
                        row.ViewStartM = options.ViewStartM;
                        row.ViewEndM = options.ViewEndM;
                        row.AxisScaleM = _source.AxisScaleM > 0 ? _source.AxisScaleM : _metricsConfig.MetersPerRow;
                        row.AxisOffsetM = _source.AxisOffsetM;
                        long rowStarted = Environment.TickCount64;

                        try
                        {
                            // 1×8 只在 IN 变化时切换（实测切换 17~67 ms，能省则省）
                            if (inChannel != lastIn)
                            {
                                _ocs1x8.SetOutput(inChannel);
                                lastIn = inChannel;
                            }
                            // 8×8 用整矩阵命令只连通当前一路：单路命令会因"输出已被占用"被判冲突，
                            // 而整矩阵一次写清、状态确定（实测整矩阵 ~148 ms）
                            var matrix = new int[8];
                            matrix[inChannel - 1] = outChannel;
                            _ocs8x8.SetMatrix(matrix);

                            if (options.SettleMs > 0)
                            {
                                System.Threading.Thread.Sleep(options.SettleMs);
                            }

                            Accumulate(row, inChannel, outChannel, options, cancel);
                        }
                        catch (DetectionException ex)
                        {
                            // 测量类错误只废掉本行；设备/传输/协议类错误中止整批
                            if (ex.ErrorClass != ErrorClass.Measurement) throw;
                            row.Failure = ex.Message;
                        }

                        row.DurationMs = Environment.TickCount64 - rowStarted;
                        result.Rows.Add(row);
                        if (onRow != null) onRow(row);
                    }
                }
            }
            finally
            {
                _source.End();
                result.DurationMs = Environment.TickCount64 - started;
            }

            return result;
        }

        void Accumulate(MetricTableRow row, int inChannel, int outChannel,
            MetricTableOptions options, System.Threading.CancellationToken cancel)
        {
            int valid = 0;
            double ilIn = 0, ilDut = 0, ilOut = 0, rlIn = 0, rlDut = 0, rlOut = 0, dutPath = 0, link = 0;
            string lastFailure = null;

            for (int s = 0; s < options.SamplesPerPath; s++)
            {
                cancel.ThrowIfCancellationRequested();

                string failure;
                double[] levels = _source.TryNextCurve(out failure);
                if (levels == null)
                {
                    lastFailure = failure ?? "无曲线";
                    continue;
                }

                // 留一份曲线给界面双击查看（仅内存，不落盘）
                row.LevelsDb = levels;
                row.MetersPerRow = _metricsConfig.MetersPerRow;
                row.PeakThresholdDb = CurveMetrics.ResolveThreshold(levels, _metricsConfig);
                row.PeakMergeGapRows = _metricsConfig.RowsOfMeters(_metricsConfig.PeakMergeGapM);

                PathMetrics m = CurveMetrics.Compute(levels, inChannel, outChannel, _metricsConfig);
                if (!m.Succeeded)
                {
                    lastFailure = m.Failure;
                    continue;
                }
                if (_mapper != null) m = _mapper.ToReport(m);

                ilIn += m.IlIn; ilDut += m.IlDut; ilOut += m.IlOut;
                rlIn += m.RlIn; rlDut += m.RlDut; rlOut += m.RlOut;
                dutPath += m.DutPathM; link += m.LinkLengthM;
                valid++;
            }

            row.ValidSamples = valid;
            if (valid == 0)
            {
                row.Failure = lastFailure ?? "无有效样本";
                return;
            }

            row.IlIn = ilIn / valid;
            row.IlDut = ilDut / valid;
            row.IlOut = ilOut / valid;
            row.RlIn = rlIn / valid;
            row.RlDut = rlDut / valid;
            row.RlOut = rlOut / valid;
            row.DutPathM = dutPath / valid;
            row.LinkLengthM = link / valid;
        }

        static void ValidateRange(int from, int to, string name)
        {
            if (from < 1 || to < 1 || from > to || to > 8)
            {
                throw new ArgumentOutOfRangeException(name, "通道范围必须在 1~8 且 from ≤ to");
            }
        }
    }
}
