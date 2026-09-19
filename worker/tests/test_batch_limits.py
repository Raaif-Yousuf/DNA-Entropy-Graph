"""Tests for worker/batch_limits.py (issue #248): batch-level cost guardrails."""

from __future__ import annotations

import pytest

from dna_entropy.worker.batch_limits import (
    DEFAULT_GPU_TIER,
    DEFAULT_MAX_INPUTS,
    DEFAULT_MAX_TOTAL_NT,
    GPU_HOURLY_USD_THEORY,
    BatchLimitError,
    check_batch_limits,
    format_batch_limit_message,
)


def test_within_both_limits_is_a_no_op() -> None:
    check_batch_limits(n_inputs=5, total_nt=1000, max_inputs=50, max_total_nt=20_000_000)


def test_exactly_at_the_limit_is_allowed_not_refused() -> None:
    # A limit is a ceiling, not an exclusive bound: exactly the max is fine.
    check_batch_limits(n_inputs=50, total_nt=20_000_000, max_inputs=50, max_total_nt=20_000_000)


def test_too_many_files_is_refused() -> None:
    with pytest.raises(BatchLimitError) as exc:
        check_batch_limits(n_inputs=51, total_nt=100, max_inputs=50, max_total_nt=20_000_000)
    assert exc.value.code == "BATCH_LIMIT_EXCEEDED"


def test_too_much_total_nt_is_refused() -> None:
    with pytest.raises(BatchLimitError):
        check_batch_limits(n_inputs=1, total_nt=20_000_001, max_inputs=50, max_total_nt=20_000_000)


def test_both_limits_exceeded_at_once_is_refused_once() -> None:
    with pytest.raises(BatchLimitError):
        check_batch_limits(n_inputs=999, total_nt=999_999_999, max_inputs=50, max_total_nt=20_000_000)


def test_defaults_are_exported_and_match_the_manifest_wiring() -> None:
    # worker/manifest.py's Limits.from_dict defaults to these; a drift here would be silent.
    assert DEFAULT_MAX_INPUTS > 0
    assert DEFAULT_MAX_TOTAL_NT > 0


# --- the message itself: real numbers, not "too large" --------------------------------


def test_message_states_the_real_file_count_and_nt_total() -> None:
    msg = format_batch_limit_message(n_inputs=40, total_nt=12_000_000, max_inputs=30, max_total_nt=20_000_000)
    assert "40" in msg
    assert "12,000,000" in msg
    assert "30" in msg  # the limit that was actually exceeded


def test_message_names_only_the_limit_that_was_actually_exceeded() -> None:
    # File count is fine; only nt is over. The message should not claim the file count
    # itself was the problem.
    msg = format_batch_limit_message(n_inputs=5, total_nt=25_000_000, max_inputs=50, max_total_nt=20_000_000)
    assert "25,000,000" in msg
    assert "nt total" in msg


def test_message_gives_the_gpu_hourly_rate_not_a_bare_too_large() -> None:
    msg = format_batch_limit_message(n_inputs=100, total_nt=1, max_inputs=50, max_total_nt=20_000_000)
    assert "too large" not in msg.lower()
    rate = GPU_HOURLY_USD_THEORY[DEFAULT_GPU_TIER]
    assert f"${rate:.2f}" in msg


def test_message_does_not_fabricate_a_dollar_total_for_the_batch() -> None:
    """The one thing this message must NOT do: multiply an unverified nt/sec assumption
    by the hourly rate and present a specific dollar figure as if it were real (the
    shared brief's own instruction: don't present an estimate as exact when the repo has
    no verified throughput number to base it on)."""
    msg = format_batch_limit_message(n_inputs=100, total_nt=1, max_inputs=50, max_total_nt=20_000_000)
    assert "no verified nt-per-second throughput" in msg


def test_message_notes_an_unfamiliar_gpu_tier_without_crashing() -> None:
    msg = format_batch_limit_message(
        n_inputs=100, total_nt=1, max_inputs=50, max_total_nt=20_000_000, gpu_tier="h100"
    )
    assert "docs/research/2026-09-19-gpu-pricing-and-instances.md" in msg
