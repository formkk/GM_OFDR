# ADR-001：Detection.Vendor 引入 `System.IO.Ports`

> **状态**：已接受（2026-11-11）
> **适用**：`products/detection/src/Detection.Vendor`
> **依据**：`.trae/rules/00-scope.md`“不引入 NuGet 或其它第三方依赖。确需引入时，先修改对应 SDD 约束并新增 ADR，再动代码。”
> **对应 SDD**：[SDD_检测平台_v0.1.md](SDD_检测平台_v0.1.md) §3.2、§8.4

## 背景

检测平台的 OCS 光开关（1×8 / 8×8）通过 **RS232** 控制，协议为 ASCII 字符帧 `<...>`，已实机核对（见 [OCS 协议实测报告](OCS_协议实测报告_2026-11-11.md)）。

.NET 8 中 `System.IO.Ports.SerialPort` **不在 BCL** 内，需通过 NuGet 包 `System.IO.Ports` 获得。这是本仓库首次为产品代码引入 NuGet 依赖：

- 现有产品线（GM-IX / LGA50）只走千兆 TCP 与文件边界，从未使用串口；
- `devices/ocs/tools/ocs_measure.ps1` 使用 PowerShell 5 内置的 `System.IO.Ports`，属测试工具，不影响产品依赖。

## 决策

在 `Detection.Vendor` 中引用 **`System.IO.Ports` 8.0.0**（微软官方包，与 .NET 8 目标框架同版本线）。

## 理由

1. **无纯 BCL 替代**：BCL 不提供串口访问；`FileStream` 打开 `\\.\COM3` 无法配置波特率/校验/停止位/流控，无法满足 9600/115200 8N1 与 `Handshake=None` 的实测强制要求。
2. **P/Invoke 自研代价高**：需自行封装 `CreateFile`/`SetCommState`/`SetCommTimeouts`/`ReadFile`/`WriteFile`/`PurgeComm` 与重叠 IO，出错面大、无测试保障，收益仅为“零依赖”这一形式目标。
3. **官方维护**：该包由 .NET 团队维护，随运行时版本发布，供应链风险低于第三方串口库。
4. **隔离可控**：仅在 Vendor 层引用，Core 与 App 不感知（符合规则 10 的厂商归口约束）。

## 备选方案与否决理由

| 方案 | 否决理由 |
|---|---|
| P/Invoke Win32 串口 API | 实现与维护成本远高于收益；错误处理与超时语义需自行重造，易引入现场故障（半帧挂起、流控误配） |
| 第三方串口库（如 SerialPortStream） | 供应链与许可风险更高，且无功能增益 |
| 改用 USB-HID / 厂商 SDK | 设备仅有 RS232 接口，无可用的厂商托管 SDK |
| 依赖 `devices/ocs/tools` 的 PowerShell 脚本 | 测试工具，不是产品运行时；不能进入发布物 |

## 约束

1. 只有 `Detection.Vendor` 的 `OcsSerialTransport` 直接使用 `System.IO.Ports`；其他类型只依赖 `IOcsTransport` 抽象。
2. `Detection.Core`、`Detection.App`、`Detection.Orchestration` **不得**出现 `System.IO.Ports` 的类型或命名空间（构建后以依赖检查验证，对应 SRS NFR-006）。
3. 串口参数固定：8N1、`Handshake = None`（实测：开流控即写超时）；波特率按设备区分（1×8 = 9600，8×8 = 115200）。
4. 升级包版本需重跑 OCS 契约测试（`Detection.Tests`）与实机冒烟，并在本 ADR 追加记录。

## 影响

- `Detection.sln` 需联网还原一次该包；离线环境需预置 NuGet 缓存或私有源。
- CI 增加 `products/detection` 构建矩阵项时，`actions/setup-dotnet` 默认源可满足还原。
- 包体积：运行时约 100 KB 级，随应用发布，不进入源码目录。

## 追溯

| 项 | 内容 |
|---|---|
| 需求 | SRS FR-002/003/020~028（OCS 串口控制） |
| 设计 | SDD §3.2（Vendor 归口）、§8.4（依赖声明） |
| 实测证据 | [OCS 协议实测报告](OCS_协议实测报告_2026-11-11.md) §7（串口参数容错） |
| 复现命令 | `dotnet restore products/detection/Detection.sln` |
