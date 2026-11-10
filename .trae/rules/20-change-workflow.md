# 改动工作流

## 验证命令（按改动范围执行）

- 改产品主线（GM-IX）：
  - `dotnet build GM-IX/src/Gmix.sln` → 0 警告 0 错误；
  - `dotnet run --project GM-IX/src/Gmix.Tests` → PASS，退出码 0。
- 改预研平台（ofdr_platform）：
  - `dotnet build ofdr_platform/src/OFDR.Platform.sln` → 0 警告 0 错误；
  - `dotnet run --project ofdr_platform/src/OFDR.Verify` → `PASS 11+，FAIL 0，SKIP 0`，退出码 0。
- 改 L1 契约：
  - `powershell -File LGA50/tools/check_contract.ps1` → `PASS 7，FAIL 0`，退出码 0。
- 提交说明中写明实际执行的命令与结果（可粘贴原文），不得只写"已测试"；未执行的命令要说明原因。

## 文档与口径

- 行为变更必须同步文档：产品线进 `docs/gmix/` SRS/SDD 与 `docs/contracts/` ICD；L1 契约升版并更新变更记录；文档地图 `docs/README.md` 同步。
- 新增需求必须带编号、优先级、来源、验收方法；未确认项登记 TBD/T 编号，不得写死猜测值。
- 口径变更（字段、公式、CSV 列、峰判定）必须同步 ICD、SRS、SDD、夹具与验证用例，并在提交说明中明确。

## 夹具与数据

- 新增测试数据一律放 `tests/fixtures/{platform,gmix,lga50}/` 并提交；**禁止依赖 `bin/`、`obj/` 或开发者本机路径**。
- 夹具缺失应报 FAIL，不得改成 SKIP。
- 原始抓包与实机结果不入库；新增或替换时同步 `docs/data-assets.md`（字节数、SHA256、采集参数）。
- 新增验证用例时同步更新验证入口清单与对应 SDD 的测试资产表。

## 提交

- 提交信息格式：`<type>(<scope>): <一句话为什么>`，type ∈ feat/fix/chore/docs/test/refactor；一次提交只做一件可描述的事。
- 不提交 `bin/`、`obj/`、`.vs/`、`*.user`、日志、临时脚本；`.trae/rules`、`.trae/skills` 属于团队资产，需提交。
