"""Tests for Shannon entropy computation (the scientific core)."""

from __future__ import annotations

import numpy as np
import pytest

from dna_entropy.analysis import MAX_ENTROPY_BITS, shannon_entropy, summarize


def test_uniform_distribution_is_max_entropy() -> None:
    probs = np.full((3, 4), 0.25, dtype=np.float32)
    h = shannon_entropy(probs)
    assert np.allclose(h, 2.0, atol=1e-6)


def test_one_hot_is_zero_entropy() -> None:
    probs = np.array([[1.0, 0.0, 0.0, 0.0]], dtype=np.float32)
    h = shannon_entropy(probs)
    assert np.allclose(h, 0.0, atol=1e-6)  # 0*log0 handled, no NaN


def test_two_way_even_split_is_one_bit() -> None:
    probs = np.array([[0.5, 0.5, 0.0, 0.0]], dtype=np.float32)
    h = shannon_entropy(probs)
    assert np.allclose(h, 1.0, atol=1e-6)


def test_output_shape_and_dtype() -> None:
    probs = np.full((10, 4), 0.25, dtype=np.float32)
    h = shannon_entropy(probs)
    assert h.shape == (10,)
    assert h.dtype == np.float32


def test_values_within_bounds_for_random_input() -> None:
    rng = np.random.default_rng(0)
    logits = rng.standard_normal((100, 4))
    probs = (np.exp(logits) / np.exp(logits).sum(1, keepdims=True)).astype(np.float32)
    h = shannon_entropy(probs)
    assert h.min() >= 0.0
    assert h.max() <= MAX_ENTROPY_BITS + 1e-6


def test_max_entropy_constant_is_two_bits() -> None:
    assert np.isclose(MAX_ENTROPY_BITS, 2.0)


# --- property, table-driven adversarial grid (issue #160, windowing/direction scope) ---
#
# `hypothesis` is not installed in worker\.venv on this laptop (verified: `import
# hypothesis` -> ModuleNotFoundError) and the brief says not to install anything, so this
# is the table-driven equivalent over a deliberately adversarial grid rather than a
# generated one. The real Hypothesis version is filed separately (see the report) rather
# than half-done here. Property: every entropy value lands in [0.0, 2.0] for EVERY input,
# never NaN, using a tolerance (never exact float equality -- MEASURED 2026-09-19: a
# log2-based parity fixture elsewhere in this repo passed on Windows and failed on Linux
# CI over an exact-equality assertion; the same last-bit risk applies to any log2 output).

_DEGENERATE_ROWS = [
    [1.0, 0.0, 0.0, 0.0],  # one-hot each column
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
    [0.25, 0.25, 0.25, 0.25],  # exactly uniform
    [0.5, 0.5, 0.0, 0.0],  # two-way split
    [1.0 - 3e-7, 1e-7, 1e-7, 1e-7],  # near-one-hot, tiny but nonzero remainder
    [0.9999999, 0.0, 0.0, 1e-8],  # right at float32 precision's edge
    [0.0, 0.0, 0.0, 0.0],  # degenerate all-zero row (never valid post-normalization,
    # but shannon_entropy itself must not NaN/crash on it -- the 0*log2(0):=0 convention)
]


@pytest.mark.parametrize("row", _DEGENERATE_ROWS)
def test_entropy_bounded_for_every_degenerate_row(row: list[float]) -> None:
    probs = np.array([row], dtype=np.float32)
    h = shannon_entropy(probs)
    assert not np.isnan(h).any()
    assert h[0] >= -1e-6  # tolerance, never exact float equality
    assert h[0] <= MAX_ENTROPY_BITS + 1e-6


@pytest.mark.parametrize("length", [1, 2, 5])
@pytest.mark.parametrize("temperature", [0.01, 1.0, 100.0])  # near-deterministic .. near-uniform
@pytest.mark.parametrize("seed", [0, 1, 2, 12345])
def test_entropy_bounded_across_an_adversarial_softmax_grid(
    length: int, temperature: float, seed: int
) -> None:
    rng = np.random.default_rng(seed)
    logits = rng.standard_normal((length, 4)) * temperature
    exp = np.exp(logits - logits.max(axis=1, keepdims=True))
    probs = (exp / exp.sum(axis=1, keepdims=True)).astype(np.float32)
    h = shannon_entropy(probs)
    assert h.shape == (length,)
    assert not np.isnan(h).any()
    assert (h >= -1e-6).all()
    assert (h <= MAX_ENTROPY_BITS + 1e-6).all()


def test_summarize_reports_extrema_positions() -> None:
    # position 1 uniform (2.0 bits), position 0 one-hot (0.0 bits)
    probs = np.array([[1.0, 0.0, 0.0, 0.0], [0.25, 0.25, 0.25, 0.25]], dtype=np.float32)
    h = shannon_entropy(probs)
    s = summarize(h)
    assert s.length == 2
    assert s.argmin == 0
    assert s.argmax == 1
    assert np.isclose(s.minimum, 0.0, atol=1e-6)
    assert np.isclose(s.maximum, 2.0, atol=1e-6)
