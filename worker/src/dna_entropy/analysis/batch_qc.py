"""Batch outlier QC (issue #192): which input's entropy profile deviates from the rest of the batch.

Running 40 clones at once, the interesting one is the outlier. Each input is reduced to its mean
entropy (the median is reported beside it) and scored with a robust z-score, ``0.6745 * (x - median)
/ MAD`` over the batch's means, so one outlier cannot hide by inflating the spread. An input is
flagged when ``|z| > Z_THRESHOLD`` (3.5, the usual cut-off for this score). A batch below
``MIN_BATCH`` inputs has nothing to deviate from, so nothing is flagged.

When every other input is exactly alike (MAD = 0) any input that differs is flagged, with its
``z_score`` capped at ``Z_CAP`` so it stays a finite number in a text file.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass

import numpy as np

MIN_BATCH = 3
Z_THRESHOLD = 3.5
Z_CAP = 99.0
_MAD_SCALE = 0.6745  # makes the MAD comparable to a standard deviation for normal data
_EPS = 1e-6  # entropy is in bits, stored as float32: smaller differences are not real


@dataclass(frozen=True)
class BatchQcRow:
    name: str
    length: int
    mean: float
    median: float
    z_score: float
    flagged: bool


def batch_qc(tracks: Sequence[tuple[str, np.ndarray]]) -> list[BatchQcRow]:
    """Score every ``(name, entropy_track)``; one row per input, in input order."""
    means = np.array([float(np.mean(v)) for _, v in tracks], dtype=np.float64)
    if len(tracks) < MIN_BATCH:
        zs = np.zeros(len(tracks))
    else:
        center = float(np.median(means))
        mad = float(np.median(np.abs(means - center)))
        if mad > _EPS:
            zs = _MAD_SCALE * (means - center) / mad
        else:
            zs = np.where(np.abs(means - center) > _EPS, np.sign(means - center) * Z_CAP, 0.0)
        zs = np.clip(zs, -Z_CAP, Z_CAP)
    return [
        BatchQcRow(
            name=name,
            length=int(v.shape[0]),
            mean=float(m),
            median=float(np.median(v)),
            z_score=float(z),
            flagged=len(tracks) >= MIN_BATCH and abs(float(z)) > Z_THRESHOLD,
        )
        for (name, v), m, z in zip(tracks, means, zs, strict=True)
    ]
