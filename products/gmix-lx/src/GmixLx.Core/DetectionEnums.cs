namespace GmixLx.Core
{
    /// <summary>批次状态（SRS §4.8）。终态：Completed / CompletedWithFailures / Aborted。</summary>
    public enum BatchState
    {
        Idle = 0,
        SelfCheck = 1,
        Running = 2,
        Paused = 3,
        Aborting = 4,
        Aborted = 5,
        Completed = 6,
        CompletedWithFailures = 7
    }

    /// <summary>路径状态（SRS §4.8）。终态：Computed / Failed / NotAttempted。</summary>
    public enum PathState
    {
        Pending = 0,
        Switching = 1,
        Settling = 2,
        Scanning = 3,
        Computed = 4,
        Failed = 5,
        NotAttempted = 6
    }

    /// <summary>连通性四值枚举（SRS §4.5.1），取值唯一且互斥。</summary>
    public enum Connectivity
    {
        Ok = 0,
        NoPeak = 1,
        InvalidFrame = 2,
        NotComputed = 3
    }

    /// <summary>标定状态（SRS §6.2）。</summary>
    public enum CalibrationStatus
    {
        Calibrated = 0,
        Uncalibrated = 1,
        Mismatch = 2
    }

    /// <summary>关注提示标记（SRS §4.6）。只做界面呈现，不改变测量值、不产生合格结论。</summary>
    public enum AttentionFlag
    {
        MeasurementFailed = 0,
        CalibrationInvalid = 1,
        NoPeak = 2,
        PeakUnexpected = 3,
        OutOfReference = 4,
        BaselineMissing = 5,
        AvgIncomplete = 6,
        CnnSuspicious = 7
    }
}
