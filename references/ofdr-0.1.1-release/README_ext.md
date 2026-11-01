# OFDR 数据处理库使用说明

本库提供 OFDR（光频域反射）联合功率谱处理功能，输入三路采集信号，输出距离轴、频率轴及联合功率谱。包含 Python 封装及 C 接口头文件。

## 功能特性

- 三路信号输入（P 路、S 路、辅助通道），返回等长处理结果；
- 输出联合功率谱（线性幅值）及对应的距离轴、频率轴；
- 提供 Python 与 C 两套调用接口。

## 文件清单

```
ofdr.dll   处理核心（动态链接库）
ofdr.h     C 接口头文件
ofdr.py    Python 封装
README.md  使用说明
```

四个文件平铺于同一目录，`ofdr.py` 会自动加载同目录的 `ofdr.dll`，请勿分开存放。

## 快速示例（Python）

以下为可直接运行的完整示例，使用模拟输入信号演示调用流程：

```python
import sys, ctypes
import numpy as np
sys.path.insert(0, r"<本目录>")
from ofdr import ofdr

N = 8192                                     # 采样点数
t = np.arange(N, dtype=np.float64)
P = np.sin(2 * np.pi * 0.03 * t)             # P 路信号
S = np.sin(2 * np.pi * 0.03 * t + 0.5)       # S 路信号
A = np.sin(2 * np.pi * 0.02 * t)             # 辅助通道信号

cfg = ofdr.ofdr_config_default()
cfg.fs = 50e6                                # 采样率 (Hz)
cfg.dist_scale = 1.0                         # 距离轴校准系数

ptr = lambda a: a.ctypes.data_as(ctypes.POINTER(ctypes.c_double))
res = ofdr.ofdr_process(ptr(P), ptr(S), ptr(A), len(A), ctypes.byref(cfg))
assert res.error == 0, f"OFDR 处理失败: error={res.error}"

dist = np.ctypeslib.as_array(res.dist, shape=(res.len,)).copy()
freq = np.ctypeslib.as_array(res.freq, shape=(res.len,)).copy()
ps   = np.ctypeslib.as_array(res.ps,   shape=(res.len,)).copy()

ofdr.ofdr_result_free(ctypes.byref(res))

print(f"距离轴: {dist[0]:.3f} ~ {dist[-1]:.3f} m，共 {len(dist)} 点")
print(f"功率谱最大值: {ps.max():.3f}")
```

## Python 用法

将本目录加入 `sys.path`（或设置 `PYTHONPATH`）后导入：

```python
import ctypes
import numpy as np
from ofdr import ofdr  # ofdr 为已加载的 ctypes.CDLL

SAMPLE_RATE_MHZ = 50

cfg = ofdr.ofdr_config_default()
cfg.fs = SAMPLE_RATE_MHZ * 1e6
cfg.dist_scale = 1.0

ptr = lambda a: a.ctypes.data_as(ctypes.POINTER(ctypes.c_double))
result = ofdr.ofdr_process(ptr(P_Clip), ptr(S_Clip), ptr(A_Clip), len(A_Clip), ctypes.byref(cfg))

assert result.error == 0, f"OFDR 处理失败: error={result.error}"

dist = np.ctypeslib.as_array(result.dist, shape=(result.len,)).copy()
freq = np.ctypeslib.as_array(result.freq, shape=(result.len,)).copy()
ps = np.ctypeslib.as_array(result.ps, shape=(result.len,)).copy()

ofdr.ofdr_result_free(ctypes.byref(result))
```

其中 `P_Clip`、`S_Clip`、`A_Clip` 分别为 P 路、S 路、辅助通道信号（`float64` numpy 数组，三路等长）。

### 输入参数

| 参数 | 含义 |
| --- | --- |
| `cfg.fs` | 采样率 (Hz) |
| `cfg.dist_scale` | 距离轴校准系数（默认 1.0），用于补偿距离轴标定误差 |

> 校准方法：对已知距离的反射点测距，实际距离与输出距离之比即为校准系数。
> 例如已知反射点位于 30 m 处而输出为 29.7 m，则 `dist_scale = 30 / 29.7 ≈ 1.0101`。

### 返回结果

| 字段 | 含义 |
| --- | --- |
| `error` | 错误码，0 为成功 |
| `error_msg` | 失败时的错误消息 |
| `dist` | 距离轴 (m) |
| `freq` | 频率轴 (Hz) |
| `ps` | 联合功率谱（线性幅值） |
| `len` | 各数组长度 |

处理完成后必须调用 `ofdr_result_free` 释放结果，避免内存泄漏。

## C 用法

声明见 `ofdr.h`，核心流程：

```c
OfdrConfig cfg = ofdr_config_default();
cfg.fs = 50e6;
cfg.dist_scale = 1.0;

OfdrResult res = ofdr_process(p, s, aux, len, &cfg);
if (res.error == 0) {
    /* 使用 res.dist / res.ps ... */
}
ofdr_result_free(&res);
```

调用后必须通过 `ofdr_result_free` 释放 `OfdrResult`，避免内存泄漏。

## C 接口一览

```c
OfdrConfig ofdr_config_default(void);
OfdrResult ofdr_process(const double *p, const double *s,
                        const double *aux, size_t len,
                        const OfdrConfig *cfg);
void ofdr_result_free(OfdrResult *result);
```
