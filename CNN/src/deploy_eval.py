"""低频 PSD 指纹的落地评估：多帧平均、扰动、阈值与耗时。

只用 50 MHz / 8 ms 数据。特征 = 每段去电平 + 低频 PSD（20 mm 物理网格）。
输出：不同平均帧数下的 AUC/EER/4 段识别、增益/倾斜/噪声扰动、距离分位阈值、单段耗时。
"""

from __future__ import annotations

import glob as globmod
import time
from itertools import combinations
from pathlib import Path

import numpy as np

import feature_eval as fe

SEG_LEN = [d1 - d0 - 2 * fe.GUARD for d0, d1 in zip(fe.PEAKS, fe.PEAKS[1:])]


def build_features(curves: np.ndarray, grid_mm: float = 20.0) -> np.ndarray:
    fe.GRID_MM = grid_mm
    rows = []
    for s in range(4):
        for i in range(curves.shape[0]):
            c = curves[i, s]
            c = (c - c.mean()) / (c.std() + 1e-9)
            rows.append(fe.featurize(c, "low_psd", 0, SEG_LEN[s]))
    x = np.array(rows)
    return (x - x.mean(0)) / (x.std(0) + 1e-9)


def pair_metrics(x: np.ndarray, steps: np.ndarray, n: int) -> dict:
    pos, neg = [], []
    for i, j in combinations(range(n), 2):
        for s in range(4):
            pos.append(np.linalg.norm(x[s * n + i] - x[s * n + j]))
        for s in range(4):
            for t in range(4):
                if s != t:
                    neg.append(np.linalg.norm(x[s * n + i] - x[t * n + j]))
    pos, neg = np.array(pos), np.array(neg)
    a, e = fe.auc_eer(pos, neg)
    return {"auc": a, "eer": e, "pos": pos, "neg": neg,
            "d95": float(np.percentile(pos, 95)),
            "n95": float(np.percentile(neg, 5))}


def rank1_groups(x: np.ndarray, n: int) -> float:
    segs = np.repeat(np.arange(4), n)
    scan = np.tile(np.arange(n), 4)
    acc = []
    for i in range(n):
        tr, te = scan != i, scan == i
        cents = np.stack([x[tr & (segs == s)].mean(0) for s in range(4)])
        cents /= np.linalg.norm(cents, axis=1, keepdims=True) + 1e-9
        v = x[te] / (np.linalg.norm(x[te], axis=1, keepdims=True) + 1e-9)
        acc.append(((v @ cents.T).argmax(1) == segs[te]).mean())
    return float(np.mean(acc))


def main() -> int:
    files = (sorted(globmod.glob(str(Path(r"c:\Mycode\GM-OFDR\products\gmix\investigations\signal-drift\raw_batch1") / "frame_*.bin")))
             + [r"c:\Mycode\GM-OFDR\products\gmix\results\raw-50mhz-8ms-1seg.bin",
                r"c:\Mycode\GM-OFDR\products\gmix\results\repeat_50mhz.bin"])
    curves, steps = fe.load_scans(files, Path(r"c:\Mycode\GM-OFDR\CNN\data\scans_50mhz_4seg.npz"))
    print(f"采集 {len(files)} 次（20 连续 + 2 独立）")

    x0 = build_features(curves)
    r0 = pair_metrics(x0, steps, len(files))
    print(f"\n基线（单帧，20mm PSD）：AUC={r0['auc']:.3f} EER={r0['eer']:.3f} "
          f"同段 d95={r0['d95']:.2f}  异段 d5={r0['n95']:.2f}")

    print("\n多帧平均（连续帧分块，dB 域平均后重算 PSD）：")
    print(f"{'N帧':>4}{'组数':>5}{'AUC':>7}{'EER':>7}{'4段识别':>9}")
    base = curves[:20]
    base_steps = steps[:20]
    for n_avg in (1, 2, 4, 5):
        n_grp = 20 // n_avg
        grp_curves = np.stack([
            np.stack([base[k * n_avg:(k + 1) * n_avg].mean(0)[s] for s in range(4)])
            for k in range(n_grp)
        ])
        grp_steps = np.array([base_steps[k * n_avg:(k + 1) * n_avg].mean() for k in range(n_grp)])
        x = build_features(grp_curves)
        m = pair_metrics(x, grp_steps, n_grp)
        print(f"{n_avg:>4}{n_grp:>5}{m['auc']:>7.3f}{m['eer']:>7.3f}{rank1_groups(x, n_grp):>9.3f}")

    print("\n扰动鲁棒性（全部 22 次采集，单帧）：")
    print(f"{'扰动':<18}{'AUC':>7}{'EER':>7}")
    for name, fn in [
        ("无", lambda c: c),
        ("增益 +3 dB", lambda c: c + 3.0),
        ("倾斜 4 dB", lambda c: c + np.linspace(-2, 2, c.size)),
        ("倾斜 8 dB", lambda c: c + np.linspace(-4, 4, c.size)),
        ("噪声 σ=0.3 dB", lambda c: c + np.random.default_rng(0).normal(0, 0.3, c.shape)),
    ]:
        pert = np.stack([[fn(curves[i, s]) for s in range(4)] for i in range(len(files))])
        r = pair_metrics(build_features(pert), steps, len(files))
        print(f"{name:<18}{r['auc']:>7.3f}{r['eer']:>7.3f}")

    print("\n单段特征提取耗时：", end="")
    t0 = time.perf_counter()
    for _ in range(200):
        build_features(curves[:1])
    print(f"{(time.perf_counter() - t0) / 200 * 1000:.3f} ms/次采集（4 段）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
