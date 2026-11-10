"""比较多个联讯采集的「同一末段」（6.5016~8.6865 m）是否稳定。

用法：
  python last_segment_compare.py --dir <raw_batch1> --glob "frame_*.bin" --rate-mhz 50
  python last_segment_compare.py --files a.bin b.bin --rate-mhz 50
输出：逐文件末段统计 + 两两相关（原始/平滑/对齐后）。
"""

from __future__ import annotations

import argparse
import glob as globmod
import re
from itertools import combinations
from pathlib import Path

import numpy as np

from gmix_io import load_raw, process

D0, D1 = 6.5016, 8.6865
GUARD = 0.10
N = 4096


def last_segment(dist: np.ndarray, db: np.ndarray) -> np.ndarray:
    m = (dist > D0 + GUARD) & (dist < D1 - GUARD)
    return np.interp(np.linspace(0, 1, N), np.linspace(0, 1, m.sum()), db[m])


def smooth(x: np.ndarray, k: int) -> np.ndarray:
    k = k // 2 * 2 + 1
    return np.convolve(x, np.ones(k) / k, mode="same")


def corr_at(a: np.ndarray, b: np.ndarray, shift: int, margin: int = 200) -> float:
    aa = a[margin:len(a) - margin]
    if shift >= 0:
        bb = b[margin + shift:len(b) - margin + shift]
    else:
        bb = b[margin + shift:len(b) - margin + shift]
    if len(aa) != len(bb) or len(aa) < 100:
        return np.nan
    return float(np.corrcoef(aa, bb)[0, 1])


def best_shift(a: np.ndarray, b: np.ndarray, max_lag: int = 200) -> tuple[int, float]:
    sa, sb = smooth(a, 101), smooth(b, 101)
    lags = range(-max_lag, max_lag + 1)
    vals = [(lag, corr_at(sa, sb, lag)) for lag in lags]
    return max(vals, key=lambda t: t[1])


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir")
    ap.add_argument("--glob", default="frame_*.bin")
    ap.add_argument("--files", nargs="*")
    ap.add_argument("--rate-mhz", type=float, default=50.0)
    ap.add_argument("--rate-from-name", action="store_true",
                    help="从文件名中的 NNmhz 解析采样率")
    ap.add_argument("--label", nargs="*", default=None)
    args = ap.parse_args()

    if args.dir:
        files = sorted(globmod.glob(str(Path(args.dir) / args.glob)))
        labels = [Path(f).stem for f in files]
    else:
        files = args.files or []
        labels = args.label or [Path(f).stem for f in files]
    if not files:
        raise SystemExit("没有输入文件")

    curves, rows = [], []
    for f, name in zip(files, labels):
        rate = args.rate_mhz
        if args.rate_from_name:
            mt = re.search(r"(\d+)mhz", Path(f).name, re.I)
            if mt:
                rate = float(mt.group(1))
        raw = load_raw(f)
        dist, ps = process(raw, fs_hz=rate * 1e6, dist_scale=1.0)
        db = 20.0 * np.log10(np.where(ps > 0, ps, np.nan))
        seg = last_segment(dist, db)
        curves.append(seg)
        win = (dist > 8.55) & (dist < 8.82)
        k = int(np.argmax(db[win]))
        d_peak = float(dist[win][k])
        v_peak = float(db[win][k])
        m = (dist > D0 + GUARD) & (dist < D1 - GUARD)
        tail = db[(dist > D1 + 5) & (dist < D1 + 15)]
        rows.append((name, dist[1] - dist[0], m.sum(), d_peak, v_peak,
                     float(np.median(db[m])), float(db[m].std()),
                     float(np.percentile(db[m], 95) - np.median(tail))))

    print(f"{'文件':<12}{'步距(mm)':>9}{'点数':>7}{'末峰(m)':>10}{'末峰(dB)':>10}"
          f"{'段中位(dB)':>11}{'段std':>7}{'SNR(dB)':>8}")
    for r in rows:
        print(f"{r[0]:<12}{r[1] * 1000:>9.3f}{r[2]:>7}{r[3]:>10.4f}{r[4]:>10.2f}"
              f"{r[5]:>11.2f}{r[6]:>7.2f}{r[7]:>8.2f}")

    print(f"\n末段 {D0:.4f}~{D1:.4f} m（保护带 {GUARD} m，重采样 {N} 点）")
    pos = np.array([r[3] for r in rows])
    lev = np.array([r[4] for r in rows])
    med = np.array([r[5] for r in rows])
    print(f"末峰位置: 均值 {pos.mean():.4f} m  标准差 {pos.std() * 1000:.2f} mm  "
          f"范围 {pos.min():.4f}~{pos.max():.4f}")
    print(f"末峰电平: 均值 {lev.mean():.2f} dB  标准差 {lev.std():.2f} dB")
    print(f"段中位电平: 均值 {med.mean():.2f} dB  标准差 {med.std():.2f} dB")

    pairs = list(combinations(range(len(curves)), 2))
    raw = [corr_at(curves[i], curves[j], 0) for i, j in pairs]
    sm = [corr_at(smooth(curves[i], 101), smooth(curves[j], 101), 0) for i, j in pairs]
    aligned, lags = [], []
    for i, j in pairs:
        lag, _ = best_shift(curves[i], curves[j])
        lags.append(lag)
        aligned.append(corr_at(curves[i], curves[j], lag))
    print(f"\n两两比较（{len(pairs)} 对）：")
    print(f"  原始细结构相关: 均值 {np.nanmean(raw):.4f}  最小 {np.nanmin(raw):.4f}  "
          f"最大 {np.nanmax(raw):.4f}")
    print(f"  平滑~5cm 相关 : 均值 {np.nanmean(sm):.4f}  "
          f"最小 {np.nanmin(sm):.4f}  最大 {np.nanmax(sm):.4f}")
    print(f"  对齐后细结构  : 均值 {np.nanmean(aligned):.4f}  "
          f"最大 {np.nanmax(aligned):.4f}  位移中位 {int(np.median(lags))} 点")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
