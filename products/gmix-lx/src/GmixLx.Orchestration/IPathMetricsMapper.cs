using GmixLx.Core;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 把"本设备原生坐标下算出的 11 列"翻译到契约参考系（LGA50）。
    /// 只挪动坐标与量纲，不重算峰、不重算 RL；失败行原样返回。
    /// </summary>
    public interface IPathMetricsMapper
    {
        PathMetrics ToReport(PathMetrics metrics);
    }
}
