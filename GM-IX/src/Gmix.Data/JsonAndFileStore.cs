using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Gmix.Data
{
    /// <summary>JSON 读写：System.Web.Script.Serialization，UTF-8 无 BOM。</summary>
    public static class JsonStore
    {
        static JavaScriptSerializer Create()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 200;
            return s;
        }

        public static void Write<T>(string path, T value)
        {
            string dir = Path.GetDirectoryName(path);
            if (dir != null && dir.Length > 0) Directory.CreateDirectory(dir);
            string json = Create().Serialize(value);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        public static T Read<T>(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            return Create().Deserialize<T>(json);
        }
    }

    /// <summary>裸 float64 小端数组文件。</summary>
    public static class ArrayStore
    {
        public static void WriteDoubles(string path, double[] values)
        {
            if (values == null) throw new ArgumentNullException("values");
            string dir = Path.GetDirectoryName(path);
            if (dir != null && dir.Length > 0) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                var buf = new byte[8];
                foreach (double v in values)
                {
                    BitConverter.GetBytes(v).CopyTo(buf, 0);
                    fs.Write(buf, 0, 8);
                }
            }
        }

        public static double[] ReadDoubles(string path, long expectedCount)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length % 8 != 0)
                throw new InvalidDataException("数组长度不是 8 的整数倍: " + path);
            int count = bytes.Length / 8;
            if (expectedCount > 0 && count != expectedCount)
                throw new InvalidDataException("数组点数不符: " + count + " != " + expectedCount);
            var values = new double[count];
            for (int i = 0; i < count; i++)
                values[i] = BitConverter.ToDouble(bytes, i * 8);
            return values;
        }
    }

    /// <summary>原始 int16 小端、4 通道交织。</summary>
    public static class RawStore
    {
        public static void ValidateLength(string path, long expectedBytes)
        {
            long actual = new FileInfo(path).Length;
            if (actual != expectedBytes)
                throw new InvalidDataException("raw.bin 长度不符: " + actual + " != " + expectedBytes);
            if (actual % 8 != 0)
                throw new InvalidDataException("raw.bin 不是 8 字节帧对齐");
        }

        public static short[] ReadFrames(string path, out int frames)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length % 8 != 0) throw new InvalidDataException("raw.bin 不是 8 字节帧对齐");
            frames = bytes.Length / 8;
            var samples = new short[frames * 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples;
        }

        /// <summary>取第 ch 通道（0..3）全部采样。</summary>
        public static double[] Channel(short[] samples, int ch)
        {
            int frames = samples.Length / 4;
            var result = new double[frames];
            for (int i = 0; i < frames; i++) result[i] = samples[i * 4 + ch];
            return result;
        }
    }

    public static class HashUtil
    {
        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return "sha256:" + sb.ToString();
            }
        }

        public static string Sha256Text(string text)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return "sha256:" + sb.ToString();
            }
        }
    }
}
