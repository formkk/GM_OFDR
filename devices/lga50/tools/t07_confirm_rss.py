# -*- coding: utf-8 -*-
"""T-07 补充实验：区分 RSS(方和根) 与其它组合假设。

预测（RSS 假设 ps = sqrt(|P|^2 + |S|^2)）：
  E: P 只有音1(amp 3)，S 只有音2(amp 4) → ps@音1 ∝ 3，ps@音2 ∝ 4，比 = 0.75
     （几何平均/|S|-only/|P|-only 假设下至少一个音会消失）
  F: (P=3, S=4)@同一音 vs (P=S=√12.5)@同一音 → 两者总功率同为 25，ps 必须相等
     （任何非"功率相加"的组合都会给出不同结果）
"""

import ctypes as C
import math
import sys

sys.path.insert(0, r"C:\Mycode\GM-OFDR\references\ofdr-0.1.1-release")
from ofdr import ofdr

N = 8192
FS = 50e6
K1, K2 = 256, 448
KA = 160


def tone_set(spec):
    """spec: {bin: amplitude}，返回信号列表。"""
    out = []
    for i in range(N):
        x = 0.0
        for k, a in spec.items():
            x += a * math.sin(2 * math.pi * k * i / N)
        out.append(x)
    return out


def aux_sig():
    return [math.sin(2 * math.pi * KA * i / N) for i in range(N)]


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


def val_at(ps, freq, target_bin):
    """取目标 bin 及相邻 bin 的最大 ps（抗泄漏）。"""
    return max(ps[max(0, target_bin - 1): target_bin + 2])


def main():
    aux = aux_sig()

    # E：P、S 承载不同音
    p_E = tone_set({K1: 3.0})
    s_E = tone_set({K2: 4.0})
    ps, freq = run(p_E, s_E, aux)
    v1 = val_at(ps, freq, K1)
    v2 = val_at(ps, freq, K2)
    print(f"[E] P=音1(amp3), S=音2(amp4)")
    print(f"    ps@音1 = {v1:.6g}   ps@音2 = {v2:.6g}   比 = {v1/v2:.4f}（RSS 预测 0.75）")

    # F：同音不同分配，总功率恒为 25
    p_F1 = tone_set({K1: 3.0});  s_F1 = tone_set({K1: 4.0})       # 总功率 9+16=25
    r = math.sqrt(12.5)
    p_F2 = tone_set({K1: r});    s_F2 = tone_set({K1: r})         # 总功率 12.5+12.5=25
    ps1, _ = run(p_F1, s_F1, aux)
    ps2, _ = run(p_F2, s_F2, aux)
    w1 = val_at(ps1, freq, K1)
    w2 = val_at(ps2, freq, K1)
    print(f"[F] (P=3,S=4) vs (P=S=√12.5)，总功率同为 25")
    print(f"    ps_a = {w1:.6g}   ps_b = {w2:.6g}   比 = {w1/w2:.6f}（RSS 预测 1.0）")

    # 附加确认：单通道存在、另一通道为零时 ps 是否存活
    p_G = tone_set({K1: 5.0});   s_G = [0.0] * N                   # 仅 P 有信号
    ps3, _ = run(p_G, s_G, aux)
    g1 = val_at(ps3, freq, K1)
    print(f"[G] 仅 P=音1(amp5)，S=0")
    print(f"    ps@音1 = {g1:.6g}（存活 → P 独立贡献；为 0 → 需两通道同时有能量）")


if __name__ == "__main__":
    main()
