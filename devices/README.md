# devices — 设备线资料与实验工具

| 目录 | 设备 | 内容 |
|---|---|---|
| `lga50/` | 昊衡 LGA50-D | 远程控制抓包（`capture/`，不入库）、协议探测与契约检查工具（`tools/`） |
| `ocs/` | OCS 光开关 | 协议实测工具（`tools/ocs_measure.ps1`）、实测原始日志（`capture/`，不入库）；厂商协议在 `references/ocs/` |

OCS 实测端口（2026-11-11）：8×8 = COM3 @ 115200 8N1，1×8 = COM4 @ 9600 8N1。工具自动还原光路，默认不发送 `<RST>`；结论见 [OCS 协议实测报告](../docs/detection/OCS_协议实测报告_2026-11-11.md)。

设备线不单独拥有上位机；产品代码在 `products/gmix/`，厂商资料在 `references/<device>/`，治理文档在 `docs/<line>/`。
