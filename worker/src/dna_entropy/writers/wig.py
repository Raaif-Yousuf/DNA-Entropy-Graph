"""WIG (fixedStep) writer for the per-position entropy track.

fixedStep WIG is 1-based and very compact (one value per line). Alternate to bedGraph.
"""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path

import numpy as np

from .base import write_text_lf

# issue #123: see bedgraph.py's identical note -- WIG is generic over "the per-position
# numeric track" too, so surprisal reuses this writer via `metric=`.
_DESCRIPTIONS: dict[str, str] = {
    "entropy": "Shannon entropy (bits)",
    "surprisal": "Surprisal: -log2 P(actual base) (bits)",
}


def _fixed_step_block(chrom: str, values: np.ndarray, start: int) -> list[str]:
    lines = [f"fixedStep chrom={chrom} start={start} step=1 span=1"]
    lines.extend(f"{float(v):.4f}" for v in values)
    return lines


class WigWriter:
    """Writes ``<name>.entropy.wig`` in fixedStep format (one block per contig)."""

    def write(
        self,
        *,
        name: str,
        values: np.ndarray,
        seq: str,
        start: int,
        out_dir: str,
        variant: str | None = None,
        metric: str = "entropy",
    ) -> str:
        return self.write_multi(
            name=name,
            blocks=[(name, values)],
            start=start,
            out_dir=out_dir,
            variant=variant,
            metric=metric,
        )

    def write_multi(
        self,
        *,
        name: str,
        blocks: Sequence[tuple[str, np.ndarray]],
        start: int,
        out_dir: str,
        variant: str | None = None,
        metric: str = "entropy",
    ) -> str:
        """Write one WIG file with a fixedStep block per ``(chrom, values)`` in ``blocks``.

        ``variant`` (e.g. ``"fwd"``/``"rev"`` for Direction.BOTH_SEPARATE, section 5.6)
        names the file ``<name>.entropy.<variant>.wig`` instead of ``<name>.entropy.wig``.
        ``metric`` (issue #123: ``"entropy"`` or ``"surprisal"``) names the file
        ``<name>.<metric>.wig`` and labels the track accordingly; the default keeps every
        existing caller's output byte-identical to before this option existed.
        """
        description = _DESCRIPTIONS.get(metric, metric)
        label = f"{name} {metric}" if variant is None else f"{name} {metric} ({variant})"
        lines = [f'track type=wiggle_0 name="{label}" description="{description}" visibility=full']
        for chrom, values in blocks:
            lines.extend(_fixed_step_block(chrom, values, start))
        text = "\n".join(lines) + "\n"
        suffix = f"{metric}.wig" if variant is None else f"{metric}.{variant}.wig"
        return write_text_lf(Path(out_dir) / f"{name}.{suffix}", text)
