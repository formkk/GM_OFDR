namespace Gmix.OcsVerify
{
    /// <summary>
    /// OCS 核验域逻辑库（纯函数）：意图表模型、期望窗口派生、锚点归一、
    /// 代价矩阵与全局分配、判定输出，以及台内/首件基线对象模型与文件型存储。
    /// 约束：只依赖 BCL；不读文件、不碰 UI、不做 IO（对标 MetricsAggregator 纯度约束）。
    /// </summary>
    internal static class Domain
    {
        public const string Version = "0.0.1";
    }
}
