using System;

namespace Detection.Vendor
{
    /// <summary>
    /// 串口传输抽象：真实串口与测试 Mock 共此接口（SDD §3.2）。
    /// 帧以完整 ASCII 字符串表示，含 &lt; &gt; 定界符；不含结束符（实测结束符可选，发送端统一省略）。
    /// 实现方负责：写失败 → <see cref="OcsTransportException"/>；读超时 → <see cref="OcsTransportException"/>。
    /// </summary>
    public interface IOcsTransport : IDisposable
    {
        bool IsOpen { get; }

        void Open();

        void Close();

        /// <summary>写入一条完整帧（含定界符）。</summary>
        void Write(string frame);

        /// <summary>读取一条完整帧（含定界符），超时抛传输异常。</summary>
        string ReadFrame(int timeoutMs);
    }
}
