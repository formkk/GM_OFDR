# -*- coding: utf-8 -*-
"""T-07 判定实验：联讯 ofdr.dll 输出 ps 的量纲（幅度域 / 功率域）。

方法：合成双音信号（整数 bin，无泄漏），对 P/S 做单变量幅度缩放，
观察 ps 在音峰处的标度律：
  一次标度（×2）  → ps 为幅度域 → 20·log10(ps) 即功率域 dB，无需 ÷2
  二次标度（×4）  → ps 为功率域 → 20·log10(ps) 为 2× 功率 dB，须 ÷2
联合（P 参与）的结构由 C/D 两列联合判读区分：
  |S|      : D×2, C×1
  |S|^2    : D×4, C×1
  |S|*|P|  : D×2, C×2
  |S|^2/|P|^2 : D×4, C×1/4
  |S|/|P|  : D×2, C×1/2
"""

import ctypes as C
import math
import sys

sys.path.insert(0, r"C:\Mycode\GM-OFDR\references\ofdr-0.1.1-release")
from ofdr import ofdr

N = 8192
FS = 50e6
K1, K2 = 256, 448          # 双音 bin（整数，无泄漏）
A1, A2 = 1.0, 0.5          # 音1/音2 幅度比 = 2，功率比 = 4
KA = 160                   # aux 音 bin


def two_tone(scale):
    """双音信号，整体幅度乘 scale。"""
    p, s = [], []
    for i in range(N):
        x = A1 * math.sin(2 * math.pi * K1 * i / N) + A2 * math.sin(2 * math.pi * K2 * i / N)
        p.append(scale * x)
        s.append(scale * x)
    return p, s


def aux_sig(scale=1.0):
    return [scale * math.sin(2 * math.pi * KA * i / N) for i in range(N)]


def run(p, s, a):
    cfg = ofdr.ofdr_config_default()
    cfg.fs = FS
    cfg.dist_scale = 1.0
    pa = (C.c_double * N)(*p)
    sa = (C.c_double * N)(*s)
    aa = (C.c_double * N)(*a)
    res = ofdr.ofdr_process(pa, sa, aa, N, C.byref(cfg))
    if res.error != 0:
        msg = C.string_at(res.error_msg).decode("utf-8", "replace") if res.error_msg else "?"
        raise RuntimeError(f"ofdr_process error {res.error}: {msg}")
    n = res.len
    ps = [res.ps[i] for i in range(n)]
    freq = [res.freq[i] for i in range(n)]
    ofdr.ofdr_result_free(C.byref(res))
    return ps, freq


def top_peaks(ps, freq, count=4, exclude_dc=2):
    """返回 ps 最高的 count 个 (bin, freq, ps)，排除前 exclude_dc 个低频 bin。"""
    idx = sorted(range(exclude_dc, len(ps)), key=lambda i: -ps[i])[:count]
    return sorted([(i, freq[i], ps[i]) for i in idx])


def main():
    base_p, base_s = two_tone(1.0)
    p2, _ = two_tone(2.0)             # B: P、S 同 ×2
    _, s2 = two_tone(2.0)             # D: 仅 S ×2
    aux = aux_sig()

    runs = {
        "A 基线":       (base_p, base_s, aux),
        "B P,S 同×2":   (p2, s2, aux),
        "C 仅 P×2":     (p2, base_s, aux),
        "D 仅 S×2":     (base_p, s2, aux),
    }

    results = {}
    for name, (p, s, a) in runs.items():
        ps, freq = run(p, s, a)
        peaks = top_peaks(ps, freq)
        results[name] = dict(ps=ps, freq=freq, peaks=peaks, len=len(ps))
        print(f"[{name}] len={len(ps)}")
        for b, f, v in peaks:
            print(f"    bin={b:5d}  freq={f/1e6:9.4f} MHz  ps={v:.6g}")

    # 用 A 的两个音峰 bin 作为锚点，跨运行对比
    a_pk = results["A 基线"]["peaks"]
    b1 = max(a_pk, key=lambda t: t[2])[0]   # 音1（幅度大）
    b2 = min(a_pk, key=lambda t: t[2])[0]   # 音2
    print(f"\n锚点 bin：音1={b1} (freq={results['A 基线']['freq'][b1]/1e6:.4f} MHz)，"
          f"音2={b2} (freq={results['A 基线']['freq'][b2]/1e6:.4f} MHz)")

    def val(name, b):
        return results[name]["ps"][b]

    r_tone = val("A 基线", b1) / val("A 基线", b2)
    print(f"\n音1/音2 ps 比（A 内）：{r_tone:.4f}   （幅度比=2，功率比=4）")

    for name in ("B P,S 同×2", "C 仅 P×2", "D 仅 S×2"):
        r1 = val(name, b1) / val("A 基线", b1)
        r2 = val(name, b2) / val("A 基线", b2)
        print(f"{name}: 音1 ×{r1:.4f}  音2 ×{r2:.4f}")

    print("\n判读表：")
    print("  D×2 & C×1     → ps=|S| 幅度域        → 20log10 即功率dB，不 ÷2")
    print("  D×4 & C×1     → ps=|S|² 功率域       → 须 ÷2")
    print("  D×2 & C×2     → ps=|S|·|P| 交叉谱    → 20log10=两项功率dB之和，不 ÷2（P 稳定时相对读数正确）")
    print("  D×4 & C×1/4   → ps=|S|²/|P|² 传递函数 → 须 ÷2")
    print("  D×2 & C×1/2   → ps=|S|/|P| 幅度比    → 不 ÷2")


if __name__ == "__main__":
    main()
