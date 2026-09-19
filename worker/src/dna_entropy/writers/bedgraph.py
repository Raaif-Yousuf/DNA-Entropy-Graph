"""bedGraph writer for the per-position entropy track (IGV default).

bedGraph is plain text, 0-based half-open: ``chrom  start  end  value``. The ``chrom``
equals the contig name we also emit as FASTA, so coordinates line up in IGV. IGV renders
it as a bar graph out of the box.
"""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path

import numpy as np

from .base import write_text_lf

# issue #123: bedGraph is generic over "the per-position numeric track", so a second
# metric (surprisal) reuses this writer wholesale via `metric=` rather than a parallel
# class. Defaults to "entropy" everywhere so every existing caller (which never passes
# `metric`) writes byte-identical files to before this option existed.
_DESCRIPTIONS: dict[str, str] = {
    "entropy": "Shannon entropy (bits)",
    "surprisal": "Surprisal: -log2 P(actual base) (bits)",
}


def _block_lines(chrom: str, values: np.ndarray, start: int) -> list[str]:
    # genomic 1-based coord of base i (0-based) is (start + i);
    # bedGraph is 0-based half-open => [start-1+i, start+i).
    base0 = start - 1
    return [f"{chrom}\t{base0 + i}\t{base0 + i + 1}\t{float(v):.4f}" for i, v in enumerate(values)]


class BedGraphWriter:
    """Writes ``<name>.entropy.bedgraph``."""

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
        """Write one bedGraph with a ``chrom`` block per ``(chrom, values)`` in ``blocks``.

        A single ``track`` header covers every block; each row already carries its own
        ``chrom``, so the records stay aligned to their FASTA contigs in IGV. ``variant``
        (e.g. ``"fwd"``/``"rev"`` for Direction.BOTH_SEPARATE, section 5.6) names the file
        ``<name>.entropy.<variant>.bedgraph`` instead of the default ``<name>.entropy.bedgraph``.
        ``metric`` (issue #123: ``"entropy"`` or ``"surprisal"``) names the file
        ``<name>.<metric>.bedgraph`` and labels the track accordingly; the default keeps
        every existing caller's output byte-identical to before this option existed.
        """
        description = _DESCRIPTIONS.get(metric, metric)
        label = f"{name} {metric}" if variant is None else f"{name} {metric} ({variant})"
        lines = [f'track type=bedGraph name="{label}" description="{description}" visibility=full']
        for chrom, values in blocks:
            lines.extend(_block_lines(chrom, values, start))
        text = "\n".join(lines) + "\n"
        suffix = f"{metric}.bedgraph" if variant is None else f"{metric}.{variant}.bedgraph"
        return write_text_lf(Path(out_dir) / f"{name}.{suffix}", text)
