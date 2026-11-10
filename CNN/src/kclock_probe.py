"""验证能否用 aux 通道（ch2，~13 点/周期的干涉参考）做等光频重采样，
把不同 dist 步距的采集对齐到同一采样网格。

做法：对 ch2 求解析信号相位并展开，按固定「每周期 8 点」重采样 P/S/aux，
再调用 ofdr.dll 得到距离曲线，比较两次采集的末段细结构相关。
"""

from __future__ import annotations

import argparse

import numpy as np

from gmix_io import load_raw, process


def analytic_phase(x: np.ndarray) -> np.ndarray:
    n = x.size
    spec = np.fft.fft(x - x.mean())
    h = np.zeros(n)
    h[0] = 1.0
    if n % 2 == 0:
        h[1:n // 2] = 2.0
        h[n // 2] = 1.0
    else:
        h[1:(n + 1) // 2] = 2.0
    return np.unwrap(np.angle(np.fft.ifft(spec * h)))


def kresample(raw: np.ndarray, points_per_fringe: int = 8) -> tuple[np.ndarray, int]:
    aux = raw[:, 2].astype(np.float64)
    ph = analytic_phase(aux)
    fringes = (ph[-1] - ph[0]) / (2 * np.pi)
    m = int(points_per_fringe * np.floor(fringes))
    target = ph[0] + np.linspace(0, 2 * np.pi * (m / points_per_fringe), m)
    idx = np.interp(target, ph, np.arange(aux.size))
    out = np.stack([np.interp(idx, np.arange(aux.size), raw[:, c].astype(np.float64))
                    for c in range(4)], axis=1)
    return out, int(fringes)


def last_segment(dist: np.ndarray, db: np.ndarray, d0: float, d1: float,
                 guard: float = 0.1, n: int = 2048) -> np.ndarray:
    m = (dist > d0 + guard) & (dist < d1 - guard)
    return np.interp(np.linspace(0, 1, n), np.linspace(0, 1, m.sum()), db[m])


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("a")
    ap.add_argument("b")
    ap.add_argument("--ppf", type=int, default=8, help="每周期采样点数")
    args = ap.parse_args()

    out = []
    for p in (args.a, args.b):
        raw = load_raw(p)
        kr, fr = kresample(raw, args.ppf)
        dist, ps = process(kr, fs_hz=50e6, dist_scale=1.0)
        db = 20 * np.log10(np.where(ps > 0, ps, np.nan))
        out.append((dist, db, fr, kr.shape[0]))
        print(f"{p.split(chr(92))[-1]}: 周期 {fr}，重采样 {kr.shape[0]} 点，"
              f"dist 步距 {(dist[1] - dist[0]) * 1000:.4f} mm，末点 {dist[-1]:.2f} m")

    (da, ba, _, _), (db_, bb, _, _) = out
    # 峰位检测
    peaks = []
    for dist, sig in ((da, ba), (db_, bb)):
        idx = np.where((sig[1:-1] > sig[:-2]) & (sig[1:-1] >= sig[2:]))[0] + 1
        idx = idx[np.argsort(sig[idx])[::-1]]
        picked = []
        for i in idx:
            if all(abs(dist[i] - dist[j]) >= 0.5 for j in picked):
                picked.append(int(i))
            if len(picked) == 5:
                break
        peaks.append(sorted(float(dist[i]) for i in picked))
        print("  峰位:", [round(v, 4) for v in peaks[-1]])

    if len(peaks[0]) >= 2 and len(peaks[1]) >= 2:
        d0, d1 = peaks[0][-2], peaks[0][-1]
        a = last_segment(da, ba, d0, d1)
        b = last_segment(db_, bb, peaks[1][-2], peaks[1][-1])
        print(f"末段细结构相关（k-clock 重采样后）：{np.corrcoef(a, b)[0, 1]:.3f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
