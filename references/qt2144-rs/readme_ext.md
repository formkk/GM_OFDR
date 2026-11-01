# QT2144 数据采集卡使用说明

本组件提供 QT2144 高速数据采集卡的调用封装，支持采样率、采样时长、段数、触发方式配置，通过 USB/PCIe/网口接口读取采集数据。包含 Python 封装及 C 接口头文件。

## 功能特性

- 多通道高速数据采集，支持多段采集；
- 支持软件触发、外部触发（上升沿 / 下降沿）及触发延时；
- 提供 Python 与 C 两套调用接口；
- 自动计算所需缓冲区大小，避免缓冲区不足。

## 文件清单

```
qt_acq.dll    采集核心动态链接库
qt_acq.h      C 接口头文件
qt_acq.py     Python 封装
readme_ext.md 本使用说明
```

请将 `qt_acq.dll` 与 `qt_acq.py` 置于同一目录（`qt_acq.py` 会自动加载同目录的 DLL），并将目录中的全部 SDK 动态链接库一并随包分发，请勿拆分存放。

## 快速示例（Python）

以下为可直接运行的完整示例：

```python
import sys
import numpy as np
sys.path.insert(0, r"<本目录>")
from qt_acq import QtAcq, TRIGGER_EXT_RISING

SAMPLE_RATE_MHZ = 50             # 采样率 (MHz)
ACQ_TIME_MS = 8                  # 采样时长 (ms)，50 MHz 下对应每路 40 万点
SEG_COUNT = 1                    # 段数
TRIGGER = TRIGGER_EXT_RISING     # 外部触发上升沿；下降沿用 TRIGGER_EXT_FALLING，软件触发用 TRIGGER_SOFTWARE
TRIGGER_DELAY_MS = 1             # 触发延时 (ms)

dev = QtAcq(card_index=0)
try:
    print(f"qt_acq version: {dev.version()}")
    dev.configure(SAMPLE_RATE_MHZ, ACQ_TIME_MS, SEG_COUNT, TRIGGER, TRIGGER_DELAY_MS)
    n = dev.required_bytes(SAMPLE_RATE_MHZ, ACQ_TIME_MS, SEG_COUNT)
    print(f"required bytes: {n} ({n / 2**20:.2f} MiB)")

    dev.start()
    raw = dev.read(n)
finally:
    dev.stop()
    dev.close()

print(f"acquired {len(raw)} bytes -> {len(raw) // 2} int16 samples")

# 原始数据为 int16 小端，每帧含 4 路通道：
#   sample[i] = [ch0, ch1, ch2, ch3]
# P/S/A 通道编号按实际硬件接线修改。
N_CHAN = 4
P_CH, S_CH, A_CH = 0, 1, 2

n_frames = len(raw) // (2 * N_CHAN)
frames = np.frombuffer(raw, dtype="<i2")[: n_frames * N_CHAN].reshape(n_frames, N_CHAN)

P = frames[:, P_CH].astype(np.float64)
S = frames[:, S_CH].astype(np.float64)
A = frames[:, A_CH].astype(np.float64)
print(f"通道数 {N_CHAN}，每路 {len(P)} 点")
```

## Python 接口

### QtAcq

| 方法 | 说明 |
| --- | --- |
| `QtAcq(card_index=0)` | 打开指定序号采集卡 |
| `configure(sample_rate_mhz, acq_time_ms, seg_count, trigger=TRIGGER_SOFTWARE, trigger_delay_ms=0)` | 配置采集参数 |
| `configure_file(path)` | 从配置文件读取参数（每行一项：采样率 MHz、时长 ms、段数、触发、触发延时 ms） |
| `start()` | 开始采集 |
| `read(capacity=None)` | 读取数据，返回 `bytes`；未给容量时按 `required_bytes` 自动计算 |
| `stop()` | 停止采集 |
| `close()` | 关闭设备 |
| `version()` | 返回 SDK 版本字符串 |
| `required_bytes(sample_rate_mhz, acq_time_ms, seg_count)` | 静态方法，计算所需缓冲区大小（字节） |

支持上下文管理器用法：`with QtAcq() as dev:` 退出时自动关闭。

### 触发方式常量

| 常量 | 值 | 含义 |
| --- | --- | --- |
| `TRIGGER_SOFTWARE` | 0 | 软件触发 |
| `TRIGGER_EXT_RISING` | 1 | 外部触发上升沿 |
| `TRIGGER_EXT_FALLING` | 2 | 外部触发下降沿 |

### 数据格式

采集数据为 16 位有符号整数（int16，小端），多通道按帧交织：

```
sample[i] = [ch0, ch1, ch2, ch3]
```

每帧通道数与通道顺序由硬件定义（示例中为 4 路），各通道信号由 `reshape(n_frames, N_CHAN)` 后按列取出。通道与用途的对应关系（如 P/S/A 通道）按实际硬件接线确定。

## C 用法

声明见 `qt_acq.h`，核心流程：

```c
qt_acq_handle h = NULL;
qt_acq_open(0, &h);

qt_acq_configure(h, 50, 8, 1, QT_ACQ_TRIG_EXT_RISING, 1);
uint32_t cap = 0;
qt_acq_calc_buffer_bytes(50, 8, 1, &cap);

qt_acq_start(h);
char *buf = malloc(cap);
uint32_t got = 0;
qt_acq_read(h, buf, cap, &got);

qt_acq_stop(h);
qt_acq_close(h);
free(buf);
```

## C 接口一览

```c
int qt_acq_open(uint32_t card_index, qt_acq_handle *out_handle);
int qt_acq_close(qt_acq_handle handle);
int qt_acq_configure(qt_acq_handle handle, uint32_t sample_rate_mhz,
                     uint32_t acq_time_ms, uint32_t seg_count, int trigger,
                     uint32_t trigger_delay_ms);
int qt_acq_start(qt_acq_handle handle);
int qt_acq_read(qt_acq_handle handle, void *buffer, uint32_t capacity,
                uint32_t *out_len);
int qt_acq_stop(qt_acq_handle handle);
int qt_acq_calc_buffer_bytes(uint32_t sample_rate_mhz, uint32_t acq_time_ms,
                             uint32_t seg_count, uint32_t *out_bytes);
int qt_acq_last_error(char *buf, size_t cap);
int qt_acq_version(char *buf, size_t len);
```

## 错误码

所有接口返回 `0` 表示成功，非零表示失败：

| 返回值 | 含义 |
| --- | --- |
| 0 | OK |
| -1 | 未知 / I/O 错误 |
| -2 | SDK 错误 |
| -3 | 参数无效 |
| -4 | 句柄无效 |
| -5 | 空指针 |
| -6 | 缓冲区过小 |
| -7 | 尚未配置 |

失败后可通过 `qt_acq_last_error`（Python 中异常信息自动附带）获取详细错误消息。
