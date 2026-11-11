using System;

namespace Gmix.Data
{
    /// <summary>group.json（ICD Part A §2.2）：组元数据，由编排进程在提交前写入。</summary>
    public sealed class GroupDocument
    {
        /// <summary>归一依据的 L1 契约版本（《统一数据契约》），记录生成时定型（R-05）。</summary>
        public const string ContractVersion = "0.7";

        /// <summary>L1 契约版本；旧数据缺失时为 null，表示早于该字段引入。</summary>
        public string contractVersion { get; set; }
        public string schemaVersion { get; set; }
        public string groupId { get; set; }
        public string createdAtUtc { get; set; }
        /// <summary>complete / incomplete / failed</summary>
        public string status { get; set; }
        /// <summary>计划平均次数 N</summary>
        public int averageCount { get; set; }
        public string[] scanIds { get; set; }
        public int validCount { get; set; }
        public string[] rejected { get; set; }
        public string avgMode { get; set; }
        /// <summary>same_dist / resampled / invalid</summary>
        public string alignment { get; set; }
        public string avgScanId { get; set; }
        public string calibrationId { get; set; }
        public string configHash { get; set; }
        public AcquisitionInfo acquisition { get; set; }
    }
}
