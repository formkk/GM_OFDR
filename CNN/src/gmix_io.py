"""联讯 GM-IX 离线 raw → 距离域曲线（调用厂商 ofdr.dll，只读引用，不复制）。

raw 格式：int16 小端、4 通道交织（ch0=P、ch1=S、ch2=A、ch3 不用），
50 MHz / 8 ms / 1 段 = 400000 帧 = 3,200,000 字节。
ofdr 输出长度 = 输入帧数 / 2 + 1（200001 点）。
"""

from __future__ import annotations

import ctypes
from pathlib import Path

import numpy as np

WORKSPACE = Path(__file__).resolve().parents[2]
DEFAULT_DLL = WORKSPACE / "GM-IX" / "runtime" / "ofdr.dll"


class OfdrConfig(ctypes.Structure):
    _fields_ = [("fs", ctypes.c_double), ("dist_scale", ctypes.c_double)]


class OfdrResult(ctypes.Structure):
    _fields_ = [
        ("error", ctypes.c_int),
        ("error_msg", ctypes.c_char_p),
        ("dist", ctypes.POINTER(ctypes.c_double)),
        ("freq", ctypes.POINTER(ctypes.c_double)),
        ("ps", ctypes.POINTER(ctypes.c_double)),
        ("len", ctypes.c_size_t),
    ]


def _lib(path: Path = DEFAULT_DLL) -> ctypes.CDLL:
    lib = ctypes.CDLL(str(path))
    lib.ofdr_config_default.restype = OfdrConfig
    lib.ofdr_process.argtypes = [
        ctypes.POINTER(ctypes.c_double), ctypes.POINTER(ctypes.c_double),
        ctypes.POINTER(ctypes.c_double), ctypes.c_size_t,
        ctypes.POINTER(OfdrConfig),
    ]
    lib.ofdr_process.restype = OfdrResult
    lib.ofdr_result_free.argtypes = [ctypes.POINTER(OfdrResult)]
    return lib


def load_raw(path: str | Path) -> np.ndarray:
    """读取 4 通道交织 int16 raw，返回 (frames, 4)。"""
    data = np.fromfile(path, dtype="<i2")
    if data.size % 4:
        raise ValueError(f"{path} 长度不是 4 通道整数倍")
    return data.reshape(-1, 4)


def process(raw: np.ndarray, fs_hz: float = 50e6, dist_scale: float = 0.9502,
            channels: tuple[int, int, int] = (0, 1, 2)) -> tuple[np.ndarray, np.ndarray]:
    """返回 (dist_m, ps_linear)。ps 为幅度代表量，dB = 20log10(ps)。"""
    lib = _lib()
    cfg = lib.ofdr_config_default()
    cfg.fs = fs_hz
    cfg.dist_scale = dist_scale
    p, s, a = channels
    arrs = [np.ascontiguousarray(raw[:, c], dtype=np.float64) for c in (p, s, a)]
    ptrs = [x.ctypes.data_as(ctypes.POINTER(ctypes.c_double)) for x in arrs]
    res = lib.ofdr_process(ptrs[0], ptrs[1], ptrs[2], raw.shape[0], ctypes.byref(cfg))
    try:
        if res.error != 0:
            msg = res.error_msg.decode(errors="replace") if res.error_msg else ""
            raise RuntimeError(f"ofdr_process error={res.error} msg={msg}")
        n = int(res.len)
        dist = np.ctypeslib.as_array(res.dist, shape=(n,)).copy()
        ps = np.ctypeslib.as_array(res.ps, shape=(n,)).copy()
    finally:
        lib.ofdr_result_free(ctypes.byref(res))
    return dist, ps


def main() -> int:
    import argparse

    ap = argparse.ArgumentParser(description="联讯 raw → dist/ps")
    ap.add_argument("raw")
    ap.add_argument("--rate-mhz", type=float, default=50.0)
    ap.add_argument("--dist-scale", type=float, default=0.9502)
    args = ap.parse_args()

    raw = load_raw(args.raw)
    dist, ps = process(raw, fs_hz=args.rate_mhz * 1e6, dist_scale=args.dist_scale)
    db = 20.0 * np.log10(np.where(ps > 0, ps, np.nan))
    print(f"输入 {raw.shape[0]} 帧 → 输出 {dist.size} 点")
    print(f"距离 {dist[0]:.6f} ~ {dist[-1]:.6f} m，步距 {(dist[1] - dist[0]):.6f} m")
    print(f"ps 范围 {np.nanmin(db):.2f} ~ {np.nanmax(db):.2f} dB，中位 {np.nanmedian(db):.2f} dB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
