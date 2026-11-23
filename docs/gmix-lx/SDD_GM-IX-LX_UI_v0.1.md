# GM-IX-LX 上位机 UI 设计说明书 SDD

> **文档编号**：SDD-LX-001
> **版本**：v0.1（骨架）
> **日期**：2026-11-23
> **状态**：草案（随 ADR-001 评审）
> **对应 ADR**：[ADR-001_WPF与NET10.md](ADR-001_WPF与NET10.md)
> **适用**：`products/gmix-lx/src/GmixLx.App`
> **上游依据**：[products/gmix-lx/README.md](../../products/gmix-lx/README.md)、[统一数据契约 v0.7](../contracts/统一数据契约_v0.7.md)、[GM-IX 数据与处理规范 v1.0](../contracts/GM-IX_数据与处理规范_v1.0.md)

## 1 范围与不变量

### 1.1 范围

本 SDD 描述 GM-IX-LX 上位机 **UI 层**的结构、数据流、线程模型、状态与错误呈现，以及 WinForms → WPF 的落地边界。

不在本 SDD 范围：

- 厂商 SDK 协议与 OCS 串口协议（分别见 `references/YinXun_2/` 与 `docs/detection/OCS_控制协议分析.md`）；
- 11 列指标公式、峰判定与参考标定数学（见 `GmixLx.Core` 与相关实测文档）；
- 数据契约字段语义（见 `docs/contracts/`）。

### 1.2 不变量

1. `GmixLx.App` 只引用 `GmixLx.Core + GmixLx.Orchestration`，**不得引用 `GmixLx.Vendor`**（含传递引用）。
2. 厂商类型、`DllImport`、原生句柄只允许出现在 `GmixLx.Vendor`。
3. 面向用户的曲线与指标必须处于产品参考系（叠加 `level_offset_db`），换算只在 Orchestration 发生一次。
4. 11 列指标表的列名、列序与计算口径不变。
5. UI 只保留必要操作与数据展示，不添加解释性/防御性说明文字。
6. 曲线只来自内存数据，不读文件、不落盘；CSV 导出是显式用户操作。

## 2 架构与依赖

```text
┌─────────────────────────────┐
│ GmixLx.App (WinForms → WPF) │  UI、布局、交互、状态展示
└──────────────┬──────────────┘
               │ 只依赖 Core + Orchestration
┌──────────────▼──────────────┐
│ GmixLx.Orchestration        │  台架装配、会话、指标表、组提交
│  YinXun2Station             │
│  YinXun2SingleSession       │
│  YinXun2CurveSource         │
│  MetricTableRunner          │
└──────────────┬──────────────┘
               │
┌──────────────▼──────────────┐
│ GmixLx.Vendor               │  厂商归口：SDK 绑定、OCS 串口
│  YinXun2NativeSdk/Client    │
│  Ocs1x8Client/Ocs8x8Client  │
└──────────────┬──────────────┘
               │
       ┌───────▼────────┐
       │ 映讯 GM-IX 设备 │
       │ OCS 1×8 / 8×8   │
       └────────────────┘
```

| 层 | 职责 | 禁止 |
|---|---|---|
| `GmixLx.App` | 布局、交互、状态、CSV 对话框 | 不碰 Vendor、不做设备 IO、不做指标重算 |
| `GmixLx.Orchestration` | 会话生命周期、台架装配、指标表编排、参考系翻译 | 不碰 UI、不解析协议字节 |
| `GmixLx.Vendor` | SDK 绑定、OCS 串口、协议常量 | 不依赖 UI/Core 之外的业务 |
| `GmixLx.Core` | 纯域模型、曲线/峰/指标、参考标定 | 不读文件、不碰 UI、不做 IO |

## 3 UI 结构

### 3.1 主窗口

| 区域 | 内容 | 当前实现 |
|---|---|---|
| 页签 1 | 距离域曲线（默认页） | `DeviceDebugPanel` |
| 页签 2 | 指标表 | `MainForm` 的连接卡片 + 判据卡片 + `DataGridView` |
| 状态栏 | 运行状态、完成/失败摘要 | `ToolStripStatusLabel` |

指标表页：

- 连接卡片：1×8 串口、8×8 串口、采样次数、开始/停止/导出按钮；
- 判据卡片：空间平均 m、时间平均 n、开始位置(m)、终止位置(m)、阈值余量(dB)；
- 表格：64 行 × 11 列，8 行一组交替底色，失败行灰化并显示 `—`。

### 3.2 距离域曲线页

| 区域 | 内容 |
|---|---|
| 顶部 | ROI IL / ROI RL 两张指标卡 |
| 中部 | 距离域曲线：ROI 底纹、峰标记、A/B 游标、悬停读数 |
| 左下 | 采集参数：空间平均、采集次数、ROI 起点/终点、显示上限 |
| 右下 | 峰表：序号、位置(m)、RL(dB)、状态（合并/旁瓣） |
| 按钮 | 单次采集、连续采集、保存数据 |

### 3.3 曲线弹窗

从指标表双击某行打开 `CurveForm`：显示该路径的距离域曲线、A/B 游标、峰标签、阈值线；只读内存数据。

## 4 数据流

### 4.1 距离域曲线页（单帧）

```text
用户点击「单次采集」
  → DeviceDebugPanel.TryReadRequest（m/n/ROI 校验）
  → YinXun2SingleSession.Open / SetRoi
  → YinXun2Client.AcquireReady
  → YinXun2NativeSdk（厂商 DLL）
  → YinXun2CurvePreparation.ApplyLevelOffset
  → CurveMetrics.FindPeaks（中位 + 余量）
  → YinXun2SingleFrame
  → DeviceDebugPanel.RenderFrame（曲线 + 峰表 + ROI 卡片）
```

连续采集：同一会话循环 `Acquire`，每帧后 `Task.Delay(50)`；停止通过 `CancellationTokenSource`。

### 4.2 指标表页（64 路批次）

```text
用户点击「开始测试」
  → MainForm.TryReadYinXun2（SDK 目录 + 参考标定）
  → MainForm.TryReadCriteria（A/D/余量）
  → ROI = calibration.ToYinXunDistance(A/D)
  → Task.Run:
       YinXun2Station.Create
         ├─ 打开 SDK 会话 + SetAnalysisRoi
         ├─ 打开 OCS 1×8 / 8×8
         └─ 构建 MetricTableRunner（原生判据 + YinXun2MetricsMapper）
       MetricTableRunner.Run(1..8, 1..8)
         ├─ 1×8.SetOutput / 8×8.SetMatrix
         ├─ Settle 50 ms
         ├─ YinXun2CurveSource.TryNextCurve
         │    ├─ AcquireReady
         │    ├─ MaskInternalSection
         │    └─ ApplyLevelOffset
         ├─ CurveMetrics.Compute（原生轴）
         └─ YinXun2MetricsMapper.ToReport（参考系）
  → 每行 BeginInvoke → MainForm.ApplyRow
```

- N>1：同一路径重复采样后取平均（`MetricTableRunner.Accumulate`）。
- 测量类错误只废掉当前行；设备/传输/协议类错误中止整批。
- 完成后可「导出 CSV」：`MetricTableCsv.Write`。

### 4.3 参考标定与参考系

- 加载 `products/gmix-lx/config/yinxun2_reference_calibration.json` → `YinXun2ReferenceCalibration`；
- 原生轴 → 参考系的换算在 `YinXun2CurveSource.AxisScaleM/AxisOffsetM` 与 `YinXun2MetricsMapper` 完成；
- 电平显示偏移 `LevelOffsetDb` 在 Orchestration 出口叠加一次，UI 只格式化。

### 4.4 尚未接入 UI 的编排能力

`BatchRunner`、`PathExecutor`、`GroupPipeline`、`DetectionStore`、`RecoveryService` 是从检测平台继承的批次/组提交能力，**当前 UI 未使用**（指标表页直接走 `MetricTableRunner`）。是否接入由 TBD-LX-09 决定，接入前不得假定 `detection.json` 会由 UI 生成。

## 5 线程模型

| 线程 | 职责 | 纪律 |
|---|---|---|
| UI 线程 | 控件、布局、状态、对话框 | 不做设备/串口/重 IO |
| 批次后台任务 | `YinXun2Station.Create` + `MetricTableRunner.Run` | 通过 `CancellationTokenSource` 取消；行结果 `BeginInvoke` 回 UI |
| 调试后台任务 | 单次/连续 `Acquire` | 同一时刻只允许一个调试任务 |
| 串口命令 | OCS 1×8 / 8×8 | 同一串口命令串行化；批量路径按 IN/OUT 顺序执行 |

约束：

1. **UI 线程零设备 IO**：SDK、串口、文件导出都在后台或由用户对话框触发。
2. **取消**：批次与连续采集都持有 `CancellationTokenSource`；停止后置空并释放。
3. **资源释放**：`YinXun2Station` / `YinXun2SingleSession` 使用 `using` 或 `Dispose`，会话与 OCS 成对释放。
4. **跨页互斥（当前缺口）**：指标表批次与距离域曲线页可同时操作设备，当前没有统一互斥。WPF 版应引入单一会话协调器，或在一页运行时禁用另一页入口（TBD-LX-08）。
5. **UI 更新节流**：批次逐行更新 64 行；曲线重绘由 16 ms 计时器合并，避免事件排队。

## 6 状态与错误

| 场景 | 表现 |
|---|---|
| 参数非法 | 状态栏红字 + 拒绝开批，不静默兜底 |
| 单行测量失败 | 该行显示 `—`、灰化，ToolTip 给出原因；不中止整批 |
| 设备/传输/协议错误 | 中止整批，状态栏红字显示原因 |
| 连续采集异常 | 停止循环并提示 |
| CSV 导出 | 保存对话框；成功后状态栏提示路径 |

错误分类沿用 `DetectionException` / `ErrorClass`；禁止 `catch {}` 静默吞掉设备错误。

## 7 性能与交互约束

- 单帧 160,000 点；曲线绘制使用 min/max 分块金字塔，每像素聚合一个块。
- 缩放/平移/游标重绘用 16 ms 合并；不阻塞 UI 线程。
- 指标表 64 行，逐行更新，不整表重建。
- WPF 目标指标见 [ADR-001 §5.4](ADR-001_WPF与NET10.md#5-后果与约束)：首帧 ≤ 基线×1.2、交互 p95 ≤ 33 ms、连续 10 帧无内存增长。

## 8 WinForms → WPF 映射

| 当前 | WPF 目标 |
|---|---|
| `MainForm` | `MainWindow.xaml` + `MainViewModel` |
| `DeviceDebugPanel` | `DeviceDebugView.xaml` + `DeviceDebugViewModel` |
| `CurveForm` | `CurveWindow.xaml`（M-41 已落地，复用 `CurvePlotControl`） |
| `CurvePlotPanel` | `CurvePlotControl`（`FrameworkElement` / `DrawingVisual`） |
| `DataGridView` | `DataGrid` + RowStyle/Trigger |
| `BeginInvoke` | `Dispatcher.InvokeAsync` |
| 嵌入 `.ico` | `Resource` + `Window.Icon` |

详细任务与验收见 [迁移清单](迁移清单_WPF_NET10.md)。

设计令牌统一收敛在 `GmixLx.App.Wpf/Themes/Tokens.xaml`（颜色、字体、间距、圆角、表格行高），WPF 界面禁止散落硬编码颜色与字号。配色自 2026-11-23 起改为参考 VIAVI PCTMax 的深色仪表风格（炭黑底 + 中蓝强调 + 绿色主动作），令牌与截图见 [UI 基线 §15](基线_UI_2026-11-23.md)。

WPF 主窗口壳由 `MainWindow` + `Views/DeviceDebugView` + `Views/MetricTableView` 组成，状态栏文本经 `MainViewModel.StatusText` 绑定；两个页面壳在 M-23/M-40 中填充具体内容。WPF App 分层检查（M-25）已确认源码与程序集均不直接引用 `GmixLx.Vendor`。

曲线渲染的 min/max 分块聚合已抽离为 `Visualization/MinMaxPyramid.cs`（纯计算，不依赖 UI），WPF 曲线控件 `Visualization/CurvePlotControl.cs` 复用该算法绘制 16 万点曲线；当前已支持滚轮缩放、左键拖动平移、A/B 游标拖动、悬停读数、峰标记/峰标签、ROI 底纹与阈值线。

距离域曲线页（M-40）由 `Views/DeviceDebugView.xaml` + `ViewModels/DeviceDebugViewModel.cs` 构成，结构与 WinForms `DeviceDebugPanel` 一一对应：

- 顶部两张 ROI IL/RL 卡片；中部 `CurvePlotControl`；底部左侧参数（空间平均、采集次数、ROI 起止、显示上限）与三个按钮，右侧峰事件表（序号/位置/RL/状态）。
- ViewModel 只依赖 `GmixLx.Orchestration`（`YinXun2SingleSession`、`YinXun2Station`）与 `GmixLx.Core`（`PeakEventFilter`），不接触 Vendor；视图负责 SDK/标定路径解析（按 exe 目录向上查找仓库根）、`SaveFileDialog` 与错误弹窗。
- 采集在后台线程执行（`Task.Run`），结果回到 UI 线程渲染；单次与连续互斥，A/B 拖动结束会先停止连续采集再按新 ROI 重采；窗口关闭时释放会话与取消令牌。
- 峰后处理、事件表过滤（0.05 m 前伪峰不进表）、CSV 列序（`distance_m,reflection_db`）与 WinForms 版一致。

指标表页（M-43）补齐连接卡（1×8/8×8 串口、采样次数）与判据卡（空间/时间平均、A/D、阈值余量），由 `MetricTableViewModel.Start()` 校验参数并驱动 `YinXun2Station` + `MetricTableRunner`；逐行结果经 `SynchronizationContext` 回投 UI。状态栏文案与颜色由 `MainViewModel.StatusText` + `StatusTone` 驱动（DataTrigger 映射 Token 画刷），文案与 WinForms `MainForm` 逐条对齐。仓库内相对路径（SDK、参考标定）统一由 `AppPaths` 解析，设备页与指标表共用。

应用启动（M-44）：`App.OnStartup` 先抢命名 Mutex 保证单实例（第二份实例置前已有窗口后退出），再预热曲线控件并创建 `MainWindow`；窗口图标用 pack URI，exe 图标由 `ApplicationIcon` 嵌入。

## 9 验收要点

1. 主窗口两页签可切换，状态栏正常。
2. 指标表 64×11 与 WinForms 版一致；双击可打开曲线。
3. 曲线支持缩放/平移/A-B 游标/峰标记，16 万点交互满足 ADR 性能指标。
4. 单次/连续采集、ROI 修改、保存 CSV 正常。
5. 真机：映讯 GM-IX 单路 + OCS 批次 + 导出全链路通过。
6. App 无 Vendor 引用；构建 0 警告 0 错误；测试 PASS。

## 10 待决项

| 编号 | 内容 | 归属 |
|---|---|---|
| TBD-LX-08 | 跨页设备互斥：会话协调器或入口禁用策略 | UI/编排 |
| TBD-LX-09 | 是否把 `BatchRunner` / `GroupPipeline` / `detection.json` 接入 UI | 产品 |
| TBD-LX-10 | UI 层自动化测试范围（当前仅控制台测试，不覆盖 App） | 测试 |
| TBD-LX-11 | 是否保持纯中文界面（当前是） | 产品 |

## 11 追溯

| 项 | 内容 |
|---|---|
| ADR | [ADR-001_WPF与NET10.md](ADR-001_WPF与NET10.md) |
| 迁移清单 | [迁移清单_WPF_NET10.md](迁移清单_WPF_NET10.md) |
| 规则 | `.trae/rules/10-architecture.md`、`20-change-workflow.md` |
| 代码 | `GmixLx.App` / `GmixLx.Orchestration` / `GmixLx.Vendor` |
| 契约 | [统一数据契约 v0.7](../contracts/统一数据契约_v0.7.md)、[GM-IX 数据与处理规范 v1.0](../contracts/GM-IX_数据与处理规范_v1.0.md) |
