"""Tests for analysis/surprisal.py (issue #123): -log2 P(actual base), alongside entropy.

Entropy says how uncertain the model is; surprisal says how surprised it is by the base
that is ACTUALLY there. Falls out of the same (L, 4) matrix and the sequence at zero
extra GPU cost.
"""

from __future__ import annotations

import numpy as np
import pytest

from dna_entropy.analysis.entropy import shannon_entropy
from dna_entropy.analysis.surprisal import (
    MAX_SURPRISAL_BITS,
    SurprisalSummary,
    summarize_surprisal,
    surprisal,
)


def test_one_hot_on_actual_base_is_zero() -> None:
    # The model put ALL its mass on the base that is actually there -> zero surprise.
    probs = np.array([[1.0, 0.0, 0.0, 0.0]], dtype=np.float32)  # A
    s = surprisal(probs, "A")
    assert np.allclose(s, 0.0, atol=1e-6)


def test_uniform_is_two_bits_regardless_of_actual_base() -> None:
    probs = np.array([[0.25, 0.25, 0.25, 0.25]], dtype=np.float32)
    for base in "ACGT":
        s = surprisal(probs, base)
        assert np.allclose(s, 2.0, atol=1e-6)


def test_mismatch_is_high_surprisal_even_at_low_entropy() -> None:
    # The model is confident (low entropy) but WRONG about this specific base -> high
    # surprisal. This is exactly the mutation-spotting signal issue #123 describes.
    probs = np.array([[0.99, 0.01 / 3, 0.01 / 3, 0.01 / 3]], dtype=np.float32)  # confident: A
    entropy_here = shannon_entropy(probs)[0]
    s_correct = surprisal(probs, "A")[0]
    s_wrong = surprisal(probs, "T")[0]
    assert entropy_here < 0.2  # low entropy: the model IS confident
    assert s_correct < 0.1  # right base: low surprisal
    assert s_wrong > 6.0  # wrong base: high surprisal despite low entropy


def test_observable_from_the_issue_p_0_01_is_6_64_bits() -> None:
    # "A position where the actual base has probability 0.01 shows surprisal 6.64 bits."
    probs = np.array([[0.01, 0.33, 0.33, 0.33]], dtype=np.float32)
    s = surprisal(probs, "A")
    assert np.isclose(s[0], 6.643856, atol=1e-3)


def test_shape_and_dtype() -> None:
    probs = np.full((10, 4), 0.25, dtype=np.float32)
    s = surprisal(probs, "A" * 10)
    assert s.shape == (10,)
    assert s.dtype == np.float32


def test_length_mismatch_raises() -> None:
    probs = np.full((5, 4), 0.25, dtype=np.float32)
    with pytest.raises(ValueError):
        surprisal(probs, "ACG")  # len 3 != 5


# --- row 0 in Forward-only mode: uniform by design, so surprisal is 2.0 for ANY base ---


def test_forward_only_row_zero_is_two_bits_for_any_base() -> None:
    # CLAUDE.md: row 0 has no preceding context in Forward-only mode, so it is exactly
    # uniform (2.0 bits) BY DESIGN, not a bug. Surprisal there must be 2.0 regardless of
    # which base is actually first -- this is not something to "fix" by special-casing.
    probs = np.array([[0.25, 0.25, 0.25, 0.25], [0.7, 0.1, 0.1, 0.1]], dtype=np.float32)
    for base in "ACGT":
        s = surprisal(probs, base + "A")
        assert np.isclose(s[0], 2.0, atol=1e-6)


# --- ambiguity codes: no single "actual base" -> falls back to the row's entropy -------
#
# DECISION (agent-made, reversible): an ambiguity code (or anything else that made it
# through validation under AmbiguityPolicy.KEEP) has no single column to index into the
# (L, 4) contract. Falling back to the row's Shannon entropy is not an arbitrary choice:
# entropy IS the model's own EXPECTED surprisal (entropy = E_p[-log2 p] by definition), so
# this is the mathematically principled value to report when the true base is unknown,
# and it stays bounded in [0.0, 2.0] like every other entropy value instead of requiring
# a new IUPAC-ambiguity-set decode table.


def test_ambiguity_code_falls_back_to_entropy() -> None:
    probs = np.array([[0.7, 0.1, 0.1, 0.1]], dtype=np.float32)
    entropy_here = shannon_entropy(probs)
    s = surprisal(probs, "N")
    assert np.allclose(s, entropy_here, atol=1e-6)


@pytest.mark.parametrize("code", list("NRYSWKMBDHV"))
def test_every_iupac_ambiguity_code_falls_back_to_entropy(code: str) -> None:
    rng = np.random.default_rng(hash(code) % (2**31))
    logits = rng.standard_normal((1, 4))
    exp = np.exp(logits - logits.max())
    probs = (exp / exp.sum()).astype(np.float32)
    entropy_here = shannon_entropy(probs)
    s = surprisal(probs, code)
    assert np.allclose(s, entropy_here, atol=1e-6)


# --- range: unbounded above as P -> 0 (unlike entropy's [0, 2]) -> documented clamp ----


def test_zero_probability_actual_base_is_clamped_not_infinite() -> None:
    probs = np.array([[1.0, 0.0, 0.0, 0.0]], dtype=np.float32)  # model is CERTAIN it's A
    s = surprisal(probs, "T")  # but it's actually T: P(T) = 0.0 -> -log2(0) = inf
    assert np.isfinite(s).all()
    assert s[0] == MAX_SURPRISAL_BITS


def test_clamp_constant_is_well_above_realistic_confidence() -> None:
    # Any float32 probability representable above 0 gives a finite, clamped value.
    tiny = np.array([[np.float32(1e-30), 0.5, 0.25, 0.25 - np.float32(1e-30)]], dtype=np.float32)
    s = surprisal(tiny, "A")
    assert s[0] == MAX_SURPRISAL_BITS  # clamped, not the raw ~99.6 bits


# --- summary: per-contig mean and total log-likelihood ---------------------------------


def test_summarize_reports_mean_and_total_log_likelihood() -> None:
    probs = np.array(
        [
            [1.0, 0.0, 0.0, 0.0],  # A, correct, exact -> 0 bits
            [0.25, 0.25, 0.25, 0.25],  # A, uniform -> 2 bits
        ],
        dtype=np.float32,
    )
    s = surprisal(probs, "AA")
    summary = summarize_surprisal(s, seq="AA")
    assert isinstance(summary, SurprisalSummary)
    assert np.isclose(summary.mean, 1.0, atol=1e-6)
    # total log-likelihood (bits) = sum(log2 P(actual base)) = -sum(surprisal) over DEFINED
    # bases only (both bases here are real A/C/G/T, so both count): -(0.0 + 2.0) = -2.0.
    assert np.isclose(summary.total_log_likelihood_bits, -2.0, atol=1e-6)
    assert summary.defined_count == 2


def test_summarize_excludes_ambiguity_fallback_positions_from_log_likelihood() -> None:
    probs = np.array(
        [
            [1.0, 0.0, 0.0, 0.0],  # A, correct -> 0 bits, DEFINED
            [0.25, 0.25, 0.25, 0.25],  # N -> entropy fallback, NOT a real observation
        ],
        dtype=np.float32,
    )
    s = surprisal(probs, "AN")
    summary = summarize_surprisal(s, seq="AN")
    assert summary.defined_count == 1  # only the 'A' position is a real observation
    assert np.isclose(summary.total_log_likelihood_bits, 0.0, atol=1e-6)  # just the 'A' term
    # mean is still over the WHOLE track (matches EntropySummary's own convention).
    assert np.isclose(summary.mean, s.mean(), atol=1e-6)
