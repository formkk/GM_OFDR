"""相对坐标 + 对齐 + 特征提取：评估 50 MHz/8 ms 数据能否形成段级指纹。

数据：20 帧连续采集（signal-drift/raw_batch1）+ 2 次独立采集（raw-50mhz、repeat_50mhz）。
段：5 峰切成 4 段，每段按峰位归一化为相对坐标 [0,1]，去 0.1 m 保护带后重采样。

特征（三种对照）：
  raw  : 重采样曲线本身
  bins : 64 等分箱的均值 + 标准差
  psd  : 频带能量（对数间隔 32 带）

评测：
  1) 同段跨采集 vs 异段的距离分布（AUC / EER）
  2) 留一采集的 4 段识别 rank-1
  3) 用 20 帧建库、两次独立采集做测试的跨会话 rank-1
  4) 同段特征距离与 dist 步距差的关系
"""

from __future__ import annotations

import argparse
import glob as globmod
from itertools import combinations
from pathlib import Path

import numpy as np

from gmix_io import load_raw, process

PEAKS = [0.0005, 1.1692, 3.3377, 6.5016, 8.6865]
GUARD = 0.10
N = 2048


def align_shift(curve: np.ndarray, ref: np.ndarray, max_lag: int = 64) -> np.ndarray:
    """按平滑包络互相关对齐（相对坐标上的整数位移）。"""
    k = 25
    a = np.convolve(curve, np.ones(k) / k, mode="same")
    b = np.convolve(ref, np.ones(k) / k, mode="same")
    best, lag_best = -2.0, 0
    for lag in range(-max_lag, max_lag + 1):
        lo, hi = max(0, -lag), min(N, N - lag)
        if hi - lo < N // 2:
            continue
        c = np.corrcoef(a[lo:hi], b[lo + lag:hi + lag])[0, 1]
        if c > best:
            best, lag_best = c, lag
    return np.roll(curve, lag_best)


def featurize(curve: np.ndarray, kind: str, bins: int = 64, bands: int = 32) -> np.ndarray:
    if kind == "raw":
        return curve.astype(np.float64)
    if kind == "bins":
        edges = np.linspace(0, N, bins + 1).astype(int)
        mean = np.array([curve[a:b].mean() for a, b in zip(edges[:-1], edges[1:])])
        std = np.array([curve[a:b].std() for a, b in zip(edges[:-1], edges[1:])])
        return np.concatenate([mean, std])
    if kind == "psd":
        spec = np.abs(np.fft.rfft(curve - curve.mean())) ** 2
        edges = np.unique(np.round(np.geomspace(1, spec.size, bands + 1)).astype(int))
        return np.log10([spec[a:b].sum() + 1e-12 for a, b in zip(edges[:-1], edges[1:])])
    raise ValueError(kind)


def auc(pos: np.ndarray, neg: np.ndarray) -> tuple[float, float]:
    """距离越小越同源；返回 AUC 与 EER。"""
    scores = np.concatenate([-pos, -neg])  # 距离取负变分数
    labels = np.concatenate([np.ones(pos.size), np.zeros(neg.size)])
    order = np.argsort(scores)
    ranks = np.empty_like(order, dtype=float)
    ranks[order] = np.arange(1, scores.size + 1)
    p = labels == 1
    a = (ranks[p].sum() - p.sum() * (p.sum() + 1) / 2) / (p.sum() * (~p).sum())
    best = 1.0
    for t in np.unique(scores):
        far = ((scores >= t) & (~p)).sum() / (~p).sum()
        frr = ((scores < t) & p).sum() / p.sum()
        best = min(best, max(far, frr))
    return float(a), float(best)


def rank1(feats: np.ndarray, segs: np.ndarray,
          train_mask: np.ndarray, test_mask: np.ndarray) -> float:
    """每段取训练集质心，测试样本按余弦最近邻分类到 4 段。"""
    cents = []
    for s in range(4):
        m = train_mask & (segs == s)
        c = feats[m].mean(axis=0)
        cents.append(c / (np.linalg.norm(c) + 1e-9))
    cents = np.stack(cents)
    x = feats[test_mask]
    x = x / (np.linalg.norm(x, axis=1, keepdims=True) + 1e-9)
    pred = (x @ cents.T).argmax(axis=1)
    return float((pred == segs[test_mask]).mean())


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=r"..\..\products\gmix\investigations\signal-drift\raw_batch1")
    ap.add_argument("--extra", nargs="*", default=[
        r"..\..\products\gmix\results\raw-50mhz-8ms-1seg.bin",
        r"..\..\products\gmix\results\repeat_50mhz.bin",
    ])
    ap.add_argument("--points", type=int, default=2048)
    ap.add_argument("--bins", type=int, default=64)
    ap.add_argument("--align", action="store_true", help="按首个采集的包络对齐")
    ap.add_argument("--per-curve-norm", action="store_true",
                    help="每段曲线先做零均值单位方差，去掉电平/衰减差异，只保留形状")
    args = ap.parse_args()

    global N
    N = args.points
    files = sorted(globmod.glob(str(Path(args.dir) / "frame_*.bin"))) + list(args.extra)
    print(f"采集数 {len(files)}（20 帧连续 + {len(args.extra)} 次独立）")

    curves, steps = [], []
    for f in files:
        dist, ps = process(load_raw(f), fs_hz=50e6, dist_scale=1.0)
        db = 20.0 * np.log10(np.where(ps > 0, ps, np.nan))
        steps.append(dist[1] - dist[0])
        seg4 = []
        for d0, d1 in zip(PEAKS, PEAKS[1:]):
            m = (dist > d0 + GUARD) & (dist < d1 - GUARD)
            c = np.interp(np.linspace(0, 1, N), np.linspace(0, 1, m.sum()), db[m])
            if args.per_curve_norm:
                c = (c - c.mean()) / (c.std() + 1e-9)
            seg4.append(c)
        curves.append(seg4)
    steps = np.array(steps) * 1000  # mm

    # 段优先顺序：索引 = 段号 × 采集数 + 采集号
    flat = np.stack([curves[g][s] for s in range(4) for g in range(len(files))])
    groups = np.tile(np.arange(len(files)), 4)
    segs = np.repeat(np.arange(4), len(files))

    if args.align:
        for s in range(4):
            ref = flat[segs == s][0]
            idx = np.where(segs == s)[0]
            flat[idx] = np.stack([align_shift(flat[i], ref) for i in idx])

    print(f"dist 步距 {steps.min():.3f}~{steps.max():.3f} mm（极差 {steps.max() - steps.min():.3f}）")
    print(f"\n{'特征':<6}{'AUC':>7}{'EER':>7}{'留一识别':>9}{'跨会话':>8}"
          f"{'同段距':>8}{'异段距':>8}")
    for kind in ("raw", "bins", "psd"):
        X = np.stack([featurize(c, kind, args.bins) for c in flat])
        X = (X - X.mean(0)) / (X.std(0) + 1e-9)

        pos, neg, dstep = [], [], []
        for i, j in combinations(range(len(files)), 2):
            for s in range(4):
                a = X[s * len(files) + i]
                b = X[s * len(files) + j]
                pos.append(np.linalg.norm(a - b))
                dstep.append(abs(steps[i] - steps[j]))
            for s in range(4):
                for t in range(4):
                    if s != t:
                        a = X[s * len(files) + i]
                        b = X[t * len(files) + j]
                        neg.append(np.linalg.norm(a - b))
        pos, neg = np.array(pos), np.array(neg)
        a, e = auc(pos, neg)

        # 留一采集
        acc = []
        for i in range(len(files)):
            tr = groups != i
            te = groups == i
            acc.append(rank1(X, segs, tr, te))
        # 跨会话：前 20 帧建库，最后两次测试
        tr = groups < len(files) - len(args.extra)
        te = ~tr
        cross = rank1(X, segs, tr, te)

        print(f"{kind:<6}{a:>7.3f}{e:>7.3f}{np.mean(acc):>9.3f}{cross:>8.3f}"
              f"{pos.mean():>8.2f}{neg.mean():>8.2f}")

    print("\n同段特征距离 vs dist 步距差：")
    X = np.stack([featurize(c, "bins", args.bins) for c in flat])
    X = (X - X.mean(0)) / (X.std(0) + 1e-9)
    rows = []
    for i, j in combinations(range(len(files)), 2):
        for s in range(4):
            rows.append((abs(steps[i] - steps[j]),
                         np.linalg.norm(X[s * len(files) + i] - X[s * len(files) + j])))
    rows = np.array(rows)
    print(f"  Pearson r = {np.corrcoef(rows[:, 0], rows[:, 1])[0, 1]:.3f}（bins 特征）")
    for lo, hi in [(0, 0.003), (0.003, 0.01), (0.01, 0.03), (0.03, 0.1)]:
        m = (rows[:, 0] >= lo) & (rows[:, 0] < hi)
        if m.sum():
            print(f"  步距差 {lo:.3f}~{hi:.3f} mm: {m.sum():3d} 对，平均距离 {rows[m, 1].mean():.2f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
