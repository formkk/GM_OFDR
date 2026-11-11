# OFDR 距离域曲线指纹识别与比对（评估原型）

用 OFDR 距离域曲线做光纤/链路指纹评估：联讯 5 峰切 4 段，按相对坐标提取低频 PSD 特征；LGA50 数据作为对照，1D-CNN 为早期基线。

- 评估方案：[docs/cnn/评估方案.md](../docs/cnn/评估方案.md)
- 评估结论：[docs/cnn/评估报告.md](../docs/cnn/评估报告.md)
- 落地评估：[docs/cnn/落地评估.md](../docs/cnn/落地评估.md)（低频 PSD、多帧平均、阈值与阶段路线）
- 数据来源：`../devices/lga50/capture/raw_1m.bin`、`../devices/lga50/capture/raw_stream_01.bin`（只读，不复制、不修改）
- 联讯数据：`../products/gmix/results/raw-50mhz-8ms-1seg.bin` 等 raw（int16 4 通道交织），经 `products/gmix/runtime/ofdr.dll` 离线转距离域曲线（只引用，不复制）

## 环境

```powershell
$py = "C:\Users\<user>\AppData\Local\Programs\Python\Python312\python.exe"
& $py -m venv .venv
.\.venv\Scripts\python.exe -m pip install numpy
# 固定 2.6.0 且走 CPU 源：更高版本的 CPU wheel 依赖本机缺失的 VCRUNTIME140_THREADS.dll
.\.venv\Scripts\python.exe -m pip install torch==2.6.0 --index-url https://mirror.nju.edu.cn/pytorch/whl/cpu
```

## 运行

```powershell
# 数据集体检
.\.venv\Scripts\python.exe src\inspect_frames.py ..\devices\lga50\capture\raw_1m.bin ..\devices\lga50\capture\raw_stream_01.bin

# E1：全量程（含末端反射/长度线索）
.\.venv\Scripts\python.exe src\train_eval.py --name full --crop-start 0.05 --crop-end 3.0

# E2：公共量程（去掉长度线索，只考背向散射指纹）
.\.venv\Scripts\python.exe src\train_eval.py --name common --crop-start 0.05 --crop-end 0.9

# E3：原始曲线余弦基线（不训练）
.\.venv\Scripts\python.exe src\train_eval.py --name full_raw --baseline raw --crop-start 0.05 --crop-end 3.0

# 两段抓包的指纹码比对（需先跑 E1 或 E2 生成 model.pt）
.\.venv\Scripts\python.exe src\compare.py --model runs\full\model.pt `
    --a ..\devices\lga50\capture\raw_1m.bin --b ..\devices\lga50\capture\raw_stream_01.bin

# 联讯：5 峰切 4 段，评估每段信噪比与跨会话可重复性
.\.venv\Scripts\python.exe src\analyze_gmix.py ..\products\gmix\results\raw-50mhz-8ms-1seg.bin
.\.venv\Scripts\python.exe src\segment_gmix.py `
    ..\products\gmix\results\raw-50mhz-8ms-1seg.bin ..\products\gmix\results\repeat_50mhz.bin

# 联讯：同一末段在 20 帧/多配置下的稳定性
.\.venv\Scripts\python.exe src\last_segment_compare.py `
    --dir ..\products\gmix\investigations\signal-drift\raw_batch1 --rate-mhz 50
.\.venv\Scripts\python.exe src\last_segment_compare.py --rate-from-name --files `
    ..\products\gmix\results\raw-10mhz-1ms-1seg.bin ..\products\gmix\results\raw-50mhz-8ms-1seg.bin `
    ..\products\gmix\results\raw-125mhz-10ms-1seg.bin ..\products\gmix\results\raw-250mhz-10ms-1seg.bin

# 联讯：相对坐标 + 对齐 + 特征值可行性（只用 50MHz/8ms）
.\.venv\Scripts\python.exe src\relative_features.py `
    --dir ..\products\gmix\investigations\signal-drift\raw_batch1 `
    --extra ..\products\gmix\results\raw-50mhz-8ms-1seg.bin ..\products\gmix\results\repeat_50mhz.bin `
    --per-curve-norm

# 分箱统计 vs 低频 PSD（网格鲁棒特征复评）
.\.venv\Scripts\python.exe src\feature_eval.py `
    --dir ..\products\gmix\investigations\signal-drift\raw_batch1 `
    --extra ..\products\gmix\results\raw-50mhz-8ms-1seg.bin ..\products\gmix\results\repeat_50mhz.bin `
    --per-curve-norm --kinds bins,low_psd --grid-mm 20

# 对齐尝试：用 aux 通道相位做等光频重采样（阴性结果，留作证据）
.\.venv\Scripts\python.exe src\kclock_probe.py `
    ..\products\gmix\investigations\signal-drift\raw_batch1\frame_01.bin `
    ..\products\gmix\investigations\signal-drift\raw_batch1\frame_02.bin --ppf 16

# 落地评估：多帧平均、扰动、阈值、耗时
.\.venv\Scripts\python.exe src\deploy_eval.py
```

## 输出

- `runs/<name>/metrics.json`：每折指标与均值±标准差
- `runs/<name>/model.pt`：第 0 折模型、二值化偏置、判定阈值
- `data/*.npy`：抓包解析缓存（`CNN/data/` 不入库）

## 边界

- 仅评估，不接入 GM-IX / LGA50 产品线，不改统一数据契约。
- 现有数据只有 2 条 LGA50 链路、各一次会话；`full` 的高分主要来自长度线索，指纹能力以 `common` 为准。
- 联讯 5 峰可切 4 段且 SNR 26~29 dB；细结构只在 dist 步距差 <0.003 mm 时可复现，跨网格失配；改用相对坐标 + 每段去电平 + 低频 PSD（10~20 mm 网格）后，可行性初测 AUC 0.91~0.98、EER 7.4%~15.2%，仍需多光纤多会话复核。
- 抓包原始文件只读；派生数据与训练产物均在本目录 `data/`、`runs/` 下。
