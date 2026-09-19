"""Tests for predictors/hardware.py: MODEL_NEEDS_HOPPER gating (issue #282).

Pure logic, no torch import, fully unit-testable with no GPU: the real
``torch.cuda.get_device_capability()`` value is injected by the caller (EvoPredictor);
here we pass synthetic tuples to exercise every branch.
"""

from __future__ import annotations

import pytest

from dna_entropy.predictors.base import PredictorError
from dna_entropy.predictors.hardware import (
    FALLBACK_MODEL,
    HOPPER_COMPUTE_CAPABILITY,
    MODEL_REQUIREMENTS,
    ModelNeedsHopperError,
    model_requirement,
    require_hardware,
)

L4 = (8, 9)  # sm_89
A100 = (8, 0)  # sm_80
H100 = (9, 0)  # sm_90
FUTURE_HOPPER_PLUS = (9, 5)  # a hypothetical newer-than-H100 part


# --- model_requirement lookup ----------------------------------------------------------


@pytest.mark.parametrize("model_id", ["evo2_7b", "evo2_7b_262k"])
def test_7b_variants_do_not_need_hopper(model_id: str) -> None:
    req = model_requirement(model_id)
    assert req.needs_hopper is False
    assert req.precision == "bf16"


@pytest.mark.parametrize("model_id", ["evo2_1b_base", "evo2_20b", "evo2_40b"])
def test_1b_20b_40b_need_hopper(model_id: str) -> None:
    req = model_requirement(model_id)
    assert req.needs_hopper is True
    assert req.precision == "fp8"


def test_40b_needs_two_gpus() -> None:
    assert MODEL_REQUIREMENTS["evo2_40b"].min_gpu_count == 2


def test_1b_and_20b_need_one_gpu() -> None:
    assert MODEL_REQUIREMENTS["evo2_1b_base"].min_gpu_count == 1
    assert MODEL_REQUIREMENTS["evo2_20b"].min_gpu_count == 1


def test_unknown_model_id_defaults_to_not_needing_hopper() -> None:
    req = model_requirement("some_future_model_id")
    assert req.needs_hopper is False


# --- require_hardware: gates on ACTUAL compute capability, not model name alone -------


@pytest.mark.parametrize("model_id", ["evo2_7b", "evo2_7b_262k"])
def test_7b_variants_pass_on_any_device_including_non_hopper(model_id: str) -> None:
    require_hardware(model_id, device="cuda", compute_capability=L4)  # must not raise
    require_hardware(model_id, device="cuda", compute_capability=A100)  # must not raise
    require_hardware(model_id, device="cpu", compute_capability=None)  # must not raise


@pytest.mark.parametrize("model_id", ["evo2_1b_base", "evo2_20b", "evo2_40b"])
def test_hopper_only_model_refused_on_l4(model_id: str) -> None:
    with pytest.raises(ModelNeedsHopperError):
        require_hardware(model_id, device="cuda", compute_capability=L4)


@pytest.mark.parametrize("model_id", ["evo2_1b_base", "evo2_20b", "evo2_40b"])
def test_hopper_only_model_refused_on_a100(model_id: str) -> None:
    with pytest.raises(ModelNeedsHopperError):
        require_hardware(model_id, device="cuda", compute_capability=A100)


@pytest.mark.parametrize("model_id", ["evo2_1b_base", "evo2_20b", "evo2_40b"])
def test_hopper_only_model_accepted_on_h100(model_id: str) -> None:
    require_hardware(model_id, device="cuda", compute_capability=H100)  # must not raise


def test_hopper_only_model_accepted_on_a_newer_than_hopper_part() -> None:
    # Gating is a >= comparison on compute capability, not an exact-match allowlist:
    # a hypothetical future part reporting a HIGHER capability than Hopper must still pass.
    require_hardware("evo2_40b", device="cuda", compute_capability=FUTURE_HOPPER_PLUS)


def test_hopper_only_model_refused_with_no_cuda_device() -> None:
    with pytest.raises(ModelNeedsHopperError):
        require_hardware("evo2_40b", device="cpu", compute_capability=None)


def test_hopper_only_model_refused_when_capability_unknown_even_if_device_is_cuda() -> None:
    # device="cuda" but capability couldn't be read (e.g. CUDA unavailable at runtime):
    # must fail closed (refuse), never silently assume it's fine.
    with pytest.raises(ModelNeedsHopperError):
        require_hardware("evo2_40b", device="cuda", compute_capability=None)


def test_error_names_what_to_use_instead() -> None:
    with pytest.raises(ModelNeedsHopperError) as exc:
        require_hardware("evo2_40b", device="cuda", compute_capability=L4)
    msg = str(exc.value)
    assert FALLBACK_MODEL in msg
    assert "L4" in msg or "A100" in msg


def test_error_has_the_model_needs_hopper_code() -> None:
    with pytest.raises(ModelNeedsHopperError) as exc:
        require_hardware("evo2_20b", device="cuda", compute_capability=L4)
    assert exc.value.code == "MODEL_NEEDS_HOPPER"


def test_hopper_floor_is_exactly_sm_90() -> None:
    assert HOPPER_COMPUTE_CAPABILITY == (9, 0)


# --- wiring: EvoPredictor.__init__ actually calls the gate, before Evo2(model) loads ---
#
# Fakes `torch`/`evo2` (torch/evo2 are not installed on this laptop) so the REAL
# evo.py module executes, proving the gate is reachable from the real code path, not
# just present as an unused pure function. Cleans up sys.modules explicitly afterward so
# this never leaks a fake module into other tests (e.g. test_pipeline.py's
# "Evo predictor is not available" test, which depends on the real ImportError).


class _FakeTokenizer:
    def tokenize(self, seq: str):
        return [0, 1, 2, 3] if seq == "ACGT" else [0] * len(seq)


class _FakeEvo2:
    def __init__(self, model_id: str) -> None:
        self.model_id = model_id
        self.tokenizer = _FakeTokenizer()
        raise AssertionError(
            f"Evo2({model_id!r}) was constructed — the hardware gate did not fire "
            "before weight loading began."
        )


def _install_fake_torch_and_evo2(monkeypatch: pytest.MonkeyPatch, capability) -> None:
    import sys
    import types

    fake_torch = types.ModuleType("torch")
    fake_torch_cuda = types.ModuleType("torch.cuda")
    fake_torch_cuda.is_available = lambda: capability is not None
    fake_torch_cuda.get_device_capability = lambda: capability
    fake_torch_cuda.OutOfMemoryError = type("OutOfMemoryError", (RuntimeError,), {})
    fake_torch.cuda = fake_torch_cuda
    fake_torch.Tensor = object

    fake_evo2 = types.ModuleType("evo2")
    fake_evo2.Evo2 = _FakeEvo2

    monkeypatch.setitem(sys.modules, "torch", fake_torch)
    monkeypatch.setitem(sys.modules, "torch.cuda", fake_torch_cuda)
    monkeypatch.setitem(sys.modules, "evo2", fake_evo2)
    monkeypatch.delitem(sys.modules, "dna_entropy.predictors.evo", raising=False)


def _import_fresh_evo_module():
    import importlib
    import sys

    sys.modules.pop("dna_entropy.predictors.evo", None)
    return importlib.import_module("dna_entropy.predictors.evo")


@pytest.fixture
def cleanup_fake_evo_module():
    yield
    import sys

    # Never let a fake-backed module leak into later tests in the same process.
    sys.modules.pop("dna_entropy.predictors.evo", None)


def test_evo_predictor_init_refuses_hopper_only_model_before_loading_weights(
    monkeypatch: pytest.MonkeyPatch, cleanup_fake_evo_module,
) -> None:
    _install_fake_torch_and_evo2(monkeypatch, capability=L4)
    evo_module = _import_fresh_evo_module()

    with pytest.raises(ModelNeedsHopperError):
        evo_module.EvoPredictor(model="evo2_40b", device="cuda")
    # _FakeEvo2.__init__ itself asserts it was never reached; reaching this line at all
    # (rather than an AssertionError bubbling up) is the proof the gate fired first.


def test_evo_predictor_init_allows_7b_on_l4(
    monkeypatch: pytest.MonkeyPatch, cleanup_fake_evo_module,
) -> None:
    _install_fake_torch_and_evo2(monkeypatch, capability=L4)
    evo_module = _import_fresh_evo_module()

    # 7B is not gated, so construction proceeds to _FakeEvo2, which raises by design to
    # prove IT was reached (a real Evo2() call is not something this test can do).
    # EvoPredictor.__init__ wraps that failure as PredictorError ("weights missing, OOM,
    # etc."), so the fake's AssertionError text survives inside PredictorError's message.
    with pytest.raises(PredictorError, match="was constructed"):
        evo_module.EvoPredictor(model="evo2_7b", device="cuda")
