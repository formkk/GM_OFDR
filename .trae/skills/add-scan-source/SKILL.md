---
name: add-scan-source
description: 按项目规范新增或替换采集适配器（IScanSource），并同步补齐契约测试、夹具与文档回填
---

# 新增采集源适配器

用于接入新的采集设备（如 FPGA 采集板）或替换现有整机 TCP 源的场景。目标不是"能跑通"，而是"符合分层、可离线验证、可追溯"。

## 使用场景

- 二期接入 FPGA 采集板（网络），形态与协议待定（SRS TBD-15）。
- 新增其它采集路径，或把现有 `OfdrTcpSource` 迁移到新协议。

## 指令

1. **先读文档，再动代码**：`docs/SRS_OFDR_Platform_v0.9.md` §4.1（ACQ 需求）、§5（IF-HW 接口）、§6（数据要求）；`docs/SDD_OFDR_Platform_v0.9.md` §5（接口）、§6（线程与生命周期）、§4.3（`StandardScan` 契约）。
2. **放置位置**：适配器代码只放 `ofdr_platform/src/OFDR.Infrastructure/<Adapter>/`，实现 `OFDR.Contracts.IScanSource`。禁止在 `Contracts`/`Metrics`/`Engine`/`App` 中出现协议字节、网络类型或厂商句柄。
3. **协议常量化**：新建独立协议文件（如 `FpgaBoardProtocol.cs`），集中 magic/帧尾/长度/字节序/通道交织/命令字/默认超时；其它文件只引用常量，不得出现字节偏移魔法数。
4. **实现契约语义**：`Open` 建会话；`StartStream` 开始；`BeginChannel` 递增代次并丢弃旧代次帧；`TryTake(timeout)` 超时返回 false；`Stop` 幂等；`DroppedFrames` 计数。接收线程与消费方用有界队列解耦。
5. **补契约测试**：在 `ofdr_platform/src/OFDR.Verify/Tests.cs` 增加用例，并在 `OFDR.Verify/Program.cs` 的 `Run(...)` 清单登记。至少覆盖：握手成功、失败可读错误、分片/粘包切帧、代次丢弃、超时返回 false、Stop 幂等。协议测试优先复用 mock 服务端模式（参考 `FakeOfdrServer`：`TcpListener` 绑定 `IPAddress.Loopback, 0` 取随机端口）。
6. **夹具入库**：测试依赖的数据放 `tests/fixtures/`，不得依赖 `bin/`、`obj/` 或本机路径；缺失夹具应 FAIL，不得改成 SKIP。
7. **验证与回填**：运行 `.trae/skills/run-verify` 中的两条命令，全 PASS 后回填 `docs/SDD_OFDR_Platform_v0.9.md` §5 的实现映射表与 §13.4 覆盖对照，必要时更新 SRS 实施状态。

## 完成标准

- `dotnet build` 0 警告 0 错误；`OFDR.Verify` 全 PASS、0 SKIP。
- 适配器之外没有任何改动涉及协议或网络类型（可用 `grep` 检查 `NetworkStream`/`SerialPort`/`DllImport` 的分布）。
- SDD §5 映射表与 §13.4 已回填，且验证证据写入提交说明。
