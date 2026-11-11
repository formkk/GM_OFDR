using System;
using System.Collections.Generic;
using Detection.Vendor;

namespace Detection.Tests
{
    /// <summary>
    /// 内存传输替身：不碰真实串口，按预设响应回放，并记录所有已发送帧。
    /// 用于验证命令构造、回包解析、错误映射与串行化，不含真实时序。
    /// </summary>
    internal sealed class MemoryOcsTransport : IOcsTransport
    {
        private readonly Dictionary<string, string> _responses = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<string> _script = new Queue<string>();
        private readonly List<string> _written = new List<string>();
        private bool _open;

        /// <summary>已发送的完整帧（按发送顺序）。</summary>
        public IReadOnlyList<string> Written
        {
            get { return _written; }
        }

        /// <summary>最近一次发送的帧。</summary>
        public string LastWritten
        {
            get { return _written.Count == 0 ? null : _written[_written.Count - 1]; }
        }

        /// <summary>为 true 时读取一律按超时失败。</summary>
        public bool SimulateReadTimeout { get; set; }

        /// <summary>为 true 时写入一律按传输失败。</summary>
        public bool SimulateWriteFailure { get; set; }

        public bool IsOpen
        {
            get { return _open; }
        }

        public void Open()
        {
            _open = true;
        }

        public void Close()
        {
            _open = false;
        }

        /// <summary>按请求原文预设回包。</summary>
        public MemoryOcsTransport Map(string requestFrame, string responseFrame)
        {
            _responses[requestFrame] = responseFrame;
            return this;
        }

        /// <summary>入队一条脚本化回包；null 表示读到超时。</summary>
        public MemoryOcsTransport Enqueue(string responseFrame)
        {
            _script.Enqueue(responseFrame);
            return this;
        }

        public void Write(string frame)
        {
            if (!_open) throw new OcsTransportException("串口未打开", frame, null, "先 Open", null);
            if (SimulateWriteFailure) throw new OcsTransportException("模拟写失败", frame, null, "重试", null);
            _written.Add(frame);
        }

        public string ReadFrame(int timeoutMs)
        {
            if (SimulateReadTimeout)
                throw new OcsTransportException("模拟读超时（" + timeoutMs + " ms）", LastWritten, null, "确认设备在线", null);

            if (_script.Count > 0)
            {
                string scripted = _script.Dequeue();
                if (scripted == null)
                    throw new OcsTransportException("模拟读超时（" + timeoutMs + " ms）", LastWritten, null, "确认设备在线", null);
                return scripted;
            }

            string response;
            if (LastWritten != null && _responses.TryGetValue(LastWritten, out response)) return response;

            throw new OcsTransportException("未预设该请求的回包：" + LastWritten, LastWritten, null, "补充 Mock 映射", null);
        }

        public void Dispose()
        {
            _open = false;
        }
    }
}
