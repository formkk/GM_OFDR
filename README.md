# GM_OFDR

OFDR（光频域反射）测试平台软件仓库。仓库包含一条演进线上的四类产物，**产品主线是 GM-IX 上位机**：

| 线 | 目录 | 定位 | 状态 |
|---|---|---|---|
| 产品主线 | `GM-IX/` | 联讯 GM-IX 整机上位机（WinForms + 采集进程 + 组存储） | 活跃开发 |
| 第二设备线 | `LGA50/` | 昊衡 LGA50-D 接入：协议分析、抓包、L1 统一契约 | 契约评审中 |
| 预研平台 | `ofdr_platform/` | 未接入硬件的独立预研平台（7 程序集） | v0.1.0 已冻结 |
| 研究分支 | `CNN/` | 距离域曲线指纹识别评估 | 独立评估，不改产品线 |

> **定位口径（2026-11-10 确认）**：`ofdr_platform` 是未接入硬件的预研平台，**不是实现基线**；正式继承链只有 **GM-IX → LGA50**。预研代码与产品线互不继承，字段不必互相同步。

[![CI](https://github.com/formkk/GM_OFDR/actions/workflows/ci.yml/badge.svg)](https://github.com/formkk/GM_OFDR/actions/workflows/ci.yml)
（CI 目前只覆盖 `ofdr_platform` 的构建与离线验证）

## 文档入口

**所有文档的权威性、状态与入口见 [docs/README.md](docs/README.md)。** 常用直达：

- L1 帧契约：[docs/contracts/统一数据契约_v0.7.md](docs/contracts/统一数据契约_v0.7.md)
- L2 记录/处理契约：[docs/contracts/GM-IX_数据与处理规范_v1.0.md](docs/contracts/GM-IX_数据与处理规范_v1.0.md)
- GM-IX 需求/设计：[SRS](docs/gmix/GM-IX_上位机软件需求规格_SRS_v0.1.md) / [SDD](docs/gmix/GM-IX_上位机软件设计说明书_SDD_v0.1.md)
- 预研平台：[SRS](docs/platform/SRS_OFDR_Platform_v0.9.md) / [SDD](docs/platform/SDD_OFDR_Platform_v0.9.md) / [一期评审](docs/platform/REVIEW_OFDR_Platform_v0.1.0.md)
- CNN 评估：[评估报告](docs/cnn/评估报告.md) / [落地评估](docs/cnn/落地评估.md)
- 培训教材：[OFDR 检测全流程培训](Study/OFDR检测全流程培训_上位机开发者版.md)

## 快速开始

### 产品主线：GM-IX

前置：Windows 10/11 x64、.NET SDK 8.0。无硬件时可构建并跑 `Gmix.Tests`；实机用例需要设备与 `GM-IX/runtime/`。

```powershell
# 构建（7 个工程，含 Gmix.App / Gmix.Acquirer / Gmix.Commit / Gmix.Import）
dotnet build GM-IX/src/Gmix.sln

# 离线回归（控制台断言 + 退出码）
dotnet run --project GM-IX/src/Gmix.Tests

# 实机一键用例（需要 GM-IX 设备与 runtime）
powershell -ExecutionPolicy Bypass -File .\GM-IX\run-tests.ps1
```

详见 [GM-IX/README.md](GM-IX/README.md)。

### 预研平台：ofdr_platform

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
|---|---|
| `GM-IX/` | **产品主线**：`src/Gmix.*`（net8）、`src/Gmix.Data`（.NET Framework 遗留）、`tests/fixtures`、`investigations`、`runtime`、`results`；文档见 `docs/gmix/` 与 `docs/contracts/` |
| `LGA50/` | 第二设备线：`capture`（抓包，不入库）、`tools`（契约检查与量纲实验）；文档见 `docs/lga50/` 与 `docs/contracts/` |
| `ofdr_platform/src/` | 预研平台：`OFDR.Contracts` / `Metrics` / `Engine` / `Infrastructure` / `Replay` / `App` / `Verify` |
| `CNN/` | 指纹评估：`src`、`data`/`runs`（不入库）；文档见 `docs/cnn/` |
| `Study/` | **学习、研究及相关知识库**：培训教材、原理与专题研究；不并入 `docs/` 治理文档根 |
| `docs/` | 唯一治理文档根：`contracts`（L1/L2 契约）、`platform`、`gmix`、`lga50`、`cnn`、`evidence`、`assets`、`archive`；入口见 `docs/README.md` |
| `tests/fixtures/` | 统一夹具根：`platform/`（历史结果级基准）、`gmix/`（schema 样例）、`lga50/`（抓包头） |
| `OFDRDataReceiver _demo` | 旧版 WinForms 实现，仅作行为基线（net6.0，独立 `slnx`，**不参与主解决方案构建**） |
| `references/` | 厂商资料与手册（参考用，不参与构建；见"第三方资料"） |
| `releases/` | 本地发布包归档（`*.zip` 不入库） |

## 预研平台范围（v0.1.0）

已实现：整机 TCP 采集、`StandardScan` 标准化、峰值与 IL/RL/长度计算、通道遍历、模拟开关、离线合成/回放、结果 CSV 与日志、距离域只读曲线与双光标。

未实现（**预研范围之外，勿按缺陷处理**）：FPGA 采集板适配、`ofdr.dll` 处理层、`detect.dll` 交叉验证、真实串口开关、原始帧留存与清理、断线重连、校准版本管理、事件类型判定。逐条状态见 SDD §13.4。

## 历史路径对照

历史文档中的 `product/ofdr/...`、`ref/keysight` 等路径来自早期工作区，在仓库中的对应关系为：

| 文档旧路径 | 仓库实际路径 |
| --- | --- |
| `product/ofdr/ofdr_platform` | `ofdr_platform/src` |
| `product/ofdr/OFDRDataReceiver _demo/...` | `OFDRDataReceiver _demo/OFDRDataReceiver _demo/...` |
| `product/ofdr/ofdr_platform.png` | `docs/assets/ofdr_platform.png` |
| `product/ofdr/{qt2144-rs, ofdr-0.1.1-release, detect_1.0}` | `references/{qt2144-rs, ofdr-0.1.1-release, detect_1.0}` |
| `ref/keysight` | `references/keysight` |

## 第三方资料

`references/` 下为厂商手册、SDK 头文件与二进制（`ofdr.dll`、`detect.dll`、QT2144 SDK 等），仅作参考，不参与构建与运行，也不随发行包分发。这些资料的版权归各自厂商所有，**不在本仓库根目录 `LICENSE`（Apache-2.0）的授权范围内**；对外发布或接受外部贡献前需单独确认其分发与使用许可。
