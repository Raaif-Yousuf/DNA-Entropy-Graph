"""Issue #367 (parent #160): real, Hypothesis-generated property tests for windowing and
direction -- the half #160 filed as follow-up work because `hypothesis` was not installed
in `worker\\.venv` at the time (that session wrote the table-driven equivalent instead, in
`test_windowing.py`, `test_direction.py`, `test_entropy.py` -- those stay; this file is the
generated complement, not a replacement).

Four properties, matching the ones CLAUDE.md and #160 already name (see this module's own
docstring sections below for which):

1. Hard Rule 3, the ``(L, 4)`` contract: shape, dtype, row-sum, entropy bound -- for
   ARBITRARY generated sequences, not a handful of examples.
2. Hard Rule 4, windowing: every position covered by exactly one winning window, and the
   window count matches the closed-form ``ceil((L - W) / S) + 1`` formula, for generated
   ``(L, K, ceiling)`` triples across the whole valid input space, not a hand-picked grid.
3. Direction: entropy is invariant under the reverse-complement column permutation, for
   generated probability distributions.
4. Direction: the seam recorded in provenance is the exact index the combiner switches at,
   checked against an independently computed BOTH_SEPARATE run, for generated ``(K, L)``.

Budget: explicit `@settings` profiles below (never the Hypothesis default), because a
large `max_examples` is itself a RAM/time cost on a box running six concurrent agents
tonight. `LANE_F_WINDOW_SETTINGS` (pure Python/NumPy, no predictor call) affords more
examples; `LANE_F_DIRECTION_SETTINGS` (drives `MockPredictor.predict`, one or two softmax
passes per example) affords fewer. Both use `deadline=None` -- this laptop runs six
concurrent agents tonight, and Hypothesis's default per-example deadline is tuned for an
idle machine, not a shared one; wall-clock-based flakiness here would be a false property
failure, not a real one.
"""

from __future__ import annotations

import math

import numpy as np
import pytest
from hypothesis import HealthCheck, given, settings
from hypothesis import strategies as st
from hypothesis.extra.numpy import arrays

from dna_entropy.analysis import direction as direction_mod
from dna_entropy.analysis import windowing as windowing_mod
from dna_entropy.analysis.entropy import MAX_ENTROPY_BITS, shannon_entropy
from dna_entropy.config import Direction
from dna_entropy.predictors.mock import MockPredictor

LANE_F_WINDOW_SETTINGS = settings(
    max_examples=200,
    deadline=None,
    suppress_health_check=[HealthCheck.too_slow],
)
LANE_F_DIRECTION_SETTINGS = settings(
    max_examples=40,
    deadline=None,
    suppress_health_check=[HealthCheck.too_slow],
)

_ACGT = "ACGT"


def _covered_exactly_by_the_winning_window(plan) -> bool:
    """Reimplements `_stitch_forward`'s coverage guarantee (max-local-context wins) at the
    plan level, with no predictor involved -- the same helper `test_windowing.py`'s own
    table-driven version uses, duplicated here (not imported) so this file stays a single
    unit Lane F owns without reaching into another lane's test module."""
    context = [-1] * plan.length
    for start in plan.starts:
        width = plan.window if start + plan.window <= plan.length else plan.length - start
        for local in range(width):
            global_idx = start + local
            if local > context[global_idx]:
                context[global_idx] = local
    return all(c >= 0 for c in context) and len(context) == plan.length


# ---------------------------------------------------------------------------------------
# Property 1 (Hard Rule 4): window coverage, over the whole valid (L, K, ceiling) space.
# ---------------------------------------------------------------------------------------


@given(
    k=st.integers(min_value=1, max_value=5000),
    ceiling_extra=st.integers(min_value=1, max_value=10000),  # ceiling = k + extra > k, always valid
    length=st.integers(min_value=1, max_value=50000),
)
@LANE_F_WINDOW_SETTINGS
def test_property_every_position_covered_exactly_once(k: int, ceiling_extra: int, length: int) -> None:
    ceiling = k + ceiling_extra
    plan = windowing_mod.plan_windows(length=length, context_length=k, ceiling=ceiling)
    assert _covered_exactly_by_the_winning_window(plan), (length, k, ceiling)


@given(
    k=st.integers(min_value=1, max_value=5000),
    ceiling_extra=st.integers(min_value=1, max_value=10000),
    length=st.integers(min_value=1, max_value=50000),
)
@LANE_F_WINDOW_SETTINGS
def test_property_pass_count_matches_the_closed_form_ceil_formula(
    k: int, ceiling_extra: int, length: int
) -> None:
    ceiling = k + ceiling_extra
    plan = windowing_mod.plan_windows(length=length, context_length=k, ceiling=ceiling)
    if length <= plan.window:
        assert plan.num_windows == 1, (length, k, ceiling)
    else:
        expected = math.ceil((length - plan.window) / plan.stride) + 1
        assert plan.num_windows == expected, (length, k, ceiling, plan.num_windows, expected)


@given(
    k=st.integers(min_value=1, max_value=5000),
    ceiling_extra=st.integers(min_value=1, max_value=10000),
    length=st.integers(min_value=1, max_value=50000),
)
@LANE_F_WINDOW_SETTINGS
def test_property_starts_sorted_unique_and_the_last_window_reaches_the_end(
    k: int, ceiling_extra: int, length: int
) -> None:
    ceiling = k + ceiling_extra
    plan = windowing_mod.plan_windows(length=length, context_length=k, ceiling=ceiling)
    assert list(plan.starts) == sorted(set(plan.starts)), (length, k, ceiling)
    assert len(plan.starts) == len(set(plan.starts)), (length, k, ceiling)
    last_start = plan.starts[-1]
    assert last_start + plan.width_at(plan.num_windows - 1) == plan.length, (length, k, ceiling)


# ---------------------------------------------------------------------------------------
# Property 2 (Hard Rule 3): the (L, 4) contract, for generated sequences and seeds.
# ---------------------------------------------------------------------------------------


@given(
    seq=st.text(alphabet=_ACGT, min_size=1, max_size=500),
    seed=st.integers(min_value=0, max_value=2**31 - 1),
)
@LANE_F_DIRECTION_SETTINGS
def test_property_predictor_output_satisfies_the_l4_contract(seq: str, seed: int) -> None:
    probs = MockPredictor(seed=seed).predict(seq)
    assert probs.shape == (len(seq), 4)
    assert probs.dtype == np.float32
    assert np.all(probs >= 0.0) and np.all(probs <= 1.0)
    row_sums = probs.sum(axis=1)
    assert np.allclose(row_sums, 1.0, atol=1e-4), row_sums
    h = shannon_entropy(probs)
    assert h.shape == (len(seq),)
    assert not np.isnan(h).any()
    assert (h >= -1e-6).all()
    assert (h <= MAX_ENTROPY_BITS + 1e-6).all()


# ---------------------------------------------------------------------------------------
# Property 3: entropy invariant under the reverse-complement column permutation.
# ---------------------------------------------------------------------------------------


@given(
    logits=arrays(
        dtype=np.float64,
        shape=st.tuples(st.integers(min_value=1, max_value=40), st.just(4)),
        elements=st.floats(min_value=-50, max_value=50, allow_nan=False, allow_infinity=False),
    )
)
@LANE_F_WINDOW_SETTINGS
def test_property_entropy_invariant_under_complement_column_permutation(logits: np.ndarray) -> None:
    exp = np.exp(logits - logits.max(axis=1, keepdims=True))
    probs = (exp / exp.sum(axis=1, keepdims=True)).astype(np.float32)
    complement_perm = [3, 2, 1, 0]  # NUCLEOTIDES = (A, C, G, T): A<->T (0<->3), C<->G (1<->2)
    h_original = shannon_entropy(probs)
    h_complemented = shannon_entropy(probs[:, complement_perm])
    assert np.allclose(h_original, h_complemented, atol=1e-4), (logits, h_original, h_complemented)


@given(seq=st.text(alphabet=_ACGT, min_size=1, max_size=300))
@LANE_F_WINDOW_SETTINGS
def test_property_reverse_complement_is_involutive(seq: str) -> None:
    rc = direction_mod.reverse_complement
    assert rc(rc(seq)) == seq


@given(seq=st.text(alphabet=_ACGT, min_size=1, max_size=300))
@LANE_F_WINDOW_SETTINGS
def test_property_reverse_complement_is_the_complement_then_reverse_never_plain_reversal(seq: str) -> None:
    """ "Reverse" means reverse complement, never reversed text (Critical Pitfalls). Assert
    the actual defining relation -- complement each base, THEN reverse the whole string --
    rather than merely "differs from a plain reversal", which a palindromic or
    single-base-alphabet input could satisfy by accident for the wrong reason."""
    complement_map = str.maketrans("ACGT", "TGCA")
    assert direction_mod.reverse_complement(seq) == seq.translate(complement_map)[::-1]


# ---------------------------------------------------------------------------------------
# Property 4: the seam matches where the combiner actually switched, generated (K, L).
# ---------------------------------------------------------------------------------------


def _adversarial_seq(length: int) -> str:
    pattern = "ACGTGGCATCGA"
    return (pattern * (length // len(pattern) + 2))[:length]


@given(
    k=st.integers(min_value=1, max_value=30),
    extra=st.integers(min_value=0, max_value=200),
    seed=st.integers(min_value=0, max_value=2**31 - 1),
)
@LANE_F_DIRECTION_SETTINGS
def test_property_seam_matches_where_the_combiner_actually_switched(k: int, extra: int, seed: int) -> None:
    length = 2 * k + extra  # always >= 2K, so a clean seam is defined
    seq = _adversarial_seq(length)
    ceiling = max(2 * k + 1000, 8192)
    separate = direction_mod.analyze_direction(
        MockPredictor(seed=seed),
        seq,
        context_length=k,
        ceiling=ceiling,
        direction=Direction.BOTH_SEPARATE,
    )
    combined = direction_mod.analyze_direction(
        MockPredictor(seed=seed),
        seq,
        context_length=k,
        ceiling=ceiling,
        direction=Direction.BOTH_COMBINED,
    )
    assert combined.seam == k, (k, extra, seed)
    assert np.allclose(combined.values[:k], separate.reverse_values[:k], atol=1e-4), (k, extra, seed)
    assert np.allclose(combined.values[k:], separate.forward_values[k:], atol=1e-4), (k, extra, seed)


if __name__ == "__main__":  # pragma: no cover -- convenience for the mutation check only
    import sys

    sys.exit(pytest.main([__file__, "-q"]))
