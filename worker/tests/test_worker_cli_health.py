"""`dna-entropy-worker health` (issue #173): the one line of JSON the app's local-engine health check reads.

The app never imports torch (Hard Rule 6) and only ``predictors/evo.py`` may (Hard Rule 1), so
"is CUDA usable in this venv" is asked of the worker, which asks that module.
"""

from __future__ import annotations

import json
import sys
import types

import pytest

from dna_entropy import __version__
from dna_entropy.worker.cli import main


def _last_json(capsys: pytest.CaptureFixture) -> dict:
    out = capsys.readouterr().out.strip().splitlines()
    return json.loads(out[-1])


def test_health_reports_version_and_no_evo_when_the_evo_extra_is_missing(
    capsys: pytest.CaptureFixture, monkeypatch: pytest.MonkeyPatch
) -> None:
    # A None entry makes `import` raise ImportError, which is the state of every venv without [evo].
    monkeypatch.setitem(sys.modules, "dna_entropy.predictors.evo", None)
    # `from ..predictors import evo` prefers an `evo` attribute already set on the package over
    # sys.modules, and test_evo_predictor / test_model_gating import the real module (against a
    # fake torch) and leave that attribute behind. Without this line the result depends on test
    # order (CI saw evo True, cuda True).
    monkeypatch.delattr("dna_entropy.predictors.evo", raising=False)

    assert main(["health"]) == 0

    assert _last_json(capsys) == {"worker": __version__, "evo": False, "cuda": False}


def test_health_reports_cuda_from_the_evo_module(
    capsys: pytest.CaptureFixture, monkeypatch: pytest.MonkeyPatch
) -> None:
    fake = types.ModuleType("dna_entropy.predictors.evo")
    fake.probe_cuda = lambda: True  # type: ignore[attr-defined]
    monkeypatch.setitem(sys.modules, "dna_entropy.predictors.evo", fake)
    monkeypatch.setattr("dna_entropy.predictors.evo", fake, raising=False)

    assert main(["health"]) == 0

    assert _last_json(capsys) == {"worker": __version__, "evo": True, "cuda": True}


def test_health_reports_evo_present_but_cuda_unusable(
    capsys: pytest.CaptureFixture, monkeypatch: pytest.MonkeyPatch
) -> None:
    fake = types.ModuleType("dna_entropy.predictors.evo")
    fake.probe_cuda = lambda: False  # type: ignore[attr-defined]
    monkeypatch.setitem(sys.modules, "dna_entropy.predictors.evo", fake)
    monkeypatch.setattr("dna_entropy.predictors.evo", fake, raising=False)

    assert main(["health"]) == 0

    assert _last_json(capsys) == {"worker": __version__, "evo": True, "cuda": False}


def test_health_survives_a_probe_that_raises(
    capsys: pytest.CaptureFixture, monkeypatch: pytest.MonkeyPatch
) -> None:
    def boom() -> bool:
        raise RuntimeError("driver exploded")

    fake = types.ModuleType("dna_entropy.predictors.evo")
    fake.probe_cuda = boom  # type: ignore[attr-defined]
    monkeypatch.setitem(sys.modules, "dna_entropy.predictors.evo", fake)
    monkeypatch.setattr("dna_entropy.predictors.evo", fake, raising=False)

    assert main(["health"]) == 0

    assert _last_json(capsys) == {"worker": __version__, "evo": True, "cuda": False}


def test_health_output_is_ascii_json_on_one_line(capsys: pytest.CaptureFixture) -> None:
    main(["health"])

    out = capsys.readouterr().out
    assert out.isascii()
    assert out.count("\n") == 1
