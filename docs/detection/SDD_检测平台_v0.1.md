# 检测平台上位机 SDD v0.1

> **文档编号**：SDD-DET-001
> **版本**：v0.1（评审稿）
> **日期**：2026-11-11
> **对应需求**：[SRS_检测平台_v0.4.md](SRS_检测平台_v0.4.md)（定稿）
> **关联文档**：[OCS 控制协议分析](OCS_控制协议分析.md)、[L1 统一数据契约](../contracts/统一数据契约_v0.7.md)、[L2 GM-IX 数据与处理规范](../contracts/GM-IX_数据与处理规范_v1.0.md)
> **待关闭输入**：[docs/detection/README.md §5.1](README.md) 的 SDD-P2-1~7
> **并行事项**：OCS 协议实测已完成首轮（2026-11-11，TBD-O01/O02/O04/O05/O09 关闭）；基线数值仍待 golden DUT 数据（TBD-D05a/b）。见 [实测报告](OCS_协议实测报告_2026-11-11.md) 与 [基线数据收集方案](基线数据收集方案.md)。

## 1. 架构总览

### 1.1 解决方案布局（落地 D07）

```text
products/detection/
├─ Detection.sln
├─ src/
│  ├─ Detection.Core/           # 纯域模型，只依赖 BCL
│  ├─ Detection.Vendor/         # OCS 串口协议 + LGA50-D 接入（厂商归口）
│  ├─ Detection.Orchestration/  # 批次状态机、路径编排、组流水线
│  ├─ Detection.App/            # WinForms UI
│  └─ Detection.Tests/          # 单元与契约测试
└─ README.md
```

现有 `products/gmix/src/Gmix.OcsVerify`、`Gmix.OcsCheck`、`Gmix.OcsCheck.App` 为占位骨架，按 §1.3 迁移表并入后删除，不在 `Gmix.sln` 留残。

### 1.2 依赖方向（不可逆）

```text
Detection.Core        → BCL
Detection.Vendor      → Detection.Core
Detection.Orchestration → Detection.Core + Detection.Vendor + Gmix.Core
Detection.App         → Detection.Core + Detection.Orchestration
Detection.Tests       → 全部
```

约束：

- `Detection.App` **不得引用** `Detection.Vendor`、`Gmix.Vendor`（NFR-006）。
- 厂商类型、`SerialPort` 字节操作、协议字符串只允许出现在 `Detection.Vendor`。
- 首版**不引用** `Gmix.Vendor`（其内容为 QT2144/GM-IX 专用；SRS 1.4 明确首版只接 LGA50-D）。SRS 1.3 中"引用共享 `Gmix.Vendor`"按本条修正为"按需引用，首版不需要"。
- 复用 `Gmix.Core` 的 `GroupStore` / `GroupAssembler` / `MetricsAggregator` / `SchemaModels` / `SchemaValidator` / `JsonAndFileStore`；检测层不复制这些实现（FR-095、§4.5.2）。

### 1.3 骨架迁移表

| 现有工程 | 内容 | 迁移目标 |
|---|---|---|
| `Gmix.OcsVerify/Domain.cs` | 核验域纯逻辑 | `Detection.Core`（并入域模型） |
| `Gmix.OcsCheck/Program.cs` | 编排进程骨架 | `Detection.Orchestration` |
| `Gmix.OcsCheck.App/Program.cs` | UI 骨架 | `Detection.App` |
| `Gmix.OcsCheck.App` 的图标资源 | 嵌入资源 | `Detection.App` 同名嵌入 |

迁移在实现阶段执行（本文档定稿后）；迁移提交单独一个 `refactor(detection)`，不与功能混提。

## 2. 进程与线程模型

### 2.1 单进程模型

检测平台采用**单进程**（WinForms UI + 后台编排线程），与 GMIX 的"采集进程 + 编排进程"双进程文件边界不同。理由：

- GMIX 双进程的动机是采集进程可强杀、与 UI 隔离崩溃域；检测平台的单路径耗时短（秒级），串口设备无 GMIX 的 `seg_count >= 2` 挂起风险。
- L2 的 staging/`.ready`/原子提交机制在**进程内**依然完整使用（§5.2），进程模型不影响数据完整性。

若后续实机发现 LGA50-D 采集驱动有崩溃风险（TBD-D10 实测结论），再把采集拆为子进程——接口边界已在 Vendor 层预留。

### 2.2 线程划分

| 线程 | 职责 | 纪律 |
|---|---|---|
| UI 线程 | 界面、状态展示、操作入口 | 不做串口/文件 IO（NFR-012）；通过 `SynchronizationContext` 收编排事件 |
| 编排线程（1） | 批次状态机主循环：路径遍历、切换→稳定→扫描→组提交 | 全批次唯一写者；对 UI 只发事件，不共享可变状态 |
| 串口 IO（每设备 1） | OCS 命令收发、定界读包 | 命令串行化：同一串口一次只有一条在途命令（FR-028）；队列满策略为阻塞（命令速率天然低于容量） |
| IO 线程池 | `detection.json` 写入、CSV 导出、日志 | `detection.json` 只由编排线程触发写入 |

### 2.3 串口独占与重连

- 打开串口时以独占模式持有；第二实例/外部程序占用即报传输类错误（FR-007/028，单实例见 §9.2）。
- 断连检测：写失败或读超时 → 关闭句柄 → 进入 `DeviceDisconnected` 状态 → UI 提示重连 → 重连成功后**强制重新查询 OCS 状态**（FR-005），并以查询结果为准继续，不信任内存缓存。

## 3. 模块设计

### 3.1 Detection.Core（纯域）

| 类型 | 职责 |
|---|---|
| `PathPlan` / `PathPlanEntry` | 64 条路径定义（`pathId`、`in`/`out`、1×8 路由、期望端口、N）；计划校验（FR-012） |
| `BatchState` / `PathState` / `Connectivity` / `AttentionFlag` / `ErrorClass` | 枚举（值域同 SRS §4.5.1/§4.6/§4.8/§4.14） |
| `DetectionBatch` / `PathResult` | `detection.json` 的内存模型（字段一一对应 SRS §6.2） |
| `BatchSummaryCalculator` | 汇总口径（`completed + measurementFailed + notAttempted = 64`，FR-070/074） |
| `FlagEvaluator` | 由 `connectivity` + `ilDb`/`rlDb` + 基线参考 + 峰证据 + 组完成状态（`validCount < averageCount` → `avg_incomplete`）派生 `flags[]`（FR-051）；无基线时跳过 `out_of_reference`（FR-063） |
| `BaselineCatalog` | `baseline.json` 的加载、校验、版本追加、按 `applicable` 匹配（FR-060~062） |
| `ConfigSnapshot`（检测侧） | 进 `configHash` 的字段集合与哈希计算（SRS §6.3） |

不读文件、不碰 UI、不做 IO；文件访问经 `JsonAndFileStore`（Gmix.Core）由 Orchestration 注入。

### 3.2 Detection.Vendor（厂商归口）

| 类型 | 职责 |
|---|---|
| `IOcsTransport` | 串口抽象（`Open/Close/Write/ReadFrame`）；Mock 与真实实现共此接口 |
| `OcsSerialTransport` | 真实串口：按设备配置（1×8 = 9600、8×8 = 115200，8N1，`Handshake=None`）；按 `<...>` 严格定界读包（不接受内部空格、`?`、半帧）；超时 300 ms（8×8 整矩阵 500 ms）。见 [实测报告 §10](OCS_协议实测报告_2026-11-11.md) |
| `Ocs1x8Client` / `Ocs8x8Client` | 命令封装：`IDN?`/`SN?`/`RST`/`SETSW`/`QURSW`；发送前参数校验（**单路与整矩阵都校验非 0 输出唯一**；实测设备仅整矩阵强制，FR-025）；错误映射（§6） |
| `Lga50ScanSource` | LGA50-D 扫描触发与曲线接收；**语义待 TBD-D10**，先落接口与回放实现 |
| `OcsProtocolConstants` | 协议常量唯一来源（帧定界符、各设备波特率、命令字、`Handshake=None`）；**超时与重试不在此文件**——它们是配置项，默认值来自实测（§8.1）；其他文件不得出现协议字节/字符串字面量（规则 30） |

错误分类在 Vendor 出口完成：`TransportError` / `ProtocolError` / `DeviceError`（对应 SRS §4.14 前三类）。

### 3.3 Detection.Orchestration（编排）

| 类型 | 职责 |
|---|---|
| `BatchRunner` | 批次状态机（§4）；对 UI 暴露事件 `PathStarted/PathCompleted/BatchStateChanged/Progress` |
| `PathExecutor` | 单路径流水线：切 1×8 → 切 8×8 → 回读 → 稳定等待 → N 次扫描 → 组提交 → `detection.json` 更新 |
| `GroupPipeline` | 组装与提交：staging 写入、`.ready` 交接、`GroupStore.Commit`；复用 `Gmix.Core.GroupStore`，`DataRoot = 批次目录`（B1） |
| `MetricsReader` | 从组内 `AVG/summary.json`（N=1 时 `R001/summary.json`）读 `totalIlDb`/`totalRlDb`，连同 `metricSource` 写入 `PathResult`（不重算） |
| `RecoveryService` | 批次恢复（§4.3） |
| `RetentionService` | 批次级清理（§5.5） |
| `DetectionStore` | `detection.json` 与 `index.csv` 的读写（§5.3） |

### 3.4 Detection.App（UI）

| 视图 | 内容 |
|---|---|
| 主窗口 | 设备面板（三设备连接状态）、批次控制（开始/暂停/中止/重测）、进度条 |
| 矩阵视图 | 8×8 单元格：连通性缩写 + IL 值 + 角标（颜色图例按 SRS §4.6 呈现要求） |
| 详情面板 | 选中路径全字段：指标、证据、flags 及触发原因、OCS 状态、标定状态、组/scanId、耗时、重试、备注 |
| 批次历史 | 列出 `DETECTION_DATA` 下批次，可打开（FR-106） |
| 基线管理 | 导入、版本列表、匹配状态（无编辑功能，FR-065） |

操作护栏：中止/重测/复位/清理二次确认（FR-080）；运行中配置锁定（FR-118）。字符串集中资源文件（NFR-013）。

**操作员与工位**：批次启动对话框输入（非配置文件持久项），写入批次元数据（FR-079）；人工备注与关注标记自动继承该身份（FR-054）。

## 4. 批次状态机与编排时序

### 4.1 批次状态机

```text
Idle → SelfCheck → Running ⇄ Paused
                       ├→ Aborting → Aborted（终态）
                       └→ Completed | CompletedWithFailures（终态）
```

SelfCheck 失败回 `Idle` 并报配置/传输类错误。终态判定：遍历完成 → 存在 `Failed` 路径为 `CompletedWithFailures`，否则 `Completed`（FR-076）。

### 4.2 单路径流水线（PathExecutor）

```text
1. Switch1x8:   SETSW1-CHi          → 回读确认（失败按设备类重试）
2. Switch8x8:   SETSWi-OUTj         → 回读确认（同上）
3. Settle:      等待 T_settle（TBD-D04 实测，配置项，禁止写死）
4. Scan×N:      N 次扫描 → staging/<groupId>/R00k/（.ready 交接）
                —— N 次期间 OCS 不动（FR-029）；R00k 失败按 FR-033 重扫
5. GroupCommit: GroupPipeline 校验 → GroupStore.Commit（原子）
                validCount<N → AVG+incomplete（FR-038）；全部失败 → 路径 Failed
6. Evaluate:    MetricsReader → FlagEvaluator → PathResult
7. Persist:     DetectionStore 原子更新 detection.json（§5.3）
```

每步耗时计入 `durationMs` 分段（NFR-001）。

### 4.3 恢复流程（关闭 SDD-P2-4、SDD-P2-5）

异常退出后重启，`RecoveryService` 扫描 `DETECTION_DATA`：

1. 找到 `batchState = Running` 的批次目录（§5.3 运行中快照）。
2. **staging 半成品组**（SDD-P2-5）：`_staging/` 中无 `.ready` 完整交接的组**整体废弃重采**——删除该 staging 组，对应路径 `attempt + 1`（SDD-P2-4：`attempt` 递增唯一场景即批次恢复后的路径重做；批内采集级重扫不递增）。
3. 已提交的组不重采；`detection.json` 中已有终态的路径保持。
4. 用户确认后从首个未完成路径继续，或标记 `Aborted`（FR-078）。

### 4.4 重测批次（关闭终审 P1-3）

重测批次持有一个**子集 PathPlan**：由原批次 `detection.json` 中 `pathState ∈ {Failed, NotAttempted}` 的路径构造，沿用原批次的 `in/out/route1x8/N` 与配置。由此：

- 子集批次的 `total` = 重测路径数（**完整批次才为 64**）；
- `detection.json` 的 `paths[]` 长度 = `total`（SRS §6.2"长度 64"与 FR-070"恒为 64"按此理解，措辞修正登记 SRS v0.5）；
- 重测完成：新批次 `retryOf` = 原批次 ID；原批次只追加 `retestBatchIds`（`DetectionStore` 原子改写，§5.3），原结果字节级不变（TC-10）；
- 重测批次仍可再重测（链式 `retryOf`），`BatchSummaryCalculator` 按 `total` 口径汇总，不与原批次混合计数。

## 5. 数据与存储

### 5.1 目录布局

完全采用 [SRS §6.6](SRS_检测平台_v0.4.md)（`DETECTION_DATA/` → `BATCH-.../` → `GR-.../` → `Rxxx/`），本文不重复。补充两个 SRS 未指定的目录：

| 路径 | 用途 | 关闭 |
|---|---|---|
| `DETECTION_DATA/_baseline/` | `baseline.json` 版本库存放地 | SDD-P2-6 |
| `DETECTION_DATA/_log/<batchId>.log` | 按批次的 TX/RX 与运行日志 | FR-124、NFR-005 |

### 5.2 GroupStore 复用（B1）

`new GroupStore(batchDir)`——批次目录即 L2 数据根。`_staging/`、`.ready`、`Commit`（拒绝覆盖）、`MarkFailed` 全部原样复用；`EnforceRetention` **不用**（B3，由 §5.5 批次级清理取代）。`CleanupStaging` 在批次恢复时使用（§4.3）。

### 5.3 detection.json 写入策略（含运行中快照）

- **写入时机**：批次创建、每路径终态、批次终态、`retestBatchIds` 追加时；均为"写临时文件 + 原子改名"。
- **运行中快照**：SRS §6.2 的 `batchState`/`pathState` 枚举是终态语义；落盘文件在运行中允许两个快照值——`batchState: "Running"`、`pathState: "Pending"`。恢复与历史读取按"非终态即未完成"处理。SRS v0.5 可将这两个值回填进 schema 注记（不改终态语义）。
- **schemaVersion**：`"1.0"`（SDD-P2-7）。
- **`avgScanId`**：直接取 L2 `AV-YYYYMMDD-HHMMSS-NNN` 格式 ID（SDD-P2-3，与 L2 §2.1 一致）。
- **`index.csv`**：批次到达终态时生成一次；列 = SRS §6.6 所列（pathId, groupId, avgScanId, scanIds, 状态）。

### 5.4 基线与校准

- 基线只读导入：`BaselineCatalog` 扫描 `_baseline/`，校验 schema 与版本递增；匹配多条时由 UI 强制人工选择（FR-062）。
- `ofdrConfigHash` 在 SelfCheck 期读取一次并记入批次；`calibrationStatus` 三态判定在批次启动时完成（FR-036/037）。

### 5.5 清理与保留（关闭 SDD-P2-2）

清理统一归 `RetentionService`（数据维护模块），FR-089 的"曲线保留"与 FR-121 的"批次清理"是同一服务的两种策略：

| 策略 | 行为 |
|---|---|
| 整批删除 | 删除 `BATCH-*` 目录（含日志）；`_baseline/`、`_calibration/` 永不涉及 |
| 仅保留结果 | 批次内删除各 `GR-*/Rxxx/` 与 `AVG/` 的 `raw.bin`、`*.f64`，保留全部 JSON；`detection.json`/`index.csv` 保留 |
| 保留天数/批次数 | 按批次目录时间戳整批删除 |

默认值待 TBD-D12；执行前预览 + 二次确认（FR-122/080）。

## 6. 错误模型（对应 SRS §4.14）

| Vendor/内部异常 | 映射 | 批次行为 | flags |
|---|---|---|---|
| `TransportError`（串口打不开/写失败/读超时/断连） | 传输 | 单路径重试（次数/退避配置，默认待 TBD-D04）→ 仍败则 `Failed` | `measurement_failed` |
| `ProtocolError`（帧不完整/超长/不可解析） | 协议 | 同上 | `measurement_failed` |
| `DeviceError`（`<ERROR>`/`<ER_SetValue>`/回读不一致） | 设备 | 同上 | `measurement_failed` |
| 扫描失败/帧无效 | 测量 | 重扫 → 仍败 `Failed` 或 `notComputed` | `measurement_failed` / 无 |
| 标定缺失/hash 不一致 | 标定 | 不阻断；IL/RL `null` | `calibration_invalid` |
| 无适用基线 | 基线 | 不阻断 | `baseline_missing` |
| 配置非法 | 配置 | 阻止开批 | 启动期错误对话框 |

所有异常携带：类别、`pathId`（若有）、可读消息、原始 TX/RX 十六进制摘要、建议动作（NFR-014）。UI 按 `ErrorClass` 分色呈现。

## 7. 测试策略

| 层 | 手段 | 覆盖 |
|---|---|---|
| Detection.Core | 纯函数单测：计划校验、汇总口径、FlagEvaluator 四值×基线矩阵、BaselineCatalog 版本/匹配 | TC-16、TC-20、TC-06/07 |
| Detection.Vendor | `IOcsTransport` 注入字节流：合法/非法/超时/断连回包夹具（`tests/fixtures/ocs/`）；命令构造断言 | TC-03、TC-04、TC-17、TC-18 |
| Orchestration | Mock 三设备（虚拟串口或内存 transport）跑全批次：失败注入、中止、恢复、N>1 平均 | TC-01/02/05/08/09/10/27/28 |
| App | 人工走查 + TC-21/23 界面判据 | UI 相关 AC |
| 实机（并行解锁后） | OCS-only → LGA50-only → 联合冒烟 → TC-29 三批重复性 | TBD-O/D05 关闭后 |

夹具新增按规则 20 放 `tests/fixtures/ocs/`、`tests/fixtures/detection/`，不依赖本机路径。

## 8. 部署与配置

### 8.1 配置文件 `detection.config.json`

```text
ofdr:      { endpoint, 扫描参数(量程/分辨率/平均), timeoutMs }
ocs1x8:    { port, baud=9600,  handshake=none, timeoutMs=300, retry=2, backoffMs=[100,300] }
ocs8x8:    { port, baud=115200, handshake=none, timeoutMs=300, matrixTimeoutMs=500, retry=2, backoffMs=[100,300] }
plan:      { planVersion, entries[64] { pathId, in, out, route1x8, N } }
settleMs:  光路稳定时间——实测未完成（TBD-O03），UI 强制填写且无默认值
retention: { mode, days, batches, diskWarnBytes, diskHardBytes, logMaxBytes }
dataRoot:  DETECTION_DATA 路径
```

运行中锁定（FR-118）；变更在批次前完成并重算 `configHash`。

默认值依据：串口超时/重试来自[实测报告 §4](OCS_协议实测报告_2026-11-11.md)（取值规则 `max(300, p99×3 取整百)`，重试 2 次、退避 100/300 ms），**是实测值而非猜测值**，可按现场调整；`settleMs` 未实测，保持强制填写、阻止自检（FR-116，SRS 1.4 禁写死未实测项）。

### 8.2 单实例

启动时获取命名 `Mutex`（`Global\GM_OFDR_Detection_App`）；获取失败弹窗退出（FR-007/TC-15）。

### 8.3 构建

`dotnet build products/detection/Detection.sln`，.NET 8，0 警告 0 错误；CI 在现有 workflow 增加该矩阵项（实现阶段提交）。

### 8.4 外部依赖声明（规则 00 程序）

`Detection.Vendor` 需要 **`System.IO.Ports`**（微软官方 NuGet 包）——.NET 8 中 `SerialPort` 不在 BCL 内。规则 00 要求引入依赖前完成约束声明与 ADR：

- **理由**：OCS 为 RS232，无纯 BCL 替代；P/Invoke Win32 API 实现代价与维护成本远高于官方包。先例：`devices/ocs/tools/ocs_measure.ps1` 已用 `System.IO.Ports`（PowerShell 5 内置）完成协议实测，见[实测报告](OCS_协议实测报告_2026-11-11.md)。
- **范围**：仅 `Detection.Vendor` 引用；不向 Core/App 传递。
- **ADR**：`docs/detection/ADR-001_System.IO.Ports.md` 随首个实现提交先行创建，记录决策、版本与替代方案考量。

## 9. 输入关闭情况

| 编号 | 处置 |
|---|---|
| SDD-P2-1 | 连通性措辞属 SRS 文本，留 SRS v0.5 顺带修正；本文 §3.1 直接按四值枚举实现 |
| SDD-P2-2 | §5.5 清理统一归 `RetentionService` |
| SDD-P2-3 | §5.3 `avgScanId` 取 L2 `AV-` 格式 |
| SDD-P2-4 | §4.3 `attempt` 递增 = 批次恢复后路径重做 |
| SDD-P2-5 | §4.3 staging 半成品组废弃重采 |
| SDD-P2-6 | §5.1 `DETECTION_DATA/_baseline/` |
| SDD-P2-7 | §5.3 `schemaVersion = "1.0"` |
| 终审 P1-1 | §3.2 `OcsProtocolConstants` 与 §8.1 矛盾已解：超时/重试为配置项，默认值来自实测 |
| 终审 P1-2 | §8.4 外部依赖声明（`System.IO.Ports`）+ ADR-001 计划 |
| 终审 P1-3 | §4.4 重测批次 = 子集 PathPlan；SRS 措辞修正登记 v0.5 |
| 实测回流 | §3.2（波特率/流控/定界/唯一性）、§8.1（超时/重试默认值）已吸收[实测报告](OCS_协议实测报告_2026-11-11.md)结论；TBD-O01/02/04/05/09 关闭 |

## 10. 追溯与变更

### 10.1 SRS 覆盖映射

| SRS 需求区 | 本文落点 |
|---|---|
| FR-001~007 | §2.3、§3.2、§8.2 |
| FR-010~014 | §3.1 `PathPlan`、§8.1 |
| FR-020~029 | §3.2、§4.2 |
| FR-030~038 | §3.2 `Lga50ScanSource`、§4.2 |
| FR-040~047 | §3.1 `FlagEvaluator`、§3.3 `MetricsReader` |
| FR-050~054 | §3.1、§3.4 |
| FR-060~066 | §3.1 `BaselineCatalog`、§5.4 |
| FR-070~080 | §4.1~4.3 |
| FR-085~090 | §3.4、§5.3 |
| FR-095~106 | §5.1~5.3 |
| FR-110~112 | §3.1（导出）+ 后期实现 |
| FR-115~118 | §8.1 |
| FR-120~125 | §5.5 |
| NFR-001~015 | §2.2、§5.3、§6、§7、§8 |

### 10.2 变更记录

| 版本 | 日期 | 变更 |
|---|---|---|
| v0.1 | 2026-11-11 | 初稿：五工程布局与依赖、单进程模型、串口独占/重连、模块设计、批次状态机与路径流水线、恢复流程、detection.json 写入策略（含运行中快照）、错误模型、测试策略、配置与单实例；关闭 SDD-P2-2~7。落盘前终审修复：P1-1 超时常量矛盾、P1-2 `System.IO.Ports` 依赖声明与 ADR 计划、P1-3 重测批次子集计划（§4.4）、P2-1/P2-3 顺手修复。吸收 OCS 协议实测结论：8×8 波特率 115200、`Handshake=None`、完整帧发送、单路不校验唯一性、超时/重试默认值（300/500 ms、2 次、100/300 ms）；`settleMs` 仍待 TBD-O03 |
