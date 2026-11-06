# GM-IX 上位机软件设计说明书（SDD）

| 项 | 内容 |
|---|---|
| 版本 | v0.1（P0 设计基线草案） |
| 状态 | 可评审；标 `TBD` 处待实机或决策 |
| 对应需求 | `GM-IX_上位机软件需求规格_SRS_v0.1.md`（无遗留待评审项） |
| 对应契约 | `GM-IX_数据与处理规范_v1.0.md`（ICD v1.0，唯一数据/行为契约） |
| 实施状态 | `Gmix.Data` 数据层已落地（.NET Framework 4.0 直编，schemaVersion 0.2，回归 PASS 14/FAIL 0）；厂商 P/Invoke 声明已存在（`Qt2144Probe.cs`）；工程化、采集进程、UI 未开始 |
| 设计范围 | 仅 P0：测量主页、事件表与结果、校准向导、历史检索与导出（4 页）+ 数据层 + 采集/处理编排 |

> **文档定位**：本文件是软件设计说明书（SDD），回答"怎么实现"。需求与验收以 SRS v0.1 为准，字段/公式/错误契约以 ICD v1.0 为准，本文只引用编号不重复定义。§0.2 的决策记录是设计前提，变更须走 §0.3 纪律。

> **文档规则**：设计条目编号 `D-<域>-NN`；引用格式 `SRS FR-xxx` / `ICD §x.x`；标 `待回填` 的条目表示设计产生了新契约，须先回填 ICD 再实现。

---

## 0. 参考材料与设计决策

### 0.1 参考资料

| 资料 | 内容 | 在本设计中的用途 |
|---|---|---|
| ICD v1.0 | 数据契约 + 处理生命周期契约 | 字段、公式、目录、错误分类的唯一来源 |
| SRS v0.1 | 需求与验收 | 功能条目、显示口径 DS-01~08、NFR |
| `references/qt2144-rs/qt_acq.h` | 采集库 C 接口（9 个函数 + 8 个错误码） | §6.1 P/Invoke 与 §6.2 错误映射 |
| `references/ofdr-0.1.1-release/ofdr.h` | 处理库 C 接口（3 个函数） | §6.1、§6.3 内存所有权 |
| `references/detect_1.0/detect.h` | 检测库 C 接口（4 个函数 + `DetectConfig` 8 参数） | §6.1、§6.4 NaN→null |
| `references/qt2144-rs/config/` | `Interface.json` 与板卡配置 | §9.1 配置快照的实际来源 |
| `references/HRAS9000/ui_*.png` | 厂商界面截图 | §8 页面形态参照（布局自主设计，不复刻） |
| `GM-IX/src/Gmix.Data/*` | 现有 6 个模块实现 | §5.1 复用边界、§12.4 迁移 |
| `GM-IX/src/Qt2144Probe.cs` | 现有 P/Invoke 声明与探针 | §5.2、§6.1 抽取来源 |
| `docs/SDD_OFDR_Platform_v0.9.md` | 一期 SDD | 章节结构与 ADR 体例参照（不沿用其 7 程序集划分） |

### 0.2 设计决策记录（ADR）

| ID | 决策 | 备选 | 理由 |
|---|---|---|---|
| D-ARC-01 | **.NET 8 + WinForms** | .NET 8+WPF；.NET Framework 4.8 | 与团队运行时统一；一期距离域曲线/事件表/双光标经验可直接复用；`Gmix.Data` 需工程化并换 JSON 实现（§12.4） |
| D-ARC-02 | **零 NuGet**，曲线自绘 | 引 ScottPlot/OxyPlot | 延续一期"发行包最小、无第三方运行时"；ICD 已定义 min/max 包络抽稀，自绘工作量可控；引库需先立 ADR |
| D-ARC-03 | **独立采集 exe + 文件交接** | 单进程双线程；先单进程后拆 | ICD §0.2 已定"采集进程独立运行、可强杀""编排进程是唯一写者"；独立进程使 UI 崩溃不污染采集，强杀语义可验证（§3.2） |
| D-ARC-04 | **SDD 仅覆盖 P0** | P0+P1 | P1（配置编辑、高级设置）需求未细化，提前设计属过度设计；P1 只在 §13 一句话延期 |
| D-ARC-05 | **4 程序集**，不照搬一期 7 个 | 复用一期分层 | GM-IX 为单机单 DUT，`Engine`/`Replay` 等层在 P0 无对应物；抽象按真实需求出现再加 |

### 0.3 设计变更纪律

- 触碰 ICD §0.3 不变量 → 先改 ICD 并升版本，再改本文与代码；
- 新增功能 → 先在 SRS 立 `FR-` 条目，再在本文落 `D-` 条目；
- 本文标 `待回填` 的条目，**在实现前必须完成 ICD 回填与评审**，不得先写代码后补契约。

---

## 1. 设计目标与约束

### 1.1 目标

1. **单一数据契约**：所有模块只认 ICD 的结构与字段，不各自另立口径。
2. **厂商隔离**：`DllImport` 与厂商类型只出现在 `Gmix.Vendor`，UI 与编排不出现其符号（对应 `SRS FR-ACQ-06`）。
3. **可离线运行**：合成数据源与真实采集同接口，无设备也能跑通 4 页流程（对应 `SRS AS-03`）。
4. **纯函数指标**：`MetricsAggregator` 不读文件、不碰 UI（对应 `ICD §0.3 #9`）。
5. **显示单点**：`rlOffsetK` 的加性偏移只在 UI 呈现层一处应用（对应 `SRS DS-08`）。

### 1.2 约束

| 约束 | 内容 | 来源 |
|---|---|---|
| 平台 | Windows 10/11 x64、.NET 8（`net8.0` / `net8.0-windows`） | D-ARC-01 |
| 依赖 | 不引入 NuGet 包；仅用 BCL 与 WinForms | D-ARC-02 |
| 设备 | 固定 IP `192.168.5.20`，软件触发，采样率 50 MHz / 8 ms | SRS IF-HW-01、ICD §3.1 |
| 运行时 | 不需要 MATLAB Runtime（`ofdr.dll`/`detect.dll` 均为纯 C 接口，四文件平铺） | ICD、`README_ext.md` |
| 厂商内存 | `OfdrResult`/`DetectResult` 使用后必须 `*_result_free`，异常路径同样释放 | ICD §0.2 角色义务 |
| 单写者 | 同一 `GMIX_DATA` 只允许一个编排进程提交 | ICD Part B §7 |

### 1.3 质量属性优先级

可测试性 ≥ 可追溯性 > 性能 > 可移植性。理由：精度口径尚在 TBD（TBD-S5/S6），先保证能回放、能对比、能复算。

---

## 2. 架构与工程结构

### 2.1 分层

```
┌────────────────────── Gmix.App (WinForms, 组合根) ──────────────────────┐
│ 4 页 UI · 编排服务 · 组提交（唯一写者）· 历史/导出 · 显示变换（单点）        │
└──────────────▲────────────────────────────────▲───────────────────────┘
               │ 只读模型 / DTO（不引用 Vendor）    │ 启动与轮询（进程边界）
┌──────────────┴──────────────┐        ┌─────────┴──────────────────────┐
│       Gmix.Core             │        │       Gmix.Acquirer (exe)       │
│ 模型·校验·指标·校准·存储·哈希 │        │ 采集会话·转换·写 staging·就绪标记 │
└──────────────▲──────────────┘        └─────────▲──────────────────────┘
               │                                 │
        ┌──────┴─────────────────────────────────┴──────┐
        │            Gmix.Vendor（唯一 P/Invoke 处）      │
        │ qt_acq.dll · ofdr.dll · detect.dll            │
        └───────────────────────────────────────────────┘
```

**依赖方向（不可逆）**：

| 程序集 | 允许依赖 | 禁止 |
|---|---|---|
| `Gmix.Core` | BCL only | Vendor、WinForms |
| `Gmix.Vendor` | Core、BCL | WinForms |
| `Gmix.Acquirer` | Vendor、Core | WinForms |
| `Gmix.App` | Core、WinForms | Vendor（不得 `DllImport`） |

> `Gmix.App` 不引用 `Gmix.Vendor` 是硬约束：UI 进程内不得加载厂商 DLL，避免崩溃连带。编排通过 Acquirer 文件交接获取数据（§3.2）。

### 2.2 程序集与职责

| 程序集 | 输出 | 职责 |
|---|---|---|
| `Gmix.Core` | `Gmix.Core.dll` | `SchemaModels`（37 个模型类，已存）、`SchemaValidator`、`MetricsAggregator`、`CalibrationModel`、`JsonAndFileStore`、`AcquisitionDefaults`、`GroupStore`（新增）、`ConfigSnapshot`（新增）、`DisplayTransform`（新增） |
| `Gmix.Vendor` | `Gmix.Vendor.dll` | `QtAcquisition`（open/configure/start/read/stop/close）、`OfdrProcessor`、`Detector`、`NativeApi`（P/Invoke 声明）、`VendorError`（错误码映射） |
| `Gmix.Acquirer` | `Gmix.Acquirer.exe` | 命令行驱动：建会话 → 循环 N 次采集 → 写 `_staging/<groupId>/Rxxx/` → 写 `.ready` |
| `Gmix.App` | `Gmix.App.exe` | 4 页 UI、编排服务、组提交、历史检索、导出 |

### 2.3 解决方案布局

```text
GM-IX/
├─ src/
│  ├─ Gmix.sln
│  ├─ Gmix.Core/            Gmix.Core.csproj
│  ├─ Gmix.Vendor/          Gmix.Vendor.csproj
│  ├─ Gmix.Acquirer/        Gmix.Acquirer.csproj
│  ├─ Gmix.App/             Gmix.App.csproj
│  └─ Gmix.Tests/           Gmix.Tests.csproj（控制台断言，非 xunit）
├─ tests/fixtures/
└─ docs/
```

> 测试保持"控制台断言 + 退出码"形态，不引入 xunit（NuGet）。`dotnet run --project GM-IX/src/Gmix.Tests` 即回归入口，沿用现有 14 条断言。

### 2.4 目录与命名空间

| 命名空间 | 内容 |
|---|---|
| `Gmix.Core.Schema` | `SchemaModels` 全部模型 |
| `Gmix.Core.Validation` | `SchemaValidator` |
| `Gmix.Core.Metrics` | `MetricsAggregator`、`CalibrationModel` |
| `Gmix.Core.Storage` | `JsonAndFileStore`、`GroupStore` |
| `Gmix.Core.Display` | `DisplayTransform`、`EnvelopeSampler` |
| `Gmix.Vendor` | `NativeApi`、`QtAcquisition`、`OfdrProcessor`、`Detector` |
| `Gmix.Acquirer` | `Program`、`ScanLoop` |
| `Gmix.App` | `MainForm`、`Pages/*`、`Orchestration/*` |

---

## 3. 进程与线程模型

### 3.1 进程划分

| 进程 | 数量 | 职责 | 强杀后果 |
|---|---|---|---|
| `Gmix.App.exe` | 1 | UI、编排、组提交、历史/导出 | 下次启动清理 staging；不产生半成品组 |
| `Gmix.Acquirer.exe` | 每批扫描 1 个（常驻至批结束） | 设备会话、N 次采集、写 staging | 残留留在 `_staging/<groupId>/`，无 `.ready`，编排判失败 |

**为什么 Acquirer 常驻而非每次一进程**：设备单卡、`qt_acq_open` 有耗时（实测 `openMs` 记录在 `timing`），N 次重复复用同一会话可避免重复开关。批次结束由 App 下达停止或进程自然退出。

### 3.2 交接协议（ICD Part B §7.1）

ICD 原只规定"采集进程只写 `_staging/<groupId>/`、不执行提交"，未定义"一次扫描写完"的判据。该判据已于 2026-11-05 回填 **ICD Part B §7.1**（TBD-D09 关闭），本节只列实现约束，字段定义以 ICD 为准。

**标记文件**：`_staging/<groupId>/Rxxx/.ready`（结构见 ICD Part B §7.1，下面为示例）

```json
{
  "schemaVersion": "1.0",
  "scanId": "SC-20261105-153022-001",
  "repeatIndex": 1,
  "folder": "R001",
  "files": [
    { "path": "scan.json", "bytes": 1428, "sha256": "..." },
    { "path": "raw.bin", "bytes": 3200000, "sha256": "..." }
  ],
  "acquirerVersion": "1.0.0",
  "finishedAtUtc": "2026-11-05T07:30:22Z"
}
```

**写入顺序（Acquirer）**：

1. 写 `scan.json`、`raw.bin`（如需）、`ofdr.json`、`dist/freq/ps/baseline.f64`、`events.json`、`summary.json`、`calibration.json`；
2. 逐个计算 `sha256` 与字节数；
3. 写 `.ready.tmp` → `File.Move` 为 `.ready`（同卷原子，对应 `SRS FR-STO-02` 的"对观察者原子"）；
4. **不写** `group.json`。

**读取与校验（App）**：

1. `PeriodicTimer` 1 s 轮询 `_staging/*/R*/.ready`（不用 `FileSystemWatcher`，避免事件丢失与缓冲溢出）；
2. 校验：`files` 清单齐全、`bytes` 与磁盘一致、`scan.json` 可解析、`Lout == N/2+1`、`dist` 严格递增（ICD Part B §4）；
3. 全部 `Rxxx` 就绪且组内 `configHash`/`calibrationId` 一致 → 组提交（§7.3）；
4. 单次扫描超时（默认 30 s，可配）未见 `.ready` → 该 scan 判 `rejected`，组内继续；
5. 超时且 `validCount == 0` → 组标记 `failed`，目录加 `.failed`（ICD Part B §4）。

### 3.3 生命周期与强杀

- App 以子进程方式启动 Acquirer，并用 **Job Object**（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）绑定：App 异常退出时子进程随之终止，避免孤儿进程占用设备。
- 正常停止：App 置停止标志 → Acquirer 完成当前扫描后写 `.ready` 并退出；App 等待退出最多 5 s，超时才强杀。
- 强杀留下的 `_staging` 无 `.ready`，由下次启动清理（§7.4）。

### 3.4 线程与背压

| 线程 | 职责 | 说明 |
|---|---|---|
| UI 线程 | 控件绑定、绘制 | **禁止**任何 P/Invoke、文件重读、大数组拷贝 |
| 编排线程（`Task`） | 轮询 `.ready`、校验、提交 | 与 UI 只通过不可变 DTO 交接 |
| Acquirer 主线程 | `qt_acq` 调用 | 设备会话独占 |

- 结果入队使用**有界队列**（容量 2），满时丢弃最旧并计数（`SRS NFR-PERF-02` 要求不阻塞 UI）；丢弃计数写入 `session.log`。
- 循环模式：编排线程按采集节拍串行消费，不做并行处理（`ps` 平均必须串行，`ICD §2.2`）。

---

## 4. 数据流水线

### 4.1 阶段流转

与 `ICD Part B §1` 一致，本文只标注实现落点：

| 阶段 | 实现位置 | 输入 → 输出 |
|---|---|---|
| 采集 | `QtAcquisition`（Vendor） | 设备 → `raw`（int16 小端、4 通道交织、8 字节/帧） |
| 转换 | `Conversion`（Acquirer） | `raw` → `PsaArrays`（三路 `double[N]`） |
| 处理 | `OfdrProcessor`（Vendor） | `PsaArrays` + `fs`/`distScale` → `dist/freq/ps`（`N/2+1`） |
| 检测 | `Detector`（Vendor） | `dist/ps` → `EventMetrics`（`ps_db` 只在出口 `20log10`） |
| 聚合 | `MetricsAggregator`（Core） | `EventMetrics` + baseline + 区间 → `Summary` |
| 落盘 | `JsonAndFileStore` + `GroupStore`（Core） | `ScanBundle` → `Rxxx/`、`AVG/`、`group.json` |

### 4.2 每阶段校验（对齐 ICD Part B §4）

| 阶段 | 校验 | 失败处理 |
|---|---|---|
| 采集 | `out_len == bufferBytes`、`out_len % 8 == 0` | 拒绝，标 `shortRead` / `frameAligned=false`，不进入转换 |
| 转换 | P/S/A 长度一致 | 抛错不截断、不补齐 |
| 处理 | `outputLength == N/2+1`、`dist` 严格递增 | 拒绝，标结果损坏 |
| 检测 | `counts` 与数组长度一致 | 拒绝 |
| 聚合 | 区间内无峰/无基线 → `null` | 不填 0、不填 `NaN` |

### 4.3 单一换算点

| 换算 | 唯一位置 | 禁止 |
|---|---|---|
| int16 → `double` | `Conversion`（无 offset/gain/factor） | 其它层再算 |
| 线性 `ps` → dB | `Detector` 出口（`20log10`） | 聚合层重复 |
| 显示偏移 `rlOffsetK` | `Gmix.Core.Display.DisplayTransform`（仅 App 使用） | 存储层、Acquirer |

---

## 5. 模块设计

### 5.1 `Gmix.Core`

| 模块 | 现状 | P0 设计 |
|---|---|---|
| `SchemaModels` | 已实现（37 个模型类，含 `ConfigSnapshotInfo`、`ScanDocument`、`SummaryDocument`、`CalibrationDocument`、`ViewDocument`） | 保留结构；`ConfigSnapshotInfo` 的 4 个 `object` 字段改为 `JsonNode`（§12.4） |
| `SchemaValidator` | 已实现 | 增加 `.ready` 清单校验入口 |
| `MetricsAggregator` | 已实现（区间过滤、guard 窗口、段噪声顶退化、两种 RL 模式、精度） | 不改公式，仅增加 `groupId` 透传 |
| `CalibrationModel` | 已实现（`distScale`、`rlOffsetK`、`ilSlope`/`ilIntercept`、`configHash` 校验；`ApplyIlStep` 只乘斜率、不加截距，与 ICD §5.1 裁定一致） | 增加 `psdRef` 字段读取（缺失=未启用） |
| `JsonAndFileStore` | 已实现（`JavaScriptSerializer`） | 换成 `System.Text.Json`（§12.4） |
| `AcquisitionDefaults` | 已实现（50 MHz / 8 ms / 1 ms 延时 / 窗口 1–9 ms） | 保留 |
| `GroupStore` | **新增** | 组目录创建、`group.json` 写入、原子提交、`.failed` 标记、启动清理 |
| `ConfigSnapshot` | **新增** | 四类配置加载 + 规范化 + `configHash`（§9） |
| `DisplayTransform` | **新增** | `rlOffsetK` 加性偏移、DS-04 IL 取负、`null` → "—"（§8.4） |
| `EnvelopeSampler` | **新增** | 按像素列 min/max 包络抽稀（§8.3） |

### 5.2 `Gmix.Vendor`

| 模块 | 职责 | 关键约束 |
|---|---|---|
| `NativeApi` | 集中全部 `DllImport`（从 `Qt2144Probe.cs` 抽取） | 唯一允许出现厂商符号的文件族 |
| `QtAcquisition` | `open/configure/start/read/stop/close` 封装 + `calc_buffer_bytes` | 暴露托管 `byte[]`/`short[]`，不暴露句柄 |
| `OfdrProcessor` | `ofdr_process` 封装 | 复制 `dist/freq/ps` 后立即 `ofdr_result_free` |
| `Detector` | `detect_process` 封装 + `DetectConfig` 8 参数 | `NAN` → `null`；复制后 `detect_result_free` |
| `VendorError` | 错误码 → 五分类可读消息 | 禁止 `catch {}`（`ICD Part B §4`） |

### 5.3 `Gmix.Acquirer`

```text
Gmix.Acquirer.exe --groupId GR-20261105-1530-001 --repeat 5 --root D:\GMIX_DATA [--synthetic]
```

1. 解析参数 → 校验 `groupId` 格式（`GR-YYYYMMDD-HHMM-NNN`）；
2. `ConfigSnapshot` 加载并计算 `configHash`（与 App 启动时同一输入，确保组内一致）；
3. `qt_acq_open` → `configure(50, 8, 1, 0, 1)` → `calc_buffer_bytes`；
4. 循环 `repeat` 次：`start` → `read` → 校验长度 → 转换 → `ofdr_process` → `detect_process` → 聚合 → 写 `_staging/<groupId>/Rxxx/` → 写 `.ready`；
5. 每次写 `timing`（`openMs/readMs/totalMs`）到 `scan.json`；
6. `stop` → `close`；
7. 退出码：`0` 全部成功；`2` 部分失败（`rejected` 已记录）；`3` 会话失败。

> `--synthetic` 走合成数据源，用于无设备开发与回归（不进发行包默认路径）。

### 5.4 `Gmix.App`

| 模块 | 职责 |
|---|---|
| `MainForm` | 4 页承载（`TabControl`）、状态栏、启动自检（`SRS FR-ACQ-09`） |
| `Pages/MeasurementPage` | 参数条 + 曲线 + 指标表 + 事件表 + 日志 |
| `Pages/ResultsPage` | 完整事件表 + 指标 + 单组导出 |
| `Pages/CalibrationPage` | 校准参数录入、校验、版本化保存、重算 |
| `Pages/HistoryPage` | 组列表、回读、曲线/指标/事件重现、导出 |
| `Orchestration/ScanJob` | 启动 Acquirer、轮询 `.ready`、校验、组提交、超时与失败处理 |
| `Orchestration/GroupCommitter` | 唯一写者：`_staging` → 最终目录原子提交 |
| `Orchestration/HistoryIndex` | 只存索引与路径，不存波形（`ICD §2.3`） |

### 5.5 模块红线

| 模块 | 不允许 |
|---|---|
| `Gmix.App` | `DllImport`、直接读 `.f64` 原始数组以外的重算、修改原始数据 |
| `Gmix.Core` | 引用 `System.Windows.Forms`、读文件（`MetricsAggregator` 除外其纯输入） |
| `Gmix.Vendor` | 依赖 WinForms、把句柄/指针向上暴露 |
| `Gmix.Acquirer` | 执行最终提交、写 `group.json` |

---

## 6. 厂商边界

### 6.1 P/Invoke 清单

| 库 | 函数 | 签名要点 |
|---|---|---|
| `qt_acq.dll` | `qt_acq_open(uint cardIndex, out IntPtr handle)` | `Cdecl`；`cardIndex` 来自 `Interface.json` 的 `CardIndx` |
| | `qt_acq_configure(IntPtr, uint rateMhz, uint acqTimeMs, uint segCount, int trigger, uint delayMs)` | 触发 0=软件、1=外上升、2=外下降 |
| | `qt_acq_start` / `qt_acq_stop` / `qt_acq_close` | 幂等性由封装层保证 |
| | `qt_acq_read(IntPtr, byte[] buffer, uint capacity, out uint outLen)` | 返回实际字节数 |
| | `qt_acq_calc_buffer_bytes(uint rateMhz, uint timeMs, uint segCount, out uint bytes)` | 容量预检 |
| | `qt_acq_last_error(StringBuilder, UIntPtr cap)` / `qt_acq_version(StringBuilder, UIntPtr len)` | ANSI 字符集 |
| `ofdr.dll` | `ofdr_config_default()` → `{double fs, double dist_scale}` | 值传递结构体 |
| | `ofdr_process(const double*, const double*, const double*, size_t, const OfdrConfig*)` | 三路等长 |
| | `ofdr_result_free(OfdrResult*)` | 必须调用 |
| `detect.dll` | `detect_config_default()` → `DetectConfig`（8 参数） | 见 `detect.h` |
| | `detect_process(dist, ps, len, cfg)` / `detect_process_db(dist, ps_db, len, cfg)` | 本文只用前者（输入线性 `ps`） |
| | `detect_result_free(DetectResult*)` | 必须调用 |

> 全部结构体用 `[StructLayout(LayoutKind.Sequential)]` 逐字段映射，字段顺序与头文件一致；`size_t` 映射为 `UIntPtr`。

### 6.2 错误码映射

| 厂商错误码 | 分类（`SRS FR-UIX-03`） | 用户可见消息 |
|---|---|---|
| `QT_ACQ_ERR_INVALID_ARGUMENT` / `NOT_CONFIGURED` | 配置 | "采集参数不合法或未配置" |
| `QT_ACQ_ERR_INVALID_HANDLE` / `NULL_POINTER` | 协议 | "设备会话失效，请重新连接" |
| `QT_ACQ_ERR_BUFFER_TOO_SMALL` | 数据 | "缓冲区不足：期望 X 字节，实际 Y" |
| `QT_ACQ_ERR_SDK` / `UNKNOWN` | 连接 | 附 `qt_acq_last_error()` 原文 |
| `ofdr.error != 0` | 数据 | "OFDR 处理失败：error=…" |
| `detect.error != 0` | 数据 | "事件检测失败：error=…" |
| 超时（`.ready` 未出现） | 超时 | "采集超时（Ns），已跳过该次扫描" |

### 6.3 内存所有权

- `OfdrResult` 的 `dist/freq/ps` 与 `DetectResult` 的全部指针由库分配；
- 封装层用 `Marshal.Copy` 复制为托管 `double[]` 后**立即** `*_result_free`；
- 异常路径用 `try/finally` 保证释放；
- 向上只传托管数组与模型对象（`ICD Part B §5`）。

### 6.4 NaN 与 null

- `detect.h` 明确：首事件 `event_il = NAN`、`event_base_before = NAN`、段数不足 `event_base_after = NAN`；
- 转换层把 `double.IsNaN` 一律映射为 JSON `null`（`ICD §0.3 #3` 禁止 `NaN`）；
- 禁止用 0 代替（`SRS FR-MET-07`）。

### 6.5 DLL 定位

- 三个厂商 DLL 与 exe 同目录；启动时 `SetDllDirectory(exeDir)` 后首次调用；
- `DefaultDllImportSearchPaths` 关闭（避免从 `PATH` 误加载其它版本）；
- 启动自检：`qt_acq_version()` + 三个 DLL 的存在性/版本记录到 `session.log`。

---

## 7. 持久化与原子提交

### 7.1 目录

与 `ICD §2` 一致，本文只标注写入方：

| 路径 | 写入方 |
|---|---|
| `GMIX_DATA/_staging/<groupId>/Rxxx/*` | Acquirer |
| `GMIX_DATA/_staging/<groupId>/Rxxx/.ready` | Acquirer（ICD Part B §7.1） |
| `GMIX_DATA/_calibration/<calibrationId>/calibration.json` | App（校准向导） |
| `GMIX_DATA/<groupId>/group.json` | App（`GroupCommitter`） |
| `GMIX_DATA/<groupId>/Rxxx/`、`AVG/` | App（提交时移动，内容来自 staging） |
| `GMIX_DATA/<groupId>/view.json`、`session.log` | App |

### 7.2 写入顺序（`ICD Part B §7`）

```text
各 Rxxx 数组 → 各 Rxxx 元数据 → summary → AVG → group.json（最后）
```

`group.json` 最后写是提交信号：观察者只认它的存在。

### 7.3 原子提交

1. 校验全部 `Rxxx` 的 `.ready` 与清单；
2. 校验组内 `configHash`、`calibrationId` 一致（`ICD §2.2`）；
3. 若 `averageCount > 1`：在线性 `ps` 域平均 → 生成 `AVG/`（`ICD §2.2`）；
4. 目标目录 `<groupId>/` 已存在 → **拒绝**，报冲突，不覆盖；
5. **删除各 `Rxxx/.ready`**：交接标记属进程内部协议，不得进入最终组目录；
6. `Directory.Move(_staging/<groupId>, <groupId>)`（同卷原子）；
7. 写 `group.json` 最后（或先写临时名再重命名）。

> `Directory.Move` 跨卷会抛异常；数据根目录必须与 staging 同卷，启动自检校验。

### 7.4 失败与清理

| 情况 | 处理 |
|---|---|
| 任一次扫描超时 | 该 `Rxxx` 不纳入，记入 `group.json.rejected` |
| 全部失败（`validCount = 0`） | 不生成 `AVG/`，目录改 `_staging/<groupId>.failed/`，`status=failed` |
| 部分成功 | 照常提交，`status=incomplete`，`validCount < averageCount` |
| 启动清理 | 删除 `_staging/` 下无 `.ready` 且无 `.failed` 的残留；`.failed` 按 `retentionDays` 清理（`SRS FR-ACQ-09`） |

### 7.5 保留策略

`saveRaw = always | onFailure | off`、`keepRepeats = all | events_only | none`、`retentionDays`、`maxTotalBytes`，行为按 `ICD §2.3`。P0 只实现配置读取与 `saveRaw/keepRepeats` 生效；`retentionDays` 清理在 P0 仅做启动时简单删除（完整预算另立 P1 需求）。

---

## 8. UI 设计

### 8.1 页面划分（P0 4 页）

| 页 | 内容 | 主用例 |
|---|---|---|
| 测量主页 | 参数条 + 距离域曲线 + 指标表 + 事件表 + 日志区 | `UC-02`、`UC-03`、`UC-04`、`UC-05` |
| 事件表与结果 | 完整事件表 + 指标 + 单组导出 | `UC-07`、`UC-09` |
| 校准向导 | 参数录入 → 校验 → 版本化保存 → 重算 | `UC-06` |
| 历史检索与导出 | 组列表 + 回读 + 导出 | `UC-08`、`UC-09` |

### 8.2 主窗体控件树（测量主页）

```text
MainForm (1280×720 起，可缩放)
├─ ToolStrip 状态区：连接状态 · 设备 IP · 版本 · 校准状态(已标定/未标定)
├─ TableLayoutPanel (2 行 × 2 列)
│  ├─ [行0 跨2列] 参数条 Panel
│  │   ├─ 采样率(只读 50 MHz) · 采集时长(只读 8 ms)
│  │   ├─ 平均次数 NumericUpDown (1..N)
│  │   ├─ 运行模式 ComboBox (single/loop)
│  │   ├─ 分析区间：startM / endM TextBox + guardM(默认 0.1，只读)
│  │   ├─ 回损模式 ComboBox (peak_only / baseline_plus_peak)
│  │   ├─ 按钮：开始 · 停止 · 保存本组
│  │   └─ 未标定提示条（DS-06 常驻标注）
│  ├─ [行1 左] CurveCanvas (自绘)
│  └─ [行1 右] TableLayoutPanel
│      ├─ 指标表 DataGridView（4 指标 + 溯源项）
│      └─ 事件表 DataGridView（7 列）
└─ StatusStrip + 日志区（可折叠 RichTextBox，最近 N 条）
```

### 8.3 曲线控件（自绘 + 抽稀）

**类型**：`CurveCanvas : Control`（双缓冲，`ControlStyles.OptimizedDoubleBuffer | AllPaintingInWmPaint | UserPaint`）。

**绘制流程**：

1. 视图区间 `{xMin, xMax, yMin, yMax}`（默认 X `0 ~ endM`、Y `-150 ~ -40`，DS-01）；
2. 计算绘图区宽度 `W`（像素）；
3. `EnvelopeSampler.Reduce(dist, values, xMin, xMax, W)` → 每列 `(minIndex, maxIndex)`；
4. 每列画竖线段 `min → max`（**保峰**，`SRS FR-CHT-07`），相邻列连线；
5. 叠加：基线曲线（细线）、峰标记（▲ + `peakNo`）、分析区间遮罩（半透明 + guard 虚线）、零距峰（按 `display.zeroPeakExcluded` 隐藏）；
6. 游标：两条竖线 + 读数框（距离、电平），拖拽时只重绘游标层。

**性能预算**：`N/2+1 ≈ 200 001` 点（50 MHz/8 ms），`W ≈ 800` → 250 点/列；抽稀后 ≤ 1600 段绘制，GDI+ 实测远低于 100 ms（`SRS NFR-PERF-02`）。抽稀与绘制在工作线程生成 `Bitmap`，UI 线程只 `Invalidate` + 贴图。

**禁止**：在 `OnPaint` 里遍历全量数组（必须走抽稀）、在 UI 线程调厂商库。

### 8.4 显示口径单点（DS-01~08）

`DisplayTransform` 是**唯一**应用位置，签名示意：

```csharp
// DS-01：字段 + rlOffsetK（已标定用 calibration.rlOffsetK，未标定用 -192）
static double Level(double dbRel, double offsetK) => dbRel + offsetK;

// DS-02：总回损，标定后用 totalRlDb，未标定用 totalRlRawDb + 默认偏移并标注
static string TotalRl(double? rlDb, double? rlRawDb, double offsetK, bool calibrated);

// DS-04：总插损取负（只作用于 IL 域，严禁套用到 RL）
static string TotalIl(double? ilDb) => ilDb is null ? "—" : (-ilDb.Value).ToString("0.00");

// DS-05：精度恒正，原样显示；null → "—"（不取绝对值，避免掩盖异常值）
static string Precision(double? v) => v is null ? "—" : v.Value.ToString("0.00");
```

| DS | 实现落点 |
|---|---|
| DS-01 | `DisplayTransform.Level` + `CurveCanvas` 轴变换 |
| DS-02 | `DisplayTransform.TotalRl` + 指标表 |
| DS-03 | 事件表直接绑定 `stepLossDb` |
| DS-04 | `DisplayTransform.TotalIl`（仅 IL 域） |
| DS-05 | `DisplayTransform.Precision` |
| DS-06 | `DisplayTransform.DefaultOffsetK = -192`，未标定态强制显示提示条 |
| DS-07 | `view.json` 只写 `xRange/yRange/cursor/activeTab/selectedPeakNo` |
| DS-08 | 仅 `Gmix.App` 引用 `DisplayTransform`；Core/Acquirer 不得引用 |

### 8.5 指标表与事件表

- 指标表 4 项：插入损耗（DS-04）、插损精度、回波损耗（DS-02）、回损精度；溯源行：平均次数、分析区间、回损模式、校准状态（`SRS FR-TBL-01/02`）。
- 事件表 7 列：`peakNo`、`distanceM`、`rlDb`/`rlDbRel`、`stepLossDb`、`baseBeforeDbRel`、`baseAfterDbRel`、`zeroPeak`（`SRS FR-TBL-04`）。
- 事件表排序与曲线联动：`DataGridView` 排序后以行内 `peakNo` 定位曲线标记；双击行把游标 A 移到该峰（`SRS FR-CHT-09`）。

### 8.6 校准向导

1. 录入：`psdRef`、`ilSlope`、`ilIntercept`、`triggerThresholdDb`、`rlOffsetK`、`distScale`（`SRS FR-CAL-01`）；默认 `ilSlope = 0.5361`、`ilIntercept = -0.1`（厂商确认 2026-11-05），**不得沿用校准页初值 `0.7373`**；
2. 校验：数值范围、`configHash` 与当前配置一致；
3. 保存：`CAL-YYYYMMDD-NNN` → `_calibration/<id>/calibration.json`；
4. 生效：重算当前曲线与指标（`SRS FR-CAL-04`），`calibrationState = calibrated`；
5. 未完成标定时允许"显示预览"（默认偏移 -192），但 `rlDb` 仍为 `null`（`SRS FR-CAL-06`）。

### 8.7 历史与导出

- 组列表：按 `createdAtUtc` 倒序 + 组 ID 搜索；索引只存路径与摘要（`ICD §2.3`）；
- 回读：`GroupStore.Load(groupId)` → 复用测量页的曲线/表格控件渲染，保证与首次一致（`SRS FR-HIS-02`）；
- 导出 CSV：列序与事件表字段一致，**导出字段原值（正刻度）**，不导出显示值（`SRS FR-HIS-03`）；
- 导出只读，不改原始数据（`SRS FR-HIS-05`）。

---

## 9. 配置快照与 configHash

### 9.1 四类配置的实际来源

`SchemaModels.ConfigSnapshotInfo` 已定义 `interface`/`afe`/`range`/`functional`/`hash`。实际文件：

| 类 | 来源 | 现状 |
|---|---|---|
| `interface` | `config/Interface.json`（根级；`config/<cardIndex>/Interface.json` 为卡槽覆盖） | 已确认（9 板卡槽位 + 寄存器基址 + `CardIndx`） |
| `afe` | `config/<cardIndex>/AFE.json` | 已确认存在（`0/AFE.json`） |
| `range` | `config/<cardIndex>/Range.json` | 已确认存在（`0/Range.json`） |
| `functional` | `config/<cardIndex>/FunctionalConfiguration.json` | 已确认存在（`0/FunctionalConfiguration.json`） |
| 模板 | `config/QT2144/{AFE,Range,FunctionalConfiguration}.json` | 板型模板，非运行态 |
| 版本 | `config/version/sdk_version_meta` | 内容 `1.0.0.0`；与 `qt_acq_version()` 返回 `0.1.0` **不一致**，待厂商澄清（`TBD-D08`） |

> **加载规则（探针实测）**：卡槽配置位于 `config/<cardIndex>/`，`cardIndex` 取自 `Interface.json` 的 `CardIndx`（当前为 0）；SDK 启动时**要求 `config/` 目录存在**，缺失则 `qt_acq_open` 返回 `-2`。

> SRS §11 的"四类配置"与磁盘目录名不完全对应；以本节实测结果为准（2026-11-05 已实机确认）。

### 9.2 加载

1. 内置默认（`AcquisitionDefaults`：50 MHz / 8 ms / 软件触发 / 延时 1 ms）；
2. 覆盖：可选配置文件（exe 同目录 `config/`）；
3. 生成不可变 `ConfigSnapshot`（一次性快照，运行期不重读）；
4. 与设备无关的字段（采样率、AFE、量程、通道映射）参与 `configHash`。

### 9.3 `configHash` 计算（ICD Part A §6.1）

口径（参与域 6 段、排除域、Canon 规则、`RS` 拼接、算法版本 `canonical-json/sha256/v1`）以 **ICD Part A §6.1 为准**。实现要点：

- 单一实现：`Gmix.Core.ConfigSnapshot.ComputeHash`，Acquirer 与 App 共用，禁止各写一份；
- 键序固定用 `Utf8JsonWriter` 按**预排序键**输出（不依赖反射/字典枚举顺序，否则跨进程不稳定）；
- 复用 `HashUtil.Sha256Text`（UTF-8 + SHA256 + `sha256:` 小写 hex），不新建哈希逻辑；
- 段间用 `U+001E` 连接，缺失文件写 `null`。

### 9.4 不可热改

配置快照在启动时一次性生成；运行期修改配置须重启采集会话（`SRS FR-CFG-01`、`ICD §0.3 #11`）。UI 上"配置锁定"状态可见。

---

## 10. 错误契约与日志

### 10.1 五分类

`连接 / 协议 / 数据 / 超时 / 组一致性`（`SRS FR-UIX-03`、`ICD Part B §4`）。所有异常必须归类后上报，禁止 `catch {}`。

### 10.2 日志

| 级别 | 内容 | 去向 |
|---|---|---|
| ERROR | 会话失败、协议错误、组一致性拒绝 | 日志区 + `session.log` |
| WARN | 校准失效、基线缺失退化、抽稀丢弃计数、超时跳过 | 日志区 + `session.log` |
| INFO | 连接、配置、采集起止、提交、耗时 | `session.log` |
| DEBUG | 逐扫描时序（`openMs/readMs/totalMs`） | `session.log`（可选） |

### 10.3 `session.log`

按组写入 `<groupId>/session.log`（`SRS FR-STO-06`），格式 `时间 | 级别 | 分类 | 消息`；同时 `scan.json.timing` 保留单次耗时，便于 NFR-PERF-03 实测。

---

## 11. 部署

### 11.1 发行包结构

```text
Gmix/
├─ Gmix.App.exe
├─ Gmix.Acquirer.exe
├─ Gmix.Core.dll
├─ Gmix.Vendor.dll
├─ qt_acq.dll          # 厂商
├─ ofdr.dll            # 厂商
├─ detect.dll          # 厂商
└─ config/             # 配置快照来源（可选）
```

### 11.2 运行时

- 仅需 .NET 8 Desktop Runtime（WinForms）；
- **必须安装 VC++ 2010 SP1 x64 运行库（`MSVCR100.dll`）**：`qt_acq.dll` → `QT_Board.dll` → `pthreadVC2.dll` → `MSVCR100.dll` 为静态依赖链，缺失时加载报 `0x8007007E`（实测 2026-11-05）；
- **不需要 MATLAB Runtime**（`ofdr.dll`/`detect.dll` 为纯 C 接口，已确认）；
- **千兆网口路径不需要 Npcap**：`QT_Board_Net.dll` 仅依赖 `WS2_32.dll`；只有万兆 `QT_Board_10Net.dll` 依赖 `wpcap.dll`；
- 必须随包提供 SDK 的 `config/` 目录（缺失时 `qt_acq_open` 返回 `-2`，`QT_BoardOpenBoard code -1`）；
- x64 单一架构；无 32 位构建。

### 11.3 首次启动自检

| 项 | 失败行为 |
|---|---|
| 三个厂商 DLL 存在且可加载 | 阻止测量，提示缺失文件 |
| `qt_acq_version()` 可读 | 阻止测量，提示驱动/权限问题 |
| 数据根目录与 `_staging` 同卷、可写 | 阻止测量，提示路径 |
| 清理无 `.ready` 的 staging 残留 | 记录日志，继续 |
| 校准库可读 | 允许未标定态启动（`SRS FR-ACQ-09`） |

---

## 12. 测试与验证

### 12.1 迁移基线（现有 14 条断言必须全绿）

`schema validation` / `calibration round-trip` / `summary rl fields` / `peak_only totalRlRaw` / `baseline_plus_peak` / `区间插损 raw=12dB` / `插损标定公式` / `绝对回损公式` / 4 条 RL 裁定回归 / `精度 sample std` / `schema v0.2`。

### 12.2 P0 新增验证项（拟入 `Gmix.Tests`）

| ID | 用例 | 判据 |
|---|---|---|
| T-IPC-01 | Acquirer 写 `.ready` → App 轮询识别 | 清单齐全、`bytes` 一致、原子可见（无半成品） |
| T-IPC-02 | 无 `.ready` 的 staging → 启动清理 | 目录被删除，不产生组 |
| T-IPC-03 | 组内 `configHash` 不一致 | 拒绝平均，`status=failed` |
| T-DSP-01 | `DisplayTransform.Level` 单点性 | Core/Acquirer 无 `rlOffsetK` 引用（静态检查） |
| T-DSP-02 | `DisplayTransform.TotalIl` 不作用于 RL | 输入 `rlDb` 不被取负 |
| T-UI-01 | 包络抽稀保峰 | 单点窄峰在 800 列下仍被绘出 |
| T-UI-02 | 抽稀等价性 | 抽稀结果的最值与全量最值一致 |
| T-NAN-01 | `DetectResult` 的 `NAN` → JSON `null` | 序列化无 `NaN` 字样 |
| T-HASH-01 | `configHash` 跨进程一致 | Acquirer 与 App 同输入同 hash |

### 12.3 夹具

沿用 `GM-IX/tests/fixtures/schema_v02_sample/`（50 MHz / 8 ms，含 raw/数组/JSON）；新增 `staging_fixture/`（含 `.ready` 与缺 `.ready` 两种），**不得依赖 `bin/` 或本机路径**。

### 12.4 `Gmix.Data` → `.NET 8` 迁移步骤

| 步 | 动作 | 风险 |
|---|---|---|
| 1 | 新建 `Gmix.Core.csproj`（`net8.0`），纳入 6 个源文件 | 无 |
| 2 | `JavaScriptSerializer` → `System.Text.Json`（`JsonSerializer`） | 属性名为小写 camel 时默认原样输出，字段名不变；需验证 4 个 `object` 字段 |
| 3 | `ConfigSnapshotInfo` 的 `interface/afe/range/functional` 由 `object` → `JsonNode` | `System.Text.Json` 对 `object` 反序列化为 `JsonElement`，不可直接当字典用 |
| 4 | 测试 `Program.cs` → `Gmix.Tests` 控制台项目 | 断言保留，退出码语义不变 |
| 5 | 删除 `build-and-test.ps1` 的 `csc.exe` 路径依赖 | 改用 `dotnet build/run`；不再依赖 Windows 内置编译器 |
| 6 | 回归：14 条断言全绿 + 夹具重新生成一致 | 若 hash/序列化文本变化，需确认不影响 `checksums` 口径 |

> 迁移只改**实现与工程形态**，不改字段、公式与 `schemaVersion`（仍为 0.2，升 1.0 随组结构另做）。

---

## 13. 实施状态与延期

### 13.1 已完成

| 项 | 状态 |
|---|---|
| `Gmix.Data` 模型/校验/指标/校准/存储 | 已实现，14 条断言全绿 |
| `.NET 8` 工程化（`Gmix.sln`：Core/Vendor/Acquirer/Tests） | 已完成（2026-11-05），构建 0 警告 0 错误；`JavaScriptSerializer` → `System.Text.Json`，`object` → `JsonNode`，回归 PASS 33 |
| `Gmix.Vendor`（NativeApi/QtAcquisition/OfdrProcessor/Detector/VendorRuntime/错误映射） | 已完成，离线编译通过；句柄不外露、结果复制后即 free、NaN→null |
| `Gmix.Core` 新增 `DisplayTransform`/`EnvelopeSampler`/`ConfigSnapshot`/`GroupStore`/`ReadyDocument` | 已完成，新增 19 条断言全绿（DS-01/02/04/05、抽稀保峰、configHash 键序与稳定性、`.ready` 校验与原子提交） |
| `Gmix.Acquirer` 合成模式 | 已跑通（2026-11-05）：峰 418、61 ms、写出 10 个文件 + `.ready`；实机模式见下行 |
| **实机采集验证（2026-11-06）** | **通过**：`open=0`、读 3,200,000 字节、峰 5（区间内 4）、基线 +95.8~+99.7、峰 +117.4~+139.5（与 g50 实测一致）、总 RL raw **+140.47（正刻度）**、`.ready` 10 文件校验 0 不符；端到端 451~527 ms，其中**采集 362~367 ms 为瓶颈**（处理 33~52、检测 17~21、聚合 0~5、落盘 32~80） |
| 设备接入探针（2026-11-05） | **已通过**：`qt_acq_version`=0.1.0、`calc_buffer_bytes`=3,200,000、`open(0)`=0、`close`=0；条件为 x64 + `MSVCR100.dll` + `config/` 目录 |
| ICD 回填：`.ready` 交接协议（Part B §7.1）、`configHash` 口径（Part A §6.1） | 已完成（2026-11-05），TBD-D09 关闭 |
| 厂商 P/Invoke 声明 | 已抽取到 `Gmix.Vendor/NativeApi.cs`（唯一 P/Invoke 位置）；`Qt2144Probe.cs` 保留为历史探针，不参与构建 |
| 数据夹具 | 已生成（`schema_v02_sample`） |
| ICD v1.0 / SRS v0.1 | 已评审，无遗留待评审项 |

### 13.2 P0 待实现

| 序 | 内容 | 依赖 |
|---|---|---|
| 1 | `Gmix.App`：WinForms 4 页（测量主页/结果/校准/历史） | Core/Vendor/Acquirer 已完成 |
| 2 | 历史检索与 CSV 导出 | 1 |
| 3 | 组结构 schema 1.0（`group.json`、`AVG/`、`psdRef`、精度字段） | — |
| 4 | 合成数据量级校准（当前合成谱的 dB_rel 为负，与 g50 实测正刻度不符，仅用于流程验证） | — |
| 5 | 采集耗时优化（TBD-D10，需厂商确认 SDK 固定等待与传输参数） | 厂商 |

### 13.3 延期（P1/P2，仅登记不设计）

配置编辑界面、`detect.h` 8 参数高级设置、批量/循环增强（P1）；合格判定与阈值、PDF 报表、多段/外触发（P2）。

### 13.4 TBD 引用

- `TBD-D08`（Open）：`sdkVersion` 元数据不一致——`config/version/sdk_version_meta`=`1.0.0.0`，`qt_acq_version()`=`0.1.0`；三类配置文件本身已确认存在（§9.1）。
- `TBD-D09`（已关闭）：`.ready` 交接判据已回填 ICD Part B §7.1 与 Part A §2（2026-11-05）。

其余 TBD 以 SRS §9 与 ICD §8 为准。

### 13.5 采集耗时实测定界（2026-11-06）

在设备实机上逐段计时（`qt_acq` 五步），结果如下：

| 阶段 | 8 ms 采集（3.2 MB） | 1 ms 采集（0.4 MB） | 性质 |
|---|---:|---:|---|
| `open` | 113~118 ms | 116~118 ms | 一次性，与数据量无关 |
| `start` | 102~104 ms | 103 ms | **固定开销** |
| `read` | 32~51 ms | **8 ms** | 与数据量成正比（≈63 MB/s） |
| `stop` / `close` | 201~204 ms | **203 ms** | **固定开销，二者等效** |

**结论**：

1. 362 ms 的采集耗时并非"传输慢"：`read` 仅 50 ms，比例正常。
2. 瓶颈是 `start`（103 ms）与 `stop`/`close`（203 ms）两段**与数据量无关的固定序列**，合计约 306 ms。
3. 跳过 `stop` 直接 `close` 无收益（`close` 同样 203 ms，已实测）。
4. 连续 `start → read` 不 `stop` 虽返回 `rc=0`，但**数据不可用**：实测 5 帧中仅 2 个不同哈希（第 1、2 帧完全相同，第 3~5 帧 ch0 全为 0，`mean=0/std=0`），属重复帧与空帧，**禁止采用**。
5. 一次 `start` 只产生一帧：`start` 后第二次 `read` 会**永久阻塞**等待下一次触发，说明软件触发模式下无法"一次启动连续读"。
6. A/B 验证：`PCIe_FAKE_INTR_TIME_S` 0.02→0.005、`PCIe_INIT_IBLOCK_COUNT` 100→60，`close` 均仍为 203 ms，**该参数不控制此开销**。

**必须采用 `start → read → stop` 完整循环**，单帧约 360 ms 为硬下限，无法通过调用序列优化。

**对设计的影响**：单帧下限约 360 ms（不含 `open`），循环模式节拍约 450 ms/帧，属硬件/固件上限；NFR-PERF-01 已按实测修订为 ≤600 ms。若要更高节拍，只能依赖厂商提供"快速停止"或可靠的连续采集接口（`TBD-D10`）。

> 实验均在 `.tmp/probe_run` 的配置副本上进行，`references/` 未改动；每轮实验后已还原配置。

### 13.6 已知限制（2026-11-06）

| 项 | 内容 | 影响 | 处置 |
|---|---|---|---|
| 采集信号漂移（`TBD-D11`） | 同一 `open`-`close` 周期内连续 20 帧，`dist` 末值波动 16~17%、`ps` 最大值波动约 3 倍；已排除 ofdr 随机性、触发相位、AFE 自动增益，根因为硬件输入信号本身漂移（`GM-IX/investigations/signal-drift`） | 超出 ICD §2.2 的 5% 轴容差时 `GroupAssembler` 判 `invalid`，N>1 平均在实机上暂不可用 | 容差暂按 5% 保留；UI 与历史页须能展示组 `failed/invalid` 原因，不得静默降级；N=1 单次测量不受影响 |

---

## 14. 需求覆盖对照（P0）

| SRS 域 | 本文落点 |
|---|---|
| ACQ（FR-ACQ-01~09） | §3、§5.3、§6、§7.4 |
| PROC（FR-PROC-01~06） | §4、§5.2、§6.3 |
| MET（FR-MET-01~07） | §5.1（`MetricsAggregator` 已实现） |
| CHT（FR-CHT-01~09） | §8.2、§8.3 |
| TBL（FR-TBL-01~06） | §8.5 |
| CAL（FR-CAL-01~06） | §8.6、§5.1 |
| HIS（FR-HIS-01~05） | §8.7、§5.4 |
| STO（FR-STO-01~06） | §7 |
| UIX（FR-UIX-01~04） | §8.1、§8.2、§10 |
| CFG（FR-CFG-01） | §9 |
| DS-01~08 | §8.4 |
| NFR-PERF/REL/MNT/TRC | §3.4、§7.4、§11、§10.3 |
