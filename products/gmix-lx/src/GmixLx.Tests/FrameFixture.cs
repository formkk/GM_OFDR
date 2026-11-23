using System;
using System.Collections.Generic;
using System.IO;

namespace GmixLx.Tests
{
    /// <summary>一条夹具记录：标签、设备、请求帧、回包帧（或超时标记）。</summary>
    internal sealed class FrameSample
    {
        public string Label;
        public string Device;
        public string Request;
        public string Response;
        public bool ExpectTimeout;

        public int LineNumber;
    }

    /// <summary>
    /// 读取 tests/fixtures/ocs/protocol_frames.txt（实机抓取样例）。
    /// 行格式：label | device | request | response；# 开头为注释。
    /// 回包为 (no response ...) 时标记为预期超时。
    /// </summary>
    internal static class FrameFixture
    {
        public const string RelativePath = "protocol_frames.txt";

        public static string ResolvePath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "ocs", RelativePath);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        public static List<FrameSample> Load(string path)
        {
            var list = new List<FrameSample>();
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                string[] parts = line.Split('|');
                if (parts.Length < 4) continue;

                var sample = new FrameSample
                {
                    Label = parts[0].Trim(),
                    Device = parts[1].Trim(),
                    Request = parts[2].Trim(),
                    Response = parts[3].Trim(),
                    LineNumber = i + 1
                };
                sample.ExpectTimeout = sample.Response.StartsWith("(", StringComparison.Ordinal);
                list.Add(sample);
            }
            return list;
        }

        /// <summary>把完整帧 &lt;XXX&gt; 转为命令体 XXX；非完整帧返回 null。</summary>
        public static string ToCommandBody(string frame)
        {
            if (string.IsNullOrEmpty(frame) || frame.Length < 2) return null;
            if (frame[0] != '<' || frame[frame.Length - 1] != '>') return null;
            return frame.Substring(1, frame.Length - 2);
        }
    }
}
