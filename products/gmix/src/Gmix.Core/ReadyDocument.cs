using System;

namespace Gmix.Data
{
    /// <summary>`.ready` 交接标记（ICD Part B §7.1）：采集进程写完一次扫描后原子写入，提交前删除。</summary>
    public sealed class ReadyDocument
    {
        public string schemaVersion { get; set; }
        public string scanId { get; set; }
        public int repeatIndex { get; set; }
        public string folder { get; set; }
        public ReadyFileInfo[] files { get; set; }
        public string acquirerVersion { get; set; }
        public string finishedAtUtc { get; set; }
    }

    public sealed class ReadyFileInfo
    {
        public string path { get; set; }
        public long bytes { get; set; }
        public string sha256 { get; set; }
    }
}
