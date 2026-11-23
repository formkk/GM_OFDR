using System;
using GmixLx.Core;
using Gmix.Data;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 组产出端口：把一条路径的一次完整测量写成合法 L2 组（`staging/&lt;groupId&gt;/Rxxx/`，
    /// 每份扫描带 `.ready`；N&gt;1 时一并产出 `AVG/`）。
    /// </summary>
    /// <remarks>
    /// 真实实现需要 LGA50 扫描触发与峰值检测，语义待 TBD-D10；首版由测试替身实现，
    /// 不在编排层臆造帧格式或指标算法（规则 00：未确认项走明确拒绝/空实现路径）。
    /// </remarks>
    public interface IGroupProducer
    {
        /// <summary>
        /// 在 `stagingGroupDir` 下产出规划数量的扫描。实现方负责写文件与 `.ready`，
        /// 编排层只做校验、`group.json` 与原子提交。
        /// </summary>
        GroupProduction Produce(string stagingGroupDir, PathPlanEntry entry, int plannedCount);
    }

    /// <summary>组产出结果。`Acquisition` 由实现方按真实设备填写，编排层不臆造。</summary>
    public sealed class GroupProduction
    {
        public int ValidCount { get; set; }

        public string[] ScanIds { get; set; }

        /// <summary>N&gt;1 且成功平均时的 `AV-` 标识；N=1 或未产出平均为 null。</summary>
        public string AvgScanId { get; set; }

        /// <summary>`same_dist / resampled / invalid`；无平均时可为 null。</summary>
        public string Alignment { get; set; }

        public string[] Rejected { get; set; }

        /// <summary>L2 `group.json` 的采集信息；由实现方填写真实值。</summary>
        public AcquisitionInfo Acquisition { get; set; }

        /// <summary>失败时的错误类别（SRS §4.14）；成功为 null。</summary>
        public ErrorClass? ErrorClass { get; set; }

        public string Message { get; set; }

        public bool Succeeded
        {
            get { return ValidCount > 0; }
        }

        public static GroupProduction Failure(ErrorClass errorClass, string message)
        {
            return new GroupProduction
            {
                ValidCount = 0,
                ScanIds = new string[0],
                Rejected = new string[0],
                ErrorClass = errorClass,
                Message = message
            };
        }
    }
}
