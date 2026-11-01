# GM_OFDR

OFDR（光频域反射）测试平台软件。当前为**一期 v0.1.0 最短路径实现**：整机 TCP 采集为唯一在线路径，配合离线合成源可在无硬件条件下跑通"采集 → 标准化 → 指标 → 显示/落盘"闭环。

> 采集硬件尚未定型（独立 FPGA 采集板/定制板的形态与协议待定，见 SRS TBD-15），因此仓库不含任何厂商二进制、也不做采集板适配。二期范围与设计见 `docs/SDD_OFDR_Platform_v0.9.md` §14。

[![CI](https://github.com/formkk/GM_OFDR/actions/workflows/ci.yml/badge.svg)](https://github.com/formkk/GM_OFDR/actions/workflows/ci.yml)

## 前置条件

- Windows 10/11 x64
- .NET SDK 8.0（版本由 `global.json` 约束为 8.0.x）
- 无需第三方 DLL、无需串口设备、无需 OFDR 整机即可构建与验证

## 快速开始

```powershell
# 构建（7 个程序集，0 警告 0 错误为基线）
dotnet build ofdr_platform/src/OFDR.Platform.sln

# 离线验证（11 项：契约/算法/协议/编排/迁移/基准数据）
dotnet run --project ofdr_platform/src/OFDR.Verify

# 启动界面（默认"离线合成（无硬件）"，直接点【开始测试】即可跑通闭环）
dotnet run --project ofdr_platform/src/OFDR.App
```

验证退出码：`0` 全部通过，`1` 存在失败项。CI 在每次 push / PR 时于 `windows-latest` 执行上述构建与验证。

## 目录结构

| 路径 | 说明 |
| --- | --- |
| `ofdr_platform/src/OFDR.Platform.sln` | 平台主解决方案（一期唯一需要构建的入口） |
| `ofdr_platform/src/OFDR.Contracts` | 唯一跨层数据契约 `StandardScan`、配置、校验、序列化 |
| `ofdr_platform/src/OFDR.Metrics` | 峰值检测、IL/RL/长度/光程、距离域事件与包络降采样（纯函数） |
| `ofdr_platform/src/OFDR.Engine` | `TestEngine`：IN×OUT 遍历、代次、超时、取消、逐通道聚合 |
| `ofdr_platform/src/OFDR.Infrastructure` | 整机 TCP 源、模拟开关、遗留配置读取、CSV/日志落盘 |
| `ofdr_platform/src/OFDR.Replay` | 离线合成谱源、历史 CSV 读取 |
| `ofdr_platform/src/OFDR.App` | WinForms 界面（组合根：指标表页 + 距离域页） |
| `ofdr_platform/src/OFDR.Verify` | 验证控制台（`PASS/FAIL/SKIP` + 退出码） |
| `tests/fixtures` | 随仓库走的测试夹具（历史 `ofdr_metrics_*.csv` 结果级基准数据） |
| `docs` | SRS（需求）、SDD（设计）、一期评审摘要 |
| `OFDRDataReceiver _demo` | 旧版 WinForms 实现，仅作行为基线（net6.0，独立 `slnx`，**不参与主解决方案构建**） |
| `references` | 厂商资料与手册（参考用，不参与构建；见"第三方资料"） |

## 文档索引

- 需求：[SRS_OFDR_Platform_v0.9.md](docs/SRS_OFDR_Platform_v0.9.md)
- 设计：[SDD_OFDR_Platform_v0.9.md](docs/SDD_OFDR_Platform_v0.9.md)
- 一期评审：[REVIEW_OFDR_Platform_v0.1.0.md](docs/REVIEW_OFDR_Platform_v0.1.0.md)

历史文档中的 `product/ofdr/...`、`ref/keysight` 等路径来自早期工作区，在仓库中的对应关系为：

| 文档旧路径 | 仓库实际路径 |
| --- | --- |
| `product/ofdr/ofdr_platform` | `ofdr_platform/src` |
| `product/ofdr/OFDRDataReceiver _demo/...` | `OFDRDataReceiver _demo/OFDRDataReceiver _demo/...` |
| `product/ofdr/ofdr_platform.png` | `references/ofdr_platform.png` |
| `product/ofdr/{qt2144-rs, ofdr-0.1.1-release, detect_1.0}` | `references/{qt2144-rs, ofdr-0.1.1-release, detect_1.0}` |
| `ref/keysight` | `references/keysight` |

## 一期范围

已实现：整机 TCP 采集、`StandardScan` 标准化、峰值与 IL/RL/长度计算、通道遍历、模拟开关、离线合成/回放、结果 CSV 与日志、距离域只读曲线与双光标。

未实现（二期，勿按缺失缺陷处理）：FPGA 采集板适配、`ofdr.dll` 处理层、`detect.dll` 交叉验证、真实串口开关、原始帧留存与清理、断线重连、校准版本管理、事件类型判定。逐条状态见 SDD §13.4。

## 第三方资料

`references/` 下为厂商手册、SDK 头文件与二进制（`ofdr.dll`、`detect.dll`、QT2144 SDK 等），仅作参考，不参与构建与运行，也不随发行包分发。这些资料的版权归各自厂商所有，**不在本仓库根目录 `LICENSE`（Apache-2.0）的授权范围内**；对外发布或接受外部贡献前需单独确认其分发与使用许可。
