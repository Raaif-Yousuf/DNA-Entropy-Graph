"""Issue #161: golden-file determinism. The MockPredictor is deterministic, so every file a run
writes for the three fixtures must match the committed bytes. A change to a writer's number
formatting, a coordinate convention or the analysis fails here until the golden files are
regenerated on purpose with a justification (``tests/golden/regenerate.py``)."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest
from golden import cases

GOLDEN = Path(__file__).parent / "golden" / "files"


def _golden(case: str) -> dict[str, bytes]:
    return {p.name: p.read_bytes() for p in sorted((GOLDEN / case).iterdir())}


def differences(produced: dict[str, bytes], expected: dict[str, bytes]) -> list[str]:
    """Names of files that are missing, extra or different (empty list == identical)."""
    out = [f"missing: {n}" for n in sorted(set(expected) - set(produced))]
    out += [f"extra: {n}" for n in sorted(set(produced) - set(expected))]
    out += [f"differs: {n}" for n in sorted(set(produced) & set(expected)) if produced[n] != expected[n]]
    return out


@pytest.mark.parametrize("case", sorted(cases.CASES))
def test_run_output_matches_the_golden_bytes(case: str, tmp_path: Path) -> None:
    expected = _golden(case)
    assert expected, f"no golden files for {case}: run worker/tests/golden/regenerate.py"
    diff = differences(cases.build(case, tmp_path / "out"), expected)
    assert not diff, (
        f"{case}: {diff}; if intended, run worker/tests/golden/regenerate.py --justification '...'"
    )


def test_the_comparison_catches_a_number_formatting_change() -> None:
    """The issue's observable: a writer printing 3 decimals instead of 4 must be a failure."""
    expected = _golden("sample_gb")
    tampered = dict(expected)
    name = "golden.entropy.bedgraph"
    rows = []
    for line in expected[name].decode("utf-8").splitlines():
        head, _, value = line.rpartition("\t")
        rows.append(f"{head}\t{float(value):.3f}" if head and not line.startswith("track") else line)
    tampered[name] = ("\n".join(rows) + "\n").encode("utf-8")
    assert differences(tampered, expected) == [f"differs: {name}"]


def test_the_comparison_catches_a_missing_and_an_extra_file() -> None:
    expected = _golden("sample_gb")
    produced = dict(expected)
    produced.pop("provenance.json")
    produced["stray.txt"] = b"x"
    assert differences(produced, expected) == ["missing: provenance.json", "extra: stray.txt"]


@pytest.mark.parametrize("case", sorted(cases.CASES))
def test_golden_files_are_utf8_lf_and_free_of_volatile_keys(case: str) -> None:
    for name, data in _golden(case).items():
        assert b"\r" not in data, f"{name} has CR"
        data.decode("utf-8")
    prov = _golden(case)["provenance.json"].decode("utf-8")
    for key in ("generated_at", "wall_time_seconds", "worker_version"):
        assert key not in prov


def test_normalize_leaves_non_provenance_files_alone() -> None:
    assert cases.normalize("x.tsv", b"1\t2\n") == b"1\t2\n"


def test_regenerate_requires_a_justification() -> None:
    script = Path(__file__).parent / "golden" / "regenerate.py"
    proc = subprocess.run([sys.executable, str(script)], capture_output=True, text=True, check=False)
    assert proc.returncode == 1
    assert "justification" in proc.stderr
    assert "ERROR" in proc.stderr
