"""Plain-text summary of an entropy run."""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path
from typing import TYPE_CHECKING

import numpy as np

from ..analysis.entropy import summarize
from .base import write_text_lf

if TYPE_CHECKING:  # avoid a hard import cycle risk; only needed for type hints
    from ..analysis.direction import DirectionResult


def _provenance_lines(dr: DirectionResult, *, indent: str = "") -> list[str]:
    """Section 5.6 windowing/direction provenance, recorded (not recomputed) per contig."""
    seam = str(dr.seam) if dr.seam is not None else "n/a"
    lines = [
        f"{indent}context length (K): {dr.context_length}",
        f"{indent}window (W):         {dr.window}",
        f"{indent}stride (S):         {dr.stride}",
        f"{indent}direction:          {dr.direction.value}",
        f"{indent}seam:               {seam}",
    ]
    if dr.reduced_context_count:
        lines.append(f"{indent}reduced-context positions: {dr.reduced_context_count}")
    return lines


class SummaryWriter:
    """Writes ``<name>.summary.txt`` (length + entropy stats)."""

    def write(
        self,
        *,
        name: str,
        values: np.ndarray,
        seq: str,
        start: int,
        out_dir: str,
        filename: str | None = None,
        provenance: DirectionResult | None = None,
    ) -> str:
        """Write the summary. ``filename`` overrides the default ``<name>.summary.txt``
        (GenBank runs use ``stats.txt`` per the professor's spec). ``provenance``, when
        given, records the windowing/direction derivation (section 5.6) that produced
        ``values`` — the same derived numbers a future manifest reader should read here
        rather than recompute."""
        s = summarize(values)
        lines = [
            "DNA-Entropy summary",
            f"name:               {name}",
            f"length:             {s.length} nt",
            f"coordinate start:   {start}",
            f"entropy mean:       {s.mean:.4f} bits",
            f"entropy min:        {s.minimum:.4f} bits (position {start + s.argmin})",
            f"entropy max:        {s.maximum:.4f} bits (position {start + s.argmax})",
        ]
        if provenance is not None:
            lines += _provenance_lines(provenance)
        text = "\n".join(lines) + "\n"
        return write_text_lf(Path(out_dir) / (filename or f"{name}.summary.txt"), text)

    def write_multi(
        self,
        *,
        name: str,
        sections: Sequence[tuple[str, np.ndarray]],
        start: int,
        out_dir: str,
        filename: str | None = None,
        provenance: Sequence[DirectionResult] | None = None,
    ) -> str:
        """Write a summary covering several contigs: an overall block, then one per contig.

        ``provenance``, when given, must align 1:1 with ``sections`` (one
        :class:`~dna_entropy.analysis.direction.DirectionResult` per contig, section 5.6).
        """
        all_values = np.concatenate([v for _, v in sections]) if sections else np.array([0.0])
        overall = summarize(all_values)
        lines = [
            "DNA-Entropy summary",
            f"name:               {name}",
            f"records:            {len(sections)}",
            f"total length:       {overall.length} nt",
            f"entropy mean (all): {overall.mean:.4f} bits",
            f"entropy min (all):  {overall.minimum:.4f} bits",
            f"entropy max (all):  {overall.maximum:.4f} bits",
            "",
        ]
        for i, (chrom, values) in enumerate(sections):
            s = summarize(values)
            lines += [
                f"[{chrom}]",
                f"  length:           {s.length} nt",
                f"  coordinate start: {start}",
                f"  entropy mean:     {s.mean:.4f} bits",
                f"  entropy min:      {s.minimum:.4f} bits (position {start + s.argmin})",
                f"  entropy max:      {s.maximum:.4f} bits (position {start + s.argmax})",
            ]
            if provenance is not None:
                lines += _provenance_lines(provenance[i], indent="  ")
            lines.append("")
        text = "\n".join(lines).rstrip("\n") + "\n"
        return write_text_lf(Path(out_dir) / (filename or f"{name}.summary.txt"), text)
