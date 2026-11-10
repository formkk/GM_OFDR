"""LGA50-D 原始曲线抓包解析。

协议依据：docs/lga50/LGA50-D_远程控制协议分析.md §4.1
- 帧边界只认 b'!'（首帧前可能粘有 OSI / SETOK）
- 帧体内每值 %9.2f + '\\n'（末值以 '\\r' 结尾），每值 10 字节
- 有效点数由 LENGTH 决定，当前固件恒为 82153
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np

POINTS = 82153
FRAME_BYTES = POINTS * 10  # 821530，不含帧尾 '!'


def parse_raw_frames(path: str | Path, points: int = POINTS) -> np.ndarray:
    """解析原始曲线流，返回 (帧数, 点数) 的 float32 数组。"""
    data = Path(path).read_bytes()
    frames: list[np.ndarray] = []
    pos = 0
    while True:
        end = data.find(b"!", pos)
        if end < 0:
            break
        body = data[pos:end]
        # 帧体可能带前置的 OSI/SETOK；只取末尾完整帧长
        if len(body) >= FRAME_BYTES:
            body = body[-FRAME_BYTES:]
        values = np.array(body.replace(b"\r", b"").split(b"\n"), dtype=np.float32)
        if values.size and np.isfinite(values).all() and values.size == points:
            frames.append(values)
        pos = end + 1
    if not frames:
        raise ValueError(f"{path} 中未解析出有效帧")
    return np.stack(frames)


def main() -> int:
    ap = argparse.ArgumentParser(description="解析 LGA50-D 原始曲线抓包")
    ap.add_argument("path")
    ap.add_argument("--out", help="保存为 .npy")
    args = ap.parse_args()

    arr = parse_raw_frames(args.path)
    print(f"文件 {args.path}")
    print(f"帧数 {arr.shape[0]}，点数 {arr.shape[1]}")
    print(f"值域 {arr.min():.2f} ~ {arr.max():.2f} dB")
    peak_idx = arr.argmax(axis=1)
    print(f"最大峰 index: min={peak_idx.min()} max={peak_idx.max()} "
          f"对应距离 {peak_idx.min() * 185e-6:.4f} ~ {peak_idx.max() * 185e-6:.4f} m")
    if args.out:
        np.save(args.out, arr)
        print(f"已保存 {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
