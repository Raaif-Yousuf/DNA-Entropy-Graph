"""Tests for the torch-free logit->(L,4) alignment used by EvoPredictor.

These run on any machine (no GPU/torch) and cover the trickiest part of the Evo
integration: softmax over the four nucleotide logits and the next-token position shift.
"""

from __future__ import annotations

import numpy as np
import pytest

from dna_entropy.predictors import aligned_acgt_probs, check_probability_matrix
from dna_entropy.predictors.logits import normalize_model_output


def _softmax(row: np.ndarray) -> np.ndarray:
    e = np.exp(row - row.max())
    return e / e.sum()


def test_shape_and_dtype() -> None:
    logits = np.zeros((5, 4), dtype=np.float32)
    out = aligned_acgt_probs(logits)
    assert out.shape == (5, 4)
    assert out.dtype == np.float32


def test_rows_sum_to_one() -> None:
    rng = np.random.default_rng(0)
    logits = rng.standard_normal((20, 4)).astype(np.float32)
    out = aligned_acgt_probs(logits)
    assert np.allclose(out.sum(axis=1), 1.0, atol=1e-5)


def test_first_row_is_uniform() -> None:
    rng = np.random.default_rng(1)
    logits = rng.standard_normal((10, 4)).astype(np.float32)
    out = aligned_acgt_probs(logits)
    assert np.allclose(out[0], 0.25, atol=1e-6)


def test_position_alignment_is_next_token_shifted() -> None:
    # Distinct rows so the shift is unambiguous.
    logits = np.array(
        [
            [5.0, 0.0, 0.0, 0.0],  # predicts base 1
            [0.0, 5.0, 0.0, 0.0],  # predicts base 2
            [0.0, 0.0, 5.0, 0.0],
        ],  # predicts base 3 (beyond -> dropped)
        dtype=np.float32,
    )
    out = aligned_acgt_probs(logits)
    # base 1 distribution == softmax(logits[0]); base 2 == softmax(logits[1])
    assert np.allclose(out[1], _softmax(logits[0]), atol=1e-6)
    assert np.allclose(out[2], _softmax(logits[1]), atol=1e-6)


def test_single_base_sequence() -> None:
    out = aligned_acgt_probs(np.zeros((1, 4), dtype=np.float32))
    assert out.shape == (1, 4)
    assert np.allclose(out[0], 0.25, atol=1e-6)


# --- zero-length input (issue #292) ----------------------------------------------------


def test_empty_input_returns_empty_array_not_indexerror() -> None:
    # Before the fix, `aligned[0] = ...` raised IndexError on a (0, 4) array.
    out = aligned_acgt_probs(np.empty((0, 4), dtype=np.float32))
    assert out.shape == (0, 4)
    assert out.dtype == np.float32


def test_empty_input_satisfies_the_l4_contract() -> None:
    # Hard Rule 3's (L, 4) contract is vacuously true for L=0: check_probability_matrix
    # must accept it (row-sum-to-1.0 has no rows to violate that).
    out = aligned_acgt_probs(np.empty((0, 4), dtype=np.float32))
    assert check_probability_matrix(out, seq_len=0) is out


# --- model-output normalization boundary (issue #293) ----------------------------------
#
# `normalize_model_output` holds the SAME unwrap steps `EvoPredictor._extract_logits` used
# to do inline (nested tuple/list -> `.logits` -> drop batch dim) — moved here, torch-free,
# so the failure mode (a bare AttributeError on an unrecognized shape) is testable on any
# machine, per CLAUDE.md's Critical Pitfalls on this exact boundary. Do not "simplify" the
# unwrap order; these tests pin it down.


class _FakeTensor:
    """A minimal stand-in exposing only `.ndim`/`__getitem__` — no torch needed."""

    def __init__(self, ndim: int, item=None) -> None:
        self.ndim = ndim
        self._item = item

    def __getitem__(self, index):
        return self._item[index] if self._item is not None else self


def test_normalize_unwraps_nested_tuple() -> None:
    leaf = _FakeTensor(ndim=2)
    nested = ((leaf, "inference_params"),)  # evo2's real shape, MEASURED on a GCP L4
    assert normalize_model_output(nested) is leaf


def test_normalize_unwraps_a_list_too() -> None:
    leaf = _FakeTensor(ndim=2)
    assert normalize_model_output([leaf]) is leaf


def test_normalize_uses_logits_attribute() -> None:
    leaf = _FakeTensor(ndim=2)

    class _Output:
        logits = leaf

    assert normalize_model_output(_Output()) is leaf


def test_normalize_drops_batch_dimension() -> None:
    row = _FakeTensor(ndim=2)
    batched = _FakeTensor(ndim=3, item=[row])
    out = normalize_model_output(batched)
    assert out is row


def test_normalize_raises_valueerror_naming_the_type_on_no_ndim() -> None:
    # A dict has neither `.logits` nor `.ndim` — before the fix, the caller's own
    # `out.ndim == 3` line raised a bare AttributeError here.
    with pytest.raises(ValueError, match="dict"):
        normalize_model_output({"unexpected": "shape"})


def test_normalize_raises_valueerror_for_unrecognized_nested_element() -> None:
    # The tuple-unwrap always takes element [0]; if THAT element is unrecognizable the
    # failure must still be a clear ValueError, not a crash three lines later.
    with pytest.raises(ValueError, match="NoneType"):
        normalize_model_output((None, "ignored"))
