# GM-IX 自有数据协议 v1.0（MVP，Schema v0.1）

> 适用范围：MVP（.NET/WPF、单次扫频、相对测量、文件 + JSON）。
> 目标：定义上位机从设备提取、处理、保存、回读所需的全部数据字段与格式。
> 关联文档：[上位机数据协议调研与讨论](./上位机数据协议调研与讨论.md)、[GM-IX 三级数据处理链路数据报告](./GM-IX_三级数据处理链路数据报告.md)

---

## 1. 全局约定

| 项 | 约定 |
|---|---|
| 字符编码 | JSON 使用 UTF-8（无 BOM） |
| `schemaVersion` | 固定 `"1.0"`，后续不兼容变更时递增 |
| 时间 | UTC ISO 8601，如 `2026-11-04T15:30:00.123Z` |
| 距离单位 | 米（`m`） |
| 频率单位 | 赫兹（`Hz`） |
| 电平单位 | **相对 dB**，统一写 `dB_rel`；MVP 不写 `dBm` |
| 原始数据 | `int16` 小端，4 通道交织，每帧 8 字节 |
| 处理数组 | `float64` 小端，裸数组文件（`.f64`），长度由 JSON 声明 |
| 缺失值 | JSON 用 `null`；**不能写 `NaN`**（JSON 不支持） |
| 通道映射 | 固定 `P=0, S=1, A=2`，`ch3` 不使用 |
| 采样率 | 10 / 50 / 125 / 250 MHz |
| 段数 | MVP 固定 `segCount = 1` |

### 1.1 协议边界

**核心协议包含**：

- 采集帧元数据、原始波形；
- `ofdr` 输出数组（`dist/freq/ps`）；
- `detect` 的峰/段/事件与相对指标；
- 距离校准 `dist_scale` 与配置快照。

**核心协议不包含**（列为可选扩展，不影响 MVP）：

- 总插损/总回损聚合；
- 绝对回损 `dBm`、PSD 基准、IL 斜率/截距、厂商触发阈值；
- “仅峰值 / 基线+峰值”回损模式；
- 平均次数、精度统计；
- 厂商基线积分窗口与台阶算法细节；
- 数据库索引、PDF 报表、多段/外触发。

扩展规则：

- 新增字段必须是**可选**，缺失时按“不适用”处理；
- 不改变已有字段的名称、单位或语义；
- 不兼容变更时 `schemaVersion` 升到 `2.0`。

---

## 2. 每次测量的文件布局

```text
GMIX_DATA/                     # 数据根目录
└─ <scanId>/                   # 一次成功扫描的完整证据包
   ├─ scan.json                # 采集元数据：设备、参数、质量
   ├─ raw.bin                  # 原始数据：int16 小端、4 通道交织
   ├─ ofdr.json                # 处理元数据：fs、distScale、N、输出长度
   ├─ dist.f64                 # 距离轴 double[]，长度 N/2+1
   ├─ freq.f64                 # 频率轴 double[]，长度 N/2+1
   ├─ ps.f64                   # 功率谱 double[]，线性幅值
   ├─ events.json              # 峰/段/事件与相对 IL/RL
   └─ events.csv               # 可选：事件表导出
```

- `scanId` 建议 `yyyyMMdd_HHmmss_NNNN`，全局唯一；
- `raw.bin` 必须与 `scan.json` 的 `bufferBytes` 一致；
- `dist/freq/ps` 必须与 `ofdr.json` 的 `outputLength` 一致；
- 文件缺失或长度不符时，回读应报错，不得静默补齐。

> 目录语义（一扫一目录、失败/中断走 `_staging`、平均/多段分组、磁盘保留策略）见 [GM-IX 数据与处理规范 v1.0](./GM-IX_数据与处理规范_v1.0.md)。

---

## 3. `scan.json`：采集帧

### 3.1 字段定义

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | `"1.0"` |
| `scanId` | string | 是 | 全局唯一 |
| `sequence` | int | 是 | 会话内递增序号 |
| `capturedAtUtc` | string | 是 | 采集完成时间 |
| `dataOrigin` | string | 是 | MVP 固定 `"device"`；回放时为 `"file"` |
| `appVersion` | string | 是 | 上位机版本 |
| `device.name` | string | 是 | `"GM-IX"` |
| `device.model` | string | 是 | `"GM-IX"` |
| `device.serial` | string/null | 否 | 未知填 `null` |
| `device.firmware` | string/null | 否 | 未知填 `null` |
| `device.sdkVersion` | string | 是 | 实测 `"0.1.0"` |
| `device.hostIp` | string | 是 | 如 `192.168.5.55` |
| `device.deviceIp` | string | 是 | 如 `192.168.5.20` |
| `acquisition.sampleRateHz` | int | 是 | 如 `50000000` |
| `acquisition.sampleRateMhz` | int | 是 | 10/50/125/250 |
| `acquisition.acqTimeMs` | int | 是 | 采集时长 |
| `acquisition.segCount` | int | 是 | MVP 固定 1 |
| `acquisition.triggerMode` | int | 是 | 0 软件 / 1 外升 / 2 外降 |
| `acquisition.triggerDelayMs` | int | 是 | 未使用填 0 |
| `acquisition.channelMap.p` | int | 是 | 0 |
| `acquisition.channelMap.s` | int | 是 | 1 |
| `acquisition.channelMap.aux` | int | 是 | 2 |
| `acquisition.channelMap.ignored` | int[] | 是 | `[3]` |
| `acquisition.expectedFrames` | int | 是 | `rate × ms × 1000` |
| `acquisition.bufferBytes` | int | 是 | `rate × ms × 8000` |
| `acquisition.bytesPerFrame` | int | 是 | 8 |
| `configSnapshot.interface` | object | 是 | `Interface.json` 内容 |
| `configSnapshot.afe` | object | 是 | `AFE.json` 内容 |
| `configSnapshot.range` | object | 是 | `Range.json` 内容 |
| `configSnapshot.functional` | object | 是 | `FunctionalConfiguration.json` 内容 |
| `configSnapshot.hash` | string | 是 | 上述内容的 SHA256 |
| `data.rawFile` | string | 是 | `"raw.bin"` |
| `data.dtype` | string | 是 | `"int16_le"` |
| `data.byteOrder` | string | 是 | `"little"` |
| `data.interleavedChannels` | int | 是 | 4 |
| `quality.shortRead` | bool | 是 | `out_len != bufferBytes` |
| `quality.frameAligned` | bool | 是 | `out_len % 8 == 0` |
| `quality.saturation` | object | 是 | 每通道 `-32768/32767` 计数 |
| `quality.zeroCounts` | object | 是 | 每通道零值计数 |
| `quality.channelStats` | array | 否 | 每通道 min/max/mean/std/rms |
| `timing.openMs` | int/null | 否 | open 耗时 |
| `timing.readMs` | int/null | 否 | read 耗时 |
| `timing.totalMs` | int/null | 否 | 总耗时 |
| `calibration.distScale` | double | 是 | MVP 默认 `1.0` |
| `calibration.state` | string | 是 | `"uncalibrated"` / `"calibrated"` |
| `calibration.calibrationId` | string/null | 否 | 校准记录 ID |
| `operator` | string/null | 否 | 操作员 |
| `station` | string/null | 否 | 工位 |
| `notes` | string/null | 否 | 备注 |
| `checksums.rawSha256` | string | 是 | 原始文件哈希 |

### 3.2 示例

```json
{
  "schemaVersion": "1.0",
  "scanId": "20261104_153000_0001",
  "sequence": 1,
  "capturedAtUtc": "2026-11-04T15:30:00.123Z",
  "dataOrigin": "device",
  "appVersion": "0.1.0",
  "device": {
    "name": "GM-IX",
    "model": "GM-IX",
    "serial": null,
    "firmware": null,
    "sdkVersion": "0.1.0",
    "hostIp": "192.168.5.55",
    "deviceIp": "192.168.5.20"
  },
  "acquisition": {
    "sampleRateHz": 50000000,
    "sampleRateMhz": 50,
    "acqTimeMs": 8,
    "segCount": 1,
    "triggerMode": 0,
    "triggerDelayMs": 0,
    "channelMap": { "p": 0, "s": 1, "aux": 2, "ignored": [3] },
    "expectedFrames": 400000,
    "bufferBytes": 3200000,
    "bytesPerFrame": 8
  },
  "configSnapshot": {
    "interface": {},
    "afe": {},
    "range": {},
    "functional": {},
    "hash": "sha256:..."
  },
  "data": {
    "rawFile": "raw.bin",
    "dtype": "int16_le",
    "byteOrder": "little",
    "interleavedChannels": 4
  },
  "quality": {
    "shortRead": false,
    "frameAligned": true,
    "saturation": { "ch0": 0, "ch1": 0, "ch2": 0, "ch3": 0 },
    "zeroCounts": { "ch0": 0, "ch1": 0, "ch2": 13, "ch3": 0 },
    "channelStats": [
      { "ch": 0, "min": -1026, "max": 63, "mean": -408.2, "std": 169.5, "rms": 442.1 }
    ]
  },
  "timing": { "openMs": 124, "readMs": 50, "totalMs": null },
  "calibration": {
    "distScale": 1.0,
    "state": "uncalibrated",
    "calibrationId": null
  },
  "operator": null,
  "station": null,
  "notes": null,
  "checksums": { "rawSha256": "sha256:..." }
}
```

---

## 4. `ofdr.json`：处理结果

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | `"1.0"` |
| `scanId` | string | 是 | 关联采集 |
| `processor.name` | string | 是 | `"ofdr"` |
| `processor.version` | string/null | 否 | 无版本资源填 `null` |
| `processor.sha256` | string | 是 | DLL 哈希 |
| `config.fsHz` | double | 是 | `sampleRateMhz × 1e6` |
| `config.distScale` | double | 是 | 与 `scan.json` 一致 |
| `config.inputLength` | int | 是 | N |
| `config.outputLength` | int | 是 | `N/2 + 1` |
| `arrays.dist.file` | string | 是 | `"dist.f64"` |
| `arrays.dist.dtype` | string | 是 | `"float64_le"` |
| `arrays.dist.count` | int | 是 | `outputLength` |
| `arrays.dist.unit` | string | 是 | `"m"` |
| `arrays.freq.file` | string | 是 | `"freq.f64"` |
| `arrays.freq.unit` | string | 是 | `"Hz"` |
| `arrays.ps.file` | string | 是 | `"ps.f64"` |
| `arrays.ps.unit` | string | 是 | `"linear_amplitude"` |
| `quality.distMonotonic` | bool | 是 | 距离轴严格递增 |
| `quality.distStepM` | double | 是 | 均匀步长 |
| `quality.freqLastHz` | double | 是 | 应为 `fs/2` |
| `quality.psPositive` | bool | 是 | 无非正幅值 |

示例：

```json
{
  "schemaVersion": "1.0",
  "scanId": "20261104_153000_0001",
  "processor": { "name": "ofdr", "version": null, "sha256": "sha256:..." },
  "config": { "fsHz": 50000000, "distScale": 1.0, "inputLength": 400000, "outputLength": 200001 },
  "arrays": {
    "dist": { "file": "dist.f64", "dtype": "float64_le", "count": 200001, "unit": "m" },
    "freq": { "file": "freq.f64", "dtype": "float64_le", "count": 200001, "unit": "Hz" },
    "ps":   { "file": "ps.f64",   "dtype": "float64_le", "count": 200001, "unit": "linear_amplitude" }
  },
  "quality": {
    "distMonotonic": true,
    "distStepM": 0.0004430647,
    "freqLastHz": 25000000,
    "psPositive": true
  }
}
```

---

## 5. `events.json`：峰 / 段 / 事件

### 5.1 字段定义

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | string | 是 | `"1.0"` |
| `scanId` | string | 是 | 关联 |
| `detector.name` | string | 是 | `"detect"` |
| `detector.sha256` | string | 是 | DLL 哈希 |
| `config.nfQ` | double | 是 | 默认 85 |
| `config.nfRadiusM` | double | 是 | 默认 0.1 |
| `config.pkBaselineWin` | int | 是 | 默认 100 |
| `config.pkMinProminenceDb` | double | 是 | 默认 10 |
| `config.pkMinDistanceM` | double | 是 | 默认 0.5 |
| `config.pkExcludeRadiusM` | double | 是 | 默认 0.1 |
| `config.pkSegPercentile` | double | 是 | 默认 95 |
| `config.pkLastSegTailM` | double | 是 | 默认 5 |
| `counts.peaks` | int | 是 | `n_peaks` |
| `counts.segments` | int | 是 | `n_segments` |
| `counts.events` | int | 是 | `n_events` |
| `peaks[].locM` | double | 是 | 峰位置 |
| `peaks[].heightDbRel` | double | 是 | `20log10(ps)` |
| `peaks[].baseDbRel` | double | 是 | 峰前基线均值 |
| `peaks[].prominenceDb` | double | 是 | 突出度 |
| `segments[].startIndex` | int | 是 | 含 |
| `segments[].endIndex` | int | 是 | 不含 |
| `segments[].noiseFloorDbRel` | double | 是 | 段内噪声顶 |
| `events[].locM` | double | 是 | 事件位置 |
| `events[].peakDbRel` | double | 是 | 峰高 |
| `events[].ilDb` | double/null | 是 | 首事件 `null` |
| `events[].rlDbRel` | double | 是 | 相对回损 |
| `events[].baseBeforeDbRel` | double/null | 是 | 首事件 `null` |
| `events[].baseAfterDbRel` | double/null | 是 | 缺失 `null` |
| `zeroPeakIncluded` | bool | 是 | 是否含零距峰 |
| `units.level` | string | 是 | `"dB_rel"` |
| `units.distance` | string | 是 | `"m"` |

### 5.2 示例

```json
{
  "schemaVersion": "1.0",
  "scanId": "20261104_153000_0001",
  "detector": { "name": "detect", "sha256": "sha256:..." },
  "config": {
    "nfQ": 85,
    "nfRadiusM": 0.1,
    "pkBaselineWin": 100,
    "pkMinProminenceDb": 10,
    "pkMinDistanceM": 0.5,
    "pkExcludeRadiusM": 0.1,
    "pkSegPercentile": 95,
    "pkLastSegTailM": 5
  },
  "counts": { "peaks": 5, "segments": 5, "events": 5 },
  "peaks": [
    { "locM": 0.000443, "heightDbRel": 147.248991, "baseDbRel": 102.55012, "prominenceDb": 44.698871 },
    { "locM": 1.169248, "heightDbRel": 130.113264, "baseDbRel": 101.00043, "prominenceDb": 29.112834 }
  ],
  "segments": [
    { "startIndex": 228, "endIndex": 2413, "noiseFloorDbRel": 103.155865 }
  ],
  "events": [
    { "locM": 0.000443, "peakDbRel": 147.248991, "ilDb": null, "rlDbRel": 147.248991, "baseBeforeDbRel": null, "baseAfterDbRel": 103.155865 },
    { "locM": 1.169248, "peakDbRel": 130.113264, "ilDb": -0.321123, "rlDbRel": 130.113264, "baseBeforeDbRel": 103.155865, "baseAfterDbRel": 102.834742 }
  ],
  "zeroPeakIncluded": true,
  "units": { "level": "dB_rel", "distance": "m" }
}
```

---

## 6. 数组文件格式

| 文件 | dtype | 字节序 | 长度 | 排列 |
|---|---|---|---|---|
| `raw.bin` | `int16` | little | `bufferBytes` | 帧交织：`[ch0,ch1,ch2,ch3]`，每帧 8 B |
| `dist.f64` | `float64` | little | `outputLength` | 距离轴，严格递增 |
| `freq.f64` | `float64` | little | `outputLength` | 频率轴，`k×fs/N` |
| `ps.f64` | `float64` | little | `outputLength` | 线性幅值 |

裸数组不带头；长度、类型、单位必须从 `ofdr.json` / `scan.json` 读取。

---

## 7. 校验规则

| 规则 | 处理 |
|---|---|
| `raw.bin` 长度 ≠ `bufferBytes` | 标记 `shortRead=true`，允许保存但禁止作为正常测量 |
| `raw.bin` 长度 % 8 ≠ 0 | `frameAligned=false`，禁止处理 |
| `dist/freq/ps` 长度 ≠ `outputLength` | 回读失败，报告文件损坏 |
| `outputLength ≠ inputLength/2+1` | 处理结果异常，拒绝入库 |
| `dist` 非严格递增 | `distMonotonic=false`，禁止作为测量结果 |
| `freqLastHz ≠ fs/2` | 标记异常 |
| 首事件 `ilDb` | 必须为 `null`，UI 显示“参考”或“N/A” |
| `rlDbRel` | 只能标 `dB_rel`，不得显示为 `dBm` |
| 零距峰 | 每次都会存在，`zeroPeakIncluded=true`，UI 应可过滤 |

---

## 8. MVP 与后续扩展

MVP 包含：

- `scan.json` + `raw.bin`；
- `ofdr.json` + `dist/freq/ps`；
- `events.json` + 可选 `events.csv`；
- 相对测量字段（`dB_rel`）；
- 文件 + JSON，无数据库。

后续扩展位：

- `calibration.psdK` / `ilSlope` / `ilIntercept`（绝对 RL）；
- `acquisition.segCount > 1`（多段，待厂商解决）；
- 数据库索引表（只存元数据/路径/关键指标）；
- PDF 报表导出。
