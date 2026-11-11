# 检测平台上位机 SRS v0.4

> **文档编号**：SRS-DET-001
> **版本**：v0.4（定稿）
> **日期**：2026-11-11
> **适用产品**：OCS + OFDR 检测系统上位机（8×8 OCS 为 DUT）
> **继承基线**：LGA50 自研上位机（GMIX）
> **关联文档**：[检测系统文档骨架](README.md)、[OCS 控制协议分析](OCS_控制协议分析.md)、[L1 统一数据契约](../contracts/统一数据契约_v0.7.md)、[L2 GM-IX 数据与处理规范](../contracts/GM-IX_数据与处理规范_v1.0.md)
> **交付边界**：本文只定义上位机软件需求；光路、OCS、OFDR、夹具与工装是外部依赖与约束。
> **契约优先**：本文与 L1/L2 冲突时以契约为准；本文只规定检测层新增需求，不重定义契约字段语义。

## 1. 范围与目标

### 1.1 目的

为 **1×8 OCS 前级路由 + 8×8 OCS DUT + LGA50-D OFDR** 组成的检测系统开发上位机软件，自动完成 64 条 `IN→OUT` 路径的通道切换、扫描采集、指标计算、测量记录与批次汇总。

### 1.2 产品定位

检测平台上位机是 LGA50 自研上位机的后续版本，继承链为 **GM-IX → LGA50 → 检测平台**。复用 LGA50 的采集、指标、标定与组存储能力，新增 OCS 控制、64 路径编排、测量记录与批次报告。

### 1.3 首版范围（v0.4）

| 项 | 范围 |
|---|---|
| OFDR | LGA50-D 单型 |
| OCS | 1×8 前级路由 + 8×8 DUT |
| 路径 | 64 条，单向 `IN i → OUT j`，`i,j = 1..8` |
| 测量输出 | 连通性、IL、RL（口径见 §4.5，权威来源为 L2 `summary.json`） |
| 辅助证据 | 峰位置/峰值存在性，用于展示与关注提示 |
| 结果输出 | 测量值、证据、关注提示；**不做自动 Pass/Fail** |
| 指纹验证 | CNN 分支后期接入，只追加独立验证标记，不改写测量值 |
| 批次策略 | 单路径测量失败继续跑完，汇总失败，支持事后重测（新批次，`retryOf` 关联） |
| 数据存储 | 一次检测 = 一个批次目录（`DETECTION_DATA/BATCH-...`）；**一条路径 = 一个 L2 组**；新增 `detection.json` 旁车文件（见 §6.6） |
| 软件形态 | 独立 `products/detection` 解决方案，引用共享 `Gmix.Core` / `Gmix.Vendor`（设计约束，SDD 落地） |

### 1.4 非目标（v0.4）

- 不做 OCS 固件、光学设计、采集板或工装开发。
- 不测反向 `OUT→IN` 路径；反向作为后续版本扩展。
- 不接入 GM-IX 整机；首版只接 LGA50-D。
- CNN 指纹不改写测量值、不生成结论，不引入 Python 运行时到产品进程。
- 不做多工位并行、多 DUT 同时检测、远程/云端。
- 不在 v0.4 输出自动 Pass/Fail 或合格结论；自动判定规则与阈值后续版本再定。
- 不生成、不修改、不审批基线；软件只导入与校验基线。
- 不实现多语言；首版只提供简体中文界面。
- 不修改 `GMIX_DATA/` 既有布局，也不复用其按组删除的保留逻辑。
- 不在未实测前写死切换稳定时间、超时、重试次数、IL/RL 参考范围或性能目标。

## 2. 系统上下文与假设

### 2.1 系统组成

| 角色 | 内容 |
|---|---|
| OFDR | 昊衡 LGA50-D，提供距离域曲线与标定/configHash |
| 前级 OCS | 1×8，`COM` 接 OFDR，`CH1~CH8` 接 8×8 DUT 的 `IN1~IN8` |
| DUT | 8×8 OCS，64 条 `IN→OUT` 路径为检测对象 |
| 测试光路 | 8×8 `OUT1~OUT8` 接测试光纤/夹具 |
| 上位机 | 设备连接、路径编排、扫描触发、指标、测量记录、报告 |

### 2.2 光路假设

```text
OFDR DUT 光口 → 1×8 COM
1×8 CH1..CH8 → 8×8 IN1..IN8
8×8 OUT1..OUT8 → 测试光纤/夹具
```

### 2.3 控制假设

- 上位机通过 LGA50-D 现有接口触发扫描并接收曲线。
- 上位机通过 RS232 控制 1×8 与 8×8 OCS；参数与命令集见 [OCS 控制协议分析](OCS_控制协议分析.md)。
- 1×8 与 8×8 使用独立串口；串口由编排器独占，UI 不直接打开串口。

### 2.4 设计假设

- 单工位、单操作员、同一时间只执行一个批次。
- 1×8 是前级路由，不是首版 DUT；8×8 是唯一 DUT。
- 每批次固定 64 条路径；路径计划可版本化。
- **一条路径的一次测量 = 一个 L2 组**；该路径的 N 次扫描 = 组内 `R001..R00N`，N>1 时生成 `AVG/`。
- 单条路径执行 N 次扫描期间，OCS 状态保持不变；状态快照记录在 `detection.json`。
- 64 条路径可通过 IL/RL 与峰位置证据区分；若硬件无法区分，需补充物理标记或已知长度（见 TBD-D03a）。
- 基线/参考数据由外部（硬件/工艺团队）提供并审批；软件只导入与校验。
- LGA50-D 的标定与 `configHash` 可读取；无法读取时按“未标定”处理。

## 3. 术语

| 术语 | 定义 |
|---|---|
| Path（路径） | 一条 `IN i → OUT j` 的 8×8 光路，共 64 条 |
| Batch（批次） | 一次完整检测：64 条路径的测量与汇总。对应一个批次目录 |
| Group（组） | **一条路径的一次完整测量**，对应 L2 组结构（`<groupId>/` 目录，含 `group.json`、`Rxxx/`、`AVG/`）；检测平台中组位于批次目录下（见 §6.6） |
| Repeat（采集） | 该路径的第 k 次扫描，对应组内 `Rxxx/`，`repeatIndex = 1..N` |
| Scan（扫描） | 与 Repeat 同义；一次扫描对应一个 `ScanId` 与一个 `Rxxx/` 目录 |
| Baseline（基线） | 外部提供、经审批发布的参考范围文件，带 `baselineId` 与版本 |
| Connectivity（连通性） | 见 §4.5.1 的可观察定义，取值 `ok / noPeak / invalidFrame / notComputed` |
| IL | 插入损耗，单位 dB，口径见 §4.5.2 |
| RL | 回波损耗，单位 dB，符号口径见 §4.5.3 |
| 关注提示（Attention flag） | 见 §4.6 的枚举，只提示不结论 |
| OCS State | 1×8 当前路由 + 8×8 当前矩阵状态 |
| 未标定 | LGA50 标定缺失或 `configHash` 不一致，此时不得输出绝对 IL/RL |

> **概念边界**：组不是“通道分组”“UI 分组”或“文件分类”，而是**一次测量的证据包**。批次由 64 个组构成；组由 N 个采集构成。三者层级不可互换。

## 4. 功能需求

优先级：**Must** = 首版必须；**Should** = 首版尽量；**Could** = 后续版本。

### 4.1 设备连接与自检

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-001 | 连接 LGA50-D，显示设备信息与连接状态 | Must | TC-01 |
| FR-002 | 连接 1×8 OCS 串口，读取 `IDN?` / `SN?` | Must | TC-01 |
| FR-003 | 连接 8×8 OCS 串口，读取 `IDN?` / `SN?` | Must | TC-01 |
| FR-004 | 启动自检：串口可打开、IDN 匹配、状态可查询、扫描就绪、磁盘余量达标 | Must | TC-01、TC-13 |
| FR-005 | 支持运行中拔插/重连，不要求重启软件；重连后必须重新查询 OCS 状态，不使用缓存 | Must | TC-14 |
| FR-006 | 错误分类见 §4.14，不同类别显示不同消息 | Must | TC-03、TC-04 |
| FR-007 | 单实例运行：同一工位同时只允许一个软件实例占用串口与批次 | Must | TC-15 |

### 4.2 路径计划

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-010 | 生成 64 条路径计划，路径 ID 为 `IN01_OUT01` ~ `IN08_OUT08` | Must | TC-01 |
| FR-011 | 路径计划包含 1×8 路由、8×8 IN/OUT、期望 DUT 端口、每路径扫描次数 N | Must | TC-01 |
| FR-012 | 校验路径计划无缺失、无重复、参数越界、N ≥ 1 | Must | TC-16 |
| FR-013 | 支持路径计划导入/导出 | Should | TC-16 |
| FR-014 | 路径计划带版本号，并纳入 `configHash` | Must | TC-12 |

### 4.3 OCS 控制

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-020 | 设置 1×8 路由（`COM → CHn`） | Must | TC-01 |
| FR-021 | 设置 8×8 单路（`IN i → OUT j`） | Must | TC-01 |
| FR-022 | 支持 8×8 整矩阵设置（`SETSWabcdefgh`） | Should | TC-17 |
| FR-023 | 查询 1×8 当前路由与 8×8 当前矩阵 | Must | TC-01 |
| FR-024 | 每次设置后回读确认，再进入扫描 | Must | TC-18 |
| FR-025 | 整矩阵设置前校验非 0 输出唯一性 | Must | TC-17 |
| FR-026 | 串口超时、重试次数、退避可配置 | Must | TC-03 |
| FR-027 | 映射 `<ERROR>`、`<ER_SetValue>` 与串口异常 | Must | TC-03、TC-04 |
| FR-028 | 串口独占：同一时刻只有一个进程/线程访问 | Must | TC-15 |
| FR-029 | 单条路径的 N 次扫描期间不得切换 OCS；切换只发生在路径边界 | Must | TC-27 |

### 4.4 扫描采集与标定校验

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-030 | 按路径触发 LGA50-D 扫描，每路径执行计划规定的 N 次采集 | Must | TC-01、TC-27 |
| FR-031 | 接收并保存距离域曲线；以 `ps_db` 为唯一绘制数据源，`ps` 作为原始证据保留 | Must | TC-12 |
| FR-032 | 扫描参数来自配置快照，不在运行中热改；同组内 `configHash` 必须一致 | Must | TC-19、TC-27 |
| FR-033 | 扫描超时、失败重扫策略可配置 | Must | TC-03 |
| FR-034 | 单路径扫描失败不终止整批 | Must | TC-02 |
| FR-035 | 扫描结果与批次、路径、组、OCS 状态关联 | Must | TC-12 |
| FR-036 | 批次开始前读取 LGA50 标定与 `configHash`，并记录到批次元数据 | Must | TC-05 |
| FR-037 | `configHash` 不一致或标定缺失时：不输出绝对 IL/RL（置 `null`），写 warning 并置 `calibration_invalid`；不阻断批次 | Must | TC-05 |
| FR-038 | N 次采集部分失败时按 L2 规则处理：`validCount < averageCount` 可出 `AVG` 但 `status = incomplete`；全部失败则该路径 `Failed` | Must | TC-28 |

> 扫描触发方式、完成判定、超时语义依赖 LGA50-D 现有接口行为，登记为 TBD-D10。

### 4.5 指标与测量口径

**4.5.1 连通性（可观察定义）**

连通性由三个可观察条件派生，取值唯一且互斥：

| 取值 | 条件 |
|---|---|
| `invalidFrame` | 扫描失败，或帧缺失/轴不完整/数组为空（不满足 L1 `L-01`） |
| `notComputed` | 帧有效但指标未计算（如标定失效导致 IL/RL 不可用，且未配置替代口径） |
| `noPeak` | 帧有效，但分析区间内无 `zeroPeak=false` 的峰 |
| `ok` | 帧有效，且分析区间内至少存在一个 `zeroPeak=false` 的峰 |

“接线正确”不作为连通性取值，而是独立证据（峰位置窗口），规则见 TBD-D05b。

**4.5.2 IL / RL 口径**

| 项 | 规定 |
|---|---|
| 权威来源 | L2 `summary.json` 的 `totalIlDb` / `totalRlDb`；`detection.json` 只引用或复制并标注来源，不重算 |
| 取值位置 | 路径测量值取**组内 `AVG/summary.json`**；N=1 时即 `R001/summary.json` |
| 原始值 | `totalIlRawDb` / `totalRlRawDb` 保留在 L2，检测层不作为主展示值 |
| 计算实现 | 复用 `Gmix.Core` 的 `MetricsAggregator`，检测层不重复实现 |
| 未标定 | 输出 `null`，UI 显示“未标定”，置 `calibration_invalid` |
| 无法计算 | 基线/段无效、区间内无有效峰时输出 `null`，禁止用 `0` 代替 |
| 精度 | `ilPrecisionDb` / `rlPrecisionDb` 仅来自 `AVG/`；单次测量不填 |
| 有效位数 | UI 显示 2 位小数，存储保留 double 原值，不做截断（L1 `D-02`） |

**4.5.3 RL 符号**

沿用 L2 裁定：`RL = 10·log10(P_refl/P_inc)`，不取倒数。未标定为正电平刻度，标定后为负值且反射越强越接近 0。任何层不得对 RL 字段取负号，UI 不改写符号。

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-040 | 计算每条路径连通性，取值按 §4.5.1 四值枚举 | Must | TC-02、TC-20 |
| FR-041 | 展示每条路径 IL，口径按 §4.5.2 | Must | TC-05、TC-11 |
| FR-042 | 展示每条路径 RL，符号按 §4.5.3 | Must | TC-11 |
| FR-043 | 计算峰位置/峰值存在性作为辅助证据；零距峰按 L2 规则过滤（位置 `< analysisRange.startM` 视为 `zeroPeak`，不参与峰编号） | Must | TC-11 |
| FR-044 | 指标与证据随路径结果持久化 | Must | TC-12 |
| FR-045 | 指标单位、口径、有效位数与 L2 一致 | Must | TC-11 |
| FR-046 | IL/RL 参考范围来自基线，不硬编码；仅用于展示与提示 | Must | TC-06、TC-07 |
| FR-047 | 距离轴、峰位置在存储、计算、导出中一律用**米**；UI 可格式化为 mm，但不得回流数据 | Must | TC-11 |

> IL/RL 参考范围与展示口径（绝对范围、基线差值或两者结合）登记为 **TBD-D05a**；峰位置窗口与提示规则登记为 **TBD-D05b**。

### 4.6 关注提示

关注提示是界面标记，不改变测量值、不产生合格/不合格结论。

| 标记 | 触发条件 | 级别 |
|---|---|---|
| `measurement_failed` | 扫描失败、串口切换失败、指标计算失败 | 错误 |
| `calibration_invalid` | 标定缺失或 `configHash` 不一致 | 警告 |
| `no_peak` | 连通性为 `noPeak` | 警告 |
| `peak_unexpected` | 峰位置落在期望窗口外（窗口规则见 TBD-D05b） | 警告 |
| `out_of_reference` | IL/RL 超出基线参考范围 | 警告 |
| `baseline_missing` | 未导入适用基线 | 提示 |
| `avg_incomplete` | 组内 `validCount < averageCount`，平均结果不完整 | 提示 |
| `cnn_suspicious` | CNN 离线验证标记异常（后期） | 提示 |

**呈现要求**

- 8×8 矩阵中每个单元格显示一个角标；多标记时按级别取最高：错误 > 警告 > 提示。
- 颜色图例：灰色 = 未执行，蓝色 = 正常（无标记），黄色 = 警告/提示，红色 = 错误。
- 顶部汇总条显示各标记数量，点击可筛选矩阵。
- 详情面板逐条列出标记、触发原因、相关数值与原始 TX/RX 摘要。

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-050 | 展示每条路径的连通性、IL、RL 测量值，不输出 `Pass/Fail` | Must | TC-01 |
| FR-051 | 按上表生成关注提示，同一路径可同时存在多个标记 | Must | TC-02、TC-05、TC-06、TC-20、TC-28 |
| FR-052 | 关注提示不拦截批次、不改变测量值 | Must | TC-02 |
| FR-053 | 关注提示可在矩阵视图筛选，详情可查看触发原因与相关数值 | Should | TC-21 |
| FR-054 | 人工可追加备注/人工关注标记，记录操作员与时间 | Should | TC-21 |

### 4.7 基线管理

基线由外部提供并审批，软件只导入、校验、使用、留痕。

**基线文件 `baseline.json`（建议字段）**

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | 基线格式版本 |
| `baselineId` | string | 是 | 唯一标识 |
| `version` | int | 是 | 追加递增，不覆盖 |
| `applicable` | object | 是 | `dutModel` 必填；`dutSerial`、`batch` 可选 |
| `metrics` | object | 是 | `il`、`rl`、`connectivity` 的参考范围与容差 |
| `approvedBy` | string | 是 | 审批人 |
| `approvedAt` | string(UTC) | 是 | 审批时间 |
| `note` | string | 否 | 说明 |

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-060 | 导入 `baseline.json` 并校验 schema、必填字段与版本递增 | Must | TC-06、TC-07 |
| FR-061 | 基线版本追加，不覆盖历史版本；历史批次仍能解析其引用版本 | Must | TC-06 |
| FR-062 | 按 `dutModel` + 可选 `dutSerial` 选择基线；匹配到多条时要求人工确认 | Must | TC-07 |
| FR-063 | 无匹配基线时允许测量，置 `baseline_missing`，不评估 `out_of_reference` | Must | TC-06 |
| FR-064 | 批次记录 `baselineId` 与 `version`；结果可回溯到当时基线 | Must | TC-12 |
| FR-065 | 软件不生成、不修改、不审批基线；不提供“从测量结果自动生成基线”功能 | Must | TC-22 |
| FR-066 | 基线与测量数据分离存储，清理批次不影响基线 | Must | TC-22 |

### 4.8 批次执行与状态机

**批次状态**

```text
Idle → SelfCheck → Running → (Paused ↔ Running) → Completed
                          ↘ Aborting → Aborted
                          ↘ CompletedWithFailures
```

**路径状态**：`Pending → Switching → Settling → Scanning → Computed | Failed`；中止时未执行的路径为 `NotAttempted`。

**汇总口径**（`total` 恒为 64）

- `completed`：状态为 `Computed` 的路径数
- `measurementFailed`：状态为 `Failed` 的路径数
- `notAttempted`：状态为 `NotAttempted` 的路径数（仅中止时可能非 0）
- `retried`：本批次内发生过重试的路径数

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-070 | 以路径计划启动批次，`total` 恒为 64 | Must | TC-01 |
| FR-071 | 默认按路径顺序串行执行 | Must | TC-01 |
| FR-072 | 显示当前路径、已完成数、总数、预计剩余时间 | Must | TC-23 |
| FR-073 | 支持暂停/继续；暂停后不再发送新命令，正在进行的单路径完成后停 | Should | TC-23 |
| FR-074 | 支持中止；已完成结果保留，未执行路径记为 `NotAttempted`，批次状态为 `Aborted` | Must | TC-08 |
| FR-075 | 单路径测量失败继续执行后续路径 | Must | TC-02 |
| FR-076 | 批次结束状态为 `Completed` 或 `CompletedWithFailures`；存在 `Failed` 路径时必须为后者 | Must | TC-02 |
| FR-077 | 支持仅重测失败/未完成路径：生成**新批次**，`retryOf` 指向原批次，原批次只追加 `retestBatchIds`，不改写原结果 | Must | TC-10 |
| FR-078 | 异常退出后可恢复未完成批次：可继续执行剩余路径，或标记为不可续 | Should | TC-09 |
| FR-079 | 记录操作员、工位、设备 SN、`ofdrConfigHash`、开始/结束时间（UTC） | Must | TC-12 |
| FR-080 | 中止、重测、复位、清理等破坏性操作执行前需二次确认 | Must | TC-08、TC-22 |

> AC-001 的“64 路径完整执行”只适用于状态为 `Completed` / `CompletedWithFailures` 的批次；`Aborted` 批次按 `NotAttempted` 计数验收。

### 4.9 结果、报告与导出

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-085 | 以 8×8 矩阵展示 64 条路径测量值与关注提示 | Must | TC-01 |
| FR-086 | 单路径详情：连通性、IL、RL、峰证据、关注提示、OCS 状态、标定状态、组与 scanId、耗时、重试 | Must | TC-21 |
| FR-087 | 导出 CSV/JSON；检测 CSV 列按 §6.5，且不得改变 L2 既有 CSV 列序 | Must | TC-12 |
| FR-088 | 导出包含批次元数据、`baselineId`/`version`、`configHash`、`ofdrConfigHash`、路径↔组映射 | Must | TC-12 |
| FR-089 | 原始曲线保留策略可配置，并支持手动立即清理 | Should | TC-22 |
| FR-090 | 生成可打印报告，内容按 §4.9.1 | Could | TC-12 |

**4.9.1 报告内容**

批次摘要（批次 ID、状态、起止时间、操作员、工位、设备 SN、标定状态、基线与版本）、配置摘要（`configHash`、路径计划版本）、8×8 结果矩阵、异常清单（标记、路径、原因、相关数值、所属组与 scanId）、统计（各标记数量、耗时分段）、生成时间。

### 4.10 存储与契约

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-095 | 复用 L2 `group.json` / `scan.json` / `summary.json` 结构与语义，不改变既有字段与 CSV 列序 | Must | TC-12 |
| FR-096 | 每条路径产出一个符合 L2 的组目录（`group.json` + `Rxxx/` + 可选 `AVG/`） | Must | TC-12、TC-27 |
| FR-097 | 新增 `detection.json` 旁车文件，字段与类型见 §6.2 | Must | TC-12 |
| FR-098 | 旁车文件带 `schemaVersion` | Must | TC-12 |
| FR-099 | 批次记录 `configHash` 与 `ofdrConfigHash` | Must | TC-12 |
| FR-100 | `scanId` 直接采用 L1 `G-01` 格式；`groupId` 采用 L2 格式，不另行定义 | Must | TC-12 |
| FR-101 | 每路径（组）完成后原子提交，异常退出不产生半写组 | Must | TC-09 |
| FR-102 | 目录层级：`DETECTION_DATA/` → `BATCH-.../`（批次）→ `GR-.../`（组）→ `Rxxx/`（采集）；批次目录等价于一个 L2 数据根 | Must | TC-12 |
| FR-103 | `GMIX_DATA/` 布局保持不变；检测数据不写入 `GMIX_DATA` 根 | Must | TC-12 |
| FR-104 | 批次目录已存在时拒绝启动；组目录已存在时拒绝提交，禁止覆盖 | Must | TC-12 |
| FR-105 | 路径↔组映射由 `detection.json` 显式记录；业务逻辑不得解析 `groupId`/`scanId` 字符串推断含义 | Must | TC-12 |
| FR-106 | 能读取旧批次并展示结果 | Should | TC-24 |

### 4.11 CNN 指纹验证（后期）

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-110 | 导出 CNN 离线验证所需的数据包 | Should | TC-25 |
| FR-111 | 导入 `cnn_verification.json`，追加 `cnn_suspicious` 标记 | Could | TC-25 |
| FR-112 | CNN 结果不得改写测量值、不得生成合格结论、不得影响连通性取值 | Must | TC-25 |

### 4.12 配置与设备管理

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-115 | 可配置：OFDR 连接参数、1×8 与 8×8 串口号、波特率、超时、重试、数据根目录、每路径扫描次数 N | Must | TC-19 |
| FR-116 | 配置校验：必填项、串口号存在性、波特率合法性、目录可写；不合法阻止自检通过 | Must | TC-19 |
| FR-117 | 配置可导出/导入，并纳入 `configHash` | Should | TC-19 |
| FR-118 | 配置为不可变快照：运行中不可修改；修改需在批次开始前完成 | Must | TC-19 |

### 4.13 数据维护

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-120 | 清理以**批次**为单位整批删除，禁止删除批次内的部分组 | Should | TC-22 |
| FR-121 | 清理策略：保留天数 / 批次数 / 仅保留结果（删除 `Rxxx/` 的 raw 与曲线，保留 `AVG/` 与 JSON） | Should | TC-22 |
| FR-122 | 清理前显示将删除的批次与占用空间，需二次确认 | Should | TC-22 |
| FR-123 | 磁盘剩余空间低于阈值时预警；低于硬阈值时阻止开批 | Should | TC-13 |
| FR-124 | 日志按批次分文件，单文件大小与总占用可配置上限 | Should | TC-22 |
| FR-125 | 不复用 L2 的按组保留逻辑（`EnforceRetention`）做跨批清理 | Must | TC-22 |

### 4.14 错误分类与失败处理

| 类别 | 触发 | 批次行为 | 关注提示 |
|---|---|---|---|
| 传输 | 串口打不开、写失败、读超时、断连 | 单路径重试；持续失败则该路径 `Failed`，批次继续 | `measurement_failed` |
| 协议 | 回包不以 `<` 起、不以 `>` 止、超长、无法解析 | 重试；持续失败则该路径 `Failed` | `measurement_failed` |
| 设备 | `<ERROR>`、`<ER_SetValue>`、回读不一致 | 重试；持续失败则该路径 `Failed` | `measurement_failed` |
| 测量 | 扫描失败、帧无效、指标不可算 | 该路径 `Failed` 或 `notComputed` | `measurement_failed` / 无 |
| 标定 | 标定缺失、`configHash` 不一致 | 不阻断；IL/RL 置 `null` | `calibration_invalid` |
| 基线 | 无适用基线、版本不匹配 | 不阻断；不评估 `out_of_reference` | `baseline_missing` |
| 配置 | 必填项缺失、串口不存在、目录不可写 | 阻止开批 | 无（启动期错误） |

错误消息必须包含：类别、路径 ID（若有）、可读说明、原始 TX/RX 或异常摘要、建议动作。禁止静默吞掉协议错误。

## 5. 非功能需求

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| NFR-001 | 批次报告必须包含总耗时与分段耗时（切换/稳定/扫描/计算）；数值目标见 TBD-D11 | Must | TC-23 |
| NFR-002 | UI 进度刷新不超过 1 秒 | Should | TC-23 |
| NFR-003 | 每条路径（组）完成后、进入下一路径前完成落盘；强制结束后已完成组可读 | Must | TC-09 |
| NFR-004 | 恢复未完成批次时，未执行路径集合以 `NotAttempted` 为准 | Should | TC-09 |
| NFR-005 | 记录原始 TX/RX、命令序号、每条路径耗时；日志可按批次定位 | Must | TC-03 |
| NFR-006 | 依赖方向：App 不引用 Vendor；厂商类型只出现在 Vendor 层 | Must | 依赖检查 |
| NFR-007 | 默认流程无需命令行；破坏性操作有二次确认 | Should | TC-08 |
| NFR-008 | 不自动执行 `<RST>`；复位必须显式触发 | Must | 代码审查 |
| NFR-009 | 本地运行，不依赖云服务与外部凭据 | Should | 离线可用 |
| NFR-010 | 运行于 Windows + .NET 8；支持含中文与空格的安装路径与数据路径 | Must | TC-26 |
| NFR-011 | 配置、批次 ID、日志时间戳统一使用 UTC 存储，UI 按本地时区显示 | Must | TC-12 |
| NFR-012 | 串口与文件 IO 不在 UI 线程执行；UI 在长操作期间保持可交互 | Must | TC-23 |
| NFR-013 | 界面为简体中文；用户可见字符串集中管理，不散落在代码中 | Should | 代码审查 |
| NFR-014 | 错误消息包含分类、说明、原始数据摘要与建议动作 | Must | TC-03、TC-04 |
| NFR-015 | 一个批次的组目录集合必须自洽：`detection.json` 列出的组全部存在，或明确标注缺失 | Must | TC-12 |

## 6. 数据与契约

### 6.1 契约边界

- **L1**：曲线使用 `CurveScan`（轴 + dB 数组 + 元数据）；横轴恒为距离、单位恒为米（C-08）；`ScanId` 按 G-01；不得有损落盘（D-02）。
- **L2**：`group.json` / `scan.json` / `summary.json` 为测量记录与指标口径的权威来源；组 = 一次测量组，`Rxxx` = 一次采集，`AVG/` 仅 N>1。
- **检测层新增**：仅 `detection.json` 与批次目录层级；不改变 L1/L2 字段语义，不升 L1/L2 版本。
- **批次目录等价于 L2 数据根**：批内 `_staging/`、`GR-*/`、`AVG/` 与 L2 目录树同构，`Gmix.Core.GroupStore` 以批次目录为 `DataRoot` 可复用（该约定写入 SDD，并在 L2 §2.0 加注记，不改条款）。
- **校准库**：`DETECTION_DATA/_calibration/` 跨批次共享，不随批次删除；各 `Rxxx/calibration.json` 记录当次使用的快照。

### 6.2 `detection.json` schema

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | 本文件版本 |
| `batchId` | string | 是 | 批次唯一标识，与批次目录同名 |
| `batchState` | enum | 是 | `Completed / CompletedWithFailures / Aborted` |
| `retryOf` | string/null | 否 | 重测批次指向的原批次（FR-077） |
| `retestBatchIds` | string[] | 否 | 原批次追加的重测批次列表 |
| `planVersion` | string | 是 | 路径计划版本 |
| `configHash` | string | 是 | 检测侧配置哈希 |
| `ofdrConfigHash` | string/null | 否 | LGA50 配置哈希；读取失败为 `null` |
| `calibrationStatus` | enum | 是 | `calibrated / uncalibrated / mismatch` |
| `baselineId` | string/null | 否 | 使用的基线 |
| `baselineVersion` | int/null | 否 | 基线版本 |
| `deviceInfo` | object | 是 | `ofdrSn`、`ocs1x8Sn`、`ocs8x8Sn` |
| `startedAt` / `endedAt` | string(UTC) | 是 | ISO 8601 |
| `operator` / `station` | string | 是 | 操作员与工位 |
| `summary` | object | 是 | `total`、`completed`、`measurementFailed`、`notAttempted`、`retried` |
| `paths[]` | array | 是 | 长度 64，元素见下 |

`paths[]` 元素：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `pathId` | string | 是 | `IN01_OUT01` 形式 |
| `in` / `out` | int | 是 | 1~8 |
| `pathState` | enum | 是 | `Computed / Failed / NotAttempted` |
| `groupId` | string/null | 否 | 该路径对应的 L2 组目录名；`NotAttempted` 为 `null` |
| `averageCount` | int | 是 | 计划扫描次数 N |
| `validCount` | int | 否 | 实际成功的采集次数 |
| `avgScanId` | string/null | 否 | N>1 时指向 `AVG/`；N=1 为 `null` |
| `scanIds` | string[] | 否 | 组内全部 scanId，与 `Rxxx/` 一一对应 |
| `ocsState` | object | 否 | 1×8 路由 + 8×8 矩阵快照 |
| `connectivity` | enum | 是 | `ok / noPeak / invalidFrame / notComputed` |
| `ilDb` | double/null | 否 | 口径见 §4.5.2；未标定或不可算为 `null` |
| `rlDb` | double/null | 否 | 符号见 §4.5.3 |
| `metricSource` | string | 否 | 引用 L2 `summary.json` 的定位信息（组内相对路径） |
| `evidence` | object | 否 | `peakPresent`、`peakPositionM`（米）、`peakNo`、`zeroPeakExcluded` |
| `flags[]` | enum[] | 否 | §4.6 标记 |
| `notes[]` | object[] | 否 | 人工备注：文本、操作员、时间 |
| `attempt` | int | 是 | 本批次内第几次尝试，从 1 起 |
| `durationMs` | int | 否 | 该路径总耗时 |
| `errorClass` | enum/null | 否 | §4.14 类别，仅 `Failed` 时填 |

### 6.3 配置快照

必须纳入 `configHash` 的字段：

- OFDR 扫描参数（量程、分辨率、平均次数等）
- OCS 串口号、波特率、超时、重试
- 64 路径计划及版本（含每路径扫描次数 N）
- 基线引用（`baselineId` + `version`，不含参考范围数值本身）
- 数据保留策略与目录

`ofdrConfigHash` 单独记录，用于判定 IL/RL 是否为绝对量。

### 6.4 单位与口径

| 量 | 存储/计算/导出 | UI |
|---|---|---|
| 距离、峰位置 | 米（m） | 可显示 mm |
| IL、RL | dB | dB |
| 时间戳 | UTC ISO 8601 | 本地时区 |
| 功率域 | 沿用 L1 C-01 | 同左 |

单位换算只发生一次；UI 格式化不得回流数据。

### 6.5 检测 CSV 列

检测导出与 L2 既有 CSV 分离，不改变 L2 列序。

```text
batchId, pathId, in, out, pathState, groupId,
averageCount, validCount, connectivity,
ilDb, rlDb, calibrationStatus, metricSource,
peakPresent, peakPositionM, peakNo, zeroPeakExcluded,
flags, errorClass, baselineId, baselineVersion,
avgScanId, scanIds, attempt, durationMs, operator, station, startedAt, endedAt
```

- 空值写空字符串，不写 `0` 或 `NaN`。
- `flags` 与 `scanIds` 多值用 `;` 分隔。
- `peakPositionM` 单位为米，保留 6 位小数。

### 6.6 目录结构

```text
DETECTION_DATA/                              # 检测层数据根（与 GMIX_DATA 并列）
├─ _calibration/                             # 全局校准库（跨批次共享）
│  └─ <calibrationId>/calibration.json
└─ BATCH-20261111-153000-001/                # 一次完整检测
   ├─ detection.json                         # 批次元数据、路径↔组映射、测量值、证据、flags
   ├─ index.csv                              # 人读索引：pathId,groupId,avgScanId,scanIds,状态
   ├─ _staging/                              # 本批次暂存（L2 语义；失败留现场）
   │  └─ GR-20261111-1530-001/
   ├─ GR-20261111-1530-001/                  # IN01_OUT01，N=1
   │  ├─ group.json                          # averageCount=1, scanIds=[...]
   │  └─ R001/
   │     ├─ scan.json  raw.bin  dist.f64
   │     ├─ ps.f64  ps_db.f64
   │     ├─ events.json  summary.json
   │     └─ calibration.json
   ├─ GR-20261111-1530-002/                  # IN01_OUT02，N=3
   │  ├─ group.json                          # averageCount=3, avgMode=linear_ps
   │  ├─ R001/  R002/  R003/
   │  └─ AVG/                                # 平均结果（布局同 Rxxx，不含 raw.bin）
   └─ ... GR-...-064/                        # IN08_OUT08
```

**命名规则**

| 对象 | 格式 | 说明 |
|---|---|---|
| 检测数据根 | `DETECTION_DATA/` | 与 `GMIX_DATA/` 并列，互不写入 |
| 批次 | `BATCH-YYYYMMDD-HHMMSS-NNN` | UTC；月/日/时/分/秒顺序见下 |
| 组 | `GR-YYYYMMDD-HHMM-NNN` | 沿用 L2；`NNN` 在批次内递增，按路径顺序 001~064，不因分钟变化重置 |
| 采集 | `R001..R00N` | 沿用 L2；`repeatIndex = 1..N` |
| 平均 | `AVG/` | 沿用 L2；仅 N>1 生成 |
| 批次索引 | `index.csv` | 人读用；不作为权威，权威为 `detection.json` |

> 批次时间成分说明：`BATCH-YYYYMMDD-HHMMSS-NNN` 为 `年月日-时分秒-序号`，UTC。批次内 `groupId` 的 `HHMM` 取该组创建时刻，序号在批次内连续递增。
>
> **唯一性规则**：`groupId` 仅在批次内唯一，不保证跨批次全局唯一。跨批次引用某个组时必须同时带批次标识（形如 `(batchId, groupId)`），禁止仅凭 `groupId` 在跨批次场景定位。`batchId` 在数据根内唯一。

## 7. 验收标准

### 7.1 验收项

| 编号 | 验收项 | 通过条件 | 覆盖 FR |
|---|---|---|---|
| AC-001 | 完整批次 | `Completed`/`CompletedWithFailures` 批次中 `completed + measurementFailed = 64`；`Aborted` 批次按 `NotAttempted` 另行核对 | FR-070、FR-074 |
| AC-002 | 三项指标 | 连通性四值可实现；IL/RL 与 L2 `summary.json` 一致 | FR-040~FR-042、FR-045 |
| AC-003 | 未标定处理 | `configHash` 不一致时 IL/RL 为 `null` 且有 `calibration_invalid` | FR-036、FR-037 |
| AC-004 | 批次失败策略 | 单路径失败后继续，汇总计数正确 | FR-075、FR-076 |
| AC-005 | 失败重测 | 新批次 `retryOf` 指向原批次，原结果未被改写 | FR-077 |
| AC-006 | 存储契约与目录 | 目录层级符合 §6.6；`detection.json` schema 通过；`GMIX_DATA` 未被写入；L2 字段与 CSV 列序未变 | FR-095~FR-106 |
| AC-007 | 单位与符号 | 导出距离为米；RL 符号与 L2 一致，未被取负 | FR-042、FR-047 |
| AC-008 | 状态机 | 暂停、中止、异常退出、恢复各场景状态与计数自洽 | FR-073、FR-074、FR-078 |
| AC-009 | 故障注入 | 各类错误按 §4.14 分类上报并置对应提示 | FR-006、FR-027、FR-033 |
| AC-010 | 关注提示 | 八个标记各有触发用例，且均不改变测量值与批次结果 | FR-051、FR-052 |
| AC-011 | 基线生命周期 | 版本追加、多匹配确认、无基线继续、批次可回溯 | FR-060~FR-064 |
| AC-012 | 可追溯 | `configHash`、`ofdrConfigHash`、`baselineId/version`、路径↔组映射、操作员、设备 SN 均可查 | FR-079、FR-088、FR-099、FR-105 |
| AC-013 | 重复性 | 同一 DUT 连续 3 批，连通性一致；IL/RL 波动在基线容差内（容差见 TBD-D05a） | FR-040~FR-042 |
| AC-014 | 配置与资源 | 配置非法阻止开批；磁盘不足阻止开批；清理不影响基线 | FR-116、FR-123、FR-066 |
| AC-015 | 环境兼容 | 中文/空格路径下可构建、可运行、可写数据 | NFR-010 |
| AC-016 | 多采集平均 | N>1 时生成 `AVG/` 且指标取平均值；部分失败出 `avg_incomplete`；N=1 不生成 `AVG/` | FR-030、FR-038、FR-096 |

> AC-002 / AC-013 中的参考范围与容差依赖 TBD-D05a、TBD-D05b；在基线评审前，验收以“流程、口径与数据结构正确”为主，不锁定数值。

### 7.2 验收用例

| 用例 | 覆盖 AC | 前置条件 | 步骤 | 通过判据 |
|---|---|---|---|---|
| TC-01 | AC-001、AC-002、AC-010 | 三设备就绪、基线已导入、Mock 或真机 | 启动 → 自检 → 执行完整批次 | 64 条均有 `pathState`；矩阵与明细一致；无 Pass/Fail 结论 |
| TC-02 | AC-001、AC-004、AC-009、AC-010 | Mock 让第 5 条扫描失败 | 执行批次 | 批次继续；第 5 条 `Failed` + `measurement_failed`；状态 `CompletedWithFailures` |
| TC-03 | AC-009、NFR-005、NFR-014 | Mock 不出回包 / 超时 | 执行批次 | 按传输类上报；含原始 TX 与建议动作；单路径重试后继续 |
| TC-04 | AC-009 | Mock 返回乱码 / 缺 `>` | 执行批次 | 按协议类上报；不静默吞掉 |
| TC-05 | AC-003 | LGA50 `configHash` 与记录不一致 | 执行批次 | IL/RL 为 `null`；`calibrationStatus=mismatch`；`calibration_invalid`；批次不中断 |
| TC-06 | AC-011 | 未导入基线 | 执行批次 | 可执行；`baseline_missing`；不产生 `out_of_reference` |
| TC-07 | AC-011 | 同一 `dutModel` 两条基线 | 启动批次 | 要求人工确认；选择后记录 `baselineId`/`version` |
| TC-08 | AC-008、AC-014 | 批次执行到第 20 条 | 中止 | 状态 `Aborted`；已完成保留；其余 `NotAttempted`；计数合计 64；中止前有二次确认 |
| TC-09 | AC-008、NFR-003、NFR-004 | 批次执行到第 30 条 | 强制结束进程 → 重启 | 已完成组可读；可继续或标记不可续；无半写组目录 |
| TC-10 | AC-005 | 存在 3 条失败 | 触发重测 | 生成新批次；`retryOf` 正确；原批次结果字节级不变 |
| TC-11 | AC-007 | 已知长度光纤 / 参考测量 | 执行单路径并导出 | 距离单位为米；RL 符号与 L2 一致；峰位置与参考一致 |
| TC-12 | AC-006、AC-012 | 完成一个批次 | 校验目录与 JSON/CSV | 目录层级符合 §6.6；schema 通过；`GMIX_DATA` 未被写入；CSV 列与 §6.5 一致；UTC 时间戳 |
| TC-13 | AC-014、NFR-010 | 磁盘余量低于阈值 | 启动批次 | 预警或阻止开批；提示可读 |
| TC-14 | AC-009 | 运行中拔掉 8×8 串口 | 重连 | 恢复后可继续；重连后重新查询 OCS 状态 |
| TC-15 | FR-007、FR-028 | 已有一个实例 | 再次启动 | 被拒绝并提示 |
| TC-16 | FR-012、FR-013 | 构造缺路径/重复/越界/N=0 计划 | 导入 | 被拒绝并指出具体行 |
| TC-17 | FR-025 | 整矩阵命令含重复非 0 输出 | 执行 | 发送前被拒绝，不产生设备错误 |
| TC-18 | FR-024 | Mock 回读值被篡改 | 执行 | 判定为设备类错误并重试 |
| TC-19 | FR-032、FR-118、FR-116 | 运行中修改配置 | 尝试保存 | 被拒绝；不影响当前批次 |
| TC-20 | FR-040、FR-051 | 构造无峰帧 | 执行 | `connectivity=noPeak` + `no_peak`；不判失败 |
| TC-21 | FR-053、FR-086 | 完成批次 | 筛选标记 → 查看详情 → 加备注 | 筛选正确；详情含组与 scanId；备注含操作员与时间 |
| TC-22 | FR-065、FR-066、FR-120~FR-125 | 有历史批次与基线 | 执行清理 | 二次确认；整批删除；基线仍可用；软件无生成基线入口 |
| TC-23 | FR-072、NFR-001、NFR-002、NFR-012 | 完整批次 | 观察界面 | 进度 ≤1s 刷新；UI 不卡死；报告含分段耗时 |
| TC-24 | FR-106 | 旧版本批次 | 打开 | 可展示；字段缺失有明确提示 |
| TC-25 | FR-110~FR-112 | CNN 结果含异常路径 | 导入 | 追加 `cnn_suspicious`；原测量值不变；连通性不变 |
| TC-26 | AC-015 | 安装路径含中文与空格 | 构建 → 运行 → 写数据 | 全部通过 |
| TC-27 | AC-016、FR-029、FR-096 | 单路径计划 N=3 | 执行该路径 | 生成 `R001..R003` + `AVG/`；OCS 在 3 次扫描期间不变；指标取 `AVG/summary.json` |
| TC-28 | AC-016、FR-038 | N=3 时第 2 次扫描失败 | 执行该路径 | `validCount=2`；生成 `AVG/`；`status=incomplete`；`avg_incomplete` 标记 |
| TC-29 | AC-013 | 同一 DUT、同一基线、配置不变、三设备就绪 | 连续执行 3 个完整批次 | 3 批连通性取值完全一致；IL/RL 波动在 TBD-D05a 容差内（容差定稿前记录波动范围供基线评审） |

## 8. TBD 与风险

| 编号 | 事项 | 影响 | 关闭方式 |
|---|---|---|---|
| TBD-D05a | IL/RL 参考范围与展示口径 | 展示与提示 | golden DUT + 良品/不良品数据评审 |
| TBD-D05b | 峰位置期望窗口、`peak_unexpected` 规则 | 关注提示边界 | 实测曲线 + 评审 |
| TBD-D04 | 切换稳定时间、扫描超时、重试 | 批次节拍与稳定性 | OCS/OFDR 实测 |
| TBD-D03a | 1×8 与 8×8 光口物理映射 | 路径映射正确性 | 硬件接线表确认 |
| TBD-D09 | CNN 接入时机与输入输出 | 后期验证范围 | CNN 评估线输出稳定后 |
| TBD-D10 | LGA50-D 扫描触发、完成判定与超时语义 | 采集层实现 | 实机验证 |
| TBD-D11 | 性能目标：单路径耗时上限、批次时长目标、UI 响应指标 | 性能验收 | 实测后与产线确认 |
| TBD-D12 | 数据保留与清理的默认值（天数/批次数/磁盘阈值/日志上限） | 现场运维 | 产线评审 |
| TBD-O01~O10 | OCS 协议实测项 | 串口可靠性与错误处理 | 按 OCS 协议分析测试计划执行 |

**已关闭**

| 编号 | 事项 | 关闭结论 |
|---|---|---|
| TBD-D01 | OFDR 选型 | LGA50-D 单型 |
| TBD-D02 | OCS 控制接口与命令集 | RS232 + 命令集已明确；时序/错误码转 TBD-O01~O10 |
| TBD-D03 | 1×8 与 8×8 关系 | 1×8 前级 + 8×8 DUT，64 条单向路径 |
| TBD-D06a | 批次/组/scan 目录命名 | 见 §6.6 命名规则 |
| TBD-D07 | 软件形态 | 独立 `products/detection` 解决方案 |
| TBD-D08 | 基线管理策略 | 外部审批、版本追加、不覆盖 |

主要风险：

- 仅用 IL/RL 可能无法发现损耗特征相近的错接；峰位置窗口是缓解手段，但规则未定。
- 64 路径批次时长对切换/稳定/扫描时间敏感，时序未实测前无法承诺节拍。
- 8×8 单路命令是否强制输出唯一尚未实测，编排器需自行校验。
- LGA50-D 标定与 `configHash` 可读性未验证，直接影响 IL/RL 是否为绝对量。
- 串口断连、乱码、无回包是主要现场故障源。
- 基线缺失或版本错配会导致 `out_of_reference` 无法评估。
- N>1 时磁盘占用按倍数增长；保留策略默认值未定，现场可能因磁盘写满中断批次。
- 批次目录作为 L2 数据根的等价根属检测层新约定，需在 SDD 与 L2 注记中保持一致，避免两处描述漂移。

## 9. 追溯与变更

### 9.1 决策来源

| 决策 | 结论 | 来源 |
|---|---|---|
| D01 | LGA50-D 单型 | 2026-11-11 评审 |
| D03 | 1×8 前级 + 8×8 DUT，64 条单向路径 | 2026-11-11 评审 |
| D05 | 验收指标为连通性、IL、RL；软件只测量记录，不自动判定 | 2026-11-11 评审 |
| D07 | 独立 `products/detection` 解决方案 | 2026-11-11 评审 |
| 批次策略 | 测量失败继续、汇总、事后重测 | 2026-11-11 评审 |
| CNN | 后期离线验证，不改写测量值、不参与结果结论 | 既有 CNN 独立评估线裁定 |
| B1 | 批次目录等价于 L2 数据根 | 2026-11-11 数据组织讨论 |
| B2 | `groupId` 批次内唯一；跨批引用用 `(batchId, groupId)` | 2026-11-11 数据组织讨论 |
| B3 | 清理以批次为单位整批删除，禁止半删 | 2026-11-11 数据组织讨论 |
| B4 | 单路径 N 次扫描期间 OCS 保持不变 | 2026-11-11 数据组织讨论 |
| B5 | 重测生成新批次，`retryOf` 关联 | 2026-11-11 数据组织讨论 |
| B6 | 数据根使用 `DETECTION_DATA/`，与 `GMIX_DATA/` 并列 | 2026-11-11 数据组织讨论 |

### 9.2 需求来源映射

| FR 区间 | 来源 |
|---|---|
| FR-001~FR-007 | 设备接入惯例（LGA50 现有能力）+ D01；单实例与错误分类为审查补充 |
| FR-010~FR-014 | D03（64 路径）+ 路径计划可追溯要求 + 每路径扫描次数 N |
| FR-020~FR-029 | [OCS 控制协议分析](OCS_控制协议分析.md) 与规则 30；FR-029 来自 B4 |
| FR-030~FR-038 | L1/L2 契约、L2 组平均规则、L2 `configHash` 失效裁定、D01 |
| FR-040~FR-047 | L1 C-01/C-08/G-01、L2 §4/§5 口径裁定、D05 |
| FR-050~FR-054 | D05（只测量记录）+ 审查补充（提示枚举化） |
| FR-060~FR-066 | D08（基线管理）+ 审查补充（外部审批、版本追加） |
| FR-070~FR-080 | 批次策略决策 + B5 + 审查补充（状态机） |
| FR-085~FR-090 | 结果展示与导出惯例 + 审查补充（报告内容） |
| FR-095~FR-106 | L1/L2 契约、B1~B3、B6（目录层级与映射） |
| FR-110~FR-112 | CNN 独立评估线裁定 + D09 |
| FR-115~FR-118 | 现场运维需求 + 审查补充 |
| FR-120~FR-125 | B3（整批清理）+ 审查补充 |
| NFR-001~NFR-015 | 工程约束（规则 10/20）+ 审查补充 |

### 9.3 需求与 TBD 映射

| TBD | 受影响 FR |
|---|---|
| TBD-D05a | FR-046、FR-051、AC-013 |
| TBD-D05b | FR-043、FR-051、AC-010 |
| TBD-D04 | FR-026、FR-033、FR-072 |
| TBD-D03a | FR-011、FR-021 |
| TBD-D09 | FR-110~FR-112 |
| TBD-D10 | FR-030、FR-033、FR-035 |
| TBD-D11 | NFR-001、NFR-002 |
| TBD-D12 | FR-121、FR-123、FR-124 |
| TBD-O01~O10 | FR-020~FR-028 |

### 9.4 变更记录

| 版本 | 日期 | 变更 |
|---|---|---|
| v0.1 | 2026-11-11 | 初稿：范围、64 路径、功能/非功能需求、验收、TBD |
| v0.1-r1 | 2026-11-11 | 移除自动 Pass/Fail，改为测量、记录与关注提示 |
| v0.2 | 2026-11-11 | 修 P0：IL/RL 口径对齐 L2、距离单位统一为米、连通性四值定义、关注提示枚举化、批次状态机、基线生命周期、LGA50 标定校验；收紧 `detection.json` schema、引用 L1 `ScanId`、指标复用 `MetricsAggregator` |
| v0.3 | 2026-11-11 | 修 P1/P2：检测 CSV 列、验收用例 TC-01~TC-26、NFR 可测指标、配置管理、数据维护、错误分类、批次↔组↔scan 映射、报告内容、提示呈现、来源映射 |
| v0.4 | 2026-11-11 | 按实际检测流程重定数据组织：**一条路径 = 一个 L2 组**（N 次扫描 = 组内 `Rxxx`，N>1 生成 `AVG/`）；批次 = 64 个组的批次目录，数据根改为 `DETECTION_DATA/`；新增 §6.6 目录结构与命名规则；关闭 TBD-D06a；`detection.json` 增加 `groupId`/`averageCount`/`validCount`/`avgScanId`/`scanIds`；新增 FR-029/038/096/102~105、FR-120~125、AC-016 与 TC-27/28 |
| v0.4-r1 | 2026-11-11 | 落盘前终审修复：① 术语表 Group 定义去除 `GMIX_DATA` 引用，与 `DETECTION_DATA` 数据根对齐；② 补 AC-013 验收用例 TC-29（重复性）。P2 七项登记至 `docs/detection/README.md` SDD 待明确项 |
