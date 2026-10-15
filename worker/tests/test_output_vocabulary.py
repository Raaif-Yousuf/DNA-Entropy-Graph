"""Issue #103: the ``outputs`` vocabulary in docs/job_contract.md is what the worker really understands.

The app's ``OutputCatalog`` (C#) is pinned to the same contract row by ``OutputCatalogTests``; this test pins
the worker to it, so a name cannot be offered on the New run page that the worker would silently ignore (a
wired-to-nothing checkbox). The end-to-end proof that a manifest without ``wig`` writes no ``.wig`` and lists
none in ``result.json`` is ``test_smoothing.py::_job`` (outputs -> files -> result.json) and
``test_genbank.py`` (a GenBank run with only bedgraph writes no ``.wig``).
"""

from __future__ import annotations

import dataclasses
import json
import re
from pathlib import Path

import pytest

from dna_entropy.config import RunConfig
from dna_entropy.worker.manifest import JobManifest

_CONTRACT = Path(__file__).resolve().parents[2] / "docs" / "job_contract.md"


def _vocabulary() -> list[str]:
    row = next(
        line
        for line in _CONTRACT.read_text(encoding="utf-8").splitlines()
        if line.startswith("| `outputs` |")
    )
    match = re.search(r"individually: `([a-z0-9_ \\|]+)` \(see", row)
    assert match, "the outputs row no longer lists its vocabulary as `a | b | c`"
    return [name.strip() for name in match.group(1).replace("\\", "").split("|")]


def _cfg(outputs: list[str]) -> RunConfig:
    m = JobManifest.parse(
        json.dumps(
            {
                "schema": 1,
                "jobId": "j1",
                "inputs": [{"id": "i1", "path": "input/x.fa", "name": "x"}],
                "outputs": outputs,
                "store": {"kind": "localdir", "root": "/tmp/store"},
            }
        )
    )
    return m.build_run_config(m.inputs[0], local_input_path="x.fa", local_out_dir="out")


def _gates(cfg: RunConfig) -> dict[str, object]:
    """Every field of a RunConfig that an ``outputs`` name can change."""
    fields = {f.name for f in dataclasses.fields(cfg) if f.name.startswith("include_")}
    return {name: getattr(cfg, name) for name in sorted(fields | {"track_format", "genes"})}


def test_the_contract_row_was_parsed() -> None:
    names = _vocabulary()

    assert len(names) >= 13
    assert len(set(names)) == len(names)


@pytest.mark.parametrize("name", _vocabulary())
def test_every_name_in_the_contract_changes_what_the_worker_writes(name: str) -> None:
    nothing = _gates(_cfg(["not_a_real_output"]))
    only_this = _gates(_cfg([name]))

    assert only_this != nothing, f"outputs name {name!r} is in the contract but the worker ignores it"
