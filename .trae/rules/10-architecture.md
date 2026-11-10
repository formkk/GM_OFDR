# 分层与依赖不变量

## 产品主线 GM-IX（`GM-IX/src`）

- 依赖方向不可逆：`Gmix.Core` 只依赖 BCL；`Gmix.Vendor` 可依赖 Core；`Gmix.Acquirer` 依赖 Vendor + Core；`Gmix.Commit` 依赖 Core；`Gmix.Import` 依赖 Core；`Gmix.App` 依赖 Core + WinForms，**不得引用 `Gmix.Vendor`**。
- **厂商类型、`DllImport`、原生句柄、协议字节结构只允许出现在 `Gmix.Vendor/`**（`NativeApi`、`QtAcquisition`、`OfdrProcessor`、`Detector`）。`Gmix.App` / `Gmix.Core` 中出现即视为违规。
- 采集进程与编排进程通过文件边界交互（`_staging` + `.ready`）；内存对象不过进程边界。
- 单位换算只发生一次：int16→物理量在采集转换处；线性→dB 在检测出口；`rlOffsetK` 只在 UI 呈现层加一次。UI 与持久化只格式化，不换算。
- 配置是不可变快照，运行中禁止热改；新增参数必须进入配置快照并纳入 `configHash`，随结果落盘。
- 指标计算保持纯函数：`MetricsAggregator` 不读文件、不碰 UI、不做 IO，输出只依赖输入数据、配置快照与基准值。
- 不改变既有口径：ICD Part A 字段语义、`group.json` / `scan.json` 结构、CSV 列序、RL 符号与显示口径。确需变更时必须同步 ICD、SRS、SDD、夹具与验证用例。

## 第二设备线 LGA50

- L1 契约只定义帧（`CurveScan`）：轴 + dB 数组 + 元数据；组、参数、标定、平均归存储层。
- 适配器只构造有效帧；下游不得重算轴、不得插值、不得就地修改帧。
- `SourceType` 只允许 `Lianxun | HengLga50`；`Replay` 不进帧。
- 契约实现归口 `Gmix.Import` / `Gmix.Acquirer`，不落成跨进程共享类型。

## 预研平台 `ofdr_platform`（冻结）

- 保持 `OFDR.Contracts` / `Metrics` / `Engine` / `Infrastructure` / `App` 既有依赖方向；厂商类型只允许出现在 `OFDR.Infrastructure/`。
- 只接受缺陷修复与文档同步，不扩功能；其 `StandardScan` 字段不得回流产品线。
- 既有口径（8 峰判定、`MeasurementResult` 字段语义、结果 CSV 前 18 列）保持不变。
