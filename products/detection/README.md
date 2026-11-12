# 检测平台（OCS + OFDR）

检测系统上位机，是 GMIX 产品线的后续版本（继承链 GM-IX → LGA50 → 检测平台）。

## 当前状态

**阶段 A 完成**：`Detection.Vendor`（OCS 串口协议层）+ 契约测试。
**阶段 B 完成**：`Detection.Orchestration`（批次状态机、路径流水线、组提交、恢复）+ 编排测试。

| 工程 | 状态 | 说明 |
|---|---|---|
| `src/Detection.Core` | 已建 | 纯域：错误分类、OCS 状态、路径计划、批次/路径模型、汇总口径、关注提示、命名、配置哈希 |
| `src/Detection.Vendor` | 已建 | OCS 串口协议、错误映射、唯一性本地校验；唯一 `System.IO.Ports` 使用点 |
| `src/Detection.Orchestration` | 已建 | `BatchRunner` 状态机、`PathExecutor` 流水线、`GroupPipeline` 提交、`MetricsReader`、`DetectionStore`、`RecoveryService` |
| `src/Detection.Tests` | 已建 | Vendor 契约回放 + 编排全批次/失败/中止/恢复/N>1 场景 |
| `src/Detection.App` | 待建 | WinForms 矩阵工作台（阶段 D） |

## 入口

| 内容 | 位置 |
|---|---|
| 需求（SRS v0.4 定稿） | [`docs/detection/SRS_检测平台_v0.4.md`](../../docs/detection/SRS_检测平台_v0.4.md) |
| 设计（SDD v0.1） | [`docs/detection/SDD_检测平台_v0.1.md`](../../docs/detection/SDD_检测平台_v0.1.md) |
| 串口依赖决策 | [`docs/detection/ADR-001_System.IO.Ports.md`](../../docs/detection/ADR-001_System.IO.Ports.md) |
| OCS 协议分析 / 实测报告 | [`OCS_控制协议分析.md`](../../docs/detection/OCS_控制协议分析.md) / [`OCS_协议实测报告_2026-11-11.md`](../../docs/detection/OCS_协议实测报告_2026-11-11.md) |
| 厂商协议与照片 | [`references/ocs/`](../../references/ocs/)、[`docs/assets/ocs/`](../../docs/assets/ocs) |
| 夹具 | [`tests/fixtures/ocs/protocol_frames.txt`](../../tests/fixtures/ocs/protocol_frames.txt) |
| 实测工具 | [`devices/ocs/tools/ocs_measure.ps1`](../../devices/ocs/tools/ocs_measure.ps1) |

## 构建与测试

```powershell
dotnet build products/detection/Detection.sln
dotnet run --project products/detection/src/Detection.Tests --no-build
```

CI 已在 `.github/workflows/ci.yml` 中增加对应两项。

## 设计约束（不可绕过）

- `Detection.App` 不引用 `Detection.Vendor`；厂商类型只出现在 Vendor（SDD §1.2）。
- 首版不引用 `Gmix.Vendor`（其内容为 QT2144/GM-IX 专用）；只复用 `Gmix.Core` 的组存储与指标。
- 串口参数按设备固定：1×8 = 9600 8N1，8×8 = 115200 8N1，`Handshake = None`（实测）。
- 每次发送完整帧；8×8 单路命令必须在本地校验输出唯一性（设备不校验）。
- 超时/重试来自配置（默认值取自实测）；光路稳定时间 `settleMs` 未实测前强制填写。

## 待办

- 阶段 C：`IGroupProducer` 的真实实现（LGA50 扫描触发 + 峰值检测，TBD-D10）与基线判定
- 阶段 D：`Detection.App`
- `Gmix.OcsVerify` / `Gmix.OcsCheck` / `Gmix.OcsCheck.App` 三个占位工程在阶段 D 落地后按 SDD §1.3 迁移并删除
