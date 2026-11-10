# OFDR 平台一期评审摘要（v0.1.0）

| 项 | 内容 |
| --- | --- |
| 评审对象 | `product/ofdr/ofdr_platform` 一期 v0.1.0（7 个程序集） |
| 对应文档 | `SRS_OFDR_Platform_v0.9.md`、`SDD_OFDR_Platform_v0.9.md` |
| 评审范围 | SDD §1.1 五项目标达成情况 + 一期实际验证证据 |
| 日期 | 2026-10-30 |

## 1. 总体结论

**建议：有条件通过。** 五项目标中 3 项基本达成、2 项部分达成；一期“测量闭环可演示”的验收证据完整，未达成部分均为二期范围且已在 SRS §4.9 登记。

## 2. 目标核对

| # | 目标 | 结论 | 主要证据 | 差距（二期） |
| --- | --- | --- | --- | --- |
| 1 | 单一数据枢轴 `StandardScan` | 基本达成 | TCP 源与合成源同产 `StandardScan`；指标层/引擎只依赖它；JSON 往返与 SHA256 校验和已测 | 原始扫描未落盘（FR-DATA-01 未实施）；UI 消费派生的 `MeasurementResult` |
| 2 | 硬件无关（厂商 DLL 仅在 Infrastructure） | 达成（一期范围） | 全仓无厂商 DLL/符号；Contracts/Metrics 无 IO；Infrastructure 仅 TCP/开关/配置/持久化 | FPGA 采集板（网络）、ofdr、detect 适配器未实现，规则尚未经真实硬件/厂商调用检验 |
| 3 | 可离线运行（CI 与算法开发不依赖硬件） | 基本达成 | 合成源与 TCP 源同接口；模拟开关；App 默认离线；`OFDR.Verify` 9 项用例含离线 2×2 闭环 | 仓库无 CI 流水线，用例目前依赖本机执行 |
| 4 | 可替换算法并与 `detect.dll` 交叉验证 | 部分达成 | `PeakDetector`/`MetricCalculator` 为静态纯函数；手算基准与合并边界用例锁定行为 | 无算法接口/策略注入，换实现需改调用点；无备选算法；交叉验证未做 |
| 5 | 可追溯（配置快照 + 校准 ID + 复算） | 部分达成 | `ConfigId`（ComputeId）与 SRI/SRO/SRB/SRC 快照随结果与 CSV 落盘；`SchemaVersion` + 校验和 | `CalibrationId` 字段存在但未填充；无校准版本管理；无 raw 全谱，无法从落盘数据复算 |

## 3. 已验证证据

| 项 | 结果 |
| --- | --- |
| 构建 `dotnet build OFDR.Platform.sln` | 成功，0 警告，0 错误（约 6.54 s） |
| 验证 `dotnet run --project OFDR.Verify` | PASS 9，FAIL 0，SKIP 0，退出码 0 |
| App 离线演示（IN 1–2、OUT 1–2、采样 5） | 4 通道、20 帧、无效 0；指标表与 CSV/日志均落盘；CSV 前 18 列与 demo 一致 |

## 4. 未验证项与风险

- `OFDR.App` 未做长时间稳定性与异常路径测试（未接真机、未断网）。
- 真实整机 TCP、真实串口开关未联调（仅有假 TCP 服务端与模拟开关）。
- 无逐值回放回归：历史 run 缺 80,619 点 raw 全谱（SRS TBD-13）。
- 默认参数口径未收敛：半窗默认 250 行（`MetricsConfig` 字段默认 / `config.txt`），而界面按 80 cm 换算为 2000 行（对齐历史实机 run）。属 CON-06 所述“两个定义源 + GUI 运行期覆盖”的遗留。

## 5. 评审决议建议

1. **通过**一期“测量闭环可演示”验收：构建与验证证据齐全，不可演示项已明确声明。
2. **限期收敛**默认参数口径（半窗、A/D、门限）并同步 SRS §6.4 / SDD §8.1。
3. 二期优先三项：`StandardScan` 原始落盘、峰值算法策略接口、结果补 `CalibrationId` 与校准集合标识。
