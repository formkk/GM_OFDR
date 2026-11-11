# 采集信号漂移验证（investigation）

本目录用于定位 GM-IX 实机采集的**信号漂移幅度**问题，属一次性调查，不参与主解决方案构建。

## 文件

| 文件 | 用途 |
|---|---|
| `probe.ps1` | 采集 + `ofdr` 处理探针，按参数输出每帧指标到 CSV |
| `baseline_recgain_true.csv` | 基线组（`is_RecGain=true`）20 帧原始数据 |
| `recgain_false.csv` | 关闭自动增益组（`is_RecGain=false`）20 帧原始数据 |
| `warmed_60min.csv` | 设备开机 1 小时后复测 20 帧原始数据 |
| `report.md` | 结果、结论与后续方向 |

## 前置条件

1. `.NET` 探针目录 `.tmp/probe_run` 已备齐：`qt_acq.dll` 及其依赖族、`ofdr.dll`、`config/`、`MSVCR100.dll`。
2. 设备在线（`192.168.5.20` 可达），厂商软件未占用采集设备。
3. Windows PowerShell 5.1（脚本为 ASCII，无需额外编码设置）。

## 用法

```powershell
# 基线
powershell -ExecutionPolicy Bypass -File probe.ps1 -RecGain true -Frames 20 -OutCsv baseline.csv

# 关闭 AFE 自动增益
powershell -ExecutionPolicy Bypass -File probe.ps1 -RecGain false -Frames 20 -OutCsv recgain_false.csv

# 指定触发延时（诊断用）
powershell -ExecutionPolicy Bypass -File probe.ps1 -RecGain true -Frames 20 -OutCsv d4.csv -DelayMs 4
```

## CSV 列

| 列 | 含义 |
|---|---|
| `frame` | 帧序号（1..N） |
| `distLast` | `dist` 轴末值（m），反映距离轴缩放 |
| `psMax` | 功率谱最大值（线性幅值） |
| `distAtMax` | `ps` 最大值所在距离（m） |
| `ch0Mean` / `ch0Std` / `ch0Min` / `ch0Max` | P 通道原始样本统计（int16） |

## 安全边界

- 只修改探针目录 `.tmp/probe_run/config/QT2144/FunctionalConfiguration.json` 的副本，脚本结束自动还原；
- **不修改 `references/`**，不写入主解决方案源码；
- 每组采集走完整的 `start → read → stop` 循环（已验证的唯一可靠模式）。
