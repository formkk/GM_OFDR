# QT2144 qt_acq 反推说明书

> 分析对象：`references/qt2144-rs/`  
> 版本快照：`qt_acq.dll` 0.1.0、`QT_Board.dll` 2.0.1.0、`sdk_version_meta` 1.0.0.0、WinPcap 4.1.0.2980  
> 编写日期：2026-11-02  
> 性质：基于随附头文件、Python 封装、配置文件以及 DLL 二进制静态分析整理的反向工程说明，**不是厂商正式文档**。  
> 结论用途：用于理解现有资料、设计接口一致性测试和评估风险；不能替代硬件协议确认（SRS TBD-15）。

---

## 0. 阅读约定

### 0.1 证据等级

本文对每条关键结论标注证据来源：

| 标记 | 含义 |
|---|---|
| 【A】 | 厂商随附文档、头文件或 Python 源码明确写出 |
| 【B】 | 本次通过 DLL 导出表、导入表、PE 头、反汇编或二进制字符串直接观察到 |
| 【C】 | 由现有证据推断，尚未实测或未经厂商确认 |

### 0.2 本文覆盖范围

覆盖：

- `qt_acq.dll` 的 9 个 C ABI 函数；
- Python 封装 `qt_acq.py` 的调用方式；
- 采集参数、状态流转、缓冲区计算、返回数据格式；
- 随附 `config/` 目录的字段含义；
- DLL 依赖、部署路径、传输方式和已知限制。

不覆盖：

- `QT_Board.dll` 全部 384 个导出函数的完整语义；
- FPGA 内部寄存器映射；
- 1G/10G 网口的完整网络协议；
- 厂商未提供的校准公式与物理量换算。

### 0.3 重要说明

`qt_acq` **没有独立命令行程序**。本文所说的“命令”指 C ABI 函数调用；“命令参数”指这些函数的入参。

---

## 1. 组件定位

### 1.1 一句话描述

`qt_acq` 是一个面向 QT2144 高速采集卡的**一次性分段采集封装**：上层配置采样率、采集时长、段数和触发方式，启动后一次性读取整块原始采样数据，数据为 **int16 小端、4 通道按帧交织**。

它不是流式采集 API，也不是 OFDR 专用协议；底层 `QT_Board.dll` 是一个覆盖多种板卡的通用 SDK。

### 1.2 调用层次

```text
应用代码（Python / C / C# / C++）
        │
        ▼
qt_acq.dll（Rust 实现的 C ABI，9 个导出）
        │  只导入 QT_Board.dll
        ▼
QT_Board.dll（C++ 通用板卡 SDK，384 个导出）
        ├─ QT_Board_PCIe.dll       PCIe 传输
        ├─ QT_Board_Net.dll        1G 网口 TCP 传输
        ├─ QT_Board_10Net.dll      10G 网口原始以太网传输
        ├─ elog.dll                日志
        ├─ pthreadVC2.dll          线程
        └─ libfftw3-3.dll          FFT/DSP
```

【B】`qt_acq.dll` 的导入表只有 `QT_Board.dll` 这一层业务依赖；`QT_Board.dll` 在加载时又同时导入三种传输 DLL。因此**只走网口也不能删除 PCIe 传输 DLL**，否则整条链路加载失败。

### 1.3 版本与架构

| 项目 | 值 | 证据 |
|---|---|---|
| `qt_acq.dll` 版本字符串 | `0.1.0` | 【B】二进制字符串 |
| `QT_Board.dll` 文件版本 | `2.0.1.0` | 【B】版本资源 |
| `sdk_version_meta` | `1.0.0.0` | 【A】配置文件 |
| 架构 | x64（AMD64，8664） | 【B】PE 头 |
| `qt_acq.dll` 导出数量 | 9 | 【B】导出表 |
| `QT_Board.dll` 导出数量 | 384 | 【B】导出表 |
| `QT_Board_Net.dll` 导出数量 | 92 | 【B】导出表 |
| `QT_Board_10Net.dll` 导出数量 | 12 | 【B】导出表 |
| `QT_Board_PCIe.dll` 导出数量 | 10 | 【B】导出表 |

`QT_Board.dll` 的版本资源中公司名、产品名、描述均为 `TODO: <...>`，说明这不是清理过的正式发布版本；稳定性不能按正式 SDK 预期。

---

## 2. 文件清单与部署

### 2.1 随附文件

| 文件 | 作用 | 说明 |
|---|---|---|
| `qt_acq.dll` | 采集封装核心 | Rust 编译的 x64 DLL，9 个 C 导出 |
| `qt_acq.h` | C 头文件 | ABI 声明、错误码、触发常量 |
| `qt_acq.py` | Python 封装 | 使用 `ctypes` 加载 DLL，提供 `QtAcq` 类 |
| `readme_ext.md` | 厂商使用说明 | 只描述封装层，不描述 `config/` 与底层 SDK |
| `QT_Board.dll` | 核心板卡 SDK | 必须存在 |
| `QT_Board_PCIe.dll` | PCIe 传输 | 必须存在，即使不用 PCIe |
| `QT_Board_Net.dll` | 1G 网口传输 | 必须存在 |
| `QT_Board_10Net.dll` | 10G 网口传输 | 必须存在 |
| `elog.dll` | 日志库 | 必须存在 |
| `libfftw3-3.dll` | FFTW 双精度库 | `QT_Board.dll` 直接导入 |
| `libfftw3f-3.dll` | FFTW 单精度库 | 随包提供 |
| `libfftw3l-3.dll` | FFTW 长双精度库 | 随包提供 |
| `libliquid.dll` | DSP 库 | 随包提供 |
| `pthreadGC2.dll` | MinGW pthread | 随包提供 |
| `pthreadVC2.dll` | MSVC pthread | `QT_Board.dll` 直接导入 |
| `wpcap.dll` | WinPcap 抓包库 | 10G 传输依赖 |
| `Packet.dll` | WinPcap 驱动接口 | 10G 传输依赖 |
| `config/` | SDK 配置目录 | 必须存在且路径正确 |
| `config/version/sdk_version_meta` | SDK 版本元数据 | SDK 会读取 |
| `config/Interface.json` | 板卡枚举与寄存器基地址 | 根配置 |
| `config/QT2144/*.json` | 板卡功能、AFE、量程配置 | 型号配置 |
| `config/0/*.json` | 另一套/占位配置 | 内容不自洽，不建议使用 |

### 2.2 推荐部署树

```text
<应用目录>/
  qt_acq.dll
  qt_acq.py                  # 仅 Python 调用需要
  QT_Board.dll
  QT_Board_PCIe.dll
  QT_Board_Net.dll
  QT_Board_10Net.dll
  elog.dll
  libfftw3-3.dll
  libfftw3f-3.dll
  libfftw3l-3.dll
  libliquid.dll
  pthreadGC2.dll
  pthreadVC2.dll
  wpcap.dll
  Packet.dll
  config/
    Interface.json
    QT2144/
      FunctionalConfiguration.json
      AFE.json
      Range.json
    0/
      Interface.json
      AFE.json
      Range.json
      FunctionalConfiguration.json
    version/
      sdk_version_meta
  log/                       # 运行时生成，目录必须可写
```

### 2.3 加载规则

【A】`qt_acq.py` 的 `_load_dll()` 按以下顺序查找 DLL：

1. 环境变量 `QT_ACQ_DLL` 指定的路径；
2. `qt_acq.py` 同目录下的 `qt_acq.dll`；
3. `qt_acq.py` 上一级 `target/release/qt_acq.dll`；
4. 直接写 `qt_acq.dll`，交给系统 DLL 搜索路径。

【B】`qt_acq.dll` 依赖 `QT_Board.dll`，而 `QT_Board.dll` 又依赖其余传输 DLL、`elog`、`pthreadVC2`、`libfftw3-3`。因此：

- 不能只复制 `qt_acq.dll`；
- 不能删除当前不使用的传输 DLL；
- 所有 DLL 建议放在同一目录；
- 应用必须是 x64；
- 需要 MSVC 运行库 `VCRUNTIME140.dll`（以及 `MSVCP140.dll`，由 `QT_Board.dll` 依赖）。

### 2.4 配置与日志路径

【B】`QT_Board.dll` 的二进制字符串包含：

```text
config\
config\version\sdk_version_meta
./config/ExternalDCBiasCalibration.json
log\QT_Board
```

这说明 SDK 按**进程当前工作目录（CWD）**查找配置和写日志，而不是按 DLL 所在目录。由此带来的部署约束：

- 启动应用时必须保证 CWD 下存在 `config/`；
- `log/` 必须可写；
- 如果应用安装在 `Program Files` 下，默认 CWD 通常不可写，需要显式设置工作目录或改用可写目录；
- 仅把 DLL 放在应用目录并不够，CWD 仍然决定配置查找位置。

### 2.5 首次加载自检建议

1. 确认进程为 x64；
2. 确认 `qt_acq.dll` 能加载；
3. 确认 `QT_Board.dll` 及其全部依赖能被找到；
4. 确认 CWD 下存在 `config/Interface.json`；
5. 确认 `config/version/sdk_version_meta` 可读；
6. 确认 `log/` 可写；
7. 再尝试 `qt_acq_open`。

任何一步失败，都可能在 `qt_acq_open` 或配置读取阶段表现为 `-2 SDK` 或 `-1 未知/IO`。

---

## 3. C ABI 总览

### 3.1 调用约定与类型

【A】头文件使用普通 C 声明，没有 `__stdcall` 等修饰。Windows x64 只有一种统一的调用约定，因此 C/C++/C#/Python 按默认 C ABI 调用即可。

| C 类型 | 含义 | 备注 |
|---|---|---|
| `uint32_t` | 32 位无符号整数 | 采样率、时长、段数、容量、延时 |
| `int` | 32 位有符号整数 | 返回码、触发方式 |
| `void *` | 缓冲区指针 | 由调用方分配 |
| `size_t` | 指针宽度无符号整数 | 仅用于字符串缓冲区长度 |
| `qt_acq_handle` | 不透明句柄指针 | 由 `qt_acq_open` 返回 |

### 3.2 导出函数

【B】`qt_acq.dll` 的 9 个导出（按序号）：

| 序号 | 函数 | RVA |
|---|---|---|
| 0 | `qt_acq_calc_buffer_bytes` | `0x1C20` |
| 1 | `qt_acq_close` | `0x1E70` |
| 2 | `qt_acq_configure` | `0x1ED0` |
| 3 | `qt_acq_last_error` | `0x21D0` |
| 4 | `qt_acq_open` | `0x24E0` |
| 5 | `qt_acq_read` | `0x2760` |
| 6 | `qt_acq_start` | `0x2AA0` |
| 7 | `qt_acq_stop` | `0x2D20` |
| 8 | `qt_acq_version` | `0x2D60` |

### 3.3 返回码

【A】所有接口返回 `0` 表示成功，负值表示失败：

| 返回码 | 宏 | 含义 |
|---|---|---|
| `0` | `QT_ACQ_OK` | 成功 |
| `-1` | `QT_ACQ_ERR_UNKNOWN` | 未知错误 / I/O 错误 |
| `-2` | `QT_ACQ_ERR_SDK` | 底层 SDK 返回错误 |
| `-3` | `QT_ACQ_ERR_INVALID_ARGUMENT` | 参数无效 |
| `-4` | `QT_ACQ_ERR_INVALID_HANDLE` | 句柄无效 |
| `-5` | `QT_ACQ_ERR_NULL_POINTER` | 空指针 |
| `-6` | `QT_ACQ_ERR_BUFFER_TOO_SMALL` | 缓冲区过小 |
| `-7` | `QT_ACQ_ERR_NOT_CONFIGURED` | 尚未配置 |

### 3.4 触发常量

【A】

| 常量 | 值 | 含义 |
|---|---|---|
| `QT_ACQ_TRIG_SOFTWARE` | `0` | 软件触发 |
| `QT_ACQ_TRIG_EXT_RISING` | `1` | 外部触发上升沿 |
| `QT_ACQ_TRIG_EXT_FALLING` | `2` | 外部触发下降沿 |

---

## 4. 生命周期与状态机

### 4.1 推荐调用顺序

```text
qt_acq_open
   │
   ▼
qt_acq_configure
   │
   ▼
qt_acq_start
   │
   ▼
qt_acq_read（阻塞，直到本次采集完成）
   │
   ▼
qt_acq_stop
   │
   ▼
qt_acq_close
```

### 4.2 状态说明

| 状态 | 进入方式 | 允许操作 | 备注 |
|---|---|---|---|
| 未打开 | 初始 | `open`、`calc_buffer_bytes`、`version`、`last_error` | `calc/version` 不需要句柄 |
| 已打开 | `open` 成功 | `configure`、`close` | 尚未有采集参数 |
| 已配置 | `configure` 成功 | `start`、再次 `configure`、`close` | `read` 需要此状态 |
| 已启动 | `start` 成功 | `read`、`stop`、`close` | 外部触发时可能等待触发 |
| 已停止 | `stop` 成功 | 再次 `configure`、`start`、`close` | `stop` 幂等性未文档化 |
| 已关闭 | `close` 成功 | 不可再用句柄 | 不要重复 `close` |

### 4.3 状态相关注意

- 【B】`qt_acq_read` 在未配置时返回 `-7`；因此 `configure` 必须在 `read` 前成功。
- 【C】`read` 是否强制要求先 `start`，反汇编中没有看到独立的“已启动”标志检查；正常流程仍应先 `start`。
- 【C】`configure` 是否能在 `start` 后直接调用未文档化；建议先 `stop` 再重新配置。
- 【C】`stop` 与 `close` 的幂等性未文档化；建议只调用一次，并用状态标志保护。
- 【A】Python 封装的 `close()` 忽略返回值；C 调用方应自行检查返回值。

---

## 5. 函数参考

### 5.1 `qt_acq_open`

```c
int qt_acq_open(uint32_t card_index, qt_acq_handle *out_handle);
```

**作用**：打开指定序号的采集卡，返回不透明句柄。

**参数**

| 参数 | 类型 | 单位 | 合法值 | 说明 |
|---|---|---|---|---|
| `card_index` | `uint32_t` | 无 | 0 起 | 0 表示第一块卡；随附配置只启用 0 号 |
| `out_handle` | `qt_acq_handle *` | 无 | 非空 | 成功时写入句柄 |

**返回**

- `0`：成功；
- `-5`：`out_handle` 为空；
- `-2`：底层 SDK 打开失败；
- `-1`：I/O 或未知错误；
- 其他负值按错误码表解释。

**行为**

【B】导入表显示 `qt_acq.dll` 使用了以下底层函数；其中 `open` 阶段至少涉及 `QT_BoardOpenLog`、`QT_BoardOpenBoard`，AFE/EEPROM/风扇/复位等初始化在打开/配置阶段完成：

- `QT_BoardOpenLog`
- `QT_BoardOpenBoard`
- `QT_BoardIfSupportUseEEPROM`、`QT_BoardSetAFEEEPROM`
- `QT_BoardIfSupportFanControl`、`QT_BoardSetFanControl`
- `QT_BoardIfSupportOffsetAdjust`、`QT_BoardSetSoftwareReset`

**注意**

- 打开前必须满足第 2 章的部署条件；
- `card_index` 与 `config/Interface.json` 的 `CardIndx` 相关；
- 打开失败时不要继续调用 `configure`；
- 建议把句柄初始化为 `NULL`，失败时不要 `close`。

**示例**

```c
qt_acq_handle h = NULL;
int rc = qt_acq_open(0, &h);
if (rc != 0) {
    /* 读取 qt_acq_last_error 并处理 */
}
```

---

### 5.2 `qt_acq_close`

```c
int qt_acq_close(qt_acq_handle handle);
```

**作用**：关闭采集卡并释放句柄相关资源。

**参数**

| 参数 | 类型 | 合法值 | 说明 |
|---|---|---|---|
| `handle` | `qt_acq_handle` | 非空且已打开 | 由 `open` 返回 |

**返回**

- `0`：成功；
- `-4`：句柄无效；
- `-5`：句柄为空；
- `-2` / `-1`：底层关闭失败。

**行为**

【B】导入表显示会调用 `QT_BoardCloseBoard` 与 `QT_BoardCloseLog`。

**注意**

- 【C】重复 `close` 的行为未文档化；不要重复调用；
- 【A】Python 封装的 `close()` 忽略返回值；
- 建议在 `finally` / `using` / `try-finally` 中调用。

---

### 5.3 `qt_acq_configure`

```c
int qt_acq_configure(qt_acq_handle handle, uint32_t sample_rate_mhz,
                     uint32_t acq_time_ms, uint32_t seg_count, int trigger,
                     uint32_t trigger_delay_ms);
```

**作用**：设置本次采集的采样率、时长、段数、触发方式和触发延时。

**参数**

| 参数 | 类型 | 单位 | 合法值 | 违反时的行为 |
|---|---|---|---|---|
| `handle` | `qt_acq_handle` | 无 | 已打开 | `-4` |
| `sample_rate_mhz` | `uint32_t` | MHz | `10`、`50`、`125`、`250` | `-3`，错误信息给出合法值 |
| `acq_time_ms` | `uint32_t` | ms | `> 0` | `-3` |
| `seg_count` | `uint32_t` | 段 | `>= 1` | `-3` |
| `trigger` | `int` | 无 | `0`、`1`、`2` | `-3` |
| `trigger_delay_ms` | `uint32_t` | ms | `0 .. 2^32-1` | 传给底层；是否有限制未文档化 |

**返回**

- `0`：成功；
- `-3`：参数无效或容量溢出；
- `-4`：句柄无效；
- `-5`：句柄为空；
- `-2`：底层配置失败；
- `-1`：I/O 或未知错误。

**参数约束说明**

【B】反汇编确认采样率只接受 `0x0A`、`0x32`、`0x7D`、`0xFA`，即 `10`、`50`、`125`、`250` MHz。`readme_ext.md` 没有写明这一点。

【B】反汇编确认：

- `acq_time_ms == 0` 会被拒绝；
- `seg_count == 0` 会被拒绝；
- `trigger` 只接受 `0/1/2`；
- 每段字节数和总字节数都会做 `u32` 溢出检查。

**行为**

【B】导入表显示会调用以下底层函数（具体调用顺序不保证）：

- `QT_BoardSetupModeRecStdMulti`
- `QT_BoardSetupRecWorkingMode`
- `QT_BoardSetupRecChannelMode`
- `QT_BoardSetupRecClockMode`
- `QT_BoardSetupTrigRecExternal`
- `QT_BoardSetupTrigRecSoftware`
- `QT_BoardSetTriggerDelay`
- `QT_BoardSetTheDataType`
- `QT_BoardSetTheExtractionMultiple`
- `QT_BoardSetSoftwareBypassFunction`
- `QT_BoardSetBiasAdjustment`
- `QT_BoardSetAFEGain`
- `QT_BoardSetAFEJSON`
- `QT_BoardSetAFEEEPROM`
- `QT_BoardIfSupportUseEEPROM`
- `QT_BoardIfSupportFanControl`
- `QT_BoardSetFanControl`
- `QT_BoardSetSoftwareReset`
- `QT_BoardGetCardADCChNumbers`
- `QT_BoardObtainTheADCBlockNum`

这说明 `configure` 不只是记录参数，还会实际改写板卡工作模式、通道、时钟、触发、数据格式和 AFE 相关设置。

**注意**

- 【C】`trigger_delay_ms` 的底层实现是 `QT_BoardSetTriggerDelay`，单位是否为 ms、是否有上限，资料未说明；本文只能确认外层把它作为 `uint32` 透传。
- 【C】`configure` 若在 `start` 后调用，底层是否允许未文档化；建议先 `stop`。
- 【A】Python 封装的 `configure_file` 会按固定 5 行读取参数，见 5.12。

---

### 5.4 `qt_acq_start`

```c
int qt_acq_start(qt_acq_handle handle);
```

**作用**：启动采集。对软件触发来说是立即开始；对硬件触发来说是进入等待触发状态。

**参数**

| 参数 | 类型 | 合法值 |
|---|---|---|
| `handle` | `qt_acq_handle` | 已配置 |

**返回**

- `0`：成功；
- `-4` / `-5`：句柄问题；
- `-2` / `-1`：底层启动失败。

**行为**

【B】导入表显示会调用 `QT_BoardSetupRecCollectData`。

**注意**

- 【C】`start` 成功不等于数据已经就绪；数据在 `read` 中等待；
- 外部触发模式下，若触发源一直不来，`read` 可能长时间阻塞；
- 没有独立的“采集完成”回调或状态查询接口。

---

### 5.5 `qt_acq_read`

```c
int qt_acq_read(qt_acq_handle handle, void *buffer, uint32_t capacity,
                uint32_t *out_len);
```

**作用**：读取本次采集的整块原始数据。

**参数**

| 参数 | 类型 | 合法值 | 说明 |
|---|---|---|---|
| `handle` | `qt_acq_handle` | 已配置 | 由 `open` 返回 |
| `buffer` | `void *` | 非空 | 调用方分配 |
| `capacity` | `uint32_t` | `>= required_bytes` | 缓冲区容量（字节） |
| `out_len` | `uint32_t *` | 非空 | 成功时写入实际返回长度 |

**返回**

- `0`：成功；
- `-4`：句柄无效；
- `-5`：`buffer` 或 `out_len` 为空；
- `-6`：`capacity` 小于所需字节数；
- `-7`：尚未 `configure`；
- `-2`：底层取数失败；
- `-1`：I/O 或未知错误。

**行为**

【B】反汇编观察到的关键语义：

1. 校验句柄、`buffer`、`out_len`；
2. 若未配置，返回 `-7`；
3. 按已保存的配置重新计算 `required_bytes`；
4. 若 `capacity < required_bytes`，返回 `-6`；
5. **只调用一次**底层 `QT_BoardSetupRecGetData`；
6. 若底层返回非负，直接把 `out_len` 设置为 `required_bytes`。

**这意味着**

- 没有循环读取；
- 没有分片回调；
- 没有原生超时参数；
- 没有取消参数；
- 不校验底层是否真的填满了整个缓冲区；
- 若底层只填了部分数据，外层仍报告完整长度。

【B】底层调用：`QT_BoardSetupRecGetData`。

**缓冲区与清零**

- 【A】Python 封装使用 `ctypes.create_string_buffer(capacity)`，该缓冲区默认清零；
- 【C】C 调用方若使用 `malloc`，缓冲区不会自动清零；底层短读时尾部会是未初始化数据。建议使用 `calloc` 或先 `memset`。

**建议**

- 用 `qt_acq_calc_buffer_bytes` 计算容量，不要手写公式；
- 分配后清零；
- 校验 `out_len == required_bytes`；
- 对 `out_len` 做 `<= capacity` 的防御性检查；
- 建议按 64 字节对齐分配，虽然 API 没有明确要求。

**示例**

```c
uint32_t cap = 0;
qt_acq_calc_buffer_bytes(50, 8, 1, &cap);

void *buf = calloc(1, cap);          /* 清零，避免短读暴露脏数据 */
uint32_t got = 0;

int rc = qt_acq_read(h, buf, cap, &got);
if (rc == 0) {
    /* got 应为 cap；数据格式见第 6 章 */
}
free(buf);
```

---

### 5.6 `qt_acq_stop`

```c
int qt_acq_stop(qt_acq_handle handle);
```

**作用**：停止采集。

**参数**

| 参数 | 类型 | 合法值 |
|---|---|---|
| `handle` | `qt_acq_handle` | 已打开 |

**返回**

- `0`：成功；
- `-4` / `-5`：句柄问题；
- `-2` / `-1`：底层停止失败。

**行为**

【B】`qt_acq.dll` 的导入表中只有一个采集控制函数 `QT_BoardSetupRecCollectData`，没有独立的停止函数。`start` 与 `stop` 很可能都通过该函数的不同参数实现，具体参数由封装内部决定。

**注意**

- 【C】`stop` 是否能从另一个线程打断正在阻塞的 `read`，未文档化；
- 【C】重复 `stop` 是否安全，未文档化；
- 建议在 `read` 返回后调用 `stop`，并在异常路径的 `finally` 中调用；
- 若 `read` 永久阻塞，优先考虑进程级看门狗或重启设备，而不是假设 `stop` 一定能解除阻塞。

---

### 5.7 `qt_acq_calc_buffer_bytes`

```c
int qt_acq_calc_buffer_bytes(uint32_t sample_rate_mhz, uint32_t acq_time_ms,
                             uint32_t seg_count, uint32_t *out_bytes);
```

**作用**：计算给定采集参数所需的缓冲区字节数。

**参数**

| 参数 | 类型 | 单位 | 合法值 |
|---|---|---|---|
| `sample_rate_mhz` | `uint32_t` | MHz | `10`、`50`、`125`、`250` |
| `acq_time_ms` | `uint32_t` | ms | `> 0` |
| `seg_count` | `uint32_t` | 段 | `>= 1` |
| `out_bytes` | `uint32_t *` | 字节 | 非空 |

**返回**

- `0`：成功；
- `-3`：参数无效或结果超出 `u32`；
- `-5`：`out_bytes` 为空。

**公式**

```text
每段字节数 = sample_rate_mhz × acq_time_ms × 8000
总字节数   = 每段字节数 × seg_count
```

其中：

```text
8000 = 1000 点/ms/MHz × 4 通道 × 2 字节
```

【B】反汇编中乘数为 `0x1F40`，即 `8000`。`qt_acq_read` 内部也按同一公式重新计算。

**示例**

| 采样率 | 时长 | 段数 | 所需字节 | 每通道点数 |
|---|---|---|---|---|
| 50 MHz | 8 ms | 1 | 3,200,000 B ≈ 3.05 MiB | 400,000 |
| 250 MHz | 10 ms | 1 | 20,000,000 B ≈ 19.07 MiB | 2,500,000 |
| 10 MHz | 1 ms | 1 | 80,000 B | 10,000 |
| 125 MHz | 20 ms | 2 | 40,000,000 B ≈ 38.15 MiB | 5,000,000（合计） |
| 250 MHz | 1000 ms | 1 | 2,000,000,000 B ≈ 1.86 GiB | 250,000,000 |

**溢出与限制**

【B】二进制中存在以下错误信息：

```text
segment of <X> bytes exceeds u32 limit
total data of <X> bytes exceeds u32 limit
```

说明：

- 每段字节数先做一次 `u32` 检查；
- 总字节数再做一次 `u32` 检查；
- 只检查 `u32`，不检查物理内存是否足够；
- 2 GB 级别的请求可以通过 `u32` 检查，但实际分配很可能失败。

**未计入的因素**

- 配置中的 `frame_header_bytes_number: 64`；
- 配置中的 `GigabitNet_BYTE_ALIGNMENT: 64`；
- DMA 填充、段间对齐；
- 底层 SDK 可能存在的额外头部。

因此该函数返回的是**纯采样数据字节数**。如果实际数据包含帧头或填充，`capacity` 需要相应增加，具体增量必须实测确认。

---

### 5.8 `qt_acq_last_error`

```c
int qt_acq_last_error(char *buf, size_t cap);
```

**作用**：读取最近一次错误的文本描述。

**参数**

| 参数 | 类型 | 说明 |
|---|---|---|
| `buf` | `char *` | 调用方提供的文本缓冲区 |
| `cap` | `size_t` | 缓冲区容量 |

**返回**

- 按 Python 封装的实际用法，返回写入的字节数或文本长度；`<= 0` 视为没有错误信息；【B】
- 具体返回值语义未在头文件说明。

**使用方式**

```c
char msg[512];
qt_acq_last_error(msg, sizeof(msg));
```

**注意**

- 应在失败后立即调用；
- 是否线程局部未文档化；不要跨线程依赖“最近错误”；
- 建议缓冲区至少 512 字节；
- Python 封装会自动把错误文本拼进异常信息。

---

### 5.9 `qt_acq_version`

```c
int qt_acq_version(char *buf, size_t len);
```

**作用**：读取 `qt_acq` 封装自身的版本字符串。

**参数**

| 参数 | 类型 | 说明 |
|---|---|---|
| `buf` | `char *` | 文本缓冲区 |
| `len` | `size_t` | 缓冲区容量 |

**返回**

- 按 Python 封装的实际用法，成功写入版本字符串；具体返回值未在头文件说明。

**当前值**

【B】二进制字符串中为 `0.1.0`。

**注意**

- 这是**封装版本**，不是 `QT_Board.dll` 版本，也不是板卡固件版本；
- Python 封装使用 64 字节缓冲区；
- 若需要板卡固件版本，需要直接调用 `QT_BoardGet...Version` 系列函数，`qt_acq` 没有暴露。

---

### 5.10 Python 封装 `QtAcq`

【A】`qt_acq.py` 提供以下方法：

| 方法 | 说明 |
|---|---|
| `QtAcq(card_index=0)` | 构造时自动 `open` |
| `open()` | 打开设备；已打开时直接返回 |
| `close()` | 关闭设备；忽略底层返回值 |
| `configure(rate, time, segs, trigger=0, delay=0)` | 配置并记录参数 |
| `configure_file(path)` | 从 5 行文本读取参数 |
| `start()` | 开始采集 |
| `read(capacity=None)` | 读取数据，返回 `bytes` |
| `stop()` | 停止采集 |
| `version()` | 返回封装版本字符串 |
| `required_bytes(rate, time, segs)` | 静态方法，调用原生计算 |

**上下文管理器**

```python
with QtAcq() as dev:
    ...
```

退出时只调用 `close()`，不会自动 `stop()`。因此仍应在 `finally` 中显式 `stop()`。

**示例**

```python
import sys
sys.path.insert(0, r"<qt2144-rs 目录>")

from qt_acq import QtAcq, TRIGGER_EXT_RISING

RATE_MHZ = 50
TIME_MS = 8
SEGS = 1
TRIGGER = TRIGGER_EXT_RISING
DELAY_MS = 1

dev = QtAcq(card_index=0)
try:
    print(dev.version())
    dev.configure(RATE_MHZ, TIME_MS, SEGS, TRIGGER, DELAY_MS)
    n = dev.required_bytes(RATE_MHZ, TIME_MS, SEGS)
    print("required bytes:", n)

    dev.start()
    raw = dev.read(n)
finally:
    dev.stop()
    dev.close()
```

**注意**

- `read(capacity=None)` 需要先 `configure`，否则抛异常；
- `read(capacity=...)` 仍要求容量不小于原生所需值，否则返回 `-6`；
- `required_bytes` 是静态方法，但仍会加载 DLL；不需要打开设备；
- Python 进程必须是 64 位，才能加载 x64 DLL。

---

### 5.11 C# / .NET P/Invoke 示例

> 本项目是 .NET 平台，以下示例仅说明如何调用；是否进入正式代码需按 SRS/TBD-15 决策。

```csharp
using System;
using System.Runtime.InteropServices;

internal static class QtAcqNative
{
    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qt_acq_open(uint cardIndex, out IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qt_acq_close(IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qt_acq_configure(
        IntPtr handle, uint rateMhz, uint timeMs, uint segCount, int trigger, uint delayMs);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qt_acq_start(IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qt_acq_read(
        IntPtr handle, IntPtr buffer, uint capacity, out uint outLen);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qt_acq_stop(IntPtr handle);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qt_acq_calc_buffer_bytes(
        uint rateMhz, uint timeMs, uint segCount, out uint outBytes);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qt_acq_last_error(IntPtr buf, UIntPtr cap);

    [DllImport("qt_acq.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int qt_acq_version(IntPtr buf, UIntPtr len);
}
```

调用要点：

```csharp
IntPtr h = IntPtr.Zero;
int rc = QtAcqNative.qt_acq_open(0, out h);
if (rc != 0) throw new InvalidOperationException("open failed");

uint bytes;
rc = QtAcqNative.qt_acq_calc_buffer_bytes(250, 10, 1, out bytes);
if (rc != 0) throw new InvalidOperationException("calc failed");
if (bytes > int.MaxValue) throw new InvalidOperationException("buffer too large");

IntPtr buf = Marshal.AllocHGlobal((int)bytes);
try
{
    // AllocHGlobal 不清零；若担心短读，先清零。
    // 可用 Marshal.Copy(new byte[(int)bytes], 0, buf, (int)bytes) 或自行 P/Invoke ZeroMemory。
    rc = QtAcqNative.qt_acq_read(h, buf, bytes, out uint got);
    if (rc != 0) throw new InvalidOperationException("read failed");
    // got 应为 bytes；数据解析见第 6 章。
}
finally
{
    Marshal.FreeHGlobal(buf);
    QtAcqNative.qt_acq_close(h);
}
```

注意：

- .NET 进程必须 x64；
- `CallingConvention.Cdecl` 在 x64 上等价于平台默认约定，写上更明确；
- `qt_acq_last_error` 的 `size_t` 用 `UIntPtr` 映射；
- `out_len` 是 `uint32`，不是 `nint`；
- `Marshal.AllocHGlobal` 不保证 64 字节对齐，也不清零；如需对齐，自行分配并取整；
- 不要用 `byte[]` 直接传 `IntPtr` 而不固定（pinning），否则 GC 可能移动内存；
- 大缓冲区（数十 MB）建议复用固定缓冲区，避免每次 `read` 都分配 + 复制。

---

### 5.12 `configure_file` 的 5 行格式

【A】Python 封装的 `configure_file(path)` 读取非空行，至少 5 行，依次解释为：

```text
第 1 行：sample_rate_mhz      采样率，MHz，合法值 10/50/125/250
第 2 行：acq_time_ms          采集时长，ms，> 0
第 3 行：seg_count            段数，>= 1
第 4 行：trigger              触发方式，0/1/2
第 5 行：trigger_delay_ms     触发延时，ms
```

示例：

```text
250
10
1
0
0
```

注意：

- 空行会被忽略；
- 不支持注释；
- 每行必须是可被 `int()` 解析的十进制整数；
- 文件编码按 UTF-8 读取；
- 带 BOM 的文件可能导致第一行解析失败。

---

## 6. 返回数据格式

### 6.1 基本格式

【A】文档明确：

- 采样值：16 位有符号整数；
- 字节序：小端；
- 通道：多通道按帧交织；
- 示例为 4 路：`[ch0, ch1, ch2, ch3]`。

【B】缓冲区公式进一步确认每帧固定为 4 通道 × 2 字节 = 8 字节。

### 6.2 帧布局

```text
帧内偏移（字节）:
  0    2    4    6
  ch0  ch1  ch2  ch3
  int16 LE，每路 2 字节
```

第 `i` 帧、第 `k` 通道的字节偏移：

```text
offset = i × 8 + k × 2
```

帧数：

```text
n_frames = out_len / 8
```

每通道点数：

```text
samples_per_channel = n_frames
```

### 6.3 多段数据布局

【C】现有资料没有明确说明多段数据在缓冲区中的排列方式。结合 `required_bytes` 公式和一次整块读取的行为，最可能的布局是：

```text
[段0 全部帧][段1 全部帧]...[段N-1 全部帧]
```

每段帧数：

```text
frames_per_segment = sample_rate_mhz × acq_time_ms × 1000
```

第 `s` 段起始帧：

```text
frame_offset = s × frames_per_segment
```

但这只是由公式推断的布局。以下情况会破坏该假设：

- 每段带 64 字节帧头；
- 段间有对齐填充；
- 底层返回的是“按块”而不是“按段”排列；
- `frame_header_bytes_number: 64` 实际生效。

因此，在未实测确认前，**不要把多段数据直接按上述公式切片**。

### 6.4 通道与 P/S/A

【A】文档明确：通道与用途的对应关系（如 P/S/A）按实际硬件接线确定。

因此：

- `ch0` 不一定就是 P；
- `ch1` 不一定就是 S；
- `ch2` 不一定就是 A；
- 不能在代码中写死映射，必须由配置或接线确认。

### 6.5 物理量换算

【A】文档没有给出 int16 码值到物理量的换算公式。

【B】`config/QT2144/AFE.json` 有 offset/gain/attenuation，`Range.json` 有每通道每量程的 factor，但厂商没有说明：

- offset 是 DAC 码值还是电压；
- gain/attenuation 是寄存器码还是 dB；
- factor 的单位；
- factor 与量程、增益、ADC 满量程的关系；
- 是否需要先做 offset 校正再乘 factor。

因此：

- 不能直接把 `Range.json` 的 factor 当作 `LsbScale`；
- 不能猜测“码值 × factor = 电压”；
- 物理量换算必须等厂商给出公式或实测标定后再实现。

### 6.6 数据中是否包含帧头

【B】`FunctionalConfiguration.json` 中有：

```json
"frame_header_bytes_number": 64
```

【B】`GigabitNet_BYTE_ALIGNMENT` 也是 64。

但【B】`qt_acq_calc_buffer_bytes` 的公式只计算纯采样字节，没有加 64 字节帧头或对齐。

两种可能：

1. 底层 `QT_BoardSetupRecGetData` 返回的是去掉帧头后的纯采样数据；
2. 配置中的 64 字节头不适用于当前路径，或只适用于其他板卡/传输。

在未实测确认前：

- 用 `required_bytes` 分配；
- 读取后先检查 `out_len`；
- 用已知信号（直流、短接、正弦）检查帧边界；
- 如果发现每帧/每段多出 64 字节，再调整解析逻辑。

### 6.7 数据解析示例（Python）

```python
import numpy as np

raw = data                      # qt_acq read 返回的 bytes
N_CHAN = 4
frame_bytes = N_CHAN * 2

n_frames = len(raw) // frame_bytes
frames = np.frombuffer(raw, dtype="<i2")[: n_frames * N_CHAN]
frames = frames.reshape(n_frames, N_CHAN)

ch0 = frames[:, 0].astype(np.float64)
ch1 = frames[:, 1].astype(np.float64)
ch2 = frames[:, 2].astype(np.float64)
```

### 6.8 数据解析示例（C）

```c
#include <stdint.h>

uint32_t n_frames = got / 8;
const int16_t *pcm = (const int16_t *)buf;

for (uint32_t i = 0; i < n_frames; ++i) {
    int16_t ch0 = pcm[i * 4 + 0];
    int16_t ch1 = pcm[i * 4 + 1];
    int16_t ch2 = pcm[i * 4 + 2];
    int16_t ch3 = pcm[i * 4 + 3];
    /* 使用四个通道 */
}
```

注意：`int16_t *` 强转要求缓冲区按 2 字节对齐；如果使用 `malloc/calloc`，自然满足。若使用自定义对齐分配，需确认。

### 6.9 数据完整性检查

建议在第一次接入时执行：

1. `out_len == required_bytes`；
2. `required_bytes % 8 == 0`；
3. 直流/短接输入下，各通道均值接近同一基线；
4. 输入已知正弦，FFT 峰值频率符合预期；
5. 多段采集时，检查每段边界是否连续；
6. 对比单段与多段总字节数是否严格符合公式；
7. 若发现 64 字节偏移，记录并回写文档。

---

## 7. 参数合法范围与容量规划

### 7.1 采样率

【B】只允许：

| 值 | 说明 |
|---|---|
| 10 MHz | 最低档 |
| 50 MHz | readme 示例使用 |
| 125 MHz | 中间档 |
| 250 MHz | 最高档，`qt_acq.py` demo 使用 |

其他值返回 `-3`，错误信息会列出合法值。

### 7.2 采集时长

【B】必须 `> 0`。

【C】没有文档化的最大值；实际受以下限制：

- 每段字节数不超过 `u32`；
- 总字节数不超过 `u32`；
- 进程可用内存；
- 底层 DMA/DDR 缓冲；
- 采集卡实际支持的最长时间。

### 7.3 段数

【B】必须 `>= 1`。

【C】最大段数未文档化；受总字节数、DDR 和底层模式限制。

### 7.4 触发延时

【B】类型为 `uint32`，单位按函数名推断为 ms。

【C】底层 `QT_BoardSetTriggerDelay` 的实际单位和范围未文档化；不要假设支持任意大的延时。

### 7.5 容量上限速查

按 `u32` 理论上限计算（不考虑内存和硬件限制）：

| 采样率 | 每秒字节数 | 单段理论最长（约） |
|---|---|---|
| 10 MHz | 80,000 B/s | 53,687 ms |
| 50 MHz | 400,000 B/s | 10,737 ms |
| 125 MHz | 1,000,000 B/s | 4,294 ms |
| 250 MHz | 2,000,000 B/s | 2,147 ms |

这些只是 `u32` 边界，不是推荐值。实际应远低于此，并按可用内存规划。

---

## 8. 底层调用链（供排障使用）

### 8.1 `qt_acq.dll` 导入的 `QT_Board.dll` 函数

【B】完整导入列表：

```text
QT_BoardCloseBoard
QT_BoardCloseLog
QT_BoardGetCardADCChNumbers
QT_BoardIfSupportFanControl
QT_BoardIfSupportOffsetAdjust
QT_BoardIfSupportUseEEPROM
QT_BoardObtainTheADCBlockNum
QT_BoardOpenBoard
QT_BoardOpenLog
QT_BoardSetAFEEEPROM
QT_BoardSetAFEGain
QT_BoardSetAFEJSON
QT_BoardSetBiasAdjustment
QT_BoardSetFanControl
QT_BoardSetSoftwareBypassFunction
QT_BoardSetSoftwareReset
QT_BoardSetTheDataType
QT_BoardSetTheExtractionMultiple
QT_BoardSetTriggerDelay
QT_BoardSetupModeRecStdMulti
QT_BoardSetupRecChannelMode
QT_BoardSetupRecClockMode
QT_BoardSetupRecCollectData
QT_BoardSetupRecGetData
QT_BoardSetupRecWorkingMode
QT_BoardSetupTrigRecExternal
QT_BoardSetupTrigRecSoftware
```

### 8.2 可能的功能映射

| qt_acq 函数 | 底层函数（观察/推断） |
|---|---|
| `open` | `QT_BoardOpenLog`、`QT_BoardOpenBoard`，以及 AFE/EEPROM/风扇/复位初始化 |
| `configure` | `QT_BoardSetupModeRecStdMulti`、`QT_BoardSetupRecWorkingMode`、`QT_BoardSetupRecChannelMode`、`QT_BoardSetupRecClockMode`、`QT_BoardSetupTrigRecExternal/Software`、`QT_BoardSetTriggerDelay`、`QT_BoardSetTheDataType`、`QT_BoardSetTheExtractionMultiple`、`QT_BoardSetSoftwareBypassFunction`、`QT_BoardSetBiasAdjustment`、`QT_BoardSetAFEGain`、`QT_BoardSetAFEJSON`、`QT_BoardSetAFEEEPROM`、`QT_BoardIfSupportUseEEPROM`、`QT_BoardIfSupportFanControl`、`QT_BoardSetFanControl`、`QT_BoardSetSoftwareReset`、`QT_BoardGetCardADCChNumbers`、`QT_BoardObtainTheADCBlockNum` |
| `start` | `QT_BoardSetupRecCollectData` |
| `read` | `QT_BoardSetupRecGetData` |
| `stop` | 很可能也是 `QT_BoardSetupRecCollectData` 的不同参数；导入表没有独立停止函数 |
| `close` | `QT_BoardCloseBoard`、`QT_BoardCloseLog` |

### 8.3 排障含义

- 如果错误信息是 `QT_Board SDK error in <函数>: code <N>`，说明已经进入底层 SDK，问题在板卡/驱动/配置，不在 Python 封装；
- 如果错误信息是 `Net card I/O error`、`Net card protocol mismatch`、`Net card read timeout`，说明走的是网口路径，问题在网络/协议/超时；
- 如果错误信息是 `invalid sample rate` 等，问题在参数；
- 如果错误信息包含 `buffer has ...` 或 `... bytes are required`，问题在容量；
- 如果 `qt_acq_open` 就失败，优先检查 DLL 依赖、CWD、`config/`、驱动。

---

## 9. 传输方式与硬件依赖

### 9.1 三种传输

| 传输 | DLL | 依赖 | 说明 |
|---|---|---|---|
| PCIe | `QT_Board_PCIe.dll` | `SETUPAPI.dll` | 需要 PCIe 驱动；`QTXdmaOpenBoard` 等 |
| 1G 网口 | `QT_Board_Net.dll` | `WS2_32.dll` | TCP socket；日志中有 `serverip` / `serverport`，但随附配置没有 IP/端口 |
| 10G 网口 | `QT_Board_10Net.dll` | `wpcap.dll`、`Packet.dll`、`IPHLPAPI.dll` | 原始以太网抓包；字符串中有 `udp and src port 1234 and src host 192.168.5.20`，以及 magic/校验和错误 |

### 9.2 10G 路径的特殊要求

【B】`QT_Board_10Net.dll` 导入 `wpcap.dll`，而随附的 `wpcap.dll` / `Packet.dll` 版本为 `4.1.0.2980`（WinPcap，2013 年）。

这意味着：

- 需要安装 WinPcap 或兼容的 Npcap；
- 原始抓包通常需要管理员权限；
- Win10/11 上老 WinPcap 可能存在兼容性问题；
- 需要指定正确的网卡；
- 可能需要关闭“只抓本机流量”的限制；
- 10G 路径的协议细节未文档化。

### 9.3 1G 路径的未知项

【B】`QT_Board_Net.dll` 日志字符串包含：

```text
serverip is <...>, serverport is <...>
```

但随附的 `config/Interface.json` 只有寄存器基地址，没有 IP/端口。可能来源：

- 板卡 EEPROM；
- SDK 默认值；
- 其他未随附的配置文件；
- 运行时由其他 API 设置。

`qt_acq` 没有暴露设置 IP/端口的函数，因此 1G 路径的实际 IP/端口配置方式需要厂商确认。

### 9.4 网口协议不是本文内容

本文只说明 `qt_acq` 的调用接口。1G/10G 的帧格式、命令字、magic、校验和、超时等属于底层传输协议，随附资料没有完整协议文档。不能根据字符串片段猜测实现。

---

## 10. 配置文件参考

### 10.1 `config/Interface.json`

【A/B】根配置文件，示例字段：

| 字段 | 示例 | 说明 |
|---|---|---|
| `QTBoard0` | `1` | 0 号板卡存在/型号标识 |
| `QTBoard1..7` | `-1` | 其余槽位不存在 |
| `GigabitNet_REG_BASE_ADDRESS` | `1073741824`（`0x40000000`） | 1G 网口寄存器基地址 |
| `10GigabitNet_REG_BASE_ADDRESS` | `0` | 10G 网口寄存器基地址 |
| `CardIndx` | `0` | 卡序号 |

### 10.2 `config/QT2144/FunctionalConfiguration.json`

【A】关键字段：

| 字段 | 值 | 说明 |
|---|---|---|
| `BOARD_TYPE` | `1` | 板型标识 |
| `CardIndx` | `0` | 卡序号 |
| `is_net` | `true` | 走网口 |
| `DRIVER_SET_DELAY_MS` | `3000` | 驱动建立延时 |
| `CLOCK_SET_DELAY_MS` | `3000` / `1500` | 时钟建立延时 |
| `RecGain_Delay_MS` | `3000` | 接收增益建立延时 |
| `Frequency_Delay_MS` | `20000` | 频率相关建立延时 |
| `*_BYTE_ALIGNMENT` | `64` | DMA 字节对齐 |
| `SamplingRate_Double` | `5000` | 双通道采样率数值，单位未标 |
| `SamplingRate_Single` | `10000` | 单通道采样率数值，单位未标 |
| `CoupleType` | `"DC"` | 直流耦合 |
| `is_REF_Clock` | `true` | 使用参考时钟 |
| `is_EXT_Clock` | `false` | 不使用外部时钟 |
| `is_FiniteMulti` | `true` | 支持有限多段 |
| `is_FiniteSingle` | `true` | 支持有限单段 |
| `is_InfiniteMulti` | `true` | 支持无限多段 |
| `is_StreamPlay` | `false` | 不支持流式播放 |
| `Pre_trigger_uint1/2` | `4` | 预触发参数 |
| `Delay_trigger_uint1/2` | `4` | 触发延时参数 |
| `is_ExtTrigger_BiasVoltage` | `true` | 支持外部触发偏置电压 |
| `frame_header_bytes_number` | `64` | **可能存在 64 字节帧头** |
| `DDR_Memory` | `4096` | DDR 容量数值，单位未标 |
| `dmatotalbytes` | `1000` | DMA 总量数值，单位未标 |
| `is_EEProm` | `false` | 不使用 EEPROM |

注意：`SamplingRate_Double/Single` 的数值与 `qt_acq` 接受的 10/50/125/250 MHz 不是同一层概念。前者很可能是 ADC 原始采样率或板卡内部速率，后者是封装暴露的输出率。具体关系必须由厂商确认。

### 10.3 `config/QT2144/AFE.json`

【A】4 个通道 × 6 个量程档：

| 通道 | offset 示例 | gain | attenuation |
|---|---|---|---|
| ch0 | `[2066,2066,2064,2059,2061,2065]` | `[5,11,18,18,15,23]` | `[0,0,0,15,9,9]` |
| ch1 | `[2063,2063,2060,2053,2047,2042]` | 同上 | 同上 |
| ch2 | 各档约 2042–2066 | 同上 | 同上 |
| ch3 | 各档约 2042–2066 | 同上 | 同上 |

这些值是 AFE 配置码，不是物理量。没有公式说明。

### 10.4 `config/QT2144/Range.json`

【A】

- `range_num: 6`；
- 档位：`50 / 100 / 250 / 500 / 1000 / 2500`；
- 每路 `input1..input8` 有 `factor1..factor8`；
- `input1..input4` 的前 4 个 factor 有非默认值；
- `input5/6` 的 factor 多为 `0.0299072`；
- `input7/8` 出现 `7/8`、`0.22/0.99` 等异常值。

这表明它是板卡族共用模板，不是针对当前 4 通道全部量程都完成校准的数据。factor 的单位和公式未文档化。

### 10.5 `config/0/*.json`

【A】

- `AFE.json`、`Range.json`、`FunctionalConfiguration.json` 内容为 `{}`；
- `Interface.json` 为 `{"QTBoard0":{"GigabitNet_REG_BASE_ADDRESS":1}}`，与根 `Interface.json` 的整数格式不一致。

结论：不能作为 QT2144 的有效配置，不建议使用。实际可用配置是 `config/QT2144/`。

### 10.6 `config/version/sdk_version_meta`

【A】内容为 `1.0.0.0`。SDK 会读取它来获取版本哈希与构建时间；缺失时报错。

---

## 11. 错误信息目录

【B】`qt_acq.dll` 中的错误文本（占位符已保留）：

| 错误文本 | 触发条件 | 建议处理 |
|---|---|---|
| `invalid sample rate <X> MHz (valid: 250, 125, 50, 10)` | 采样率不在 4 档 | 改为 10/50/125/250 |
| `invalid acquisition time <X> ms (must be > 0)` | 时长为 0 | 设置 >0 |
| `invalid segment count <X> (must be >= 1)` | 段数为 0 | 设置 >=1 |
| `invalid trigger mode <X> (valid: 0=Software, 1=Ext Rising, 2=Ext Falling)` | 触发值非法 | 改为 0/1/2 |
| `segment of <X> bytes exceeds u32 limit` | 每段字节数溢出 | 降低采样率/时长 |
| `total data of <X> bytes exceeds u32 limit` | 总字节数溢出 | 减少段数/时长 |
| `buffer has ...` / `... bytes are required` | 容量不足 | 用 `calc_buffer_bytes` 重新分配 |
| `QT_Board SDK error in <函数>: code <N>` | 底层 SDK 失败 | 查底层函数、驱动、配置 |
| `Net card I/O error: <...>` | 网口 I/O 失败 | 查网线、IP、驱动、权限 |
| `Net card protocol mismatch: expected command 0x<...>, got 0x<...>` | 网口协议不匹配 | 查固件/协议版本/传输路径 |
| `Net card read timeout: wanted <X> bytes, got <Y>` | 网口读超时/短读 | 查触发、超时、丢包 |
| `internal panic in qt_acq` | Rust 内部 panic | 记录场景，避免跨 FFI 崩溃 |

### 11.1 错误处理模板

```c
static void report_error(const char *where, int rc) {
    char msg[512];
    qt_acq_last_error(msg, sizeof(msg));
    fprintf(stderr, "%s failed: rc=%d, msg=%s\n", where, rc, msg);
}
```

Python 封装会自动把 `last_error` 拼进 `QtAcqError`。

---

## 12. 线程、超时与取消

### 12.1 已知事实

- 【B】原生 `read` 只调用一次底层取数函数；
- 【A】头文件没有超时参数；
- 【C】`read` 是否阻塞到“采集完成”由底层决定；
- 【C】`stop` 能否从其他线程解除阻塞未文档化；
- 【C】同一句柄能否并发调用多个函数未文档化；
- 【C】`last_error` 是否线程局部未文档化。

### 12.2 推荐封装策略

如果要在 OFDR 平台中使用，建议：

1. 每个句柄由一个专用工作线程持有；
2. 上层不直接调用 `read`，而是提交“采集任务”；
3. 工作线程执行 `configure → start → read → stop`；
4. 结果放入有界队列；
5. 上层 `TryTake` 在队列上等待超时；
6. 超时返回 false，不依赖原生 `read` 超时；
7. `Stop` 设置取消标志，调用原生 `stop`，并等待工作线程退出；
8. 若工作线程无法退出，记录为严重故障，不要无限等待；
9. 队列满时显式丢最旧或拒绝新数据，并计数。

### 12.3 不要假设

- 不要假设 `stop` 一定能打断 `read`；
- 不要假设 `close` 一定能打断 `read`；
- 不要假设重复 `stop/close` 安全；
- 不要假设 `read` 返回短数据时会报错；
- 不要假设 `last_error` 跨线程可靠。

---

## 13. 已知限制与未确认项

| 编号 | 限制/未确认项 | 影响 | 建议 |
|---|---|---|---|
| L1 | 只支持 10/50/125/250 MHz | 不能直接匹配 OFDR 所需的任意采样率 | 确认硬件方案 |
| L2 | 固定 4 通道、int16 | 不能适配其他通道数/位宽 | 确认实际板卡 |
| L3 | 一次整块读取，无流式/回调 | 不适合连续实时处理 | 确认是否需要流式 |
| L4 | 无原生超时/取消 | `TryTake` 需自建线程与看门狗 | 适配器层实现 |
| L5 | 短读不报错 | 可能静默使用未初始化尾部 | 清零并校验 |
| L6 | 64 字节帧头/对齐未计入公式 | 可能错位/缓冲不足 | 实测确认 |
| L7 | 多段布局未文档化 | 不能直接按段切片 | 实测确认 |
| L8 | P/S/A 与通道映射未定义 | 不能写死 | 接线/配置确认 |
| L9 | AFE/Range 校准语义未定义 | 不能换算物理量 | 厂商给公式 |
| L10 | 1G IP/端口来源不明 | 网口部署不确定 | 厂商确认 |
| L11 | 10G 依赖老 WinPcap | 兼容/安全风险 | 评估 Npcap |
| L12 | 配置按 CWD 查找 | 部署易失败 | 固定工作目录 |
| L13 | 版本资源为 TODO | 非正式发布质量 | 风险评估 |
| L14 | 无板卡固件版本接口 | 生产自检不足 | 直接调 QT_Board API 或请厂商扩展 |
| L15 | Rust panic 跨 FFI | 可能进程终止 | 隔离进程/实测 |
| L16 | 许可证未明确 | 不能随意分发 | 法务确认 |

---

## 14. 无硬件自检与上机验证

### 14.1 无硬件可做的检查

`qt_acq_calc_buffer_bytes` 和 `qt_acq_version` 不需要打开板卡。可在完整 DLL 目录下执行：

```python
import ctypes, os
os.add_dll_directory(r"<qt2144-rs 目录>")
dll = ctypes.CDLL(r"<qt2144-rs 目录>\qt_acq.dll")

out = ctypes.c_uint32()
rc = dll.qt_acq_calc_buffer_bytes(50, 8, 1, ctypes.byref(out))
print("rc=", rc, "bytes=", out.value)   # 期望 3200000

for rate in (10, 50, 125, 250, 100):
    rc = dll.qt_acq_calc_buffer_bytes(rate, 1, 1, ctypes.byref(out))
    print(rate, "MHz -> rc", rc, "bytes", out.value)

buf = ctypes.create_string_buffer(64)
dll.qt_acq_version(buf, len(buf))
print(buf.value)
```

注意：

- `os.add_dll_directory` 是 Python 3.8+ 的依赖搜索方式；
- 即便 `calc` 不需要硬件，DLL 加载仍需要 `QT_Board.dll` 及其依赖存在；
- 若加载失败，先解决依赖，不要归因于硬件。

### 14.2 上机验证顺序

1. 10 MHz、1 ms、1 段、软件触发，最小数据量；
2. 检查 `out_len` 是否等于 `required_bytes`；
3. 检查数据是否全为合理基线，不出现明显错位；
4. 50 MHz、8 ms、1 段，重复；
5. 外部上升沿触发，检查是否等待触发；
6. 外部下降沿触发，检查极性；
7. 触发延时 0 / 1 / 10 ms，检查相对时序；
8. 2 段、4 段，检查段边界和总长度；
9. 250 MHz、10 ms，检查大缓冲区和耗时；
10. 长时间浸泡，观察内存、句柄、日志和崩溃；
11. 断开/重连网口，观察错误分类；
12. 若发现帧头/对齐，记录实际偏移并回写文档。

### 14.3 判定标准

- `rc == 0`；
- `out_len == required_bytes`；
- `required_bytes % 8 == 0`；
- 数据可按 4 通道解析；
- 已知信号频率/幅度符合预期；
- 多段边界连续；
- 无崩溃、无句柄泄漏、无持续增长的内存。

---

## 15. 附录

### 15.1 `qt_acq.dll` 导出表

```text
qt_acq_calc_buffer_bytes
qt_acq_close
qt_acq_configure
qt_acq_last_error
qt_acq_open
qt_acq_read
qt_acq_start
qt_acq_stop
qt_acq_version
```

### 15.2 `qt_acq.dll` 导入 DLL

```text
QT_Board.dll
api-ms-win-core-synch-l1-2-0.dll
KERNEL32.dll
ntdll.dll
VCRUNTIME140.dll
api-ms-win-crt-runtime-l1-1-0.dll
api-ms-win-crt-heap-l1-1-0.dll
```

### 15.3 `QT_Board.dll` 导入的传输/基础依赖

```text
QT_Board_PCIe.dll
QT_Board_Net.dll
QT_Board_10Net.dll
elog.dll
pthreadVC2.dll
libfftw3-3.dll
```

### 15.4 关键配置字段速查

| 文件 | 字段 | 值/含义 |
|---|---|---|
| `Interface.json` | `QTBoard0` | `1`，0 号卡存在 |
| `Interface.json` | `CardIndx` | `0` |
| `FunctionalConfiguration.json` | `is_net` | `true` |
| `FunctionalConfiguration.json` | `frame_header_bytes_number` | `64` |
| `FunctionalConfiguration.json` | `*_BYTE_ALIGNMENT` | `64` |
| `AFE.json` | `chid 0..3` | 4 个 AFE 通道 |
| `AFE.json` | `gain` | `[5,11,18,18,15,23]` |
| `AFE.json` | `attenuation` | `[0,0,0,15,9,9]` |
| `Range.json` | `range_num` | `6` |
| `Range.json` | `ranges` | `50/100/250/500/1000/2500` |
| `sdk_version_meta` | 版本 | `1.0.0.0` |

### 15.5 本文用到的分析手段

- `dumpbin /exports`：导出函数与序号；
- `dumpbin /imports`：依赖 DLL 与导入函数；
- `dumpbin /headers`：架构、子系统；
- `dumpbin /disasm`：校验逻辑、缓冲区公式、`read` 行为；
- 二进制可打印字符串提取：错误信息、版本、日志格式、配置路径；
- 阅读 `qt_acq.h`、`qt_acq.py`、`readme_ext.md`、`config/*.json`。

### 15.6 与项目的关系

- 本目录位于 `references/`，是厂商资料，不参与构建；
- 本文是分析文档，不是实现承诺；
- 采集板协议未确认前，不得据此实现二期适配器；
- 若最终采用该卡，应把适配器隔离在 `OFDR.Infrastructure/Qt2144/`，只暴露 `IScanSource`，并按项目规则完成接口一致性测试后再上真机。

---

## 16. 快速结论

| 问题 | 答案 |
|---|---|
| 怎么打开？ | `qt_acq_open(0, &handle)` |
| 怎么配置？ | `qt_acq_configure(handle, rate, time, segs, trigger, delay)` |
| 采样率有哪些？ | 10 / 50 / 125 / 250 MHz |
| 怎么算缓冲？ | `rate × time × 8000 × segs` 字节 |
| 怎么读？ | `qt_acq_read(handle, buf, capacity, &out_len)`，一次整块读取 |
| 数据格式？ | int16 小端，每帧 `[ch0,ch1,ch2,ch3]`，8 字节/帧 |
| 有帧头吗？ | 配置写有 64 字节，但公式未计入，需实测 |
| 有超时吗？ | 原生没有，需上层自建 |
| 能流式吗？ | 封装不支持，底层有流式模式但未暴露 |
| 能换算物理量吗？ | 不能，校准公式未提供 |
| 能直接用于 OFDR 吗？ | 不能，TBD-15 未关闭，只能作为参考 |
</content>
</Write>
