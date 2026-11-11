# 文档地图

> 本文件是仓库治理文档的唯一入口，只回答三件事：**哪份是正本、哪份是证据、哪份已过期**。
> 最后核对：2026-11-11（目录按生命周期重排）。定位口径：`research/platform`（原 `ofdr_platform`）为未接入硬件的预研平台；`products/gmix`（原 `GM-IX`）为产品主线；`Study/` 为学习、研究及相关知识库。产品方向：OCS 光开关与 OFDR 设备组成检测系统，其上位机软件是 LGA50 自研上位机的后续版本（继承链 GM-IX → LGA50 → 检测平台）。

## 1. 三条规则

1. **正本唯一**：同一主题只有一个"现行正本"，其余要么是证据，要么是归档；副本不得与正本同名同版本号。
2. **契约分层**：L1 帧契约与 L2 记录/处理契约同放 `docs/contracts/`；两者是**层不同**，不是新旧关系。
3. **证据不改**：预研分析、测试报告、调研记录只追加勘误，不覆盖历史结论；结论变化时改正本，证据保留。

### 1.1 开发历史与目录对应

| 阶段 | 历史产物 | 当前承载目录 | 性质 |
|---|---|---|---|
| 0 | 昊衡 LGA-50 演示（未接硬件） | `research/demo-lga50/` | 行为基线，只读 |
| 1 | 演示分析后的试做上位机 | `research/platform/` | 预研，已冻结 |
| 2 | 映讯 HRAS 9000 接入 + GMIX 上位机 | `products/gmix/`、`references/HRAS9000/`、`references/联讯/` | 产品第 1 代 |
| 3 | 昊衡 LGA50 适配器 | `products/gmix/src/Gmix.Import/`、`devices/lga50/`、`docs/lga50/`、`references/lga50/` | 产品第 2 代 |
| 4 | OCS 1×8 + 8×8 DUT 检测平台 | `docs/detection/`、`products/detection/`、`products/gmix/src/Gmix.Ocs*`（骨架） | 下一代，规划中 |

目录角色：`products/` 放产品代码，`research/` 放预研与演示，`devices/` 放设备线抓包与实验工具，`references/` 放厂商资料，`docs/` 放治理文档。

## 2. 权威矩阵（按主题）

| 主题 | 正本 | 层级 / 状态 | 说明 |
|---|---|---|---|
| 跨设备 L1 帧 | [统一数据契约_v0.7.md](contracts/统一数据契约_v0.7.md) | L1，评审中未冻结 | `CurveScan`；条款编号 G/L/C/R/D/E |
| GM-IX 记录与处理 | [GM-IX_数据与处理规范_v1.0.md](contracts/GM-IX_数据与处理规范_v1.0.md) | L2，现行（ICD v1.0） | `scan.json` / `events.json` / `summary.json` / `calibration.json` / `group.json` |
| GM-IX 需求 | [GM-IX_上位机软件需求规格_SRS_v0.1.md](gmix/GM-IX_上位机软件需求规格_SRS_v0.1.md) | 产品主线 | 功能、显示口径 DS、NFR |
| GM-IX 设计 | [GM-IX_上位机软件设计说明书_SDD_v0.1.md](gmix/GM-IX_上位机软件设计说明书_SDD_v0.1.md) | 产品主线 | 进程/线程、模块、厂商边界 |
| LGA50 设备协议 | [LGA50-D_远程控制协议分析.md](lga50/LGA50-D_远程控制协议分析.md) | 实测证据 | 厂商 PDF 在 `references/lga50/` |
| 预研平台需求 | [platform/SRS_OFDR_Platform_v0.9.md](platform/SRS_OFDR_Platform_v0.9.md) | 预研，冻结 | 未接硬件，不作产品基线 |
| 预研平台设计 | [platform/SDD_OFDR_Platform_v0.9.md](platform/SDD_OFDR_Platform_v0.9.md) | 预研，冻结 | `StandardScan` 保留预研形态 |
| 预研平台评审 | [platform/REVIEW_OFDR_Platform_v0.1.0.md](platform/REVIEW_OFDR_Platform_v0.1.0.md) | 预研，冻结 | 一期 v0.1.0 |
| CNN 评估 | [cnn/评估方案.md](cnn/评估方案.md) / [评估报告.md](cnn/评估报告.md) / [落地评估.md](cnn/落地评估.md) | 独立评估线 | 不改产品线，不改契约 |
| 培训与研究 | [Study/OFDR检测全流程培训_上位机开发者版.md](../Study/OFDR检测全流程培训_上位机开发者版.md) | 知识库 | 与契约冲突时以 `docs/contracts/` 为准 |
| 检测系统（OCS + OFDR） | [detection/README.md](detection/README.md) | SRS v0.2 评审稿，SDD 待建 | 上位机为 LGA50 后续版本；TBD-D01~D10 |

## 3. 文档清单（按目录）

### 3.1 `docs/contracts/` — 契约正本

| 文件 | 性质 |
|---|---|
| `统一数据契约_v0.7.md` | L1 帧契约（评审中，未冻结） |
| `GM-IX_数据与处理规范_v1.0.md` | L2 记录与处理契约（ICD v1.0） |

### 3.2 `docs/gmix/` — 产品主线

| 文件 | 性质 |
|---|---|
| `GM-IX_上位机软件需求规格_SRS_v0.1.md` | 正本：需求与验收（含评审决议） |
| `GM-IX_上位机软件设计说明书_SDD_v0.1.md` | 正本：设计与实施状态 |
| `GM-IX_三级数据处理链路数据报告.md` | 证据：三级链路实测 |
| `测试报告.md` | 证据：实机接入主报告 |
| `测试报告_1000M.md` | 证据：千兆链路补测章节 |
| `上位机数据协议调研与讨论.md` | 证据：协议选型过程记录 |

代码侧可执行证据仍在 `products/gmix/investigations/signal-drift/`（脚本、原始数据、报告），结论以 `docs/gmix/` 正本为准。

### 3.3 `docs/lga50/` — 第二设备线

| 文件 | 性质 |
|---|---|
| `LGA50-D_远程控制协议分析.md` | 正本：该设备协议与实测口径 |

抓包与工具仍在 `devices/lga50/capture/`、`devices/lga50/tools/`；厂商 PDF 已归 `references/lga50/`。

### 3.4 `docs/detection/` — 检测系统（OCS + OFDR）

| 文件 | 性质 |
|---|---|
| `README.md` | 骨架：系统边界、检测流程、文档规划、已知事实、TBD-D01~D10 |
| `SRS_检测平台_v0.2.md` | 评审稿：范围、64 路径、测量口径、关注提示、基线、批次状态机、验收标准、TBD |
| `OCS_控制协议分析.md` | 正本：1×8 / 8×8 协议提取、状态模型、实现约束、TBD-O01~O10 与测试获取方法 |

SRS v0.2 已建；SDD、通道编排与时序、接线核验与基线管理设计待建。工程占位骨架：`products/gmix/src/Gmix.OcsVerify`（核验域纯逻辑）、`Gmix.OcsCheck`（编排进程）、`Gmix.OcsCheck.App`（UI，不引 Vendor）。厂商协议文档在 `references/ocs/`，夹具在 `tests/fixtures/ocs/`。

### 3.5 `docs/platform/` — 预研平台（冻结）

| 文件 | 性质 |
|---|---|
| `SRS_OFDR_Platform_v0.9.md` | 正本：预研需求 |
| `SDD_OFDR_Platform_v0.9.md` | 正本：预研设计 + 一期实施记录 |
| `REVIEW_OFDR_Platform_v0.1.0.md` | 正本：一期评审摘要 |

### 3.6 `docs/evidence/` — 预研取证（非治理）

| 文件 | 性质 |
|---|---|
| `QT2144_qt_acq_反推说明书.md` | 证据：采集 SDK 反推 |
| `映讯QT2144_SDK接入说明书.md` | 证据：接入设计与调用示例 |
| `HRAS9000_上位机与QT2144_SDK_核实报告.md` | 证据：厂商上位机与 SDK 同一性核实 |

### 3.7 `docs/cnn/` — 研究分支

| 文件 | 性质 |
|---|---|
| `评估方案.md` | 正本：任务定义与实验矩阵 |
| `评估报告.md` | 正本：评估结论 |
| `落地评估.md` | 正本：落地架构、阈值与阶段路线 |

### 3.8 `docs/assets/` — 项目自有资产

| 文件 | 说明 |
|---|---|
| `ofdr_platform.png` | 四层架构图（原在 `references/`） |
| `光脉OFDR演示系统.pptx` | 项目演示稿（原在 `references/`） |
| `OFDR平台软件设计总结_一期_7页.pptx` | 一期设计总结 |
| `gmix/厂商UI截图.jpg`、`gmix/厂商UI截图_N4.jpg`、`gmix/校准值.jpg` | GM-IX 实机证据截图（原散落在 GM-IX 根目录） |
| `ocs/` | OCS 光开关（1×8 / 8×8）实物照片（厂商协议文档在 `references/ocs/`） |

### 3.9 `docs/archive/gmix/` — 历史版本（只读）

| 文件 | 说明 |
|---|---|
| `上位机数据Schema_v0.1.md` | 旧 Schema（版本倒挂已修正，移入归档） |
| `上位机数据Schema_v0.2.md` | 被 ICD v1.0 取代 |
| `数据处理契约_v0.1.md` | Part B 已并入 ICD v1.0 |

### 3.10 `Study/` — 学习、研究及相关知识库

| 文件 | 性质 |
|---|---|
| `OFDR检测全流程培训_上位机开发者版.md` | 正本：从光到文件的全流程培训 |
| `GM-IX_数据与处理规范_v1.0.md` | 学习用历史快照，**非正本**（见 §4） |

## 4. 过期与重复清单

| 文件 | 判定 | 处理 |
|---|---|---|
| `Study/GM-IX_数据与处理规范_v1.0.md` | 过期快照：867 行，缺 11-06 新增的 `ps_db`、`zeroPeak`、`distScale=0.9502` 等段落；SHA 与正本不同 | 保留在知识库并标注"过期快照"；正本见 `docs/contracts/` |
| `docs/gmix/测试报告.md` + `测试报告_1000M.md` | 主报告 + 补测章节，非重复 | 保留；先读主报告再读补测 |
| `docs/gmix/上位机数据协议调研与讨论.md` | 过程记录，非正本 | 保留为证据 |
| `docs/archive/gmix/*` | 历史版本 | 只读，不作为实现依据 |
| `tests/fixtures/lga50/heng_capture_head.txt` | 已使用并入库 | P2 已统一到 `tests/fixtures/{platform,gmix,lga50}/` |

## 5. 数据资产（不进 git，清单见 [data-assets.md](data-assets.md)）

| 数据 | 位置 | 当前状态 |
|---|---|---|
| LGA50 抓包 | `devices/lga50/capture/`（约 101 MB） | 未入库 |
| GM-IX 实机结果 | `products/gmix/results/` | 被 `.gitignore` 忽略 |
| GM-IX 运行时 | `products/gmix/runtime/` | 被 `.gitignore` 忽略 |
| 漂移验证原始帧 | `products/gmix/investigations/signal-drift/raw*` | 被 `.gitignore` 忽略 |
| CNN 派生数据 | `CNN/data/`、`CNN/runs/` | 被 `.gitignore` 忽略 |

**已建立**：[data-assets.md](data-assets.md) 记录 LGA50 抓包、GM-IX 结果与漂移数据的字节数、SHA256 和采集参数；干净克隆可验证什么、不能复现什么，见该文 §6。

## 6. 新增内容放哪

| 内容 | 放置位置 |
|---|---|
| L1 / L2 契约 | `docs/contracts/` |
| 产品需求、设计、测试报告 | `docs/gmix/` |
| 检测系统需求、设计、基线管理设计 | `docs/detection/` |
| 预研平台文档 | `docs/platform/` |
| 预研取证、SDK 分析 | `docs/evidence/` |
| CNN 评估 | `docs/cnn/` |
| 学习、研究、专题笔记、培训 | `Study/` |
| 厂商二进制、手册 | `references/`（禁止复制进源码树） |
| 项目自有图、演示稿 | `docs/assets/` |
| 历史版本 | `docs/archive/<线>/` |
| 过程证据与探针 | 对应线的 `investigations/`，结论回写正本 |

## 7. 已解决与仍待处理

**已解决（2026-11-10）**
1. 物理收口：原 `GM-IX/docs`、`LGA50/docs`、`CNN/docs` 已并入 `docs/`；`docs/` 根不再混放厂商 SDK 取证。
2. 版本倒挂：`上位机数据Schema_v0.1.md` 与 v0.2 一并移入 `docs/archive/gmix/`。
3. 参考目录：LGA50 厂商 PDF 已归 `references/lga50/`；项目自有图与 PPT 已移出 `references/`。
4. `Study/` 定位为学习、研究及相关知识库，不并入治理文档根。
5. 夹具统一到 `tests/fixtures/{platform,gmix,lga50,ocs}/`，代码与文档路径已同步。
6. 数据资产清单已建立：[data-assets.md](data-assets.md)。
7. CI 已覆盖 GM-IX 构建、`Gmix.Tests`、预研平台验证与 L1 契约检查。
8. `.trae/rules` 已按"products/gmix 产品主线 / research/platform 预研"重写。
9. `devices/lga50/README.md` 入口已建立，指向契约、协议分析与数据清单。
10. `add-scan-source` 技能已补产品线归口（`Gmix.Acquirer`/`Gmix.Import`），与规则 30 对齐。

**仍待处理**
1. `SourceType` 取值：L1 契约只有 `Lianxun | HengLga50`；预研 SDD 仍为三值含 `Replay`。预研字段不得回流产品。
2. `devices/lga50/capture/` 与 `products/gmix/results/` 的原始数据按体积与版权边界不入库，只保留哈希清单；CNN 复现需先恢复数据。
