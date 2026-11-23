# GM-IX-LX 专用上位机

> **产品代号**：GM-IX-LX
> **定位**：GM-IX OFDR 专用上位机，当前接入 **映讯 GM-IX（YinXun_2）**：UDP `192.168.1.199:10086` + 厂商 `ocs_measurement.dll`
> **来源**：2026-11-23 从 `products/detection`（检测平台）独立 fork，缩减接入范围
> **产品主线**：`products/detection` 仍是检测系统主线；本目录是其「仅 GM-IX OFDR」的专用裁剪版

## 0. 文档入口

| 内容 | 位置 |
|---|---|
| UI 设计（草案） | [SDD：GM-IX-LX UI v0.1](../../docs/gmix-lx/SDD_GM-IX-LX_UI_v0.1.md) |
| UI 技术选型（草案） | [ADR-001：WPF + .NET 10](../../docs/gmix-lx/ADR-001_WPF与NET10.md) |
| 迁移清单（草案） | [WPF + .NET 10 迁移清单](../../docs/gmix-lx/迁移清单_WPF_NET10.md) |

## 1. 接入范围

| 能力 | 状态 |
|---|---|
| 映讯 GM-IX OFDR（SDK 采集、距离域曲线） | 保留 |
| OCS 1×8 + 8×8 光开关与 64 路批次 | 保留 |
| 单次测量、11 列指标表、曲线弹窗、CSV 导出 | 保留 |
| 多次平均 / 精度统计（N>1） | 保留 |
| 设备调试页（原始曲线、ROI、采集参数） | 保留 |
| 参考标定（`yinxun2_reference_calibration.json`） | 保留 |
| 昊衡 LGA50-D 设备接入（TCP 4300 / `OfdrClient`） | **移除** |
| 跳线维护、指纹采集与比对 | **移除** |

判据参考系仍沿用历史 GM-IX/LGA50 锚点（`4.440 m` / `10.601 m` / `0.000185 m/行`），仅作参考系坐标，不代表接入 LGA50 设备。

## 2. 工程结构

```text
products/gmix-lx/
├─ GmixLx.sln
├─ config/
│  └─ yinxun2_reference_calibration.json   # 参考标定（从检测平台复制）
└─ src/
   ├─ GmixLx.Core/           # 纯域模型、曲线/峰/指标计算、参考标定
   ├─ GmixLx.Vendor/         # 厂商归口：YinXun_2 SDK + OCS 串口协议
   ├─ GmixLx.Orchestration/  # 台架装配、指标表、组提交、批次状态机
   ├─ GmixLx.App/            # WinForms UI（当前发布形态，迁移回退基线）
   ├─ GmixLx.App.Wpf/        # WPF 目标 UI（阶段 2 骨架）
   │  ├─ Views/              # 页面壳（DeviceDebugView / MetricTableView）
   │  ├─ ViewModels/         # MainViewModel 等
   │  ├─ Visualization/      # MinMaxPyramid + CurvePlotControl 曲线渲染
   │  └─ Themes/Tokens.xaml  # 设计令牌（颜色/字体/间距）
   └─ GmixLx.Tests/          # 契约回放、编排与设备适配测试
```

依赖方向与检测平台一致：`Core → BCL`；`Vendor → Core`；`Orchestration → Core + Vendor + Gmix.Core`；`App` / `App.Wpf → Core + Orchestration`。厂商类型只允许出现在 `GmixLx.Vendor`。

## 3. 构建与验证

需要 .NET 10 SDK（`products/gmix-lx/global.json` 固定 `10.0.404`）。命令必须在 `products/gmix-lx` 目录下执行：

```powershell
cd products/gmix-lx
dotnet build GmixLx.sln
dotnet run --project src/GmixLx.Tests --no-build
```

判定标准：构建 0 警告 0 错误；测试输出 `PASS gmix-lx vendor+orchestration`，退出码 0。

真机联调入口：

```powershell
cd products/gmix-lx
# 映讯 GM-IX 单路冒烟
dotnet run --project src/GmixLx.Tests -- --live-yinxun2
# 映讯 GM-IX 64 路指标表
dotnet run --project src/GmixLx.Tests -- --live-yinxun2-table
```

## 4. 口径约束

- 批次记录文件名固定 `detection.json`，字段语义与检测平台一致；`deviceInfo.ofdrPlatform` 取 `YinXun2`。
- 11 列指标表的列名、列序、计算口径不变；`RL` 为设备内部参考，不是绝对回损。
- 曲线与指标必须处于产品参考系（叠加 `level_offset_db`），换算只在 `YinXun2SingleSession` 出口发生一次。
- 配置是不可变快照；参考标定变化需同步 `config/yinxun2_reference_calibration.json`。
- 不引入新的第三方依赖；`System.IO.Ports` 仍是唯一 NuGet 依赖（OCS 串口）。
