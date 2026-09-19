"""Tests for the MockPredictor and the Predictor contract guard.

These assert the *contract* (shape, dtype, range, row-sums, determinism), not the
implementation — so they will equally protect EvoPredictor and the future ANN.
"""

from __future__ import annotations

import numpy as np
import pytest

from dna_entropy.predictors import (
    NUM_NUCLEOTIDES,
    MockPredictor,
    Predictor,
    check_probability_matrix,
)


def test_mock_satisfies_predictor_protocol() -> None:
    assert isinstance(MockPredictor(), Predictor)


def test_output_shape_and_dtype(sample_seq: str) -> None:
    probs = MockPredictor().predict(sample_seq)
    assert probs.shape == (len(sample_seq), NUM_NUCLEOTIDES)
    assert probs.dtype == np.float32


def test_rows_sum_to_one(sample_seq: str) -> None:
    probs = MockPredictor().predict(sample_seq)
    assert np.allclose(probs.sum(axis=1), 1.0, atol=1e-5)


def test_values_in_unit_range(sample_seq: str) -> None:
    probs = MockPredictor().predict(sample_seq)
    assert probs.min() >= 0.0
    assert probs.max() <= 1.0


def test_deterministic_across_instances(sample_seq: str) -> None:
    a = MockPredictor(seed=42).predict(sample_seq)
    b = MockPredictor(seed=42).predict(sample_seq)
    assert np.array_equal(a, b)


def test_different_seed_changes_output(sample_seq: str) -> None:
    a = MockPredictor(seed=0).predict(sample_seq)
    b = MockPredictor(seed=1).predict(sample_seq)
    assert not np.array_equal(a, b)


def test_different_sequence_changes_output() -> None:
    pred = MockPredictor(seed=0)
    a = pred.predict("ACGTACGTAC")
    b = pred.predict("TGCATGCATG")
    assert not np.array_equal(a, b)


def test_single_base_sequence() -> None:
    probs = MockPredictor().predict("A")
    assert probs.shape == (1, NUM_NUCLEOTIDES)
    assert np.allclose(probs.sum(axis=1), 1.0, atol=1e-5)


# --- contract guard -------------------------------------------------------------------


def test_check_rejects_wrong_shape() -> None:
    bad = np.ones((5, 3), dtype=np.float32) / 3.0
    with pytest.raises(ValueError):
        check_probability_matrix(bad, seq_len=5)


def test_check_rejects_wrong_dtype() -> None:
    bad = np.full((5, NUM_NUCLEOTIDES), 0.25, dtype=np.float64)
    with pytest.raises(ValueError):
        check_probability_matrix(bad, seq_len=5)


def test_check_rejects_rows_not_summing_to_one() -> None:
    bad = np.full((5, NUM_NUCLEOTIDES), 0.5, dtype=np.float32)  # rows sum to 2.0
    with pytest.raises(ValueError):
        check_probability_matrix(bad, seq_len=5)


def test_check_accepts_valid_matrix() -> None:
    good = np.full((5, NUM_NUCLEOTIDES), 0.25, dtype=np.float32)
    assert check_probability_matrix(good, seq_len=5) is good


# --- NaN diagnosis (issue #347) ---------------------------------------------------------
#
# A NaN probability was already CAUGHT before this fix -- it poisons the row sum, and
# np.allclose(row_sums, 1.0) treats a NaN sum as never close -- but only incidentally,
# via a message reading "worst deviation nan" that never says NaN was the cause. A GPU
# bug is the single most likely real source of a NaN row, so the message should say so
# directly and name which rows, not send the reader looking for a normalization bug.


def test_check_rejects_nan_with_a_message_naming_nan() -> None:
    bad = np.full((5, NUM_NUCLEOTIDES), 0.25, dtype=np.float32)
    bad[2, 0] = np.nan
    with pytest.raises(ValueError, match="NaN"):
        check_probability_matrix(bad, seq_len=5)


def test_check_nan_message_names_the_offending_row_indices() -> None:
    bad = np.full((5, NUM_NUCLEOTIDES), 0.25, dtype=np.float32)
    bad[2, 0] = np.nan
    bad[4, 3] = np.nan
    with pytest.raises(ValueError) as exc:
        check_probability_matrix(bad, seq_len=5)
    msg = str(exc.value)
    assert "2" in msg and "4" in msg


def test_check_nan_message_does_not_talk_about_row_sum_deviation() -> None:
    # Before the fix this fell through to the row-sum branch and reported a confusing
    # "worst deviation nan" instead of naming NaN as the actual cause.
    bad = np.full((5, NUM_NUCLEOTIDES), 0.25, dtype=np.float32)
    bad[0, 0] = np.nan
    with pytest.raises(ValueError) as exc:
        check_probability_matrix(bad, seq_len=5)
    assert "deviation" not in str(exc.value)
