"""联讯 GM-IX：按 5 峰切 4 段，评估每段的信噪比与跨会话可重复性。

用法：
  python segment_gmix.py <rawA.bin> <rawB.bin>
峰位取厂商 detect 输出（50 MHz/8 ms，dist_scale=1.0）：
  0.0005 / 1.1692 / 3.3377 / 6.5016 / 8.6865 m
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np

from gmix_io import load_raw, process

PEAKS_M = [0.0005, 1.1692, 3.3377, 6.5016, 8.6865]
GUARD_M = 0.10
RESAMPLE = 2048


def curve(raw_path: str, fs_hz: float) -> tuple[np.ndarray, np.ndarray]:
    dist, ps = process(load_raw(raw_path), fs_hz=fs_hz, dist_scale=1.0)
    return dist, 20.0 * np.log10(np.where(ps > 0, ps, np.nan))


def segment(dist: np.ndarray, db: np.ndarray, d0: float, d1: float,
            guard: float = GUARD_M, n: int = RESAMPLE) -> np.ndarray:
    m = (dist > d0 + guard) & (dist < d1 - guard)
    return np.interp(np.linspace(0, 1, n), np.linspace(0, 1, m.sum()), db[m])


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("raw_a")
    ap.add_argument("raw_b")
    ap.add_argument("--rate-mhz", type=float, default=50.0)
    args = ap.parse_args()

    da, dba = curve(args.raw_a, args.rate_mhz * 1e6)
    db_, dbb = curve(args.raw_b, args.rate_mhz * 1e6)
    step = da[1] - da[0]
    tail = dba[(da > PEAKS_M[-1] + 5) & (da < PEAKS_M[-1] + 15)]
    floor = float(np.median(tail))
    print(f"距离步距 {step * 1000:.3f} mm；参考噪声底 {floor:.2f} dB（末峰后 5~15 m）\n")

    segs_a, segs_b = [], []
    print(f"{'段':<3}{'起(m)':>8}{'止(m)':>8}{'长度(m)':>9}{'点数':>7}"
          f"{'中位(dB)':>9}{'起伏std':>8}{'SNR(dB)':>8}{'跨会话相关':>11}{'差异std':>8}")
    for i, (d0, d1) in enumerate(zip(PEAKS_M, PEAKS_M[1:]), start=1):
        m = (da > d0 + GUARD_M) & (da < d1 - GUARD_M)
        seg_a = segment(da, dba, d0, d1)
        seg_b = segment(db_, dbb, d0, d1)
        segs_a.append(seg_a)
        segs_b.append(seg_b)
        corr = float(np.corrcoef(seg_a, seg_b)[0, 1])
        diff = float((seg_a - seg_b).std())
        print(f"{i:<3}{d0:>8.3f}{d1:>8.3f}{d1 - d0:>9.3f}{m.sum():>7}"
              f"{np.median(dba[m]):>9.2f}{dba[m].std():>8.2f}"
              f"{np.percentile(dba[m], 95) - floor:>8.2f}{corr:>11.3f}{diff:>8.2f}")

    print("\n阴性对照：同一次采集内不同段的互相关（越低越说明段间模式可区分）")
    for i in range(4):
        for j in range(i + 1, 4):
            c = np.corrcoef(segs_a[i], segs_a[j])[0, 1]
            print(f"  A 段{i + 1} vs A 段{j + 1}: {c:+.3f}")

    print("\n同一段两次采集的相关（上行）与同段不同段的相关（下行）对比即可判断"
          "「段内指纹稳定、段间可区分」。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
