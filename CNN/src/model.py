"""1D-CNN 指纹编码网络。

输入：单帧距离域曲线（裁剪+重采样+归一化后的一维序列）
输出：128 维 L2 归一化嵌入；二值化后即 128 bit「指纹码」，用汉明距离比对。
"""

from __future__ import annotations

import torch
import torch.nn as nn
import torch.nn.functional as F


class FingerprintNet(nn.Module):
    def __init__(self, emb_dim: int = 128, n_classes: int = 2) -> None:
        super().__init__()

        def block(cin: int, cout: int, k: int, pool: int) -> nn.Sequential:
            return nn.Sequential(
                nn.Conv1d(cin, cout, k, padding=k // 2),
                nn.BatchNorm1d(cout),
                nn.ReLU(inplace=True),
                nn.MaxPool1d(pool),
            )

        self.features = nn.Sequential(
            block(1, 32, 9, 4),
            block(32, 64, 7, 4),
            block(64, 96, 5, 4),
            nn.Conv1d(96, 128, 3, padding=1),
            nn.BatchNorm1d(128),
            nn.ReLU(inplace=True),
        )
        self.pool = nn.AdaptiveAvgPool1d(1)
        self.fc = nn.Sequential(nn.Linear(128, emb_dim), nn.Dropout(0.1))
        self.head = nn.Linear(emb_dim, n_classes)

    def forward(self, x: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        h = self.pool(self.features(x)).squeeze(-1)
        e = self.fc(h)
        return F.normalize(e, dim=1), self.head(e)
