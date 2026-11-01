---
name: run-verify
description: 执行本项目的构建与离线验证（OFDR.Verify），判定通过标准并排查失败、夹具缺失与干净克隆复现
---

# 构建与离线验证

一期唯一的自动化质量证据就是离线验证；本技能给出固定命令、期望输出与排查顺序。

## 使用场景

- 任何代码改动完成前后，需要确认"构建 0 警告 + 验证全 PASS"。
- 新增/修改验证用例、测试夹具后。
- 怀疑 CI 与本机结果不一致时，做干净克隆复现。

## 指令

1. 构建（工作目录 `ofdr_platform/src`，或使用绝对路径）：
   `dotnet build ofdr_platform/src/OFDR.Platform.sln`
   期望：`0 个警告`、`0 个错误`。警告即错误（`TreatWarningsAsErrors` 已开启），出现警告必须修掉，不得用 NoWarn 屏蔽。
2. 验证：
   `dotnet run --project ofdr_platform/src/OFDR.Verify`
   期望：`PASS 11+，FAIL 0，SKIP 0`，退出码 0。Release 下可加 `-c Release --no-build`（先 Release 构建）。

## 失败排查顺序

1. **编译错误**：先看是否 `global.json` 要求的 8.0.x SDK 缺失（`dotnet --list-sdks`）。
2. **[FAIL] 契约/算法用例**：按断言消息中的"期望/实际"定位；先确认最近改动是否触碰峰值口径、`StandardScan` 校验或 `MetricsConfig.ComputeId()`。
3. **[FAIL] 历史基准数据**：检查 `tests/fixtures/` 是否存在 `ofdr_metrics_*.csv`。该用例在夹具缺失时**故意失败**（不得回退为 SKIP），这是防止 CI 假通过的约束。
4. **[SKIP]**：一期基线应为 0 SKIP。出现 SKIP 说明有人把缺失资产降级处理，应改为 FAIL 或补夹具。
5. **TCP 用例失败**：`FakeOfdrServer` 使用回环随机端口（`IPAddress.Loopback, 0`），一般不涉及端口冲突；优先检查握手/切帧逻辑与本机防火墙策略。
6. **超时类失败**：`EngineOfflineLoop` 与 TCP 用例含真实等待，杀毒软件/低性能机器可能超时；先重跑一次，仍失败再查代码。

## 干净克隆复现（模拟 CI）

```powershell
$tmp = Join-Path $env:TEMP "gm_ofdr_ci_check"
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
git clone --quiet . $tmp
dotnet build "$tmp\ofdr_platform\src\OFDR.Platform.sln" -c Release
dotnet run --project "$tmp\ofdr_platform\src\OFDR.Verify" -c Release --no-build
"verifyExit=$LASTEXITCODE"
Remove-Item -Recurse -Force $tmp
```

期望与 CI 一致：构建 0 警告，验证 `PASS 11，FAIL 0，SKIP 0`，`verifyExit=0`。若本机通过而干净克隆失败，通常是新增文件未提交或依赖了 `bin/` 下的未跟踪数据。

## 新增夹具规范

- 放在 `tests/fixtures/`，文件名保持可被 `ofdr_metrics_*.csv` 之类的既有模式匹配。
- 只放测试必需的最小样本，不放大体积日志；二进制与历史结果数据需在提交说明中注明来源与日期。
