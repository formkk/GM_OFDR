# GM-IX 硬件子项目

面向 `references/qt2144-rs`（采集）、`references/ofdr-0.1.1-release`（处理）、`references/detect_1.0`（事件检测）的实机验证工程，服务于 OFDR.Platform 二期的硬件接入评估。

**被测整机正式命名 GM-IX**：联讯完整单路 OFDR 设备，对外只有 1 个千兆 RJ45 采集网口和 1 个接 DUT 的光口；内部使用 QT2144 采集板 + `qt_acq` + `ofdr` + `detect` 三级软件链路。三级链路的数据格式、公式与实测值见 [GM-IX 三级数据处理链路数据报告](docs/GM-IX_三级数据处理链路数据报告.md)。

上位机 MVP 边界：单次扫频、相对测量、事件表；不做多段、外触发、直连协议；绝对 RL 留到第二阶段。详见报告 §8.2。

默认采集采用厂商口径：**50 MHz / 8 ms / 软件触发 / trigger_delay = 1 ms**，对应光源 10 ms 上扫周期的 1–9 ms 窗口。整机只有网口和 DUT 光口，没有外触发输入，**软件触发是唯一可用方式**。

## 结论摘要

设备 `192.168.5.20` 在本机 `192.168.5.55/24` 上可达；千兆网口路径可用，单段采集在 10/50/125/250 MHz 四档均成功，`out_len` 与 `rate × ms × 8000 × segs` 完全一致，4 通道 int16 小端交织格式已由原始字节验证。`ofdr` + `detect` 可对实测数据正常处理。

三项必须注意的实测行为：

1. 多段采集（`segs >= 2`）在软件触发下 `read` 不返回；`qt_acq_stop` 也无法中断，调用 stop 本身会一起挂起。
2. `ofdr` 输出长度是 `输入长度 / 2 + 1`，与 README“等长”不符。
3. `ofdr` 结果的距离轴随采集数据变化（91–99 m），功能映射已固定为 `ch0=P、ch1=S、ch2=A`。

补充：2026-11-03 更换千兆交换机后复测，链路由 100 Mbps 提升到 1 Gbps；四档单段仍全部成功，250 MHz / 50 ms 的 100 MB 采集读到 68.3 MB/s（约 546 Mbps）。多段挂起、stop 阻塞与链路速率无关。详见报告 §10。

## 目录结构

```
GM-IX/
├─ runtime/                 # 测试运行时：SDK DLL（含 ofdr/detect）、config 文本副本、log/
├─ src/Qt2144Probe.cs       # C# P/Invoke 测试探针（.NET Framework 4.x / x64）
├─ src/Gmix.Data/           # Schema v0.2 模型、读写、校准、汇总、校验与测试
├─ bin/Qt2144Probe.exe      # 由 run-tests.ps1 编译
├─ results/                 # JSON 结果、原始 bin、峰值/事件 CSV、控制台日志
├─ run-tests.ps1            # 构建 + 已验证用例的一键执行
├─ docs/测试报告.md          # 测试结果、差异、TBD 回填建议
├─ docs/GM-IX_数据与处理规范_v1.0.md  # Part A 数据协议 + Part B 处理契约
├─ docs/GM-IX_三级数据处理链路数据报告.md
└─ README.md
```

`runtime/` 中的厂商二进制是从 `references/` 复制的测试运行时副本，**不进入 OFDR.Platform 源码树，也不作为发行产物**。项目规则默认禁止复制；本目录的复制行为已由用户明确豁免。

`results/` 中 JSON 与日志里的绝对路径是改名前（`QT2144/`）的历史快照，数据本身未改动；当前目录名为 `GM-IX/`。

## 运行前提

- x64 Windows 与 .NET Framework 4.x（`csc.exe` 可用）；
- `runtime/msvcr100.dll` 必须存在：`pthreadVC2.dll` 依赖 VC++2010 运行库，缺失时 `qt_acq.dll` 加载失败（错误 126/`DllNotFoundException`）；
- 进程工作目录必须是 `runtime/`（`QT_Board.dll` 按 CWD 查找 `config\` 并写 `log\`）；
- 设备与主机同网段，SDK 网络传输层内置目标地址 `192.168.5.20`；
- 主机网口实测协商为 **100 Mbps**，不是千兆；单段有限采集不受影响，但吞吐上限约 10.8 MB/s。

## 运行

```powershell
powershell -ExecutionPolicy Bypass -File .\GM-IX\run-tests.ps1
```

脚本依次执行：编译 → stage0（离线加载/公式/参数校验）→ 10/50/125/250 MHz 单段采集 → 50 MHz 的 `ofdr` + `detect`。

会挂起的用例不要放进一键脚本：

```powershell
# 多段：50 MHz / 8 ms / 4 段，read 与 stop 均会挂起，必须外部限时并强杀进程
Qt2144Probe.exe <runtime> stage1 50 8 4 0 0 <bin> <json>

# 阻塞 read 的 stop 中断测试：stop 自身挂起
Qt2144Probe.exe <runtime> blocktest 50 8 4 0 <json>
```

## 结果解读

- `stage0.json`：DLL 加载、版本、容量公式矩阵、非法参数、空句柄、`ofdr`/`detect` 合成自检；
- `stage1_*.json`：单段采集参数、各调用返回码、耗时、`out_len`、帧对齐、四通道统计；
- `stage2_*.json`：`ofdr` 输出长度/距离轴/频率轴/功率谱，`detect` 峰与段数量；
- `peaks_*.csv`、`events_*.csv`：峰位置/高度/突出度，事件位置/峰高/插损/回损；
- `raw-*.bin`：原始 int16 小端、4 通道交织，帧结构 `[ch0, ch1, ch2, ch3]`。

详细结论见 `docs/测试报告.md`。
