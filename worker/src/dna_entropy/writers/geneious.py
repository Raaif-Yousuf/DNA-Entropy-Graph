"""GFF3 entropy-track writer for Geneious Prime.

Geneious Prime does **not** import WIG or bedGraph as graph tracks (in Geneious those
are *export*-only from the Graphs tab). It *does* import GFF3, and it can shade an
annotation track by a numeric qualifier via *Color by / Heatmap*. So we emit the
per-position entropy as a GFF3 feature track: one 1 bp feature per position carrying the
entropy in both the score column and an ``entropy`` qualifier, on the same contig
(``name``) and coordinate frame as the FASTA/GenBank we also write, so it lines up.

See docs/DISTRIBUTION.md / README.md for the load-into-Geneious instructions.

**Large-sequence binning (issue #296):** one 9-column GFF3 line per base scales linearly
in a heavy per-line format. MEASURED 2026-09-19 (``worker/tests/test_writers.py``'s
large-sequence tests and the ad hoc benchmark that produced this number): an unbinned
1,000,000-position track is 87.78 MB and takes 1.587s to write; a 10,000-position track
is 0.82 MB / 0.012s. Writing it is not the problem -- 87+ MB is a poor size for Geneious
to import and a poor density for its Heatmap view, and it scales straight through the
10 Mnt outer sanity bound (``config.DEFAULT_MAX_TOTAL_LEN``) toward a file too large to
be useful. Above :data:`DEFAULT_MAX_PER_BASE_FEATURES` combined positions, this writer
switches to fixed-size bins (mean entropy per bin) instead of refusing to run or silently
truncating, and records a ``# NOTE`` line in the file saying so and pointing at the
still-per-base WIG/bedGraph tracks. ``max_per_base_features=None`` forces full per-base
resolution regardless of length, for a caller that wants it anyway.
"""

from __future__ import annotations

import math
from collections.abc import Sequence
from pathlib import Path

import numpy as np

from .base import write_text_lf

# Above this many combined positions (summed across all blocks in one write_multi call),
# GeneiousWriter bins instead of emitting one feature per base (issue #296). Chosen so the
# resulting file stays in the low tens of MB even at the largest realistic run (roughly
# `DEFAULT_MAX_PER_BASE_FEATURES * ~88 bytes/line`), while staying far above any window
# or context-length default (`config.DEFAULT_MAX_LEN` is 8192) so ordinary runs are
# unaffected.
DEFAULT_MAX_PER_BASE_FEATURES = 200_000


def _feature_lines(chrom: str, values: np.ndarray, start: int) -> list[str]:
    offset = start - 1  # 1-based genomic coord of position i (0-based) is (start + i)
    lines = []
    for i, v in enumerate(values):
        pos = offset + i + 1  # GFF3 is 1-based, inclusive
        val = f"{float(v):.4f}"
        lines.append(
            "\t".join(
                [
                    chrom,
                    "dna-entropy",
                    "entropy",
                    str(pos),
                    str(pos),
                    val,  # score column: Geneious can Color by / Heatmap on this
                    ".",
                    ".",
                    f"Name=H={val};entropy={val}",
                ]
            )
        )
    return lines


def _binned_feature_lines(chrom: str, values: np.ndarray, start: int, bin_size: int) -> list[str]:
    offset = start - 1
    length = len(values)
    lines = []
    for bin_start in range(0, length, bin_size):
        bin_end = min(bin_start + bin_size, length)  # last bin may be shorter
        mean_h = float(values[bin_start:bin_end].mean())
        pos_start = offset + bin_start + 1  # GFF3 is 1-based, inclusive
        pos_end = offset + bin_end
        val = f"{mean_h:.4f}"
        width = bin_end - bin_start
        lines.append(
            "\t".join(
                [
                    chrom,
                    "dna-entropy",
                    "entropy_bin",
                    str(pos_start),
                    str(pos_end),
                    val,  # score column: mean entropy over the bin
                    ".",
                    ".",
                    f"Name=H={val};entropy_mean={val};bin_positions={width}",
                ]
            )
        )
    return lines


class GeneiousWriter:
    """Writes ``<name>.entropy.geneious.gff3`` (a per-position entropy heatmap track).

    Above ``max_per_base_features`` combined positions, switches to fixed-size mean-
    entropy bins instead of one feature per base (issue #296; see module docstring).
    """

    def write(
        self,
        *,
        name: str,
        values: np.ndarray,
        seq: str,
        start: int,
        out_dir: str,
        max_per_base_features: int | None = DEFAULT_MAX_PER_BASE_FEATURES,
    ) -> str:
        return self.write_multi(
            name=name,
            blocks=[(name, values)],
            start=start,
            out_dir=out_dir,
            max_per_base_features=max_per_base_features,
        )

    def write_multi(
        self,
        *,
        name: str,
        blocks: Sequence[tuple[str, np.ndarray]],
        start: int,
        out_dir: str,
        max_per_base_features: int | None = DEFAULT_MAX_PER_BASE_FEATURES,
    ) -> str:
        """Write one GFF3 with a per-position (or, above the threshold, per-bin) block per
        ``(chrom, values)`` in ``blocks``.

        ``max_per_base_features`` caps the TOTAL feature count across every block
        combined (one file, one import into Geneious). ``None`` disables binning
        unconditionally, for a caller that wants full per-base resolution regardless of
        length.
        """
        total_length = sum(len(values) for _, values in blocks)
        binned = max_per_base_features is not None and total_length > max_per_base_features
        bin_size = max(1, math.ceil(total_length / max_per_base_features)) if binned else 1

        lines = ["##gff-version 3"]
        for chrom, values in blocks:
            end = start + len(values) - 1
            lines.append(f"##sequence-region {chrom} {start} {end}")
        if binned:
            lines.append(
                f"# NOTE: entropy binned into {bin_size}-nt windows (mean per bin) because "
                f"the combined track length ({total_length} positions) exceeds "
                f"max_per_base_features ({max_per_base_features}). See "
                "docs/science_and_formats.md for the still-per-base WIG/bedGraph tracks, "
                "or pass max_per_base_features=None for unbinned output."
            )
        for chrom, values in blocks:
            if binned:
                lines.extend(_binned_feature_lines(chrom, values, start, bin_size))
            else:
                lines.extend(_feature_lines(chrom, values, start))
        text = "\n".join(lines) + "\n"
        return write_text_lf(Path(out_dir) / f"{name}.entropy.geneious.gff3", text)
