---
name: run-verify
description: 执行本项目的构建与验证（GM-IX 产品主线 + ofdr_platform 预研平台 + L1 契约检查），判定通过标准并排查失败、夹具缺失与干净克隆复现
---

# 构建与验证

产品主线的质量证据是 **GM-IX 构建 + Gmix.Tests**；预研平台是 **OFDR.Verify**；契约是 **check_contract.ps1**。按改动范围执行，不要用预研结果替代产品线验证。

## 使用场景

- 任何代码改动完成前后，需要确认构建与回归结果。
- 新增/修改验证用例、测试夹具后。
- 怀疑 CI 与本机结果不一致时，做干净克隆复现。

## 指令

1. **产品主线 GM-IX**（改动 `GM-IX/src` 时必跑）：
   `dotnet build GM-IX/src/Gmix.sln`
   期望：`0 个警告`、`0 个错误`（`TreatWarningsAsErrors` 已开启）。
   `dotnet run --project GM-IX/src/Gmix.Tests`
   期望：PASS，退出码 0。
2. **预研平台**（仅改动 `ofdr_platform/src` 时）：
   `dotnet build ofdr_platform/src/OFDR.Platform.sln`
   期望：`0 个警告`、`0 个错误`。
   `dotnet run --project ofdr_platform/src/OFDR.Verify`
   期望：`PASS 11+，FAIL 0，SKIP 0`，退出码 0。Release 下可加 `-c Release --no-build`（先 Release 构建）。
3. **L1 契约**（改动 `docs/contracts/统一数据契约_v*.md` 时）：
   `powershell -File LGA50/tools/check_contract.ps1`
   期望：`PASS 7，FAIL 0`，退出码 0。

## 失败排查顺序

1. **编译错误**：先看是否 `global.json` 要求的 8.0.x SDK 缺失（`dotnet --list-sdks`）。
2. **GM-IX 夹具缺失**：检查 `tests/fixtures/gmix/schema_v02_sample/`；缺失应 FAIL，不得改成 SKIP。
3. **[FAIL] 契约/算法用例**：按断言消息中的"期望/实际"定位；先确认最近改动是否触碰 ICD 字段、组结构或 `configHash`。
4. **[FAIL] 历史基准数据**：检查 `tests/fixtures/platform/` 是否存在 `ofdr_metrics_*.csv`。该用例在夹具缺失时**故意失败**（不得回退为 SKIP），这是防止 CI 假通过的约束。
5. **[SKIP]**：基线应为 0 SKIP。出现 SKIP 说明有人把缺失资产降级处理，应改为 FAIL 或补夹具。
6. **TCP 用例失败**：`FakeOfdrServer` 使用回环随机端口（`IPAddress.Loopback, 0`），一般不涉及端口冲突；优先检查握手/切帧逻辑与本机防火墙策略。
7. **超时类失败**：`EngineOfflineLoop` 与 TCP 用例含真实等待，杀毒软件/低性能机器可能超时；先重跑一次，仍失败再查代码。

## 干净克隆复现（模拟 CI）

```powershell
$tmp = Join-Path $env:TEMP "gm_ofdr_ci_check"
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
git clone --quiet . $tmp
dotnet build "$tmp\GM-IX\src\Gmix.sln" -c Release
dotnet run --project "$tmp\GM-IX\src\Gmix.Tests" -c Release --no-build
dotnet build "$tmp\ofdr_platform\src\OFDR.Platform.sln" -c Release
dotnet run --project "$tmp\ofdr_platform\src\OFDR.Verify" -c Release --no-build
powershell -ExecutionPolicy Bypass -File "$tmp\LGA50\tools\check_contract.ps1"
"exit=$LASTEXITCODE"
Remove-Item -Recurse -Force $tmp
```

若本机通过而干净克隆失败，通常是新增文件未提交、夹具未入库，或依赖了 `bin/`、`GM-IX/results/` 下的未跟踪数据。

## 新增夹具规范

- 放在 `tests/fixtures/` 对应子目录（`platform/`、`gmix/`、`lga50/`），文件名保持可被 `ofdr_metrics_*.csv` 之类的既有模式匹配。
- 只放测试必需的最小样本，不放大体积日志；二进制与历史结果数据需在提交说明中注明来源与日期。
- 原始抓包与实机结果不入库，统一登记到 `docs/data-assets.md`。
