# 数据资产清单

> 本文件登记**不进入 git** 的原始数据与关键派生数据，用于干净克隆后的完整性与来源核对。
> 最后更新：2026-11-11。

## 1. 规则

1. 原始抓包、实机结果、运行时厂商 DLL、CNN 派生数据不入库；只入库脚本、文档、夹具与清单。
2. 新增或替换原始数据时，必须同步更新本表的**字节数、SHA256、采集参数、帧数**。
3. 校验命令：

```powershell
Get-FileHash -Algorithm SHA256 <文件> | Format-List
```

4. 哈希不一致时，先确认文件是否被重新采集或截断，不得直接覆盖历史结论。

## 2. LGA50-D 抓包（`devices/lga50/capture/`）

采集口径：`RAW_DATA_ON` 原始曲线流；82153 点/帧；185.0 µm/点；单帧 821531 字节；基线帧率 17~19 fps；X 轴为物理距离。协议依据见 [LGA50-D_远程控制协议分析.md](lga50/LGA50-D_远程控制协议分析.md)。

| 文件 | 字节 | SHA256 | 内容 |
|---|---:|---|---|
| `raw_1m.bin` | 28,693,722 | `432587137715E6C85EAEEB6E6D9C49B0662A3784C5E81A03A837667B333E1AD9` | 1 m 尾纤，34 帧 |
| `raw_stream_01.bin` | 77,221,561 | `100A46F7FD8333CB6BC464E66EADEC66B157CDC70BF48C646975F9A287857BC6` | 2 m 尾纤，93 帧 |
| `result_stream_mode1.bin` | 1,908 | `D4CEAB6427499A8442F8B72E34B6D2B99A1D766C42A3E1F3E26D84EACD2FAF01` | 结果帧流（`MODE_1`），25 字节/帧 |
| `delay1000.bin` | 2,938 | `DFC8DBE4FF0023E23757EF30F107D69D428FF30B43F3357C88A6D87741AEA331` | `DELAY_1000` 下 6 秒结果帧 |
| `fiberstart0.bin` | 1,983 | `2785F038DE16F067A0534488D30098ED7016DB39EE9814B8F15CFCDF5BF64A54` | 起点切换为 0 的过渡帧 |
| `mode2_start1.bin` | 1,963 | `A8B96351B414D206AF9F9A9CFD8D4DA476EAE293D30747E2D2DC9B923AFB2F80` | `MODE_2` + 起点 1.000 结果帧 |
| `session_1m_raw.bin` | 0 | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` | 空文件（会话未产出），保留为失败证据 |

## 3. GM-IX 实机原始帧（`products/gmix/results/`）

格式：int16 小端、4 通道交织 `[ch0=P, ch1=S, ch2=A, ch3 忽略]`；单帧字节数 = `采样率(MHz) × 时长(ms) × 8000`；软件触发，`trigger_delay = 1 ms`（文件名另有标注除外）。

### 3.1 单段采集

| 文件 | 字节 | SHA256 | 参数 |
|---|---:|---|---|
| `raw-10mhz-1ms-1seg.bin` | 80,000 | `1E5E48966D02EAF04D7D776656275E9A49D7B8FF36C274CD090A1B3B43ECBEF7` | 10 MHz / 1 ms |
| `raw-50mhz-8ms-1seg.bin` | 3,200,000 | `07CEEC4A401D6745453FC5BB95612252B0F829BD810A8FB17555660FE40924E0` | 50 MHz / 8 ms |
| `raw-125mhz-10ms-1seg.bin` | 10,000,000 | `FE1657598E371287FA4702196606D2CC4A0A6FD16F1409006662410ED92C04FE` | 125 MHz / 10 ms |
| `raw-250mhz-10ms-1seg.bin` | 20,000,000 | `84A6778B0FFC58326E9821889E534DA127694A268EE4D693A06218B7825C79EB` | 250 MHz / 10 ms |
| `repeat_50mhz.bin` | 3,200,000 | `C2B68E5C43DE3C3EF590081FF5E368EA09F8E95CE2DF1CD392622F17B9A84D18` | 50 MHz / 8 ms 重复采集 |

### 3.2 千兆链路复测（`products/gmix/results/gigabit/`）

| 文件 | 字节 | SHA256 | 参数 |
|---|---:|---|---|
| `raw-g10.bin` | 80,000 | `3C9FCFFA01A0C719C48D40C93F526719CA10B31C41388D99F4C9603B216A0BFA` | 10 MHz / 1 ms |
| `raw-g10_10ms.bin` | 800,000 | `012F72F7E5869D751F03BBD8560836B6D10063EA873B5A922328FD95C89E75C1` | 10 MHz / 10 ms |
| `raw-g50_2ms.bin` | 800,000 | `9543843EAEE1E2376D171CD93C385C90BCA71A32A4750E6D2B91719995603FB2` | 50 MHz / 2 ms |
| `raw-g50_delay1.bin` | 3,200,000 | `83AFD815E8981CFBA82A1832753009A72208BE9E2A89486EBC7887A05778A7A1` | 50 MHz / 8 ms / delay 1 |
| `raw-g50_retry.bin` | 3,200,000 | `50358EB24610646664EE7C5722E92448A1E2095FF354884B06E8E5C3F3E041E3` | 50 MHz / 8 ms 复测 |
| `raw-g125.bin` | 10,000,000 | `B17A8812CB1C71D799CFEA55FDB695C74F8310E121640B69458BA2ED25C438BC` | 125 MHz / 10 ms |
| `raw-g250.bin` | 20,000,000 | `EB29EF26CE7BDCD7186BC13C9F33DA8A01479CA35E534CF18AA7E5FF1325D52E` | 250 MHz / 10 ms |
| `raw-g250_50ms.bin` | 100,000,000 | `51CD9F0B82330A5643D9FE3F268500F286A75D004086A0E7CD7BD78CBAA5CC31` | 250 MHz / 50 ms |
| `repeat_g250.bin` | 20,000,000 | `BD36FE39F22066FE966B693EAD1FB4EF341D5A9327F60EA63469D3266C8EBDD8` | 250 MHz / 10 ms 重复采集 |

## 4. 信号漂移验证（`products/gmix/investigations/signal-drift/`）

| 数据 | 规模 | 参数 |
|---|---|---|
| `raw_batch1/`（20 个 `frame_*.bin`） | 64,000,000 字节 | 50 MHz / 8 ms / delay 1，单帧 3,200,000 字节 |
| `ch0_raw_20frames.csv` | 不入库 | 同批次 ch0 原始采样导出 |
| `report.md` / `README.md` | 入库 | 结论与复现步骤 |

## 5. CNN 派生数据（`CNN/data/`、`CNN/runs/`）

- `.npy` 解析缓存、模型权重、训练指标均由脚本从上述原始数据生成，不入库。
- 复现前提：先恢复 `devices/lga50/capture/` 与 `products/gmix/results/` 的 raw，并按本表校验 SHA256；否则只能复跑代码，不能复现评估数值。

## 6. OCS 串口实测（`devices/ocs/capture/`）

采集口径：2026-11-11，COM3 = 8×8 @ 115200 8N1，COM4 = 1×8 @ 9600 8N1；只读命令各 100 次、切换命令各 50 次、8×8 整矩阵 30 次、1×8 长稳 1000 次；结束时已还原光路并回读校验。原始串口日志不入库。

| 文件 | 字节 | SHA256 | 内容 |
|---|---:|---|---|
| `ocs-20261111-145325/raw.jsonl` | 515,130 | `6E99C48060E2AF09CECEFD39ECB1552C40E40C7870646510D5913EDF61EA2E89` | 1898 条 TX/RX 记录（ASCII + 十六进制 + 时延） |
| `ocs-20261111-145325/result.json` | 41,431 | `B54B55294D0D3242946B395296369D1952E2568696ED67738EE8CC6791B06CF6` | 结构化实测结果 |
| `ocs-20261111-145325/report.md` | 8,551 | `8B2A6B02FD97969975E642C74D072C4160F9A25F924770538C2EB08E193D993F` | 自动生成报告 |

结论见 [detection/OCS_协议实测报告_2026-11-11.md](detection/OCS_协议实测报告_2026-11-11.md)；可复现回包样例见 `tests/fixtures/ocs/protocol_frames.txt`。

## 7. 干净克隆的验证边界

| 能力 | 干净克隆可用性 | 依赖 |
|---|---|---|
| GMIX 构建与 `Gmix.Tests` | 可用 | `tests/fixtures/gmix/schema_v02_sample/`（已入库） |
| 预研平台构建与 `OFDR.Verify` | 可用 | `tests/fixtures/platform/`、`tests/fixtures/lga50/`（已入库） |
| LGA50 契约机械检查 | 可用 | `docs/contracts/`（已入库） |
| LGA50 抓包解析 / CNN 评估 | **不可用** | 需恢复本节原始数据并校验哈希 |
| OCS 协议实测复现 | **不可用** | 需真机串口；仅回包样例与结论可查 |
