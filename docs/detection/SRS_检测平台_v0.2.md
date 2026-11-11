# 检测平台上位机 SRS v0.2

> **文档编号**：SRS-DET-001
> **版本**：v0.2（评审稿，替代 v0.1）
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

### 1.3 首版范围（v0.2）

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
| 数据存储 | 复用 L2 `group.json` / `scan.json`；新增 `detection.json` 旁车文件 |
| 软件形态 | 独立 `products/detection` 解决方案，引用共享 `Gmix.Core` / `Gmix.Vendor` |

### 1.4 非目标（v0.2）

- 不做 OCS 固件、光学设计、采集板或工装开发。
- 不测反向 `OUT→IN` 路径；反向作为后续版本扩展。
- 不接入 GM-IX 整机；首版只接 LGA50-D。
- CNN 指纹不改写测量值、不生成结论，不引入 Python 运行时到产品进程。
- 不做多工位并行、多 DUT 同时检测、远程/云端。
- 不在 v0.2 输出自动 Pass/Fail 或合格结论；自动判定规则与阈值后续版本再定。
- 不生成、不修改、不审批基线；软件只导入与校验基线。
- 不在未实测前写死切换稳定时间、超时、重试次数或 IL/RL 参考范围。

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
- 64 条路径可通过 IL/RL 与峰位置证据区分；若硬件无法区分，需补充物理标记或已知长度（见 TBD-D03a）。
- 基线/参考数据由外部（硬件/工艺团队）提供并审批；软件只导入与校验。
- LGA50-D 的标定与 `configHash` 可读取；无法读取时按“未标定”处理。

## 3. 术语

| 术语 | 定义 |
|---|---|
| Path（路径） | 一条 `IN i → OUT j` 的 8×8 光路，共 64 条 |
| Batch（批次） | 一次检测运行，包含 1~64 条路径的执行记录 |
| Baseline（基线） | 外部提供、经审批发布的参考范围文件，带 `baselineId` 与版本 |
| Connectivity（连通性） | 见 §4.5.1 的可观察定义，取值 `ok / noPeak / invalidFrame / notComputed` |
| IL | 插入损耗，单位 dB，口径见 §4.5.2 |
| RL | 回波损耗，单位 dB，符号口径见 §4.5.3 |
| 关注提示（Attention flag） | 见 §4.6 的枚举，只提示不结论 |
| OCS State | 1×8 当前路由 + 8×8 当前矩阵状态 |
| 未标定 | LGA50 标定缺失或 `configHash` 不一致，此时不得输出绝对 IL/RL |

## 4. 功能需求

优先级：**Must** = 首版必须；**Should** = 首版尽量；**Could** = 后续版本。

### 4.1 设备连接与自检

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-001 | 连接 LGA50-D，显示设备信息与连接状态 | Must | 断开/重连后状态正确 |
| FR-002 | 连接 1×8 OCS 串口，读取 `IDN?` / `SN?` | Must | 返回型号、SN 与实物一致 |
| FR-003 | 连接 8×8 OCS 串口，读取 `IDN?` / `SN?` | Must | 返回型号、SN 与实物一致 |
| FR-004 | 启动自检：串口可打开、IDN 匹配、状态可查询、扫描就绪 | Must | 自检失败给出可读原因并阻止开批 |
| FR-005 | 支持运行中拔插/重连，不要求重启软件 | Must | 重连后状态恢复；重连后必须重新查询 OCS 状态，不使用缓存状态 |
| FR-006 | 错误分类：传输 / 协议 / 设备 / 测量 / 标定 | Must | 不同错误显示不同消息 |
| FR-007 | 单实例运行：同一工位同时只允许一个软件实例占用串口与批次 | Must | 第二次启动被拒绝并提示 |

### 4.2 路径计划

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-010 | 生成 64 条路径计划，路径 ID 为 `IN01_OUT01` ~ `IN08_OUT08` | Must | 数量与 ID 唯一性校验 |
| FR-011 | 路径计划包含 1×8 路由、8×8 IN/OUT、期望 DUT 端口 | Must | 计划完整性校验 |
| FR-012 | 校验路径计划无缺失、无重复、参数越界 | Must | 非法计划被拒绝 |
| FR-013 | 支持路径计划导入/导出 | Should | 导入后与源计划一致 |
| FR-014 | 路径计划带版本号，并纳入 `configHash` | Must | 批次记录可追溯计划版本 |

### 4.3 OCS 控制

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-020 | 设置 1×8 路由（`COM → CHn`） | Must | 回读一致 |
| FR-021 | 设置 8×8 单路（`IN i → OUT j`） | Must | 回读一致 |
| FR-022 | 支持 8×8 整矩阵设置（`SETSWabcdefgh`） | Should | 64 路径批次可选用该策略 |
| FR-023 | 查询 1×8 当前路由与 8×8 当前矩阵 | Must | 查询值与设置值一致 |
| FR-024 | 每次设置后回读确认，再进入扫描 | Must | 不一致时按设备错误处理 |
| FR-025 | 整矩阵设置前校验非 0 输出唯一性 | Must | 冲突计划在发送前被拒绝 |
| FR-026 | 串口超时、重试次数、退避可配置 | Must | 配置生效；具体值见 TBD-D04 |
| FR-027 | 映射 `<ERROR>`、`<ER_SetValue>` 与串口异常 | Must | 错误分类正确 |
| FR-028 | 串口独占：同一时刻只有一个进程/线程访问 | Must | 并发打开被拒绝 |

### 4.4 扫描采集与标定校验

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-030 | 按路径触发 LGA50-D 扫描 | Must | 每条路径产生一次扫描 |
| FR-031 | 接收并保存距离域曲线；以 `ps_db` 为唯一绘制数据源，`ps` 作为原始证据保留 | Must | 曲线可回放、可导出；不重复做 `20log10` |
| FR-032 | 扫描参数来自配置快照，不在运行中热改 | Must | 批次内参数一致 |
| FR-033 | 扫描超时、失败重扫策略可配置 | Must | 具体值见 TBD-D04 |
| FR-034 | 单路径扫描失败不终止整批 | Must | 批次继续，失败被记录 |
| FR-035 | 扫描结果与批次、路径、OCS 状态关联 | Must | `detection.json` 可追溯 |
| FR-036 | 批次开始前读取 LGA50 标定与 `configHash`，并记录到批次元数据 | Must | 批次元数据含 `ofdrConfigHash`、`calibrationStatus` |
| FR-037 | `configHash` 不一致或标定缺失时：不输出绝对 IL/RL（置 `null`），写 warning 并置 `calibration_invalid` 关注标记；不阻断批次 | Must | 构造不一致场景，验证 IL/RL 为 `null` 且有提示 |

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
| FR-040 | 计算每条路径连通性，取值按 §4.5.1 四值枚举 | Must | 四条分支各有用例 |
| FR-041 | 展示每条路径 IL，口径按 §4.5.2 | Must | 与 L2 `summary.json` 一致 |
| FR-042 | 展示每条路径 RL，符号按 §4.5.3 | Must | 与 L2 一致，未取负号 |
| FR-043 | 计算峰位置/峰值存在性作为辅助证据；零距峰按 L2 规则过滤（位置 `< analysisRange.startM` 视为 `zeroPeak`，不参与峰编号） | Must | 与 L2 `events.json` 一致 |
| FR-044 | 指标与证据随路径结果持久化 | Must | 重开批次可查看 |
| FR-045 | 指标单位、口径、有效位数与 L2 一致 | Must | 文档评审 + 数值比对 |
| FR-046 | IL/RL 参考范围来自基线，不硬编码；仅用于展示与提示 | Must | 修改基线后提示随之变化 |
| FR-047 | 距离轴、峰位置在存储、计算、导出中一律用**米**；UI 可格式化为 mm，但不得回流数据 | Must | 契约检查 + 导出文件校验 |

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
| `cnn_suspicious` | CNN 离线验证标记异常（后期） | 提示 |

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-050 | 展示每条路径的连通性、IL、RL 测量值，不输出 `Pass/Fail` | Must | 测量值与明细一致 |
| FR-051 | 按上表生成关注提示，同一路径可同时存在多个标记 | Must | 每个标记有独立用例 |
| FR-052 | 关注提示不拦截批次、不改变测量值 | Must | 带提示批次仍完整执行 64 条 |
| FR-053 | 关注提示可在矩阵视图筛选，可在详情中查看触发原因与相关数值 | Should | UI 走查 |
| FR-054 | 人工可追加备注/人工关注标记，记录操作员与时间 | Should | 审计记录完整 |

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
| FR-060 | 导入 `baseline.json` 并校验 schema、必填字段与版本递增 | Must | 非法文件被拒绝且给出原因 |
| FR-061 | 基线版本追加，不覆盖历史版本；历史批次仍能解析其引用版本 | Must | 导入新版本后旧批次可正常打开 |
| FR-062 | 按 `dutModel` + 可选 `dutSerial` 选择基线；匹配到多条时要求人工确认 | Must | 多匹配场景有提示 |
| FR-063 | 无匹配基线时允许测量，置 `baseline_missing`，不评估 `out_of_reference` | Must | 无基线批次可执行且标注清楚 |
| FR-064 | 批次记录 `baselineId` 与 `version`；结果可回溯到当时基线 | Must | 追溯检查通过 |
| FR-065 | 软件不生成、不修改、不审批基线；不提供“从测量结果自动生成基线”功能 | Must | 功能审查 |
| FR-066 | 基线与测量数据的存储位置分离，基线不随批次删除 | Must | 清理批次后基线仍在 |

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
| FR-070 | 以路径计划启动批次，`total` 恒为 64 | Must | 任意批次 `total=64` |
| FR-071 | 默认按路径顺序串行执行 | Must | 不出现并发切换/扫描 |
| FR-072 | 显示当前路径、已完成数、总数、预计剩余时间 | Must | 进度与实际一致 |
| FR-073 | 支持暂停/继续；暂停后不再发送新命令，正在进行的单路径完成后停 | Should | 暂停期间无 TX 日志 |
| FR-074 | 支持中止；已完成结果保留，未执行路径记为 `NotAttempted`，批次状态为 `Aborted` | Must | 中止后计数满足 `completed + measurementFailed + notAttempted = 64` |
| FR-075 | 单路径测量失败继续执行后续路径 | Must | 失败路径记录，批次不中断 |
| FR-076 | 批次结束状态为 `Completed` 或 `CompletedWithFailures`；存在 `Failed` 路径时必须为后者 | Must | 状态与计数一致 |
| FR-077 | 支持仅重测失败/未完成路径：生成**新批次**，`retryOf` 指向原批次，原批次只追加 `retestBatchIds`，不改写原结果 | Must | 两批次可双向追溯，原结果字节级不变 |
| FR-078 | 异常退出后可恢复未完成批次：可继续执行剩余路径，或标记为不可续 | Should | 重启场景验证；恢复后使用 `NotAttempted` 集合 |
| FR-079 | 记录操作员、工位、设备 SN、`ofdrConfigHash`、开始/结束时间（UTC） | Must | 批次元数据完整 |
| FR-080 | 中止、重测、复位等破坏性操作执行前需二次确认 | Must | UI 走查 |

> AC-001 的“64 路径完整执行”只适用于状态为 `Completed` / `CompletedWithFailures` 的批次；`Aborted` 批次按 `NotAttempted` 计数验收。

### 4.9 结果、报告与导出

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-085 | 以 8×8 矩阵展示 64 条路径测量值与关注提示 | Must | 单元格状态与明细一致 |
| FR-086 | 单路径详情：连通性、IL、RL、峰证据、关注提示、OCS 状态、标定状态、耗时、重试 | Must | 任意路径可查看 |
| FR-087 | 导出 CSV/JSON；检测 CSV 列定义在 SDD 确定，且不得改变 L2 既有 CSV 列序 | Must | 导出内容与界面一致；L2 CSV 回归通过 |
| FR-088 | 导出包含批次元数据、`baselineId`/`version`、`configHash`、`ofdrConfigHash` | Must | 可离线追溯 |
| FR-089 | 原始曲线保留策略可配置，并支持手动立即清理 | Should | 按策略清理，不误删结果 |
| FR-090 | 生成可打印报告（含批次摘要、矩阵、异常清单） | Could | 报告字段完整 |

### 4.10 存储与契约

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-095 | 复用 L2 `group.json` / `scan.json`，不改变既有字段语义与 CSV 列序 | Must | 旧工具可读取 |
| FR-096 | 新增 `detection.json` 旁车文件，字段与类型见 §6.2 | Must | schema 校验通过 |
| FR-097 | 旁车文件带 `schemaVersion` | Must | 版本可识别 |
| FR-098 | 批次记录 `configHash` 与 `ofdrConfigHash` | Must | 配置变化可追溯 |
| FR-099 | `scanId` 直接采用 L1 `G-01` 格式，不另行定义 | Must | 正则校验通过 |
| FR-100 | 每路径完成后原子写入，异常退出不产生半写文件 | Must | 强制结束场景验证 |
| FR-101 | 能读取旧批次并展示结果 | Should | 历史批次可打开 |

### 4.11 CNN 指纹验证（后期）

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| FR-105 | 导出 CNN 离线验证所需的数据包 | Should | 数据包可被 CNN 分支读取 |
| FR-106 | 导入 `cnn_verification.json`，追加 `cnn_suspicious` 标记 | Could | 标记可追溯，不覆盖原测量记录 |
| FR-107 | CNN 结果不得改写测量值、不得生成合格结论、不得影响连通性取值 | Must | 审计记录保留原始测量值 |

## 5. 非功能需求

| 编号 | 需求 | 优先级 | 验收方法 |
|---|---|---|---|
| NFR-001 | 批次报告必须包含总耗时与分段耗时（切换/稳定/扫描/计算） | Must | 报告字段检查 |
| NFR-002 | UI 进度刷新不超过 1 秒 | Should | 操作观察 |
| NFR-003 | 每路径完成后原子落盘；强制结束后已完成路径可读 | Must | 强制结束场景验证 |
| NFR-004 | 恢复未完成批次时，未执行路径集合以 `NotAttempted` 为准 | Should | 重启场景验证 |
| NFR-005 | 记录原始 TX/RX、命令序号、每路径耗时；日志可按批次定位 | Must | 日志可复现问题 |
| NFR-006 | 依赖方向：App 不引用 Vendor；Vendor 类型只出现在 Vendor/或检测 Vendor 层 | Must | 依赖检查 |
| NFR-007 | 默认流程无需命令行；破坏性操作有二次确认 | Should | 可用性走查 |
| NFR-008 | 不自动执行 `<RST>`；复位必须显式触发 | Must | 代码审查 + 操作验证 |
| NFR-009 | 本地运行，不依赖云服务与外部凭据 | Should | 离线可用 |
| NFR-010 | 运行于 Windows + .NET 8；支持含中文与空格的安装路径 | Must | 构建与运行验证 |
| NFR-011 | 配置、批次 ID、日志时间戳统一使用 UTC 存储，UI 按本地时区显示 | Must | 数据校验 |

## 6. 数据与契约

### 6.1 契约边界

- **L1**：曲线使用 `CurveScan`（轴 + dB 数组 + 元数据）；横轴恒为距离、单位恒为米（C-08）；`ScanId` 按 G-01；不得有损落盘（D-02）。
- **L2**：`group.json` / `scan.json` / `summary.json` 为测量记录与指标口径的权威来源；配置变化时绝对 IL/RL 置 `null` 并写 warning。
- **检测层新增**：仅 `detection.json`，不改变 L1/L2 字段语义。

### 6.2 `detection.json` schema

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | 本文件版本 |
| `batchId` | string | 是 | 批次唯一标识 |
| `retryOf` | string/null | 否 | 重测批次指向的原批次（§4.8 FR-077） |
| `retestBatchIds` | string[] | 否 | 原批次追加的重测批次列表 |
| `batchState` | enum | 是 | `Completed / CompletedWithFailures / Aborted` |
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
| `ocsState` | object | 否 | 1×8 路由 + 8×8 矩阵快照 |
| `scanId` | string/null | 否 | L1 G-01 格式；`NotAttempted` 为 `null` |
| `connectivity` | enum | 是 | `ok / noPeak / invalidFrame / notComputed` |
| `ilDb` | double/null | 否 | 口径见 §4.5.2；未标定或不可算为 `null` |
| `rlDb` | double/null | 否 | 符号见 §4.5.3 |
| `metricSource` | string | 否 | 引用 L2 `summary.json` 的定位信息 |
| `evidence` | object | 否 | `peakPresent`、`peakPositionM`（米）、`peakNo`、`zeroPeakExcluded` |
| `flags[]` | enum[] | 否 | §4.6 标记 |
| `notes[]` | object[] | 否 | 人工备注：文本、操作员、时间 |
| `attempt` | int | 是 | 本批次内第几次尝试，从 1 起 |
| `durationMs` | int | 否 | 该路径总耗时 |

### 6.3 配置快照

必须纳入 `configHash` 的字段：

- OFDR 扫描参数（量程、分辨率、平均次数等）
- OCS 串口、波特率、超时、重试
- 64 路径计划及版本
- 基线引用（`baselineId` + `version`，不含参考范围数值本身）
- 数据保留策略

`ofdrConfigHash` 单独记录，用于判定 IL/RL 是否为绝对量。

### 6.4 单位与口径

| 量 | 存储/计算/导出 | UI |
|---|---|---|
| 距离、峰位置 | 米（m） | 可显示 mm |
| IL、RL | dB | dB |
| 时间戳 | UTC ISO 8601 | 本地时区 |
| 功率域 | 沿用 L1 C-01 | 同左 |

单位换算只发生一次；UI 格式化不得回流数据。

## 7. 验收标准

| 编号 | 验收项 | 通过条件 |
|---|---|---|
| AC-001 | 完整批次 | `Completed` / `CompletedWithFailures` 批次中 `completed + measurementFailed = 64`；`Aborted` 批次按 `NotAttempted` 另行核对 |
| AC-002 | 三项指标 | 连通性四值可实现；IL/RL 与 L2 `summary.json` 一致 |
| AC-003 | 未标定处理 | `configHash` 不一致时 IL/RL 为 `null` 且有 `calibration_invalid` |
| AC-004 | 批次失败策略 | 单路径失败后继续，汇总计数正确 |
| AC-005 | 失败重测 | 新批次 `retryOf` 指向原批次，原结果未被改写 |
| AC-006 | 存储契约 | `detection.json` schema 校验通过；`group.json`/`scan.json` 与既有 CSV 列序未被破坏 |
| AC-007 | 单位与符号 | 导出距离为米；RL 符号与 L2 一致，未被取负 |
| AC-008 | 状态机 | 暂停、中止、异常退出、恢复各场景状态与计数自洽 |
| AC-009 | 故障注入 | OCS 超时、乱码、断连、扫描失败、标定失效均被分类上报并置对应提示 |
| AC-010 | 关注提示 | 七个标记各有触发用例，且均不改变测量值与批次结果 |
| AC-011 | 基线生命周期 | 版本追加、多匹配确认、无基线继续、批次可回溯 |
| AC-012 | 可追溯 | `configHash`、`ofdrConfigHash`、`baselineId/version`、操作员、设备 SN 均可查 |
| AC-013 | 重复性 | 同一 DUT 连续 3 批，连通性一致；IL/RL 波动在基线容差内（容差见 TBD-D05a） |

> AC-002 / AC-013 中的参考范围与容差依赖 TBD-D05a、TBD-D05b；在基线评审前，验收以“流程、口径与数据结构正确”为主，不锁定数值。

## 8. TBD 与风险

| 编号 | 事项 | 影响 | 关闭方式 |
|---|---|---|---|
| TBD-D05a | IL/RL 参考范围与展示口径 | 展示与提示 | golden DUT + 良品/不良品数据评审 |
| TBD-D05b | 峰位置期望窗口、`peak_unexpected` 规则 | 关注提示边界 | 实测曲线 + 评审 |
| TBD-D04 | 切换稳定时间、扫描超时、重试 | 批次节拍与稳定性 | OCS/OFDR 实测 |
| TBD-D03a | 1×8 与 8×8 光口物理映射 | 路径映射正确性 | 硬件接线表确认 |
| TBD-D06a | 64 路径存储目录与 `scanId` 映射 | 可追溯性 | SDD 数据模型评审 |
| TBD-D09 | CNN 接入时机与输入输出 | 后期验证范围 | CNN 评估线输出稳定后 |
| TBD-D10 | LGA50-D 扫描触发、完成判定与超时语义 | 采集层实现 | 实机验证 |
| TBD-O01~O10 | OCS 协议实测项 | 串口可靠性与错误处理 | 按 OCS 协议分析测试计划执行 |

主要风险：

- 仅用 IL/RL 可能无法发现损耗特征相近的错接；峰位置窗口是缓解手段，但规则未定。
- 64 路径批次时长对切换/稳定/扫描时间敏感，时序未实测前无法承诺节拍。
- 8×8 单路命令是否强制输出唯一尚未实测，编排器需自行校验。
- LGA50-D 标定与 `configHash` 可读性未验证，直接影响 IL/RL 是否为绝对量。
- 串口断连、乱码、无回包是主要现场故障源。
- 基线缺失或版本错配会导致 `out_of_reference` 无法评估。

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

### 9.2 需求与 TBD 映射

| TBD | 受影响 FR |
|---|---|
| TBD-D05a | FR-046、FR-051、AC-013 |
| TBD-D05b | FR-043、FR-051、AC-010 |
| TBD-D04 | FR-026、FR-033、FR-072 |
| TBD-D03a | FR-011、FR-021 |
| TBD-D06a | FR-095、FR-099 |
| TBD-D09 | FR-105~FR-107 |
| TBD-D10 | FR-030、FR-033、FR-035 |
| TBD-O01~O10 | FR-020~FR-028 |

### 9.3 变更记录

| 版本 | 日期 | 变更 |
|---|---|---|
| v0.1 | 2026-11-11 | 初稿：范围、64 路径、功能/非功能需求、验收、TBD |
| v0.1-r1 | 2026-11-11 | 移除自动 Pass/Fail，改为测量、记录与关注提示 |
| v0.2 | 2026-11-11 | 审查后修 P0：① IL/RL 口径对齐 L2（权威来源、未标定 null、RL 符号）；② 距离单位统一为米；③ 连通性改为四值可观察定义；④ 关注提示枚举化并定义触发条件；⑤ 批次状态机与汇总/重测/中止语义；⑥ 基线生命周期与 `baseline.json`；⑦ 新增 LGA50 标定与 `configHash` 校验。同时收紧 `detection.json` schema、引用 L1 `ScanId`、明确指标复用 `MetricsAggregator`、补单实例与 UTC 时间戳 |
