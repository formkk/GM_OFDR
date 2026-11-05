# GM-IX 自有数据协议 Schema v0.2（扩展版）

> 目的：在 v0.1（MVP）基础上，补齐厂商 UI 可显示功能所需的字段，同时明确仍未闭合的 TBD 缺口。
> 关系：v0.2 **向后兼容** v0.1；新增字段全部可选，v0.1 文件可直接读取。
> 版本：文档 v0.2；JSON `schemaVersion = "0.2"`。
> 关联：[Schema v0.1](./上位机数据Schema_v0.1.md)、[上位机数据协议调研与讨论](./上位机数据协议调研与讨论.md)

---

## 1. 版本与兼容规则

| 项 | 规则 |
|---|---|
| 向后兼容 | v0.1 的 `scan.json/ofdr.json/events.json` 可直接被 v0.2 读取 |
| 新增字段 | 全部可选；缺失表示“不支持/未启用” |
| 语义变更 | 禁止在 v0.2 内改已有字段单位或含义；需变更时升 `v0.3` |
| 配置失效 | 采样率、AFE、量程、通道映射变化后，旧 `calibrationId` 失效 |
| 数值缺失 | JSON 用 `null`，禁止 `NaN` |

---

## 2. 文件布局（v0.2）

```text
GMIX_DATA/                     # 数据根目录
├─ _staging/                   # 暂存区：未完成采集/处理，启动时清理
├─ _calibration/               # 全局校准库
│  └─ <calibrationId>/         # 一个校准版本
│     └─ calibration.json      # 校准参数、标定件、校验点
└─ <scanId>/                   # 一次成功扫描的完整证据包
   ├─ scan.json                # 采集元数据：设备、参数、质量、会话
   ├─ raw.bin                  # 原始数据：int16 小端、4 通道交织
   ├─ ofdr.json                # 处理元数据：fs、distScale、N、输出长度
   ├─ dist.f64                 # 距离轴 double[]，长度 N/2+1
   ├─ freq.f64                 # 频率轴 double[]，长度 N/2+1
   ├─ ps.f64                   # 功率谱 double[]，线性幅值
   ├─ baseline.f64             # 可选：基线 double[]，长度 N/2+1
   ├─ events.json              # 峰/段/事件与相对 IL/RL
   ├─ summary.json             # 总 IL/RL、精度、分析区间
   ├─ calibration.json         # 本次实际使用的校准快照
   ├─ view.json                # 可选：UI 视图状态
   └─ session.log              # 可选：运行日志
```

v0.1 文件缺少 `summary.json/calibration.json` 时，按“仅相对模式、无汇总”处理。

### 2.1 扫描目录语义

- **一个 `<scanId>` 目录 = 一次成功扫描的全部数据**；不跨扫描、不追加到旧目录。
- 每次成功扫描新建一个目录，`scanId` 全局唯一（建议 `yyyyMMdd_HHmmss_NNNN`）。
- `sequence` 是会话内序号，但 MVP **不建独立“会话目录”**。
- 失败/中断/强杀的采集留在 `_staging/<scanId>`，不进入最终目录；启动时清理或标记为不完整。
- 每个 scan 目录内复制一份 `calibration.json` 快照，保证可复现；全局校准库在 `_calibration/<calibrationId>/`。
- `view.json`、`session.log` 可选；缺失不影响测量语义。

### 2.2 平均次数与多段

MVP 固定 `averageCount=1`、`segCount=1`，即“一扫一目录”。

后续平均/多段上线时：

- 不把多次扫描塞进同一个 `<scanId>` 目录；
- 每次重复仍生成独立 `<scanId>`，用 `repeatGroupId` 关联；
- 组级汇总单独建 `<groupId>/group.json` 或 `avg/`，具体结构另行定义；
- 多段数据必须明确 `segmentIndex`，不能默认拼接。

### 2.3 存储增长与保留策略

| 参数 | raw.bin | dist/freq/ps/baseline | 单次约 |
|---|---:|---:|---:|
| 50 MHz / 8 ms | 3.2 MB | 6.4 MB | ~9.6 MB |
| 250 MHz / 10 ms | 20 MB | 40 MB | ~60 MB |
| 250 MHz / 50 ms | 100 MB | 400 MB | ~500 MB |

建议配置：

```text
saveRaw = always | onFailure | off
retentionDays / maxTotalBytes
```

- `saveRaw=off` 时：`data.rawFile = null`、`checksums.rawSha256 = null`，派生数组仍保存；
- 250 MHz 长采集必须配保留策略，否则磁盘增长很快；
- `_staging` 每次启动清理；
- 历史检索的数据库只存索引与路径，不存波形。

---

## 3. `scan.json` v0.2 新增

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `session.averageCount` | int | 否 | 平均次数，MVP 默认 1 |
| `session.runMode` | string | 否 | `single` / `loop` |
| `session.repeatGroupId` | string/null | 否 | 同一平均组的 ID |
| `session.analysisRange.startM` | double | 否 | 分析起点 |
| `session.analysisRange.endM` | double | 否 | 分析终点 |
| `session.analysisRange.guardM` | double | 否 | 边界保护带，默认 0 |
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
"session": {
  "averageCount": 1,
  "runMode": "single",
  "repeatGroupId": null,
  "analysisRange": { "startM": 0.05, "endM": 20.0, "guardM": 0.1 },
  "rlMode": "peak_only",
  "zeroPeakExcluded": true
},
"calibrationId": "CAL-20261104-0001",
"calibrationState": "calibrated"
```

---

## 4. `events.json` v0.2 新增

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
- `rlDb` 为 `null` 表示未标定，UI 只能显示 `rlDbRel`。

---

## 5. `summary.json` v0.2（新增）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | `"0.2"` |
| `scanId` | string | 是 | 关联 |
| `analysisRange.startM/endM` | double | 是 | 本次统计区间 |
| `rlMode` | string | 是 | `peak_only` / `baseline_plus_peak` |
| `averageCount` | int | 是 | 参与平均的次数 |
| `totalIlRawDb` | double | 是 | 未标定区间插损 |
| `totalIlDb` | double/null | 是 | 标定后总插损 |
| `totalRlRawDb` | double | 是 | 未标定总回损 |
| `totalRlDb` | double/null | 是 | 标定后总回损 |
| `ilPrecisionDb` | double/null | 否 | 插损精度 |
| `rlPrecisionDb` | double/null | 否 | 回损精度 |
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

精度：
ilPrecisionDb = std(重复测量的 totalIlDb)
rlPrecisionDb = std(重复测量的 totalRlDb)
```

说明：这些是**自有口径**，不保证与厂商 UI 数值一致；厂商公式仍属 TBD-S1~S6。

---

## 6. `calibration.json` v0.2（新增）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | `"0.2"` |
| `calibrationId` | string | 是 | 唯一 ID |
| `createdAtUtc` | string | 是 | 标定时间 |
| `operator` | string/null | 否 | 操作员 |
| `configHash` | string | 是 | 采样率/AFE/量程/通道组合哈希 |
| `method` | string | 是 | `reference_reflector` / `attenuator` / `vendor` |
| `distScale` | double | 是 | 距离比例 |
| `rlOffsetK` | double/null | 否 | 绝对回损偏移，dB |
| `rlCurveKFile` | string/null | 否 | 频率相关 K 曲线文件 |
| `ilSlope` | double | 否 | 插损斜率 |
| `ilIntercept` | double | 否 | 插损截距 |
| `triggerThresholdDb` | double/null | 否 | 峰值触发阈值 |
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
  "schemaVersion": "0.2",
  "scanId": "...",
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

| ID | 缺口 | 影响 | 关闭方式 | 优先级 |
|---|---|---|---|---|
| TBD-S1 | 厂商基线积分窗口（类型/长度/统计量/分位数/边界） | 总 IL/RL 与厂商不一致 | 厂商伪代码或黑盒实验 | P0 |
| TBD-S2 | 厂商店阶算法（取哪两段基线、窗口、正负号、首末事件） | 台阶值不一致 | 厂商伪代码或黑盒实验 | P0 |
| TBD-S3 | 总插损定义（台阶求和 vs 区间基线差） | 总 IL 数值口径 | 自有口径已定，厂商对齐待定 | P1 |
| TBD-S4 | 总回损仅峰值/基线+峰值积分范围 | 总 RL 数值口径 | 自有公式已定，厂商对齐待定 | P1 |
| TBD-S5 | 绝对回损 `K`/PSD 基准与可溯源标定件 | 无法显示绝对 RL/dBm | 标定实验 + 参考件 | P0 |
| TBD-S6 | IL 斜率/截距作用对象（逐事件/总 IL，截距是否只加一次） | 标定后数值偏差 | 厂商确认或自定义 | P1 |
| TBD-S7 | 厂商触发阈值 35 的语义 | 峰值检出差异 | 厂商确认 | P2 |
| TBD-S8 | 平均次数与精度统计方式 | MVP 不显示精度，不向厂商索取；如后续需要，自定义并记录 | P2 |
| TBD-S9 | 距离标定基准与 `dist_scale` 溯源 | 距离绝对值偏差 | 已知长度光纤/反射件 | P0 |
| TBD-S10 | 零距峰过滤规则与峰编号 | UI 峰数不一致 | 已确认默认排除，规则待定 | P2 |
| TBD-S11 | 分析区间边界处理/保护带 | 区间总 IL/RL 偏差 | 自定义并记录 | P2 |
| TBD-S12 | 配置变化后校准失效规则 | 误用旧校准 | 自定义：configHash 不匹配即失效 | P1 |
| TBD-S13 | PDF 报表格式 | 导出功能 | 第二阶段 | P2 |
| TBD-S14 | 多段/外触发 | 采集模式 | 厂商解决后再说 | P2 |

---

## 9. 实施优先级

1. **P0**：Schema v0.2 字段落地（session/summary/calibration/events 扩展）；
2. **P0**：`dist_scale` 标定（TBD-S9）+ 绝对回损标定（TBD-S5）；
3. **P1**：总 IL/RL 自有口径实现（TBD-S3/S4/S6）；
4. **P1**：配置失效规则（TBD-S12）；
5. **P2**：厂商基线/台阶算法对齐（TBD-S1/S2/S7）、PDF、多段。

---

## 10. 当前实现（Schema v0.2 字段落地）

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
