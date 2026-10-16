"""Writer for ``dna-entropy variants`` (issue #130): ``<name>.variants.tsv``."""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path

from ..analysis.variants import VariantResult
from .base import write_text_lf

HEADER = (
    "position\tref\talt\twindow_start\twindow_end\tdelta_surprisal_bits\tdelta_mean_entropy_bits"
    "\tsite_surprisal_ref_bits\tsite_surprisal_alt_bits"
)


def write_variants(*, name: str, results: Sequence[VariantResult], out_dir: str) -> str:
    """One row per variant, 1-based positions, 4 decimals (bits), like every other table here."""
    lines = [HEADER] + [
        f"{r.variant.pos}\t{r.variant.ref}\t{r.variant.alt}\t{r.window_start}\t{r.window_end}"
        f"\t{r.delta_surprisal:.4f}\t{r.delta_mean_entropy:.4f}"
        f"\t{r.site_surprisal_ref:.4f}\t{r.site_surprisal_alt:.4f}"
        for r in results
    ]
    return write_text_lf(Path(out_dir) / f"{name}.variants.tsv", "\n".join(lines) + "\n")
