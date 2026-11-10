"""数据集体检：峰位、噪声底、帧内稳定性和跨链路可分性。

用法：
  python inspect_frames.py ../data/captures/*.bin
输出用于判断现有抓包能否支撑指纹实验，以及确定裁剪区间。
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np

from lga50_io import parse_raw_frames

STEP_M = 185e-6
INTERNAL_END = 200  # index < 200 为设备内部反射区，不属于被测链路


def top_peaks(y: np.ndarray, start: int = INTERNAL_END, top: int = 6) -> list[tuple[int, float]]:
    """在 start 之后找局部极大，按幅度返回前 top 个（最小间距 20 点）。"""
    seg = y[start:]
    idx = np.where((seg[1:-1] > seg[:-2]) & (seg[1:-1] >= seg[2:]))[0] + 1
    idx = idx[np.argsort(seg[idx])[::-1]]
    picked: list[int] = []
    for i in idx:
        if all(abs(i - j) >= 20 for j in picked):
            picked.append(int(i + start))
        if len(picked) == top:
            break
    return [(i, float(y[i])) for i in picked]


def corr_stats(a: np.ndarray, crop: slice) -> tuple[float, float]:
    """抽样帧两两 Pearson 相关的最小值与均值。"""
    x = a[:: max(1, a.shape[0] // 20), crop]
    x = (x - x.mean(axis=1, keepdims=True)) / (x.std(axis=1, keepdims=True) + 1e-9)
    c = x @ x.T / x.shape[1]
    iu = np.triu_indices(c.shape[0], k=1)
    return float(c[iu].min()), float(c[iu].mean())


def describe(path: str) -> np.ndarray:
    a = parse_raw_frames(path)
    n, m = a.shape
    print(f"\n=== {Path(path).name} ===  帧数={n} 点数={m}")
    print(f"值域 {a.min():.2f} ~ {a.max():.2f} dB；远端噪声底 "
          f"{np.median(a[:, m // 2:]).mean():.2f} dB")

    peaks = top_peaks(a.mean(axis=0))
    print("平均曲线主峰（index > 200）:")
    for i, v in peaks:
        print(f"  index={i:6d} dist={i * STEP_M:.4f} m level={v:8.2f} dB")

    # 末端反射峰跨帧位置稳定性
    region = a[:, 4000:14000]
    end_idx = region.argmax(axis=1) + 4000
    print(f"末端峰 index: median={np.median(end_idx):.0f} std={end_idx.std():.2f} "
          f"min={end_idx.min()} max={end_idx.max()}")
    print(f"末端峰电平: mean={a[np.arange(n), end_idx].mean():.2f} "
          f"std={a[np.arange(n), end_idx].std():.2f} dB")

    for label, crop in (("全段", slice(0, m)), ("链路段0.05-3m", slice(270, 16216))):
        lo, mean = corr_stats(a, crop)
        print(f"帧间相关({label}): 最小 {lo:.4f} 均值 {mean:.4f}")
    print(f"逐帧整体电平 std: {a.std(axis=1).mean():.3f} dB")
    return a


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("paths", nargs="+")
    args = ap.parse_args()
    curves = [describe(p) for p in args.paths]
    if len(curves) >= 2:
        print("\n=== 跨文件（链路）可分性 ===")
        for i in range(len(curves)):
            for j in range(i + 1, len(curves)):
                a = curves[i][:, 270:16216].mean(axis=0)
                b = curves[j][:, 270:16216].mean(axis=0)
                c = np.corrcoef(a, b)[0, 1]
                print(f"{Path(args.paths[i]).name} vs {Path(args.paths[j]).name}: "
                      f"平均曲线相关 {c:.4f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
