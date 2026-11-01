# 分层与依赖不变量

- 依赖方向：`OFDR.Contracts` 不依赖任何平台程序集；`OFDR.Metrics` 只依赖 `Contracts`；`OFDR.Engine` 只依赖 `Contracts + Metrics`；`OFDR.Infrastructure` 依赖 `Contracts`；`OFDR.App` 是唯一组合根。
- **厂商类型、`DllImport`、原生句柄、协议字节结构只允许出现在 `ofdr_platform/src/OFDR.Infrastructure/`**。`Contracts`/`Metrics`/`Engine`/`App` 中出现即视为违规。
- 采集层不得向上暴露厂商 API 类型或网络/串口类型，只通过 `IScanSource`（`ScanRequest`、`StandardScan`）交互。
- 单位换算只允许在文档指定位置发生一次：int16→物理量在采集适配器（显式 `LsbScale`）；线性→dB 在处理层出口；基准修正在 `Metrics` 内部。UI 与持久化只格式化，不换算。
- 配置是不可变快照，运行中禁止热改；新增参数必须进入 `MetricsConfig`/对应 Profile，并纳入 `ComputeId()` 的哈希，随结果落盘。
- 不改变既有口径：8 峰判定、`MeasurementResult` 字段语义、结果 CSV 前 18 列。确需变更时必须同步 SRS、SDD、回归夹具与验证用例，并在提交说明中明确。
- 指标计算保持纯函数：不读文件、不碰 UI、不做 IO，输出只依赖输入数据、配置快照与基准值。
