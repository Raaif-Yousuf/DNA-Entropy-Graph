"""Per-gene summary statistics over the entropy track (issue #124).

One :class:`GeneRow` per gene/CDS: where it is, and the mean/min/max entropy (and mean
surprisal, when computed) over **its own bases**. Pure numpy over the ``(L,)`` entropy track
the run already produced (Hard Rule 3: nothing here touches the ``(L, 4)`` matrix or the
predictor).

Which bases count (the part that is easy to get wrong):

- A plain feature is ``begin..end``.
- A **compound** feature (a spliced ``join(...)``) is summarised over its real segments only
  (``GeneFeature.segments``), never the outer span, so the intron is not averaged in.
- On a **circular** molecule (issue #128) a feature that has a segment ending at the last
  base and another starting at base 1 crosses the origin. Its outer span would be the whole
  molecule, which is wrong, so its statistics come from its segments, and it is reported
  with ``begin > end`` (it starts near the end, runs through the origin and ends near the
  start) plus ``wraps_origin``. The same ``join`` on a linear molecule is just a spliced
  gene: no wrap flag, outer span as ``begin``/``end``.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass

import numpy as np

from ..annotators.base import GeneFeature

# "Low entropy" for the table's `fraction_low_entropy` column: the same 0.5 bit default the
# region caller (issue #125) uses for its own threshold, so the two agree out of the box.
DEFAULT_LOW_ENTROPY_BITS = 0.5


@dataclass(frozen=True)
class GeneRow:
    """One row of ``<name>.genes.tsv``. Entropy/surprisal are in bits."""

    contig: str
    gene_id: str
    begin: int  # 1-based, offset by the run's start coordinate; > end when wraps_origin
    end: int
    strand: str
    length: int  # bases actually summarised (segments only for a compound feature)
    partial: bool
    wraps_origin: bool
    segments: str  # "a..b,c..d" in genomic order, sequence-relative offset applied
    mean_entropy: float
    min_entropy: float
    max_entropy: float
    mean_surprisal: float | None
    fraction_low_entropy: float


def _segments_of(feature: GeneFeature) -> list[tuple[int, int]]:
    return sorted(feature.segments) if feature.segments else [(feature.begin, feature.end)]


def _wraps_origin(segments: Sequence[tuple[int, int]], length: int, circular: bool) -> bool:
    return (
        circular
        and len(segments) > 1
        and any(end == length for _, end in segments)
        and any(begin == 1 for begin, _ in segments)
    )


def summarize_genes(
    contig: str,
    features: Sequence[GeneFeature],
    values: np.ndarray,
    *,
    circular: bool,
    start: int,
    surprisal: np.ndarray | None = None,
    low_threshold: float = DEFAULT_LOW_ENTROPY_BITS,
) -> list[GeneRow]:
    """Summarise ``values`` (the ``(L,)`` entropy track) over each feature's bases.

    ``start`` is the run's genomic start coordinate (``RunConfig.start``): it relabels
    ``begin``/``end``/``segments`` exactly as the GFF3/bedGraph writers do and never moves
    the bases the statistics are taken over. A partial feature running past the end of the
    sequence is clamped to it; a feature with no bases left is skipped.
    """
    length = len(values)
    offset = start - 1
    rows: list[GeneRow] = []
    for feature in features:
        segments = [(b, min(e, length)) for b, e in _segments_of(feature) if b <= min(e, length)]
        if not segments:
            continue
        index = np.concatenate([np.arange(b - 1, e) for b, e in segments])
        track = values[index].astype(np.float64)
        wraps = _wraps_origin(segments, length, circular)
        if wraps:
            begin = max(b for b, e in segments if e == length)
            end = min(e for b, e in segments if b == 1)
        else:
            begin, end = min(b for b, _ in segments), max(e for _, e in segments)
        rows.append(
            GeneRow(
                contig=contig,
                gene_id=feature.gene_id,
                begin=begin + offset,
                end=end + offset,
                strand=feature.strand,
                length=int(index.size),
                partial=feature.partial,
                wraps_origin=wraps,
                segments=",".join(
                    f"{b + offset}..{e + offset}" for b, e in _display_order(segments, wraps, length)
                ),
                mean_entropy=float(track.mean()),
                min_entropy=float(track.min()),
                max_entropy=float(track.max()),
                mean_surprisal=None
                if surprisal is None
                else float(surprisal[index].astype(np.float64).mean()),
                fraction_low_entropy=float((track < low_threshold).mean()),
            )
        )
    return rows


def _display_order(segments: list[tuple[int, int]], wraps: bool, length: int) -> list[tuple[int, int]]:
    """Genomic order, except a wrapping gene reads from where it starts: rotate so the segment
    that ends at the last base comes first (``91..100,1..10``, the way GenBank writes it)."""
    if not wraps:
        return segments
    first = next(i for i, (_, e) in enumerate(segments) if e == length)
    return segments[first:] + segments[:first]
