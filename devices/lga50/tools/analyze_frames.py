"""分析 LGA50-D 原始数据流帧结构。

用法：
  python analyze_frames.py <bin文件> [--expect-points 82153] [--show 2]
"""

import argparse
import re
import sys

VALUE_RE = re.compile(rb"^ *-?\d+\.\d{2}$")


def analyze_result(data: bytes, show: int) -> int:
    """解析 25 字节结果帧：%11.6f\\n + %11.6f\\r + '!'。"""
    frames = []
    pos = 0
    while True:
        i = data.find(b"!", pos)
        if i < 0:
            break
        body = data[pos:i]
        if len(body) == 24:
            try:
                length_m = float(body[:11])
                rl_db = float(body[11:23])
                frames.append((length_m, rl_db, body[11], body[23]))
            except ValueError:
                frames.append((None, None, None, None))
        else:
            frames.append((None, None, None, None))
        pos = i + 1
    print(f"帧数 {len(frames)}；剩余未成帧字节 {len(data) - pos}")
    bad = [k for k, f in enumerate(frames) if f[0] is None]
    print(f"解析失败帧 {len(bad)} 个 {bad[:5]}")
    ok = [f for f in frames if f[0] is not None]
    if ok:
        lens = [f[0] for f in ok]
        rls = [f[1] for f in ok]
        print(f"长度 m: min={min(lens):.6f} max={max(lens):.6f} first={lens[0]:.6f} last={lens[-1]:.6f}")
        print(f"回损 dB: min={min(rls):.3f} max={max(rls):.3f} first={rls[0]:.3f} last={rls[-1]:.3f}")
        print(f"第二值前的分隔符=0x{ok[0][2]:02X}，末值后的分隔符=0x{ok[0][3]:02X}")
    for f in frames[:show]:
        print(f"  {f}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--expect-points", type=int, default=None)
    ap.add_argument("--show", type=int, default=2, help="打印前 N 帧的统计")
    ap.add_argument("--mode", choices=["raw", "result"], default="raw",
                    help="raw=10字节/值的原始曲线；result=25字节的长度+回损帧")
    args = ap.parse_args()

    data = open(args.path, "rb").read()
    print(f"总字节 {len(data)}")

    if args.mode == "result":
        return analyze_result(data, args.show)

    # 统计非数字/非换行的异常字节
    bad = {}
    for i, b in enumerate(data):
        if b in (0x0A, 0x20, 0x2D, 0x2E, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35,
                 0x36, 0x37, 0x38, 0x39):
            continue
        bad[i] = b
    print(f"非数字字符 {len(bad)} 个；前 20 个位置：{list(bad.items())[:20]}")

    # 按 10 字节切记录，检查格式
    n = len(data) // 10
    bad_rec = []
    for k in range(n):
        rec = data[k * 10:(k + 1) * 10]
        if not (VALUE_RE.match(rec[:9]) and rec[9] == 0x0A):
            bad_rec.append((k, rec))
            if len(bad_rec) >= 10:
                break
    print(f"按 10 字节切分：{n} 条记录，格式异常 {len(bad_rec)} 条")
    for k, rec in bad_rec:
        print(f"  记录 {k}: {rec!r}")

    # 找出所有 '!' 位置，验证是否为帧尾
    bangs = [i for i, b in enumerate(data) if b == 0x21]
    print(f"'!' 出现 {len(bangs)} 次；前 5 个位置 {bangs[:5]}；后 3 个位置 {bangs[-3:]}")
    if len(bangs) >= 2:
        diffs = [bangs[i + 1] - bangs[i] for i in range(len(bangs) - 1)]
        uniq = sorted(set(diffs))
        print(f"相邻 '!' 间隔去重：{uniq[:5]} ... 共 {len(uniq)} 种")
        print(f"间隔众数：{max(set(diffs), key=diffs.count)}")
        if args.expect_points:
            want = args.expect_points * 10 + 1
            print(f"预期帧长(点数={args.expect_points})：{want}；匹配帧数：{diffs.count(want)}")

    # 每次 '!' 之后 16 字节，看紧跟什么（OSI 还是下一帧首值）
    for pos in bangs[:5]:
        nxt = data[pos + 1:pos + 21]
        print(f"  '{'!'}'@{pos} 之后 20 字节: {nxt!r}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
