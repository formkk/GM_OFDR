# OFDR 检测全流程培训（上位机开发者版）

> **读者**：负责 GM-IX 上位机软件开发，但不熟悉 OFDR、也没有光检测知识基础的工程师。
> **目标**：读完本文后，你能独立回答以下问题——
> 1. 设备到底在测什么？数据从哪来、到哪去？
> 2. 每个文件、每个字段为什么存在、值大概是什么范围？
> 3. 上位机在每一步要做什么校验、会踩哪些坑？
>
> **基准数据文档**：[GM-IX_数据与处理规范_v1.0.md](./GM-IX_数据与处理规范_v1.0.md)（本目录版本为学习基准；规范仍在并行会话中更新，以本目录版本为准。下文简称"规范"）。
> **实测数据来源**：`GM-IX/docs/GM-IX_三级数据处理链路数据报告.md`（下文简称"链路报告"）。本文所有具体数值均取自其中 50 MHz / 8 ms 的 g50 实测数据集，不是编造的示例值。
> **字段全集说明**：规范 v1.0 定义了组结构与各 JSON 的 v1.0 增量字段；`scan.json / ofdr.json / events.json` 的基础字段定义在其引用的 `GM-IX/docs/上位机数据Schema_v0.1.md`（下文简称"Schema v0.1"）中。本文为讲解方便会合并呈现，字段冲突时一律以规范（Part A）为准。

---

## 0. 一图看懂：数据从光到文件

```text
【物理世界】              【电/数字世界】                【文件世界】

光源发射扫频光 ──► DUT 光纤中各反射点返回光
                        │
                        ▼ (QT2144 板卡 ADC 采样)
                  raw.bin  ◄── int16 小端、4 通道交织（scan.json 描述它）
                        │
                        ▼ (上位机反交织 + 选 P/S/A + 转 double)
                  PsaArrays ◄── 内存对象，不落盘
                        │
                        ▼ (ofdr.dll：FFT 解算)
                  dist/freq/ps.f64  ◄── 三个 double 数组（ofdr.json 描述它们）
                        │
                        ▼ (detect.dll：找峰、切段、算事件)
                  events.json + baseline.f64
                        │
                        ▼ (MetricsAggregator + CalibrationModel)
                  summary.json ◄── 总 IL/RL、精度
                        │
                        ▼ (JsonAndFileStore：先 _staging，再原子提交)
                  GMIX_DATA/<groupId>/R001/ ... AVG/
```

记住这条主线：**光 → 时域电压采样 → 频谱 → 距离轴曲线 → 事件表 → 汇总指标 → 文件**。下文每个章节展开其中一环，并对应到具体的数据格式。

---

## 1. 30 分钟光学测量速成（只讲上位机开发者需要的部分）

你不需要会光学设计，但需要理解 4 个概念，否则后面的数据格式只是"一堆没有意义的字段"。

### 1.1 光纤测量在测什么

被测设备 GM-IX 是一台**光频域反射计（OFDR, Optical Frequency Domain Reflectometry）整机**。它的用途：把一根光纤（行话叫 DUT，Device Under Test，被测件）接 to 设备的光口，设备告诉你：

- 这根光纤里**哪里**有反射点（连接器、熔接点、弯折、断裂、末端）；
- 每个反射点**反射多强**（回损 RL）；
- 光**穿过**每个点后损失了多少功率（插损 IL）；
- 全程总共损失了多少。

物理背景只有一句话：光在光纤中传播时，遇到折射率不连续的地方（连接器接口、熔接点、末端断面），会有一部分光**沿原路反射回来**。反射回来的光携带了"从哪来、有多强"的信息。OFDR 的全部工作就是精确地解码这些反射光。

### 1.2 两个核心指标：IL 与 RL

| 指标 | 全称 | 直觉解释 | 数值含义 |
|---|---|---|---|
| **IL** | Insertion Loss（插损） | 光**经过**某个点后，功率掉了多少 | 损耗越大越不好；数据库中原始值 `ilDb` 为**负值**（如 `-0.32 dB`），UI 显示时取正 `stepLossDb = -ilDb` |
| **RL** | Return Loss（回损） | 某个点**反射回来**的光相对入射光的比例，衡量"这个连接有多干净" | RL 越高，反射越弱，连接质量越好（好连接器的 RL 通常 > 40 dB） |

软件注意事项：

- **相对 vs 绝对**：没有做系统级功率标定（PSD 基准）之前，我们算出的 RL 是**相对电平**（`rlDbRel`，实测值在 120–147 dB 这种"不合理"的区间），不是物理意义上的绝对回损，UI 绝不能显示为 dBm 或绝对 RL。规范 §4 中 `rlDb = null` 即表示"未标定，只能显示 `rlDbRel`"。
- **IL 对标定不敏感，RL 敏感**：IL 是两段电平的**差值**，绝对偏移会抵消；RL 是绝对电平，必须标定（规范 TBD-S5）。

### 1.3 OFDR 如何把"距离"变成"频率"（核心原理）

这是全文最重要的物理概念，用软件思维讲一遍：

1. **光源是一个"线性调频信号发生器"**：GM-IX 内部光源发出的光，频率随时间线性上升（就像雷达的 chirp）。已知：上扫周期 10 ms，有效窗口 1–9 ms（规范 §3.1，厂商固定值）。

2. **反射光是"延迟的副本"**：光传到距离 z 处的反射点再返回，飞行时间 τ = 2z/v（v 是光在光纤中的速度）。返回时，光源已经扫到了更高的频率。

3. **混频出拍频**：返回光与本地的参考光在探测器上叠加，产生一个稳定的差频（拍频）：
   ```text
   f_beat = γ × τ = γ × 2z / v        （γ = 光源扫频速率）
   ```
   **拍频与反射点距离严格成正比。** 这就是把"距离"编码成"频率"的关键一步——和雷达测距、FMCW 汽车毫米波雷达是同一个数学。

4. **FFT 解码**：探测器输出的拍频电信号被 ADC 以 50 MHz 采样 8 ms，得到 40 万个时域点。对这个信号做 FFT：
   - 第 k 个频率 bin 对应频率 `freq[k] = k × fs / N`（fs = ADC 采样率，N = 采样点数）；
   - 每个 bin 对应一个距离 `dist[k]`（正比于频率）；
   - 每个 bin 的幅值 `ps[k]` = 该距离上反射强度。

   **这就是 dist.f64、freq.f64、ps.f64 三个数组的物理来源**：一次 FFT 把"时域拍频信号"变成"距离轴上的反射强度曲线"。

### 1.4 从原理直接推出的软件铁律

这三条不是经验，是上面原理的必然推论，请当作硬约束记住：

| 铁律 | 原理依据 | 对应实测证据（链路报告） |
|---|---|---|
| **距离轴不能由采样率推算** | dist 的比例系数是 γ×2/v，γ 是光源参数，厂商未公开，与 ADC 的 fs 无关 | 同一份数据把 fs 从 50 改 25 MHz：dist 完全不变；同参数不同采集，dist 步长在 4.43e-4 ~ 4.95e-4 m 之间变化 |
| **输出长度是 N/2+1，不是 N** | 实信号 FFT 的单边频谱：共轭对称去掉一半，保留 DC 与 Nyquist | N=400,000 → 输出 200,001 |
| **`dist_scale` 是距离轴的纯比例系数** | 标定就是修正 γ×2/v 的系统误差 | dist_scale=2.0 时 dist 末点与步长全部 ×2，freq/ps 不变 |

另外，干涉仪把光信号转换成**三路电信号 P / S / A**，由 ADC 的前三个通道同步采样（ch3 不使用）。P/S/A 的具体光学含义属于厂商内部实现，上位机只需要知道：

- **映射固定**：`ch0=P、ch1=S、ch2=A`（厂商已确认，规范与链路报告一致）；
- **映射不能错**：实测只把 aux 从 ch2 改成 ch3，dist 末点从 88.61 m 变成 52.14 m、峰数从 5 变成 1——错误映射不会报错，只会给出"看起来正常但完全错误"的结果。

---

## 2. GM-IX 设备与数据链路总览

### 2.1 设备形态

- 对外接口只有 2 个：**1 个千兆 RJ45 采集网口**（固定 IP `192.168.5.20`，主机如 `192.168.5.55`）+ **1 个接 DUT 的光口**；
- 无外触发输入，**软件触发（trigger=0）是唯一触发方式**；
- "单路"指一条 DUT 测量光路，**不是单个 ADC 通道**——内部仍有 P/S/A 多路信号；
- 内部链路：QT2144 采集板 → `qt_acq` → `ofdr` → `detect` 三级软件（均为厂商提供的 C ABI DLL）。

### 2.2 三级软件链路与"前置转换"

| 级 | 模块 | 输入 → 输出 | 是否碰硬件 |
|---|---|---|---|
| 1 | `qt_acq.dll`（采集） | 板卡 → int16 原始采样字节流 | 是 |
| — | **前置转换**（上位机自研） | byte[] → P/S/A 三路 double[] | 否 |
| 2 | `ofdr.dll`（OFDR 解算） | P/S/A → dist/freq/ps | 否，纯计算 |
| 3 | `detect.dll`（事件检测） | dist + ps → 峰/段/事件/IL/RL | 否，纯计算 |
| — | MetricsAggregator + CalibrationModel（自研） | 事件 + 基线 + 区间 → summary | 否 |
| — | JsonAndFileStore（自研） | 内存模型 → 磁盘文件 | 否 |

注意：`ofdr` 与 `qt_acq` 之间没有依赖；`detect` 只消费 `ofdr` 的输出。"前置转换"和后两级编排是**上位机自己的代码**（规范 Part B §8：当前"未形成正式服务"，是要落地的部分）。

### 2.3 一次测量的目录结构（规范 §2）

**一个 `<groupId>/` 目录 = 一次测量组**。N=1（单次）与 N>1（重复平均）结构一致，只是有没有 `AVG/`：

```text
GMIX_DATA/                     # 数据根目录
├─ _staging/                   # 暂存区：未完成采集/处理，启动时清理
│  └─ <groupId>/               # 失败的组留在这里，不进最终目录
├─ _calibration/               # 全局校准库
│  └─ <calibrationId>/calibration.json
└─ GR-20261105-1530-001/       # 一次测量组
   ├─ group.json               # 组元数据：平均次数、scanIds、校准引用
   ├─ R001/                    # 第 1 次采集 = 完整"证据包"
   │  ├─ scan.json             # 采集元数据：设备、参数、质量、会话
   │  ├─ raw.bin               # 原始数据：int16 小端、4 通道交织
   │  ├─ ofdr.json             # 处理元数据：fs、distScale、N、输出长度
   │  ├─ dist.f64 / freq.f64 / ps.f64   # 三个 double[]，长度 N/2+1
   │  ├─ baseline.f64          # 可选：基线数组
   │  ├─ events.json           # 峰/段/事件与相对 IL/RL
   │  ├─ summary.json           # 总 IL/RL、精度、分析区间
   │  └─ calibration.json      # 本次实际使用的校准快照（不可变）
   ├─ R002/ ... R00N/          # N>1 时才有
   └─ AVG/                     # 仅 N>1 时生成；内部布局与 R001 相同
```

ID 命名规则（规范 §2.1，业务逻辑只读字段、**不得解析 ID 字符串**）：

```text
groupId   = GR-YYYYMMDD-HHMM-NNN      # 组，精确到分钟
scanId    = SC-YYYYMMDD-HHMMSS-NNN    # 单次采集，精确到秒
avgScanId = AV-YYYYMMDD-HHMMSS-NNN    # 平均结果
calibrationId = CAL-YYYYMMDD-NNN      # 校准版本
序号 001–999；同桶超 999 条阻塞排队，不使用 4 位序号
```

存储量级（规范 §2.3）：50 MHz/8 ms 单次约 **9.6 MB**（raw 3.2 MB + 四个派生 f64 数组合计 6.4 MB），N=5 重复合计约 48 MB。250 MHz 长采集必须配保留策略（`saveRaw / keepRepeats / retentionDays`），否则磁盘增长很快。

---

## 3. 全流程数据走读（核心章节）

本章以 **g50 运行样例**贯穿：`50 MHz / 8 ms / 1 段`，总帧数 N = 400,000，实测 DUT 上有 5 个反射点（0 m 零距峰 + 1.169 / 3.337 / 6.514 / 8.686 m）。每一步讲三件事：**这一步做什么、数据长什么样、上位机必须校验什么**。

### 3.1 第 1 级：采集 → `raw.bin` + `scan.json`

#### 做什么

调用 `qt_acq` 的 C ABI，顺序固定（链路报告 §2.1）：

```text
qt_acq_open(0)                                  # 打开设备，实测耗时 118–124 ms
→ qt_acq_configure(rate, ms, segs, trigger, delay)   # 50, 8, 1, 0(软件触发), 0
→ qt_acq_calc_buffer_bytes(rate, ms, segs)      # 计算所需缓冲区大小
→ qt_acq_start()
→ qt_acq_read(buffer, capacity)                  # 整块阻塞读取，无超时
→ qt_acq_stop() → qt_acq_close()
```

#### 字节格式（`raw.bin`）

每帧固定 8 字节 = 4 通道 × int16（2 字节）小端交织：

```text
帧 i 的字节偏移 = i × 8
+0..1  ch0 (P)   int16 little-endian
+2..3  ch1 (S)
+4..5  ch2 (A)
+6..7  ch3 (不用)

第 c 通道第 i 个采样：sample_index = i×4 + c
value = int16le(raw[8i + 2c], raw[8i + 2c + 1])
```

实测前 64 字节（g50）：`E3 FD 33 FC 60 02 99 00 ...`
解码第 0 帧：ch0 = 0xFDE3 → **-541**，ch1 = 0xFC33 → **-973**，ch2 = 0x0260 → 608，ch3 = 0x0099 → 153。这些就是 ADC 采到的三路拍频电信号 + 一路未用信号。

**长度公式（已实测验证）**：

```text
每段帧数 = rate_mhz × acq_time_ms × 1000
总帧数   = 每段帧数 × seg_count
总字节 L = rate_mhz × acq_time_ms × 8000 × seg_count    （8000 = 1000ms→s × 4通道 × 2字节）

g50：L = 50 × 8 × 8000 = 3,200,000 字节，N = 400,000 帧
```

#### `scan.json` 记录什么（Schema v0.1 基础字段 + 规范 §3 组字段）

它回答规范 Part B §6 的追溯问题："谁采的、怎么采的、什么时候"。关键片段（字段全集见 Schema v0.1 §3.1 与规范 §3）：

```jsonc
{
  "schemaVersion": "1.0",
  "scanId": "SC-20261105-153022-001",
  "session": {                        // ← v1.0 新增组字段（规范 §3）
    "groupId": "GR-20261105-1530-001",
    "repeatIndex": 1,                  // 组内序号 1..N；AVG 为 null
    "isAverage": false,
    "averageCount": 5,
    "avgMode": "linear_ps",
    "runMode": "single",
    "analysisRange": { "startM": 0.05, "endM": 20.0, "guardM": 0.1 },
    "rlMode": "peak_only",
    "zeroPeakExcluded": true
  },
  "device": { "name": "GM-IX", "sdkVersion": "0.1.0",
              "hostIp": "192.168.5.55", "deviceIp": "192.168.5.20" },
  "acquisition": {
    "sampleRateHz": 50000000, "sampleRateMhz": 50,
    "acqTimeMs": 8, "segCount": 1,
    "triggerMode": 0,                 // 软件触发，唯一可用方式
    "channelMap": { "p": 0, "s": 1, "aux": 2, "ignored": [3] },
    "expectedFrames": 400000, "bufferBytes": 3200000, "bytesPerFrame": 8,
    // v1.0 新增（规范 §3.1，厂商固定参数）：
    "sweepPeriodMs": 10,              // 光源上扫周期
    "windowStartMs": 1,               // 有效窗口起点（推荐）
    "windowEndMs": 9                  // 有效窗口终点 → 有效 8 ms
  },
  "quality": { "shortRead": false, "frameAligned": true, "saturation": {...} },
  "calibration": { "distScale": 1.0, "state": "uncalibrated", "calibrationId": null },
  "checksums": { "rawSha256": "sha256:..." }
}
```

**关于 10 ms / 1–9 ms 窗口**：光源每 10 ms 完成一次上扫，但扫频的首尾边缘不理想，只有中间 1–9 ms（8 ms）是有效数据。所以推荐配置是 50 MHz / 8 ms、窗口起点 1 ms。250 MHz / 10 ms 会覆盖整个扫频周期（含边缘），不适合与厂商 UI 对齐。

#### 上位机必须校验什么（规范 Part B §4）

| 校验 | 规则 | 不通过怎么办 |
|---|---|---|
| 长度 | `out_len == bufferBytes` | 拒绝，标记 `shortRead=true`，不得进入处理 |
| 帧对齐 | `out_len % 8 == 0` | 拒绝，标记 `frameAligned=false` |
| 饱和 | 各通道无 -32768/32767 | 计入 `quality.saturation`，超阈值应告警 |
| 质量统计 | 每通道 min/max/mean/std | 存入 `quality.channelStats`（g50 实测 ch0 mean=-408.2 等） |

**进程隔离（本级最重要的工程约束）**：`qt_acq_read` 整块阻塞、**无超时、无取消**，`stop` 也无法中断阻塞中的 read（且 seg_count≥2 会让 read 永久挂起）。因此：

- `read` 必须放在**独立进程**（可强杀的执行单元）中调用，UI 线程禁止直接 P/Invoke；
- 上位机要实现**外部看门狗**：超时 → 强杀采集进程 → 重新 open；
- UI 必须明确禁用 `segCount > 1`（多段不可用，见本文 §5 坑 7）。

### 3.2 前置转换 → PsaArrays（内存对象，不落盘）

#### 做什么

从 `raw.bin` 的字节流中拆出 P/S/A 三路并转 double（规范 Part B §3.2）：

```text
P[i] = double(samples[i × 4 + 0])     // ch0
S[i] = double(samples[i × 4 + 1])     // ch1
A[i] = double(samples[i × 4 + 2])     // ch2
i = 0 .. N-1
```

#### 两条不容商量的规则

1. **不做任何换算**：int16 直接转 double，不乘 offset/gain/factor。厂商没有给码值到物理量的公式；相对测量（距离、事件、相对 IL）不需要这个换算（规范 Part B §2："不做标定系数猜测、不写 dB"）。
2. **长度不一致直接报错**：三路必须等长，不截断、不补齐。

这一步的产物 `PsaArrays` 只在内存中存在，是 `ofdr.dll` 的输入。它不落盘——但描述它的参数（fs、N、通道映射）都记录在 `scan.json` 中，所以 `raw.bin` + `scan.json` 可以完整重建它。

### 3.3 第 2 级：OFDR 解算 → `dist/freq/ps.f64` + `ofdr.json`

#### 做什么

```c
OfdrConfig cfg = { fs = 50e6, dist_scale = 1.0 };
OfdrResult r = ofdr_process(P, S, A, N, &cfg);
// r.dist / r.freq / r.ps：长度 Lout = N/2 + 1，由 DLL 分配
// 用完必须 ofdr_result_free(&r)
```

#### 输出三数组（g50 实测值）

| 数组 | 公式/含义 | g50 实测 |
|---|---|---|
| `freq.f64` | `freq[k] = k × fs / N`，唯一可由参数直接计算的轴 | 0 → 25 MHz，步长 **125 Hz**，末点 = fs/2 |
| `dist.f64` | 反射点距离，严格递增、单次内等间距；**不能由 fs 推算** | 0 → 88.6129 m，步长 **4.4306e-4 m**（同参数另两次采集为 4.56e-4 / 4.95e-4） |
| `ps.f64` | 各距离上反射强度，**线性幅值**（未取 dB、未归一化） | 最大 23,038,253.1 @ 0.000443 m（零距峰） |

到这一步，**物理世界已经变成了软件最好懂的东西：一条"距离-强度"曲线**。`dist[k]` 是 x 轴，`ps[k]` 是 y 轴。

**内存所有权**：`dist/freq/ps` 由厂商 DLL 分配，上层用完必须立即**复制到托管 double[] 并调用 `ofdr_result_free`**（规范 Part B §5），厂商指针不得向上层传递。

#### `ofdr.json` 记录什么（Schema v0.1 §4）

```jsonc
{
  "schemaVersion": "1.0",
  "scanId": "SC-20261105-153022-001",
  "processor": { "name": "ofdr", "version": null, "sha256": "sha256:..." },
  "config": { "fsHz": 50000000, "distScale": 1.0,
              "inputLength": 400000, "outputLength": 200001 },   // N/2+1
  "arrays": {
    "dist": { "file": "dist.f64", "dtype": "float64_le", "count": 200001, "unit": "m" },
    "freq": { "file": "freq.f64", "dtype": "float64_le", "count": 200001, "unit": "Hz" },
    "ps":   { "file": "ps.f64",   "dtype": "float64_le", "count": 200001, "unit": "linear_amplitude" }
  },
  "quality": { "distMonotonic": true, "distStepM": 0.0004430647,
               "freqLastHz": 25000000, "psPositive": true }
}
```

注意 `.f64` 是**裸数组文件**（无文件头），长度、类型、单位必须从 `ofdr.json` 读取——回读代码不能假设、必须校验（长度 ≠ `count` → 报文件损坏，不得静默补齐）。

#### 上位机必须校验什么

| 校验 | 规则 |
|---|---|
| 输出长度 | `outputLength == N/2 + 1`，否则拒绝、标记结果损坏 |
| 距离轴 | `dist` 严格递增（`distMonotonic`），否则不得作为测量结果 |
| 频率轴 | `freqLastHz == fs/2`（50 MHz → 25,000,000），不符标记异常 |
| 幅值 | `ps` 无非正值（`psPositive`） |
| 输入约束 | 三路指针非空、等长、len>0；长度不必是 2 的幂（400,000 也可） |

### 3.4 第 3 级：检测 → `events.json`（+ 可选 `baseline.f64`）

#### 做什么

`detect.dll` 消费 `dist + ps` 曲线，输出峰/段/事件（配置默认值见链路报告 §4.1，如最小突出度 10 dB、相邻峰最小间距 0.5 m）。它内部完成 **`ps_db[k] = 20×log10(ps[k])`**。

> **dB 只转一次**：线性→dB 的转换发生在 detect 内部。上位机拿到的事件指标已经是 dB 域，**不得再次取对数**；`baseline.f64` 也是 dB 域（来自 `detect.baseline_db`）。

#### 关键计算关系（已实测逐值确认，链路报告 §4.3）

```text
peak_height_db = ps_db[峰索引]                  → 即该点反射强度（相对 RL）
peak_base_db   = 峰前 100 点基线均值            → 该点附近的"地板"
prominence_db  = peak_height_db - peak_base_db  → 峰突出度

RL  = peak_height_db                            → 事件回损（相对）
IL  = 后一段噪声顶 - 前一段噪声顶                → 事件插损（负值）
段噪声顶 = 该段内 ps_db 的 95 分位
```

#### g50 实测结果（这就是 `events.json` 的真实内容）

峰（5 个）：

| # | dist (m) | heightDbRel | baseDbRel | prominenceDb | 含义 |
|---|---:|---:|---:|---:|---|
| 0 | 0.000443 | 147.25 | 102.55 | 44.70 | **零距峰**（设备内部连接器反射，每次都有） |
| 1 | 1.169248 | 130.11 | 101.00 | 29.11 | DUT 反射点 1 |
| 2 | 3.337607 | 120.33 | 100.62 | 19.71 | DUT 反射点 2 |
| 3 | 6.513938 | 142.36 | 100.19 | 42.17 | DUT 反射点 3 |
| 4 | 8.686284 | 133.35 | 98.23 | 35.11 | DUT 反射点 4（很可能是光纤末端） |

事件（5 个，峰+前后噪声顶组合）：

| # | dist (m) | RL (dB_rel) | IL (dB) | base_before | base_after | 解读 |
|---|---:|---:|---:|---:|---:|---|
| 0 | 0.000443 | 147.25 | **null** | null | 103.16 | 零距峰，首事件无前段，IL 必为 null |
| 1 | 1.169248 | 130.11 | -0.321 | 103.16 | 102.83 | 过了此点曲线整体降 0.32 dB |
| 2 | 3.337607 | 120.33 | -0.547 | 102.83 | 102.29 | |
| 3 | 6.513938 | 142.36 | -1.908 | 102.29 | 100.38 | |
| 4 | 8.686284 | 133.35 | -19.079 | 100.38 | 81.30 | 曲线暴跌 19 dB → 光到此终结 |

#### `events.json` 字段要点（Schema v0.1 §5 + 规范 §4）

- `peaks[].locM / heightDbRel / baseDbRel / prominenceDb`：峰表；
- v1.0 新增：`peaks[].peakNo`（显示编号）、`distanceM`（= locM，UI 用）、`zeroPeak`（零距峰标记）；
- `segments[].startIndex/endIndex/noiseFloorDbRel`：段表（起止索引，end 不含）；
- `events[].locM / peakDbRel / ilDb / rlDbRel / baseBeforeDbRel / baseAfterDbRel`：事件表；
- v1.0 新增：`events[].stepLossDb = -ilDb`（UI 显示正损耗）、`rlDb`（标定后绝对回损，**未标定必须为 null**）、`zeroPeak`；
- `display.peakCount / zeroPeakExcluded`：UI 过滤后的显示峰数。

#### 上位机必须处理的三件事

1. **首事件 `ilDb` / `baseBeforeDbRel` 必须是 `null`**（JSON 禁止 NaN，规范 §1）；UI 显示"N/A"或"参考"。
2. **零距峰**：每次测量都存在，默认 `zeroPeakExcluded=true` 不显示，但**数据必须保留**（规范 Part B §4）。
3. `counts` 与峰/段/事件数组长度必须一致，否则拒绝。

### 3.5 指标聚合 → `summary.json`

#### 做什么

`MetricsAggregator`（已实现，`GM-IX/src/Gmix.Data/MetricsAggregator.cs`）把事件表压缩成整条链路的最终指标。公式为**自有口径**（规范 §5.1；与厂商 UI 数值不保证一致，厂商公式仍是 TBD-S1~S6）：

```text
设电平 L = 20log10(ps)，功率 P = 10^(L/10)

总回损（rlMode = peak_only）：
  P_peak = Σ 10^(peakHeightDb_i / 10)          # 区间内各峰功率求和
  totalRlRawDb = 10 × log10(P_peak)             # 功率求和后回 dB

总回损（rlMode = baseline_plus_peak）：
  totalRlRawDb = 10 × log10(P_peak + P_base)    # 加上基线功率（需 baseline.f64）

绝对回损：totalRlDb = totalRlRawDb + rlOffsetK  # 未标定时为 null

区间插损：
  totalIlRawDb = median(L_start) - median(L_end)  # 分析区间两端电平中位数之差
  totalIlDb = ilSlope × totalIlRawDb + ilIntercept # 标定后；未标定为 null
```

**为什么功率求和再回 dB**：多个反射点的回波功率在线性域叠加，直接把各峰 dB 相加是错的。这是 dB 域运算的经典陷阱——**dB 相加 = 功率相乘**。

#### `summary.json` 关键字段（规范 §5）

```jsonc
{
  "schemaVersion": "1.0",
  "scanId": "SC-20261105-153022-001",
  "groupId": "GR-20261105-1530-001",
  "analysisRange": { "startM": 0.05, "endM": 20.0 },
  "rlMode": "peak_only",
  "averageCount": 5,
  "totalIlRawDb": 21.4,          // 未标定区间插损（必填）
  "totalIlDb": 11.4,             // 标定后；null 表示未标定
  "totalRlRawDb": 147.8,         // 未标定总回损（必填）
  "totalRlDb": null,             // 未标定时 null，UI 只能显示 raw
  "ilPrecisionDb": null,         // 仅 AVG/ 填（见 §3.7），Rxxx 内保持 null
  "rlPrecisionDb": null,
  "peakCount": 5, "eventCount": 5,
  "warnings": []
}
```

调用条件（规范 Part B §3.5）：校准 `configHash` 不匹配时 `totalIlDb/totalRlDb = null` 并写 warning；`rlMode` 决定仅峰值/基线+峰值；缺 `baseline.f64` 时"基线+峰值"退化为"仅峰值"并写 warning。

### 3.6 校准 → `calibration.json`

校准回答："这条曲线的 x 轴（距离）和 y 轴（电平）准不准？"

| 字段 | 作用 | 当前值 |
|---|---|---|
| `distScale` | 距离轴比例修正（对应原理中的 γ×2/v 系统误差） | MVP 默认 1.0；标定流程待定（TBD-S9，P0） |
| `rlOffsetK` | 绝对回损偏移 dB（对应公式 `totalRlDb = totalRlRawDb + rlOffsetK`） | 待标定（TBD-S5，P0）；未标定时 `rlDb` 全为 null |
| `ilSlope` / `ilIntercept` | 插损标定：`totalIlDb = slope × raw + intercept` | 实机标定记录：slope=0.5361（厂商截图值）、intercept=-0.1 |
| `configHash` | 采样率/AFE/量程/通道组合的哈希 | **不匹配 = 旧校准失效**，不得应用（TBD-S12） |
| `calibrationId` | 唯一 ID，`CAL-YYYYMMDD-NNN` | 引用链：scan.json → calibrationId → _calibration 库 |

每个 `Rxxx/` 与 `AVG/` 内都复制一份**不可变快照** `calibration.json`——即使全局库后来更新了，旧结果依然能解释"当时用了什么校准"。

### 3.7 组与平均 → `group.json` + `AVG/`

多次重复采集后求平均，用于提升信噪比与给出"精度"指标。**规则（规范 §2.2）**：

1. 平均在**线性 ps 域**进行：`ps_avg[k] = mean(ps_i[k])`。**不能在 dB 域平均**（dB 平均 = 几何平均，会系统性低估），也不能对多次 IL/RL 结果再平均；
2. 先校验各次 `dist` 轴：完全一致 → `same_dist`；轻微差异 → 插值到参考轴并标记 `resampled`；明显不一致 → **拒绝整组**；
3. **只对平均后的 ps 做一次 detect**；
4. 某次重复失败：`validCount < averageCount`，仍可出 AVG，但标记"不完整"；`configHash`/校准不一致 → 整组拒绝；
5. N=1 时**不生成** `AVG/`（避免重复存相同数据）。

**精度规则（自有口径，规范 §2.2 / TBD-S8）**：

```text
仅 AVG/summary.json 填 ilPrecisionDb / rlPrecisionDb
= 有效重复各自 totalIlDb / totalRlDb 的样本标准差（分母 N-1）
validCount < 2 或任一值为 null → 精度填 null（禁止写 0）
各 Rxxx/summary.json 的精度字段恒为 null
precisionMode = "sample_std"，precisionCount = validCount
```

`group.json` 示例（规范 §2.2）：

```json
{
  "groupId": "GR-20261105-1530-001",
  "averageCount": 5,
  "scanIds": ["SC-20261105-153022-001", "...", "SC-20261105-153030-001"],
  "validCount": 5,
  "rejected": [],
  "avgMode": "linear_ps",
  "alignment": "same_dist",
  "avgScanId": "AV-20261105-153032-001",
  "calibrationId": "CAL-20261105-001"
}
```

### 3.8 持久化契约：`_staging` 与原子提交（规范 Part B §7）

- 一次成功测量 = 一个 `<groupId>/` 目录；**先写 `_staging/<groupId>/`，整组处理完成后原子重命名提交**；
- 写入顺序：各 `Rxxx/` 数组 → 元数据 → summary → `AVG/` → `group.json` **最后提交**；
- 失败/中断/强杀：留在 `_staging/`，不产生最终目录；启动时清理；
- JSON 只放元数据，数组用 `.f64`，原始数据用 `.bin`；
- `saveRaw` 可配置（`off` 时 `rawFile=null`、`rawSha256=null`，派生数组仍保存）；`keepRepeats` 可配置，但 `group.json` 与 `AVG/` 必须保留。

**"原子性"的软件含义**：最终目录里的数据要么完整、要么不存在，用户永远不会看到"写了一半"的测量结果。

---

## 4. 上位机软件的工作过程（把数据串成程序）

### 4.1 一次"点击测量"的完整时序（N 次重复）

```text
用户点击"测量"（N=5）
  │
  ├─ 1. 生成 groupId = GR-...（序号管理）
  ├─ 2. 建立 _staging/<groupId>/
  ├─ 3. 循环 i = 1..N：
  │     ├─ 启动/复用采集进程（独立进程！）
  │     │    open → configure → calc → start → read        [~118+50 ms]
  │     │    （看门狗计时：超时 → 强杀 → 重启采集进程）
  │     ├─ 校验 out_len == bufferBytes、%8==0              [不通过 → 本次失败]
  │     ├─ 质量统计（饱和/零值/通道统计）→ scan.json
  │     ├─ 写 raw.bin + 哈希
  │     ├─ 反交织 → P/S/A double[N]（×3）
  │     ├─ ofdr_process → 复制 → free                       [~30 ms]
  │     │    校验 N/2+1、dist 单调、freqLast
  │     ├─ 写 dist/freq/ps.f64 + ofdr.json
  │     ├─ detect_process → 复制 → free
  │     │    校验 counts、首事件 null；写 baseline.f64（可选）
  │     ├─ 应用校准（configHash 匹配才应用）→ summary.json
  │     └─ 写 R00i/ 完整证据包 + calibration.json 快照
  ├─ 4. 平均（N>1）：校验 dist 轴 → 线性 ps 平均 → 一次 detect → AVG/
  │     + 精度（样本标准差）→ AVG/summary.json
  └─ 5. 写 group.json → 原子重命名 _staging/<groupId> → GMIX_DATA/<groupId>
        UI 刷新：曲线 + 事件表 + 总指标
```

性能量级（实测）：设备已打开时单次 pipeline 总耗时约 **105–125 ms**（open 118–124 ms、read 50 ms、ofdr 30–31 ms 均为首末实测）；冷启动首次约 225–250 ms。N=5 的测量组约需 1 秒量级。

### 4.2 线程与进程模型（规范 Part B §5）

| 规则 | 原因 |
|---|---|
| `read` 只在独立采集进程调用，UI 线程**禁止** P/Invoke | read 无超时、无取消；seg≥2 会永久挂起，只能强杀进程恢复 |
| 厂商 DLL 分配的 dist/freq/ps/baseline 在处理层**立即复制并释放** | 跨 DLL 边界的内存所有权；防止泄漏与野指针 |
| 处理层（ofdr/detect/聚合）是纯计算：不读文件、不碰 UI、不做 IO | 可测试、可回放、可并行 |
| UI 只读 `ScanBundle` 模型，大数组按屏幕像素**抽稀**显示 | 200,001 点直接画线既卡又看不清 |

### 4.3 UI 显示哪些数据（与文件对应）

| UI 区域 | 数据来源 | 注意 |
|---|---|---|
| 距离域曲线（主视图） | `dist.f64` + `ps.f64`（或 baseline.f64） | y 轴电平只能标 `dB_rel`；抽稀显示 |
| 事件表 | `events.json` | 零距峰默认过滤（`zeroPeakExcluded`）；首事件 IL 显示"N/A"；IL 用 `stepLossDb`（正值） |
| 链路指标 | `summary.json` | `totalIlDb/totalRlDb` 为 null 时只显示 raw 值并标注"未标定" |
| 精度（N>1） | `AVG/summary.json` | `ilPrecisionDb/rlPrecisionDb`；null 表示样本不足 |
| 视图状态 | `view.json`（可选） | 只存 UI 状态（缩放、游标、选中峰），不影响测量语义 |

### 4.4 错误契约速查（规范 Part B §4）

核心原则：**任何异常不得 `catch {}` 静默吞掉，必须分类上报**；失败数据留在 `_staging/`，不产生最终目录。

| 场景 | 处理 |
|---|---|
| `out_len != bufferBytes` | 拒绝，标记 `shortRead` |
| `out_len % 8 != 0` | 拒绝，`frameAligned=false` |
| P/S/A 长度不一致 | 抛错，不截断 |
| `outputLength != N/2+1` | 拒绝，标记结果损坏 |
| `dist` 非严格递增 | 拒绝，不作为测量结果 |
| `freqLast != fs/2` | 标记异常 |
| 缺 `baseline.f64` | 基线+峰值退化为仅峰值，写 warning |
| 校准 `configHash` 不匹配 | 不应用绝对 IL/RL（置 null），写 warning |
| 零距峰 | 保留数据，默认过滤显示 |
| JSON 数值缺失 | 用 `null`，禁止 `NaN` |

---

## 5. 上位机开发者最容易踩的 14 个坑

全部来自实测教训（链路报告 + 项目记忆），按"血泪程度"排序：

| # | 坑 | 正确做法 |
|---|---|---|
| 1 | 按 N 预分配 ofdr 输出、按索引对齐 | 输出固定 **N/2+1**；输出与输入不是 1:1，不可按索引对齐 |
| 2 | 对 ps 再做一次 20log10 | detect 内部已转 dB，**只转一次** |
| 3 | 用 fs 推算 dist 步长/量程 | dist 由数据决定，**直接消费 dist 数组**；dist_scale 才是标定手段 |
| 4 | JSON 里写 NaN | JSON 不支持 NaN，一律 **null**；首事件 `ilDb=null` |
| 5 | 把零距峰当 DUT 反射点 | 每次 dist≈0 都有强峰，标记 `zeroPeak`，UI 默认排除、数据保留 |
| 6 | 在 UI 线程直接调 read | read 无超时无取消，**独立进程 + 看门狗 + 强杀重启** |
| 7 | 允许 seg_count ≥ 2 | 多段 read **永久挂起**，stop 无法中断，MVP 明确禁用 |
| 8 | 忘记 `ofdr_result_free` / `detect_result_free` | 厂商内存必须复制后立即 free；`error_msg` 要在 free 前读 |
| 9 | 在 dB 域做平均 | 平均只在线性 **ps 域**；dB 相加 = 功率相乘，是错的 |
| 10 | configHash 变了还用旧校准 | 配置（采样率/AFE/量程/通道映射）变化 → 旧校准失效，绝对 IL/RL 置 null |
| 11 | 把 rlDbRel 当绝对回损显示 | 未标定时 120–147 dB 是相对电平，不得显示 dBm/绝对 RL |
| 12 | 通道映射写死成"想当然" | 固定 ch0=P/ch1=S/ch2=A；映射错误**不报错但结果全错**（实测 aux 换通道：dist 末点 88.6→52.1 m、峰 5→1） |
| 13 | qt_acq_open 返回 0 就认为设备已连接 | 无设备时 open 仍返回 0，需用实际采集/错误通道判断 |
| 14 | 把 dist 末点（如 88.6 m）当设备最大量程对外宣传 | dist 末点是算法输出、随采集变化；已实测稳定检测最远 **8.7 m** DUT 反射峰，量程承诺以实测为准 |

---

## 6. 术语表

| 术语 | 含义 |
|---|---|
| OFDR | 光频域反射计：扫频光源 + 干涉拍频 + FFT，把反射点距离编码为频率 |
| DUT | 被测件（Device Under Test），这里指接在光口上的光纤/链路 |
| IL（插损） | 光经过某点损失的功率；`ilDb` 负值，UI 用 `stepLossDb = -ilDb` |
| RL（回损） | 某点反射光相对入射光的比例；RL 越高反射越弱、连接越好 |
| 扫频 / chirp | 光源频率随时间线性上升；上扫周期 10 ms，有效窗口 1–9 ms |
| 拍频（beat） | 返回光与本地光混频产生的稳定差频，正比于反射点距离 |
| P/S/A | 干涉仪输出的三路电信号，固定映射 ch0/ch1/ch2；ch3 不用 |
| 零距峰 | dist≈0 处设备内部连接器的反射，每次都存在，UI 默认过滤 |
| 突出度（prominence） | 峰高 - 峰前基线均值；detect 默认最小 10 dB |
| 段 / 噪声顶（noise floor） | detect 按峰切分曲线的段；段内 ps_db 的 95 分位；相邻段之差 = IL |
| dist_scale | 距离轴比例标定系数，只作用于 dist |
| configHash | 采样率/AFE/量程/通道组合哈希；变化即旧校准失效 |
| dB_rel / dBm | 相对电平 / 绝对功率；未标定只能用 dB_rel |
| FFT 单边谱 | 实信号频谱共轭对称，N 点时域 → N/2+1 点（含 DC 与 Nyquist） |
| `ScanBundle` | 一次采集全部数据（raw + 数组 + 事件 + 汇总 + 校准快照）的内存模型，UI 只读它 |

---

## 7. 延伸阅读与自查

**文档地图**（建议阅读顺序）：

1. 本文（全流程直觉 + 数据对照）
2. [GM-IX_数据与处理规范_v1.0.md](./GM-IX_数据与处理规范_v1.0.md)（数据格式唯一事实源：Part A 字段与公式，Part B 处理契约）
3. `GM-IX/docs/GM-IX_三级数据处理链路数据报告.md`（SDK 实测：C ABI、字节格式、数值验证）
4. `GM-IX/docs/上位机数据Schema_v0.1.md`（scan/ofdr/events 的基础字段定义）
5. `GM-IX/src/Gmix.Data/`（已有实现：模型、聚合、存储、校验器与回归测试）

**自查清单**（能独立回答 = 培训达标）：

- [ ] 能不看资料说出 raw.bin 一帧 8 字节的布局和总字节数公式
- [ ] 能解释为什么 ofdr 输出是 N/2+1、为什么 dist 不能用 fs 推算
- [ ] 能手算 g50 事件 1 的 IL：102.834742 - 103.155865 = -0.321123 dB
- [ ] 能说出 RL=147 dB 这个数为什么"不合理"（相对电平，未标定）
- [ ] 能说出平均为什么必须在线性 ps 域、精度为什么用样本标准差且 N<2 时为 null
- [ ] 能列出一次测量从点击到落盘的 10 个步骤与对应产物文件
- [ ] 能说出 14 个坑中至少 8 个的"正确做法"
