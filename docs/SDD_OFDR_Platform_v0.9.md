# OFDR 测试平台软件设计说明书（SDD）

| 项 | 内容 |
| --- | --- |
| 版本 | V0.9（架构基线草案）+ 一期实施回填 + 距离域显示回填（2026-10-31）+ 审核修订（2026-11-01） |
| 状态 | 可评审；标记 `TBD` 处待资料/决策 |
| 对应需求 | `SRS_OFDR_Platform_v0.9.md` |
| 实施状态 | 一期 v0.1.0（`src/OFDR.Platform.sln`）：Contracts / Metrics / Engine / Infrastructure / Replay / App / Verify 七个程序集；整机 TCP + 离线合成闭环已实现并验证；距离域只读曲线、事件表与双光标已落地 |
| 设计目标 | 层次清晰、模块化、硬件无关、可离线验证 |

> **文档定位**：本文件是软件设计说明书（SDD），回答“怎么实现”。需求条目与验收标准以 `SRS_OFDR_Platform_v0.9.md` 为准，本文只引用需求编号；§13 的构建与验证记录不属于设计规范，评审结论见 `REVIEW_OFDR_Platform_v0.1.0.md`。

---

## 0. 参考材料与架构总览

### 0.1 四层架构基线（原始架构图）

![OFDR 平台四层架构](../references/ofdr_platform.png)

四层为：**数据采集 → 数据处理 → OFDR 标准扫描数据获取（标准化） → 测试控制与结果计算**。一期只落地其中三条：

- 数据采集：仅整机 TCP（`OfdrTcpSource`）；
- 标准扫描数据获取：`StandardScan` 契约（唯一枢轴）；
- 测试控制与结果计算：`TestEngine` + `MetricCalculator`。
- 数据处理：整机路径直接输出 dBm 谱，一期不实现 `ofdr.dll` 处理层（二期随 FPGA 采集板路径落地）。

图左侧“昊衡 LGA50 API / 映讯 QT2144.dll”为历史采集适配示例；二期采集侧为独立 FPGA 开发板或定制板，经网络接入，形态与协议未定（SRS TBD-15），一期均不接入。

### 0.2 参考资料（仅列与本设计直接相关者）

| 资料 | 内容 | 在本设计中的用途 |
| --- | --- | --- |
| `OFDRDataReceiver _demo` | 旧 WinForms 实现 + `config.txt` + 历史 run | 行为基线：公式、协议、默认值、CSV 列序；对应 §2.3 映射与 §10 迁移用例 |
| `references/qt2144-rs` | 旧采集卡 SDK 与数据格式说明 | 仅作帧格式/通道交织参考；采集板形态未定，不作为二期既定适配器（SRS TBD-15） |
| `references/ofdr-0.1.1-release` | 处理库（P/S/A → 轴/功率谱）与头文件 | 二期 `OfdrProcessor` 适配器设计参考（§3、§5、§9） |
| `references/detect_1.0` | 结果库（峰值/IL/RL 参考实现）与头文件 | 二期交叉验证实现参考（§5、§10） |
| `references/ofdr_platform.png` | 四层架构图 | §0.1、§2 的架构依据 |
| 初步调研（未随仓库提供） | 产品背景与光链路说明 | 仅背景，不构成本平台软件需求；指标口径以 SRS 为准 |
| `references/keysight` | N7700 IL Engine 用户指南 | 测法与术语参照，**非 OFDR 算法来源**；其“保存原始数据以便重算”对应 SRS FR-DATA-01 |
| `references/luna`（Luna OBR 4600/6200 官方 PDF + 对照总结） | 距离域曲线、事件列表、双光标读数布局 | 仅 UI 形态参考，不涉及算法，**非需求来源**；对应 SRS FR-VIS-01~06。明细与逐项对照见下方 |

Luna 资料明细（`references/luna/`，不参与构建、不参与算法）：

- `OBR_6200_Series_User_Guide_6225_6235_Rev001.pdf`（DOC000015 Rev001，32 页）：一期距离域布局的主要参照。上下分区与事件表 p.7；光标四模式 p.8–11；事件表、事件类型、宽度、标记与双击 p.12–14；文件与配置 p.15–26。
- `OBR_4600_User_Guide_UG6_SW3.12.0.pdf`（UG6 / SW3.12.0，236 页）：主窗口六区与双图 p.18–19；光标、图例、积分区 p.72–73；LPA 事件列表字段 p.77、p.85（**不采纳**其 Pass/Fail 与多 trace）。
- `LUNA_Data_Sheet_OBR_4600_V2.pdf`（V2，4 页）、`LUNA_Data_Sheet_6225_2023.pdf`、`LUNA_Data_Sheet_6235_2023.pdf`：仅用于确认功能边界；其性能参数不是本平台指标。
- 逐项对照与“明确不照搬”清单：`references/luna/Luna_OBR_UI_参考总结.md`（整理日期 2026-11-01，**晚于** §13.5 的 2026-10-31 落地回填，属事后溯源，不作为设计当时的证据）。

> 引用口径：一期源码中检索 `DllImport`、`qt_acq`、`ofdr.dll`、`detect.dll` 均无匹配，上述厂商资料不参与一期运行。Keysight 文档为扫波长 IL 测法，无距离域与峰值检测，不得表述为“按 Keysight 算法实现”；其 Reference 基线对应本设计的 SRI/SRO/SRB/SRC 基准快照。Luna 资料只作 SDD 设计层的 UI 形态参照，未进入 SRS §1.4；FR-VIS-01~06 的来源标签仍为 `[DEMO]`/`[决策]`。参考资料的需求侧用途与来源标签见 SRS §1.4、§1.5。

---

## 1. 设计目标与约束

### 1.1 目标

1. **单一数据枢轴**：`StandardScan` 是唯一跨层数据契约，采集、处理、指标、存储、UI 均只依赖它。
2. **硬件无关**：任何厂商库/DLL（采集板 SDK、`ofdr`、`detect`）只出现在 `Infrastructure` 适配器内部，Domain 与 UI 不出现其符号。
3. **可离线运行**：回放源、合成谱源、模拟仪表与真实实现同接口，CI 与算法开发不依赖硬件。
4. **可替换算法**：峰值检测与 IL/RL 为纯函数，可切换实现并与 `detect.dll` 交叉验证。
5. **可追溯**：结果携带配置快照与校准 ID，支持复算与追责。

### 1.2 约束

- 单进程、x64、**.NET 8**（类库 `net8.0`，WinForms `net8.0-windows`）；不引入消息队列/微服务/数据库服务（一期）。
- 一期（v0.1.0）只实现整机 TCP 采集路径与离线合成闭环；不引用任何厂商 DLL，不引入 `System.IO.Ports`。
- 厂商库无源码，遵循“调用后必须 free”的生命周期规则（SRS CON-02）。
- 采集侧存在两类接入方式（采集板网络协议、整机 TCP 文本协议），必须适配器化；一期仅落地 TCP。
- 现状吞吐基线约 800 KB/70 ms；采集板单段可达数十 MB，设计按流式考虑。

### 1.3 质量属性优先级

可测试性 ≥ 可维护性 > 性能 > 可移植性。理由：精度指标尚未确定，先保证能回放、能对比、能替换。

---

## 2. 架构总览

### 2.1 分层与依赖方向

```
┌──────────────────────── OFDR.App (WinForms UI) ─────────────────────────────┐
│ MainForm · 参数 · 指标表 / 距离域（曲线+事件表+双光标）· 日志/状态（组合根）      │
└───────────────────────────────▲──────────────────────────────────────────────┘
                                │ 事件 / 只读结果
┌────────────────────── OFDR.Engine (TestEngine) ─────────────────────────────┐
│ 状态机 · IN×OUT 遍历 · 采样/超时 · 代次 · 取消 · 逐通道聚合                    │
└───────▲──────────────▲──────────────────▲──────────────────▲───────────────┘
        │              │                  │                  │
┌───────┴────┐ ┌───────┴──────┐ ┌────────┴───────┐ ┌────────┴───────────┐
│ OFDR.     │ │ OFDR.        │ │ OFDR.          │ │ OFDR.Infrastructure│
│ Metrics   │ │ Contracts    │ │ Replay         │ │ Tcp / Switches /   │
│ 峰值/IL/RL│ │ 契约/校验/   │ │ 合成谱/CSV     │ │ Config / Persistence│
│ 纯函数     │ │ 序列化       │ │ 回放读取       │ │ (无厂商 DLL)        │
└───────────┘ └──────────────┘ └────────────────┘ └────────────────────┘
```

依赖规则（可静态检查）：

- `OFDR.Contracts` 不依赖任何其他平台程序集，不含 IO/网络/UI 类型。
- `OFDR.Metrics` 只依赖 `OFDR.Contracts`；不读文件、不碰 UI、不做 IO。
- `OFDR.Metrics` 另含仅用于显示的派生计算（`EventExtractor`、`TraceEnvelope`），不改变指标口径与结果 CSV 合同。
- `OFDR.Engine` 只依赖 Contracts + Metrics；通过 `IScanSource`/开关接口与实现解耦。
- `OFDR.Infrastructure` 依赖 Contracts；实现 TCP 源、模拟开关、配置读取、CSV/日志落盘。一期无厂商类型。
- `OFDR.Replay` 依赖 Contracts + Metrics；提供合成谱源与历史 CSV 读取。
- `OFDR.App` 是组合根：允许引用 Infrastructure/Replay 的具体实现来装配，但不实现业务规则。
- 程序集数量 7 个（含 `OFDR.Verify` 验证控制台），避免过度切分。

> 现状差异：`OFDR.Infrastructure.csproj` 多出一个对 `OFDR.Metrics` 的项目引用（源码未使用），属待清理的多余引用，不改变上表的依赖方向。

### 2.2 部署视图

单机单进程，模块化为程序集。一期部署只有平台自身程序集（.NET 8 运行时），不含任何厂商 DLL；不再有 `runtimes/win-x64/native` 依赖，也不执行 DLL 自检（§9 的清单为二期范围）。日志与结果默认写 `logs/`（应用目录或用户指定目录）。

### 2.3 与现状的映射

| 现状 | 一期落地（实际类型/文件） |
| --- | --- |
| `DataReceiver` 的 TCP 握手、收发与切帧 | `OFDR.Infrastructure.Tcp.OfdrTcpSource` |
| `DataReceiver` 的 ASCII 行解析 + `FrameData` | `OfdrTcpSource.Decode` → `StandardScan`（消费线程解析） |
| `DataReceiver` 峰值检测/IL/RL/基准修正 | `OFDR.Metrics.PeakDetector` + `MetricCalculator` + `PowerUnits` |
| `MainForm` 流程编排 | `OFDR.Engine.TestEngine` + `EngineOptions` |
| `MainForm` 文件保存 | `OFDR.Infrastructure.Persistence.ResultCsvWriter` / `LogFileWriter` |
| `SerialHelper` | 串口部分未实施；`IOpticalSwitch`/`IMatrixSwitch` + `SimulatedSwitches` |
| `config.txt` 的 4 类参数 | `LegacyConfigReader` → `MetricsConfig` / `ReferenceValues` / 设备选项 |
| `MetricTable` | `OFDR.App.MainForm` 内的 `DataGridView`（一期 13 列，未复用自绘三级表头） |
| 现状无对应（仅指标表） | `OFDR.Metrics.EventExtractor`（距离域事件）+ `OFDR.Metrics.TraceEnvelope` + `OFDR.App.DistancePlot`（只读曲线、双光标） |
| 现状无对应 | `OFDR.App` 的 `TabControl`：指标表页 / 距离域页（约 62% 曲线 + 38% 事件表） |
| 离线/回放 | `OFDR.Replay.SyntheticScanSource` + `SyntheticSpectrum` + `MetricsCsvReader` |

### 2.4 架构优化对照（以 `ofdr_platform.png` 四层为参考）

| 架构层（PNG） | 原 demo | 一期平台 | 优化点 |
| --- | --- | --- | --- |
| 数据采集 | 握手/收帧/解析混在 `DataReceiver` | `IScanSource` 适配器：`OfdrTcpSource` + 合成源，FPGA 采集板分支预留（网络） | 采集与收发解耦、源可替换，硬件细节不外溢；同一接口支持离线 |
| 数据处理 | 无独立层，转换混在接收线程 | 整机已输出 dBm，一期不实现 `ofdr.dll` 层 | 避免重复处理；单位换算固定单点 |
| 标准扫描数据获取 | 缺失，UI/算法直接吃字节 | `StandardScan`（轴/单位/元数据/质量/schema/校验和） | 建立唯一枢轴，换硬件或算法不改上层；可序列化与追溯 |
| 测试控制与结果计算 | `MainForm` 直接编排并调接收器 | `TestEngine`（遍历/超时/代次/聚合/取消）+ 纯函数 `MetricCalculator` | 业务规则脱离 UI、可单测；代次防串帧、有界队列防膨胀 |
| 其他仪表 | `SerialHelper` 直接耦合串口 | `IOpticalSwitch`/`IMatrixSwitch` + 模拟实现 | 通信与命令解耦，无硬件可跑；串口实现可后插（§5） |
| 配置/校准 | 参数写成进程级全局变量；配置来源分散（`config.txt` / 代码默认 / GUI 运行期覆盖） | 不可变 `MetricsConfig` + `ComputeId()` + `LegacyConfigReader` | 消除参数来源歧义；配置快照随结果落盘，可复算追责 |
| 可验证性 | 只能连真机 | 回放源同接口 + `OFDR.Verify` 11 项用例 | 无硬件回归、可进 CI；算法改动可对比 |
| 可视化 | 仅 64 行指标表 | 指标表 + 距离域（每帧曲线、事件刻度、事件表、双光标） | 事件抽取与 8 峰判定解耦，无效帧也能看谱；min/max 包络防削峰 |
| 结构 | 单 WinForms 工程，UI 承载业务 | 7 程序集、单向依赖，UI 仅组合根 | 依赖可静态检查；切分克制，不过度设计 |

> 一期仅覆盖 PNG 右侧“整机 TCP”分支与上方三层；左侧 FPGA 采集板（网络）、`ofdr.dll` 处理层、`detect.dll` 交叉验证、真实串口开关均登记为二期（SRS §4.9）。

---

## 3. 模块划分与职责

> 本表是**设计层模块划分**（含二期规划），不是一期交付清单；一期实际程序集见本节末尾。

| 模块 | 职责 | 明确不做 |
| --- | --- | --- |
| `Acquisition.Abstractions` | `IScanSource`、`ScanRequest`、`RawScanFrame`、错误枚举 | 不解析业务、不落盘 |
| `Acquisition.FpgaBoard` | 采集板网络会话建立、配置/启停、收帧、错误映射；若板卡提供 SDK 则封装其调用 | 不做刻度换算之外的信号处理 |
| `Acquisition.Tcp` | 整机握手、命令、切帧、行解析、帧→`StandardScan` | 不做峰值计算 |
| `Processing` | 封装 `ofdr_process`；P/S/A→轴+谱；归一化与 dB 视图 | 不做指标判定 |
| `ScanData` | 契约定义、单位、校验、序列化、schema 版本 | 不含设备知识 |
| `Metrics` | 峰值检测、标记点、IL/RL、长度/光程、基准修正、距离域事件抽取与包络降采样 | 不读文件、不碰 UI |
| `Calibration` | 三层校准数据的加载、版本、有效性 | 不直接改算法常量 |
| `Instruments` | 光开关/矩阵抽象与串口实现、模拟实现 | 不决定测试顺序 |
| `TestEngine` | 状态机、通道遍历、超时/重试、聚合、代次、取消 | 不做 UI 渲染 |
| `Persistence` | 原始/标准/结果/日志四类输出，轮转与命名 | 不参与计算 |
| `App.WinForms` | 界面、指标表/距离域页、自绘曲线与双光标、进度、命令绑定 | 不包含业务规则 |

**一期实际程序集**：`OFDR.Contracts`（契约）、`OFDR.Metrics`（指标纯函数）、`OFDR.Engine`（编排）、`OFDR.Infrastructure`（TCP/模拟开关/配置/落盘）、`OFDR.Replay`（合成谱与 CSV 读取）、`OFDR.App`（WinForms 组合根）、`OFDR.Verify`（验证控制台）。`Acquisition.FpgaBoard`（网络）、`Processing`、`Calibration`、`Instruments.Serial` 尚未落地（SRS §4.9）。

---

## 4. 数据契约

### 4.1 `ScanRequest`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `RequestId` | string | 本次请求唯一标识 |
| `ChannelIn/ChannelOut` | int | 1~8；单次扫描时两者必填 |
| `SampleRateMHz` | int | 50/100/250/500/1000/2500（现状档位，TBD 是否扩展） |
| `Points` | int | 点数或扫描长度换算所得（二者只允许一个权威来源） |
| `ScanLengthM` / `ResolutionM` | double | 扫描长度与分辨率（图上的 3、4 项设置） |
| `TriggerMode` | enum | Software / ExtRising / ExtFalling |
| `TriggerDelayMs` | double | 图上的触发延时 |
| `SegmentCount` | int | 多段采集段数 |
| `UploadFrom` / `UploadTo` | int / int | 上传数据起止行（图上第 5 项设置）；默认 `0` / `-1`（至末行） |
| `CalibrationId` | string | 本次使用的校准集合 |

不变量：`Points>0`；`TriggerDelayMs>=0`；`ChannelIn/Out ∈ [1,8]`；分辨率与点数换算必须显式记录在元数据中。

### 4.2 `RawScanFrame`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `RequestId` / `Sequence` | string / long | 关联请求与帧序号 |
| `P/S/A` | float64[] | 等长三路；由 int16 按显式 `LsbScale` 转换 |
| `SampleCount` | int | 三路长度一致 |
| `SampleRateMHz` | int | 采集实际档位 |
| `TriggerTimestamp` | long | 单调时钟，纳秒或 tick |
| `DeviceId` / `SdkVersion` | string | 设备标识与厂商版本（如 1.0.0.0） |
| `Epoch` | long | 通道代次，用于丢弃残留帧 |
| `Quality` | flags | 溢出/丢段/触发超时等 |

### 4.3 `StandardScan`（唯一枢轴，一期实际定义）

> 类型依据代号：**A** = 现状事实（demo 行为 / 整机 TCP 协议已证实）；**B** = 语义与参考（物理量定义、厂商 SDK、参考文档口径）；**C** = 工程决策（精度、溢出、封闭取值、可校验、可扩展）。

**主体字段**

| 字段 | 类型 | 说明 | 类型依据 |
| --- | --- | --- | --- |
| `ScanId` | string | 唯一标识 | C：需落 CSV/日志并人工对照，不限定 GUID 格式 |
| `Sequence` | long | 顺序号 | A：帧序号单调递增；C：与 `Epoch` 统一为 `long`，避免跨类型隐式转换 |
| `Axis` | `ScanAxis` | 轴描述（见下） | C：轴四元组独立成类型，避免散落为多个顶层字段 |
| `Values` | double[] | 数值数组；整机路径为逐点 dBm | B+C：物理量需浮点，`decimal` 对含噪声的量无意义；数组便于逐帧 80,619 点处理 |
| `ValueUnit` | string | `"dBm"` / `"mW"`；其他单位返回 `UnsupportedUnit` | C：单位不写死，由使用方校验而非类型强制 |
| `Metadata` | `ScanMetadata` | 元数据（见下） | C：与数值分离，使校验和只覆盖轴+数值+单位 |
| `Quality` | `ScanQuality` | `Ok/Empty/AxisInvalid/CountMismatch/ContainsNonFinite` | C：取值封闭，由采集源构造时声明；**一期整机路径恒为 `Ok`，异常判定由 `Validate()` 返回值计算，不读此字段**（见 §13.5） |
| `SchemaVersion` | int | 默认 1 | C：单调递增整数，便于按版本分支 |
| （无存储字段） | `StandardScanCodec.ComputeChecksum(scan)` | 按需计算，只覆盖轴 + 数值 + 单位（SHA256）；时间戳等易变元数据不参与，结果中不落该值 | C：时间戳每帧变化，纳入校验会使结果不可复现；`StandardScan` 本身不含校验和成员 |

**`ScanAxis` 字段**

| 字段 | 类型 | 说明 | 类型依据 |
| --- | --- | --- | --- |
| `Kind` | `AxisKind` | Distance / Frequency | A+B：只有距离域（整机 0.0002 m 行距）与频率域（采集板扫波长）两种 |
| `Start` / `Step` | double | 起点与步距 | B：连续物理量，`float` 在 100 m 处累积误差可达可观测级 |
| `Unit` | string | m / nm / GHz | C：不同采集源坐标单位不同，不能用一个枚举锁死 |
| `Count` | int | 点数 | C：与 `Values.Length` 及数组索引直接对应 |
| `End` / `At(i)` / `IsValid` | 派生属性 | `End => Start+Step*(Count-1)` | C：只由四元组派生，不额外存冗余字段 |

**`ScanMetadata` 字段**

| 字段 | 类型 | 说明 | 类型依据 |
| --- | --- | --- | --- |
| `DeviceId` / `SdkVersion` | string | 设备标识与厂商版本 | B：厂商口径本身是文本（如 1.0.0.0），不做版本号解析 |
| `ChannelIn` / `ChannelOut` | int | 1~8 | A：硬件实际 1~8，`Validate()` 显式校验范围 |
| `Epoch` | long | 通道代次 | C：与 `Sequence` 同数量级且需比较，用 `long` 防溢出 |
| `Timestamp` | DateTimeOffset | 帧到达时刻 | B+C：必须带时区偏移；`DateTime` 换机/换时区产生歧义，也对不上历史 CSV 时间戳 |
| `CalibrationId` | string（默认 `""`） | 校准集合 | C：一期无校准流程，空串优于 null，便于 CSV 直出 |
| `RequestId` | string? | 关联请求 | A：整机 TCP 无“请求”概念（发 `START` 即采），不适用时用 null 而非造假值 |
| `SegmentIndex` | int（默认 0） | 多段采集段号 | C：一期恒 0 |
| `TriggerDelayMs` | double? | 触发延时 | A：仅采集板路径存在，整机路径不适用 |

不变量：`Values.Length == Axis.Count`；`Axis.Step>0`；通道 ∈ [1,8]；`Validate()` 返回错误列表，`ContainsNonFinite` 计数值非有限项。序列化往返（`ToJson`/`FromJson`）已验证一致。

> 精度提示：契约 `Values` 为 `double[]`；二期存储草案（§14）计划 `float32`。`float32` 在 |dBm|≈100 处绝对分辨率约 1e-5，显示 0.0001 dB 够用，但“契约值 ↔ 存储值”不再严格相等，回归断言必须带容差。二期二选一：存 `float64`，或明确存储往返容差（绝对 1e-5 / 相对 1e-6）；不得既存 `float32` 又要求逐位相等。

> 一期未实现 `RawScanFrame`：整机 TCP 已输出 dBm 谱，无需本地 P/S/A 原始帧。

### 4.4 `MeasurementResult`（一期实际定义）

| 字段 | 类型 | 说明 | 类型依据 |
| --- | --- | --- | --- |
| `ScanId` / `Sequence` | string / long | 关联扫描 | 沿用 §4.3（C：文本标识 + 单调序号） |
| `ChannelIn` / `ChannelOut` | int | 取自扫描元数据快照 | A：与契约同源，1~8 |
| `Status` / `Error` / `Message` | `MeasurementStatus` / `MeasurementError` / string | `Ok/Invalid/Failed`；`None/EmptyScan/UnsupportedUnit/NotEightPeaks/ComputationFailed` | C：状态与错误码取值封闭可穷举；`Message` 是给人看的自由文本，不参与判定 |
| `Markers` | `MarkerPoints?` | A/B/BB/CC/C/D/E（行）；无效时为 null | C：用 null 表达“未定位”；行号 0 是合法值，不能用 0 冒充无效 |
| `IlIn/IlDut/IlOut` | double | dB；无效为 `NaN`（禁止用 0 冒充） | B+C：dB 为浮点物理量；0 dB 是合法插损，只能用 `NaN` 表示测不到 |
| `RlIn/RlDut/RlOut` | double | dBm | B：与输入功率同 / 往返换算的量纲 |
| `DutPathM` / `LinkLengthM` | double | 由行距换算 | B：距离量，需浮点 |
| `PeakCount` | int | 检出峰数（要求恰好 8） | A：8 峰是 demo 既定语义，计数用 `int` |
| `PeakMs` / `MetricMs` | double | 检测/计算耗时（诊断） | C：`Stopwatch` 亚毫秒精度，用 `double` 保留小数 |
| `ConfigId` | string | 配置快照标识（`MetricsConfig.ComputeId()`） | C：哈希串，用于复算与追责关联 |
| `SroBase/SriIn/SroOut/SrbIn/SrcOut` | double | 本次使用的基准值快照 | B：与 IL/RL 同量纲，保证基准与结果可逐值对照 |

聚合层使用 `ChannelAggregate`：`Frames/ValidFrames/ExpectedFrames`、`ChannelRunStatus`（Completed/TimedOut/NoData）、8 个 `MetricStats(Count,Sum,Min,Max,Last)` 与 `Message`。

### 4.5 `DistanceEvent`（距离域显示派生，一期实际定义）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Row` | int | 峰所在行（`Peak.Index`） |
| `DistanceM` | double | `Axis.At(Row)` |
| `PeakDbm` | double | 峰顶 dBm（`Peak.Db`） |
| `WidthMm` | double | `Peak.Width × Axis.Step × 1000` |
| `SegmentLossDb` | double | 与上一事件之间平直段的功率差（线性域平均后转 dBm）；首事件或不可测时为 `NaN` |
| `CumulativeLossDb` | double | 可测段插损累加；首事件为 `NaN`，不可测段沿用上一值 |

不变量：

- 事件数**不受 8 峰约束**；检测直接复用 `PeakDetector.FindByThreshold`（阈值/合并间隔取自 `MetricsConfig`），因此峰数不足的帧仍给出曲线与事件。
- 段插损只在相邻两峰之间取平直段：窗口半宽 `min(200 行, max(1, 峰间距/4))`，两端各按 `Peak.Width` 留护栏；**不得复用 `AvgHalfWindow`**（默认 2000 行会跨事件）。
- 不进入 `MeasurementResult`、不写结果 CSV、不落盘；`MetricsConfig` 与 `MetricCalculator` 的 8 峰判定不变。
- 包络降采样由 `TraceEnvelope.MinMax(values, buckets)` 实现：按像素桶取 min/max，桶数取绘图区宽度，单行窄峰不会因降采样消失；桶数超过点数时退化为逐点。

---

## 5. 接口设计

> 下列为**契约签名示意**，以 `ofdr_platform/src` 源码为准；本节只保留跨层契约，Engine/App 的内部实现形状不在文档中复写。

```csharp
public interface IScanSource : IDisposable
{
    string Name { get; }
    void Open();
    void StartStream();
    long BeginChannel(int channelIn, int channelOut);
    bool TryTake(TimeSpan timeout, out StandardScan? scan);
    void Stop();
    long DroppedFrames { get; }
}

public readonly record struct SwitchReply(bool Ok, string Command, string? Response, double ElapsedMs, string? Error);

public interface IOpticalSwitch : IDisposable
{
    string Name { get; }
    void Open();
    SwitchReply Select(int channelIn);
}

public interface IMatrixSwitch : IDisposable
{
    string Name { get; }
    void Open();
    SwitchReply Select(int channelIn, int channelOut);
}

public static class MetricCalculator
{
    public static MeasurementResult Compute(StandardScan scan, MetricsConfig config, ReferenceValues references);
    public static MeasurementResult Compute(StandardScan scan, MetricsConfig config, ReferenceValues references, string configId);
}
```

Engine / App 内部成员只记语义，不记签名：

| 成员 | 语义 | 约束 |
| --- | --- | --- |
| `TestEngine.ScanProduced` | 每帧原始扫描，供曲线与事件表 | `StandardScan.Values` 只读，禁止就地修改；编排线程触发，UI 经 `BeginInvoke` 回投 |
| `TestEngine.ResultProduced` | 每帧指标结果 | 与 `ScanProduced` 独立；结果回调内写 CSV |
| `TestEngine.ChannelCompleted` | 通道结束聚合 | 取消场景当前仍记 `Completed`（见 §13.5 已知限制） |
| `TestEngine.StateChanged` | 状态机文本 | 编排线程触发，UI 经 `BeginInvoke` 回投 |
| `DistancePlot.SetTrace` | 灌入一帧曲线（dBm） | 强制重算包络；光标只在首次有事件时初始化 |
| `DistancePlot.SetEvents` | 灌入事件列表 | 事件刻度与表格联动；事件数不变时原地更新单元格 |
| `DistancePlot.FocusOn` | 光标 A 定位到指定距离 | 事件表双击调用；距离钳制到轴范围 |
| `DistancePlot.Clear` | 清空曲线、事件与光标 | 停止或新一轮开始时调用 |

设计要点：

- `ScanProduced` 独立于 `ResultProduced`：曲线/事件不依赖“恰好 8 峰”，指标无效帧仍可显示；两者都在编排线程触发，UI 经 `BeginInvoke` 回投。
- 事件抽取与包络计算均在 UI 线程完成（单帧 80,619 点的两遍线性扫描），不引入后台线程与锁；订阅方只读数组，禁止就地修改。
- 曲线为 GDI+ 自绘（`DistancePlot`），无 NuGet 绘图依赖；y 轴步长随绘图区高度自适应，x 轴标签按步长决定小数位。
- 双光标只允许在绘图区内拖拽；点击绘图区时移动最近的光标。事件表事件数不变时原地更新单元格，避免每帧重建导致选中行与滚动位置丢失。

一期实现映射：

| 实现 | 位置 | 状态 |
| --- | --- | --- |
| `OfdrTcpSource` | `Infrastructure/Tcp/OfdrTcpSource.cs` | 已实现：握手 `OSI→DELAY→SETOK→START`、按 `!` 切帧、消费线程解码、代次 + 有界队列、双 STOP |
| `SyntheticScanSource` | `Replay/SyntheticScanSource.cs` | 已实现：固定帧间隔合成 8 峰谱（离线闭环） |
| `SimulatedOpticalSwitch` / `SimulatedMatrixSwitch` | `Infrastructure/Switches/SimulatedSwitches.cs` | 已实现：生成 `<SETSW1-0n>` / `<SETSWn-0m>` 命令文本 |
| `SerialOpticalSwitch` / `SerialMatrixSwitch` | `Infrastructure/Switches/`（预留） | 未实现（二期）：实现同一 `IOpticalSwitch`/`IMatrixSwitch`，负责串口打开、命令下发、回显判定与超时；需引入 `System.IO.Ports`（一期 CON-08 不引入） |
| `FpgaBoardScanSource` | — | 未实现（二期，网络接入，名称暂定） |
| `OfdrProcessor` | — | 未实现（整机路径无需本机处理） |
| `DetectMetricsEngine` | — | 未实现（二期交叉验证） |

设计规则：一期接口为同步阻塞 + 调用方控制超时（`TryTake(timeout)`），线程与队列由实现内部管理；接口签名只用契约与基础类型，不出现厂商句柄或 `SerialPort`/`NetworkStream` 类型。

串口替换点（二期接入约定）：引擎与 UI 只依赖接口，新增串口实现即可替换模拟开关，不改编排与界面；命令模板、波特率、回显判据、超时值为待确认项（SRS TBD-06），配置字段随实现一并落地，一期不预留空实现，避免过度设计。`SwitchReply` 已包含 `Command/Response/ElapsedMs/Error`，可直接承接回显与计时结果。

---

## 6. 运行视图

### 6.1 线程与队列

| 线程/任务 | 职责 | 队列 |
| --- | --- | --- |
| UI 线程 | 参数输入、指标表、距离域曲线与事件表、日志更新、开始/停止 | 不阻塞；引擎事件（`ScanProduced`/`ResultProduced`/`StateChanged`）经 `BeginInvoke` 回投；包络桶数 ≤ 绘图区像素宽 |
| TCP 接收线程（1 个） | `Read` + 按 `!` 切帧 + 打 `Epoch`/通道戳 + 入队 | 有界 `BlockingCollection<RawFrame>`（容量 8） |
| 编排线程（1 个，App 的 worker） | `TestEngine.Run`：遍历通道、`BeginChannel`、`TryTake`、`MetricCalculator.Compute`、聚合 | 从源队列取帧；结果事件回调内写 CSV |
| 离线合成源 | 在 `TryTake` 内按帧间隔生成（调用线程即编排线程） | 无独立线程 |

`StartStream` 后接收线程立即启动，`START` 由编排线程发送；ASCII 解码（约 800 KB/帧）放在消费方（编排）线程，不阻塞收帧。队列满时 `Add` 阻塞接收线程形成背压，不做无界堆积。

### 6.2 关键时序（整机 TCP 单通道）

```
TestEngine.Run: source.Open()  → OSI → DELAY_<ms> → SETOK
                source.StartStream() → 接收线程启动 → START → 等待 StartSettleMs
通道切换:        开关 Select(IN[,OUT]) → source.BeginChannel(IN,OUT) → Epoch++
逐帧:          接收线程入队(RawFrame, Epoch) → TestEngine.TryTake(timeout)
               → 丢弃旧 Epoch → Decode → StandardScan → MetricCalculator.Compute
               → ScanProduced(scan) → UI 曲线/事件表
               → ResultProduced(事件) → CSV + 指标表
停止:          source.Stop() → STOP ×2（间隔 100 ms）→ 关连接 → 队列 CompleteAdding
```

通道切换时接收线程仍在收帧：切换瞬间到达的旧帧带旧 `Epoch`，消费端直接丢弃并累加 `DroppedFrames`，不污染新通道。

### 6.3 生命周期

- 一期整机 TCP：`TcpClient` 在 `Open` 建立一次；`Stop` 发两次 `STOP` 后关闭连接并 `CompleteAdding`，`Dispose` 等价于 `Stop`（幂等）。异常路径由 `TestEngine.Run` 的 `finally` 兜底。
- 采集板（二期，网络）：`Open` 一次，`Configure/Start/Stop` 可多次，`Close` 在 `Dispose`；异常路径同样 `Close`。
- `ofdr`：`ofdr_process` 返回的结果在 `finally` 中 `ofdr_result_free`；无持久句柄，配置随每次调用传入。
- `detect`：`detect_result_free` 放在 `finally`。
- 统一规则：所有 P/Invoke 结果结构由 `SafeHandle` 或 `try/finally` 管理，禁止跨层传递指针。

### 6.4 单位转换唯一位置

一期（整机 TCP 路径）只有第 3 条生效；第 1、2 条随二期采集板/处理层落地（SRS §4.9）。

- 原始 int16 → 物理量：采集适配器（显式 `LsbScale`）——二期。
- 功率归一化与线性→dB：`Processing` 出口——二期。
- 基准修正（SRI/SRO/SRB/SRC）：`Metrics` 内部，读取 `ReferenceValues` 快照（下标 1~8，下标 0 未使用）。
- UI 与持久化不做任何换算，只格式化。

### 6.5 状态机（FR-ORC-03 一期实况）

`TestEngine.Run` 通过 `StateChanged` 上报 4 个状态文本，与 SRS FR-ORC-03 的六态是**简化映射**，不等价：

| 一期状态文本 | 触发点 | 对应 SRS 六态 | 说明 |
| --- | --- | --- | --- |
| `连接中` | `Run` 开始，`Open` 前 | 空闲 → 配置 | 无独立「配置」状态 |
| `运行中` | `StartStream` 成功后；通道切换期间沿用 | 扫描 / 切换 | 切换与扫描共用同一文本 |
| `已停止` | 取消令牌触发 | 停止 | — |
| `已完成` | 全部轮次跑完 | 停止 | — |
| （无） | 异常路径 | 错误 | 异常直接抛出，由 `finally` 收尾、App 捕获提示 |

差距：缺「空闲」「配置」「切换」「错误」四个显式状态，切换期间无状态区分。补齐六态需扩展状态枚举与 UI 映射，属行为变更，须先改 SRS 验收方式与回归用例；当前在 §13.4 记为部分实施。

---

## 7. 错误处理与日志

| 类别 | 示例 | 处理 |
| --- | --- | --- |
| Device | 开卡失败、读超时、断线 | 状态机进入 Error，允许重连/重试；上报 UI |
| Parameter | 采样率非法、点数超限 | 配置阶段拒绝，给出合法范围 |
| Data | 帧长度不符、切帧失败、NaN | 隔离并记录 `bad frame`，不参与统计 |
| Algorithm | 峰数不符、处理失败 | 结果 `Status=Invalid`、`Error=NotEightPeaks`（或 `ComputationFailed`），不输出 0 |
| Business | 校准缺失、门限冲突 | 阻止测试或标记无效 |

日志：结构化（时间、级别、模块、通道、帧号、耗时）；默认级别不逐行打印数据；文件轮转按大小与数量；单次运行日志目标需相对现状（55.9 MB/次）显著下降（TBD-10）。

---

## 8. 配置与校准

### 8.1 三类配置（分离且不可变）

| 配置 | 内容 | 一期实现 |
| --- | --- | --- |
| `DeviceProfile` | TCP 地址/端口、延时/稳定等待、队列容量 | `TcpScanOptions`（`Host`、`Port`、`DelayMs`、`DelaySettleMs`、`StartSettleMs`、`RowSpacingM`、`QueueCapacity`） |
| `AlgorithmProfile` | 阈值、合并间隔、A/D 行、局部平均半窗、长度系数、光程系数 | `MetricsConfig`（7 字段默认：`PeakThresholdDbm=-85`、`PeakMergeGap=50`、`MarkARow=2500`、`MarkDRow=32500`、`AvgHalfWindow=250`、`LinkLenPerRow=0.0002`、`DutPathPerRow=0.0002`） |
| `BusinessProfile` | 通道范围、采样帧数、超时、轮次 | `EngineOptions`（`InFrom/InTo/OutFrom/OutTo`、`SamplesPerChannel=5`、`ChannelTimeoutMs=10000`、`Rounds`、`Infinite`） |
| 结果基准 | SRI/SRO/SRB/SRC 各 8 路 + 标量 SRO | `ReferenceValues`（下标 1~8，下标 0 未使用） |

运行开始时 `MetricsConfig.ComputeId()` 生成快照标识（SHA256 前 6 字节），随每个 `MeasurementResult` 与 CSV 行落盘；运行中禁止热改。遗留 `config.txt` 由 `LegacyConfigReader` 迁移读取，只映射平台实际使用的键（未知键忽略）。

### 8.2 三层校准

| 层 | 归属 | 内容 | 失效策略 |
| --- | --- | --- | --- |
| 设备 AFE | `Acquisition.FpgaBoard` | 若板卡含 AFE：每通道每档 offset/gain/attenuation | 缺失则该档禁止采集 |
| 处理标定 | `Processing` | `dist_scale`、功率归一化系数 | 缺失则结果标记 `Uncalibrated` |
| 结果基准 | `Metrics` | SRI/SRO/SRB/SRC | 缺省 0，但结果记录来源 |

校准集合以 `CalibrationId` 引用，禁止把校准值硬编码进算法。

---

## 9. 二期发行包与部署（FPGA 采集板路径）

> 一期（v0.1.0）只交付平台自身程序集，**下列清单为一期范围外**，随 FPGA 采集板（网络）路径在二期落地；本节保留，用于约束二期发行形态。采集板固件不随 PC 发行包分发。

PC 侧发行包必须包含：

| 类别 | 文件 |
| --- | --- |
| 应用 | `OFDR.App.exe`（当前程序集名，二期若改名另行登记） + 平台程序集 + `.deps.json` / `.runtimeconfig.json` |
| 采集 | 无（采集与收发在板卡固件内，经网络输出；若最终采用厂商 SDK/驱动，按 TBD-15 结论另行补充） |
| 处理 | `ofdr.dll` 及其依赖 |
| 结果参考 | `detect.dll`（可选） |
| 第三方 | `libfftw3/3f/3l-3.dll`、`libliquid.dll`、`pthreadGC2/VC2.dll`、`elog.dll`、`wpcap.dll`、`Packet.dll` |

> 清单取自 `references/` 现有发行包，不是对二期依赖的确认；二期改走网络采集板后是否仍需 `ofdr`/`detect` 与上述第三方 DLL，按 TBD-15 结论复核。

启动自检：x64 校验、处理库存在性与加载、采集板固件版本与连通性检查（协议待定，TBD-15）、串口枚举、网口连通性；失败时输出缺失清单与修复建议，不进入测试。

---

## 10. 测试策略（一期实际资产）

一期验证集中在 `src/OFDR.Verify`（控制台断言框架，`PASS/FAIL/SKIP` + 退出码），不依赖外部测试框架。

| 层级 | 方法 | 一期资产/实现 |
| --- | --- | --- |
| 契约 | `StandardScan` 校验、JSON 往返、SHA256 校验和；`MetricsConfig.ComputeId()` 快照标识 | `Tests.ContractStandardScan`、`Tests.ContractConfigId` |
| 单元（算法） | 峰值检测合并间隔边界（gap=7 合并 / gap=6 切分）与 dBm→mW 加权质心 | `Tests.PeakMergeBoundary` |
| 单元（显示） | 包络降采样保留单行窄峰、首末桶、桶数超点数退化为逐点、非法桶数与空输入 | `Tests.TraceEnvelopeKeepsPeaks` |
| 单元（显示） | 合成 3 峰扫描的事件抽取：位置、宽度、段插损、累积，且不要求 8 峰 | `Tests.EventExtractSynthetic` |
| 单元（公式） | 8 峰手算基准：IL-IN/DUT/OUT、RL 三项、链路长度、DUT 光程、基准修正 | `Tests.MetricGolden` |
| 单元（无效态） | 非 8 峰返回 `Invalid/NotEightPeaks`，指标为 `NaN` 而非 0 | `Tests.MetricNotEightPeaks` |
| 协议 | 本地假整机服务端：`OSI → DELAY → SETOK → START`，64 字节分片写入制造分片/粘包 | `FakeOfdrServer` + `Tests.TcpEndToEnd` |
| 集成（离线闭环） | 合成谱源 + 模拟开关 + `TestEngine`，IN×OUT 2×2、每通道 2 帧，校验 8 结果/4 聚合 | `Tests.EngineOfflineLoop` |
| 迁移 | 读取 demo `config.txt`，断言阈值/标记点/窗口/超时与 SRI/SRO/SRB/SRC 映射 | `Tests.LegacyConfigMigration` |
| 结果级基准数据 | 读取历史 `ofdr_metrics_*.csv`，断言可解析、通道 1~8、数值有限、多数 8 峰、前 18 列表头一致 | `Tests.LegacyMetricsBaseline` |
| 回放（逐值） | 用 `ofdr_raw_*.dat` 全谱重算并与历史 CSV 逐值比对 | 缺失（历史 run 无 raw 全谱，SRS TBD-13） |
| 硬件冒烟 | 真实整机握手与收帧、真实串口开关 | 未执行（一期无硬件） |
| 交叉验证 | 与 `detect.dll` 同输入比对 | 未实施（二期，FR-MET-12） |

一期实际执行结果：11/11 通过（明细与命令见 §13.2）。

---

## 11. 架构决策记录（ADR）

| ID | 决策 | 理由 | 代价 |
| --- | --- | --- | --- |
| ADR-01 | `StandardScan` 作为唯一跨层契约 | 换硬件/换算法不改上层；可回放 | 需要一次性适配与校验成本 |
| ADR-02 | 厂商 DLL 只允许出现在 Infrastructure | 实现“硬件无关”与可测试性 | 适配器样板代码增加 |
| ADR-03 | 离线回放与真机采集同等重要（正式功能，不是临时调试手段） | 无硬件也能开发、测试、对比算法 | 需维护回放格式与基准答案数据 |
| ADR-04 | 配置不可变快照，禁止静态可变全局 | 杜绝参数来源歧义（现状 CON-06：两个定义源 + GUI 运行期覆盖） | 需重构现有静态配置 |
| ADR-05 | 三层校准分别归属 | 换卡后结果可比 | 校准流程与 UI 需分别设计 |
| ADR-06 | 有界队列 + 代次标记 | 避免内存膨胀与通道串帧 | 需定义丢弃策略与计数 |
| ADR-07 | 单进程模块化，不上微服务 | 部署简单、实时性可控 | 后期多工位需再拆分 |
| ADR-08 | 指标计算为纯函数 | 可单测、可回放、可与 detect 比对 | 需把 IO/配置外部化 |
| ADR-09 | 距离域显示只读，事件抽取与 8 峰判定解耦；min/max 包络降采样 | 不改 `MetricsConfig`、8 峰判定与 CSV 前 18 列，历史回归不失效；无效帧也能观谱排障 | 事件仅供查看、不落盘；新增显示层代码 |

---

## 12. 演进路线与 TBD

### 12.1 迁移步骤（行为不变优先）

状态标记：`已完成` = 一期 v0.1.0 落地并验证；`未开始` = 二期。

1. **已完成**：冻结基线并定义契约。`StandardScan`/`ScanRequest` 已落地（`OFDR.Contracts`），历史 `ofdr_metrics_*.csv` 作为结果级回归资产（逐值回归因缺 raw 全谱受限，见 SRS TBD-13）。
2. **已完成**：把 TCP 路径包成 `IScanSource` 并输出 `StandardScan`。`OfdrTcpSource` 复刻 demo 握手/切帧/代次/双 STOP，端到端（含分片与粘包）已验证。
3. **已完成**：抽离 `Metrics` 为纯函数。`PeakDetector`/`MetricCalculator`/`PowerUnits` 与 demo 公式、取整、合并宽度语义对齐，并由 8 峰手算基准与合并边界用例锁定。
4. **已完成**：引入 `TestEngine` 状态机与模拟开关，`MainForm` 退化为组合根/视图。离线合成源 + 模拟矩阵开关跑通 2×2 闭环。
5. **未开始**：`Acquisition.FpgaBoard`（网络）与 `Processing`，与整机路径并行验证（形态与协议待定，SRS TBD-15）。
6. **未开始**：统一持久化分级/轮转、部署自检与 CI（一期仅逐帧 CSV + 日志文件）。
7. **未开始**：校准管理与业务门限配置化（一期仅 `config.txt` 的 SRI/SRO/SRB/SRC）。
8. **已完成**：距离域只读曲线、事件表与双光标（2026-10-31）；不改指标口径、配置字段与 CSV 合同。

### 12.2 未决项

不重复登记，逐条见 SRS §9.1：采集板协议与流式语义（TBD-15/TBD-09/TBD-10）、处理库点数与线程安全（TBD-03）、整机协议与开关命令（TBD-04/TBD-06）、精度与重复性（TBD-01）、历史 run 缺 80,619 点 raw 全谱（TBD-13）、MES/权限/审计（TBD-08）。

---

## 13. 一期实施记录（v0.1.0）

> 本节记录构建、验证与需求覆盖情况；评审结论见 `REVIEW_OFDR_Platform_v0.1.0.md`。

工作目录：`ofdr_platform/src`；解决方案 `OFDR.Platform.sln`；7 个程序集（Contracts / Metrics / Engine / Infrastructure / Replay / App / Verify）。

### 13.1 构建

```
dotnet build OFDR.Platform.sln
```

结果：成功，`0 个警告`，`0 个错误`（约 6.54 s）。

### 13.2 验证

```
dotnet run --project OFDR.Verify --no-build
```

结果：`PASS 11，FAIL 0，SKIP 0`，退出码 0。

用例名与资产见 §10（其前 11 行即当前执行顺序；第 4、5 项为 2026-10-31 距离域回填新增），本节只记汇总结论，不复制用例表。

### 13.3 未验证项（明确声明）

- `OFDR.App` 已于 2026-10-30 实际启动并跑通离线闭环（2×2、20 帧、无效 0，CSV/日志已落盘）；未做长时间稳定性与在线真机运行。
- 4M 点真实谱下的距离域刷新帧率未验证（合成谱 80,619 点已实跑）。
- 真实整机 TCP 设备、真实串口开关未联调；TCP 仅对本地假服务端验证。
- 无逐值回放回归（缺 raw 全谱）。

### 13.4 需求覆盖对照（实施回填）

| 需求域 | 一期状态 | 说明 |
| --- | --- | --- |
| FR-ACQ-01~09（FPGA 采集板） | 未实施 | 采集板形态与协议待定（SRS TBD-15），仅保留契约与后续接入点 |
| FR-ACQ-10（整机 TCP） | 已实施 | 握手、分片/粘包切帧、行解析、代次 + 有界队列，端到端已验证 |
| FR-PROC-01~07（处理层） | 未实施 | 整机路径直接输出 dBm 谱，无需本机处理 |
| FR-STD-01~06 | 已实施 | 契约、校验、JSON 序列化、SHA256 校验和 |
| FR-MET-01/03~11 | 已实施 | 峰值检测、标记点、IL/RL、长度/光程、基准修正、无效态、纯函数 |
| FR-MET-02/12 | 未实施 | 备选算法与 `detect` 交叉验证，列二期 |
| FR-VIS-01~06 | 已实施 | 距离域页、只读曲线（min/max 包络）、事件表（位置/回损/段插损/累积/宽度）、双光标与 ΔRL/间距、双击联动；事件不落盘 |
| FR-VIS-07 | 未实施 | 事件类型判定、group/span、基线差分，列二期（SRS TBD-14） |
| FR-INS-01/02/04/05 | 已实施 | 接口 + 模拟实现（真实命令文本、并行切换、计时）；无独立断言用例，由离线闭环用例间接覆盖 |
| FR-INS-03 | 未实施 | 真实串口回显/超时，一期无串口依赖 |
| FR-ORC-01~05/07 | 部分实施 | 通道遍历、采样数、超时跳过、代次、聚合已落地；FR-ORC-03 的状态文本只到 4 态，未覆盖 SRS 六态（见 §6.5） |
| FR-ORC-06 | 未实施 | 断线仅提示并结束本轮 |
| FR-DATA-01、FR-DATA-07~10 | 未实施 | 一期不落盘原始帧；留存与清理按 SRS 需求、本文 §14 设计 |
| FR-DATA-02/05 | 已实施 | 逐帧结果 CSV（前 18 列与 demo 一致）+ 日志文件 |
| FR-DATA-03/04/06 | 部分实施 | 无效帧以状态列记录；日志未分级/轮转；离线源已实现 |
| FR-CAL-01~04 | 部分实施 | 仅结果基准 SRI/SRO/SRB/SRC 经 `config.txt` 读取；无校准版本管理 |

### 13.5 距离域显示回填（2026-10-31）

**参考来源（事后补记）**：UI 形态参照 `references/luna` 的 6200/4600 手册；文件、版本、页码与“不照搬”清单见 §0.2。对照总结整理于 2026-11-01，晚于本节的实现与验证日期，属事后溯源；SRS FR-VIS-01~06 的来源标签仍为 `[DEMO]`/`[决策]`，Luna 不是需求来源。

范围与约束：

- 引擎新增 `ScanProduced`（每帧原始扫描，订阅者只读）；`ResultProduced` 签名与 CSV 合同不变。
- 新增 `OFDR.Metrics.EventExtractor`（事件抽取）与 `TraceEnvelope`（min/max 包络）；新增 `OFDR.App.DistancePlot`（GDI+ 自绘）。
- 布局改为 `TabControl`：指标表页 / 距离域页（约 62% 曲线 + 38% 事件表，事件表 6 列）。
- 未改：`MetricsConfig` 字段、`MetricCalculator` 的 8 峰判定、`ResultCsvWriter` 前 18 列；未新增 NuGet 依赖；事件不落盘。

验证：

| 项 | 结果 |
| --- | --- |
| 构建 | 0 警告 0 错误 |
| 验证集 | PASS 11，FAIL 0（新增包络降采样、事件抽取两项） |
| UI 实跑（离线合成 IN×OUT 2×2，每通道 5 帧） | 20/20 帧有效；CSV 与历史数值一致（`4.402200 / 13.073800 / 8 峰 / 8C3294612B9E`） |
| 中途停止（采样数 40） | 36/40 帧后停止，状态「已停止」，可再次启动 |
| 光标交互 | 拖动 B 至 8.389 m，读数 `A 2.062/-57.0  B 8.389/-122.8  ΔRL 65.8 dB  间距 6.327 m` |
| 最小尺寸 1180×680 | 标签/输入框成组换行；y 轴自适应为 -65/-90/-115/-140 四档，无重叠 |

已知限制（未改）：

- 事件表在 62/38 分割下 8 行需滚动一格才可见。
- 段插损按“线性域平均后转 dBm 再相减”（10log 功率比）计算，与 SRS §1.3 的 IL“往返功率差的一半”口径不一致；需确认段插损语义是往返值还是单向值，确认前不改代码、不改夹具与 CSV。
- 取消采集时通道聚合仍记 `Completed`；要区分「已取消」需扩展 `ChannelRunStatus`。
- 4M 点真实谱的刷新帧率未验证；若卡顿，预案是把包络计算移出 UI 线程。
- `StandardScan.Quality` 与 `StandardScanCodec.ComputeChecksum` 在一期运行链路中未被消费：`Quality` 恒为 `Ok`，校验和只在契约用例中计算。

---

## 14. 二期设计草案：原始数据留存与清理

状态：设计草案，二期实施，不在 v0.1.0 范围；关联 FR-DATA-01、TBD-13。

> 本节所有类型名、配置键、目录与文件命名均为草案占位；二期落地时以实际实现回填，不作为已定 API 契约。

### 14.1 目标与一期最小口子

- 目的：让每条结果都能用原始谱**复算**（改参数/换算法后重判），并支撑逐值回归、异常复盘与校准追溯。
- 一期（v0.1.0）不实现完整策略；建议先落一个最小口子（**尚未实现**）：**无效帧或人工勾选时**把当前 `StandardScan` 落盘（`StandardScanCodec` 已具备序列化与校验和），用于积累真实全谱，供二期的逐值回归使用。

### 14.2 存储格式与目录

- 每帧一个文件：二进制 v1（magic、schema 版本、轴起点/步距/点数、单位、通道、Epoch、时间戳、`ConfigId`、`CalibrationId`、校验和）+ `float32` 数值，外层用 .NET 内置 `GZipStream`（不引入 NuGet，满足 CON-08）。
- 目录结构：

```
runs/<RunId>/
  run.json            # 参数快照、ConfigId、基准值、软件/算法版本
  results.csv         # 列序与一期结果 CSV 一致
  IN1-OUT1/f0001.ofdrs.gz ...
  IN1-OUT1/channel.json
manifest.sha256       # 每文件校验和与行数
```

- 命名：`RunId = yyyyMMdd_HHmmss_<ConfigId 前 6 位>`；帧文件 `f<序号>.ofdrs.gz`；`complete` 标记写入 `run.json` 后视为收尾。

### 14.3 留存策略的落点（默认值见 SRS，本文不重复定义）

策略本身是需求（SRS FR-DATA-07~10），这里只给出它在设计中的承载位置：

| 策略项 | 设计承载（占位描述，二期落地时回填） |
| --- | --- |
| 保存粒度（首末帧 / 异常全帧 / 手动全存） | 运行参数中的保存模式，运行开始时注入 |
| 空间预算 | 运行参数中的空间预算，运行结束统计占用 |
| 保留期（天数与运行次数） | 运行参数中的保留天数与保留次数，两者同时满足才删除 |
| 豁免（NG/异常/人工锁定） | `run.json` 的 `complete` 与人工复核/锁定标记 |
| 触发时机 | 运行结束检查；采集过程中不删除 |

### 14.4 清理机制（实现）

- 触发：启动与运行结束各检查一次；超配额或超期即清理。
- 顺序：按 `RunId` 从旧到新删除；未写入 `complete` 的运行跳过，避免误删正在写入的数据。
- 安全：只删除已收尾的 `runs/<RunId>/` 目录；磁盘剩余低于阈值（默认值见 SRS FR-DATA-10）时停止新运行并提示。
- 审计：每次删除记录时间、`RunId`、释放空间与原因，审计日志保留期限见 SRS FR-DATA-10。
- 回放校验：读取时校验 `manifest.sha256`；校验失败标记该 run 为不可复算，不参与回归。

### 14.5 验收与关联

- 二期验收路径：保存 → 重启程序 → 回放重算 → 与同 run 的 `results.csv` 在约定容差内一致；篡改文件应被校验和拦截。
- 关联需求：FR-DATA-01（原始数据保存与回放）、FR-DATA-03/04（异常隔离与日志）、TBD-13（历史 run 无 raw 全谱）。
