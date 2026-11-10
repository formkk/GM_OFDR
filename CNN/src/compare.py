"""指纹码比对：把两段抓包编码成 128 bit 指纹码，输出相似度与判定。

用法：
  python compare.py --model ../runs/full/model.pt \
      --a ../LGA50_capture/raw_1m.bin --b ../LGA50_capture/raw_stream_01.bin
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
import torch

from lga50_io import parse_raw_frames
from model import FingerprintNet
from preprocess import detrend, normalize, resample_frame


def encode_capture(path: Path, meta: dict, model: FingerprintNet, bias: np.ndarray,
                   frames: int) -> tuple[np.ndarray, np.ndarray]:
    arr = parse_raw_frames(path)[:frames]
    xs = np.stack([
        normalize(detrend(resample_frame(y, meta["crop_start"], meta["crop_end"], meta["points"]),
                          meta.get("detrend", 0)), meta.get("norm", "ref"))
        for y in arr
    ])
    with torch.no_grad():
        emb, _ = model(torch.from_numpy(xs[:, None, :]))
    emb = emb.numpy()
    codes = (emb > bias).astype(np.int8)
    return emb, codes


def to_hex(code: np.ndarray) -> str:
    packed = np.packbits(code.astype(np.uint8))
    return packed.tobytes().hex()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--a", required=True, help="参照抓包（注册端）")
    ap.add_argument("--b", required=True, help="待比对抓包（现场端）")
    ap.add_argument("--frames", type=int, default=10)
    args = ap.parse_args()

    ckpt = torch.load(args.model, map_location="cpu", weights_only=False)
    meta = ckpt["meta"]
    model = FingerprintNet(emb_dim=meta["emb_dim"], n_classes=2)
    model.load_state_dict(ckpt["state_dict"])
    model.eval()

    emb_a, code_a = encode_capture(Path(args.a), meta, model, ckpt["bias"], args.frames)
    emb_b, code_b = encode_capture(Path(args.b), meta, model, ckpt["bias"], args.frames)

    mean_a, mean_b = emb_a.mean(0), emb_b.mean(0)
    cos = float(mean_a @ mean_b / (np.linalg.norm(mean_a) * np.linalg.norm(mean_b)))
    ber = float((code_a.mean(0).round().astype(np.int8) !=
                 code_b.mean(0).round().astype(np.int8)).mean())
    same = cos >= ckpt["cos_thr"] and ber <= ckpt["ber_thr"]

    print(f"A = {args.a}  指纹码 {to_hex(code_a.mean(0).round().astype(np.int8))}")
    print(f"B = {args.b}  指纹码 {to_hex(code_b.mean(0).round().astype(np.int8))}")
    print(f"余弦相似度 {cos:.4f}（判定阈值 {ckpt['cos_thr']:.4f}）")
    print(f"码间误码率 {ber:.4f}（判定阈值 {ckpt['ber_thr']:.4f}）")
    print(f"判定：{'同一链路' if same else '不同链路'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
