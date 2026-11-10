using System;
using System.IO;
using System.IO.Compression;

namespace Gmix.Acquirer
{
    /// <summary>
    /// 合成数据模板：从 2026-11-06 实机 20 帧 RAW 中选出的代表帧
    /// （GR-20261106-1550-001/R004，峰位/峰高最接近 20 帧中位数），gzip 后嵌入程序集。
    /// 合成模式在该帧上叠加实测量级的噪声，使峰值位置、电平与实机一致。
    /// </summary>
    internal static class SyntheticTemplate
    {
        const string ResourceName = "Gmix.Acquirer.Synthetic.raw_template.bin.gz";
        static short[] _samples;

        public static short[] Samples()
        {
            if (_samples == null) _samples = Load();
            return _samples;
        }

        static short[] Load()
        {
            using (Stream raw = typeof(SyntheticTemplate).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (raw == null)
                    throw new InvalidOperationException("缺少合成数据模板资源：" + ResourceName);

                byte[] bytes;
                using (var gzip = new GZipStream(raw, CompressionMode.Decompress))
                using (var buffer = new MemoryStream())
                {
                    gzip.CopyTo(buffer);
                    bytes = buffer.ToArray();
                }

                if (bytes.Length % 2 != 0)
                    throw new InvalidDataException("合成数据模板字节数不是 int16 的整数倍");

                var samples = new short[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                return samples;
            }
        }
    }
}
