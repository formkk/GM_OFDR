"""曲线预处理：裁剪、重采样、归一化与训练期增广。

物理口径（LGA50-D）：index × 185.0 µm = 米，X 轴为物理距离。
index < 200（约 3.7 cm）为设备内部反射，不随被测链路变化，必须裁掉。
"""

from __future__ import annotations

import numpy as np

STEP_M = 185.0e-6


def resample_frame(
    y: np.ndarray,
    start_m: float,
    end_m: float,
    points: int,
    step_m: float = STEP_M,
    shift_m: float = 0.0,
) -> np.ndarray:
    """按物理距离裁剪并线性重采样到固定点数。shift_m 用于模拟距离轴偏移。"""
    pos = (start_m + shift_m + np.linspace(0.0, end_m - start_m, points)) / step_m
    return np.interp(pos, np.arange(y.size), y).astype(np.float32)


def normalize(x: np.ndarray, mode: str = "ref") -> np.ndarray:
    """归一化。ref=以 5% 分位为噪声底；zscore=逐帧零均值单位方差。"""
    if mode == "zscore":
        return ((x - x.mean()) / (x.std() + 1e-9)).astype(np.float32)
    ref = np.percentile(x, 5)
    return ((x - ref) / 6.0).astype(np.float32)


def detrend(x: np.ndarray, order: int) -> np.ndarray:
    """去除 low-order 多项式基线，抑制设备窗函数/会话电平造成的缓变包络。"""
    if order <= 0:
        return x
    t = np.linspace(-1.0, 1.0, x.size)
    return (x - np.polyval(np.polyfit(t, x, order), t)).astype(np.float32)


def augment(
    x: np.ndarray,
    rng: np.random.Generator,
    gain_db: float = 2.0,
    noise_db: float = 0.3,
    max_shift_m: float = 2e-3,
    step_m: float = STEP_M,
) -> np.ndarray:
    """训练期增广：随机增益、加性噪声、亚毫米级距离轴平移与轻度平滑。"""
    out = x + rng.uniform(-gain_db, gain_db)
    out = out + rng.normal(0.0, noise_db, out.shape)
    if max_shift_m > 0:
        k = int(round(rng.uniform(-max_shift_m, max_shift_m) / step_m))
        k = max(1, min(k, x.size // 10))
        out = np.interp(
            np.arange(out.size) + k, np.arange(out.size), out,
            left=out[0], right=out[-1],
        )
    if rng.random() < 0.3:
        out = np.convolve(out, np.ones(3) / 3.0, mode="same")
    return out.astype(np.float32)


def make_input(y: np.ndarray, start_m: float, end_m: float, points: int,
               rng: np.random.Generator | None = None) -> np.ndarray:
    """单帧 → 模型输入。rng 非空时启用训练期增广。"""
    shift = 0.0
    if rng is not None:
        shift = float(rng.uniform(-2e-3, 2e-3))
    x = resample_frame(y, start_m, end_m, points, shift_m=shift)
    if rng is not None:
        x = augment(x, rng)
    return normalize(x)
