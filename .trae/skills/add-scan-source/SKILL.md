---
name: add-scan-source
description: 按项目规范新增或替换采集适配器（产品线 Gmix.Acquirer/Gmix.Import 或预研 IScanSource），并同步补齐契约测试、夹具与文档回填
---

# 新增采集源适配器

用于接入新的采集设备或替换现有采集源。目标不是"能跑通"，而是"符合分层、可离线验证、可追溯"。

## 第一步：确定归口（必读）

| 目标 | 归口 | 契约依据 |
|---|---|---|
| **产品主线**（GM-IX → 新设备） | 在线采集：`products/gmix/src/Gmix.Acquirer/`；离线导入：`products/gmix/src/Gmix.Import/`；厂商调用只经 `Gmix.Vendor` | [`docs/contracts/统一数据契约_v0.7.md`](../../../docs/contracts/统一数据契约_v0.7.md)（L1 帧）+ ICD（L2 记录） |
| **预研平台**（ofdr_platform，冻结） | 实现 `OFDR.Contracts.IScanSource`，代码放 `research/platform/src/OFDR.Infrastructure/<Adapter>/` | `docs/platform/SDD_OFDR_Platform_v0.9.md` §4.3（`StandardScan`） |

**判断规则**：为 products/gmix/LGA50 产品线接入设备走产品主线归口；只有预研验证才动 `ofdr_platform`。两条线的适配器不得互相引用，上层不得感知适配器存在。

## 产品主线路径（GM-IX）

1. **先读文档**：`docs/contracts/统一数据契约_v*.md`（帧构造前提 L-01~L-03、轴归口 L-04、错误通道 E-01~E-04）；`docs/gmix/GM-IX_上位机软件设计说明书_SDD_v0.1.md` §3（进程模型）、§6（厂商边界）。
2. **放置位置**：在线采集落 `Gmix.Acquirer`（独立进程，`_staging` + `.ready` 文件交接）；离线导入落 `Gmix.Import`。厂商类型、`DllImport`、协议字节结构只允许出现在 `Gmix.Vendor/`。
3. **协议常量化**：协议常量集中到适配器目录下的单个协议文件（如 `Qt2144Protocol.cs`）：magic、帧尾/长度、字节序、通道交织、命令字、超时默认值。其它文件不得出现字节偏移魔法数。
4. **帧构造纪律**：适配器只构造有效帧（点数=数组长度、无非有限值、轴参数合法），失败走错误通道分类上报；下游不得重算轴、不得插值、不得就地修改帧；`SourceType` 只允许 `Lianxun | HengLga50`。
5. **生命周期**：采集进程 `open → configure → start → read → stop → close` 成对释放；`seg_count >= 2` 已知会挂起，禁止启用。
6. **补测试**：`products/gmix/src/Gmix.Tests/` 增加用例；夹具放 `tests/fixtures/gmix/` 或 `tests/fixtures/lga50/`，缺失应 FAIL，不得 SKIP。
7. **验证与回填**：`dotnet build products/gmix/src/Gmix.sln` → 0 警告 0 错误；`dotnet run --project products/gmix/src/Gmix.Tests` → PASS。回填 SDD §13 与 ICD §10 实现清单；触及 L1 契约时先升版契约并跑 `devices/lga50/tools/check_contract.ps1`。
8. **接入顺序固定**：mock 服务端 → 契约测试 → 真机冒烟。真机联调的不确定行为（时序、粘包、断连）必须回写 SDD 与 SRS 风险登记。

## 预研平台路径（ofdr_platform，冻结，仅缺陷修复）

1. **先读文档**：`docs/platform/SRS_OFDR_Platform_v0.9.md` §4.1（ACQ 需求）、§5（IF-HW 接口）；`docs/platform/SDD_OFDR_Platform_v0.9.md` §5（接口）、§6（线程与生命周期）、§4.3。
2. **放置位置**：只放 `research/platform/src/OFDR.Infrastructure/<Adapter>/`，实现 `OFDR.Contracts.IScanSource`。禁止在 `Contracts`/`Metrics`/`Engine`/`App` 中出现协议字节、网络类型或厂商句柄。
3. **契约语义**：`Open` 建会话；`StartStream` 开始；`BeginChannel` 递增代次并丢弃旧代次帧；`TryTake(timeout)` 超时返回 false；`Stop` 幂等；`DroppedFrames` 计数。有界队列背压。
4. **补契约测试**：`research/platform/src/OFDR.Verify/Tests.cs` 增加用例并在 `Program.cs` 的 `Run(...)` 登记。至少覆盖：握手成功、失败可读错误、分片/粘包切帧、代次丢弃、超时、Stop 幂等。协议测试复用 mock 服务端模式（参考 `FakeOfdrServer`）。
5. **验证与回填**：`dotnet build research/platform/src/OFDR.Platform.sln` → 0 警告 0 错误；`dotnet run --project research/platform/src/OFDR.Verify` → 全 PASS、0 SKIP。回填 SDD §5 映射表与 §13.4。

## 通用红线

- 夹具入库放 `tests/fixtures/` 对应子目录，不得依赖 `bin/`、`obj/` 或本机路径。
- 原始抓包与实机结果不入库，同步登记 `docs/data-assets.md`（字节数、SHA256、采集参数）。
- 错误必须可分类（连接/协议/数据/超时）并映射为可读消息，禁止 `catch {}` 静默吞协议错误。
- 未确认协议（如 FPGA 板，TBD-15）不得臆测实现；未确认点登记 TBD 并走"明确拒绝"路径。

## 完成标准

- 对应线的构建 0 警告 0 错误、测试全 PASS、0 SKIP。
- 适配器之外没有任何改动涉及协议或网络类型（可用 `grep` 检查 `NetworkStream`/`SerialPort`/`DllImport` 的分布）。
- 对应 SDD/ICD/契约已回填，验证证据写入提交说明。
