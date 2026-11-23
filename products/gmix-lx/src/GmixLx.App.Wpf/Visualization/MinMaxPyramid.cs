using System;

namespace GmixLx.App.Wpf.Visualization
{
    /// <summary>
    /// 曲线 min/max 分块金字塔：一次构建，缩放/平移时按块聚合，边缘逐点扫描。
    /// 纯计算类，不引用任何 UI 类型；与 WinForms 曲线控件算法保持一致。
    /// </summary>
    public sealed class MinMaxPyramid
    {
        public const int DefaultBlockSize = 512;

        readonly double[] _values;
        readonly double[] _blockMin;
        readonly double[] _blockMax;

        public MinMaxPyramid(double[] values, int blockSize = DefaultBlockSize)
        {
            if (values == null) throw new ArgumentNullException("values");
            if (blockSize <= 0) throw new ArgumentOutOfRangeException("blockSize");

            _values = values;
            BlockSize = blockSize;

            int blocks = (values.Length + blockSize - 1) / blockSize;
            _blockMin = new double[Math.Max(1, blocks)];
            _blockMax = new double[Math.Max(1, blocks)];

            for (int b = 0; b < blocks; b++)
            {
                int from = b * blockSize;
                int to = Math.Min(values.Length - 1, from + blockSize - 1);
                double lo = double.MaxValue;
                double hi = double.MinValue;
                for (int i = from; i <= to; i++)
                {
                    double v = values[i];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                _blockMin[b] = lo;
                _blockMax[b] = hi;
            }
        }

        public int Count
        {
            get { return _values.Length; }
        }

        public int BlockSize { get; private set; }

        /// <summary>取 [from,to] 的 min/max：长区间走块缓存，短区间与边缘逐点扫描。</summary>
        public void GetRange(int from, int to, out double lo, out double hi)
        {
            lo = double.MaxValue;
            hi = double.MinValue;
            if (to < from) return;

            if (to - from + 1 <= BlockSize * 2)
            {
                ScanDirect(from, to, ref lo, ref hi);
                return;
            }

            int firstFull = ((from + BlockSize - 1) / BlockSize) * BlockSize;
            int lastFull = ((to + 1) / BlockSize) * BlockSize;
            if (firstFull > to || lastFull <= firstFull)
            {
                ScanDirect(from, to, ref lo, ref hi);
                return;
            }

            ScanDirect(from, firstFull - 1, ref lo, ref hi);
            for (int b = firstFull / BlockSize; b <= lastFull / BlockSize - 1; b++)
            {
                if (b < 0 || b >= _blockMin.Length) continue;
                if (_blockMin[b] < lo) lo = _blockMin[b];
                if (_blockMax[b] > hi) hi = _blockMax[b];
            }
            ScanDirect(lastFull, to, ref lo, ref hi);
        }

        void ScanDirect(int from, int to, ref double lo, ref double hi)
        {
            int end = Math.Min(to, _values.Length - 1);
            for (int i = Math.Max(0, from); i <= end; i++)
            {
                double v = _values[i];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
        }
    }
}
