"""数据集装配：抓包 → 帧数组缓存 → 分块划分。

当前可用的两条真实链路（不同物理尾纤）：
  link_1m  LGA50/capture/raw_1m.bin         34 帧，末端峰 1.0177 m
  link_2m  LGA50/capture/raw_stream_01.bin  93 帧，末端峰 2.1042 m

同一抓包内的连续帧存在时间相关性，划分必须按连续块，不能随机打散。
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import numpy as np

from lga50_io import parse_raw_frames

WORKSPACE = Path(__file__).resolve().parents[2]
CAPTURES = {
    "link_1m": WORKSPACE / "LGA50" / "capture" / "raw_1m.bin",
    "link_2m": WORKSPACE / "LGA50" / "capture" / "raw_stream_01.bin",
}


@dataclass
class FrameSet:
    label: int
    link: str
    frames: np.ndarray  # (n, points)


def load_capture(name: str, path: Path, cache: Path) -> FrameSet:
    cache.mkdir(parents=True, exist_ok=True)
    npy = cache / f"{name}.npy"
    arr = np.load(npy) if npy.exists() else parse_raw_frames(path)
    if not npy.exists():
        np.save(npy, arr)
    return FrameSet(label=0, link=name, frames=arr)


def load_all(cache: Path) -> list[FrameSet]:
    sets = [load_capture(name, path, cache) for name, path in CAPTURES.items()]
    for i, fs in enumerate(sets):
        fs.label = i
    return sets


def block_split(n: int, folds: int, fold: int) -> tuple[np.ndarray, np.ndarray]:
    """连续块留一：把 n 帧顺序切成 folds 块，第 fold 块做测试。"""
    edges = np.linspace(0, n, folds + 1).astype(int)
    test = np.arange(edges[fold], edges[fold + 1])
    train = np.setdiff1d(np.arange(n), test)
    return train, test
