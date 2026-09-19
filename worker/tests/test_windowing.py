"""Tests for analysis/windowing.py (section 5.6): W/S derivation, window tiling, pushback."""

from __future__ import annotations

import pytest

from dna_entropy.analysis.windowing import (
    MIN_CONTEXT_LENGTH,
    MIN_RECOMMENDED_CONTEXT_LENGTH,
    MIN_SEQUENCE_LENGTH,
    WindowingError,
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


# --- OOM halving -----------------------------------------------------------------------


def test_halved_keeps_k_when_it_still_fits() -> None:
    # ceiling 8192 -> 4096; K=2048 still < 4096, so K is kept.
    k, ceiling = halved(context_length=2048, ceiling=8192)
    assert ceiling == 4096
    assert k == 2048


def test_halved_also_halves_k_when_it_no_longer_fits() -> None:
    # ceiling 8192 -> 4096; K=4096 is NOT < 4096, so K must also halve.
    k, ceiling = halved(context_length=4096, ceiling=8192)
    assert ceiling == 4096
    assert k == 2048


def test_halved_never_goes_below_one() -> None:
    k, ceiling = halved(context_length=1, ceiling=1)
    assert k >= 1
    assert ceiling >= 1
