# 映讯 QT2144 SDK 接入说明书

> 分析对象：`references/qt2144-rs/`（采集）、`references/ofdr-0.1.1-release/`（处理）  
> 版本快照：`qt_acq.dll` 0.1.0、`QT_Board.dll` 2.0.1.0、`sdk_version_meta` 1.0.0.0、`ofdr.dll`（目录名 0.1.1，版本资源为空）、WinPcap 4.1.0.2980  
> 编写日期：2026-11-02  
> 性质：基于厂商随附头文件、Python 封装、配置文件与 DLL 二进制静态分析整理的接入说明，**不是厂商正式文档**。  
> 用途：为接入 QT2144 设备做准备；不能替代硬件协议确认（SRS TBD-15）。

---

## 0. 文档定位

### 0.1 本文回答什么

本文把两个目录合成一条完整链路，回答以下问题：

1. 两个目录各自是什么、谁是采集、谁是处理；
2. 应用代码应该按什么顺序调用；
3. 每个函数的参数、返回值、错误码和数据格式；
4. 采集数据怎样转换成处理库要求的输入；
5. 部署时需要带哪些 DLL、配置放在哪里；
6. 上机前能做哪些离线验证，上机时按什么顺序排查；
7. 哪些结论已经确认，哪些仍是未确认项。

### 0.2 证据等级

| 标记 | 含义 |
|---|---|
| 【A】 | 厂商随附文档、头文件或 Python 源码明确写出 |
| 【B】 | 通过 DLL 导出表、导入表、PE 头、反汇编或二进制字符串直接观察 |
| 【C】 | 由现有证据推断，尚未实测或未经厂商确认 |

### 0.3 与既有一期分析的关系

`qt_acq` 的逐函数反推、反汇编细节、配置字段逐项解读和排障方法，已写在：

- [QT2144_qt_acq_反推说明书.md](./QT2144_qt_acq_反推说明书.md)

本文不再重复全部反汇编证据，只保留接入必需的结论，并补齐 `ofdr` 处理端与两端之间的转换层。

### 0.4 一句话结论

**`qt2144-rs` 负责“把板卡上的 4 通道 int16 原始采样取回来”，`ofdr-0.1.1-release` 负责“把 P/S/A 三路 float64 信号算成距离轴、频率轴和功率谱”。两者没有代码或 DLL 依赖，是采集 → 转换 → 处理的上游/下游关系。**

---

## 1. 两个目录的角色与关系

### 1.1 目录对照

| 维度 | `references/qt2144-rs` | `references/ofdr-0.1.1-release` |
|---|---|---|
| 角色 | 采集端（设备 I/O） | 处理端（纯计算） |
| 输入 | QT2144 板卡 | 三路 `double` 数组（P/S/A） |
| 输出 | `int16` 小端、4 通道交织的原始采样 | `dist` / `freq` / `ps` 三组 `double` |
| C ABI 导出数 | 9 | 3 |
| 主要依赖 | `QT_Board.dll` 及其传输/基础 DLL | 仅 Windows 系统运行库 |
| 是否接触硬件 | 是 | 否 |
| 是否需要 `config/` | 是，按 CWD 查找 | 否 |
| 架构 | x64 | x64 |
| 实现语言 | Rust（C ABI） | Rust（C ABI，`rustfft`/`realfft`/`rayon`） |
| Python 封装 | `qt_acq.py`，类封装 `QtAcq` | `ofdr.py`，直接暴露 `ctypes.CDLL` |
| 当前能否离线跑 | 只能做加载与参数校验；采集需硬件 | 可以，用合成数组即可 |

### 1.2 调用层次

```text
应用代码（Python / C / C# / C++）
        │
        ├── 采集：qt_acq.dll（9 个导出）
        │        │  只导入 QT_Board.dll
        │        ▼
        │   QT_Board.dll（通用板卡 SDK，384 个导出）
        │        ├─ QT_Board_PCIe.dll       PCIe 传输
        │        ├─ QT_Board_Net.dll        1G 网口 TCP 传输
        │        ├─ QT_Board_10Net.dll      10G 网口原始以太网传输
        │        ├─ elog.dll                日志
        │        ├─ pthreadVC2.dll          线程
        │        └─ libfftw3-3.dll          FFT/DSP
        │
        └── 处理：ofdr.dll（3 个导出）
                 └─ 仅系统运行库（VCRUNTIME140、KERNEL32、ntdll、CRT）
```

【B】`qt_acq.dll` 的业务依赖只有 `QT_Board.dll`；`QT_Board.dll` 在加载时又同时导入三种传输 DLL。因此**即使只走网口，也不能删除 PCIe 传输 DLL**，否则整条链路加载失败。

【B】`ofdr.dll` 不导入 `QT_Board.dll`、不导入 `qt_acq.dll`，可以单独部署、单独测试。

### 1.3 为什么分成两个包

- 采集包随硬件交付，包含厂商通用板卡 SDK 和板卡配置；
- 处理包是与硬件无关的算法库，输入只是三路双精度数组；
- 两个包的 Python 封装风格相似（同目录 DLL + `ctypes`），但构建者不同、版本节奏不同；
- 因此升级采集 SDK 不影响算法库，替换算法库也不影响板卡配置。

### 1.4 建议的接入分层

```text
┌────────────────────────────────────────────┐
│ UI / 业务层                                 │
├────────────────────────────────────────────┤
│ 转换层：反交织、通道选择、int16→double、标定 │  ← 本文第 5 章
├────────────────────────────────────────────┤
│ 采集适配器 qt_acq        │ 算法适配器 ofdr   │
├────────────────────────────────────────────┤
│ QT2144 硬件              │ 纯计算            │
└────────────────────────────────────────────┘
```

转换层是接入工作里唯一需要自己写的部分；两个厂商 SDK 都只提供库，不提供这段胶水。

---

## 2. 端到端数据流

### 2.1 数据流

```text
QT2144 板卡
   │  FPGA 采样，4 通道交织
   ▼
qt_acq.dll
   │  int16 小端，帧 = [ch0, ch1, ch2, ch3]
   ▼
原始缓冲区（bytes）
   │  反交织 + 选通道 + 转 float64 + 可选标定
   ▼
P[] / S[] / A[]（三路等长 double）
   │  fs = 采样率 MHz × 1e6
   ▼
ofdr.dll
   │  dist / freq / ps
   ▼
距离轴、频率轴、联合功率谱
```

### 2.2 每一步的输入输出与责任

| 步骤 | 输入 | 输出 | 责任方 |
|---|---|---|---|
| 打开板卡 | 卡序号 | 句柄 | `qt_acq_open` |
| 配置 | 采样率、时长、段数、触发、触发延时 | 状态 | `qt_acq_configure` |
| 启动 | 句柄 | 状态 | `qt_acq_start` |
| 读取 | 缓冲区、容量 | 原始字节、实际长度 | `qt_acq_read` |
| 反交织 | 原始字节 | `N × 4` 的 int16 帧 | 转换层 |
| 通道选择 | 帧 + 通道号 | 三路 int16 | 转换层 |
| 类型转换 | int16 | float64 | 转换层 |
| 标定（可选） | int16 码值 | 物理量 | 转换层，需标定系数 |
| OFDR 处理 | P/S/A + `OfdrConfig` | `OfdrResult` | `ofdr_process` |
| 释放 | `OfdrResult` | 无 | `ofdr_result_free` |

### 2.3 三个不能省的关键转换

1. **4 通道 → 3 路**：板卡给 4 路，算法只要 P/S/A 三路。哪一路对应 P、S、A **必须由接线和配置决定**，不能写死。
2. **int16 → float64**：`qt_acq` 只给原始码值；`ofdr_process` 只收 `double`。
3. **采样率单位**：`qt_acq` 用 MHz，`OfdrConfig.fs` 用 Hz，必须乘 `1e6`。

---

## 3. 采集 SDK：`qt2144-rs`

### 3.1 文件清单

| 文件 | 大小 | 作用 |
|---|---:|---|
| `qt_acq.dll` | 135,680 | 采集核心，Rust 实现的 C ABI，9 个导出 |
| `qt_acq.h` | 1,476 | C 头文件 |
| `qt_acq.py` | 7,720 | Python 封装 `QtAcq` |
| `readme_ext.md` | 5,261 | 厂商随附使用说明 |
| `QT_Board.dll` | 2,168,320 | 通用板卡 SDK，384 个导出 |
| `QT_Board_PCIe.dll` | 53,248 | PCIe 传输 |
| `QT_Board_Net.dll` | 65,024 | 1G 网口传输 |
| `QT_Board_10Net.dll` | 79,360 | 10G 网口传输 |
| `elog.dll` | 26,112 | 日志 |
| `libfftw3-3.dll` | 2,712,765 | FFT（双精度） |
| `libfftw3f-3.dll` | 2,772,692 | FFT（单精度） |
| `libfftw3l-3.dll` | 1,247,967 | FFT（长双精度） |
| `libliquid.dll` | 1,774,360 | DSP |
| `pthreadVC2.dll` / `pthreadGC2.dll` | 82,944 / 185,976 | 线程运行库 |
| `wpcap.dll` / `Packet.dll` | 370,424 / 107,768 | WinPcap 抓包（10G 路径相关） |
| `config/Interface.json` | 291 | 板卡选择与基地址 |
| `config/QT2144/*.json` | — | AFE、量程、功能配置 |
| `config/version/sdk_version_meta` | 9 | SDK 版本 `1.0.0.0` |
| `config/0/*.json` | — | 占位/空配置，不能用于 QT2144 |

### 3.2 部署树与工作目录

【B】`QT_Board.dll` 的二进制字符串包含：

```text
config\
config\version\sdk_version_meta
./config/ExternalDCBiasCalibration.json
log\QT_Board
```

说明 SDK 按**进程当前工作目录（CWD）**查找配置、写日志，而不是按 DLL 所在目录。推荐的部署结构：

```text
<应用根目录>/                 ← 启动时把 CWD 设到这里
├─ qt_acq.dll
├─ QT_Board.dll
├─ QT_Board_PCIe.dll
├─ QT_Board_Net.dll
├─ QT_Board_10Net.dll
├─ elog.dll
├─ libfftw3-3.dll
├─ libfftw3f-3.dll
├─ libfftw3l-3.dll
├─ libliquid.dll
├─ pthreadVC2.dll
├─ pthreadGC2.dll
├─ wpcap.dll
├─ Packet.dll
├─ config/
│  ├─ Interface.json
│  ├─ QT2144/
│  │  ├─ AFE.json
│  │  ├─ Range.json
│  │  └─ FunctionalConfiguration.json
│  └─ version/
│     └─ sdk_version_meta
└─ log/                       ← 必须可写
```

部署要点：

- 全部 DLL 必须能被 Windows 加载器找到，最稳妥是全部放在应用目录；
- `config/` 必须位于 CWD，放在 DLL 旁边但 CWD 不同则无效；
- `log/` 必须可写；安装在 `Program Files` 下时需显式设置可写的工作目录；
- 不要只复制 `qt_acq.dll` 和 `QT_Board.dll`，传输与基础 DLL 缺一不可。

### 3.3 C ABI 总览

```c
typedef struct qt_acq_handle_t qt_acq_handle_t;
typedef qt_acq_handle_t *qt_acq_handle;
```

所有函数返回 `int`，`0` 为成功，负数为错误码。调用约定为 C ABI，64 位进程。

| 函数 | 作用 |
|---|---|
| `qt_acq_open` | 打开指定序号板卡，输出句柄 |
| `qt_acq_close` | 关闭板卡，释放句柄 |
| `qt_acq_configure` | 设置采样率、时长、段数、触发方式、触发延时 |
| `qt_acq_start` | 开始采集 |
| `qt_acq_read` | 阻塞读取整块数据 |
| `qt_acq_stop` | 停止采集 |
| `qt_acq_calc_buffer_bytes` | 按公式计算所需缓冲区字节数 |
| `qt_acq_last_error` | 取最近一次错误消息 |
| `qt_acq_version` | 取 SDK 版本字符串 |

### 3.4 函数参考

#### 3.4.1 `qt_acq_open`

```c
int qt_acq_open(uint32_t card_index, qt_acq_handle *out_handle);
```

| 参数 | 类型 | 说明 |
|---|---|---|
| `card_index` | `uint32_t` | 板卡序号，从 0 开始，对应 `config/Interface.json` 的 `CardIndx` |
| `out_handle` | `qt_acq_handle *` | 输出句柄 |

- 成功返回 `0`，`out_handle` 非空；
- 失败返回 `-2`（SDK）、`-1`（I/O）或 `-5`（空指针）；
- 【B】`qt_acq.dll` 导入表包含 AFE、EEPROM、风扇、复位等 `QT_Board` 函数，但静态分析无法证明它们全部在 `open` 阶段调用；应理解为“打开/配置阶段完成硬件初始化”；
- 没有硬件时通常会失败，可用于验证错误路径。

#### 3.4.2 `qt_acq_close`

```c
int qt_acq_close(qt_acq_handle handle);
```

- 关闭会话并释放句柄；
- 建议先 `stop` 再 `close`；
- 【C】重复 `close` 或 `close(NULL)` 的行为未实测，调用方应自行保证只关一次。

#### 3.4.3 `qt_acq_configure`

```c
int qt_acq_configure(qt_acq_handle handle, uint32_t sample_rate_mhz,
                     uint32_t acq_time_ms, uint32_t seg_count, int trigger,
                     uint32_t trigger_delay_ms);
```

| 参数 | 约束 | 证据 |
|---|---|---|
| `sample_rate_mhz` | 只接受 `10`、`50`、`125`、`250` | 【B】反汇编与错误字符串 |
| `acq_time_ms` | `> 0` | 【B】错误字符串 |
| `seg_count` | `>= 1` | 【B】错误字符串 |
| `trigger` | `0` 软件、`1` 外部上升沿、`2` 外部下降沿 | 【A】头文件 |
| `trigger_delay_ms` | 未观察到显式校验 | 【B】未见；语义待实测 |

其他返回：

- `-3` 参数无效；
- `-7` 尚未配置（在 `read` 前未配置时出现）；
- 单段或总字节超过 `u32` 上限时返回参数错误。

#### 3.4.4 `qt_acq_start`

```c
int qt_acq_start(qt_acq_handle handle);
```

- 启动一次采集；
- 是否要求先 `configure`：是；
- 是否可在一次 `open` 后重复 `configure → start → read → stop`：未实测，建议每次采集前重新 `configure`。

#### 3.4.5 `qt_acq_read`

```c
int qt_acq_read(qt_acq_handle handle, void *buffer, uint32_t capacity,
                uint32_t *out_len);
```

行为（【B】反汇编）：

- 只调用一次底层 `QT_BoardSetupRecGetData`；
- 成功后 `*out_len = required_bytes`；
- 没有原生超时、分片读取或取消机制；
- 未配置返回 `-7`；
- `capacity` 不足返回 `-6`；
- 底层 SDK 报错返回 `-2`，I/O 错误返回 `-1`。

调用要求：

- `buffer` 必须至少 `capacity` 字节，按 2 字节对齐；
- 推荐 `capacity = qt_acq_calc_buffer_bytes(...)`；
- 读取后以 `out_len` 为准，不要用 `capacity` 当实际长度。

#### 3.4.6 `qt_acq_stop`

```c
int qt_acq_stop(qt_acq_handle handle);
```

- 停止采集；
- 【B】`qt_acq.dll` 没有导入独立的停止函数，`stop` 很可能复用底层采集控制函数；
- 建议放在 `finally` 中，确保异常路径也会停止。

#### 3.4.7 `qt_acq_calc_buffer_bytes`

```c
int qt_acq_calc_buffer_bytes(uint32_t sample_rate_mhz, uint32_t acq_time_ms,
                             uint32_t seg_count, uint32_t *out_bytes);
```

公式（【B】反汇编常量 `0x1F40 = 8000`）：

```text
bytes = sample_rate_mhz × acq_time_ms × 8000 × seg_count
```

拆解：

```text
1000  = ms → s
4     = 通道数
2     = int16 字节数
8000  = 1000 × 4 × 2
```

每段帧数：

```text
frames_per_segment = sample_rate_mhz × acq_time_ms × 1000
```

常用容量：

| 采样率 | 时长 | 段数 | 字节数 | 约合 |
|---:|---:|---:|---:|---:|
| 10 MHz | 10 ms | 1 | 800,000 | 0.76 MiB |
| 50 MHz | 8 ms | 1 | 3,200,000 | 3.05 MiB |
| 50 MHz | 8 ms | 4 | 12,800,000 | 12.21 MiB |
| 125 MHz | 10 ms | 1 | 10,000,000 | 9.54 MiB |
| 250 MHz | 10 ms | 1 | 20,000,000 | 19.07 MiB |

### 3.5 生命周期与状态机

```text
        open
  未打开 ──────► 已打开
                   │ configure
                   ▼
                已配置
                   │ start
                   ▼
                采集中
                   │ read（阻塞，整块）
                   ▼
                已读取
                   │ stop
                   ▼
                已停止
                   │ close
                   ▼
                未打开
```

推荐顺序：

```text
open → (version) → configure → calc_buffer_bytes → start → read → stop → close
```

严格用 `try/finally` 保证 `stop` 和 `close`：

```python
dev = None
try:
    dev = QtAcq(0)
    dev.configure(...)
    dev.start()
    raw = dev.read()
finally:
    if dev is not None:
        try:
            dev.stop()
        finally:
            dev.close()
```

### 3.6 返回数据格式

【A】`readme_ext.md` 明确：

- `int16` 有符号、小端；
- 多通道按帧交织；
- `sample[i] = [ch0, ch1, ch2, ch3]`。

帧结构：

```text
帧 i 的字节偏移 = i × 8
  +0  ch0  int16 低字节
  +1  ch0  int16 高字节
  +2  ch1
  +3  ch1
  +4  ch2
  +5  ch2
  +6  ch3
  +7  ch3
```

第 `c` 通道第 `i` 个采样：

```text
sample_index = i × 4 + c
```

多段数据布局【C】：现有资料没有说明段间排列。最可能是：

```text
[段0 全部帧][段1 全部帧]...[段N-1 全部帧]
```

但以下因素可能破坏该假设，未实测前不要直接切片：

- `FunctionalConfiguration.json` 中有 `frame_header_bytes_number = 64`；
- `*_BYTE_ALIGNMENT = 64`；
- 段间是否对齐、是否带帧头均未确认；
- `calc_buffer_bytes` 只计算纯采样字节，未加帧头。

### 3.7 Python 封装 `QtAcq` 的行为细节

| 方法 | 实际行为 |
|---|---|
| `QtAcq(card_index=0)` | 构造时立即 `open` |
| `open()` | 已打开则直接返回，不重复打开 |
| `configure(...)` | 透传 C 函数，本地不校验参数 |
| `configure_file(path)` | 读取非空行，至少 5 行：采样率 MHz、时长 ms、段数、触发、触发延时 ms |
| `start()` | 透传 |
| `read(capacity=None)` | 未给容量时要求已 `configure`，内部调用 `required_bytes`；用 `create_string_buffer` 分配并**清零**；返回 `buf.raw[:out_len]` |
| `stop()` | 透传，忽略返回值 |
| `close()` | 透传，忽略返回值；置空句柄 |
| `version()` | 忽略返回值，返回缓冲区解码后的字符串 |
| `required_bytes(...)` | 静态方法，调用 `calc_buffer_bytes` |
| 上下文管理器 | `with QtAcq() as dev:` 退出时只 `close`，**不会自动 `stop`** |

DLL 加载顺序（`_dll_candidates`）：

1. 环境变量 `QT_ACQ_DLL`；
2. `qt_acq.py` 同目录的 `qt_acq.dll`；
3. 上一级的 `target/release/qt_acq.dll`；
4. 系统搜索路径 `qt_acq.dll`。

模块级单例 `_LIB` 保证 DLL 只加载一次。

### 3.8 错误码

| 返回值 | 常量 | 含义 |
|---:|---|---|
| 0 | `QT_ACQ_OK` | 成功 |
| -1 | `QT_ACQ_ERR_UNKNOWN` | 未知 / I/O 错误 |
| -2 | `QT_ACQ_ERR_SDK` | 底层 SDK 错误 |
| -3 | `QT_ACQ_ERR_INVALID_ARGUMENT` | 参数无效 |
| -4 | `QT_ACQ_ERR_INVALID_HANDLE` | 句柄无效 |
| -5 | `QT_ACQ_ERR_NULL_POINTER` | 空指针 |
| -6 | `QT_ACQ_ERR_BUFFER_TOO_SMALL` | 缓冲区过小 |
| -7 | `QT_ACQ_ERR_NOT_CONFIGURED` | 尚未配置 |

【B】二进制中的错误消息片段：

```text
invalid sample rate ... (valid: 250, 125, 50, 10)
invalid acquisition time ... (must be > 0)
invalid segment count ... (must be >= 1)
invalid trigger mode ...
segment of ... bytes exceeds u32 limit
total data of ... bytes exceeds u32 limit
buffer has ...
... bytes are required
QT_Board SDK error in ...: code ...
Net card I/O error...
Net card protocol mismatch: expected command 0x..., got 0x...
Net card read timeout: wanted ..., got ...
internal panic in qt_acq
```

Python 侧统一抛出 `QtAcqError`，消息中包含错误名、错误码和 `last_error` 文本。

### 3.9 已知限制

1. 采样率只有 4 档；
2. `read` 是整块阻塞，没有超时和取消；
3. 没有流式接口，不能边采边处理；
4. 通道与 P/S/A 的映射由硬件接线决定；
5. int16 → 物理量的公式厂商未提供；
6. 多段布局与 64 字节帧头是否生效未确认；
7. `readme_ext.md` 声称支持 USB，但目录内没有 USB 传输 DLL，【C】当前资料只能确认 PCIe、1G、10G 三种；
8. `stop` 的底层实现不确定，只能按契约使用。

---

## 4. 处理 SDK：`ofdr-0.1.1-release`

### 4.1 文件清单

| 文件 | 大小 | 作用 |
|---|---:|---|
| `ofdr.dll` | 1,214,464 | 处理核心，Rust 实现，3 个导出 |
| `ofdr.h` | 1,721 | C 头文件 |
| `ofdr.py` | 1,170 | Python `ctypes` 绑定 |
| `README_ext.md` | 4,238 | 厂商随附使用说明 |

【B】`ofdr.dll` 只导入系统 DLL，不依赖采集 SDK，可独立部署。

### 4.2 C ABI

```c
typedef struct {
    double fs;           /* 采样率 (Hz) */
    double dist_scale;   /* 距离轴校准系数，默认 1.0 */
} OfdrConfig;

typedef struct {
    int32_t error;         /* 0 成功，非 0 失败 */
    const char *error_msg; /* 失败时有效，随结果释放 */
    double *dist;          /* 距离轴 (m)，长度 len */
    double *freq;          /* 频率轴 (Hz)，长度 len */
    double *ps;            /* 联合功率谱（线性幅值），长度 len */
    size_t len;            /* 各数组长度 */
} OfdrResult;

OfdrConfig ofdr_config_default(void);
OfdrResult ofdr_process(const double *p, const double *s,
                        const double *aux, size_t len,
                        const OfdrConfig *cfg);
void ofdr_result_free(OfdrResult *result);
```

### 4.3 内存所有权

- `dist`、`freq`、`ps`、`error_msg` 都由 `ofdr.dll` 分配；
- 调用方**不得** `free` 或 `delete` 这些指针；
- 使用完必须调用 `ofdr_result_free(&res)`；
- 读取 `error_msg` 要在释放前完成；
- 【C】`ofdr_result_free` 对已释放结果重复调用是否安全未验证，调用方应保证只释放一次。

### 4.4 输入输出约束

| 项 | 说明 | 证据 |
|---|---|---|
| `p` / `s` / `aux` | 三路 `double` 数组，等长 | 【A】 |
| `len` | 三路共同长度 | 【A】 |
| `cfg` | 不可为空 | 【A】+【B】错误字符串 |
| `p/s/aux` | 不可为空 | 【B】`input pointers must not be null` |
| `len` | 不可为 0 | 【B】`inputs must be non-empty` |
| 输出长度 | 与输入等长 | 【A】README“等长处理结果” |
| 输入长度是否必须 2 的幂 | **未确认** | 【C】 |
| 最小长度 | **未确认** | 【C】 |

【B】`ofdr.dll` 的错误/校验字符串：

```text
cfg must not be null
input pointers must not be null
inputs must be non-empty
Wrong length of input, expected ...
Wrong length of output, expected ...
unknown error
src\capi.rs
src\processing.rs
```

### 4.5 `fs` 与 `dist_scale`

| 字段 | 含义 | 接入要求 |
|---|---|---|
| `fs` | 采样率，单位 Hz | 必须设为 `qt_acq` 的 MHz 值 × `1e6` |
| `dist_scale` | 距离轴校准系数，默认 `1.0` | 用已知距离反射点标定：`实际距离 / 输出距离` |

校准示例：已知反射点 30 m，输出 29.7 m：

```text
dist_scale = 30 / 29.7 ≈ 1.0101
```

`ofdr_config_default()` 的 `fs` 初值未确认，调用后**必须显式赋值**，不要依赖默认值。

### 4.6 Python 封装

`ofdr.py` 在导入时立即加载同目录 `ofdr.dll`（找不到时尝试 `../target/release/ofdr.dll`），并声明：

- `ofdr_config_default`：无参，返回 `OfdrConfig`；
- `ofdr_process`：三个 `POINTER(c_double)` + `c_size_t` + `POINTER(OfdrConfig)`，返回 `OfdrResult`；
- `ofdr_result_free`：`POINTER(OfdrResult)`，无返回。

`ofdr.py` 没有类封装，模块级对象 `ofdr` 就是 `ctypes.CDLL`。

> 注意：`from ofdr import ofdr` 中，模块名与 `CDLL` 变量同名，但这是厂商写法，按原样使用即可。

---

## 5. 转换层设计

这是接入 QT2144 时必须自己实现的部分，也是风险最集中的部分。

### 5.1 职责边界

转换层只做四件事：

1. 把 `read` 返回的字节按帧拆开；
2. 按配置选择 P/S/A 三个通道；
3. 把 int16 转成 float64；
4. 如已有标定系数，做一次物理量换算。

不做：

- 不做 FFT、不做滤波、不做 OFDR 算法；
- 不在 UI 层做换算；
- 不把厂商类型向上暴露。

### 5.2 通道映射必须可配置

【A】厂商文档明确：通道与用途（P/S/A）的对应关系按实际硬件接线确定。

因此需要配置项，例如：

```json
{
  "channelMap": {
    "p": 0,
    "s": 1,
    "aux": 2
  }
}
```

不要写死 `ch0=P, ch1=S, ch2=A`，也不要假设 `ch3` 一定不用。

### 5.3 反交织代码

Python：

```python
import numpy as np

N_CHAN = 4
raw = data                                  # qt_acq.read() 返回的 bytes
n_frames = len(raw) // (2 * N_CHAN)
frames = np.frombuffer(raw, dtype="<i2")[: n_frames * N_CHAN]
frames = frames.reshape(n_frames, N_CHAN)

P = frames[:, P_CH].astype(np.float64)
S = frames[:, S_CH].astype(np.float64)
A = frames[:, A_CH].astype(np.float64)
```

C：

```c
#include <stdint.h>

uint32_t n_frames = got / 8;
const int16_t *pcm = (const int16_t *)buf;

/* 假设 P=0, S=1, A=2，实际应由配置传入 */
double *p = malloc(n_frames * sizeof(double));
double *s = malloc(n_frames * sizeof(double));
double *a = malloc(n_frames * sizeof(double));

for (uint32_t i = 0; i < n_frames; ++i) {
    p[i] = (double)pcm[i * 4 + 0];
    s[i] = (double)pcm[i * 4 + 1];
    a[i] = (double)pcm[i * 4 + 2];
}
```

注意：`int16_t *` 强转要求缓冲区 2 字节对齐，`malloc/calloc` 自然满足。

### 5.4 标定（`LsbScale`）

按项目分层规则，int16 → 物理量只能在采集适配器内发生一次，且必须使用显式 `LsbScale`。

现状：

- 【A】厂商没有给出码值到物理量的公式；
- 【B】`AFE.json` 有 `offset/gain/attenuation`；
- 【B】`Range.json` 有每通道每量程的 `factor`；
- 【C】这些字段的单位、是否互斥、是否需要先 offset 校正再乘 factor，均未确认。

因此：

- **不能**直接把 `Range.json` 的 `factor` 当作 `LsbScale`；
- **不能**猜测“码值 × factor = 电压”；
- 在拿到厂商公式或完成实测标定前，转换层只做 int16 → double，不引入任何比例系数；
- 标定系数必须进入配置快照并随结果落盘。

### 5.5 长度、分段与采样率

| 问题 | 当前结论 | 处理建议 |
|---|---|---|
| `fs` | `sample_rate_mhz × 1e6` | 显式换算，不依赖默认值 |
| 多段数据 | 布局未确认 | 先按单段接入；多段先逐段验证边界 |
| 段间是否带 64 字节头 | 未确认 | 首次实测时检查 `out_len` 与帧边界 |
| 三路长度 | 必须相同 | 以最短为准截断或报错，禁止静默补齐 |
| 输入长度是否需 2 的幂 | 未确认 | 先用固定长度做合成测试，再上机验证 |
| `len=0` | 返回非 0 错误 | 调用前显式拒绝 |

多段建议：

- 不要在多段未确认前把整块数据直接拼成一条长序列；
- 每段独立处理，再按业务需要聚合；
- 若必须拼接，先证明段边界连续且无帧头。

### 5.6 数据质量检查

每次采集后建议至少检查：

1. `out_len == required_bytes`；
2. `out_len % 8 == 0`；
3. 各通道无持续饱和（大量 `-32768` / `32767`）；
4. 直流/短接输入下各通道均值接近基线；
5. 已知正弦输入下 FFT 峰值频率符合预期；
6. 多段时每段边界前后无异常跳变；
7. 若 P/S/A 通道交换，结果应显著变化，用于确认映射。

### 5.7 建议的中间结构

转换层向上只暴露与厂商无关的结构：

```text
ScanRaw
  ├─ SampleRateHz   double
  ├─ ChannelCount   int
  ├─ FrameCount     int
  ├─ SegmentCount   int
  ├─ TriggerMode    enum
  ├─ CapturedAt     timestamp
  └─ Data           double[]（已按 P/S/A 拆好）或按通道索引
```

具体命名应与 `OFDR.Contracts` 的 `StandardScan` 对齐；**不要**把 `qt_acq_handle`、`int16*`、`QT_Board` 类型向上传递。

---

## 6. 三种语言的调用示例

### 6.1 Python 端到端

```python
import ctypes
import sys

import numpy as np

QT_DIR = r"d:\Mycode\GM_OFDR\references\qt2144-rs"
OFDR_DIR = r"d:\Mycode\GM_OFDR\references\ofdr-0.1.1-release"
sys.path.insert(0, QT_DIR)
sys.path.insert(0, OFDR_DIR)

from qt_acq import QtAcq, TRIGGER_EXT_RISING
from ofdr import ofdr

SAMPLE_RATE_MHZ = 50
ACQ_TIME_MS = 8
SEG_COUNT = 1
P_CH, S_CH, A_CH = 0, 1, 2          # 必须按实际接线配置

dev = QtAcq(card_index=0)
try:
    dev.configure(SAMPLE_RATE_MHZ, ACQ_TIME_MS, SEG_COUNT,
                  TRIGGER_EXT_RISING, 1)
    nbytes = dev.required_bytes(SAMPLE_RATE_MHZ, ACQ_TIME_MS, SEG_COUNT)
    dev.start()
    raw = dev.read(nbytes)
finally:
    try:
        dev.stop()
    finally:
        dev.close()

n_frames = len(raw) // 8
frames = np.frombuffer(raw, dtype="<i2")[: n_frames * 4].reshape(n_frames, 4)
P = frames[:, P_CH].astype(np.float64)
S = frames[:, S_CH].astype(np.float64)
A = frames[:, A_CH].astype(np.float64)

cfg = ofdr.ofdr_config_default()
cfg.fs = SAMPLE_RATE_MHZ * 1e6
cfg.dist_scale = 1.0

ptr = lambda a: a.ctypes.data_as(ctypes.POINTER(ctypes.c_double))
res = ofdr.ofdr_process(ptr(P), ptr(S), ptr(A), len(P), ctypes.byref(cfg))
if res.error != 0:
    raise RuntimeError(f"OFDR 处理失败: error={res.error}")

dist = np.ctypeslib.as_array(res.dist, shape=(res.len,)).copy()
freq = np.ctypeslib.as_array(res.freq, shape=(res.len,)).copy()
ps = np.ctypeslib.as_array(res.ps, shape=(res.len,)).copy()
ofdr.ofdr_result_free(ctypes.byref(res))

print(f"距离轴: {dist[0]:.3f} ~ {dist[-1]:.3f} m，共 {len(dist)} 点")
print(f"功率谱峰值: {ps.max():.3f}")
```

### 6.2 C 端到端

```c
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include "qt_acq.h"
#include "ofdr.h"

int main(void)
{
    qt_acq_handle h = NULL;
    if (qt_acq_open(0, &h) != QT_ACQ_OK) {
        return 1;
    }

    if (qt_acq_configure(h, 50, 8, 1, QT_ACQ_TRIG_EXT_RISING, 1) != QT_ACQ_OK) {
        qt_acq_close(h);
        return 2;
    }

    uint32_t cap = 0;
    if (qt_acq_calc_buffer_bytes(50, 8, 1, &cap) != QT_ACQ_OK) {
        qt_acq_close(h);
        return 3;
    }

    int16_t *raw = (int16_t *)malloc(cap);
    if (!raw) {
        qt_acq_close(h);
        return 4;
    }

    if (qt_acq_start(h) != QT_ACQ_OK) {
        free(raw);
        qt_acq_close(h);
        return 5;
    }

    uint32_t got = 0;
    if (qt_acq_read(h, raw, cap, &got) != QT_ACQ_OK) {
        qt_acq_stop(h);
        free(raw);
        qt_acq_close(h);
        return 6;
    }

    qt_acq_stop(h);
    qt_acq_close(h);

    uint32_t frames = got / 8;
    double *p = (double *)malloc(frames * sizeof(double));
    double *s = (double *)malloc(frames * sizeof(double));
    double *a = (double *)malloc(frames * sizeof(double));
    if (!p || !s || !a) {
        free(p); free(s); free(a); free(raw);
        return 7;
    }

    for (uint32_t i = 0; i < frames; ++i) {
        p[i] = (double)raw[i * 4 + 0];
        s[i] = (double)raw[i * 4 + 1];
        a[i] = (double)raw[i * 4 + 2];
    }

    OfdrConfig cfg = ofdr_config_default();
    cfg.fs = 50e6;
    cfg.dist_scale = 1.0;

    OfdrResult res = ofdr_process(p, s, a, frames, &cfg);
    if (res.error == 0) {
        printf("len=%zu dist=%.3f..%.3f ps_max=%.3f\n",
               res.len, res.dist[0], res.dist[res.len - 1], res.ps[0]);
    } else {
        printf("err=%d msg=%s\n", res.error,
               res.error_msg ? res.error_msg : "(null)");
    }
    ofdr_result_free(&res);

    free(p); free(s); free(a); free(raw);
    return 0;
}
```

### 6.3 C# P/Invoke

```csharp
using System;
using System.Runtime.InteropServices;

public static class QtAcqNative
{
    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_open(uint cardIndex, out IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_close(IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_configure(IntPtr handle, uint sampleRateMhz,
        uint acqTimeMs, uint segCount, int trigger, uint triggerDelayMs);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_start(IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_read(IntPtr handle, IntPtr buffer,
        uint capacity, out uint outLen);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_stop(IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int qt_acq_calc_buffer_bytes(uint sampleRateMhz,
        uint acqTimeMs, uint segCount, out uint outBytes);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi)]
    public static extern int qt_acq_last_error(System.Text.StringBuilder buf,
        UIntPtr cap);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi)]
    public static extern int qt_acq_version(System.Text.StringBuilder buf,
        UIntPtr len);
}

[StructLayout(LayoutKind.Sequential)]
public struct OfdrConfig
{
    public double fs;
    public double distScale;
}

[StructLayout(LayoutKind.Sequential)]
public struct OfdrResult
{
    public int error;
    public IntPtr errorMsg;
    public IntPtr dist;
    public IntPtr freq;
    public IntPtr ps;
    public UIntPtr len;
}

public static class OfdrNative
{
    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern OfdrConfig ofdr_config_default();

    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern OfdrResult ofdr_process(IntPtr p, IntPtr s, IntPtr aux,
        UIntPtr len, ref OfdrConfig cfg);

    [DllImport("ofdr.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ofdr_result_free(ref OfdrResult result);
}
```

C# 注意事项：

- 必须使用 x64 进程；
- `qt_acq.dll` / `ofdr.dll` 放在应用输出目录；
- 传数组时用 `GCHandle.Alloc(array, GCHandleType.Pinned)` 或 `Marshal.UnsafeAddrOfPinnedArrayElement`，调用期间保持 pinned；
- `OfdrResult` 布局必须与 C 结构一致；x64 下 `int error` 后有 4 字节填充，`StructLayout` 默认按指针对齐，通常正确，但仍建议用 `Marshal.SizeOf` 与 C `sizeof` 对照；
- 读取 `dist/freq/ps` 用 `Marshal.Copy` 到 `double[]`，再 `ofdr_result_free`。

---

## 7. 部署与运行环境

### 7.1 必需文件

采集端：

```text
qt_acq.dll
QT_Board.dll
QT_Board_PCIe.dll
QT_Board_Net.dll
QT_Board_10Net.dll
elog.dll
libfftw3-3.dll
libfftw3f-3.dll
libfftw3l-3.dll
libliquid.dll
pthreadVC2.dll
pthreadGC2.dll
wpcap.dll
Packet.dll
config/
```

处理端：

```text
ofdr.dll
```

【C】`wpcap.dll` / `Packet.dll` 与 10G 原始以太网采集相关；1G 路径是否需要未确认。为降低部署风险，建议全部随包分发。

### 7.2 运行环境

| 项 | 要求 |
|---|---|
| 架构 | x64；32 位进程无法加载这些 DLL |
| C++ 运行库 | 需要 MSVC 运行库（`VCRUNTIME140.dll` 等） |
| WinPcap | 【B】版本 4.1.0.2980，10G 路径相关 |
| 工作目录 | 必须包含 `config/`，且可写 `log/` |
| Python | `ctypes` 标准库；示例另需 `numpy` |
| .NET | P/Invoke 无额外包依赖 |

### 7.3 配置目录

| 路径 | 作用 |
|---|---|
| `config/Interface.json` | 选择哪块板卡、1G/10G 寄存器基地址、`CardIndx` |
| `config/QT2144/FunctionalConfiguration.json` | 板卡类型、传输方式、时钟、触发、采集模式、DMA、帧头 |
| `config/QT2144/AFE.json` | 4 通道 × 6 档的 offset/gain/attenuation |
| `config/QT2144/Range.json` | 6 档量程与每通道 factor |
| `config/version/sdk_version_meta` | `1.0.0.0` |
| `config/0/*.json` | 空/占位配置，**不能**作为 QT2144 有效配置 |

关键字段（【A】）：

```json
// Interface.json
"QTBoard0": 1,
"QTBoard1": -1,
"GigabitNet_REG_BASE_ADDRESS": 1073741824,
"10GigabitNet_REG_BASE_ADDRESS": 0,
"CardIndx": 0
```

```json
// FunctionalConfiguration.json 节选
"BOARD_TYPE": 1,
"is_net": true,
"frame_header_bytes_number": 64,
"GigabitNet_BYTE_ALIGNMENT": 64,
"SamplingRate_Double": 5000,
"SamplingRate_Single": 10000,
"CoupleType": "DC",
"is_REF_Clock": true,
"is_EXT_Clock": false,
"is_FiniteMulti": true,
"is_FiniteSingle": true,
"is_InfiniteMulti": true,
"is_StreamPlay": false,
"Pre_trigger_uint1": 4,
"Delay_trigger_uint1": 4,
"is_ExtTrigger_BiasVoltage": true,
"DDR_Memory": 4096,
"dmatotalbytes": 1000
```

`AFE.json` 实际 offset：

| 通道 | offset（6 档） | gain | attenuation |
|---|---|---|---|
| ch0 | 2066, 2066, 2064, 2059, 2061, 2065 | 5, 11, 18, 18, 15, 23 | 0, 0, 0, 15, 9, 9 |
| ch1 | 2063, 2063, 2060, 2053, 2047, 2042 | 同上 | 同上 |
| ch2 | 2069, 2069, 2065, 2059, 2056, 2060 | 同上 | 同上 |
| ch3 | 2069, 2069, 2062, 2054, 2050, 2046 | 同上 | 同上 |

`Range.json` 量程档：

```text
50 / 100 / 250 / 500 / 1000 / 2500
```

每路有 `factor1..factor8`；input5/6 多为 `0.0299072`，input7/8 出现 `7/8`、`0.22/0.99`。这些数值的单位与用途未确认，不能直接当标定系数。

### 7.4 日志

【B】日志路径为 CWD 下的 `log/QT_Board`。建议：

- 启动时显式设置 CWD；
- 确保 `log/` 存在且可写；
- 采集异常时先看该目录；
- 不要把日志目录放在只读安装路径。

---

## 8. 错误处理与健壮性

### 8.1 错误分类

| 类别 | 典型返回 | 处理建议 |
|---|---|---|
| 参数错误 | `-3` | 调用前本地校验，直接拒绝 |
| 句柄/空指针 | `-4` / `-5` | 视为编程错误，修正调用顺序 |
| 未配置 | `-7` | 强制 `configure` 在 `start/read` 前 |
| 缓冲区不足 | `-6` | 用 `calc_buffer_bytes` 精确分配 |
| SDK 错误 | `-2` | 读取 `last_error`，按消息分类 |
| I/O / 网络 | `-1` | 检查网口、WinPcap、IP、连接状态 |
| 处理失败 | `res.error != 0` | 读取 `error_msg` 后再释放 |

### 8.2 超时与取消

现状：

- `qt_acq_read` 没有原生超时；
- `qt_acq_stop` 能否中断阻塞中的 `read` 未确认；
- `qt_acq` 的 Rust 实现有内部 panic 捕获，但 panic 后句柄状态未知。

推荐封装：

1. 把采集调用放到独立线程；
2. UI 线程只等待结果或超时；
3. 超时后不要假设 `stop` 一定能恢复，必要时：
   - 放弃该句柄（不重用）；
   - 重新 `open`；
   - 极端情况下重启采集进程；
4. 不在 UI 线程直接调用 `read`。

### 8.3 资源释放顺序

```text
停止采集 → 关闭句柄 → 释放原始缓冲区 → 释放 OFDR 结果
```

任何异常路径都要保证：

- `qt_acq_stop`（尽力）；
- `qt_acq_close`（必须）；
- `ofdr_result_free`（只要 `ofdr_process` 返回过结果就必须调用）。

### 8.4 禁止的写法

```text
catch { }                          // 吞掉协议/数据错误
把猜测的 factor 写成 LsbScale
把 ch0/ch1/ch2 写死为 P/S/A
用 capacity 当实际数据长度
在 UI 线程阻塞 read
忽略 ofdr_result_free
```

---

## 9. 上机接入验证清单

### 9.1 阶段 0：无硬件

| 检查 | 方法 | 通过标准 |
|---|---|---|
| DLL 可加载 | `ctypes.CDLL` 加载 `qt_acq.dll` | 无 `OSError` |
| 依赖完整 | 加载 `QT_Board.dll` 及其传输 DLL | 无缺失 DLL 提示 |
| 版本 | `qt_acq_version` | 返回 `0.1.0` |
| 容量公式 | `calc_buffer_bytes` 参数矩阵 | 与 `rate × time × 8000 × segs` 一致 |
| 参数拒绝 | 非法采样率/时长/段数/触发 | 返回 `-3`，消息可读 |
| 处理库独立 | 合成 P/S/A 调 `ofdr_process` | `error == 0`，输出非空 |
| 内存释放 | 调用 `ofdr_result_free` | 无崩溃、无泄漏迹象 |

合成测试建议：

```python
N = 8192
t = np.arange(N, dtype=np.float64)
P = np.sin(2 * np.pi * 0.03 * t)
S = np.sin(2 * np.pi * 0.03 * t + 0.5)
A = np.sin(2 * np.pi * 0.02 * t)
```

### 9.2 阶段 1：单板采集

| 顺序 | 检查 | 通过标准 |
|---:|---|---|
| 1 | CWD 含 `config/`，`log/` 可写 | 文件存在 |
| 2 | `qt_acq_open(0)` | 返回 `0` |
| 3 | `configure(50, 1, 1, 0, 0)` | 返回 `0` |
| 4 | `start` + `read` | `out_len == required_bytes` |
| 5 | 帧完整性 | `out_len % 8 == 0` |
| 6 | 四通道有数据 | 各通道非全零、非全饱和 |
| 7 | 已知直流/正弦 | 幅值、频率符合预期 |
| 8 | 多段 | 先确认段边界，再决定是否拼接 |
| 9 | 外部触发 | 上升沿/下降沿行为符合预期 |

### 9.3 阶段 2：与 `ofdr` 联调

| 顺序 | 检查 | 通过标准 |
|---:|---|---|
| 1 | P/S/A 接线映射 | 交换通道后结果显著变化 |
| 2 | `fs` | 等于 MHz × `1e6` |
| 3 | 三路等长 | 长度一致，无补齐 |
| 4 | 输入长度 | 记录实际可用长度；验证是否需 2 的幂 |
| 5 | `dist` 单调性 | 距离轴单调递增 |
| 6 | `ps` 合理性 | 峰值位置对应已知反射点 |
| 7 | `dist_scale` 标定 | 已知距离点误差满足要求 |
| 8 | 重复性 | 多次采集结果稳定 |

### 9.4 验收判据（建议）

- 采集：`out_len` 与公式完全一致，连续 100 次无失败；
- 数据：四通道无异常饱和，帧边界无错位；
- 处理：`error == 0`，`len == 输入长度`，`dist` 单调；
- 标定：已知反射点距离误差在业务允许范围内；
- 稳定性：连续运行 1 小时无崩溃、无句柄泄漏、无内存持续增长。

---

## 10. 风险与未确认项

| 编号 | 事项 | 现状 | 影响 |
|---|---|---|---|
| TBD-15 | 采集板形态、网络协议、帧格式 | 未确认 | 不能实现正式适配器 |
| TBD-A | P/S/A 与物理通道映射 | 仅知“按接线确定” | 结果可能完全错误 |
| TBD-B | int16 → 物理量公式 / `LsbScale` | 厂商未给 | 不能做绝对幅值 |
| TBD-C | 多段布局、64 字节帧头是否生效 | 未实测 | 多段切片不可信 |
| TBD-D | `trigger_delay_ms` 单位与语义 | 未实测 | 触发时序可能偏差 |
| TBD-E | `read` 超时/取消、`stop` 能否中断 | 未确认 | 异常时可能挂死 |
| TBD-F | `ofdr` 输入长度约束（2 的幂、最小值） | 未确认 | 长/短数据可能失败 |
| TBD-G | `ofdr` 输出 `len` 与 `dist` 轴公式 | 文档称等长，未实测 | 轴解释可能偏差 |
| TBD-H | 1G/10G 路径选择机制 | 配置项存在，选择逻辑未确认 | 可能连错传输 |
| TBD-I | USB 支持声明 | 文档声称支持，目录无 USB DLL | 不能按 USB 部署 |
| TBD-J | `close` 重复调用、`stop` 前置要求 | 未实测 | 资源释放风险 |
| TBD-K | `wpcap`/`Packet` 是否 1G 必需 | 未确认 | 部署可能缺件 |

风险处理原则：

- 所有 TBD 在代码中走“明确拒绝/空实现”，不做静默兜底；
- 未确认项必须登记到 SRS/SDD，而不是只留在本文件；
- 上机实测结果回写对应章节。

---

## 11. 与 `OFDR.Platform` 二期的关系

### 11.1 映射到 `IScanSource`

若后续确认采用 QT2144 + `qt_acq`：

| `IScanSource` 成员 | 对应实现 |
|---|---|
| `Open` | 加载 DLL、设置 CWD、`qt_acq_open` |
| `StartStream` | `configure` + `start` |
| `BeginChannel` | 递增代次，丢弃旧代次缓冲 |
| `TryTake` | 在超时内等待采集线程交付 `StandardScan` |
| `Stop` | `qt_acq_stop`，幂等 |
| `DroppedFrames` | 本适配器内部的丢帧/失败计数 |

实现位置必须是：

```text
ofdr_platform/src/OFDR.Infrastructure/<Adapter>/
```

厂商类型、`DllImport`、原生句柄、协议字节结构只允许出现在该目录。

### 11.2 当前阶段不做什么

- 不新建空适配器；
- 不引入 NuGet；
- 不复制 `references/` 下的二进制到源码或发行目录；
- 不把本说明书中的推断值写死为默认配置；
- 不在 TBD-15 关闭前实现正式协议。

### 11.3 文档回填要求

- 新需求进 SRS，含编号、优先级、来源、验收方法；
- 实施状态回填 SDD §13.4；
- 验证项回填 SDD §13.2；
- 新增测试数据放 `tests/fixtures/`；
- 行为变更同步回归夹具与验证用例。

---

## 12. 附录

### 12.1 快速参数速查

| 项 | 值 |
|---|---|
| 采样率 | 10 / 50 / 125 / 250 MHz |
| 采集时长 | `> 0` ms |
| 段数 | `>= 1` |
| 触发 | 0 软件 / 1 外部上升沿 / 2 外部下降沿 |
| 每帧 | 4 × int16 = 8 字节 |
| 缓冲区 | `rate × time × 8000 × segs` |
| 通道顺序 | `[ch0, ch1, ch2, ch3]` |
| 数据字节序 | 小端 |
| `fs` | `rate_mhz × 1e6` Hz |
| OFDR 输入 | 三路等长 `double` |
| OFDR 输出 | `dist` / `freq` / `ps`，长度 `len` |
| 结果释放 | 必须 `ofdr_result_free` |

### 12.2 错误码速查

`qt_acq`：

```text
 0  成功
-1  未知 / I/O
-2  SDK
-3  参数无效
-4  句柄无效
-5  空指针
-6  缓冲区过小
-7  未配置
```

`ofdr`：`0` 成功，非 `0` 失败，具体文本见 `error_msg`。

### 12.3 术语

| 术语 | 含义 |
|---|---|
| P 路 | OFDR 探测光路信号 |
| S 路 | OFDR 参考/本振路信号 |
| A / aux | 辅助通道 |
| 交织 | 同一帧内按通道顺序连续存放采样 |
| 反交织 | 把交织帧拆成每通道独立数组 |
| `LsbScale` | int16 码值到物理量的比例系数 |
| `dist_scale` | 距离轴校准系数 |
| CWD | 进程当前工作目录 |

### 12.4 参考资料

- [QT2144_qt_acq_反推说明书.md](./QT2144_qt_acq_反推说明书.md)
- `references/qt2144-rs/qt_acq.h`
- `references/qt2144-rs/qt_acq.py`
- `references/qt2144-rs/readme_ext.md`
- `references/qt2144-rs/config/`
- `references/ofdr-0.1.1-release/ofdr.h`
- `references/ofdr-0.1.1-release/ofdr.py`
- `references/ofdr-0.1.1-release/README_ext.md`
- `docs/SRS_OFDR_Platform_v0.9.md`（§4.9、TBD-15）
- `docs/SDD_OFDR_Platform_v0.9.md`

### 12.5 结论

1. 两个目录是同一厂商 QT2144 方案的**采集端**和**处理端**，不存在直接依赖。
2. 采集端只有 9 个 C 函数，调用顺序简单，但 `read` 是整块阻塞、无超时。
3. 处理端只有 3 个 C 函数，输入输出清晰，但必须手动释放结果。
4. 接入的真正工作量在转换层：通道映射、反交织、类型转换、标定与长度约束。
5. 在 TBD-15 与本文第 10 章未确认项关闭前，只能做离线验证和接口一致性测试，不能进入正式实现。
