# LGA50 设备线（昊衡 LGA50-D）

以适配方式接入 GMIX 产品主线的第二设备线；本目录只保留抓包与工具，**不单独拥有上位机**。正式继承链：GM-IX → LGA50 → 检测平台。

## 文档入口

| 内容 | 位置 |
|---|---|
| L1 统一数据契约（`CurveScan`，评审中） | [`docs/contracts/统一数据契约_v0.7.md`](../../docs/contracts/统一数据契约_v0.7.md) |
| 设备协议与实测口径（帧格式、参数语义、X 轴换算） | [`docs/lga50/LGA50-D_远程控制协议分析.md`](../../docs/lga50/LGA50-D_远程控制协议分析.md) |
| 厂商远程控制说明 PDF | [`references/lga50/`](../../references/lga50/) |
| 抓包数据清单（字节数 / SHA256 / 采集参数） | [`docs/data-assets.md`](../../docs/data-assets.md) |
| 代码归口 | `products/gmix/src/Gmix.Import/`（抓包 → GM-IX 组目录）、`products/gmix/src/Gmix.Acquirer/`（在线采集） |

## 目录

```
devices/lga50/
├─ capture/            # 远程控制抓包（不入库，清单见 docs/data-assets.md）
└─ tools/
   ├─ check_contract.ps1        # 契约机械检查（CI 入口，7 项校验）
   ├─ lga50_probe.py            # 远程控制协议探测（连设备抓原始字节）
   ├─ analyze_frames.py         # 抓包帧结构分析（帧边界、点数、粘包）
   ├─ probe_matrix.py           # 参数响应矩阵探针（逐用例建连、设置、采集汇总）
   ├─ t07_ps_dimension.py       # T-07：联讯 ps 量纲判定（标度律实验）
   └─ t07_confirm_rss.py        # T-07 补充：RSS 假设确认（已闭环，ps = √(|P|²+|S|²)）
```

## 验证

```powershell
powershell -File devices/lga50/tools/check_contract.ps1    # 期望 PASS 7，FAIL 0
```
