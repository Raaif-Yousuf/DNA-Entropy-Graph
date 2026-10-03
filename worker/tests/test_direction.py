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
from dna_entropy.analysis.entropy import shannon_entropy
from dna_entropy.analysis.surprisal import surprisal as compute_surprisal
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


def test_reverse_complement_complements_every_iupac_ambiguity_code() -> None:
    """MEASURED 2026-10-02 (issue #78): `reverse_complement("ACGTRYKMBDHVN")` used to return
    "NVHDBMKYRACGT" -- reversed but R, Y, K, M, B, D, H, V left uncomplemented."""
    pairs = {"R": "Y", "Y": "R", "K": "M", "M": "K", "B": "V", "V": "B", "D": "H", "H": "D"}
    for code, comp in pairs.items():
        assert reverse_complement(code) == comp
    for self_comp in "NSW":
        assert reverse_complement(self_comp) == self_comp
    assert reverse_complement("ACGTRYKMBDHVN") == "NBDHVKMRYACGT"[::1]


def test_the_reverse_pass_feeds_the_model_the_true_reverse_complement_of_a_kept_ambiguity_code() -> None:
    """The observable: under ambiguityPolicy=keep an R reaches the predictor; the reverse
    pass must hand it a Y at the mirrored position, not another R."""
    seen: list[str] = []

    class _Recording:
        def predict(self, window: str) -> np.ndarray:
            seen.append(window)
            return MockPredictor(seed=0).predict(window)

    seq = "ACGT" * 3 + "R" + "ACGT" * 3
    analyze_direction(_Recording(), seq, context_length=8, ceiling=1024, direction=Direction.REVERSE_ONLY)
    rc = reverse_complement(seq)
    assert seen and all(w in rc for w in seen), "the model saw something other than rc windows"
    assert not any("R" in w for w in seen)
    assert any("Y" in w for w in seen)


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


# --- issue #407: analyze_direction() after a one-sided OOM-halving retry ---------------
#
# MEASURED 2026-09-19: filed as THEORY (unverified), then reproduced by hand before this
# fix. `analyze_direction()` calls `run_windowed` once for forward, once for reverse, on
# the SAME shared predictor instance; each `run_windowed` call only reacts to ITS OWN OOM
# (issue #313's halving). When the OOM lands during the FIRST predictor call (the forward
# pass, since analyze_direction always runs forward before reverse), forward halves its K
# but reverse -- recovering already, no longer erroring -- runs at the FULL, un-halved K.
# The pre-fix `_combine` used ONE shared, nominal `context_length` threshold for both
# `fwd_ok`/`rev_ok`; forward's own achieved context is capped at its new, smaller
# `window - 1`, which can fall permanently below that stale, larger threshold, so forward
# NEVER qualifies again for the rest of the sequence even once it re-establishes its own
# (smaller) K -- combined mode silently collapses to reverse-only past the halving point,
# with no error. Confirmed by hand: at a position where forward's own post-halving K
# (`window - stride`) was satisfied but the stale nominal K was not, the pre-fix combined
# value equalled the REVERSE track, not forward -- fixed by giving `_combine` each
# direction's own actually-achieved `window - stride` instead of one shared value.


def test_both_combined_after_a_forward_only_oom_halving_lets_forward_requalify_at_its_own_k() -> None:
    seq = _adversarial_seq(20000)
    K, ceiling = 4096, 16384  # K-bound: old window = 2K = 8192

    shared = _OOMOnceThenOK()  # OOMs on the very first predict() call: the forward pass
    fwd_only = analyze_direction(
        _OOMOnceThenOK(), seq, context_length=K, ceiling=ceiling, direction=Direction.FORWARD_ONLY
    )
    assert fwd_only.window < 2 * K  # confirms the forward pass really did halve

    k_used_fwd = fwd_only.window - fwd_only.stride
    probe = k_used_fwd + 10  # qualifies under forward's OWN new K, not under the stale K

    combined = analyze_direction(
        shared, seq, context_length=K, ceiling=ceiling, direction=Direction.BOTH_COMBINED
    )
    # Before the fix this equalled the REVERSE track at `probe` (forward locked out by the
    # stale, pre-halving K); after the fix, forward qualifies at its own achieved K and
    # (both directions qualifying here) forward wins, matching the forward-only track.
    assert np.isclose(combined.values[probe], fwd_only.values[probe], atol=1e-5)


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


# --- property, table-driven adversarial grid (issue #160, direction scope only) --------
#
# `hypothesis` is not installed in worker\.venv on this laptop (verified: `import
# hypothesis` -> ModuleNotFoundError); per the brief, nothing was installed to get it.
# This is the table-driven equivalent over a deliberately adversarial grid rather than a
# generated one -- the real Hypothesis version is filed as its own issue instead of
# half-implemented here. A tolerance is used throughout, never exact float equality
# (MEASURED 2026-09-19: a log2-based parity fixture elsewhere in this repo passed on
# Windows and failed on Linux CI over an exact-equality assertion on the last bit).


def _adversarial_seq(length: int) -> str:
    """A deterministic ACGT sequence of exactly `length` bases with no long homopolymer
    run, so a reversed-text bug and a real reverse-complement bug would not
    coincidentally produce the same result."""
    pattern = "ACGTGGCATCGA"
    return (pattern * (length // len(pattern) + 2))[:length]


def test_entropy_is_invariant_under_the_complement_column_permutation() -> None:
    """The whole justification for combining a forward and a reverse-complement pass
    (module docstring): complementing (A<->T, C<->G) is just a relabelling of the same
    four symbols, so it must never change how spread-out a distribution is. Swapping the
    A/T and C/G probability COLUMNS must leave shannon_entropy unchanged for every
    distribution -- tested as a property over a grid, not one fixture.
    """
    # NUCLEOTIDES = ("A", "C", "G", "T"): complement column permutation is A<->T (0<->3),
    # C<->G (1<->2).
    complement_perm = [3, 2, 1, 0]
    for seed in (0, 1, 2, 7, 99):
        for length in (1, 2, 5, 50):
            rng = np.random.default_rng(seed)
            logits = rng.standard_normal((length, 4))
            exp = np.exp(logits - logits.max(axis=1, keepdims=True))
            probs = (exp / exp.sum(axis=1, keepdims=True)).astype(np.float32)
            h_original = shannon_entropy(probs)
            h_complemented = shannon_entropy(probs[:, complement_perm])
            assert np.allclose(h_original, h_complemented, atol=1e-5), (seed, length)


_ADVERSARIAL_K = [1, 2, 5, 20]


def _adversarial_lengths_at_least_2k(k: int) -> list[int]:
    # Seam is only defined for L >= 2K; sweep exactly-2K, one-past, and further out.
    return sorted({2 * k, 2 * k + 1, 2 * k + 5, 10 * k + 3})


@pytest.mark.parametrize("k", _ADVERSARIAL_K)
def test_seam_position_matches_where_the_combiner_actually_switched(k: int) -> None:
    """The seam recorded in provenance must be the EXACT index the combined track
    switches from reverse-sourced to forward-sourced values -- checked against an
    independently computed BOTH_SEPARATE run with the identically-seeded predictor
    (the same determinism `test_both_separate_populates_forward_and_reverse_values`
    relies on), not merely "seam == K" taken on faith.
    """
    for length in _adversarial_lengths_at_least_2k(k):
        seq = _adversarial_seq(length)
        separate = analyze_direction(
            MockPredictor(seed=42),
            seq,
            context_length=k,
            ceiling=max(2 * k + 1000, 8192),
            direction=Direction.BOTH_SEPARATE,
        )
        combined = analyze_direction(
            MockPredictor(seed=42),
            seq,
            context_length=k,
            ceiling=max(2 * k + 1000, 8192),
            direction=Direction.BOTH_COMBINED,
        )
        seam = combined.seam
        assert seam == k, (length, k)
        # Before the seam: combined must equal the reverse-only track (reduced tolerance,
        # never exact float equality per Hard Rule/MEASURED note above).
        assert np.allclose(combined.values[:seam], separate.reverse_values[:seam], atol=1e-5), (length, k)
        # At and after the seam: combined must equal the forward-only track.
        assert np.allclose(combined.values[seam:], separate.forward_values[seam:], atol=1e-5), (length, k)


# --- issue #123: surprisal is combined alongside entropy, by the SAME rule -------------


def test_forward_only_surprisal_matches_direct_computation() -> None:
    seq = "ACGTACGTACGTACGTACGT"
    predictor = MockPredictor(seed=5)
    result = analyze_direction(
        predictor,
        seq,
        context_length=4096,
        ceiling=8192,
        direction=Direction.FORWARD_ONLY,
    )
    expected_probs = run_windowed(MockPredictor(seed=5), seq, context_length=4096, ceiling=8192).probs
    assert result.surprisal_values is not None
    assert np.array_equal(result.surprisal_values, compute_surprisal(expected_probs, seq))


def test_reverse_only_surprisal_uses_the_reverse_complement_sequence() -> None:
    seq = "ACGTACGTACGTACGTACGT"
    predictor = MockPredictor(seed=5)
    result = analyze_direction(
        predictor,
        seq,
        context_length=4096,
        ceiling=8192,
        direction=Direction.REVERSE_ONLY,
    )
    rc = reverse_complement(seq)
    expected_probs = run_windowed(MockPredictor(seed=5), rc, context_length=4096, ceiling=8192).probs
    expected = compute_surprisal(expected_probs, rc)[::-1]
    assert np.array_equal(result.surprisal_values, expected)


def test_both_combined_surprisal_follows_the_same_seam_as_entropy() -> None:
    """This is THE test that would catch surprisal being combined by a DIFFERENT rule
    than entropy (e.g. always forward, or a fresh/independent context decision)."""
    K = 50
    seq = "ACGT" * 40  # L=160
    fwd = analyze_direction(
        MockPredictor(seed=9), seq, context_length=K, ceiling=200, direction=Direction.FORWARD_ONLY
    )
    rev = analyze_direction(
        MockPredictor(seed=9), seq, context_length=K, ceiling=200, direction=Direction.REVERSE_ONLY
    )
    combined = analyze_direction(
        MockPredictor(seed=9), seq, context_length=K, ceiling=200, direction=Direction.BOTH_COMBINED
    )
    assert combined.surprisal_values is not None
    # First K bases: reverse read wins (matches the reverse-only surprisal exactly).
    assert np.array_equal(combined.surprisal_values[:K], rev.surprisal_values[:K])
    # Remaining bases: forward read wins (matches the forward-only surprisal exactly).
    assert np.array_equal(combined.surprisal_values[K:], fwd.surprisal_values[K:])


def test_both_averaged_surprisal_takes_mean_where_both_qualify() -> None:
    K = 50
    seq = "ACGT" * 40  # L=160
    fwd = analyze_direction(
        MockPredictor(seed=4), seq, context_length=K, ceiling=8192, direction=Direction.FORWARD_ONLY
    )
    rev = analyze_direction(
        MockPredictor(seed=4), seq, context_length=K, ceiling=8192, direction=Direction.REVERSE_ONLY
    )
    avg = analyze_direction(
        MockPredictor(seed=4), seq, context_length=K, ceiling=8192, direction=Direction.BOTH_AVERAGED
    )
    mid = slice(K, len(seq) - K)
    expected_mid = (
        fwd.surprisal_values[mid].astype(np.float64) + rev.surprisal_values[mid].astype(np.float64)
    ) / 2.0
    assert np.allclose(avg.surprisal_values[mid], expected_mid, atol=1e-5)


def test_both_separate_still_populates_the_combined_surprisal_values() -> None:
    # DirectionResult deliberately has NO forward_surprisal/reverse_surprisal pair (unlike
    # forward_values/reverse_values): check_unused_fields.py flagged both as genuinely
    # unread when this session first added them (no writer consumes per-direction
    # surprisal yet), so they were removed rather than left "for later" -- same reasoning
    # as WindowPlan.context's removal. surprisal_values (the combined track) is still
    # always populated, including under BOTH_SEPARATE.
    K = 20
    seq = "ACGT" * 30
    result = analyze_direction(
        MockPredictor(seed=7), seq, context_length=K, ceiling=8192, direction=Direction.BOTH_SEPARATE
    )
    assert not hasattr(result, "forward_surprisal")
    assert not hasattr(result, "reverse_surprisal")
    assert result.surprisal_values is not None
    assert result.surprisal_values.shape == (len(seq),)
    combined = analyze_direction(
        MockPredictor(seed=7), seq, context_length=K, ceiling=8192, direction=Direction.BOTH_COMBINED
    )
    assert np.array_equal(result.surprisal_values, combined.surprisal_values)


def test_surprisal_values_none_only_on_a_hand_built_direction_result() -> None:
    # A real analyze_direction() call ALWAYS populates surprisal_values -- cfg.include_surprisal
    # (config.py) gates only the WRITER, never the computation (issue #123).
    result = analyze_direction(
        MockPredictor(seed=0), "ACGT" * 10, context_length=8, ceiling=64, direction=Direction.FORWARD_ONLY
    )
    assert result.surprisal_values is not None
    # A hand-built DirectionResult (as writer tests use) defaults to None.
    hand_built = DirectionResult(
        values=np.zeros(4, dtype=np.float32),
        direction=Direction.FORWARD_ONLY,
        context_length=8,
        window=16,
        stride=8,
        seam=None,
        reduced_context_count=0,
    )
    assert hand_built.surprisal_values is None


# --- issue #456: after an OOM halving the seam follows the K each pass ACTUALLY ran with -----


class _OOMOnFirstPass:
    """Predictor stub: raises PredictorOOMError on the first window of ONE direction
    (``"forward"``: a prefix of the sequence; ``"reverse"``: a prefix of its reverse
    complement; ``None``: never), otherwise delegates to the mock."""

    def __init__(self, seq: str, direction: str | None) -> None:
        self._prefix = {
            "forward": seq,
            "reverse": reverse_complement(seq),
            None: None,
        }[direction]
        self._fired = direction is None

    def predict(self, window: str) -> np.ndarray:
        if not self._fired and self._prefix.startswith(window):
            self._fired = True
            raise PredictorOOMError("simulated OOM")
        return MockPredictor(seed=0).predict(window)


_SEQ_456 = "ACGTTGCAAGCT" * 20  # 240 nt


def _combined_and_separate_456(oom_on: str | None, k: int = 16):
    kwargs = {"context_length": k, "ceiling": 1024}
    combined = analyze_direction(
        _OOMOnFirstPass(_SEQ_456, oom_on), _SEQ_456, direction=Direction.BOTH_COMBINED, **kwargs
    )
    separate = analyze_direction(
        _OOMOnFirstPass(_SEQ_456, oom_on), _SEQ_456, direction=Direction.BOTH_SEPARATE, **kwargs
    )
    return combined, separate


def test_seam_after_a_forward_only_halving_is_the_forward_passes_actual_k() -> None:
    """MEASURED 2026-10-02 (issue #456): with K=16 and the forward pass halved to K=8, the
    combiner takes forward from index 8, but the seam used to be reported as 16."""
    combined, separate = _combined_and_separate_456("forward")
    assert combined.seam == 8
    assert combined.context_length == 16  # the configured K stays configured (#407)
    assert np.allclose(combined.values[:8], separate.reverse_values[:8], atol=1e-5)
    assert np.allclose(combined.values[8:], separate.forward_values[8:], atol=1e-5)
    assert any("forward pass ran with K=8" in n for n in combined.notices)


def test_seam_after_a_reverse_only_halving_stays_at_the_forward_k() -> None:
    combined, separate = _combined_and_separate_456("reverse")
    assert combined.seam == 16  # forward ran with the configured K
    assert np.allclose(combined.values[:16], separate.reverse_values[:16], atol=1e-5)
    assert np.allclose(combined.values[16:], separate.forward_values[16:], atol=1e-5)
    assert any("reverse pass ran with K=8" in n for n in combined.notices)


def test_no_halving_means_no_k_notice_and_the_configured_seam() -> None:
    combined, _ = _combined_and_separate_456(None)
    assert combined.seam == 16
    assert not any("pass ran with K=" in n for n in combined.notices)


def test_seam_is_none_when_the_actual_ks_no_longer_fit_the_sequence() -> None:
    """A clean seam needs L >= fwd_K + rev_K. 20 nt with K=16 never had one; check the
    halved variant too (forward K=8, reverse K=16, L=23 < 24)."""
    seq = "ACGTTGCAAGCTACGTACGTACG"  # 23 nt
    result = analyze_direction(
        _OOMOnFirstPass(seq, "forward"),
        seq,
        context_length=16,
        ceiling=1024,
        direction=Direction.BOTH_COMBINED,
    )
    assert result.seam is None


# --- issue #78's observables, stated directly ---------------------------------------------


class _UniformFirstRow:
    """Evo-like boundary: no BOS token, so row 0 is uniform (2.0 bits); the other rows are
    the mock's deterministic softmax."""

    def predict(self, window: str) -> np.ndarray:
        probs = MockPredictor(seed=0).predict(window).copy()
        probs[0] = 0.25
        return probs


def test_the_reverse_track_of_a_reverse_palindromic_sequence_mirrors_the_forward_track() -> None:
    """ "ACGT" * n equals its own reverse complement, so both passes feed the model the SAME
    string; the reverse track must then be the forward track read backwards (i -> L-1-i)."""
    seq = "ACGT" * 30
    assert reverse_complement(seq) == seq
    result = analyze_direction(
        MockPredictor(seed=3), seq, context_length=16, ceiling=1024, direction=Direction.BOTH_SEPARATE
    )
    assert np.allclose(result.reverse_values, result.forward_values[::-1], atol=1e-6)


def test_the_last_base_of_the_reverse_track_is_the_uniform_row() -> None:
    seq = "ACGTTGCAAGCT" * 20
    result = analyze_direction(
        _UniformFirstRow(), seq, context_length=16, ceiling=1024, direction=Direction.BOTH_SEPARATE
    )
    assert result.forward_values[0] == pytest.approx(2.0)
    assert result.reverse_values[-1] == pytest.approx(2.0)  # rc index 0 maps to original L-1
    assert result.reverse_values[0] < 2.0 - 1e-3  # and only that end


# --- issue #79: reduced context is recorded as POSITIONS, not only a count ---------------


def test_reduced_context_range_is_the_exact_middle_span_neither_direction_reached_k() -> None:
    K, seq = 100, "ACGT" * 30  # L=120: positions [L-K, K) = [20, 100) have < K in both directions
    result = analyze_direction(
        MockPredictor(seed=6), seq, context_length=K, ceiling=8192, direction=Direction.BOTH_COMBINED
    )
    assert result.reduced_context_range == (len(seq) - K, K)
    start, end = result.reduced_context_range
    assert end - start == result.reduced_context_count


def test_reduced_context_range_is_none_when_l_at_least_2k() -> None:
    result = analyze_direction(
        MockPredictor(seed=6), "ACGT" * 30, context_length=20, ceiling=8192, direction=Direction.BOTH_COMBINED
    )
    assert result.reduced_context_range is None


def test_reduced_context_range_is_none_for_a_single_direction() -> None:
    result = analyze_direction(
        MockPredictor(seed=6), "ACGT" * 30, context_length=100, ceiling=8192, direction=Direction.FORWARD_ONLY
    )
    assert result.reduced_context_range is None
