"""Tests for analysis/direction.py: reverse-complement, windowed runs, and combination."""

from __future__ import annotations

import numpy as np
import pytest

from dna_entropy.analysis.direction import (
    DirectionResult,
    analyze_direction,
    reverse_complement,
    run_windowed,
)
from dna_entropy.config import Direction
from dna_entropy.predictors.base import PredictorOOMError, check_probability_matrix
from dna_entropy.predictors.mock import MockPredictor

# --- reverse complement: the single most expensive mistake to get wrong ---------------


def test_reverse_complement_maps_bases_correctly() -> None:
    assert reverse_complement("A") == "T"
    assert reverse_complement("T") == "A"
    assert reverse_complement("C") == "G"
    assert reverse_complement("G") == "C"


def test_reverse_uses_complement_not_reversed_text() -> None:
    # Plain reversal of "AACCGGTT" is "TTGGCCAA" (still a permutation of the same string).
    # Reverse-COMPLEMENT of "AACCGGTT" must instead be "AACCGGTT" swapped to complements
    # THEN reversed: complement("AACCGGTT") = "TTGGCCAA", reversed = "AACCGGTT".
    # Use a non-palindromic sequence to force a real distinction between the two:
    seq = "AAAACCCC"
    plain_reversed = seq[::-1]  # "CCCCAAAA"
    assert reverse_complement(seq) != plain_reversed
    assert reverse_complement(seq) == "GGGGTTTT"


def test_reverse_complement_is_involutive() -> None:
    seq = "ATGCGTACGTTAGCAACGTACGATCGATCG"
    assert reverse_complement(reverse_complement(seq)) == seq


def test_reverse_complement_preserves_length() -> None:
    seq = "ACGT" * 17
    assert len(reverse_complement(seq)) == len(seq)


# --- run_windowed: one predict() call per window, stitched back to full length --------


def test_run_windowed_single_window_matches_direct_predict() -> None:
    seq = "ACGTACGTACGTACGTACGT"  # well under any reasonable ceiling
    predictor = MockPredictor(seed=3)
    result = run_windowed(predictor, seq, context_length=4096, ceiling=8192)
    direct = MockPredictor(seed=3).predict(seq)
    assert np.array_equal(result.probs, direct)
    assert list(result.context) == list(range(len(seq)))  # position i has exactly i context
    assert result.window == 8192
    assert result.stride == 4096


def test_run_windowed_tiled_covers_every_position_with_valid_probs() -> None:
    seq = "ACGT" * 60  # 240 nt
    predictor = MockPredictor(seed=1)
    result = run_windowed(predictor, seq, context_length=32, ceiling=64)  # W=64, S=32
    assert result.probs.shape == (240, 4)
    check_probability_matrix(result.probs, 240)
    assert result.context.min() >= 0  # every position was covered by some window
    # Context strictly increases within the early window then resets pattern repeats;
    # at minimum every position should reach up to (window-1) context somewhere.
    assert result.context.max() == result.window - 1


class _OOMOnceThenOK:
    """Predictor stub: raises PredictorOOMError on the first call, succeeds after."""

    def __init__(self) -> None:
        self.calls = 0

    def predict(self, seq: str) -> np.ndarray:
        self.calls += 1
        if self.calls == 1:
            raise PredictorOOMError("simulated OOM")
        return MockPredictor(seed=0).predict(seq)


def test_run_windowed_retries_once_after_oom_with_halved_window() -> None:
    seq = "ACGT" * 10
    predictor = _OOMOnceThenOK()
    result = run_windowed(predictor, seq, context_length=8, ceiling=16)  # halved -> ceiling=8
    assert result.window == 8  # halved from 16
    assert any("out of gpu memory" in n.lower() for n in result.notices)
    check_probability_matrix(result.probs, len(seq))


def test_run_windowed_retry_actually_shrinks_a_k_bound_window() -> None:
    """issue #313/#315: the case above (context_length=8, ceiling=16) is the *tied*
    boundary W == 2K == ceiling, the one regime where the old, broken `halved()` also
    happened to work (K-bound and ceiling-bound coincide there). This test uses a K-bound
    plan WITH headroom (2K=8 well under ceiling=64, matching the default K=4096 on every
    GPU tier above an L4) -- the case the old code silently did nothing for, since halving
    the ceiling alone never moves a K-bound window."""
    seq = "ACGT" * 10
    predictor = _OOMOnceThenOK()
    result = run_windowed(predictor, seq, context_length=4, ceiling=64)
    assert result.window == 4  # halved from 8 (== 2*4, K-bound -- ceiling was never close)
    assert any("out of gpu memory" in n.lower() for n in result.notices)
    check_probability_matrix(result.probs, len(seq))


class _AlwaysOOM:
    def predict(self, seq: str) -> np.ndarray:
        raise PredictorOOMError("simulated OOM")


def test_run_windowed_propagates_a_second_oom() -> None:
    with pytest.raises(PredictorOOMError):
        run_windowed(_AlwaysOOM(), "ACGT" * 10, context_length=8, ceiling=16)


# --- analyze_direction: forward-only / reverse-only ------------------------------------


def test_forward_only_matches_run_windowed_forward_pass() -> None:
    seq = "ACGTACGTACGTACGTACGT"
    predictor = MockPredictor(seed=5)
    result = analyze_direction(
        predictor,
        seq,
        context_length=4096,
        ceiling=8192,
        direction=Direction.FORWARD_ONLY,
    )
    expected = run_windowed(MockPredictor(seed=5), seq, context_length=4096, ceiling=8192)
    from dna_entropy.analysis.entropy import shannon_entropy

    assert np.array_equal(result.values, shannon_entropy(expected.probs))
    assert result.forward_values is None  # only populated for BOTH_SEPARATE
    assert result.reverse_values is None


def test_reverse_only_uses_reverse_complement() -> None:
    seq = "ACGTACGTACGTACGTACGT"
    predictor = MockPredictor(seed=5)
    fwd_result = analyze_direction(
        MockPredictor(seed=5),
        seq,
        context_length=4096,
        ceiling=8192,
        direction=Direction.FORWARD_ONLY,
    )
    rev_result = analyze_direction(
        predictor,
        seq,
        context_length=4096,
        ceiling=8192,
        direction=Direction.REVERSE_ONLY,
    )
    # Different direction of the same deterministic mock predictor sees a different
    # (reverse-complemented) string, so it must NOT equal the forward-only track.
    assert not np.array_equal(fwd_result.values, rev_result.values)
    assert rev_result.values.shape == (len(seq),)


# --- both-combined: the seam at K, and the L>=2K clean split --------------------------


def test_both_combined_seam_is_at_context_length_when_l_at_least_2k() -> None:
    K = 50
    seq = "ACGT" * 40  # L=160 >= 2*50
    predictor = MockPredictor(seed=9)
    result = analyze_direction(
        predictor,
        seq,
        context_length=K,
        ceiling=200,
        direction=Direction.BOTH_COMBINED,
    )
    assert result.seam == K


def test_direction_result_records_the_ceiling_it_was_run_with() -> None:
    # WindowPlan.ceiling was set and never read anywhere (found during the lane-B audit);
    # DirectionResult now threads the SAME ceiling value analyze_direction was called
    # with, so a report can show why window < 2*context_length (the ceiling clamped it)
    # without digging through notice text.
    K = 50
    seq = "ACGT" * 40  # L=160
    result = analyze_direction(
        MockPredictor(seed=9),
        seq,
        context_length=K,
        ceiling=75,  # deliberately less than 2K=100, so W is ceiling-bound
        direction=Direction.BOTH_COMBINED,
    )
    assert result.ceiling == 75
    assert result.window < 2 * K  # sanity: the ceiling really did clamp W


def test_both_combined_first_k_bases_come_from_reverse_rest_from_forward() -> None:
    """This is THE test that would catch reversed-text-instead-of-reverse-complement."""
    K = 50
    seq = "ACGT" * 40  # L=160
    fwd = analyze_direction(
        MockPredictor(seed=9),
        seq,
        context_length=K,
        ceiling=200,
        direction=Direction.FORWARD_ONLY,
    )
    rev = analyze_direction(
        MockPredictor(seed=9),
        seq,
        context_length=K,
        ceiling=200,
        direction=Direction.REVERSE_ONLY,
    )
    combined = analyze_direction(
        MockPredictor(seed=9),
        seq,
        context_length=K,
        ceiling=200,
        direction=Direction.BOTH_COMBINED,
    )
    # First K bases: reverse read wins (matches the reverse-only track exactly).
    assert np.array_equal(combined.values[:K], rev.values[:K])
    # Remaining bases: forward read wins (matches the forward-only track exactly).
    assert np.array_equal(combined.values[K:], fwd.values[K:])


def test_both_combined_reduces_to_forward_only_when_l_le_w_and_reverse_never_qualifies() -> None:
    # A single-window forward pass (L <= W) means forward_context[i] = i, so forward
    # never reaches full K until i >= K; verifies the seam falls at the SAME place as the
    # general case even in the single-window regime.
    K = 20
    seq = "ACGT" * 30  # L = 120, still >= 2K = 40
    result = analyze_direction(
        MockPredictor(seed=2),
        seq,
        context_length=K,
        ceiling=8192,
        direction=Direction.BOTH_COMBINED,
    )
    assert result.seam == K
    assert result.reduced_context_count == 0


# --- both-averaged ----------------------------------------------------------------------


def test_both_averaged_takes_mean_where_both_qualify() -> None:
    K = 50
    seq = "ACGT" * 40  # L=160
    fwd = analyze_direction(
        MockPredictor(seed=4),
        seq,
        context_length=K,
        ceiling=8192,
        direction=Direction.FORWARD_ONLY,
    )
    rev = analyze_direction(
        MockPredictor(seed=4),
        seq,
        context_length=K,
        ceiling=8192,
        direction=Direction.REVERSE_ONLY,
    )
    avg = analyze_direction(
        MockPredictor(seed=4),
        seq,
        context_length=K,
        ceiling=8192,
        direction=Direction.BOTH_AVERAGED,
    )
    # With L=160, K=50: the middle region [K, L-K) = [50, 110) has BOTH directions
    # qualifying (>=K context each way) -> averaged there.
    mid = slice(K, len(seq) - K)
    expected_mid = (fwd.values[mid].astype(np.float64) + rev.values[mid].astype(np.float64)) / 2.0
    assert np.allclose(avg.values[mid], expected_mid, atol=1e-5)
    # Outside that region only one direction qualifies -> matches averaged's fallback,
    # which is identical to both-combined's fallback (not an average).
    assert np.array_equal(avg.values[:K], rev.values[:K])


# --- reduced context: only possible when L < 2K ----------------------------------------


def test_reduced_context_recorded_when_l_less_than_2k() -> None:
    K = 100
    seq = "ACGT" * 30  # L=120 < 2K=200
    result = analyze_direction(
        MockPredictor(seed=6),
        seq,
        context_length=K,
        ceiling=8192,
        direction=Direction.BOTH_COMBINED,
    )
    assert result.reduced_context_count > 0
    assert result.seam is None  # no clean seam when L < 2K
    assert any("reduced context" in n.lower() for n in result.notices)


def test_no_reduced_context_when_l_at_least_2k() -> None:
    K = 20
    seq = "ACGT" * 30  # L=120 >= 2K=40
    result = analyze_direction(
        MockPredictor(seed=6),
        seq,
        context_length=K,
        ceiling=8192,
        direction=Direction.BOTH_COMBINED,
    )
    assert result.reduced_context_count == 0


# --- both-separate: forward/reverse tracks populated, plus the combined track ---------


def test_both_separate_populates_forward_and_reverse_values() -> None:
    K = 20
    seq = "ACGT" * 30
    result = analyze_direction(
        MockPredictor(seed=7),
        seq,
        context_length=K,
        ceiling=8192,
        direction=Direction.BOTH_SEPARATE,
    )
    assert result.forward_values is not None
    assert result.reverse_values is not None
    assert result.forward_values.shape == (len(seq),)
    assert result.reverse_values.shape == (len(seq),)
    # The "combined" values field is still populated using the both-combined rule.
    combined = analyze_direction(
        MockPredictor(seed=7),
        seq,
        context_length=K,
        ceiling=8192,
        direction=Direction.BOTH_COMBINED,
    )
    assert np.array_equal(result.values, combined.values)


def test_direction_result_is_the_expected_dataclass() -> None:
    result = analyze_direction(
        MockPredictor(seed=0),
        "ACGT" * 10,
        context_length=8,
        ceiling=64,
        direction=Direction.FORWARD_ONLY,
    )
    assert isinstance(result, DirectionResult)
    assert result.context_length == 8
    assert result.window == 16  # min(2*8, 64)
    assert result.stride == 8
