"""Variant effect scan (issue #130): in-silico substitutions, one forward pass per variant.

Each variant is scored on the window ``pos +- K/2`` (``K`` = the context length) of the reference
and of the variant, so the cost is bounded by ``max_variants`` rather than by the sequence length:

- ``delta_surprisal``: total surprisal (bits) of the variant window minus the reference window,
  both against their own predictions. Positive means the model finds the variant window less
  likely overall.
- ``delta_mean_entropy``: mean entropy of the variant window minus the reference window.
- ``site_surprisal_ref`` / ``site_surprisal_alt``: ``-log2 P`` of the reference base and of the
  alternative base at the site, from the reference window's own prediction (the model's view of the
  site before the substitution).

Reference windows are predicted once per distinct window, so several alternatives at one site cost
``n + 1`` passes. The predictor is passed in (this module never imports a model: hard rule 1).
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from ..predictors.base import NUCLEOTIDES, Predictor
from ..readers.encoding import read_text
from ..validation.validators import ValidationError
from .entropy import shannon_entropy
from .surprisal import surprisal

DEFAULT_MAX_VARIANTS = 50
_SECONDS_PER_PASS_L4 = 1  # the issue's own estimate: about 1 s per forward pass on an L4


class VariantError(ValidationError):
    """The variant list cannot be used. A ``ValidationError`` so callers report it like bad input."""


@dataclass(frozen=True)
class Variant:
    pos: int  # 1-based, sequence-relative
    ref: str
    alt: str


@dataclass(frozen=True)
class VariantResult:
    variant: Variant
    window_start: int  # 1-based inclusive
    window_end: int
    delta_surprisal: float
    delta_mean_entropy: float
    site_surprisal_ref: float
    site_surprisal_alt: float


def _check_position(pos: int, seq: str, where: str) -> None:
    if not 1 <= pos <= len(seq):
        raise VariantError(
            f"{where}: position {pos} is outside the sequence (1..{len(seq)}). Use a position inside it."
        )


def parse_variants_tsv(path: str, seq: str) -> list[Variant]:
    """Read ``pos<TAB>ref<TAB>alt`` lines (an optional header, ``#`` comments and blank lines are
    skipped). Every reference base must match ``seq`` at its position."""
    out: list[Variant] = []
    for lineno, line in enumerate(read_text(path).replace("\r\n", "\n").split("\n"), start=1):
        if not line.strip() or line.startswith("#"):
            continue
        cols = [c.strip() for c in line.split("\t")]
        if cols[0].lower() in ("pos", "position"):
            continue
        where = f"Variants line {lineno}"
        if len(cols) < 3:
            raise VariantError(
                f"{where} needs three tab-separated columns: position, ref, alt. Fix the line."
            )
        try:
            pos = int(cols[0])
        except ValueError:
            raise VariantError(
                f"{where}: the position {cols[0]!r} is not a whole number. Fix the line."
            ) from None
        ref, alt = cols[1].upper(), cols[2].upper()
        _check_position(pos, seq, where)
        if alt not in NUCLEOTIDES or ref not in NUCLEOTIDES:
            raise VariantError(f"{where}: ref and alt must each be one of A, C, G or T. Fix the line.")
        if ref == alt:
            raise VariantError(f"{where}: ref and alt are the same base. Give a real substitution.")
        if seq[pos - 1] != ref:
            raise VariantError(
                f"{where}: the file says position {pos} is {ref} but the sequence has {seq[pos - 1]} there. "
                "Check the position and the sequence."
            )
        out.append(Variant(pos, ref, alt))
    if not out:
        raise VariantError("The variants file has no variants. Add lines like '12<TAB>A<TAB>G'.")
    return out


def positions_to_variants(positions: list[int], seq: str) -> list[Variant]:
    """Each position becomes its three possible substitutions."""
    out: list[Variant] = []
    for pos in positions:
        _check_position(pos, seq, "--positions")
        ref = seq[pos - 1]
        if ref not in NUCLEOTIDES:
            raise VariantError(
                f"--positions: position {pos} is {ref}, not A, C, G or T. Choose another position."
            )
        out += [Variant(pos, ref, b) for b in NUCLEOTIDES if b != ref]
    return out


def check_count(variants: list[Variant], max_variants: int) -> None:
    if len(variants) > max_variants:
        raise VariantError(
            f"{len(variants)} variants is more than the limit of {max_variants} (one forward pass each). "
            "Give fewer, or raise --max-variants if you accept the cost."
        )


def cost_estimate(n: int) -> str:
    noun = "forward pass " if n == 1 else "forward passes"
    return f"{n} {noun} (plus one per distinct window), about {n * _SECONDS_PER_PASS_L4} seconds on an L4 GPU"


def scan(seq: str, variants: list[Variant], predictor: Predictor, context_length: int) -> list[VariantResult]:
    """Score every variant, in input order."""
    half = max(context_length // 2, 1)
    ref_cache: dict[tuple[int, int], tuple[np.ndarray, str, float, float]] = {}
    results: list[VariantResult] = []
    for v in variants:
        pos0 = v.pos - 1
        lo, hi = max(0, pos0 - half), min(len(seq), pos0 + half + 1)
        if (lo, hi) not in ref_cache:
            ref_seq = seq[lo:hi]
            probs = predictor.predict(ref_seq)
            ref_cache[(lo, hi)] = (
                probs,
                ref_seq,
                float(surprisal(probs, ref_seq).sum()),
                float(shannon_entropy(probs).mean()),
            )
        ref_probs, ref_seq, ref_total, ref_entropy = ref_cache[(lo, hi)]
        rel = pos0 - lo
        alt_seq = ref_seq[:rel] + v.alt + ref_seq[rel + 1 :]
        alt_probs = predictor.predict(alt_seq)
        results.append(
            VariantResult(
                variant=v,
                window_start=lo + 1,
                window_end=hi,
                delta_surprisal=float(surprisal(alt_probs, alt_seq).sum()) - ref_total,
                delta_mean_entropy=float(shannon_entropy(alt_probs).mean()) - ref_entropy,
                site_surprisal_ref=float(
                    -np.log2(max(float(ref_probs[rel, NUCLEOTIDES.index(v.ref)]), 2**-20))
                ),
                site_surprisal_alt=float(
                    -np.log2(max(float(ref_probs[rel, NUCLEOTIDES.index(v.alt)]), 2**-20))
                ),
            )
        )
    return results
