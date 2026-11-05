# HRAS-9000 上位机与 QT-2144 SDK 核实分析报告

| 项目 | 内容 |
| --- | --- |
| 分析对象 | `references/HRAS9000`（上位机软件与手册）<br>`references/qt2144-rs`（采集 SDK）<br>`references/ofdr-0.1.1-release`（OFDR 处理库）<br>`references/detect_1.0`（IL/RL 检测库） |
| 分析日期 | 2026-11-03 |
| 分析性质 | 基于安装包解包、PE 导入/导出表、配置与二进制字符串、PDF 文本提取、ctypes 运行探测的**离线核实**；不是厂商正式文档 |
| 硬件状态 | 本机 `QT_BoardGetDeviceCnt() = 0`（无板卡），所有涉及真实采集的行为均未实测 |
| 关联文档 | `docs/映讯QT2144_SDK接入说明书.md`、`docs/QT2144_qt_acq_反推说明书.md`（SDK 细节以其为准，本报告只补充 HRAS 侧证据与差异） |
| 证据等级 | 【A】随附文档/头文件/Python 源码明示；【B】二进制、解包内容或本次运行直接观察；【C】由证据推断，未经厂商确认 |

---

## 0. 结论摘要

1. **HRAS-9000 是 MATLAB Compiler（R2023a）生成的上位机**：安装包是 MathWorks 的 Web Installer（`Installer_web.exe`），程序为 App Designer 应用 `HRAS_9000.exe` + 加密 p-code `.m` + 内嵌 CTF，运行必须安装 **MATLAB Runtime R2023a（MCR 9.14）**，首次安装需联网下载且需管理员权限【A/B】。
2. **HRAS 直接调用 `QT_Board.dll`，不调用 `qt_acq.dll` / `ofdr.dll` / `detect.dll`**：CTF 内只有 `QT_Board` 系列运行库与 MATLAB thunk，没有后三者；二进制中也检索不到 `qt_acq`、`ofdr` 字样【B】。
3. **HRAS 随包的 13 个 QT2144 运行库与 `references/qt2144-rs` 同名文件 SHA-256 全部一致**；唯一实质差异是 `config/QT2144/AFE.json` 的逐机校准值【B】。
4. **采集走网口路径**：配置 `is_net = true`、随包 `QT_Board_Net.dll` / `QT_Board_10Net.dll`，1G 寄存器基址 `0x40000000`；配置声明 `frame_header_bytes_number = 64`，但帧头实际布局与网络协议未公开【B/C】。
5. **处理链是 p-code，算法不可读**：`OFDR_process_segments.m`、`OFDR_baseline_il.m`、`OFDR_calibrate_il.m`、`OFDR_summarize_metrics.m` 等均以 `V2MCC8000...` 加密头封装，无法核实其与 `ofdr`/`detect` 库是否等价，**不能假设等价**【B】。
6. **新增 3 个运行期事实（既有 SDK 文档未覆盖）**：
   - `qt_acq_open(0)` 在**无板卡**时仍返回 `0`（成功），不能以 open 成功判断设备已连接【B】；
   - 干净 Windows 10 上 `QT_Board.dll` 因 `pthreadVC2.dll → MSVCR100.dll` 缺失而**无法加载**，补上 `msvcr100.dll` 后可正常加载【B】；
   - `qt_acq_configure` 传入非 `NULL` 的野句柄会**访问违例崩溃**，仅 `NULL` 句柄被拦截返回 `-4`【B】。

---

## 1. 分析对象与方法

### 1.1 对象清单

| 目录/文件 | 内容 | 体积 |
| --- | --- | --- |
| `references/HRAS9000/Installer_web.exe` | 上位机 Web 安装包 | 7,514,112 B |
| `references/HRAS9000/HRAS-9000 操作指导书.pdf` | 中文操作指导书 V1.0 | 1,200,801 B，12 页 |
| `references/qt2144-rs/` | 采集 SDK（`qt_acq.dll` + `QT_Board` 系列 + 配置 + Python/C 封装） | 13 个同名 DLL + 4 个板卡传输 DLL |
| `references/ofdr-0.1.1-release/` | OFDR 处理库（Rust 编译的 C ABI） | `ofdr.dll` 1,214,464 B |
| `references/detect_1.0/` | IL/RL 检测库（Rust 编译的 C ABI） | `detect.dll` 294,912 B |

### 1.2 方法

- 安装包：手工解析 ZIP 本地头/中央目录、解压 CTF，读取 XML/JSON/头文件；
- 二进制：自写 PE 解析器读取导入表、导出表；用 ASCII/UTF-16 字符串定位版本、依赖与错误消息；
- PDF：使用便携 Python + `pypdf` 提取全文（12 页）；
- 运行探测：将 SDK 复制到临时目录（不改动 `references/`），用 Python `ctypes` 直接调用 DLL；
- 未做：反汇编语义还原、硬件实测、网络抓包。

### 1.3 本机限制

- `QT_BoardGetDeviceCnt()` 返回 0，**没有 QT-2144 板卡**；因此 `configure/start/read` 的真实数据路径未验证；
- 为避免阻塞或崩溃，未对真实句柄调用 `start/read`。

---

## 2. HRAS9000 目录内容

### 2.1 `Installer_web.exe`

| 项 | 观察值 | 证据 |
| --- | --- | --- |
| 大小 | 7,514,112 B | 【B】 |
| 数字签名 | `NotSigned` | 【B】`Get-AuthenticodeSignature` |
| 版本资源 | 无 FileDescription/ProductName/CompanyName/FileVersion | 【B】 |
| 安装器类型 | MathWorks Application Compiler Web Installer（含 `ApplicationInstallerManifest.xml`、`appdata/products/...`、`MW_BOOTSTRAP_INSTALLER` 字符串） | 【B】 |
| 应用名/版本 | `HRAS_9000` / `1.0`；产品号 5000；GUID `5ca526d9-...` | 【B】`ApplicationInstallerManifest.xml` |
| 默认安装目录 | `%ProgramFiles%\HRAS_9000\` | 【B】 |
| 运行库要求 | MATLAB Runtime `R2023a`（MCR 9.14），首次安装需联网下载；安装 MCR 需管理员权限 | 【A】`application/readme.txt` |
| MCR 组件 ID | 35002、35003、35010、35108 | 【B】`.META/requiredMCRProducts.txt` |
| 依赖工具箱迹象 | 清单包含 `toolbox/signal/signal`、`toolbox/shared/siglib`、`signalwavelet`、`spcuilib`、`coder` 等路径 | 【B】（具体产品映射未确认）【C】 |
| 安装包内嵌内容 | 16 项应用 ZIP + `appdata` 清单 + 6,005,192 B 的 `HRAS_9000.exe`（内部含 83 项 CTF） | 【B】 |

安装包内同时出现 MathWorks R2023a 部署文件地址：

```
https://ssd.mathworks.com/supportfiles/downloads/R2023a/Release/0/deployment_files/installagent/complete/win64/InstallAgent_R2023a_win64.zip
```

这与“Web Installer 首次联网下载 Runtime”的行为一致【B】。

### 2.2 `HRAS-9000 操作指导书.pdf`

12 页，可提取文本。要点：

| 主题 | 内容 | 证据 |
| --- | --- | --- |
| 产品定位 | 基于 OFDR 的高精度光纤链路分析仪 + 专用上位机；获取沿程反射分布，完成事件定位、IL、RL 测量；识别连接器、熔接点、宏弯、断点、端面反射 | 【A】 |
| 产品组成 | HRAS-9000 主机 ×1、国标 10A 三插 C13 电源线 ×1、**采集网口数据线** ×1 | 【A】 |
| 供电/环境 | 220 V / 1 A；运行温度 20℃~40℃ | 【A】 |
| 连接顺序 | 采集网口数据线连接整机与电脑 → 光纤跳线连接待测物 → 接电源并开机 | 【A】 |
| 软件安装 | 手册写 `MyAppInstaller_web.exe`；实际随附文件名为 `Installer_web.exe`（命名不一致） | 【A/B】 |
| 启动判据 | 运行日志出现 **“ADC 启动完成”** 后才可采集 | 【A】 |
| 采集方式 | 单次运行 / 循环运行；推荐平均次数 **5~10** 次 | 【A】 |
| 区间选择 | 修改“分析起点/分析终点”定义 IL 测量区间，界面黄色区域同步显示 | 【A】 |
| 回损算法 | 两种：**仅峰值**（区间内离散反射峰总回波功率）、**基线+峰值**（离散峰 + 背景瑞利散射总功率） | 【A】 |
| 校准参数 | 功率谱密度（绝对电平基准）、插损斜率（比例误差）、插损截距（固定偏移）、触发阈值（峰检测门槛，dB） | 【A】 |
| 校准示例 | 程控光衰减器 0/1/2/3/4/5 dB，测得 IL：-0.35/1.43/2.37/3.7/4.5/5.5 dB；拟合 `ILreal = 0.879·ILraw − 0.014`，回填斜率/截距后 2 dB、4 dB 测量准确 | 【A】 |

PDF 页面中的软件截图（第 8~11 页）与安装包内 `appScreenshot.png` 一致：平均次数、单次/循环运行、IL/回损读数、扫描范围/校准/保存页签、分析起点/终点、X/Y 轴范围、回损计算下拉框（仅峰值）、运行日志、峰/台阶(dB)/回损(dB)/距离(m) 表格【A/B】。

---

## 3. HRAS_9000 程序构成

### 3.1 技术栈（已核实）

```text
Installer_web.exe（MathWorks Web Installer）
└─ HRAS_9000.exe（MATLAB Compiler 可执行程序，FileVersion 1.0.0.0）
   └─ 内嵌 CTF（83 项，ZIP 容器）
      ├─ fsroot/HRAS_9000/HRAS9000.mlapp        App Designer 工程（Responsive，最低 R2019a）
      ├─ fsroot/HRAS_9000/ADC/*.m               采集包装（p-code）
      ├─ fsroot/HRAS_9000/ADC/QT2144_Dll/*      QT_Board SDK、MATLAB thunk、配置、头文件
      ├─ fsroot/HRAS_9000/OFDR_*.m              处理/校准/汇总（p-code）
      └─ metadata、签名、资源
```

- CTF 元数据：`mcr-major-version=9`、`mcr-minor-version=14`、`mcr-release-version=0`，`supported_archs=win64`，`need_java=1`【B】；
- `HRAS9000.mlapp` 的 `coreProperties.xml`：创建 2026-08-06，修改 2026-08-25，标题 `HRAS_9000`，版本 1.0【B】。

### 3.2 CTF 内容分类（83 项）

| 类别 | 主要文件 | 说明 |
| --- | --- | --- |
| 采集包装（p-code） | `ADC_start.m`、`ADC_acquisition.m`、`ADC_multiple_delay_acquisition.m`、`private/adc_decode_std_multi_buffer.m`、`adc_validate_*.m` | 启动、单次采集、带延时的多次采集、多段解码、入参校验 |
| QT2144 运行库 | 14 个 DLL（见 §4.1）、`QT_Board.h`、`QT_Board_MATLAB.h`、`QT_Board_Project.h`、`QT_Board_thunk_pcwin64.dll` | 板卡调用、MATLAB thunk、配置 |
| 板卡包装（p-code） | `QT_BoardGetBoardInfo.m`、`QT_BoardSetAFE.m`、`QT_BoardSetFanCtrl.m`、`QT_BoardSetRecChSelect.m`、`QT_BoardSetRecClockMode.m`、`QT_BoardSetRecFIFOMulti/Single.m`、`QT_BoardSetRecRepMode.m`、`QT_BoardSetRecStdMulti/Single.m`、`QT_BoardSetRecTrigMode.m`、`QT_BoardSetRep*.m` | 与 `QT_Board_MATLAB.h` 的导出签名一一对应 |
| 配置 | `config/Interface.json`、`config/QT2144/{AFE,Range,FunctionalConfiguration}.json`、`config/version/sdk_version_meta`、`config/InitUI.json`（加密） | 接口、量程、模拟前端、UI |
| 处理（p-code） | `OFDR_process_segments.m`、`OFDR_baseline_il.m`、`OFDR_calibrate_il.m`、`OFDR_summarize_metrics.m`、`extract_baselines.m`、`remove_points_by_std.m` | 分段处理、基线/插损、校准、汇总、基线提取、按标准差剔除点 |
| UI/资源 | `HRAS9000.mlapp`、`splash.png` | App Designer 工程 |

### 3.3 可分析性边界

- 所有 `.m` 与 `HRAS9000.mlapp` 的 `matlab/document.xml` 均以 `V2MCC8000MEC2000MCR2000…` 开头，属于 MATLAB p-code 加密载荷【B】；
- 因此只能确认**模块名、文件清单、配置、调用接口和 UI 文案**，**不能确认算法细节**；
- `config/InitUI.json` 也是加密/编码文本，未还原。

---

## 4. 采集链路核实

### 4.1 与 `references/qt2144-rs` 的同一性

对 HRAS 随包文件与 SDK 目录同名文件逐一计算 SHA-256：

| 文件 | SHA-256 前 16 位 | 结果 |
| --- | --- | --- |
| `QT_Board.dll` | `DABEC23DBFD8F673` | 相同 |
| `QT_Board_Net.dll` | `06D9796A98D8F67D` | 相同 |
| `QT_Board_10Net.dll` | `AC519CD4BBA75F56` | 相同 |
| `QT_Board_PCIe.dll` | `C403F43C4BA1EA7B` | 相同 |
| `Packet.dll` | `AB3CCE674F521689` | 相同 |
| `wpcap.dll` | `35AAB6CAAAF1720A` | 相同 |
| `elog.dll` | `67F96C3B66FCCFF2` | 相同 |
| `libfftw3-3.dll` | `001A835D2F25AC06` | 相同 |
| `libfftw3f-3.dll` | `42CA18FFF35DD128` | 相同 |
| `libfftw3l-3.dll` | `CE78FCD1866E7A5E` | 相同 |
| `libliquid.dll` | `C72517E0C6290C29` | 相同 |
| `pthreadGC2.dll` | `0250C506A0D79B1D` | 相同 |
| `pthreadVC2.dll` | `0E6AF724609EF684` | 相同 |

结论：**HRAS 使用的就是 `references/qt2144-rs` 这一套 QT2144 运行库，二进制完全一致**。第 14 个 DLL 是 MATLAB 专用 `QT_Board_thunk_pcwin64.dll`，SDK 目录中没有（见 §4.4）。

### 4.2 配置差异

| 文件 | 语义比较 | 结论 |
| --- | --- | --- |
| `config/Interface.json` | 与 SDK 版本逐行相同 | 相同 |
| `config/QT2144/Range.json` | 逐行相同（仅空白/缩进差异） | 相同 |
| `config/QT2144/FunctionalConfiguration.json` | 逐行相同（仅空白/缩进差异） | 相同 |
| `config/QT2144/AFE.json` | 4 个通道 × 6 个量程的 `offset/gain/attenuation` 不同 | **逐机校准差异** |
| `config/version/sdk_version_meta` | SDK：`1.0.0.0`；HRAS：`8e8a01d2257103d20d4f84b56d091dada7ad9fd5` + `2025-06-16 11:51:56` | 二进制相同，元数据口径不同 |

AFE 差异示例（`offset` / `gain`）：

| 通道 | SDK 示例配置 | HRAS 随包配置 |
| --- | --- | --- |
| ch0 | `2066,2066,2064,2059,2061,2065` / `5,11,18,18,15,23` | `2060,2061,2063,2059,2061,2065` / `5,12,18,18,15,22` |
| ch1 | `2063,2063,2060,2053,2047,2042` / `5,11,18,18,15,23` | `2057,2057,2059,2053,2047,2042` / `5,12,18,18,15,22` |
| ch2 | `2069,2069,2065,2059,2056,2060` / `5,11,18,18,15,23` | `2060,2061,2062,2059,2056,2060` / `5,12,18,18,15,22` |
| ch3 | `2069,2069,2062,2054,2050,2046` / `5,11,18,18,15,23` | `2058,2059,2059,2054,2050,2046` / `5,12,18,18,15,22` |

结论：**AFE 校准值不能直接沿用 `references/qt2144-rs` 的示例配置**，必须使用本机/本设备的实测校准结果【B/C】。

### 4.3 接口与数据通路（来自 HRAS 配置）

| 字段 | 值 | 解读 |
| --- | --- | --- |
| `Interface.json → QTBoard0` | `1`（其余 QTBoard1..7 为 -1，`CardIndx` 0） | 板卡 0 的接口类型 1【B】；结合 `is_net=true` 与 `QT_Board_Net/10Net`，判断为网口【C】 |
| `GigabitNet_REG_BASE_ADDRESS` | `1073741824`（0x40000000） | 1G 网口寄存器基址；`10GigabitNet_REG_BASE_ADDRESS=0` 未启用【C】 |
| `is_net` | `true` | 网络传输【B】 |
| `frame_header_bytes_number` | `64` | 每帧/段声明 64 字节帧头；实际布局未公开【B/C】 |
| `is_EEProm` | `false` | 不用 EEPROM 通道【B】 |
| `BOARD_TYPE` | `1` | 板卡类型枚举（未确认具体型号）【B/C】 |
| `is_REF_Clock` / `is_EXT_Clock` | `true` / `false` | 使用参考时钟，不用外部采样时钟【B】 |
| `CoupleType` | `DC` | 直流耦合【B】 |
| 采集模式支持 | `is_FiniteMulti=true`、`is_FiniteSingle=true`、`is_InfiniteMulti=true`、`is_InfiniteSingle=false` | 有限点多次/单次、无限点多次可用【B】 |
| 其他 | `DRIVER_SET_DELAY_MS=3000`、`GigabitNet_BYTE_ALIGNMENT=64`、`DDR_Memory=4096`、`dmatotalbytes=1000`、`DMA_parameter_sleep=0` | 初始化/对齐/DMA 参数【B】 |

### 4.4 `QT_Board` 调用方式

- 随包 `QT_Board_MATLAB.h` 是 MATLAB 专用头文件，声明约 100+ 个 `QT_Board*` 导出，并在尾部注明：

  ```c
  // MATLAB extension: delayed trigger API from QT_Board_Project.h
  QT_BOARD_PRODUCT_API int QT_BoardSetTriggerDelay(unsigned int unCardIndex, uint32_t delayTime);
  ```

  说明该头文件是在通用 `QT_Board.h` / `QT_Board_Project.h` 基础上为 MATLAB 项目补充导出【B】；
- `QT_Board_thunk_pcwin64.dll` 只导出 50 个按签名命名的通用 thunk（如 `int32uint32int32uint32Thunk`），仅导入 `KERNEL32.dll` / `msvcrt.dll`，不直接导入 `QT_Board.dll`——这是 MATLAB `loadlibrary` 的标准 thunk 机制，真正的 `QT_Board.dll` 由 MATLAB 运行时加载【B】；
- 由包装文件名可确认的调用面：`OpenLog/OpenBoard/CloseBoard/CloseLog`、`SetTheDataType`、`SetTheExtractionMultiple`、`SetupRecClockMode`、`SetupRecChannelMode`、`SetupRecWorkingMode`、`SetAFE*`、`SetFanControl`、`SetupTrigRec*`、`SetupModeRecStdSingle/Multi`、`SetupModeRecFIFOSingle/Multi`、`SetRecCollectDelay`、`SetupRecCollectData`、`SetupRecGetData`、`ObtainTheADCBlockNum`、`SetSoftwareReset`【B（文件名）/A（头文件）】。

### 4.5 版本口径（三个层次，均已核实）

| 口径 | 值 | 位置/来源 |
| --- | --- | --- |
| `QT_Board.dll` 文件版本 | `2.0.1.0`（公司/产品字段为 `TODO: <公司名>` / `TODO: <产品名>`） | PE 版本资源【B】 |
| `QT_BoardGetSDKVersion` 运行时字符串 | `2.1.6_alpha` | 直接调用【B】 |
| `config/version/sdk_version_meta`（SDK 目录） | `1.0.0.0` | 文件【B】 |
| `config/version/sdk_version_meta`（HRAS） | `8e8a01d2257…` + `2025-06-16 11:51:56`（疑似 Git 提交 + 构建时间） | 文件【B/C】 |
| `qt_acq.dll` 版本字符串 | `0.1.0` | `qt_acq_version()` 实测【B】 |

结论：**文件版本、运行时字符串与元数据三套口径互不一致**；由于二进制哈希一致，应以“同一套二进制、不同打包元数据”理解，不能互相替代【B/C】。

---

## 5. 处理链路与算法边界

### 5.1 模块名与职责（仅名称可读）

| 模块 | 可推断职责 | 证据 |
| --- | --- | --- |
| `ADC_start.m` | 配置并启动采集 | 【B】文件名 |
| `ADC_acquisition.m` | 单次采集 | 【B】 |
| `ADC_multiple_delay_acquisition.m` | 多次/带延时采集 | 【B】 |
| `adc_decode_std_multi_buffer.m` | 多段标准采集缓冲区解码（多段布局、通道拆分） | 【B】 |
| `adc_validate_*`、`test_ADC_*` | 入参校验与自测（随包存在测试脚本） | 【B】 |
| `OFDR_process_segments.m` | 分段 OFDR 处理（P/S/A → 谱） | 【B】 |
| `OFDR_baseline_il.m` | 基线/插损计算 | 【B】 |
| `OFDR_calibrate_il.m` | 插损斜率/截距校准 | 【B】 |
| `OFDR_summarize_metrics.m` | 指标汇总（界面读数/表格） | 【B】 |
| `extract_baselines.m` | 基线段提取 | 【B】 |
| `remove_points_by_std.m` | 按标准差剔除异常点（对应界面“平均次数”后的统计处理） | 【C】 |

### 5.2 手册给出的处理口径（【A】）

- 分析起点/终点定义 IL 测量区间；
- 平均次数推荐 5~10；
- 回损两种口径：仅峰值、基线+峰值；
- 校准四参数：功率谱密度、插损斜率、插损截距、触发阈值；
- 校准示例：`ILreal = 0.879·ILraw − 0.014`，即斜率 0.879、截距 -0.014。

### 5.3 与 `ofdr` / `detect` SDK 的关系

| 维度 | HRAS 侧 | SDK 侧 | 核实结论 |
| --- | --- | --- | --- |
| 代码依赖 | 无 `ofdr.dll` / `detect.dll` / `qt_acq.dll` 文件，也无相关字符串 | 独立 DLL | **无调用关系**【B】 |
| 输入 | ADC 原始 int16 多通道 → MATLAB 数组 | `ofdr`：P/S/A `double`；`detect`：`dist` + 谱 | 数据形态相近，但转换层在 p-code 内，不可见 |
| 参数命名 | 功率谱密度、插损斜率、插损截距、触发阈值 | `detect`：`nf_q`、`pk_min_prominence_db` 等；`ofdr`：`fs`、`dist_scale` | **参数集不同**，无法一一对应【B】 |
| 算法等价性 | 不可读（p-code） | 可运行、可观测 | **不能假设等价**；只能做行为对照【B/C】 |

### 5.4 校准流程（可直接复用的现场方法）

1. 连接“程控光衰减器 + 光纤跳线 + 主机 + 上位机”；
2. 将分析起点/终点定位到衰减器位置；
3. 依次设置衰减 0/1/2/3/4/5 dB，记录上位机 IL；
4. 对“实际衰减—测得 IL”做一次线性拟合，得到 `ILreal = k·ILraw + b`；
5. 将斜率 k、截距 b 回填校准页；示例 k=0.879、b=-0.014【A】。

---

## 6. SDK 目录核实（运行期补充）

### 6.1 `qt2144-rs`

| 项 | 结果 | 证据 |
| --- | --- | --- |
| 导出 | 9 个：`qt_acq_open/close/configure/start/read/stop/calc_buffer_bytes/last_error/version` | 【B】PE 导出表 |
| 依赖 | `QT_Board.dll`（导入 27 个 `QT_Board*` 函数）+ VC 运行库 | 【B】 |
| 版本 | `qt_acq_version()` 返回 `0.1.0` | 【B】实测 |
| 合法采样率 | `250 / 125 / 50 / 10` MHz；其它值返回 `-3`，消息 `invalid sample rate …` | 【B】实测 |
| 缓冲区公式 | `bytes = rate(MHz) × time(ms) × 1000 × 4 通道 × 2 字节 × 段数`，与实测完全一致（50/8/1 → 3,200,000；250/10/1 → 20,000,000；50/8/4 → 12,800,000） | 【B】实测 |
| 无板卡时 `open(0)` | 返回 `0`（成功）并给出非空句柄；同时 `QT_BoardGetDeviceCnt()=0` | 【B】实测 |
| 野句柄 | `configure(NULL)` → `-4`；`configure(0xDEAD)` → **访问违例崩溃** | 【B】实测 |
| 干净环境加载 | 失败：`pthreadVC2.dll` 依赖 `MSVCR100.dll`；补入 `msvcr100.dll` 后 `QT_Board.dll`、`qt_acq.dll` 均加载成功 | 【B】实测 |

`QT_Board.dll` 的导入依赖（PE 导入表）：`QT_Board_PCIe.dll`、`QT_Board_Net.dll`、`QT_Board_10Net.dll`、`elog.dll`、`pthreadVC2.dll`、`libfftw3-3.dll`、`MSVCP140.dll`、`SETUPAPI.dll`、`SHLWAPI.dll`、`VCRUNTIME140.dll`、UCRT API 集【B】。其中 `pthreadVC2.dll` 又依赖 `MSVCR100.dll`（VS2010 运行库）【B】。

`QT_Board.dll` 共 384 个导出（含 `cJSON_*` 全套 JSON 解析函数，用于读取 `config/*.json`）【B】。

### 6.2 `ofdr-0.1.1-release`

| 项 | 结果 | 证据 |
| --- | --- | --- |
| 导出 | 3 个：`ofdr_config_default`、`ofdr_process`、`ofdr_result_free` | 【B】 |
| 默认配置 | `fs = 250,000,000 Hz`，`dist_scale = 1.0` | 【B】实测 |
| 实测（N=8192，`fs=50e6`） | `len=4097`（N/2+1）；`freq: 0…25 MHz`，步进 `6103.52 Hz`；`dist: 0…374.798 m`，步进 `0.0915033 m`；谱峰位于 `dist≈22.51 m` | 【B】 |
| 错误路径 | `len=0` 或空指针 → `error=-1`，`len=0` | 【B】 |
| 实现语言 | Rust（字符串含 `rustc`、`cargo`、`*.rs`），导出标准 C ABI | 【B】 |

### 6.3 `detect_1.0`

| 项 | 结果 | 证据 |
| --- | --- | --- |
| 导出 | 4 个：`detect_config_default`、`detect_process`、`detect_process_db`、`detect_result_free` | 【B】 |
| 默认配置 | `nf_q=85`、`nf_radius_m=0.1`、`pk_baseline_win=100`、`pk_min_prominence_db=10`、`pk_min_distance_m=0.5`、`pk_exclude_radius_m=0.1`、`pk_seg_percentile=95`、`pk_last_seg_tail_m=5` | 【B】实测，与 README 一致 |
| 合成谱实测（3 峰：2/5/8 m） | `n_peaks=3`、`n_segments=4`、`n_events=3`；事件 1 的 `il=NaN`（无前基线），事件 2/3 `il=0`；`rl=-30 dB`；段噪声顶 `-60 dB` | 【B】 |
| 错误路径 | `len=0` → `error=-1` | 【B】 |
| 实现语言 | Rust（同 `ofdr`） | 【B】 |

### 6.4 与既有 SDK 文档的差异

- 既有文档已覆盖导出表、缓冲区公式、64 字节帧头疑点；
- 本次新增：**无板卡 `open` 成功**、**野句柄崩溃**、**MSVCR100 缺失导致 DLL 无法加载**、**合法采样率枚举**（由运行期错误消息确认）；
- 建议后续把上述四条并入 `docs/映讯QT2144_SDK接入说明书.md` 的“已知限制/部署”章节（本报告只记录，不擅自改既有文档）。

---

## 7. 一致性核对汇总

| 核对项 | 结果 | 说明 |
| --- | --- | --- |
| HRAS 是否使用 QT2144 SDK | 是 | 14 个 DLL 随包，13 个与 SDK 目录字节一致 |
| HRAS 是否使用 `qt_acq` | 否 | 无文件、无字符串 |
| HRAS 是否使用 `ofdr` / `detect` | 否 | 无文件、无字符串 |
| 配置是否可直接复用 | 部分 | `Interface`/`Range`/`FunctionalConfiguration` 相同；**AFE 必须逐机校准** |
| 版本是否一致 | 二进制一致 | 文件版本 2.0.1.0 / 运行时 2.1.6_alpha / 元数据 1.0.0.0 或 hash+时间戳，三套口径 |
| 帧头 | 配置声明 64 B | `qt_acq_calc_buffer_bytes` 未计入 64 B，实际是否生效未确认 |
| 手册与安装包 | 命名不一致 | `MyAppInstaller_web.exe` vs `Installer_web.exe` |

---

## 8. 风险与未确认项

| ID | 事实/风险 | 影响 | 建议动作 |
| --- | --- | --- | --- |
| R-01 | 配置声明 64 字节帧头，但布局、归属（帧/段/包）未知 | 多段解码可能错位 | 实测抓取 `out_len` 与边界；登记 TBD-15 |
| R-02 | 多段采集的数据排列（段优先/通道优先/交织）未确认 | P/S/A 取错列会得到错误谱 | 用已知信号上机标定；契约测试先行 |
| R-03 | 网络协议（端口、命令字、粘包/分包）未公开 | 无法独立实现采集端 | 不得臆测；按 TBD-15 走 mock → 契约 → 真机 |
| R-04 | OFDR/IL/RL 算法为 p-code | 无法核实 HRAS 与 SDK 等价；无法复用其口径 | 以 HRAS 为“行为参考”，不可当规范；自研算法需独立验证 |
| R-05 | AFE 校准值逐机不同 | 直接套用 SDK 示例配置会引入偏置/增益误差 | 每台设备实测校准；配置随设备归档 |
| R-06 | `pthreadVC2 → MSVCR100` 缺失 | 干净机器上 `QT_Board.dll` 无法加载，采集全链路不可用 | 发行说明列明 VC++ 2010 运行库；启动自检给出可读错误 |
| R-07 | MCR R2023a 依赖、Web 安装需联网/管理员 | HRAS 部署门槛高；离线环境不可安装 | 若需交付，提前准备离线 MCR 包 |
| R-08 | 安装包未签名、无版本资源 | 无法校验来源/完整性 | 发行侧做哈希清单与签名 |
| R-09 | `qt_acq_open` 无板卡仍成功 | 误判“已连接”，后续 configure/read 才失败 | 接入层以 `QT_BoardGetDeviceCnt()` + 后续状态双重判定 |
| R-10 | 野句柄导致访问违例 | 上层错误恢复可能拖垮进程 | 适配器只使用自己创建的句柄；异常隔离 |
| R-11 | 随包无 `QTXdma.dll`，但 `QT_Board_PCIe.dll` 提供 `QTXdma*` | PCIe 路径缺驱动组件，无法从该包部署 | 若选 PCIe，需向厂商索取完整驱动包 |
| R-12 | `QT_Board_10Net` 依赖 `wpcap.dll`，抓包需 WinPcap/npcap 驱动 | 仅拷贝 DLL 不足以完成 10G 采集 | 部署清单区分“DLL”与“驱动”；管理员安装 |
| R-13 | 手册文件名与实物不一致 | 现场按手册找不到安装包 | 交付前统一命名或补说明 |

---

## 9. 对本项目（OFDR 平台）的建议

1. **把 HRAS 当“参考实现/参考 UI”，不当规范**：其算法为 p-code，无法核实；任何“照抄”结论都必须登记为推断。
2. **协议未确认前不实现**：64 B 帧头、多段布局、网络端口/命令字均属 TBD-15；代码只允许“明确拒绝/空实现”，不得写死猜测值。
3. **适配器边界**：厂商类型、`DllImport`、句柄、协议字节结构只放 `OFDR.Infrastructure`；上层只经 `IScanSource`。
4. **接入顺序**：mock 服务端 → 契约测试 → 真机冒烟；`open` 成功不作为连接判据，需 `GetDeviceCnt` 与首帧双重确认。
5. **部署清单**：`QT_Board.dll` 全套 + `QT_Board_*` 传输 DLL + `elog` + `pthreadVC2` + `libfftw3-3` + `MSVCR100` +（10G 路径）`wpcap`/`Packet` + 驱动；缺一即启动失败，需给出可读错误。
6. **采样率与缓冲区**：只接受 250/125/50/10 MHz；缓冲按 `rate×time×1000×4×2×seg` 计算，再额外预留 64 B/段与对齐余量（是否必要待实测）。
7. **校准数据随设备落盘**：`AFE.json` 属逐机资产，不进入源码/发行包；配置快照与 `ComputeId()` 绑定。

---

## 附录 A：关键证据索引

| 证据 | 位置/值 |
| --- | --- |
| 安装包内应用清单 | `appdata/products/HRAS_9000 1.0 platform.xml`（`releaseFamily=R2023a`、`productNumber=5000`） |
| 安装器清单 | `ApplicationInstallerManifest.xml`（默认目录 `%ProgramFiles%\HRAS_9000\`） |
| MCR 说明 | `application/readme.txt`（R2023a、管理员权限） |
| CTF 元数据 | `.META/manifest.xml`（`mcr-major-version=9`、`mcr-minor-version=14`） |
| 加密头 | `.m` 与 `matlab/document.xml` 首部 `V2MCC8000MEC2000MCR2000` |
| 板卡配置 | `fsroot/HRAS_9000/ADC/QT2144_Dll/config/QT2144/FunctionalConfiguration.json` |
| 接口配置 | `.../config/Interface.json` |
| SDK 元数据 | 参考目录 `config/version/sdk_version_meta` = `1.0.0.0`；HRAS 内为 hash + `2025-06-16 11:51:56` |
| 运行时版本 | `QT_BoardGetSDKVersion()` = `2.1.6_alpha`；`qt_acq_version()` = `0.1.0` |
| 依赖链 | `QT_Board.dll → pthreadVC2.dll → MSVCR100.dll`；`QT_Board_10Net.dll → wpcap.dll` |
| 运行期公式 | `qt_acq_calc_buffer_bytes(50,8,1)=3,200,000`；`(250,10,1)=20,000,000` |

## 附录 B：复现要点

1. 解析安装包：定位 `PK\x03\x04` 本地头 → 解出 `application/HRAS_9000.exe` → 在其内部定位 CTF 的中央目录（`PK\x01\x02`）→ 按条目解压；
2. 解析 PDF：`pypdf` 逐页 `extract_text()`；
3. 运行 SDK：把 `qt2144-rs` 复制到临时目录，补 `msvcr100.dll`，`os.add_dll_directory()` 后 `ctypes.CDLL`；
4. 采集验证：`qt_acq_version()`、`qt_acq_calc_buffer_bytes()`；`QT_BoardGetDeviceCnt()` 确认无板卡；
5. 处理验证：构造 8192 点正弦 P/S/A 调 `ofdr_process`；构造 3 峰 dB 谱调 `detect_process_db`；
6. 全程不得修改 `references/`，临时产物放系统临时目录。

---

> 说明：本报告仅用于内部技术评估与接入设计。`references/` 中的厂商二进制、p-code 与手册不作为发行物复制；涉及硬件协议、帧格式、校准归属的结论必须在真机实测并与厂商确认后，才能进入 SRS/SDD。
