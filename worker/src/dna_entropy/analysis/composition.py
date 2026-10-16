"""Composition tracks that need no model (issue #191): GC percent and codon usage.

Cheap context that helps read an entropy track: a stretch of low entropy that is also GC-rich
or AT-rich is probably not a coincidence, and a gene whose codon usage deviates from uniform
may be horizontally acquired. Pure Python/numpy over the sequence the run already validated;
nothing here touches the predictor or the ``(L, 4)`` matrix.

GC percent
    Centred rolling window (default 51, odd). Value at base ``i`` is
    ``100 * (G + C) / (A + C + G + T)`` over the window. Ambiguity codes (``N`` ...) are NOT
    counted in the denominator; a window with no unambiguous base is ``0.0``. Edges follow
    ``analysis/smoothing.py``: the window shrinks at the ends of a linear molecule and wraps
    the origin of a circular one (a window longer than one turn is clamped to one turn).

Codon usage
    Per CDS-shaped feature (length a multiple of three once its segments are joined): codons
    are read on the feature's own strand (a minus-strand feature is reverse-complemented),
    translated with the standard genetic code (table 1; the sense assignments are the same
    in table 11), and counted. ``frequency`` = count / codons counted; ``rscu`` (relative
    synonymous codon usage) = count / (the amino acid's total / its number of synonymous
    codons), so ``1.0`` means that codon is used exactly as often as an even split among its
    synonyms; ``deviation`` = ``rscu - 1``. A codon containing an ambiguity code is not
    counted. Only codons that occur are listed.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from ..annotators.base import GeneFeature
from .smoothing import validate_smoothing_windows

DEFAULT_GC_WINDOW = 51

_BASES = "TCAG"
_AMINO = "FFLLSSSSYY**CC*WLLLLPPPPHHQQRRRRIIIMTTTTNNKKSSRRVVVVAAAADDEEGGGG"
CODON_TABLE: dict[str, str] = {
    a + b + c: _AMINO[16 * i + 4 * j + k]
    for i, a in enumerate(_BASES)
    for j, b in enumerate(_BASES)
    for k, c in enumerate(_BASES)
}
_SYNONYMS: dict[str, int] = {}
for _aa in CODON_TABLE.values():
    _SYNONYMS[_aa] = _SYNONYMS.get(_aa, 0) + 1

_COMPLEMENT = str.maketrans("ACGTacgt", "TGCAtgca")


def gc_percent(seq: str, window: int = DEFAULT_GC_WINDOW, *, circular: bool = False) -> np.ndarray:
    """GC percent (0 to 100) per base over a centred ``window``; ``float32``, ``len(seq)`` long."""
    (window,) = validate_smoothing_windows([window])
    length = len(seq)
    if length == 0:
        return np.zeros(0, dtype=np.float32)
    upper = np.frombuffer(seq.upper().encode("ascii", errors="replace"), dtype=np.uint8)
    is_gc = ((upper == ord("G")) | (upper == ord("C"))).astype(np.float64)
    is_acgt = is_gc + ((upper == ord("A")) | (upper == ord("T"))).astype(np.float64)

    half = (window - 1) // 2
    index = np.arange(length)
    if circular:
        half = min(half, (length - 1) // 2)

        def sums(x: np.ndarray) -> np.ndarray:
            ext = np.concatenate((x[length - half :], x, x[:half])) if half else x
            c = np.concatenate(([0.0], np.cumsum(ext)))
            return c[2 * half + 1 : 2 * half + 1 + length] - c[:length]
    else:
        lo = np.maximum(index - half, 0)
        hi = np.minimum(index + half + 1, length)

        def sums(x: np.ndarray) -> np.ndarray:
            c = np.concatenate(([0.0], np.cumsum(x)))
            return c[hi] - c[lo]

    gc = sums(is_gc)
    total = sums(is_acgt)
    out = np.divide(100.0 * gc, total, out=np.zeros(length), where=total > 0.5)
    return np.clip(out, 0.0, 100.0).astype(np.float32)


@dataclass(frozen=True)
class CodonRow:
    """One codon's usage inside one CDS-shaped feature."""

    gene_id: str
    begin: int
    end: int
    strand: str
    codon: str
    amino_acid: str
    count: int
    frequency: float
    rscu: float
    deviation: float


def _feature_bases(seq: str, feature: GeneFeature) -> str:
    spans = feature.segments or ((feature.begin, feature.end),)
    joined = "".join(seq[a - 1 : b] for a, b in sorted(spans))
    joined = joined.upper()
    return joined.translate(_COMPLEMENT)[::-1] if feature.strand == "-" else joined


def codon_usage(seq: str, feature: GeneFeature) -> list[CodonRow] | None:
    """Codon usage for ``feature``, or ``None`` when it is not CDS-shaped (joined length not a
    multiple of three, or outside ``seq``). Rows are sorted by codon."""
    spans = feature.segments or ((feature.begin, feature.end),)
    if any(a < 1 or b > len(seq) or b < a for a, b in spans):
        return None
    bases = _feature_bases(seq, feature)
    if not bases or len(bases) % 3:
        return None
    counts: dict[str, int] = {}
    for i in range(0, len(bases), 3):
        codon = bases[i : i + 3]
        if codon in CODON_TABLE:
            counts[codon] = counts.get(codon, 0) + 1
    total = sum(counts.values())
    if total == 0:
        return []
    per_aa: dict[str, int] = {}
    for codon, n in counts.items():
        aa = CODON_TABLE[codon]
        per_aa[aa] = per_aa.get(aa, 0) + n
    rows = []
    for codon in sorted(counts):
        aa = CODON_TABLE[codon]
        rscu = counts[codon] / (per_aa[aa] / _SYNONYMS[aa])
        rows.append(
            CodonRow(
                gene_id=feature.gene_id,
                begin=feature.begin,
                end=feature.end,
                strand=feature.strand,
                codon=codon,
                amino_acid=aa,
                count=counts[codon],
                frequency=counts[codon] / total,
                rscu=rscu,
                deviation=rscu - 1.0,
            )
        )
    return rows
