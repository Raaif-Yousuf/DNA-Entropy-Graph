"""Tests for analysis/windowing.py (section 5.6): W/S derivation, window tiling, pushback."""

from __future__ import annotations

import dataclasses

import pytest

from dna_entropy.analysis.windowing import (
    MIN_CONTEXT_LENGTH,
    MIN_RECOMMENDED_CONTEXT_LENGTH,
    MIN_SEQUENCE_LENGTH,
    WindowingError,
    WindowPlan,
    compute_window,
    halved,
    plan_windows,
    validate_context,
)

# --- compute_window: W = min(2K, ceiling), S = W - K ----------------------------------


def test_window_is_double_context_when_under_ceiling() -> None:
    w, s = compute_window(context_length=4096, ceiling=8192)
    assert w == 8192
    assert s == 4096  # W - K


def test_window_clamps_to_ceiling_when_2k_exceeds_it() -> None:
    w, s = compute_window(context_length=6000, ceiling=8192)
    assert w == 8192  # min(12000, 8192)
    assert s == 2192  # 8192 - 6000


def test_ceiling_smaller_than_k_is_an_error() -> None:
    with pytest.raises(WindowingError):
        compute_window(context_length=8192, ceiling=4096)


def test_negative_or_zero_inputs_are_errors() -> None:
    with pytest.raises(WindowingError):
        compute_window(context_length=0, ceiling=8192)
    with pytest.raises(WindowingError):
        compute_window(context_length=4096, ceiling=0)


# --- plan_windows: L <= W -> exactly one window at 0 ----------------------------------


def test_short_sequence_is_a_single_window_at_zero() -> None:
    plan = plan_windows(length=200, context_length=4096, ceiling=8192)
    assert plan.window == 8192
    assert plan.num_windows == 1
    assert plan.starts == (0,)


def test_sequence_exactly_at_window_is_a_single_window() -> None:
    plan = plan_windows(length=8192, context_length=4096, ceiling=8192)
    assert plan.num_windows == 1
    assert plan.starts == (0,)


# --- plan_windows: L > W -> tiled, last window right-aligned --------------------------


def test_tiled_windows_start_at_multiples_of_stride() -> None:
    # L=20000, K=4096, ceiling=8192 -> W=8192, S=4096
    plan = plan_windows(length=20000, context_length=4096, ceiling=8192)
    assert plan.window == 8192
    assert plan.stride == 4096
    # passes = ceil((20000-8192)/4096) + 1 = ceil(2.882..) + 1 = 3 + 1 = 4
    assert plan.num_windows == 4
    assert plan.starts[0] == 0
    assert plan.starts[1] == 4096
    assert plan.starts[2] == 8192
    # last window is right-aligned: 20000 - 8192 = 11808, not a stride multiple
    assert plan.starts[3] == 11808
    assert plan.starts[3] != 3 * plan.stride  # proves right-alignment actually triggered


def test_every_window_except_a_short_final_one_is_full_width() -> None:
    plan = plan_windows(length=20000, context_length=4096, ceiling=8192)
    for i in range(plan.num_windows):
        assert plan.width_at(i) == plan.window  # every window here is a full W wide


# --- WindowPlan.context: deliberately removed (found during the lane-B audit) ---------
#
# Was set (as `context=context_length` in plan_windows) and never read anywhere as
# `plan.context` -- unlike `.ceiling` (same shape, fixed by threading it into
# DirectionResult/SummaryWriter's provenance, since nothing else recorded it), K is
# ALREADY recorded independently on DirectionResult.context_length, sourced from the same
# `context_length` parameter every caller already has in scope. There was no unmet need
# for a second, redundant home for the same value, so this one was deleted rather than
# wired up. (`check_unused_fields.py`'s own name-based matching missed this one because
# `.context` also names an unrelated, genuinely-read field on `SinglePassResult`.)


def test_windowplan_no_longer_carries_a_redundant_context_field() -> None:
    field_names = {f.name for f in dataclasses.fields(WindowPlan)}
    assert "context" not in field_names
    plan = plan_windows(length=200, context_length=4096, ceiling=8192)
    assert not hasattr(plan, "context")


def test_last_window_covers_the_sequence_end_exactly() -> None:
    plan = plan_windows(length=20000, context_length=4096, ceiling=8192)
    last_start = plan.starts[-1]
    assert last_start + plan.width_at(plan.num_windows - 1) == plan.length


def test_window_starts_strictly_increasing_no_duplicates() -> None:
    plan = plan_windows(length=30000, context_length=4096, ceiling=8192)
    assert list(plan.starts) == sorted(set(plan.starts))
    assert len(plan.starts) == len(set(plan.starts))


def test_pass_count_matches_the_ceil_formula_for_several_lengths() -> None:
    import math

    for length in (8193, 12000, 16384, 30000, 100_000):
        plan = plan_windows(length=length, context_length=4096, ceiling=8192)
        expected = math.ceil((length - plan.window) / plan.stride) + 1
        assert plan.num_windows == expected, length


def test_boundary_one_base_past_window_needs_two_windows() -> None:
    plan = plan_windows(length=8193, context_length=4096, ceiling=8192)
    assert plan.num_windows == 2
    assert plan.starts == (0, 1)  # second window right-aligned to length-window=1


def test_plan_windows_rejects_nonpositive_length() -> None:
    with pytest.raises(WindowingError):
        plan_windows(length=0, context_length=4096, ceiling=8192)


# --- pushback rules (section 5.6) ------------------------------------------------------


def test_k_below_minimum_refuses() -> None:
    with pytest.raises(WindowingError):
        validate_context(context_length=MIN_CONTEXT_LENGTH - 1, ceiling=8192, seq_len=1000)


def test_k_at_minimum_is_allowed() -> None:
    notices = validate_context(context_length=MIN_CONTEXT_LENGTH, ceiling=8192, seq_len=1000)
    # Still below the *recommended* minimum, so it should warn (noisy near the edge).
    assert any("noisy" in n.lower() or "prior" in n.lower() for n in notices)


def test_k_below_recommended_warns_but_does_not_raise() -> None:
    notices = validate_context(context_length=MIN_RECOMMENDED_CONTEXT_LENGTH - 1, ceiling=8192, seq_len=10000)
    assert any("recommended" in n.lower() or "noisy" in n.lower() for n in notices)


def test_k_at_or_above_recommended_has_no_context_warning() -> None:
    notices = validate_context(context_length=MIN_RECOMMENDED_CONTEXT_LENGTH, ceiling=8192, seq_len=10000)
    assert not any("noisy" in n.lower() for n in notices)


def test_seq_shorter_than_minimum_refuses() -> None:
    with pytest.raises(WindowingError):
        validate_context(context_length=4096, ceiling=8192, seq_len=MIN_SEQUENCE_LENGTH - 1)


def test_seq_at_minimum_length_is_allowed() -> None:
    # 10 nt is fine on its own merits (though far shorter than K, so it also warns).
    notices = validate_context(context_length=4096, ceiling=8192, seq_len=MIN_SEQUENCE_LENGTH)
    assert any("shorter than the context length" in n for n in notices)


def test_seq_shorter_than_k_warns() -> None:
    notices = validate_context(context_length=4096, ceiling=8192, seq_len=500)
    assert any("shorter than the context length" in n for n in notices)


def test_seq_at_or_above_k_has_no_short_input_warning() -> None:
    notices = validate_context(context_length=4096, ceiling=8192, seq_len=4096)
    assert not any("shorter than the context length" in n for n in notices)


def test_window_above_ceiling_warns_never_silently_clamps() -> None:
    # K=6000 -> preferred window 12000 > ceiling 8192: must warn, not just quietly proceed.
    notices = validate_context(context_length=6000, ceiling=8192, seq_len=50000)
    assert any("ceiling" in n.lower() and "clamp" in n.lower() for n in notices)


def test_window_within_ceiling_has_no_clamp_warning() -> None:
    notices = validate_context(context_length=4096, ceiling=8192, seq_len=50000)
    assert not any("clamp" in n.lower() for n in notices)


def test_clean_config_has_no_notices() -> None:
    assert validate_context(context_length=4096, ceiling=8192, seq_len=50000) == []


# --- OOM halving -------------------------------------------------------------------
#
# issue #313: `halved()` used to halve the *ceiling* and let W fall out of a fresh
# `min(2K, ceiling)`, which only moves W when the plan was ceiling-bound. A K-bound plan
# (W == 2K <= ceiling — true for the default K=4096 on every GPU tier above an L4) got an
# UNCHANGED window back: the OOM retry re-ran an identically shaped pass and OOM'd again.
# issue #315: the tests here used to assert only the returned (K, ceiling) *values*, never
# the derived window itself — the quantity the docstring and spec 5.6 actually promise —
# so a case that happened to return the "right" numbers for the wrong reason still passed.
# Every test below asserts the DERIVED WINDOW (via compute_window on halved()'s own
# output), not just the (K, ceiling) tuple.

# (context_length, ceiling) pairs that exercise both regimes real usage can hit. Every
# pair here is one `compute_window` would already have accepted once (halved() is only
# ever called on a (K, ceiling) that just ran a real, successful predictor pass before
# OOM'ing on a SECOND call) — an inherent precondition, not new behaviour.
_HALVING_CASES = [
    pytest.param(4096, 16384, id="k-bound-default-l4-to-a100-headroom"),  # issue #313's own repro
    pytest.param(4096, 8192, id="k-bound-tight-l4-default-ceiling"),  # W == 2K == ceiling exactly
    pytest.param(2048, 8192, id="k-bound-with-headroom"),
    pytest.param(3000, 5000, id="ceiling-bound-with-headroom"),  # K < ceiling < 2K
    pytest.param(100, 101, id="ceiling-bound-at-its-tightest-stride-of-1"),
    pytest.param(1, 2, id="already-at-the-absolute-floor"),
]


@pytest.mark.parametrize("context_length, ceiling", _HALVING_CASES)
def test_halved_strictly_decreases_the_derived_window_or_is_already_at_the_floor(
    context_length: int, ceiling: int
) -> None:
    """The regression test for #313: assert the WINDOW shrinks, not just that `halved()`
    returns some (K, ceiling) pair. Every case above genuinely shrinks except the last
    (1, 2), which is already the smallest valid plan (K=1, W=2, S=1) and is a floor, not a
    regression — asserted separately below by `test_halved_reaches_a_stable_floor`."""
    old_window, _old_stride = compute_window(context_length, ceiling)
    new_context, new_ceiling = halved(context_length, ceiling)
    new_window, new_stride = compute_window(new_context, new_ceiling)

    assert new_context >= 1
    assert new_ceiling >= 1
    assert new_stride >= 1  # halved() must always return a still-valid plan
    assert new_window <= old_window
    if (context_length, ceiling) != (1, 2):  # the one already-at-the-floor case
        assert new_window < old_window, (
            f"halved({context_length}, {ceiling}) returned ({new_context}, {new_ceiling}) "
            f"-> window {new_window}, no smaller than the {old_window} that just OOM'd"
        )


def test_halved_keeps_k_when_the_ceiling_was_the_actual_constraint() -> None:
    """ "Keep K if possible" (spec 5.6) means: when the plan was ceiling-bound (the
    ceiling, not 2K, is what `min()` picked), K was never the limiting factor, so
    shrinking the ceiling alone already produces a smaller window — K does not need to
    move. (3000, 5000): 2K=6000 > ceiling=5000, so W=5000 is ceiling-bound."""
    old_window, _ = compute_window(3000, 5000)
    new_context, new_ceiling = halved(3000, 5000)
    new_window, _ = compute_window(new_context, new_ceiling)

    assert new_context == 3000  # K kept exactly
    assert new_window < old_window


def test_halved_also_halves_k_when_the_window_was_k_bound() -> None:
    """The other half of "keep K if possible, else halve both": a K-bound plan (W == 2K)
    cannot shrink by touching the ceiling alone — `min(2K, ceiling)` would still pick
    `2K` — so K itself must halve too. This is issue #313's exact repro: K=4096,
    ceiling=16384 (an A100/H100-tier ceiling, far above 2K=8192)."""
    old_window, _ = compute_window(4096, 16384)
    assert old_window == 8192  # K-bound: 2*4096, not the 16384 ceiling

    new_context, new_ceiling = halved(4096, 16384)
    new_window, _ = compute_window(new_context, new_ceiling)

    assert new_context < 4096  # K moved -- this is the bug: it used to stay 4096
    assert new_window == 8192 // 2  # a genuine halving, not a no-op


def test_halved_reaches_a_stable_floor_not_an_infinite_shrink() -> None:
    """Repeatedly retrying (as if every retry OOM'd again) must converge to a fixed,
    still-valid plan -- never raise, never shrink K or the window below 1, and never
    loop without settling."""
    context_length, ceiling = 4096, 16384
    windows = []
    for _ in range(50):
        window, _ = compute_window(context_length, ceiling)
        windows.append(window)
        context_length, ceiling = halved(context_length, ceiling)

    assert context_length >= 1
    assert ceiling >= 1
    # Monotonically non-increasing throughout, and settles (does not oscillate/grow).
    assert windows == sorted(windows, reverse=True)
    assert windows[-1] == windows[-2] == windows[-3]  # reached and held a floor


# --- property, table-driven adversarial grid (issue #160, windowing scope only) --------
#
# `hypothesis` is not installed in worker\.venv on this laptop (verified: `import
# hypothesis` -> ModuleNotFoundError); per the brief, nothing was installed to get it.
# This is the table-driven equivalent over a deliberately adversarial (L, K, ceiling)
# grid rather than a generated one -- the real Hypothesis version is filed as its own
# issue instead of half-implemented here. Property under test: every position of the
# input is covered by exactly one WINNING window after stitching (the "combined output
# position" -- see `analysis/direction.py::_stitch_forward`'s "local > context[..]"
# tie-break, simulated here at the windowing level since that is this module's own
# surface), for every L relative to K including L < K, L == K, L == 2K, L == 2K + 1, and
# L == 1 -- each swept across both a K-bound window (ceiling far above 2K) and a
# ceiling-bound window (ceiling barely above K, the tightest valid stride).


def _covered_exactly_by_the_winning_window(plan: WindowPlan) -> bool:
    """Reimplements _stitch_forward's coverage guarantee (max-local-context wins) at the
    plan level, with no predictor involved: every position 0..length-1 must end up
    assigned from exactly one window (never left uncovered)."""
    context = [-1] * plan.length
    for start in plan.starts:
        width = plan.window if start + plan.window <= plan.length else plan.length - start
        for local in range(width):
            global_idx = start + local
            if local > context[global_idx]:
                context[global_idx] = local
    return all(c >= 0 for c in context) and len(context) == plan.length


_ADVERSARIAL_K = [1, 2, 5, 128]


def _adversarial_lengths(k: int) -> list[int]:
    return sorted({1, k, k + 1, 2 * k, 2 * k + 1, 2 * k + 5, 10 * k + 3})


@pytest.mark.parametrize("k", _ADVERSARIAL_K)
def test_every_position_covered_exactly_once_k_bound_window(k: int) -> None:
    # ceiling far above 2K: window is K-bound (W = 2K exactly).
    ceiling = 2 * k + 1000
    for length in _adversarial_lengths(k):
        plan = plan_windows(length=length, context_length=k, ceiling=ceiling)
        assert _covered_exactly_by_the_winning_window(plan), (length, k, ceiling)


@pytest.mark.parametrize("k", _ADVERSARIAL_K)
def test_every_position_covered_exactly_once_ceiling_bound_window(k: int) -> None:
    # ceiling barely above K: window is ceiling-bound (the tightest valid stride, S=1).
    ceiling = k + 1
    for length in _adversarial_lengths(k):
        plan = plan_windows(length=length, context_length=k, ceiling=ceiling)
        assert _covered_exactly_by_the_winning_window(plan), (length, k, ceiling)


@pytest.mark.parametrize("k", _ADVERSARIAL_K)
def test_starts_are_sorted_and_unique_across_the_adversarial_grid(k: int) -> None:
    # A duplicate or out-of-order start would silently re-run or skip a window.
    for ceiling in (2 * k + 1000, k + 1):
        for length in _adversarial_lengths(k):
            plan = plan_windows(length=length, context_length=k, ceiling=ceiling)
            assert list(plan.starts) == sorted(set(plan.starts))


# --- issue #345: the WINDOWING layer's own S = W - K invariant (a narrower claim) -----
#
# CORRECTED 2026-09-19: an earlier pass on this issue concluded stride needed no
# cross-check at all, reasoning that S = W - K is a pure function of context_length and
# ceiling with "no third input a manifest's declared stride could disagree with". That
# reasoning is correct about THIS module (`compute_window`/`plan_windows` never accept an
# externally supplied stride -- the two tests below still document that, honestly) but
# wrong about the MANIFEST layer: `worker/manifest.py::AnalysisSpec` parses its OWN
# `stride` field independently of `window`/`contextLength`, and nothing downstream used to
# read it -- a real, silent "wired to nothing" gap (the coordinator's own catch, not found
# by this session). `AnalysisSpec.from_dict` now cross-checks the manifest's declared
# `stride` against `window - contextLength` at parse time and refuses a mismatch (see
# `worker/tests/test_worker_manifest.py`'s `test_analysis_stride_*` tests and
# `docs/job_contract.md`'s corrected `analysis.contextLength`/`window`/`stride` row) --
# that is #345's actual resolution. The two tests below are narrower and still true: they
# lock the WINDOWING module's own internal invariant, not the manifest contract.


def test_stride_has_no_parameter_to_accept_an_independently_declared_value() -> None:
    """`compute_window`/`plan_windows` take only `context_length` and `ceiling`; there is
    no `stride=` (or `S=`) keyword either function could be called with. A manifest's
    declared `analysis.stride` therefore has no code path into this module at all -- the
    ONLY way stride ever enters a `WindowPlan` is as `window - context_length`, computed
    here, never accepted as an input."""
    import inspect

    compute_window_params = set(inspect.signature(compute_window).parameters)
    plan_windows_params = set(inspect.signature(plan_windows).parameters)
    assert compute_window_params == {"context_length", "ceiling"}
    assert plan_windows_params == {"length", "context_length", "ceiling"}


@pytest.mark.parametrize(
    "context_length, ceiling",
    [(4096, 8192), (6000, 8192), (128, 129), (1, 2), (2048, 100_000)],
)
def test_stride_is_always_exactly_window_minus_context_length(context_length: int, ceiling: int) -> None:
    """Locks the invariant a cross-check would otherwise exist to protect: for every
    (context_length, ceiling) pair `compute_window` will accept, the derived stride is
    EXACTLY `window - context_length`, with no other value ever possible. If this ever
    stopped being true, #345's "no cross-check needed" reasoning would stop being true
    with it, and this test would be the one to go red."""
    window, stride = compute_window(context_length, ceiling)
    assert stride == window - context_length
    plan = plan_windows(length=window * 3, context_length=context_length, ceiling=ceiling)
    assert plan.stride == window - context_length
