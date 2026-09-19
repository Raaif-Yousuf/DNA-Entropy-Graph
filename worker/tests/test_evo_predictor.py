"""Tests for EvoPredictor (Evo 2 7B): a torch-free error-boundary section, plus the real
GPU integration section.

The GPU integration tests need REAL Evo 2 weights on a GPU; each is marked ``gpu``
individually and its fixture calls ``pytest.importorskip`` lazily, so they are skipped
automatically where torch/evo2 aren't installed (this dev laptop has an Intel Arc and
cannot run them at all -- Hard Rule 15). Run them on the A10 box: ``pytest -m gpu``.

The error-boundary tests below (issue #293) run on ANY machine, including this laptop:
``evo.py`` does an unconditional ``import torch`` / ``from evo2 import Evo2`` at module
scope (Hard Rule 1 keeps that import confined to this one file), so importing the module
at all normally requires torch/evo2 to be installed -- these tests get around that by
injecting a minimal fake ``torch``/``evo2`` module into ``sys.modules`` before importing
the REAL ``dna_entropy.predictors.evo``, the same technique ``test_model_gating.py`` uses
to exercise the real hardware gate without a real GPU. The logic under test
(``_extract_logits``) makes no torch call at all -- it is pure duck typing -- so this
proves the real code path, not a reimplementation of it.
"""

from __future__ import annotations

import sys
import types

import numpy as np
import pytest

from dna_entropy.predictors.base import NUM_NUCLEOTIDES, check_probability_matrix

# --- torch-free: the model-output normalization boundary (issue #293) -----------------


def _install_fake_torch_and_evo2(monkeypatch: pytest.MonkeyPatch) -> None:
    fake_torch = types.ModuleType("torch")
    fake_evo2 = types.ModuleType("evo2")
    fake_evo2.Evo2 = object  # never constructed by these tests
    monkeypatch.setitem(sys.modules, "torch", fake_torch)
    monkeypatch.setitem(sys.modules, "evo2", fake_evo2)
    monkeypatch.delitem(sys.modules, "dna_entropy.predictors.evo", raising=False)


@pytest.fixture
def evo_module(monkeypatch: pytest.MonkeyPatch):
    """Import the REAL dna_entropy.predictors.evo module against fake torch/evo2."""
    import importlib

    _install_fake_torch_and_evo2(monkeypatch)
    module = importlib.import_module("dna_entropy.predictors.evo")
    yield module
    # Never let a fake-backed module leak into later tests in the same process (the same
    # cleanup test_model_gating.py's own fixture does).
    sys.modules.pop("dna_entropy.predictors.evo", None)


def test_extract_logits_unwraps_nested_tuple(evo_module) -> None:
    leaf = np.zeros((3, 4), dtype=np.float32)
    nested = ((leaf, "inference_params"),)  # evo2's real shape, MEASURED on a GCP L4
    assert evo_module.EvoPredictor._extract_logits(nested) is leaf


def test_extract_logits_drops_batch_dimension(evo_module) -> None:
    batched = np.zeros((1, 3, 4), dtype=np.float32)
    out = evo_module.EvoPredictor._extract_logits(batched)
    assert out.shape == (3, 4)


def test_extract_logits_raises_predictor_error_not_attribute_error(evo_module) -> None:
    # A dict has neither `.logits` nor `.ndim`. Before the fix, the caller's own
    # `out.ndim == 3` check raised a bare AttributeError here; now it must be a named
    # PredictorError naming the type it actually got (issue #293).
    with pytest.raises(evo_module.PredictorError) as exc:
        evo_module.EvoPredictor._extract_logits({"unexpected": "shape"})
    assert "dict" in str(exc.value)


def test_extract_logits_raises_predictor_error_for_unrecognized_nested_element(evo_module) -> None:
    # The nested-tuple unwrap always takes element [0]; if THAT element is unrecognizable
    # the failure must still be a PredictorError, never a raw crash three lines later.
    with pytest.raises(evo_module.PredictorError):
        evo_module.EvoPredictor._extract_logits((None, "ignored"))


# --- GPU integration tests: need real Evo 2 weights on a GPU ---------------------------


@pytest.fixture(scope="module")
def evo():
    # Loads weights (large, first run downloads). Module-scoped to do it once. Skips this
    # whole fixture (and every test that requests it) cleanly where torch/evo2 aren't
    # installed, rather than failing collection of the file's torch-free tests above.
    pytest.importorskip("torch")
    pytest.importorskip("evo2")
    from dna_entropy.predictors.evo import EvoPredictor

    return EvoPredictor(model="evo2_7b", device="cuda")


@pytest.mark.gpu
def test_evo_output_satisfies_contract(evo) -> None:
    seq = "ACGTACGTACGTACGTACGT"
    probs = evo.predict(seq)
    check_probability_matrix(probs, len(seq))  # shape (L,4), float32, rows sum to 1
    assert probs.shape == (len(seq), NUM_NUCLEOTIDES)


@pytest.mark.gpu
def test_evo_first_position_is_uniform(evo) -> None:
    probs = evo.predict("ACGTACGT")
    assert np.allclose(probs[0], 1.0 / NUM_NUCLEOTIDES, atol=1e-5)


@pytest.mark.gpu
def test_evo_matches_mock_contract_shape(evo) -> None:
    # The whole point of the contract: Evo is a drop-in for the mock.
    from dna_entropy.predictors import MockPredictor

    seq = "ACGTACGTACGT"
    evo_probs = evo.predict(seq)
    mock_probs = MockPredictor().predict(seq)
    assert evo_probs.shape == mock_probs.shape
    assert evo_probs.dtype == mock_probs.dtype
