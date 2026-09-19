"""Per-position surprisal: ``-log2 P(actual base)``, alongside entropy (issue #123).

Entropy says how uncertain the model is; surprisal says how surprised it is by the base
that is ACTUALLY there. A low-entropy, high-surprisal position is exactly where the real
sequence deviates from what a conserved region "should" be -- the mutation-spotting
signal this issue asks for. It falls straight out of the same ``(L, 4)`` matrix and the
sequence itself, at zero extra GPU cost (no second forward pass, no extra windowing).
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from ..predictors.base import NUCLEOTIDES
from .entropy import shannon_entropy

# Surprisal is unbounded above as P -> 0 (unlike entropy's natural [0.0, 2.0] ceiling from
# a 4-symbol alphabet), so a viewer axis needs a documented, finite ceiling rather than
# whatever a run's data happens to contain. 20.0 bits corresponds to P >= 2**-20
# (~9.5e-7) -- generous (a real Evo prediction essentially never assigns a lower
# probability to any of the 4 nucleotide tokens) and a clean round number for an axis
# label. A track value pinned at this ceiling means "the model was confident and wrong
# beyond what this track bothers to distinguish further," not a literal probability.
MAX_SURPRISAL_BITS: float = 20.0

_BASE_TO_COLUMN: dict[str, int] = {b: i for i, b in enumerate(NUCLEOTIDES)}
_COLUMN_LUT: np.ndarray = np.full(256, -1, dtype=np.int16)
for _base, _col in _BASE_TO_COLUMN.items():
    _COLUMN_LUT[ord(_base)] = _col
del _base, _col


def _columns_of(seq: str) -> np.ndarray:
    """Map each character of ``seq`` to its NUCLEOTIDES column index, or ``-1`` if it is
    not one of A/C/G/T (an IUPAC ambiguity code or anything else)."""
    codes = np.frombuffer(seq.encode("ascii", errors="replace"), dtype=np.uint8)
    return _COLUMN_LUT[codes]


def surprisal(probs: np.ndarray, seq: str) -> np.ndarray:
    """Compute per-position surprisal in bits: ``-log2(P(actual base))``.

    Args:
        probs: ``(L, 4)`` per-position probabilities (the SAME array entropy is computed
            from), columns ``[A, C, G, T]`` (:data:`~dna_entropy.predictors.base.NUCLEOTIDES`).
        seq: the length-``L`` sequence the probabilities were predicted for. Uppercase.

    Returns:
        ``(L,)`` float32 array, clamped to ``[0.0, MAX_SURPRISAL_BITS]``.

    At a position whose actual base is one of A/C/G/T, this is exactly
    ``-log2(probs[i, column_of(seq[i])])`` — 0 bits when the model was fully confident
    and right, clamped to :data:`MAX_SURPRISAL_BITS` when it was fully confident and
    WRONG. Row 0 in Forward-only mode is uniform by design (CLAUDE.md: no preceding
    context, 2.0 bits), so surprisal there is exactly 2.0 for ANY actual base — this is
    not a bug to special-case away.

    At a position whose actual base is an IUPAC ambiguity code (or anything else that
    made it through validation under ``AmbiguityPolicy.KEEP``), there is no single
    "actual base" to index into the 4-column contract. DECISION (agent-made,
    reversible): fall back to the row's Shannon entropy there. This is not an arbitrary
    choice — entropy IS the model's own EXPECTED surprisal (``entropy = E_p[-log2 p]`` by
    definition), so it is the mathematically principled value to report when the true
    base is unknown, and it stays bounded in ``[0.0, 2.0]`` like every other entropy
    value rather than requiring a new IUPAC-ambiguity-set decode table this issue never
    asked for. If a future issue wants a different treatment (e.g. decoding the
    ambiguity code's possible-base set and averaging), that is a new, separate decision.

    Raises:
        ValueError: if ``probs`` is not ``(len(seq), 4)``.
    """
    if probs.ndim != 2 or probs.shape[0] != len(seq) or probs.shape[1] != len(NUCLEOTIDES):
        raise ValueError(f"probs must be (len(seq)={len(seq)}, {len(NUCLEOTIDES)}), got {probs.shape}")

    length = probs.shape[0]
    fallback = shannon_entropy(probs)  # (L,) float32 -- also the ambiguity-code answer
    if length == 0:
        return fallback

    columns = _columns_of(seq)  # (L,) in {-1, 0, 1, 2, 3}

    out = fallback.copy()
    defined = columns >= 0
    if defined.any():
        rows = np.nonzero(defined)[0]
        p = probs[rows, columns[defined]].astype(np.float64)
        s = np.full(p.shape, MAX_SURPRISAL_BITS, dtype=np.float64)
        positive = p > 0.0
        s[positive] = np.minimum(-np.log2(p[positive]), MAX_SURPRISAL_BITS)
        out[rows] = s.astype(np.float32)
    return out


@dataclass
class SurprisalSummary:
    """Summary statistics for a surprisal track."""

    length: int
    mean: float  # over the WHOLE track (ambiguity-fallback positions included), like EntropySummary
    # Total log-likelihood in bits: sum(log2 P(actual base)) = -sum(surprisal), over
    # DEFINED (real A/C/G/T) positions ONLY -- an ambiguity-fallback position is not a
    # real observation of a specific symbol, so it contributes no log-likelihood term.
    total_log_likelihood_bits: float
    defined_count: int  # how many positions actually contributed to the log-likelihood


def summarize_surprisal(values: np.ndarray, *, seq: str) -> SurprisalSummary:
    """Compute :class:`SurprisalSummary` for a ``(L,)`` surprisal array and its sequence."""
    defined = _columns_of(seq) >= 0
    defined_values = values[defined]
    return SurprisalSummary(
        length=int(values.shape[0]),
        mean=float(values.mean()) if values.size else 0.0,
        total_log_likelihood_bits=float(-defined_values.sum()) if defined_values.size else 0.0,
        defined_count=int(defined.sum()),
    )
