using System;

namespace Gmix.Data
{
    /// <summary>group.json（ICD Part A §2.2）：组元数据，由编排进程在提交前写入。</summary>
    public sealed class GroupDocument
    {
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
