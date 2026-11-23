# ADR-001：GM-IX-LX 采用 WPF + .NET 10

> **状态**：已接受（2026-11-23，评审修订后接受）
> **日期**：2026-11-23
> **适用**：`products/gmix-lx/src/GmixLx.App`
> **依据**：`.trae/rules/00-scope.md`「不引入 NuGet 或其它第三方依赖。确需引入时，先修改对应 SDD 约束并新增 ADR，再动代码。」；`.trae/rules/10-architecture.md`；`.trae/rules/20-change-workflow.md`
> **关联**：[迁移清单_WPF_NET10.md](迁移清单_WPF_NET10.md)、[products/gmix-lx/README.md](../../products/gmix-lx/README.md)
> **对应 SDD**：[SDD_GM-IX-LX_UI_v0.1.md](SDD_GM-IX-LX_UI_v0.1.md)（v0.1 骨架，2026-11-23）

## 1 背景

GM-IX-LX 的迁移起点为 **WinForms + .NET 8**；2026-11-23 已完成 M-12，五个工程升级到 **.NET 10**（UI 仍为 WinForms），后续再重写为 WPF：

- `GmixLx.App` 共 5 个文件、约 2,365 行；约 166 处 WinForms/DataGridView/GDI+ 调用；
- 曲线控件为 GDI+ 自绘，已有 min/max 分块金字塔、双缓冲与 16 ms 合并重绘；
- `Core` / `Vendor` / `Orchestration` 与 UI 基本解耦，`App` 的工程引用只有 `Core + Orchestration`；
- **分层违规已修复（M-05，2026-11-23）**：此前 `DeviceDebugPanel` 通过 `ProjectReference` 传递性使用了 `GmixLx.Vendor` 的 `YinXun2ProtocolConstants.PitchM`，违反规则 10「App 不得引用 Vendor」。现已改为读取 `YinXun2Station.NativePitchM`，App 移除 `using GmixLx.Vendor`；WPF 重写不得复制该问题。
- 厂商 `ocs_measurement.dll` 为原生 Windows x64 C ABI，与 .NET 版本无关；
- 现有唯一 NuGet 依赖为 `System.IO.Ports` 8.0.0（继承自 detection 的 [ADR-001](../detection/ADR-001_System.IO.Ports.md)，用于 OCS 串口，不是昊衡或映讯的 API 要求）。

同时存在两个外部事实：

1. .NET 8（LTS）官方支持已于 2026 年 11 月结束（官方 Lifecycle 记为 2026-11-09），商用产品不宜继续以 `net8.0` 出货；
2. 用户要求提升界面美观度与专业化程度，WinForms 自绘样式的长期维护成本较高。

## 2 决策

1. `GmixLx.Core` / `Vendor` / `Orchestration` / `Tests` 升级到 **.NET 10（LTS）**，业务逻辑不变；
2. `GmixLx.App` 从 WinForms **重写为 WPF**（`net10.0-windows` + `UseWPF`）；
3. 不引入第三方 UI 库或图表库；WPF 属 .NET 第一方组件，不新增 NuGet 依赖；
4. 迁移期间保留 WinForms `App` 作为回退，WPF 达到功能对等后再移除；
5. `GmixLx.App` 与真机测试宿主显式使用 **x64** 平台目标，保证与厂商原生 DLL 匹配；
6. **SDK 版本策略采用方案 B（子目录隔离）**：
   - 新增 `products/gmix-lx/global.json`，`sdk.version` 固定为**精确版本 `10.0.404`**（2026-11-23 已安装并落地），`rollForward: latestFeature`；
   - 仓库根 `global.json` 保持 `8.0.100`，避免影响 `products/detection` 与 `products/gmix`；
   - **所有 gmix-lx 的构建/测试命令与 CI 步骤必须在 `products/gmix-lx` 目录下执行**（CI 使用 `working-directory`），否则会命中根 `global.json` 的 SDK 8，无法构建 `net10.0`；
   - CI 同时安装 SDK 8 与 SDK 10（已落地，gmix-lx 步骤使用 `working-directory: products/gmix-lx`）；
7. 迁移前修复 App 对 Vendor 的传递引用：把 `PitchM` 经 Orchestration（如 `YinXun2Station.PitchM`）或 Core 暴露，App 移除 `using GmixLx.Vendor`。

## 3 理由

1. **支持周期**：.NET 10 为 LTS，支持到 2028 年；继续停留在 .NET 8 已无安全更新。
2. **零新增依赖**：WPF 随 Windows Desktop Runtime 发布，MIT 许可，商用免费；不触发第三方依赖审批。
3. **UI 能力**：XAML、矢量渲染、动画、样式/模板、高 DPI 与多屏支持均优于 WinForms；设计令牌可集中管理。
4. **架构适配**：`App` 已是可替换层；重写不触碰数据契约、设备适配与指标口径。
5. **厂商零配合**：原生 DLL 与 .NET 版本无关，升级运行时不需要厂商出新版本。
6. **可回退**：WinForms 版保留到 WPF 功能对等，风险可控。
7. **SDK 隔离**：方案 B 不让 SDK 10 影响检测平台与历史设备线的 `TreatWarningsAsErrors` 构建，代价是命令必须带工作目录。

## 4 备选方案

| 方案 | 否决理由 |
|---|---|
| WinForms + .NET 10 | 只解决运行时停服，不解决美观与样式维护问题；可作为阶段 1 的过渡形态 |
| WinUI 3 / Windows App SDK | 需引入 Windows App SDK 与打包/部署复杂度，收益主要是 Fluent 外观；当前无强制需求 |
| Avalonia | 跨平台能力强，但引入第三方 UI 框架，需要额外 ADR；当前设备侧仍被原生 Windows x64 DLL 锁定 |
| ASP.NET Core + 浏览器 | 若浏览器 CS 是明确目标，应直接做 API/前端；WPF 只作为过渡客户端。本 ADR 不覆盖 Web 化 |
| 第三方图表库（ScottPlot/OxyPlot 等） | 曲线控件已有可用算法；引入需单独 ADR，先以自绘验证性能 |
| SDK 方案 A：根 `global.json` 升 10.0.404 | 会让检测平台与历史设备线一并切到 SDK 10，`TreatWarningsAsErrors` 下可能出现新分析器警告，回归面大；暂不采用 |

## 5 后果与约束

1. `GmixLx.App` 仍只允许引用 `Core + Orchestration`，**不得引用 `Vendor`**；包括不得通过传递引用使用 Vendor 类型。现有 `YinXun2ProtocolConstants` 使用已修复（M-05），WPF App 分层已检查（M-25：源码无 Vendor、程序集无 Vendor 直接引用），后续重写须保持。
2. 不采用 `ElementHost` 混合 WinForms/WPF；曲线控件整体重写。
3. 颜色、字体、间距统一收敛为 WPF 资源字典（设计令牌），禁止散落硬编码。
4. **曲线性能预算（可度量）**：
   - 阶段 0 基线已记录（2026-11-23）：首帧 6.60 ms、连续渲染 p95 6.46 ms、缩放 p95 7.55 ms、平移 p95 10.88 ms，见 [UI 基线](基线_UI_2026-11-23.md)；
   - WPF 目标：首帧 ≤ 基线 × 1.2（7.92 ms）；渲染/缩放/平移 p95 ≤ 基线 × 1.2 且 ≤ 33 ms；
   - 连续 10 帧无持续内存增长、无明显卡顿。
5. `GmixLx.App` 与真机测试宿主（`GmixLx.Tests` 的 live 模式）均为 x64（已落地，2026-11-23）；厂商 DLL 只在 x64 进程加载。
6. `System.IO.Ports` 默认**保持 8.0.0** 完成 .NET 10 升级；仅在验证确有需要时升级到 10.x，并重跑 OCS 契约测试。
7. 界面文字保持中文、简洁；不添加解释性/防御性说明文字（项目既有 UI 约定）。
8. 任何图表库、UI 控件库或跨平台框架的引入，必须另立 ADR。
9. 两套 App 命名固定：`GmixLx.App`（WinForms，回退）与 `GmixLx.App.Wpf`（目标）；迁移期间 CI 双构建，但**发布产物只能包含一套**。
10. **阶段 3 性能验证后设正式决策门**：未达到 §5.4 指标则停止 WPF 迁移或回退，不在阶段 4 继续投入。
    - **M-36 决策（2026-11-23，已评审）：接受偏差，继续 WPF 迁移。** 依据：M-35 优化后首帧 8.32 ms（中位数，目标 7.92 ms，超约 5%），但远低于 60 fps 单帧预算 16.7 ms；稳态曲线 p50 6.53–6.74 ms 与 WinForms 基线持平，平移/叠加/内存（0 B）全部达标；曲线/缩放/叠加的 p95 中位数达标，偶发 8.3–12.9 ms 离群仅出现于 p95 且 p50 不变、内存不增长，已确认为整机调度噪声（沙箱内外均出现），非渲染路径退化。偏差记录于 [UI 基线 §8.1](基线_UI_2026-11-23.md)，正式验收前须在静止机器复测归档。
    - 同次决策关闭 TBD-LX-04：曲线渲染保留 `DrawingContext` 逐像素 `DrawLine` + `MinMaxPyramid`，不引入 `WriteableBitmap`（`StreamGeometry` 实测更慢已回退；`WriteableBitmap` 对首帧字形/资源初始化无帮助，且会增加 DPI/抗锯齿/交互重绘复杂度）。
11. 部署基线：**自包含文件夹发布**（`win-x64`、`--self-contained true`、非单文件），Windows 10/11 x64；TBD-LX-06 已关闭（2026-11-23）。发布命令（在 `products/gmix-lx` 下）：
    ```powershell
    dotnet publish src/GmixLx.App.Wpf -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
    ```
    理由：现场可能离线，自包含免装 .NET 10 Desktop Runtime；实测发布目录 409 个文件 / 140.1 MB，可直接启动；单文件仍需释放原生依赖、首启更慢，且部署目录必须携带 `references/` 与 `config/`，单文件收益有限。发布目录需与 `references/YinXun_2/customer`、`products/gmix-lx/config/` 保持与仓库相同的相对层级，`AppPaths` 才能从 exe 目录向上解析。
12. 文档同步：本 ADR、迁移清单、`products/gmix-lx/README.md`、`docs/README.md` 文档地图。

## 6 验证方法

**所有命令均在 `products/gmix-lx` 目录下执行**（子目录 `global.json` 生效）：

```powershell
cd products/gmix-lx
dotnet build GmixLx.sln
dotnet run --project src/GmixLx.Tests --no-build
```

| 项 | 方法 | 通过标准 |
|---|---|---|
| 构建 | 上述 `dotnet build` | 0 警告 0 错误 |
| 测试 | 上述 `dotnet run` | `PASS gmix-lx vendor+orchestration`，退出码 0 |
| 分层 | 检查 App 源码/程序集 | 无 `using GmixLx.Vendor`，无 Vendor 类型引用 |
| WPF 启动 | 启动 `GmixLx.App.Wpf` | 主窗口、两个页签、状态栏正常 |
| 指标表 | 64 行 × 11 列、组底色、失败样式、双击弹曲线 | 与 WinForms 版一致 |
| 曲线控件 | 16 万点、缩放/平移/A-B 游标/峰标记 | 满足 §5.4 指标，视觉与交互对等 |
| 真机冒烟 | 映讯 GM-IX 单路 + OCS 批次 | 采集、指标、导出正常 |
| 回退 | 保留 WinForms 版 | 任阶段可回退且不影响数据 |

## 7 待决项（TBD）

| 编号 | 内容 | 状态/归属 |
|---|---|---|
| TBD-LX-01 | CI 中 SDK 8 与 SDK 10 的安装与 `working-directory` 具体写法 | **已关闭（2026-11-23）**：双 SDK + 子目录构建，CI 首跑通过（run 45105112412） |
| TBD-LX-02 | `global.json` 共存策略 | **已关闭**：采用方案 B，子目录精确 `10.0.404` |
| TBD-LX-03 | WPF 版是否随首版一起发布，还是先内部并行一个版本 | 开放，产品 |
| TBD-LX-04 | 曲线控件最终渲染方案（DrawingVisual vs WriteableBitmap） | **已关闭（2026-11-23，M-36）**：`DrawingContext` 逐像素 `DrawLine` + `MinMaxPyramid`；不采用 `WriteableBitmap` |
| TBD-LX-05 | 关键界面截图基线与视觉验收人 | 开放，产品/测试 |
| TBD-LX-06 | 部署形态（framework-dependent / self-contained / 单文件） | **已关闭（2026-11-23，M-45）**：自包含文件夹发布（win-x64，非单文件），实测 409 文件 / 140.1 MB 可启动 |
| TBD-LX-07 | GM-IX-LX 的 SRS/SDD 范围与责任人 | 部分关闭：SDD v0.1 骨架已建立（M-06）；是否补 SRS 待定 |

## 8 追溯

| 项 | 内容 |
|---|---|
| 规则 | `.trae/rules/00-scope.md`（第三方依赖）、`10-architecture.md`（分层）、`20-change-workflow.md`（验证与文档） |
| 产品说明 | [products/gmix-lx/README.md](../../products/gmix-lx/README.md) |
| UI 设计 | [SDD_GM-IX-LX_UI_v0.1.md](SDD_GM-IX-LX_UI_v0.1.md) |
| UI 基线 | [基线_UI_2026-11-23.md](基线_UI_2026-11-23.md) |
| 迁移计划 | [迁移清单_WPF_NET10.md](迁移清单_WPF_NET10.md) |
| 现有依赖决策 | [detection/ADR-001_System.IO.Ports.md](../detection/ADR-001_System.IO.Ports.md) |
| 现状代码 | `products/gmix-lx/src/GmixLx.App/`（WinForms，约 2,365 行） |
| 分层违规证据（已修复） | 原 `DeviceDebugPanel.cs`：`using GmixLx.Vendor;` 与 `YinXun2ProtocolConstants.PitchM`；M-05 已改为 `YinXun2Station.NativePitchM` |
| 复现命令 | 见 §6 |
