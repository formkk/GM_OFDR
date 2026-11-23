using System;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// 指标表的曲线源：各设备交出自己的曲线（轴由接入端给出，禁止插值/重算），
    /// 指标表用同一套 8 峰算法，按 <see cref="GmixLx.Core.CurveMetricsConfig.MetersPerRow"/>
    /// 把"米"换算成该设备的行号。跨设备差异只在曲线源与指标翻译层各出现一次。
    /// </summary>
    public interface ICurveSource : IDisposable
    {
        /// <summary>
        /// 参考系显示轴：x(i) = <see cref="AxisOffsetM"/> + i × <see cref="AxisScaleM"/>（米）。
        /// 返回 0 表示按数据步距、无偏移（LGA50 自身即参考系）。
        /// </summary>
        double AxisScaleM { get; }

        double AxisOffsetM { get; }

        /// <summary>开始一次采集会话（RAW 模式、清场、就绪检查等由实现负责）。</summary>
        void Begin();

        /// <summary>
        /// 取一条曲线（dB）。返回 null 表示本样本不可用，<paramref name="failure"/> 给出原因，
        /// 指标表据此判该行失败。设备/传输/协议类故障必须抛
        /// <see cref="GmixLx.Core.DetectionException"/>，不得静默返回 null。
        /// </summary>
        double[] TryNextCurve(out string failure);

        /// <summary>结束会话；幂等，且不得覆盖主流程已抛出的异常。</summary>
        void End();
    }
}
