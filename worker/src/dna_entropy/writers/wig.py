"""WIG (fixedStep) writer for the per-position entropy track.

fixedStep WIG is 1-based and very compact (one value per line). Alternate to bedGraph.
"""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path

import numpy as np

from .base import write_text_lf


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
    ) -> str:
        return self.write_multi(
            name=name,
            blocks=[(name, values)],
            start=start,
            out_dir=out_dir,
            variant=variant,
        )

    def write_multi(
        self,
        *,
        name: str,
        blocks: Sequence[tuple[str, np.ndarray]],
        start: int,
        out_dir: str,
        variant: str | None = None,
    ) -> str:
        """Write one WIG file with a fixedStep block per ``(chrom, values)`` in ``blocks``.

        ``variant`` (e.g. ``"fwd"``/``"rev"`` for Direction.BOTH_SEPARATE, section 5.6)
        names the file ``<name>.entropy.<variant>.wig`` instead of ``<name>.entropy.wig``.
        """
        label = f"{name} entropy" if variant is None else f"{name} entropy ({variant})"
        lines = [f'track type=wiggle_0 name="{label}" description="Shannon entropy (bits)" visibility=full']
        for chrom, values in blocks:
            lines.extend(_fixed_step_block(chrom, values, start))
        text = "\n".join(lines) + "\n"
        suffix = "entropy.wig" if variant is None else f"entropy.{variant}.wig"
        return write_text_lf(Path(out_dir) / f"{name}.{suffix}", text)
