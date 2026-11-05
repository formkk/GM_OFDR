# GM-IX 三级数据处理链路数据报告（实测版）

> 设备：GM-IX，联讯完整单路 OFDR 整机；对外只有 **1 个千兆 RJ45 采集网口** 和 **1 个接 DUT 的光口**。
> 内部链路：QT2144 采集板 → `qt_acq` → `ofdr` → `detect`。
> 测试日期：2026-11-03　链路：1 Gbps / 全双工　主机：`192.168.5.55/24`　设备：`192.168.5.20`
> 数据与日志：`GM-IX/results/gigabit/`；本报告所有数值均来自该目录实测结果。

---

## 0. 定位与结论速览

三个 SDK 目录是 GM-IX 整机内部的三级软件链路：

| 级 | 目录 | 角色 | 输入 → 输出 | 是否接触硬件 |
|---|---|---|---|---|
| 1 | `references/qt2144-rs` | 采集/驱动 | 板卡 → 4 通道 int16 原始采样 | 是 |
| 2 | `references/ofdr-0.1.1-release` | OFDR 解算 | P/S/A 三路 double → `dist/freq/ps` | 否，纯计算 |
| 3 | `references/detect_1.0` | 事件/指标 | `dist + ps` → 峰、段、事件、IL/RL | 否，纯计算 |

关键实测事实：

1. **“单路”是一条 DUT 测量光路，不是单个 ADC 通道**。整机内部仍有 P/S/A 多路信号；上位机接口已固定为 `ch0=P、ch1=S、ch2=A`，`ch3` 不使用。
2. `qt_acq` 单段采集在 10/50/125/250 MHz 均正常，`out_len` 与公式完全一致；`seg_count >= 2` 会挂起。
3. `ofdr` 输出长度是 `N/2 + 1`，不是 `N`；`freq` 步长 `fs/N`；`dist` 步长单次内均匀但随采集数据变化，与 `fs` 无关；`dist_scale` 按比例缩放 `dist`。
4. `detect` 的 dB 换算与 IL/RL 关系已由实测数值确认（见 §5.3）。
5. 1 Gbps 链路持续吞吐最高约 68.3 MB/s（约 546 Mbps），未达线速。
6. **厂商固定采集参数**：50 MHz、光源上扫 10 ms、有效窗口 1–9 ms（8 ms）。与厂商 UI 对齐应使用 50 MHz/8 ms + 窗口起点 1 ms；250 MHz/10 ms 覆盖整个扫频周期，包含首尾边缘，不适合直接对比。

---

## 1. 端到端数据流

```text
QT2144 板卡
  │  4 通道同时采样，每帧 [ch0,ch1,ch2,ch3]
  ▼
qt_acq.dll
  │  byte[]：int16 小端、4 通道交织、每帧 8 字节
  ▼
前置转换（上位机自己实现）
  │  反交织 + 选 P/S/A + int16→double
  ▼
ofdr.dll
  │  double[N/2+1]：dist / freq / ps
  ▼
detect.dll
  │  峰 / 基线段 / 事件 / IL / RL
  ▼
上位机 UI、数据库、报表
```

`ofdr` 与 `qt_acq` 没有 DLL 依赖；`detect` 只消费 `ofdr` 的输出数据，不与硬件交互。

---

## 2. 第一级：采集 `qt2144-rs`

### 2.1 接口与调用顺序

```text
qt_acq_open(card_index=0) → qt_acq_configure(rate, ms, segs, trigger, delay)
→ qt_acq_calc_buffer_bytes(rate, ms, segs) → qt_acq_start
→ qt_acq_read(buffer, capacity) → qt_acq_stop → qt_acq_close
```

| 函数 | 参数/返回 | 实测说明 |
|---|---|---|
| `qt_acq_open` | `card_index`；成功 0 + 句柄 | 耗时 118–124 ms |
| `qt_acq_configure` | 采样率 MHz、时长 ms、段数、触发、触发延时 ms | 采样率仅 10/50/125/250；触发 0 软件 / 1 外升 / 2 外降 |
| `qt_acq_calc_buffer_bytes` | 输出所需字节数 | 24 组参数全部与公式一致 |
| `qt_acq_start` | 无 | 返回 0 |
| `qt_acq_read` | 缓冲区、容量、实际长度 | 整块阻塞，无超时；单段 `out_len == required` |
| `qt_acq_stop` | 无 | 阻塞 read 期间调用会一起挂起 |
| `qt_acq_close` | 无 | `close(NULL)` 返回 0 |
| `qt_acq_last_error` | `char* buf, size_t cap` | 返回值是**消息字节长度**，不是状态码；成功调用后仍保留旧消息 |
| `qt_acq_version` | `char* buf, size_t len` | 返回 `0.1.0` |

### 2.2 原始数据格式（字节级）

每帧固定 8 字节：

```text
帧 i 字节偏移 = i × 8
+0..1  ch0 int16 little-endian
+2..3  ch1
+4..5  ch2
+6..7  ch3
```

第 `c` 通道第 `i` 个采样：

```text
sample_index = i × 4 + c
value        = int16le(raw[8*i + 2*c], raw[8*i + 2*c + 1])
```

50 MHz / 8 ms 原始数据前 64 字节实测：

```text
E3 FD 33 FC 60 02 99 00 | F2 FD 30 FC 76 0D 8D 00 | ...
= ch0 -541 | ch1 -973 | ch2 608 | ch3 153
```

与各通道 `first8` 逐值一致，确认 int16 小端、4 通道交织。

### 2.3 长度与容量公式（已实测验证）

```text
每段帧数      = sample_rate_mhz × acq_time_ms × 1000
总帧数        = 每段帧数 × seg_count
总字节数 L    = sample_rate_mhz × acq_time_ms × 8000 × seg_count
```

推导：`8000 = 1000 (ms→s) × 4 (通道) × 2 (int16 字节)`。

常用值：

| 参数 | 总字节 L | 总帧数 |
|---|---:|---:|
| 10 MHz / 10 ms / 1 段 | 800,000 | 100,000 |
| 50 MHz / 8 ms / 1 段 | 3,200,000 | 400,000 |
| 125 MHz / 10 ms / 1 段 | 10,000,000 | 1,250,000 |
| 250 MHz / 10 ms / 1 段 | 20,000,000 | 2,500,000 |
| 250 MHz / 50 ms / 1 段 | 100,000,000 | 12,500,000 |

### 2.4 1 Gbps 实测采集结果

| 参数 | 字节数 | read 耗时 | 吞吐 | 结果 |
|---|---:|---:|---:|---|
| 10 MHz / 1 ms | 80,000 | 1 ms | — | PASS |
| 10 MHz / 10 ms | 800,000 | 17 ms | 47.1 MB/s | PASS |
| 50 MHz / 2 ms | 800,000 | 9 ms | 88.9 MB/s（小样本） | PASS |
| 50 MHz / 8 ms | 3,200,000 | 50 ms | 64.0 MB/s | PASS |
| 125 MHz / 10 ms | 10,000,000 | 158 ms | 63.3 MB/s | PASS |
| 250 MHz / 10 ms | 20,000,000 | 340 ms | 58.8 MB/s | PASS |
| 250 MHz / 50 ms | 100,000,000 | 1465 ms | **68.3 MB/s（约 546 Mbps）** | PASS |

所有单段用例 `out_len == L` 且 `L % 8 == 0`。

### 2.5 数据质量（50 MHz / 8 ms / 400,000 帧）

| 通道 | min | max | mean | std | 饱和 |
|---|---:|---:|---:|---:|---:|
| ch0 | -1026 | 63 | -408.2 | 169.5 | 0 |
| ch1 | -1512 | -288 | -941.3 | 192.7 | 0 |
| ch2 | -8224 | 7435 | -394.9 | 5441.4 | 0 |
| ch3 | 120 | 184 | 149.9 | 6.2 | 0 |

ch2 为明显拍频/大信号；ch0、ch1 低幅缓变；ch3 近直流。无 `-32768/32767` 饱和。

### 2.6 第一级限制

- `seg_count >= 2`：`read` 30–90 s 不返回，`stop` 也无法中断；只能强杀进程后重新 `open`。
- `read` 无超时、无取消；不能在 UI 线程直接调用。
- 强杀采集会话后，后续调用可能短暂挂起；本次通过其他采样率成功采集后恢复，机制未确认。
- 通道与 P/S/A 的映射不由本层提供。

---

## 3. 第二级：OFDR 解算 `ofdr-0.1.1-release`

### 3.1 前置转换（上位机自研，不属于厂商库）

#### 3.1.1 反交织与通道选择

```text
P[i] = double(samples[i × 4 + pCh])
S[i] = double(samples[i × 4 + sCh])
A[i] = double(samples[i × 4 + auxCh])
i = 0 .. N-1，N = 总帧数
```

本次测试取 `pCh=0, sCh=1, auxCh=2`。厂商回复通道映射为 **1P 2S 3A**，功能上固定为 SDK `ch0=P, ch1=S, ch2=A`，`ch3` 不使用。该映射与本次实测一致，物理丝印/连接器编号无需关心。

#### 3.1.2 int16 → double

```text
value_double = (double)value_int16
```

本阶段**未乘任何标定系数**。厂商未给码值到物理量公式（TBD-B），因此不做 offset/gain 修正。

#### 3.1.3 采样率

```text
fs_hz = sample_rate_mhz × 1,000,000
```

50 MHz → `50,000,000 Hz`；250 MHz → `250,000,000 Hz`。

### 3.2 C ABI 与数据结构

```c
typedef struct {
    double fs;           /* 采样率 Hz */
    double dist_scale;   /* 距离轴校准系数，默认 1.0 */
} OfdrConfig;

typedef struct {
    int32_t error;
    const char *error_msg;
    double *dist;        /* 长度 len */
    double *freq;        /* 长度 len */
    double *ps;          /* 线性幅值，长度 len */
    size_t len;
} OfdrResult;

OfdrConfig ofdr_config_default(void);
OfdrResult ofdr_process(const double *p, const double *s,
                        const double *aux, size_t len,
                        const OfdrConfig *cfg);
void ofdr_result_free(OfdrResult *result);
```

内存所有权：`dist/freq/ps/error_msg` 由 DLL 分配，调用方不得 `free`；用完必须 `ofdr_result_free`，读取 `error_msg` 要在释放前完成。

### 3.3 输入约束（实测）

| 约束 | 结果 |
|---|---|
| 三路指针非空 | 空指针报错 |
| `len > 0` | 0 报错 |
| 三路等长 | 必须 |
| 是否必须 2 的幂 | **否**；400,000 与 2,500,000 均接受 |
| `cfg` 非空 | 必须 |
| 输出是否等长 | **否**，见 §3.4 |

### 3.4 输出公式与参数实验

#### 3.4.1 输出长度

```text
Lout = N / 2 + 1
```

实测：`8192 → 4097`、`400,000 → 200,001`、`2,500,000 → 1,250,001`。这是单边频谱长度，包含 0 Hz 与 Nyquist。

#### 3.4.2 频率轴

```text
freq[k] = k × fs / N,  k = 0 .. N/2
```

实测验证：

- 50 MHz、N=400,000：步长 `125 Hz`，末点 `25 MHz`；
- 同一份数据把 `fs` 改为 25 MHz：步长变 `62.5 Hz`，末点 `12.5 MHz`；
- 250 MHz、N=2,500,000：步长 `100 Hz`，末点 `125 MHz`。

#### 3.4.3 距离轴

```text
dist[0] = 0
dist[k] = dist[k-1] + Δ        （单次输出内严格均匀）
Δ = dist[Lout-1] / (Lout-1)
dist_scaled[k] = dist_raw[k] × dist_scale
```

实测：

| 数据 | N | Lout | Δ | dist 末点 |
|---|---:|---:|---:|---:|
| 50 MHz（g50） | 400,000 | 200,001 | 4.4306e-4 m | 88.6129 m |
| 50 MHz（另一次采集） | 400,000 | 200,001 | 4.5596e-4 m | 91.1916 m |
| 50 MHz（首轮） | 400,000 | 200,001 | 4.9498e-4 m | 98.9967 m |
| 250 MHz（g250） | 2,500,000 | 1,250,001 | 3.8414e-4 m | 480.1691 m |

参数实验：

- `dist_scale=2.0`：`dist` 末点与 Δ 全部 ×2（88.6129 → 177.2259 m，4.4306e-4 → 8.8613e-4 m），`freq/ps` 不变；
- 同一数据 `fs` 从 50 MHz 改 25 MHz：`dist` 完全不变，`freq` 末点减半，`ps` 不变。

结论：

- `dist` 轴与 `fs` 配置无关，步长由数据本身决定（具体公式厂商未给出，TBD-G）；
- `dist_scale` 是对 `dist` 轴的纯比例系数；
- 同参数不同采集的 Δ/量程会变化，但峰位置相对稳定（见 §5.4）；
- `aux` 选择同样敏感：同一数据固定 P/S=ch0/ch1，只把 aux 从 ch2 改为 ch3，`dist` 末点由 88.6129 m 变为 52.1367 m、步长由 4.4306e-4 变为 2.6068e-4、峰数由 5 变为 1。因此 P/S/A 三者都必须按接线固定，不能只固定 P/S。

#### 3.4.4 功率谱

```text
ps[k] = 线性幅值（未取 dB、未归一化）
```

实测 `ps` 与 `detect` 的 dB 输出满足 `dB = 20 × log10(ps)`（见 §5.3）。

### 3.5 实测处理耗时

| 数据 | N | ofdr 耗时 | 输出长度 |
|---|---:|---:|---:|
| 50 MHz / 8 ms | 400,000 | 30–31 ms | 200,001 |
| 250 MHz / 10 ms | 2,500,000 | 185 ms | 1,250,001 |

---

## 4. 第三级：事件与指标 `detect-1.0`

### 4.1 接口与配置默认值

```c
DetectConfig detect_config_default(void);
DetectResult detect_process(const double *dist, const double *ps,
                            size_t len, const DetectConfig *cfg);
DetectResult detect_process_db(const double *dist, const double *ps_db,
                               size_t len, const DetectConfig *cfg);
void detect_result_free(DetectResult *result);
```

默认配置：

| 字段 | 默认值 | 含义 |
|---|---:|---|
| `nf_q` | 85 | 基线分位数（%） |
| `nf_radius_m` | 0.1 | 滑动基线窗口半径（m） |
| `pk_baseline_win` | 100 | 峰前基线窗口点数 |
| `pk_min_prominence_db` | 10 | 最小峰突出度（dB） |
| `pk_min_distance_m` | 0.5 | 相邻峰最小间距（m） |
| `pk_exclude_radius_m` | 0.1 | 峰排除窗口半径（m） |
| `pk_seg_percentile` | 95 | 段内噪声顶分位数（%） |
| `pk_last_seg_tail_m` | 5 | 最后一段尾部截去距离（m） |

输入要求（头文件）：`dist` 必须严格递增且等间距；`dist` 与 `ps` 等长；`len` 至少 2。

### 4.2 输出数据

`DetectResult` 包含：

- `ps_db`、`baseline_db`：长度 `len`；
- 峰：`n_peaks`、`peak_loc/height/prominence/base`；
- 段：`n_segments`、`seg_start/end/noise_floor`；
- 事件：`n_events`、`event_loc/peak/il/rl/base_before/base_after`。

内存同样由 DLL 分配，必须 `detect_result_free`。

### 4.3 已由实测确认的计算关系

以下关系由同一帧的线性 `ps`、detect 输出逐值比对确认：

```text
ps_db[k]        = 20 × log10(ps[k])

peak_height_db  = ps_db[peak_index]
peak_base_db    = baseline_db 在峰前 pk_baseline_win(100) 点内的均值
prominence_db   = peak_height_db - peak_base_db

RL_db           = peak_height_db
IL_db           = seg_noise_floor_after - seg_noise_floor_before
event_base_before/after = 相邻段的 seg_noise_floor
```

验证示例（g50，事件 1）：

```text
base_before = 103.155865 dB
base_after  = 102.834742 dB
IL = 102.834742 - 103.155865 = -0.321123 dB   （与输出一致）
RL = peak_height = 130.113264 dB              （与输出一致）
```

首事件没有前段，`event_il = NaN`；零距峰也是首事件。

### 4.4 实测峰与事件（50 MHz / 8 ms，g50）

峰：

| # | dist (m) | peak_height_db | peak_base_db | prominence_db | ps 线性值 |
|---|---|---|---|---|---|
| 0 | 0.000443 | 147.248991 | 102.550120 | 44.698871 | 23,038,253.1 |
| 1 | 1.169248 | 130.113264 | 101.000430 | 29.112834 | 3,203,783.9 |
| 2 | 3.337607 | 120.327878 | 100.619156 | 19.708722 | 1,038,469.88 |
| 3 | 6.513938 | 142.355969 | 100.185275 | 42.170694 | 13,115,911.4 |
| 4 | 8.686284 | 133.348064 | 98.233597 | 35.114466 | 4,649,467.08 |

事件：

| # | dist (m) | RL (dB) | IL (dB) | base_before | base_after |
|---|---|---|---|---|---|
| 0 | 0.000443 | 147.248991 | NaN | NaN | 103.155865 |
| 1 | 1.169248 | 130.113264 | -0.321123 | 103.155865 | 102.834742 |
| 2 | 3.337607 | 120.327878 | -0.547458 | 102.834742 | 102.287284 |
| 3 | 6.513938 | 142.355969 | -1.908450 | 102.287284 | 100.378834 |
| 4 | 8.686284 | 133.348064 | -19.079166 | 100.378834 | 81.299668 |

基线段：

| # | start | end（不含） | noise_floor_db |
|---|---:|---:|---:|
| 0 | 228 | 2413 | 103.155865 |
| 1 | 2866 | 7307 | 102.834742 |
| 2 | 7760 | 14476 | 102.287284 |
| 3 | 14929 | 19379 | 100.378834 |
| 4 | 19832 | 30891 | 81.299668 |

### 4.5 实测峰与事件（250 MHz / 10 ms，g250）

峰：

| # | dist (m) | peak_height_db | peak_base_db | prominence_db | ps 线性值 |
|---|---|---|---|---|---|
| 0 | 0.000384 | 164.352345 | 119.177750 | 45.174595 | 165,050,660 |
| 1 | 1.168924 | 147.011630 | 116.909349 | 30.102281 | 22,417,206.7 |
| 2 | 3.337367 | 135.330949 | 116.521925 | 18.809024 | 5,841,810.24 |
| 3 | 6.501873 | 156.103485 | 119.175105 | 36.928380 | 63,851,962.4 |
| 4 | 8.686835 | 152.264056 | 114.352771 | 37.911285 | 41,039,569.2 |

事件 IL：`NaN / -0.288436 / -0.473687 / -1.978059 / -21.805809` dB；RL 与峰高相同。

基线段噪声顶：`118.565398 / 118.276962 / 117.803275 / 115.825217 / 94.019408` dB。

### 4.6 第三级输入约束与行为

- 输入 `ps` 为线性幅值；若给 `ps_db`，用 `detect_process_db`。
- `dist` 必须严格递增且等间距，否则返回错误。
- 每次处理输出 5 个峰/事件（默认配置下，本设备当前光路）。
- 零距峰（`dist ≈ 0.0004 m`）每次都存在，接入 UI 时必须明确是否排除。
- `detect` 会自行做 `20log10`，上位机不要再次做线性→dB 转换。

---

## 5. 端到端数值样例（g50）

```text
采集：50 MHz × 8 ms × 1 段
      总字节 L = 50 × 8 × 8000 = 3,200,000 B
      总帧数 N = 400,000
原始格式：int16 小端，4 通道交织，每帧 8 B
转换：P/S/A = ch0/ch1/ch2（示例假定），转 double
ofdr：输入 N=400,000 → 输出 Lout=200,001
      freq：0 .. 25 MHz，步长 125 Hz
      dist：0 .. 88.6129 m，步长 4.4306e-4 m
      ps：线性幅值，最大 23,038,253.1 @ dist 0.000443 m
detect：5 个峰 / 5 个段 / 5 个事件
      峰 0 为 0 m 附近零距峰，IL=NaN
      后续峰位 1.169 / 3.337 / 6.514 / 8.686 m
      事件 4 IL = -19.079 dB，RL = 133.348 dB
```

---

## 6. 三级链路的数据格式对照

| 级 | 数据类型 | 长度 | 单位/字节序 | 所有者 |
|---|---|---|---|---|
| 采集 | `byte[]` / `int16[]` | `rate × ms × 8000 × segs` | int16 小端，4 通道交织 | 调用方 |
| 前置转换 | `double[]` ×3（P/S/A） | N = 总帧数 | 原始码值，无标定 | 调用方 |
| OFDR 输入 | `double[]` ×3 + `OfdrConfig` | N | `fs` 为 Hz | 调用方 |
| OFDR 输出 | 3× `double[]` | N/2+1 | `dist` m、`freq` Hz、`ps` 线性 | `ofdr.dll`，需 free |
| 检测输入 | `double[]` ×2 + `DetectConfig` | N/2+1 | `dist` m、`ps` 线性 | 调用方 |
| 检测输出 | 多个数组 | 峰/段/事件数量 | dB / m / 索引 | `detect.dll`，需 free |

---

## 7. 对上位机开发的约束（可直接作为接口设计输入）

1. **数据长度**：`ofdr` 输出与输入不是 1:1，固定 `N/2+1`；UI/数据库不能按 N 预分配或按索引对齐。
2. **距离轴**：单次输出内均匀；`dist_scale` 是纯比例系数；不要用 `fs` 推算距离步长。
3. **频率轴**：`freq[k] = k × fs / N`，是唯一可以由 `fs` 和 N 直接计算的轴。
4. **dB 只转换一次**：`detect` 内部已做 `20log10(ps)`；UI 不要重复换算。
5. **IL/RL**：`RL = peak_height_db`；`IL = 后段噪声顶 - 前段噪声顶`；首事件 `IL=NaN`，UI 必须处理 NaN。
6. **零距峰**：每次都有 `dist≈0` 的强峰，需在事件列表/指标中显式标记或排除。
7. **通道映射**：P/S/A 必须来自配置/接线确认，不能写死 `ch0=P, ch1=S, ch2=aux`；当前默认映射很可能不符合实际接线。
8. **多段**：`seg_count > 1` 在 TBD-C 关闭前应明确拒绝。
9. **采集隔离**：`read` 无超时、`stop` 不可中断，必须放在独立进程/可强杀执行单元，UI 线程不得直接调用。
10. **配置快照**：`fs`、`dist_scale`、`DetectConfig`、通道映射必须随结果保存，保证结果可复现。

---

## 8. 未确认项与 TBD 回填

| 编号 | 事项 | 本次结论 |
|---|---|---|
| TBD-15 | 板卡形态/网络协议/帧格式 | 1G 网络路径可用；帧格式已确认；走 SDK 时协议字节级不需要，MVP 关闭为内部实现细节 |
| TBD-A | P/S/A 与 4 通道映射 | 功能映射已确认：ch0=P、ch1=S、ch2=A，ch3 不使用；实测一致 |
| TBD-B | int16 → 物理量 / 绝对电平 | 相对测量（距离、事件、相对 IL）不需要；绝对 RL/光功率需要系统级标定。`qt_acq` 无换算接口；`QT_Board.dll` 有 `QT_BoardConvertCodeValueToVoltageValue` 等导出但无头文件。详见 §8.1 |
| TBD-C | 多段布局/64 B 帧头 | 多段不可用；MVP 不做多段，UI 明确禁用 `seg_count>1`，后续再验证 |
| TBD-D | 触发延时单位/语义 | 整机仅网口+光口，无外触发接口，trigger=1/2 不适用；软件触发唯一可用，`trigger_delay_ms` 是否能实现 1–9 ms 窗口仍待确认 |
| TBD-E | read 超时/取消 | 无超时；stop 不可中断。不是待厂商确认项，而是 MVP 实现约束：采集进程隔离 + 外部超时 + 强杀重启 |
| TBD-F | ofdr 输入长度约束 | 非 2 的幂可接受；已关闭 |
| TBD-G | ofdr 距离轴公式 | 公式 UI 不需要（直接消费 `dist`）；距离准确性需要 `dist_scale` 标定流程 |

### 8.1 TBD-B 补充：典型 OFDR UI 是否需要绝对回损标定

典型 OFDR UI 的四个区域与标定依赖：

| 区域 | 内容 | 是否需要绝对标定 |
|---|---|---|
| 距离域曲线 | 反射/功率随距离变化（dB） | 弱：相对 dB 也能看 |
| 事件表 | 位置、IL、RL | **RL 需要**；IL 多为相对量 |
| 链路指标 | 总损耗、总回损、链路长度 | **总回损需要** |
| 判定 | 阈值、合格/不合格 | **RL 阈值需要** |

结论：

- 若 UI 只做相对模式（距离、事件、相对谱、相对 IL），可以先不标定，RL 标注为“相对 dB”，不做合格判定。
- 若 UI 要显示绝对回损（如“RL = 45 dB”）、dBm 或按 RL 阈值判合格，则必须做系统级幅度标定。
- 只做 int16→电压不够；绝对回损需要功率谱密度基准（PSD anchor）或已知反射件作为参考。

标定模型：

```text
RL_abs(f) = 20 × log10(ps(f)) + K(f)
```

`K` 来自已知反射件或厂商“功率谱密度”校准。IL 是两个基线噪声顶的差值，绝对偏移 `K` 会抵消，因此 IL 对绝对标定不敏感；RL 不会抵消，必须标定。

当前实测 RL 为 120–147 dB，是任意线性 `ps` 的 `20log10`，属于相对电平，不是绝对回损。

建议上位机分两阶段：

1. 相对模式：距离、事件、相对谱、相对 IL；RL 标注“相对 dB”。
2. 绝对模式：按厂商的“功率谱密度 / IL 斜率 / IL 截距”流程标定，输出绝对 RL，并保存校准快照。

### 8.2 上位机 MVP 边界

MVP 目标：**单次扫频、相对测量、事件表**，不依赖未关闭 TBD 的厂商公式。

| 功能 | MVP | 依赖 |
|---|---|---|
| 连接 / 自检 | 包含 | 无（走 SDK，1G 固定 IP `192.168.5.20`） |
| 10/50/125/250 MHz 单段采集 | 包含 | 无 |
| P/S/A 固定 `ch0/ch1/ch2` | 包含 | 已确认 |
| 距离域曲线 | 包含 | 无（直接消费 `dist` 数组） |
| 事件表：位置、相对 IL、相对 RL | 包含 | 无 |
| 距离标定 `dist_scale` | 包含（基本距离精度） | 需定义标定流程，不依赖厂商公式 |
| 绝对 RL / dBm / 阈值判定 | 第二阶段 | TBD-B（PSD 基准/已知反射件标定） |
| 多段采集 | 不做 | TBD-C |
| 外触发 / 触发延时 | 外触发不做；软件触发延时待确认 | TBD-D（外触发不适用） |
| 直连 TCP、绕过 SDK | 不做 | TBD-15 |

MVP 必须落实的四件事：

1. **采集隔离**：`read` 放独立进程/可强杀执行单元，外部超时 + 强杀 + 重新 open（TBD-E）。
2. **配置快照**：`fs`、`dist_scale`、`DetectConfig`、通道映射随结果保存。
3. **数值边界**：输出长度按 `N/2+1`；dB 只由 `detect` 转换一次；零距峰显式标记或排除。
4. **明确禁用** `seg_count > 1`。

后续版本再处理：绝对 RL 标定、多段/外触发（待厂商语义）、直连协议（若要绕过 SDK）。

---

## 9. 证据文件

| 内容 | 路径 |
|---|---|
| 采集与处理主结果 | `GM-IX/results/gigabit/stage1_g*.json`、`stage2_g50.json`、`stage2_g250.json` |
| 详细峰/事件/段 | `GM-IX/results/gigabit/analysis/g50_peaks.csv`、`g50_events.csv`、`g250_peaks.csv`、`g250_events.csv` |
| dist_scale / fs 实验 | `GM-IX/results/gigabit/analysis/g50_scale2.json`、`g50_fs25.json` |
| 原始数据 | `GM-IX/results/gigabit/raw-g50_retry.bin`、`raw-g250.bin`、`raw-g250_50ms.bin` |
| 千兆测试报告 | `GM-IX/docs/测试报告_1000M.md` |
