# 插损/回损（IL/RL）数据处理库使用说明

本库提供基于 OFDR 功率谱的反射事件检测与逐事件插损（IL）/回损（RL）计算功能。输入距离轴与功率谱，自动识别反射事件并输出各事件的位置、插损与回损。包含 Python 封装及 C 接口头文件。

## 功能特性

- 自动检测功率谱中的反射事件；
- 逐事件输出插损（IL）与回损（RL）；
- 支持线性幅值谱与 dB 谱两种输入；
- 提供 Python 与 C 两套调用接口，参数可调。

## 文件清单

```
detect.dll    处理核心（动态链接库）
detect.h      C 接口头文件
detect.py     Python 封装
README.md     使用说明
```

四个文件平铺于同一目录，`detect.py` 会自动加载同目录的 `detect.dll`，请勿分开存放。

## 快速示例（Python）

以下为可直接运行的完整示例，使用模拟功率谱演示调用流程：

```python
import sys
import numpy as np
sys.path.insert(0, r"<本目录>")
from detect import run_db

DIST  = np.linspace(0, 10, 20001)            # 距离轴 (m)
PS_DB = -60.0 * np.ones_like(DIST)           # 背景噪声底 (dB)
for loc in (2.0, 5.0, 8.0):                  # 模拟三个反射点
    PS_DB += (-30.0 - (-60.0)) * np.exp(-((DIST - loc) / 0.02) ** 2)

out = run_db(DIST, PS_DB)

print(f"{out['n_peaks']} 个峰, {out['n_segments']} 段基线, {out['n_events']} 个事件")
for i, (loc, il, rl) in enumerate(zip(out["event_loc"], out["event_il"], out["event_rl"])):
    il_txt = f"{abs(il):.2f} dB" if il == il else "参考"
    print(f"  事件{i+1}: {loc:.3f} m  插损 {il_txt}  回损 {rl:.1f} dB")
```

## Python 用法

将本目录加入 `sys.path`（或设置 `PYTHONPATH`）后导入：

```python
import numpy as np
from detect import run_db   # 或 run（线性幅值输入）

DIST  = ...  # 距离轴 (m)，float64 numpy 数组
PS_DB = ...  # 功率谱 (dB)，与 DIST 等长

out = run_db(DIST, PS_DB)

print(f"{out['n_peaks']} 个峰, {out['n_segments']} 段基线, {out['n_events']} 个事件")
for i, (loc, il, rl) in enumerate(zip(out["event_loc"], out["event_il"], out["event_rl"])):
    il_txt = f"{abs(il):.2f} dB" if il == il else "参考"
    print(f"  事件{i+1}: {loc:.3f} m  插损 {il_txt}  回损 {rl:.1f} dB")
```

结果字段：

| 字段 | 含义 |
| --- | --- |
| `ps_db` | dB 功率谱（长度 `len`） |
| `baseline_db` | 基线（dB，长度 `len`） |
| `peak_loc/height/prominence/base` | 峰位置(m)/峰高/突出度/峰前基线均值（dB） |
| `seg_start/seg_end` | 基线段起止索引（`end` 不含） |
| `seg_noise_floor` | 段内噪声顶（dB） |
| `event_loc/peak/il/rl` | 事件位置/峰高/插损/回损（`il` 首段为 NaN） |
| `event_base_before/after` | 事件前/后基线段噪声顶（dB，缺失为 NaN） |

如只有线性幅值谱，改用 `run(DIST, PS)`（与 `detect_process` 等价）。

## C 用法

声明见 `detect.h`，核心流程：

```c
DetectConfig cfg = detect_config_default();
cfg.pk_min_prominence_db = 10.0;

DetectResult res = detect_process_db(dist, ps_db, len, &cfg);
if (res.error == 0) {
    /* res.peak_loc / res.seg_noise_floor / res.event_il / res.event_rl ... */
}
detect_result_free(&res);
```

调用后必须通过 `detect_result_free` 释放 `DetectResult`，避免内存泄漏。

## 配置字段

| 字段 | 含义 | 默认值 |
| --- | --- | --- |
| `nf_q` | 基线分位数 (%) | 85 |
| `nf_radius_m` | 基线窗口半径 (m) | 0.1 |
| `pk_baseline_win` | 峰前基线窗口点数 | 100 |
| `pk_min_prominence_db` | 最小峰突出度 (dB) | 10 |
| `pk_min_distance_m` | 相邻峰最小间距 (m) | 0.5 |
| `pk_exclude_radius_m` | 峰排除窗口半径 (m) | 0.1 |
| `pk_seg_percentile` | 段内噪声顶分位数 (%) | 95 |
| `pk_last_seg_tail_m` | 最后一段噪声顶结束距离偏移 (m) | 5 |

## C 接口一览

```c
DetectConfig detect_config_default(void);
DetectResult detect_process(const double *dist, const double *ps,
                            size_t len, const DetectConfig *cfg);
DetectResult detect_process_db(const double *dist, const double *ps_db,
                               size_t len, const DetectConfig *cfg);
void detect_result_free(DetectResult *result);
```
