"""联讯 GM-IX：5 峰 → 4 段光纤的分段可用性评估。

用法：
  python analyze_gmix.py <raw1.bin> [raw2.bin ...]
输出每段的长度、点数、背向散射电平、噪声底与信噪比，用于判断能否切出可用指纹段。
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np

from gmix_io import load_raw, process


def find_peaks(dist: np.ndarray, db: np.ndarray, min_dist_m: float = 0.5,
               min_prom_db: float = 10.0, top: int = 8) -> list[tuple[float, float]]:
    """简化寻峰：局部极大 + 最小间距 + 相对滑动基线的突出度。"""
    k = max(1, int(min_dist_m / (dist[1] - dist[0])))
    idx = np.where((db[1:-1] > db[:-2]) & (db[1:-1] >= db[2:]))[0] + 1
    # 滑动中值基线（窗口 2×min_dist）
    w = 2 * k + 1
    base = np.array([np.median(db[max(0, i - w):i + w]) for i in idx])
    cand = idx[db[idx] - base > min_prom_db]
    cand = cand[np.argsort(db[cand])[::-1]]
    picked: list[int] = []
    for i in cand:
        if all(abs(dist[i] - dist[j]) >= min_dist_m for j in picked):
            picked.append(int(i))
        if len(picked) == top:
            break
    return sorted((float(dist[i]), float(db[i])) for i in picked)


def segment_stats(dist: np.ndarray, db: np.ndarray, peaks: list[tuple[float, float]],
                  guard_m: float = 0.05, noise_tail_m: float = 5.0) -> None:
    step = dist[1] - dist[0]
    last = peaks[-1][0]
    noise = db[(dist > last + noise_tail_m) & (dist < last + noise_tail_m + 10.0)]
    floor = float(np.median(noise))
    print(f"参考噪声底（末峰后 {noise_tail_m:.0f}~{noise_tail_m + 10:.0f} m 中位）：{floor:.2f} dB")

    print(f"{'段':<4}{'起(m)':>8}{'止(m)':>8}{'长度(m)':>9}{'点数':>7}"
          f"{'中位(dB)':>10}{'p95(dB)':>9}{'起伏std':>9}{'SNR(dB)':>9}")
    for n, ((d0, _), (d1, _)) in enumerate(zip(peaks, peaks[1:]), start=1):
        a = dist > d0 + guard_m
        b = dist < d1 - guard_m
        seg = db[a & b]
        if seg.size < 100:
            print(f"{n:<4}{d0:>8.3f}{d1:>8.3f}  点数不足({seg.size})")
            continue
        med, p95 = float(np.median(seg)), float(np.percentile(seg, 95))
        print(f"{n:<4}{d0:>8.3f}{d1:>8.3f}{d1 - d0:>9.3f}{seg.size:>7}"
              f"{med:>10.2f}{p95:>9.2f}{seg.std():>9.2f}{p95 - floor:>9.2f}")

    # 段内可分辨起伏：相邻点的标准差相对量化噪声的比例
    print(f"\n距离步距 {step * 1000:.3f} mm；指纹可用性取决于段内起伏是否稳定高于噪声。")
    print(f"末峰 {last:.3f} m，曲线末点 {dist[-1]:.3f} m")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("raws", nargs="+")
    ap.add_argument("--rate-mhz", type=float, default=50.0)
    ap.add_argument("--dist-scale", type=float, default=0.9502)
    ap.add_argument("--peaks", type=int, default=8)
    args = ap.parse_args()

    for raw_path in args.raws:
        print(f"\n=== {Path(raw_path).name} ===")
        raw = load_raw(raw_path)
        dist, ps = process(raw, fs_hz=args.rate_mhz * 1e6, dist_scale=args.dist_scale)
        db = 20.0 * np.log10(np.where(ps > 0, ps, np.nan))
        peaks = find_peaks(dist, db, top=args.peaks)
        print("候选峰：")
        for d, v in peaks:
            print(f"  {d:8.4f} m  {v:8.2f} dB")
        segment_stats(dist, db, peaks)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
