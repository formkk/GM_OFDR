"""相对坐标 + 对齐 + 网格鲁棒特征：分箱统计与低频 PSD 的可行性重评估。

只用 50 MHz / 8 ms 数据。每段以 5 峰为锚点归一化为相对坐标 [0,1]，
去 0.1 m 保护带后重采样；特征分两类：

  bins    : B 等分箱的均值 + 标准差
  low_psd : 先把段重采样到固定 10 mm 物理网格（块平均），再用 Hann 窗
            计算 PSD，按物理波长 1.28/0.64/0.32/0.16/0.08/0.04/0.02 m
            分带取对数能量

评测：同段跨采集 vs 异段（AUC/EER），并按 dist 步距差分箱检查网格鲁棒性；
留一采集 4 段识别；20 帧建库 + 2 次独立采集的跨会话识别。
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
POINTS = 2048
WAVELENGTHS = [1.28, 0.64, 0.32, 0.16, 0.08, 0.04, 0.02]
GRID_MM = 10.0


def load_scans(files: list[str], cache: Path) -> tuple[np.ndarray, np.ndarray]:
    """返回 (n_scan, 4, POINTS) 与 dist 步距 (mm)；带缓存。"""
    key = [str(Path(f).resolve()) for f in files]
    if cache.exists():
        z = np.load(cache, allow_pickle=True)
        if list(z["files"]) == key and z["curves"].shape[2] == POINTS:
            return z["curves"], z["steps"]

    curves, steps = [], []
    for f in files:
        dist, ps = process(load_raw(f), fs_hz=50e6, dist_scale=1.0)
        db = 20.0 * np.log10(np.where(ps > 0, ps, np.nan))
        steps.append((dist[1] - dist[0]) * 1000.0)
        row = []
        for d0, d1 in zip(PEAKS, PEAKS[1:]):
            m = (dist > d0 + GUARD) & (dist < d1 - GUARD)
            row.append(np.interp(np.linspace(0, 1, POINTS),
                                 np.linspace(0, 1, m.sum()), db[m]))
        curves.append(row)
    curves = np.stack(curves)
    steps = np.array(steps)
    cache.parent.mkdir(parents=True, exist_ok=True)
    np.savez(cache, curves=curves, steps=steps, files=np.array(key))
    return curves, steps


def block_mean(x: np.ndarray, n_out: int) -> np.ndarray:
    edges = np.linspace(0, x.size, n_out + 1).astype(int)
    return np.array([x[a:b].mean() for a, b in zip(edges[:-1], edges[1:])])


def featurize(curve: np.ndarray, kind: str, bins: int, length_m: float) -> np.ndarray:
    if kind == "bins":
        edges = np.linspace(0, curve.size, bins + 1).astype(int)
        mean = np.array([curve[a:b].mean() for a, b in zip(edges[:-1], edges[1:])])
        std = np.array([curve[a:b].std() for a, b in zip(edges[:-1], edges[1:])])
        return np.concatenate([mean, std])
    if kind == "low_psd":
        step_m = GRID_MM / 1000.0
        n_out = max(32, int(length_m / step_m))
        x = block_mean(curve, n_out)
        x = x - x.mean()
        spec = np.abs(np.fft.rfft(x * np.hanning(n_out))) ** 2
        freqs = np.fft.rfftfreq(n_out, d=step_m)  # cycles / m
        energies = []
        for lam_hi, lam_lo in zip(WAVELENGTHS[:-1], WAVELENGTHS[1:]):
            m = (freqs >= 1.0 / lam_hi) & (freqs < 1.0 / lam_lo)
            energies.append(spec[m].sum())
        return np.log10(np.array(energies) + 1e-12)
    if kind == "combo":
        return np.concatenate([featurize(curve, "bins", bins, length_m),
                               featurize(curve, "low_psd", 0, length_m)])
    raise ValueError(kind)


def auc_eer(pos: np.ndarray, neg: np.ndarray) -> tuple[float, float]:
    scores = np.concatenate([-pos, -neg])
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
          train: np.ndarray, test: np.ndarray) -> float:
    cents = []
    for s in range(4):
        m = train & (segs == s)
        c = feats[m].mean(axis=0)
        cents.append(c / (np.linalg.norm(c) + 1e-9))
    cents = np.stack(cents)
    x = feats[test]
    x = x / (np.linalg.norm(x, axis=1, keepdims=True) + 1e-9)
    return float(((x @ cents.T).argmax(axis=1) == segs[test]).mean())


def evaluate(X: np.ndarray, steps: np.ndarray, n_scan: int, n_extra: int) -> dict:
    segs = np.repeat(np.arange(4), n_scan)
    scan_of = np.tile(np.arange(n_scan), 4)
    pos, neg, pair_dstep = [], [], []
    for i, j in combinations(range(n_scan), 2):
        pair_dstep.append(abs(steps[i] - steps[j]))
        for s in range(4):
            pos.append(np.linalg.norm(X[s * n_scan + i] - X[s * n_scan + j]))
        for s in range(4):
            for t in range(4):
                if s != t:
                    neg.append(np.linalg.norm(X[s * n_scan + i] - X[t * n_scan + j]))
    pos, neg, pair_dstep = np.array(pos), np.array(neg), np.array(pair_dstep)
    a, e = auc_eer(pos, neg)

    # 按步距差分箱的 AUC（正负样本都来自同一批采集对）
    bins = [(0, 0.003), (0.003, 0.01), (0.01, 0.03), (0.03, 0.1)]
    per_bin = []
    for lo, hi in bins:
        ks = np.where((pair_dstep >= lo) & (pair_dstep < hi))[0]
        if ks.size:
            p = np.concatenate([pos[k * 4:(k + 1) * 4] for k in ks])
            n = np.concatenate([neg[k * 12:(k + 1) * 12] for k in ks])
            per_bin.append(auc_eer(p, n)[0])
        else:
            per_bin.append(float("nan"))

    loso = []
    for i in range(n_scan):
        tr = scan_of != i
        te = scan_of == i
        loso.append(rank1(X, segs, tr, te))
    tr = scan_of < n_scan - n_extra
    cross = rank1(X, segs, tr, ~tr)
    return {"auc": a, "eer": e, "loso": float(np.mean(loso)), "cross": cross,
            "per_bin": per_bin, "pos": pos.mean(), "neg": neg.mean()}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=r"..\..\GM-IX\investigations\signal-drift\raw_batch1")
    ap.add_argument("--extra", nargs="*", default=[
        r"..\..\GM-IX\results\raw-50mhz-8ms-1seg.bin",
        r"..\..\GM-IX\results\repeat_50mhz.bin",
    ])
    ap.add_argument("--bins", nargs="*", type=int, default=[16, 32, 64])
    ap.add_argument("--kinds", default="bins,low_psd")
    ap.add_argument("--per-curve-norm", action="store_true")
    ap.add_argument("--grid-mm", type=float, default=10.0, help="低频 PSD 的物理重采样间隔")
    ap.add_argument("--cache", default=str(Path(__file__).resolve().parents[1] / "data" / "scans_50mhz_4seg.npz"))
    args = ap.parse_args()

    global GRID_MM
    GRID_MM = args.grid_mm

    files = sorted(globmod.glob(str(Path(args.dir) / "frame_*.bin"))) + list(args.extra)
    print(f"采集数 {len(files)}（{len(files) - len(args.extra)} 帧连续 + {len(args.extra)} 次独立）")
    curves, steps = load_scans(files, Path(args.cache))
    if args.per_curve_norm:
        mu = curves.mean(axis=2, keepdims=True)
        sd = curves.std(axis=2, keepdims=True) + 1e-9
        curves = (curves - mu) / sd
    lengths = [d1 - d0 - 2 * GUARD for d0, d1 in zip(PEAKS, PEAKS[1:])]
    print(f"dist 步距 {steps.min():.3f}~{steps.max():.3f} mm（极差 {steps.max() - steps.min():.3f}）")

    variants = []
    for kind in args.kinds.split(","):
        if kind == "bins":
            variants += [(f"bins{k}", "bins", k) for k in args.bins]
        elif kind == "combo":
            variants.append(("bins64+psd", "combo", 64))
        else:
            variants.append(("low_psd", "low_psd", 0))

    print(f"\n{'特征':<10}{'AUC':>7}{'EER':>7}{'留一':>7}{'跨会话':>8}"
          f"{'<3µm':>7}{'3-10':>7}{'10-30':>7}{'>30µm':>8}")
    for name, kind, nbins in variants:
        X = np.stack([featurize(curves[i, s], kind, nbins, lengths[s])
                      for s in range(4) for i in range(len(files))])
        X = (X - X.mean(0)) / (X.std(0) + 1e-9)
        r = evaluate(X, steps, len(files), len(args.extra))
        pb = r["per_bin"]
        print(f"{name:<10}{r['auc']:>7.3f}{r['eer']:>7.3f}{r['loso']:>7.3f}{r['cross']:>8.3f}"
              f"{pb[0]:>7.3f}{pb[1]:>7.3f}{pb[2]:>7.3f}{pb[3]:>8.3f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
