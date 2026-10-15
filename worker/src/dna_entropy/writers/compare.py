"""Writers for ``dna-entropy compare`` (issue #129): the difference track and the deviations table."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import numpy as np

from ..analysis.compare import largest_deviations
from .base import write_text_lf


@dataclass(frozen=True)
class CompareOutputs:
    bedgraph: str
    deviations: str
    top: list[tuple[int, float]]


def write_compare(
    *, name: str, contig: str, positions: np.ndarray, diff: np.ndarray, out_dir: str, top: int = 10
) -> CompareOutputs:
    """Write ``<name>.compare.bedgraph`` (B minus A, 0-based half-open like every bedGraph here) and
    ``<name>.compare.deviations.tsv`` (the ``top`` largest differences, 1-based positions)."""
    header = (
        f'track type=bedGraph name="{name} B minus A" '
        'description="Entropy difference, B minus A (bits)" visibility=full'
    )
    lines = [header] + [
        f"{contig}\t{int(p) - 1}\t{int(p)}\t{float(d):.4f}" for p, d in zip(positions, diff, strict=True)
    ]
    bedgraph = write_text_lf(Path(out_dir) / f"{name}.compare.bedgraph", "\n".join(lines) + "\n")
    ranked = largest_deviations(positions, diff, top)
    rows = ["position\tdifference_bits"] + [f"{p}\t{d:.4f}" for p, d in ranked]
    deviations = write_text_lf(Path(out_dir) / f"{name}.compare.deviations.tsv", "\n".join(rows) + "\n")
    return CompareOutputs(bedgraph=bedgraph, deviations=deviations, top=ranked)
