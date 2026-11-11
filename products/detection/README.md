# 检测平台（OCS + OFDR）

**产品边界占位**：下一代检测系统上位机，是 GMIX 产品线的后续版本（继承链 GM-IX → LGA50 → 检测平台）。

| 内容 | 位置 |
|---|---|
| 检测系统文档骨架（系统边界、流程、TBD） | [`docs/detection/`](../../docs/detection/README.md) |
| 当前代码骨架 | `products/gmix/src/Gmix.OcsVerify`（核验域）、`Gmix.OcsCheck`（编排）、`Gmix.OcsCheck.App`（UI） |
| OCS 厂商协议 | [`references/ocs/`](../../references/ocs/) |
| OCS 实物照片 | [`docs/assets/ocs/`](../../docs/assets/ocs) |
| 夹具 | [`tests/fixtures/ocs/`](../../tests/fixtures/ocs) |

**状态**：骨架。SRS 明确模块边界后，把 `Gmix.Ocs*` 迁入本目录并重命名为 `Gmix.Detection.*`，通过项目引用复用 `products/gmix/src/Gmix.Core` 与 `Gmix.Vendor`，不复制实现。
