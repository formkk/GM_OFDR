# 改动工作流

- 每次改动完成前必须本地验证并通过：
  - `dotnet build ofdr_platform/src/OFDR.Platform.sln` → 0 警告 0 错误（`TreatWarningsAsErrors` 已开启）；
  - `dotnet run --project ofdr_platform/src/OFDR.Verify` → `PASS 11+，FAIL 0，SKIP 0`，退出码 0。
- 提交说明中写明上述两条命令的结果（数值可粘贴原文），不得只写"已测试"。
- 行为变更必须同步文档：新需求进 SRS（含编号、优先级、来源、验收方法），实施状态回填 SDD §13.4，验证项回填 SDD §13.2。
- 新增测试数据一律放 `tests/fixtures/` 并提交；**禁止依赖 `bin/`、`obj/` 或开发者本机路径**。夹具缺失应报 FAIL，不得改成 SKIP。
- 新增验证用例时同步更新 `OFDR.Verify/Program.cs` 的运行清单与 SDD §10 的测试资产表。
- 提交信息格式：`<type>(<scope>): <一句话为什么>`，type ∈ feat/fix/chore/docs/test/refactor；一次提交只做一件可描述的事。
- 不提交 `bin/`、`obj/`、`.vs/`、`*.user`、日志、临时脚本；`.trae/rules`、`.trae/skills` 属于团队资产，需提交。
