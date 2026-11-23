# GM-IX-LX WPF + .NET 10 迁移清单

> **状态**：草案（评审修订稿，待复审）
> **日期**：2026-11-23
> **关联 ADR**：[ADR-001_WPF与NET10.md](ADR-001_WPF与NET10.md)
> **适用**：`products/gmix-lx/`
> **原则**：UI 可替换、领域层不动、每阶段可回退、每阶段有验证、命令在 `products/gmix-lx` 目录下执行。

## 1 迁移边界

| 层 | 是否改动 | 说明 |
|---|---|---|
| `GmixLx.Core` | 仅改目标框架 | 纯域、曲线/峰/指标、参考标定，逻辑不变 |
| `GmixLx.Vendor` | 仅改目标框架 | 厂商归口；原生 DLL 与 .NET 版本无关；`System.IO.Ports` 默认保持 8.0.0 |
| `GmixLx.Orchestration` | 仅改目标框架 + 暴露 `PitchM` | 台架装配、指标表、组提交；引用 `Gmix.Core` 保持不变 |
| `GmixLx.Tests` | 仅改目标框架 + x64 | 控制台测试，不引用 App |
| `GmixLx.App` | **整体重写为 WPF** | 唯一需要重写的工程；同时修复对 Vendor 的传递引用 |
| `Gmix.Core` | 不动 | 保持 `net8.0`，可被 `net10.0` 工程引用 |

## 2 阶段与任务

### 阶段 0：决策、基线与既有缺陷修复（M-0x）

| 编号 | 任务 | 优先级 | 验收方法 |
|---|---|---|---|
| M-01 | 复审并接受 ADR-001 **（已完成 2026-11-23）** | 高 | ADR 状态改为「已接受」 |
| M-02 | 采集 WinForms 关键界面截图基线（指标表、距离域曲线、曲线弹窗） **（已完成 2026-11-23，见 [基线_UI_2026-11-23.md](基线_UI_2026-11-23.md)）** | 高 | 截图入库或登记路径 |
| M-03 | 记录曲线交互性能基线：首帧耗时、缩放/平移帧时间 p95、连续采集 10 帧内存 **（已完成 2026-11-23，见 [基线_UI_2026-11-23.md](基线_UI_2026-11-23.md)）** | 高 | 记录数值与测量方法 |
| M-04 | 在 `products/gmix-lx/README.md` 与 `docs/README.md` 增加文档入口 | 中 | 链接可达 |
| M-05 | 修复 App 对 Vendor 的传递引用：`PitchM` 经 `YinXun2Station`（Orchestration）暴露，`DeviceDebugPanel` 移除 `using GmixLx.Vendor` **（已完成 2026-11-23）** | 高 | App 源码无 Vendor 引用；构建 0 警告 0 错误；测试 PASS；曲线页峰距正常 |
| M-06 | 建立 `docs/gmix-lx/` 的 SDD 骨架（UI 结构、数据流、线程模型），或书面声明 ADR + 清单暂代设计依据 **（已完成 2026-11-23，见 [SDD_GM-IX-LX_UI_v0.1.md](SDD_GM-IX-LX_UI_v0.1.md)）** | 高 | 文档存在或声明记录在 ADR §8 |

### 阶段 1：.NET 10 升级（保留 WinForms）（M-1x）

| 编号 | 任务 | 优先级 | 验收方法 |
|---|---|---|---|
| M-10 | 安装 .NET 10 SDK，确认 Windows Desktop Runtime 与现场 OS 在 .NET 10 支持范围 **（已完成 2026-11-23：SDK 10.0.404，WindowsDesktop 10.0.15）** | 高 | `dotnet --list-sdks` 含 10.0.404；`dotnet --list-runtimes` 含 WindowsDesktop 10.x |
| M-11 | 新增 `products/gmix-lx/global.json`，`sdk.version = 10.0.404`、`rollForward = latestFeature`；根 `global.json` 不动 **（已完成 2026-11-23）** | 高 | 在 `products/gmix-lx` 下 `dotnet --version` 为 10.0.404 |
| M-12 | 五个工程 TFM 改为 `net10.0` / `net10.0-windows` **（已完成 2026-11-23）** | 高 | 在 `products/gmix-lx` 下构建通过：0 警告 0 错误，测试 PASS |
| M-13 | `System.IO.Ports` **默认保持 8.0.0**；仅当验证确需时升 10.x **（已完成 2026-11-23：保持 8.0.0，net10 构建与 OCS 测试通过）** | 中 | OCS 契约测试通过；变更需在 ADR 追加记录 |
| M-14 | `GmixLx.App` 与 `GmixLx.Tests` 显式 `PlatformTarget=x64` **（已完成 2026-11-23：两工程 PlatformTarget=x64，产物 PE 均为 x64）** | 高 | 进程为 64 位，能加载厂商 DLL |
| M-15 | CI 同时安装 SDK 8 与 SDK 10；gmix-lx 的构建/测试步骤设置 `working-directory: products/gmix-lx` **（已完成 2026-11-23；CI 首跑通过，run 45105112412）** | 高 | CI 全部通过；不得从仓库根直接构建 gmix-lx |
| M-16 | 构建、测试、真机冒烟（命令均在 `products/gmix-lx` 下） | 高 | 0 警告 0 错误；测试 PASS；单路采集正常 |

### 阶段 2：WPF 骨架（M-2x）

| 编号 | 任务 | 优先级 | 验收方法 |
|---|---|---|---|
| M-20 | 新建 `GmixLx.App.Wpf`（`net10.0-windows`、`UseWPF`、x64），加入 `GmixLx.sln` **（已完成 2026-11-23：构建 0/0，启动验证通过）** | 高 | 构建通过 |
| M-21 | 设计令牌资源字典（配色、字体、间距、圆角） **（已完成 2026-11-23：`Themes/Tokens.xaml`，构建 0/0，启动验证通过；2026-11-23 按 VIAVI PCTMax 风格改版为炭黑+蓝，见[基线 §15](基线_UI_2026-11-23.md)）** | 高 | 与 WinForms 版视觉一致 |
| M-22 | 主窗口壳：两个页签 + 状态栏 + 窗口图标 **（已完成 2026-11-23：MainWindow + DeviceDebugView/MetricTableView 占位，状态栏绑定 MainViewModel，启动验证通过）** | 高 | 可启动、可切换 |
| M-23 | 指标表：64 行 × 11 列、8 行分组底色、失败样式、ToolTip、双击事件 **（已完成 2026-11-23：两行表头、失败行灰底+破折号、ToolTip 绑定、双击 RowActivated；截图见基线文档 §6）** | 高 | 与 WinForms 版逐项比对 |
| M-24 | 高 DPI 清单（PerMonitorV2） **（已完成 2026-11-23：`app.manifest` 声明 PerMonitorV2；实测 PerMonitorV2=True、窗口 DPI 144；96/144/192 渲染检查 11 列完整）** | 中 | 100%/150%/200% 缩放正常 |
| M-25 | App 分层检查：WPF App 不出现 `GmixLx.Vendor` 类型 **（已完成 2026-11-23：源码无 Vendor 匹配；程序集引用仅 Orchestration/Core；deps 中 Vendor 仅为 Orchestration 传递依赖）** | 高 | 源码检查通过 |

### 阶段 3：曲线控件技术验证（M-3x）

| 编号 | 任务 | 优先级 | 验收方法 |
|---|---|---|---|
| M-30 | 把 min/max 分块金字塔算法抽成与 UI 无关的纯类（放在 App.Wpf 内部或独立可视化辅助，不放入 Core 域） **（已完成 2026-11-23：`Visualization/MinMaxPyramid.cs`；20,000 随机区间与暴力扫描比对 0 失配）** | 高 | 单元测试或对照数据一致 |
| M-31 | WPF 曲线渲染器（DrawingVisual 或 WriteableBitmap） **（已完成 2026-11-23：`Visualization/CurvePlotControl.cs`；16 万点渲染成功，离屏首帧 14.53 ms；交互/峰标记待 M-32/M-33）** | 高 | 16 万点可显示 |
| M-32 | 缩放、平移、A/B 游标、悬停读数 **（已完成 2026-11-23：滚轮缩放、左键拖动平移、A/B 游标拖动、悬停读数；游标截图见基线文档 §7）** | 高 | 交互与 WinForms 对等 |
| M-33 | 峰标记、峰标签、ROI 底纹、阈值线 **（已完成 2026-11-23：P1–P5 标记/标签、ROI 5.37–11.31 m 底纹、112 dB 阈值线；截图见基线文档 §7；叠加后离屏首帧 26.38 ms，M-34 重点复核）** | 高 | 视觉对等 |
| M-34 | 性能测量：首帧 ≤ 基线×1.2；缩放/平移 p95 ≤ 33 ms 且 ≤ 基线×1.2；连续 10 帧无内存增长/卡顿 **（已完成 2026-11-23：首帧 13.97 ms、曲线 p95 7.85 ms 未达标；缩放 7.86 / 平移 7.71 / 叠加 7.58 / 内存 0 B 达标；进入 M-35）** | 高 | 数值达标并记录 |
| M-35 | 若性能不达标，评估 `WriteableBitmap` 或按需降采样 **（已完成 2026-11-23：预热 + 静态 Pen/Brush/FormattedText 缓存；7 次复测首帧 13.97→8.32 ms（中位）、曲线 p50 6.63 ms、叠加首帧 6.72 ms、内存 0 B；p95 偶发离群经沙箱内外对照确认为整机噪声；`StreamGeometry` 更慢已回退；结论：不采用 WriteableBitmap，交 M-36；数据见[基线 §8.1](基线_UI_2026-11-23.md)）** | 中 | 给出结论与取舍 |
| M-36 | **决策门**：阶段 3 结束评审是否继续 WPF；未达标则停止或回退 **（已完成 2026-11-23：评审结论「接受 8.32 ms 首帧偏差，继续 WPF」，记录于 [ADR-001 §5 第 10 条](ADR-001_WPF与NET10.md)，同时关闭 TBD-LX-04）** | 高 | 结论记录在 ADR §5 第 10 条 |

### 阶段 4：功能对等（M-4x）

| 编号 | 任务 | 优先级 | 验收方法 |
|---|---|---|---|
| M-40 | 设备调试页：ROI IL/RL 卡片、单次/连续采集、参数、峰表、保存 **（已完成 2026-11-23：`DeviceDebugView` + `DeviceDebugViewModel`，卡片/参数/峰表/CSV 对齐 WinForms；后台线程采集、单次与连续互斥、A-B 拖动重采、窗口关闭释放会话；截图见[基线 §9](基线_UI_2026-11-23.md)；真机采集待 M-16/M-51）** | 高 | 功能逐项勾选 |
| M-41 | 指标表双击弹出曲线窗口 **（已完成 2026-11-23：`CurveWindow` 复用 `CurvePlotControl`，轴换算 x = i×AxisScaleM + AxisOffsetM、峰阈/合并间距与 WinForms `CurveForm` 同口径、缺省视窗取 `ViewStartM/ViewEndM`；无数据/无曲线时状态栏按 WinForms 文案提示；窗口打开与关闭已验证，截图见[基线 §10](基线_UI_2026-11-23.md)）** | 高 | 正常打开与关闭 |
| M-42 | CSV 导出（指标表、曲线） **（已完成 2026-11-23：指标表页新增「导出 CSV」，直接复用 `MetricTableCsv` 列序与格式化，UTF-8 BOM；无结果时按钮禁用并提示；曲线 CSV 沿用 M-40 设备页「保存数据」（`distance_m,reflection_db`），`CurveForm` 本身无导出，保持一致；验证见[基线 §11](基线_UI_2026-11-23.md)）** | 高 | 文件内容与列序一致 |
| M-43 | 状态栏、错误提示、进度反馈 **（已完成 2026-11-23：指标表页补齐连接卡/判据卡/开始·停止，`MetricTableViewModel.Start` 按 WinForms 顺序校验并驱动 `YinXun2Station` + `MetricTableRunner`；状态栏文案与语气（就绪/采集进行中/完成/已停止/失败/参数错误）与 WinForms 一致，颜色由 `StatusTone` 驱动；路径解析收敛到 `AppPaths`；验证见[基线 §12](基线_UI_2026-11-23.md)；真机采集待 M-16/M-51）** | 中 | 与现有文案一致 |
| M-44 | SDK/标定路径解析、图标资源、单实例行为 **（已完成 2026-11-23：路径统一由 `AppPaths` 从 exe 目录向上解析（部署时把 `references/YinXun_2/customer` 与 `products/gmix-lx/config/…` 保持在 exe 的可达父级即可）；窗口图标用 pack URI、exe 图标经 `ApplicationIcon` 嵌入（实测可提取 32×32）；单实例用命名 Mutex，第二份实例置前已有窗口后静默退出（实测退出码 0），验证见[基线 §13](基线_UI_2026-11-23.md)）** | 中 | 部署后可运行 |
| M-45 | 确定部署形态（framework-dependent / self-contained / 单文件）并记录 **（已完成 2026-11-23：自包含文件夹发布 win-x64、非单文件；`dotnet publish -c Release -r win-x64 --self-contained true` 实测 409 文件 / 140.1 MB，发布 exe 启动存活、单实例与图标正常；记录于 [ADR-001 §5 第 11 条](ADR-001_WPF与NET10.md) 与 README §5，TBD-LX-06 关闭）** | 中 | ADR TBD-LX-06 关闭 |
| M-46 | 打包规则：发布物只含一套 App（`GmixLx.App` 或 `GmixLx.App.Wpf`） | 高 | 发布目录检查 |

### 阶段 5：验收与切换（M-5x）

| 编号 | 任务 | 优先级 | 验收方法 |
|---|---|---|---|
| M-50 | 截图基线比对（主界面、曲线、弹窗） | 高 | 差异可解释且可接受 |
| M-51 | 真机验收：映讯 GM-IX 单路 + OCS 批次 + 导出 | 高 | 全链路通过 |
| M-52 | CI 同时构建 WinForms 与 WPF，确认测试仍 PASS | 高 | CI 绿 |
| M-53 | WPF 达到对等后移除 WinForms App，更新解决方案与 CI | 高 | 仅保留一套 App |
| M-54 | 更新 ADR 实施记录、README、长期记忆 | 中 | 文档与代码一致 |
| M-55 | 关闭或重置遗留 TBD（TBD-LX-01~07） | 中 | ADR §7 状态更新 |

## 3 文件级映射（WinForms → WPF）

| 现有文件 | 行数 | WPF 目标 |
|---|---|---|
| `Program.cs` | 14 | `App.xaml` / `App.xaml.cs` |
| `MainForm.cs` | 626 | `MainWindow.xaml(.cs)` + `MainViewModel` |
| `DeviceDebugPanel.cs` | 689 | `DeviceDebugView.xaml(.cs)` + `DeviceDebugViewModel` |
| `CurveForm.cs` | 525 | `CurveWindow.xaml(.cs)` + `CurveViewModel` |
| `CurvePlotPanel.cs` | 511 | `CurvePlotControl`（`FrameworkElement` / `DrawingVisual`） |
| 嵌入 `guangmai.ico` | — | `Resource` + `Window.Icon`（`BitmapFrame`） |

## 4 验证命令

**所有命令在 `products/gmix-lx` 目录下执行**，确保子目录 `global.json`（10.0.404）生效：

```powershell
cd products/gmix-lx

dotnet build GmixLx.sln
dotnet run --project src/GmixLx.Tests --no-build

# 真机冒烟（需要设备与 OCS）
dotnet run --project src/GmixLx.Tests -- --live-yinxun2
dotnet run --project src/GmixLx.Tests -- --live-yinxun2-table
```

通过标准：构建 0 警告 0 错误；测试输出 `PASS gmix-lx vendor+orchestration` 且退出码 0；WPF 冒烟与真机验收按阶段 5 执行。

> 注意：从仓库根执行 `dotnet build products/gmix-lx/GmixLx.sln` 会命中根 `global.json`（SDK 8），无法构建 `net10.0`，不作为有效命令。

## 5 回退策略

- 阶段 1 失败：回退 TFM 与 `products/gmix-lx/global.json`，WinForms + .NET 8 仍可运行。
- 阶段 3 决策门未通过：停止 WPF 迁移，保留 WinForms 曲线方案；不进入阶段 4。
- 阶段 3 局部不达标：可保留 WinForms 曲线控件或重新评估图表库（需新 ADR）。
- 阶段 4/5 出现功能缺口：不切换发布产物，继续保留 WinForms 版。
- 任一阶段回退不得影响 `Core/Vendor/Orchestration` 与数据契约。

## 6 风险登记

| 编号 | 风险 | 等级 | 缓解 |
|---|---|---|---|
| R-LX-01 | 16 万点曲线在 WPF 下刷新不达标 | 中高 | 先做阶段 3 原型；按需 WriteableBitmap/降采样 |
| R-LX-02 | 字体度量差异导致布局错位 | 中 | 统一设计令牌；截图比对 |
| R-LX-03 | .NET 10 SDK/CI 未就绪 | 低 | 阶段 1 先解决 SDK 与 CI |
| R-LX-04 | 团队不熟悉 XAML/MVVM | 中 | 仅表单/表格用 MVVM，曲线控件保留 code-behind |
| R-LX-05 | 与 `products/detection` 共用 `Gmix.Core` 的 SDK 冲突 | 中 | 子目录 `global.json` + CI `working-directory`；禁止从仓库根构建 gmix-lx |
| R-LX-06 | SDK 10 新分析器在 `TreatWarningsAsErrors` 下导致构建失败 | 中 | 阶段 1 全量构建，逐项修复或按 ADR 记录抑制理由 |
| R-LX-07 | App 继续通过传递引用使用 Vendor 类型 | 中 | M-05/M-25 源码检查；WPF App 不引用 Vendor |

## 7 待决项

见 [ADR-001 §7](ADR-001_WPF与NET10.md#7-待决项tbd)：TBD-LX-01 ~ TBD-LX-07。
