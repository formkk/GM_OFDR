"""指纹实验：连续块划分训练 + 验证/识别/指纹码稳定性评测。

用法示例：
  python train_eval.py --name full   --crop-start 0.05 --crop-end 3.0
  python train_eval.py --name common --crop-start 0.05 --crop-end 0.9
  python train_eval.py --name full_raw --baseline raw
"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np
import torch

from dataset import load_all, block_split
from model import FingerprintNet
from preprocess import detrend, normalize, resample_frame


# ---------- 指标 ----------

def roc_auc(labels: np.ndarray, scores: np.ndarray) -> float:
    order = np.argsort(scores)
    ranks = np.empty_like(order, dtype=np.float64)
    ranks[order] = np.arange(1, scores.size + 1)
    pos = labels == 1
    n_pos, n_neg = int(pos.sum()), int((~pos).sum())
    if n_pos == 0 or n_neg == 0:
        return float("nan")
    return float((ranks[pos].sum() - n_pos * (n_pos + 1) / 2) / (n_pos * n_neg))


def eer(labels: np.ndarray, scores: np.ndarray) -> float:
    best = 1.0
    for t in np.unique(scores):
        far = ((scores >= t) & (labels == 0)).sum() / max((labels == 0).sum(), 1)
        frr = ((scores < t) & (labels == 1)).sum() / max((labels == 1).sum(), 1)
        best = min(best, max(far, frr))
    return float(best)


def pair_scores(emb: np.ndarray, labels: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    sim = emb @ emb.T
    iu = np.triu_indices(emb.shape[0], k=1)
    y = (labels[:, None] == labels[None, :])[iu].astype(np.int64)
    return y, sim[iu]


def code_ber(codes: np.ndarray, labels: np.ndarray) -> tuple[float, float]:
    dist = (codes[:, None, :] != codes[None, :, :]).mean(axis=2)
    iu = np.triu_indices(codes.shape[0], k=1)
    intra = (labels[:, None] == labels[None, :])[iu]
    d = dist[iu]
    return float(d[intra].mean()), float(d[~intra].mean())


def averaged_code_ber(emb: np.ndarray, labels: np.ndarray,
                      bias: np.ndarray) -> tuple[float, float]:
    """多帧平均后再二值化的码稳定性：同链路用块内前后半段对比。"""
    half_codes: dict[int, list[np.ndarray]] = {}
    for c in np.unique(labels):
        idx = np.where(labels == c)[0]
        if len(idx) < 2:
            continue
        h = len(idx) // 2
        half_codes[c] = [
            (emb[idx[:h]].mean(0) > bias).astype(np.int8),
            (emb[idx[h:]].mean(0) > bias).astype(np.int8),
        ]
    intra = [np.mean(v[0] != v[1]) for v in half_codes.values()]
    classes = list(half_codes)
    inter = []
    for i in range(len(classes)):
        for j in range(i + 1, len(classes)):
            a = np.mean(half_codes[classes[i]], axis=0) > 0.5
            b = np.mean(half_codes[classes[j]], axis=0) > 0.5
            inter.append(np.mean(a != b))
    return float(np.mean(intra)) if intra else float("nan"), \
        float(np.mean(inter)) if inter else float("nan")


def rank1(emb: np.ndarray, labels: np.ndarray, gallery: np.ndarray) -> float:
    pred = (emb @ gallery.T).argmax(axis=1)
    return float((pred == labels).mean())


def evaluate(emb_test: np.ndarray, y_test: np.ndarray,
             emb_train: np.ndarray, y_train: np.ndarray) -> dict:
    y_pair, s_pair = pair_scores(emb_test, y_test)
    bias = np.median(emb_train, axis=0)
    codes = (emb_test > bias).astype(np.int8)
    intra_ber, inter_ber = code_ber(codes, y_test)
    avg_intra, avg_inter = averaged_code_ber(emb_test, y_test, bias)
    gallery = np.stack([emb_train[y_train == c].mean(axis=0) for c in np.unique(y_train)])
    gallery /= np.linalg.norm(gallery, axis=1, keepdims=True) + 1e-9
    return {
        "auc": roc_auc(y_pair, s_pair),
        "eer": eer(y_pair, s_pair),
        "rank1": rank1(emb_test, y_test, gallery),
        "intra_ber": intra_ber,
        "inter_ber": inter_ber,
        "avg_intra_ber": avg_intra,
        "avg_inter_ber": avg_inter,
        "intra_cos": float(s_pair[y_pair == 1].mean()),
        "inter_cos": float(s_pair[y_pair == 0].mean()),
        "_bias": bias,
    }


# ---------- 训练 ----------

def train_model(x_train: np.ndarray, y_train: np.ndarray,
                args: argparse.Namespace, rng: np.random.Generator) -> FingerprintNet:
    torch.manual_seed(args.seed)
    model = FingerprintNet(emb_dim=args.emb_dim, n_classes=int(y_train.max()) + 1)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)

    xb_all = torch.from_numpy(x_train[:, None, :])
    yb_all = torch.from_numpy(y_train)
    n = len(y_train)
    model.train()
    for _ in range(args.epochs):
        perm = rng.permutation(n)
        for i in range(0, n, args.batch):
            idx = perm[i:i + args.batch]
            if len(idx) < 2:
                continue
            xb = xb_all[idx].clone()
            xb = xb + torch.from_numpy(
                rng.uniform(-args.aug_gain, args.aug_gain, (len(idx), 1, 1)).astype(np.float32))
            xb = xb + torch.from_numpy(
                rng.normal(0, args.aug_noise, (len(idx), 1, xb.shape[-1])).astype(np.float32))
            k = int(rng.integers(-args.aug_shift, args.aug_shift + 1))
            if k:
                xb = torch.roll(xb, shifts=k, dims=-1)
            _, logits = model(xb)
            loss = torch.nn.functional.cross_entropy(logits, yb_all[idx])
            opt.zero_grad()
            loss.backward()
            opt.step()
        sched.step()

    model.eval()
    return model


def encode(model: FingerprintNet, x: np.ndarray) -> np.ndarray:
    with torch.no_grad():
        emb, _ = model(torch.from_numpy(x[:, None, :]))
    return emb.numpy()


def l2norm(x: np.ndarray) -> np.ndarray:
    return x / (np.linalg.norm(x, axis=1, keepdims=True) + 1e-9)


def run(args: argparse.Namespace) -> dict:
    out_dir = Path(args.out) / args.name
    out_dir.mkdir(parents=True, exist_ok=True)
    sets = load_all(Path(args.cache))

    xs, ys = [], []
    for fs in sets:
        for y in fs.frames:
            xs.append(resample_frame(y, args.crop_start, args.crop_end, args.points))
            ys.append(fs.label)
    xs = np.stack(xs)
    ys = np.asarray(ys)

    folds = min(args.folds, min(len(fs.frames) for fs in sets))
    results = []
    for fold in range(folds):
        tr, te = [], []
        offset = 0
        for fs in sets:
            a, b = block_split(len(fs.frames), folds, fold)
            tr.append(a + offset)
            te.append(b + offset)
            offset += len(fs.frames)
        tr, te = np.concatenate(tr), np.concatenate(te)

        x_tr = np.stack([normalize(detrend(v, args.detrend), args.norm) for v in xs[tr]])
        x_te = np.stack([normalize(detrend(v, args.detrend), args.norm) for v in xs[te]])
        if args.test_tilt:
            ramp = np.linspace(-args.test_tilt / 2, args.test_tilt / 2, x_te.shape[1])
            x_te = x_te + ramp.astype(np.float32)
        if args.test_noise:
            x_te = x_te + np.random.default_rng(7).normal(
                0, args.test_noise, x_te.shape).astype(np.float32)
        rng = np.random.default_rng(args.seed + fold * 100)
        t0 = time.time()

        if args.baseline == "raw":
            emb_train, emb_test = l2norm(x_tr), l2norm(x_te)
        else:
            model = train_model(x_tr, ys[tr], args, rng)
            emb_train, emb_test = l2norm(encode(model, x_tr)), l2norm(encode(model, x_te))

        res = evaluate(emb_test, ys[te], emb_train, ys[tr])
        if fold == 0 and args.baseline == "cnn":
            torch.save({
                "state_dict": model.state_dict(),
                "meta": vars(args),
                "bias": res["_bias"],
                "cos_thr": (res["intra_cos"] + res["inter_cos"]) / 2,
                "ber_thr": (res["intra_ber"] + res["inter_ber"]) / 2,
            }, out_dir / "model.pt")
        res.pop("_bias")
        print(f"[{args.name}] fold {fold}: AUC={res['auc']:.3f} EER={res['eer']:.3f} "
              f"rank1={res['rank1']:.3f} BER={res['intra_ber']:.3f}/{res['inter_ber']:.3f} "
              f"avgBER={res['avg_intra_ber']:.3f}/{res['avg_inter_ber']:.3f} "
              f"({time.time() - t0:.1f}s)")
        results.append(res)

    summary = {
        key: [float(np.mean([r[key] for r in results])), float(np.std([r[key] for r in results]))]
        for key in ("auc", "eer", "rank1", "intra_ber", "inter_ber",
                    "avg_intra_ber", "avg_inter_ber", "intra_cos", "inter_cos")
    }
    with open(out_dir / "metrics.json", "w", encoding="utf-8") as f:
        json.dump({"meta": vars(args), "folds": results, "summary": summary},
                  f, indent=2, ensure_ascii=False)
    print(f"[{args.name}] 均值±标准差：")
    for k, (m, s) in summary.items():
        print(f"  {k:10s} {m:.4f} ± {s:.4f}")
    print(f"结果已写入 {out_dir / 'metrics.json'}")
    return summary


def parse_args() -> argparse.Namespace:
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", required=True)
    ap.add_argument("--baseline", choices=["cnn", "raw"], default="cnn")
    ap.add_argument("--crop-start", type=float, default=0.05)
    ap.add_argument("--crop-end", type=float, default=3.0)
    ap.add_argument("--points", type=int, default=2048)
    ap.add_argument("--norm", choices=["ref", "zscore"], default="ref")
    ap.add_argument("--detrend", type=int, default=0, help="去基线多项式阶数，0=不去")
    ap.add_argument("--test-tilt", type=float, default=0.0, help="测试集注入线性倾斜(dB)")
    ap.add_argument("--test-noise", type=float, default=0.0, help="测试集注入高斯噪声")
    ap.add_argument("--folds", type=int, default=5)
    ap.add_argument("--epochs", type=int, default=60)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--emb-dim", type=int, default=128)
    ap.add_argument("--aug-gain", type=float, default=0.3)
    ap.add_argument("--aug-noise", type=float, default=0.1)
    ap.add_argument("--aug-shift", type=int, default=8)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--cache", default=str(Path(__file__).resolve().parents[1] / "data"))
    ap.add_argument("--out", default=str(Path(__file__).resolve().parents[1] / "runs"))
    return ap.parse_args()


if __name__ == "__main__":
    run(parse_args())
