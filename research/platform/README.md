# OFDR 预研平台（冻结）

基于 LGA-50 演示分析试做的上位机，7 个程序集，未接入硬件；v0.1.0 已冻结，**不是产品实现基线**。

```powershell
dotnet build research/platform/src/OFDR.Platform.sln
dotnet run --project research/platform/src/OFDR.Verify
```

- 文档：[`docs/platform/`](../../docs/platform/SRS_OFDR_Platform_v0.9.md)
- 夹具：`tests/fixtures/platform/`、`tests/fixtures/lga50/`
- 与产品线无继承关系；其 `StandardScan` 字段不得回流 `products/gmix`。
