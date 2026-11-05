# GM-IX 数据与处理规范 v1.0

> **定位：规范即契约。**
> 本文档是 GM-IX 上位机各模块协作的唯一契约：采集进程、处理编排、校准、UI、报表、测试互不感知彼此实现，只通过本契约交互。
> Part A 是**数据契约**（格式与字段的唯一事实源）；Part B 是**处理与生命周期契约**（行为、边界与错误）。
> 规范承载测量语义，但不产生正确性：未确认的口径一律登记 TBD（Part A §8），禁止臆测值写死。

## 0. 契约视图（总览）

§0 是契约的索引与纪律；契约正文在 Part A / Part B。§0 摘要与正文冲突时，以正文为准。

### 0.1 契约边界

规范在四个边界上钉住各模块的协作方式：

| 边界 | 契约内容 | 承载章节 | 违反后果 |
|---|---|---|---|
| 进程边界：采集进程 ⇄ 编排/UI | staging 写入路径、提交时序、单写者 | Part B §7 | 数据竞争、组丢失/重复 |
| 厂商边界：SDK 黑盒 ⇄ 自有数据 | 字段映射、`N/2+1` 长度口径、明确拒绝路径 | Part A §3/§4、Part B §3/§4 | 垃圾数据入库、静默错误 |
| 数据边界：落盘 ⇄ 长期读取 | `schemaVersion`、兼容规则、字段语义冻结 | Part A §1 | 旧数据不可读 |
| 协作边界：模块 ⇄ 模块 | 字段锚定（校准/精度/审计/UI/测试共用一套定义） | Part A §2–§7 | 一处变更、多处失配 |

### 0.2 角色与义务

| 角色 | 承担者 | 义务 | 禁止 |
|---|---|---|---|
| 生产者 | 采集进程（独立运行、可强杀） | 只写 `_staging/<groupId>/`（Part B §7） | 执行最终提交；写 `GMIX_DATA/` 其它位置 |
| 处理者 | 编排进程（唯一写者） | 校验 → 纯函数处理 → 原子提交；错误按 Part B §4 分类上报 | 跳过校验直接提交；`catch {}` 吞错 |
| 消费者 | WPF UI、报表、检索 | 只读字段（`scanId`/`groupId` 等）；显示状态按 `view.json` | 解析 ID 字符串；做单位换算；直读厂商类型 |
| 仲裁 | `SchemaValidator` | 提交与回读双重校验（长度/单调性/计数） | 放宽任何一项校验 |

### 0.3 不变量（枢轴纪律）

以下各条散布于正文，是契约的硬约束。违反任一条即为**破坏性变更**，必须升主版本并走 §0.4 纪律，禁止就地改语义：

1. `ofdr` 输出长度恒为 `N/2 + 1`（Part B §3.3）；
2. `scanId`/`groupId` 只读字段，业务逻辑不解析 ID 字符串（Part A §2.1）；
3. 数值缺失用 `null`，禁止 `NaN`（Part A §1）；
4. 单位换算各只发生一次：int16→double 在 Conversion，`20log10` 只在 detect 出口（Part B §3.2/§3.4）；
5. 厂商类型、指针、句柄不越出处理层（Part B §3.3、§5）；
6. 平均只在线性 `ps` 域，且只对平均结果做一次 `detect`（Part A §2.2）；
7. 精度口径组内统一（`precisionBasis`），样本标准差分母 `N-1`（Part A §2.2/§5.1）；
8. dB 基线统计只使用 `baselineDb < 0` 的点（Part A §4）；
9. 指标计算是纯函数：只依赖输入数据、配置快照与校准值，不做 IO（Part B §5）；
10. 组提交：先 `_staging` 后原子重命名，单写者，目标已存在即拒绝（Part B §7）；
11. `configHash` 变化即旧校准失效（Part A §1、Part B §6）。

### 0.4 契约变更纪律

- 字段/格式变更：升 `schemaVersion`（Part A §1 规则），同步 `SchemaValidator` 与 `tests/fixtures/`，回归全绿后合入；
- 行为/边界/错误变更：同步 Part B §4 错误契约与 §9 验收清单；按团队工作流回填 SRS/SDD；
- 每次变更必须回答三个问题：**谁生产、谁消费、触碰哪条不变量**；
- 触碰 §0.3 不变量 = 破坏性变更：升主版本 + 全量数据迁移评估，禁止静默修改。

---

# Part A：数据契约（Schema v1.0，含平均组）


> 契约地位：Part A 承载**数据边界**与**协作边界**的契约（见 §0.1）；字段定义被校准/精度/审计/UI/测试共同锚定。
> 目的：在 v0.2 基础上增加测量组、重复采集与平均结果的组织方式，并保持单次测量可兼容。
> 关系：v1.0 **向后兼容** v0.2/v0.1；旧数据缺少组字段时，按“单次组 N=1”处理。
> 版本：文档 v1.0；新数据 JSON `schemaVersion = "1.0"`；读取兼容 `"0.2"` / `"0.1"`。
> 关联：[Schema v0.1](./上位机数据Schema_v0.1.md)、[上位机数据协议调研与讨论](./上位机数据协议调研与讨论.md)
> 基础字段：device/acquisition/quality/checksums/ofdr/peaks 等本文未重复列出的字段，以 [Schema v0.2 字段表](./archive/上位机数据Schema_v0.2.md) 为准；本文只定义 v1.0 变更与新增。

---

## 1. 版本与兼容规则

| 项 | 规则 |
|---|---|
| 向后兼容 | v0.2/v0.1 的 `scan.json/ofdr.json/events.json` 可直接读取 |
| 组扩展 | v1.0 新增 `groupId/repeatIndex/isAverage/averageOf/avgMode`；旧数据缺失时按 `averageCount=1` 处理 |
| 新增字段 | 除组字段外，其余新增字段可选；缺失表示“不支持/未启用” |
| 语义变更 | 禁止在 v1.0 内改已有字段单位或含义；需变更时升 `v1.1` |
| 版本号 | 不兼容变更（旧数据读取失败或字段语义改变）升主版本；仅新增可选字段升次版本；v1.0 为首个稳定基线命名 |
| 配置失效 | 采样率、AFE、量程、通道映射变化后，旧 `calibrationId` 失效 |
| 数值缺失 | JSON 用 `null`，禁止 `NaN` |

---

## 2. 文件布局（v1.0，含平均组）

```text
GMIX_DATA/                     # 数据根目录
├─ _staging/                   # 暂存区：未完成采集/处理，启动时清理
│  └─ <groupId>/               # 按组暂存，失败不进入最终目录
├─ _calibration/               # 全局校准库
│  └─ <calibrationId>/         # 一个校准版本
│     └─ calibration.json      # 校准参数、标定件、校验点
└─ <groupId>/                  # 一次测量组，N=1 与 N>1 结构一致
   ├─ group.json               # 组元数据：平均次数、scanIds、设置、校准
   ├─ R001/                    # 第 1 次采集（完整 scan 证据包）
   │  ├─ scan.json             # 采集元数据：设备、参数、质量、会话
   │  ├─ raw.bin               # 原始数据：int16 小端、4 通道交织
   │  ├─ ofdr.json             # 处理元数据：fs、distScale、N、输出长度
   │  ├─ dist.f64              # 距离轴 double[]，长度 N/2+1
   │  ├─ freq.f64              # 频率轴 double[]，长度 N/2+1
   │  ├─ ps.f64                # 功率谱 double[]，线性幅值
   │  ├─ baseline.f64          # 可选：基线 double[]，长度 N/2+1
   │  ├─ events.json           # 峰/段/事件与相对 IL/RL
   │  ├─ summary.json          # 总 IL/RL、精度、分析区间
   │  └─ calibration.json      # 本次实际使用的校准快照
   ├─ R002/ ... R00N/          # N>1 时才有
   └─ AVG/                     # 仅 N>1 时生成；布局与 R001 相同，但不含 raw.bin
```

### 2.1 组、扫描与平均结果

- **一个 `<groupId>` 目录 = 一次测量组**；N=1 时只含 `group.json + R001/`。
- **一个 `Rxxx/` 目录 = 一次采集**；组内序号 `repeatIndex = 1..N`，不跨组复用。
- **`AVG/` = 平均结果**；仅 `averageCount > 1` 时生成，N=1 不生成，避免重复存相同数据。
- `groupId` 负责组级配置与清理；`scanId` 负责单次证据与审计；`avgScanId` 指向平均结果。
- 命名格式：**两个字母前缀 + 时间 + 3 位自然数序号**：
  ```text
  groupId   = GR-YYYYMMDD-HHMM-NNN
  scanId    = SC-YYYYMMDD-HHMMSS-NNN
  avgScanId = AV-YYYYMMDD-HHMMSS-NNN
  ```
  示例：
  ```text
  groupId   = GR-20261105-1530-001
  scanId    = SC-20261105-153022-001
  avgScanId = AV-20261105-153032-001
  ```
- 前缀含义：`GR`=组，`SC`=单次采集，`AV`=平均结果。
- 序号范围 `001–999`：
  - `GR` 序号按**分钟**内递增；
  - `SC`/`AV` 序号按**秒**内递增；
  - 同一时间桶超过 999 条时阻塞/排队，不使用 4 位序号。
- `groupId` 精确到分钟，适合按分钟聚合；`scanId`/`avgScanId` 精确到秒，避免同分钟多次采集冲突。
- `scanId` 全局唯一；业务逻辑只读字段，不解析字符串。
- ID 中的日期时间为**本地时间**（部署时区，默认 Asia/Shanghai）；`createdAtUtc`/`capturedAtUtc` 等 ISO 时间戳为 UTC。
- 序号不持久化计数器：生成 ID 时扫描 `GMIX_DATA/` 与 `_staging/` 中同前缀目录，取当前时间桶已用最大序号 +1。
- 审计完整性：`scan.json/checksums` 覆盖 `raw.bin` 与各 `.f64` 数组的 sha256（v1.0 起数组哈希可选，缺失表示未启用）。
- `sequence` 是会话内序号，但 **不建独立“会话目录”**。
- 失败/中断/强杀的采集留在 `_staging/<groupId>/`，不进入最终目录；失败组目录重命名加 `.failed` 标记（`_staging/<groupId>.failed/`），保留失败现场（`saveRaw=onFailure` 时的 raw.bin 即在此），按 `retentionDays` 清理。
- 每个 `Rxxx/` 与 `AVG/` 内复制一份 `calibration.json` 快照；全局校准库在 `_calibration/<calibrationId>/`。
- `view.json`、`session.log` 可选；缺失不影响测量语义。

### 2.2 平均规则

N=1 与 N>1 共用同一套结构，只有 `AVG/` 是否存在不同。

`group.json` 完整字段：

```json
{
  "schemaVersion": "1.0",
  "groupId": "GR-20261105-1530-001",
  "createdAtUtc": "2026-11-05T07:30:00Z",
  "status": "complete",
  "averageCount": 5,
  "scanIds": [
    "SC-20261105-153022-001",
    "SC-20261105-153024-001",
    "SC-20261105-153026-001",
    "SC-20261105-153028-001",
    "SC-20261105-153030-001"
  ],
  "validCount": 5,
  "rejected": [],
  "avgMode": "linear_ps",
  "alignment": "same_dist",
  "avgScanId": "AV-20261105-153032-001",
  "calibrationId": "CAL-20261105-001",
  "configHash": "sha256:9f2c...",
  "acquisition": {
    "sampleRateMhz": 50,
    "acqTimeMs": 8,
    "triggerMode": "software",
    "triggerDelayMs": 1
  }
}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | `"1.0"` |
| `groupId` | string | 是 | `GR-YYYYMMDD-HHMM-NNN` |
| `createdAtUtc` | string | 是 | 组创建时间（UTC） |
| `status` | string | 是 | `complete` / `incomplete`（validCount<N 仍出 AVG）/ `failed`（未出 AVG） |
| `averageCount` | int | 是 | 计划平均次数 N |
| `scanIds` | string[] | 是 | 全部采集的 scanId，与 `Rxxx/` 目录一一对应 |
| `validCount` | int | 是 | 有效重复数 |
| `rejected` | string[] | 否 | 被剔除的 scanId |
| `avgMode` | string | 是 | `linear_ps` |
| `alignment` | string | 是 | `same_dist` / `resampled` / `invalid`（拒绝平均） |
| `avgScanId` | string/null | 是 | 平均结果 ID；N=1 与 `failed` 组为 `null` |
| `calibrationId` | string/null | 是 | 组级校准；组内所有 scan 必须一致 |
| `configHash` | string | 是 | 组内所有 scan 必须一致，否则整组拒绝平均 |
| `acquisition` | object | 是 | 组级采集设置快照（采样率、时长、触发等） |

规则：

- 平均在**线性 `ps` 域**进行：`ps_avg[k] = mean(ps_i[k])`；
- 先校验各次 `dist` 轴（量化判据）：
  - 长度一致且逐点最大偏差 ≤ 1 个 bin 间隔 → `same_dist`（浮点误差范围内）；
  - 首末点覆盖范围相对偏差 ≤ 5% → 线性插值到参考轴（第 1 个有效 scan 的 `dist`），标记 `resampled`；
  - 超出上述范围 → 拒绝整组平均，`alignment = invalid`，组标记 `failed`；
- 只对平均后的 `ps` 做一次 `detect`；不对多次 IL/RL 结果再平均；
- 某次重复失败：`validCount < averageCount`，仍可出 `AVG`，但 `status = incomplete`；
- 组内 `configHash` 或 `calibrationId` 不一致：整组拒绝平均，`status = failed`；
- 多段数据必须明确 `segmentIndex`，不能默认拼接。

精度字段（自有口径，不向厂商索取）：

- `AVG/summary.json` 填 `ilPrecisionDb` / `rlPrecisionDb`；
- 计算样本标准差（分母 `N-1`），取值口径**组内统一、不混用**：
  - 组已标定且所有有效重复均有 `totalIlDb`/`totalRlDb` → 用**标定值**，`precisionBasis = "calibrated"`；
  - 否则（含未标定组）统一用**原始值** `totalIlRawDb`/`totalRlRawDb`，`precisionBasis = "raw"`；
- 参与精度计算的有效重复数 `valid.Count < 2` → 两项精度填 `null`，不写 0；
- 各 `Rxxx/summary.json` 的精度字段保持 `null`，只有 `AVG/` 填；
- `precisionMode = "sample_std"`，`precisionCount = valid.Count`（可小于 `validCount`）。

### 2.3 存储增长与保留策略

| 参数 | raw.bin | 派生数组 | 单次约 | N=5 重复合计 |
|---|---:|---:|---:|---:|
| 50 MHz / 8 ms | 3.2 MB | 6.4 MB | ~9.6 MB | ~48 MB |
| 250 MHz / 10 ms | 20 MB | 40 MB | ~60 MB | ~300 MB |
| 250 MHz / 50 ms | 100 MB | 200 MB | ~300 MB | ~1.5 GB |

建议配置：

```text
saveRaw = always | onFailure | off
keepRepeats = all | events_only | none
retentionDays / maxTotalBytes
```

- `saveRaw=off` 时：`data.rawFile = null`、`checksums.rawSha256 = null`，派生数组仍保存；
- `keepRepeats=all`：保留 R001..R00N 完整数据；
- `keepRepeats=events_only`：只保留各次 `events.json/summary.json`，raw/数组只留 `AVG/`；
- `keepRepeats=none`：只留 `group.json` 与 `AVG/`；
- 250 MHz 长采集必须配保留策略，否则磁盘增长很快；
- `_staging` 启动时清理：未带 `.failed` 标记的进行中残留目录直接删除；`.failed` 目录按保留策略清理；
- 历史检索的数据库只存索引与路径，不存波形。

---

## 3. `scan.json` v1.0（含组字段）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `scanId` | string | 是 | 单次/平均结果 ID，`SC-...` 或 `AV-...` |
| `session.groupId` | string | 是（新数据） | 测量组 ID，`GR-...`，对应 `<groupId>/` 目录 |
| `session.repeatIndex` | int/null | 是（新数据） | 组内序号 `1..N`；`AVG` 为 `null` |
| `session.isAverage` | bool | 是（新数据） | 是否平均结果 |
| `session.averageOf` | string[] | AVG 必填 | 平均引用的 `scanId` 列表 |
| `session.avgMode` | string | 否 | `linear_ps` |
| `session.averageCount` | int | 否 | 平均次数，默认 1 |
| `session.runMode` | string | 否 | `single` / `loop` |
| `session.repeatGroupId` | string/null | 否 | 兼容字段，等同 `groupId`，新数据不再使用 |
| `session.analysisRange.startM` | double | 否 | 分析起点 |
| `session.analysisRange.endM` | double | 否 | 分析终点 |
| `session.analysisRange.guardM` | double | 否 | 边界保护带，默认 0.1 m；未设置或 ≤ 0 时按 0.1 计算 |
| `session.rlMode` | string | 否 | `peak_only` / `baseline_plus_peak` |
| `session.zeroPeakExcluded` | bool | 否 | UI 是否过滤零距峰 |
| `calibrationId` | string/null | 否 | 关联 `calibration.json` |
| `calibrationState` | string | 否 | `uncalibrated` / `calibrated` |

### 3.1 `acquisition` v0.2 新增（厂商固定参数）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `acquisition.sweepPeriodMs` | double | 否 | 光源上扫周期，厂商值 10 ms |
| `acquisition.windowStartMs` | double | 否 | 有效采集窗口起点，厂商建议 1 ms |
| `acquisition.windowEndMs` | double | 否 | 有效采集窗口终点，厂商建议 9 ms |

厂商固定配置（已确认）：

```text
采样率   = 50 MHz
上扫周期 = 10 ms
有效窗口 = 1–9 ms（8 ms）
推荐采集 = 50 MHz / 8 ms，窗口起点 1 ms
```

`acquisition.acqTimeMs` 仍表示采集时长；窗口起止用于说明这 8 ms 落在 10 ms 扫频周期的哪一段。

示例：

```json
"scanId": "SC-20261105-153022-001",
"session": {
  "groupId": "GR-20261105-1530-001",
  "repeatIndex": 1,
  "isAverage": false,
  "averageCount": 1,
  "avgMode": "linear_ps",
  "runMode": "single",
  "analysisRange": { "startM": 0.05, "endM": 20.0, "guardM": 0.1 },
  "rlMode": "peak_only",
  "zeroPeakExcluded": true
},
"calibrationId": "CAL-20261104-001",
"calibrationState": "calibrated"
```

---

## 4. `events.json` v1.0

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `peaks[].peakNo` | int | 否 | 显示编号，1..N |
| `peaks[].distanceM` | double | 否 | 与 `locM` 相同，UI 用 |
| `peaks[].zeroPeak` | bool | 否 | 是否零距峰 |
| `events[].stepLossDb` | double/null | 否 | **正损耗** = `-ilDb` |
| `events[].rlDb` | double/null | 否 | 绝对回损（标定后，可为负） |
| `events[].rlDbRel` | double | 否 | 相对回损（未标定） |
| `events[].zeroPeak` | bool | 否 | 是否零距峰 |
| `display.peakCount` | int | 否 | 过滤后显示峰数 |
| `display.zeroPeakExcluded` | bool | 否 | 是否已排除零距峰 |
| `arrays.baseline.file` | string | 否 | `baseline.f64`，来自 `detect.baseline_db` |
| `arrays.baseline.dtype` | string | 否 | `float64_le` |
| `arrays.baseline.count` | int | 否 | `outputLength` |

规则：

- `ilDb` 保留原始差值（负值）；`stepLossDb = -ilDb` 用于 UI 显示正损耗；
- 零距峰默认 `zeroPeak=true`，UI 默认不显示，但数据保留；
- `rlDb` 为 `null` 表示未标定，UI 只能显示 `rlDbRel`；
- **dB 相对电平值域**：`baseline.f64`、`heightDbRel`、`noiseFloorDbRel` 等均为 `20log10` 相对电平，实测典型为**负值**（约 -40 ~ -110 dB）；基线统计（中值窗口、基线功率求和）只使用 `baselineDb < 0` 的点，`>= 0` 视为无效/哨兵值不参与计算；
- `peaks[]` 中全部已检出峰参与功率求和，`heightDbRel` 可为负。

---

## 5. `summary.json` v1.0

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | `"1.0"` |
| `scanId` | string | 是 | 关联 |
| `groupId` | string | 是（新数据） | 测量组 ID |
| `group.validCount` | int | 否 | 有效重复数 |
| `group.rejected` | string[] | 否 | 被剔除的 scanId |
| `group.avgMode` | string | 否 | `linear_ps` |
| `group.alignment` | string | 否 | `same_dist` / `resampled` |
| `analysisRange.startM/endM` | double | 是 | 本次统计区间 |
| `rlMode` | string | 是 | `peak_only` / `baseline_plus_peak` |
| `averageCount` | int | 是 | 参与平均的次数 |
| `totalIlRawDb` | double/null | 是 | 未标定区间插损；无法计算（无有效基线/段）时为 `null` |
| `totalIlDb` | double/null | 是 | 标定后总插损 |
| `totalRlRawDb` | double/null | 是 | 未标定总回损；区间内无有效峰时为 `null` |
| `totalRlDb` | double/null | 是 | 标定后总回损 |
| `ilPrecisionDb` | double/null | 否 | 插损精度；仅 `AVG/` 填，样本标准差 |
| `rlPrecisionDb` | double/null | 否 | 回损精度；仅 `AVG/` 填，样本标准差 |
| `precisionMode` | string | 否 | `sample_std` |
| `precisionCount` | int | 否 | 参与精度计算的有效重复数 |
| `precisionBasis` | string | 否 | 精度取值口径：`calibrated` / `raw`（见 §2.2） |
| `peakCount` | int | 是 | 区间内峰数 |
| `eventCount` | int | 是 | 区间内事件数 |
| `warnings` | string[] | 否 | 质量警告 |

### 5.1 自有口径公式

设电平 `L = 20log10(ps)`，功率 `P = 10^(L/10)`：

```text
仅峰值：
P_peak = Σ 10^(peakHeightDb_i / 10)
totalRlRawDb = 10 × log10(P_peak)

基线+峰值：
P_base = Σ 10^(baselineDb_k / 10)
totalRlRawDb = 10 × log10(P_peak + P_base)

绝对回损：
totalRlDb = totalRlRawDb + rlOffsetK

区间插损：
totalIlRawDb = median(L_start) - median(L_end)
totalIlDb = ilSlope × totalIlRawDb + ilIntercept

精度（仅 AVG/，自有口径）：
组已标定且所有有效重复均有标定值 → basis = "calibrated"，v_i = totalIlDb/totalRlDb
否则（含未标定组）→ basis = "raw"，v_i = totalIlRawDb/totalRlRawDb
组内统一口径，不混用两种取值
ilPrecisionDb = sample_std(valid 的 v_i)          # 分母 N-1
rlPrecisionDb = sample_std(valid 的 v_i)          # 分母 N-1
valid.Count < 2 → 两项均为 null
各 Rxxx/summary.json 的精度字段 = null
precisionMode = "sample_std"
precisionCount = valid.Count
precisionBasis = "calibrated" | "raw"
```

说明：这些是**自有口径**，不保证与厂商 UI 数值一致；厂商公式仍属 TBD-S1~S6。`peakHeightDb_i` 即 `peaks[].heightDbRel`（相对电平，可为负），全部已检出峰参与求和；`median(L_start)`/`median(L_end)` 只统计窗口内 `baselineDb < 0` 的点。

---

## 6. `calibration.json` v1.0

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | `"1.0"` |
| `calibrationId` | string | 是 | 唯一 ID，格式 `CAL-YYYYMMDD-NNN`（末段 3 位自然数） |
| `createdAtUtc` | string | 是 | 标定时间 |
| `operator` | string/null | 否 | 操作员 |
| `configHash` | string | 是 | 采样率/AFE/量程/通道组合哈希 |
| `method` | string | 是 | `reference_reflector` / `attenuator` / `vendor` |
| `distScale` | double | 是 | 距离比例 |
| `rlOffsetK` | double/null | 否 | 绝对回损偏移，dB |
| `rlCurveKFile` | string/null | 否 | 频率相关 K 曲线文件 |
| `ilSlope` | double | 否 | 插损斜率 |
| `ilIntercept` | double | 否 | 插损截距 |
| `triggerThresholdDb` | double/null | 否 | 峰值触发阈值（检测参数，随校准快照存储以保证单文件可复现） |
| `reference.type/value/serial` | string/double/string | 否 | 标定件信息 |
| `validation.points` | array | 否 | 标定点与残差 |

厂商截图示例（仅作对照，不是默认值）：

```text
功率谱密度 = 48
插损斜率   = 0.5361
插损截距   = 0.7373
触发阈值   = 35
```

本次实机标定记录（自有口径）：

```text
插损斜率 = 0.5361
插损截距 = -0.1
```

当前斜率取自厂商截图值 `0.5361`；实现与测试样例已按该值计算，后续如有实测斜率再替换。

---

## 7. `view.json` 与 `session.log`（可选）

`view.json` 只存 UI 状态，不影响测量语义：

```json
{
  "schemaVersion": "1.0",
  "groupId": "GR-20261105-1530-001",
  "scanId": "SC-20261105-153022-001",
  "xRange": [0, 20],
  "yRange": [-150, -40],
  "cursor": [{ "x": 0.78562, "y": -94.8857 }],
  "activeTab": "scan",
  "selectedPeakNo": 3
}
```

`session.log` 为纯文本运行日志，记录连接、配置、采集、异常、耗时。

---

## 8. TBD 缺口清单

| ID | 缺口 | 影响 | 状态 | 关闭方式 | 优先级 |
|---|---|---|---|---|---|
| TBD-S1 | 厂商基线积分窗口（类型/长度/统计量/分位数/边界） | 总 IL/RL 与厂商不一致 | 待定 | 厂商伪代码或黑盒实验 | P0 |
| TBD-S2 | 厂商店阶算法（取哪两段基线、窗口、正负号、首末事件） | 台阶值不一致 | 待定 | 厂商伪代码或黑盒实验 | P0 |
| TBD-S3 | 总插损定义（台阶求和 vs 区间基线差） | 总 IL 数值口径 | 已关闭 | 自有口径已定；厂商对齐移入可选扩展 | P1 |
| TBD-S4 | 总回损仅峰值/基线+峰值积分范围 | 总 RL 数值口径 | 已关闭 | 自有口径已定；厂商对齐移入可选扩展 | P1 |
| TBD-S5 | 绝对回损 `K`/PSD 基准与可溯源标定件 | 无法显示绝对 RL/dBm | 待定 | 标定实验 + 参考件 | P0 |
| TBD-S6 | IL 斜率/截距作用对象（逐事件/总 IL，截距是否只加一次） | 标定后数值偏差 | 待定 | 厂商确认或自定义 | P1 |
| TBD-S7 | 厂商触发阈值 35 的语义 | 峰值检出差异 | 待定 | 厂商确认 | P2 |
| TBD-S8 | 平均次数与精度统计方式 | 精度口径 | 已关闭 | 自有口径：样本标准差 + `precisionBasis` 回退规则 | P2 |
| TBD-S9 | 距离标定基准与 `dist_scale` 溯源 | 距离绝对值偏差 | 待定 | 已知长度光纤/反射件 | P0 |
| TBD-S10 | 零距峰过滤规则与峰编号 | UI 峰数不一致 | 待定 | 已确认默认排除，规则待定 | P2 |
| TBD-S11 | 分析区间边界处理/保护带 | 区间总 IL/RL 偏差 | 待定 | 自定义并记录 | P2 |
| TBD-S12 | 配置变化后校准失效规则 | 误用旧校准 | 已定义待实现 | configHash 不匹配即失效 | P1 |
| TBD-S13 | PDF 报表格式 | 导出功能 | 待定 | 第二阶段 | P2 |
| TBD-S14 | 多段/外触发 | 采集模式 | 待定 | 厂商解决后再说 | P2 |

---

## 9. 实施优先级

1. **P0**：Schema v1.0 组结构落地（`<groupId>/group.json`、`R001..R00N`、`AVG/`、组字段）；
2. **P0**：`dist_scale` 标定（TBD-S9）+ 绝对回损标定（TBD-S5）；
3. **P1**：总 IL/RL 自有口径实现（TBD-S3/S4/S6）；
4. **P1**：配置失效规则（TBD-S12）；
5. **P2**：厂商基线/台阶算法对齐（TBD-S1/S2/S7）、PDF、多段。

---

## 10. 当前实现（v0.2 字段已落地，v1.0 组结构待实现）

> 当前 `Gmix.Data` 代码仍按 `schemaVersion="0.2"` 输出；实现组结构时升级为 `"1.0"`。
> 基线过滤（`baselineDb < 0`）、guard 默认 0.1、全部已检出峰参与功率求和，已按本文口径修正代码与测试（2026-11-05）；`precisionBasis` 随组结构落地实现。

| 文件 | 作用 |
|---|---|
| `GM-IX/src/Gmix.Data/SchemaModels.cs` | v0.2 全部 JSON 模型（scan/ofdr/events/summary/calibration/view） |
| `GM-IX/src/Gmix.Data/CalibrationModel.cs` | 距离比例、绝对 RL、IL 斜率/截距、configHash 校验 |
| `GM-IX/src/Gmix.Data/MetricsAggregator.cs` | 总 IL/RL、仅峰值/基线+峰值、精度、区间统计 |
| `GM-IX/src/Gmix.Data/JsonAndFileStore.cs` | JSON、`.f64`、`raw.bin` 读写与哈希 |
| `GM-IX/src/Gmix.Data/SchemaValidator.cs` | 长度、单调性、通道、计数等校验 |
| `GM-IX/src/Gmix.Data/Program.cs` | 合成样例 + 公式回归测试 |
| `GM-IX/src/Gmix.Data/build-and-test.ps1` | 一键编译 `Gmix.Data.dll` + 测试 |

测试输出：

```text
PASS schema validation
PASS calibration round-trip
PASS peak_only totalRlRaw
PASS baseline_plus_peak
PASS 区间插损 raw=12dB
PASS 插损标定公式
PASS 绝对回损公式
PASS 精度 sample std
PASS schema v0.2
```

样例数据生成在 `GM-IX/tests/fixtures/schema_v02_sample/`（10 MHz / 1 ms，含 raw、数组、JSON）。

---

# Part B：处理与生命周期契约


> 契约地位：Part B 承载**进程边界**与**厂商边界**的契约（见 §0.1）；各角色的义务与禁止见 §0.2。
> 适用范围：定义自研软件内部的数据处理契约，包括输入/输出、边界条件、错误处理、数据所有权与持久化规则。
> 不包含：厂商私有算法复刻、UI 实现、数据库与多工位部署。
> 数据协议见 **Part A**；字段、公式与文件布局以 Part A 为准。

---

## 1. 处理链路

```text
设备
  │  qt_acq（独立进程，可强杀）
  ▼
ScanFrame          raw.bin + scan.json
  │  反交织、选 P/S/A、int16→double
  ▼
PsaArrays          P/S/A：double[N]
  │  ofdr_process
  ▼
OfdrResult         dist/freq/ps：double[N/2+1]
  │  detect_process
  ▼
EventMetrics       峰/段/事件 + 相对 IL/RL
  │  MetricsAggregator + CalibrationModel
  ▼
Summary            总 IL/RL、精度、分析区间
  │  JsonStore / ArrayStore
  ▼
GMIX_DATA/<groupId>/R001..R00N/ + AVG/
```

---

## 2. 模块职责

模块级职责是 §0.2 角色义务的展开；"不允许做的事"一列即模块级契约红线。

| 模块 | 输入 | 输出 | 不允许做的事 |
|---|---|---|---|
| Acquisition | 采集参数 | `ScanFrame` | 不做 OFDR、不做 UI 绑定 |
| Conversion | `ScanFrame.raw` | `PsaArrays` | 不做标定系数猜测、不写 dB |
| OfdrProcessor | `PsaArrays` + `fs/distScale` | `OfdrResult` | 不暴露厂商指针、不做峰值检测 |
| Detector | `OfdrResult` + `DetectConfig` | `EventMetrics` | 不做绝对电平标定、不改 dist/ps |
| Aggregator | `EventMetrics` + baseline + 区间 | `Summary` | 不修改原始数组、不读硬件 |
| Store | `ScanBundle` | 文件 | 不做计算、不静默补齐 |

---

## 3. 数据对象契约

### 3.1 ScanFrame

- `raw`：int16 小端、4 通道交织、每帧 8 字节；
- `out_len == bufferBytes`，且 `out_len % 8 == 0`；
- `bufferBytes = rate_mhz × acq_time_ms × 8000 × seg_count`；
- 通道固定 `ch0=P, ch1=S, ch2=A, ch3 忽略`；
- 失败/短读不得进入后续阶段。

### 3.2 PsaArrays

- `P/S/A` 三路等长，长度 `N = 总帧数`；
- 元素为 `int16` 直接转 `double`，**不做 offset/gain/factor 换算**；
- `fs = sampleRateMhz × 1e6`；
- 长度不一致直接报错，不截断、不补齐。

### 3.3 OfdrResult

- 输出长度 `Lout = N/2 + 1`；
- `freq[k] = k × fs / N`；
- `dist` 严格递增、单次内等间距；
- `ps` 为线性幅值，未取 dB、未归一化；
- `dist_scale` 只作用于 `dist`，不改 `freq/ps`；
- 厂商结果指针用完即释放，不向上传递。

### 3.4 EventMetrics

- 输入 `dist/ps` 必须等长、`dist` 严格递增；
- 峰/段/事件数组长度必须与 `counts` 一致；
- `ilDb = after_floor - before_floor`（负值）；显示层用 `stepLossDb = -ilDb`；
- 首事件 `ilDb = null`，禁止 JSON `NaN`；
- 零距峰保留数据，但默认 `zeroPeakExcluded=true`；
- `ps_db = 20log10(ps)` 只在这里发生一次，后续不得重复转换。

### 3.5 Summary

字段与公式以 **Part A §5.1** 为准；本契约只规定调用条件：

- 仅峰值 / 基线+峰值由 `rlMode` 决定；
- 校准 `configHash` 不匹配时，`totalIlDb/totalRlDb = null`，并写 warning；
- 精度字段可选，MVP 不显示。

---

## 4. 错误契约

| 场景 | 处理 |
|---|---|
| `out_len != bufferBytes` | 拒绝，标记 `shortRead`，不进入处理 |
| `out_len % 8 != 0` | 拒绝，标记 `frameAligned=false` |
| P/S/A 长度不一致 | 抛错，不截断 |
| `len <= 0` | 拒绝 |
| `outputLength != N/2+1` | 拒绝，标记结果损坏 |
| `dist` 非严格递增 | 拒绝，不作为测量结果 |
| `freqLast` 与 `fs/2` 相对偏差 > 1e-6 | 标记异常 |
| 缺少 `baseline.f64` | 基线+峰值退化为仅峰值，写 warning |
| 校准 `configHash` 不匹配 | 不应用绝对 IL/RL，写 warning |
| 零距峰 | 保留，默认过滤显示 |
| `dist` 轴长度不同或偏差超 §2.2 阈值 | 拒绝整组平均，`alignment=invalid`，组留 `_staging` |
| `dist` 轴轻微偏差（浮点误差外、5% 内） | 插值到参考轴，标记 `resampled`，写 warning |
| 组内 `configHash`/`calibrationId` 不一致 | 拒绝平均，组标记 `failed` |
| `scanIds` 与 `Rxxx/` 目录不一致 | 拒绝提交 `group.json`，报组完整性错误 |
| 提交时最终目录已存在 | 拒绝重命名，报冲突，不覆盖已有数据 |
| `validCount = 0` | 不生成 `AVG/`，组标记 `failed` |
| 任何异常 | 不得 `catch {}` 静默吞掉；必须分类上报 |

错误分类：所有错误按 `连接 / 协议 / 数据 / 超时 / 组一致性` 归类后上报 UI。

---

## 5. 所有权与线程

- 厂商 DLL 分配的 `dist/freq/ps/baseline` 由处理层立即复制并释放；
- 上层只持有托管 `double[]` / 模型对象；
- `read` 只在采集进程调用，UI 线程禁止 P/Invoke `read`；
- 处理是纯计算，不读文件、不碰 UI；
- UI 只读 `ScanBundle`，大数组按屏幕像素抽稀显示；
- 每个 `ScanBundle` 的 `calibration.json` 不可变。

---

## 6. 版本与追溯

每个结果必须能回答：

```text
谁采的：device、sdkVersion、appVersion
怎么采的：sampleRate、acqTime、trigger、window
怎么算的：ofdr/detect sha256、configHash
用什么校准：calibrationId、K/slope/intercept
什么时候：capturedAtUtc
```

规则：

- `schemaVersion` 固定随协议版本；
- 新增字段必须可选，不改变已有语义；
- 不兼容变更升主版本；
- `configHash` 变化即旧校准失效；
- 缺字段时按“不支持/未启用”，不得猜默认值。

---

## 7. 持久化契约

- 一次成功测量 = 一个 `<groupId>` 目录；N=1 为 `group.json + R001/`，N>1 为 `group.json + R001..R00N + AVG/`；
- 先写 `_staging/<groupId>/`，整组处理完成后原子重命名提交；
- JSON 只放元数据，数组用 `.f64`，原始数据用 `.bin`；
- 写入顺序：各 `Rxxx/` 数组 → 元数据 → summary → `AVG/` → `group.json` 最后提交；
- 失败/中断留在 `_staging/<groupId>/`，不产生最终目录；
- `saveRaw` 可配置；关闭时 `rawFile=null`，派生数组仍保存；
- `keepRepeats` 可配置，但 `group.json` 与 `AVG/` 必须保留；
- 单写者假设：同一 `GMIX_DATA` 同一时刻只允许一个编排进程执行 staging→最终目录的提交；采集进程只写 `_staging/<groupId>/`，不执行提交。

---

## 8. 当前实现与缺口

| 项 | 状态 |
|---|---|
| Schema 模型、JSON/数组读写 | 已实现（`Gmix.Data`，暂不含 group 字段） |
| 组目录、`group.json`、`AVG/` | 文档已定义，代码待实现 |
| CalibrationModel、MetricsAggregator | 已实现 |
| SchemaValidator、回归测试 | 已实现 |
| Acquisition 适配器 | 复用 `Qt2144Probe`，未产品化 |
| Conversion/Ofdr/Detect 编排 | 未形成正式服务 |
| WPF UI | 未开始 |
| 厂商基线/台阶/绝对 RL 对齐 | 可选扩展，不阻塞主线 |

---

## 9. 验收检查

- [ ] §0.3 不变量清单全项通过；
- [ ] 采集 `out_len == bufferBytes`；
- [ ] P/S/A 三路等长；
- [ ] `Lout == N/2+1`；
- [ ] `dist` 严格递增；
- [ ] `freq` 末点 `fs/2`；
- [ ] `events.counts` 与数组长度一致；
- [ ] 首事件 `ilDb = null`；
- [ ] 校准 `configHash` 不匹配时不输出绝对 IL/RL；
- [ ] `groupId`/`scanId`/`avgScanId` 符合 `GR/SC/AV-YYYYMMDD-HHMM(SS)-NNN`，末段 3 位；
- [ ] `group.json` 存在，且 `scanIds` 与 `Rxxx/` 目录一致；
- [ ] N=1 时存在 `R001/`，不生成 `AVG/`；
- [ ] N>1 时存在 `AVG/`，且 `avgScanId` 有效；
- [ ] 平均只在线性 `ps` 域进行，`dist` 轴一致或已标记 `resampled`；
- [ ] N>1 时 `AVG/summary.json` 填 `ilPrecisionDb/rlPrecisionDb`，各 `Rxxx/` 保持 `null`；
- [ ] 精度按样本标准差（N-1）计算，`validCount<2` 或值为 `null` 时为 `null`；
- [ ] 精度取值口径组内统一，`precisionBasis` 正确标注；未标定组用原始值，不再恒为 `null`；
- [ ] 基线统计只使用 `baselineDb < 0` 的点；
- [ ] 每个 `Rxxx/` 与 `AVG/` 可回读并通过 SchemaValidator（按 `keepRepeats` 模式：已被保留策略删除的数组不参与校验）；
- [ ] 任何失败不进入最终目录，`_staging/<groupId>/` 可清理。
