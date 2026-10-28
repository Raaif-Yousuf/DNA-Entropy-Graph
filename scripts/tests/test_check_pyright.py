"""Tests for scripts/check_pyright.py (the pyright ratchet).

The comparison logic is tested against hand-built pyright reports, so it needs no pyright and
no network. One test runs the REAL pinned pyright on a two-file temp project, because the whole
gate rests on an assumption about pyright's JSON shape that a hand-built report cannot prove.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_pyright as cp  # noqa: E402

SCRIPT = SCRIPTS_DIR / "check_pyright.py"


def _diag(root: Path, rel: str, severity: str = "error", rule: str = "reportArgumentType") -> dict:
    return {
        "file": str(root / rel),  # absolute, native separators: what pyright prints
        "severity": severity,
        "message": "m",
        "rule": rule,
        "range": {"start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 1}},
    }


def _report(root: Path, errors: dict[str, int], analysed: int = 100, extra: list[dict] | None = None) -> dict:
    diags = [_diag(root, rel) for rel, n in errors.items() for _ in range(n)]
    return {"generalDiagnostics": diags + (extra or []), "summary": {"filesAnalyzed": analysed}}


def _touch(root: Path, *rels: str) -> None:
    for rel in rels:
        p = root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text("x = 1\n", encoding="utf-8", newline="\n")


# --- counting ---------------------------------------------------------------------------------


def test_errors_are_counted_per_file_relative_to_the_root_with_forward_slashes(tmp_path: Path) -> None:
    report = _report(tmp_path, {"worker/tests/test_a.py": 3, "scripts/x.py": 1})
    assert cp.errors_by_file(report, tmp_path) == {"worker/tests/test_a.py": 3, "scripts/x.py": 1}


def test_warnings_and_information_are_not_counted(tmp_path: Path) -> None:
    report = _report(
        tmp_path,
        {"scripts/x.py": 1},
        extra=[_diag(tmp_path, "scripts/x.py", "warning"), _diag(tmp_path, "scripts/x.py", "information")],
    )
    assert cp.errors_by_file(report, tmp_path) == {"scripts/x.py": 1}


def test_a_report_with_no_diagnostics_key_is_an_error_not_a_clean_tree(tmp_path: Path) -> None:
    with pytest.raises(cp.PyrightGateError):
        cp.errors_by_file({"summary": {"filesAnalyzed": 100}}, tmp_path)


def test_vacuity_a_run_that_analysed_almost_nothing_is_refused(tmp_path: Path) -> None:
    """A wrong config (no `include`) analyses 0 files and reports 0 errors; that must not read as clean."""
    with pytest.raises(cp.PyrightGateError, match="analysed"):
        cp.check_report(_report(tmp_path, {}, analysed=3), tmp_path, {})


# --- the ratchet ------------------------------------------------------------------------------


def test_at_baseline_passes(tmp_path: Path) -> None:
    _touch(tmp_path, "worker/tests/test_a.py")
    cur = {"worker/tests/test_a.py": 2}
    assert cp.compare(cur, {"worker/tests/test_a.py": 2}, tmp_path) == []


def test_an_error_over_the_baseline_fails_naming_both_numbers(tmp_path: Path) -> None:
    _touch(tmp_path, "worker/tests/test_a.py")
    problems = cp.compare({"worker/tests/test_a.py": 3}, {"worker/tests/test_a.py": 2}, tmp_path)
    assert len(problems) == 1
    assert problems[0].startswith("NEW:")
    assert "worker/tests/test_a.py" in problems[0]
    assert "3" in problems[0] and "2" in problems[0]


def test_a_file_not_in_the_baseline_is_held_to_zero(tmp_path: Path) -> None:
    _touch(tmp_path, "scripts/new.py")
    problems = cp.compare({"scripts/new.py": 1}, {}, tmp_path)
    assert [p.split(":")[0] for p in problems] == ["NEW"]


def test_a_count_that_went_down_but_whose_baseline_was_not_lowered_is_stale(tmp_path: Path) -> None:
    """The allowlist-that-expires-itself arm: progress must be banked, or the slack lets a new error in."""
    _touch(tmp_path, "worker/tests/test_a.py")
    problems = cp.compare({"worker/tests/test_a.py": 1}, {"worker/tests/test_a.py": 4}, tmp_path)
    assert len(problems) == 1
    assert problems[0].startswith("STALE:")
    assert "1" in problems[0] and "4" in problems[0]
    assert "--lower" in problems[0]


def test_a_file_fixed_to_zero_leaves_a_stale_entry(tmp_path: Path) -> None:
    _touch(tmp_path, "worker/tests/test_a.py")
    problems = cp.compare({}, {"worker/tests/test_a.py": 4}, tmp_path)
    assert [p.split(":")[0] for p in problems] == ["STALE"]


def test_an_entry_for_a_deleted_file_is_stale_and_says_so(tmp_path: Path) -> None:
    problems = cp.compare({}, {"worker/tests/gone.py": 4}, tmp_path)
    assert len(problems) == 1
    assert problems[0].startswith("STALE:") and "no longer exists" in problems[0]


def test_worker_src_may_not_be_in_the_baseline_at_all(tmp_path: Path) -> None:
    _touch(tmp_path, "worker/src/dna_entropy/x.py")
    problems = cp.compare({"worker/src/dna_entropy/x.py": 1}, {"worker/src/dna_entropy/x.py": 1}, tmp_path)
    assert any(p.startswith("ZERO:") and "worker/src/dna_entropy/x.py" in p for p in problems)


def test_worker_src_errors_fail_even_with_an_empty_baseline(tmp_path: Path) -> None:
    _touch(tmp_path, "worker/src/dna_entropy/x.py")
    assert cp.compare({"worker/src/dna_entropy/x.py": 1}, {}, tmp_path)


# --- the baseline file ------------------------------------------------------------------------


@pytest.mark.parametrize("bad", [0, -1, "3", 1.5, True, None])
def test_a_baseline_count_must_be_a_positive_whole_number(tmp_path: Path, bad) -> None:
    p = tmp_path / "b.json"
    p.write_text(json.dumps({"files": {"scripts/x.py": bad}}), encoding="utf-8", newline="\n")
    with pytest.raises(cp.PyrightGateError, match="scripts/x.py"):
        cp.load_baseline(p)


def test_a_missing_baseline_file_is_an_error(tmp_path: Path) -> None:
    with pytest.raises(cp.PyrightGateError, match="baseline"):
        cp.load_baseline(tmp_path / "nope.json")


def test_the_committed_baseline_loads_and_keeps_worker_src_at_zero() -> None:
    baseline = cp.load_baseline(cp.BASELINE_PATH)
    assert baseline, "vacuity: the committed baseline lists the test-suite debt, so it cannot be empty"
    assert not [f for f in baseline if f.startswith(cp.ZERO_PREFIXES)]
    # every entry is a real file (the stale-entry arm would also say so, but not before a run)
    repo = SCRIPTS_DIR.parent
    assert [f for f in baseline if not (repo / f).is_file()] == []


def test_lower_writes_min_of_current_and_baseline_and_drops_fixed_files(tmp_path: Path) -> None:
    _touch(tmp_path, "scripts/a.py", "scripts/b.py", "scripts/c.py")
    base = tmp_path / "b.json"
    base.write_text(json.dumps({"files": {"scripts/a.py": 5, "scripts/b.py": 2, "scripts/c.py": 1}}), encoding="utf-8")
    new = cp.lowered_baseline({"scripts/a.py": 3, "scripts/b.py": 2}, cp.load_baseline(base))
    assert new == {"scripts/a.py": 3, "scripts/b.py": 2}  # a lowered, b kept, c (fixed) dropped


def test_lower_refuses_when_anything_grew(tmp_path: Path) -> None:
    """--lower must never be the way to bank a new error."""
    with pytest.raises(cp.PyrightGateError, match="grew"):
        cp.lowered_baseline({"scripts/a.py": 6}, {"scripts/a.py": 5})
    with pytest.raises(cp.PyrightGateError, match="grew"):
        cp.lowered_baseline({"scripts/new.py": 1}, {})


# --- end to end through the CLI, on a saved report (no pyright) ---------------------------------


def _cli(tmp_path: Path, report: dict, baseline: dict, *extra: str) -> subprocess.CompletedProcess[str]:
    (tmp_path / "report.json").write_text(json.dumps(report), encoding="utf-8", newline="\n")
    (tmp_path / "baseline.json").write_text(json.dumps({"files": baseline}), encoding="utf-8", newline="\n")
    return subprocess.run(
        [
            sys.executable,
            str(SCRIPT),
            "--root",
            str(tmp_path),
            "--report",
            str(tmp_path / "report.json"),
            "--baseline",
            str(tmp_path / "baseline.json"),
            *extra,
        ],
        capture_output=True,
        text=True,
        timeout=60,
    )


def test_cli_exit_codes_and_ascii_output(tmp_path: Path) -> None:
    _touch(tmp_path, "scripts/a.py", "worker/src/dna_entropy/x.py")
    ok = _cli(tmp_path, _report(tmp_path, {"scripts/a.py": 2}), {"scripts/a.py": 2})
    assert ok.returncode == 0, ok.stdout + ok.stderr
    assert ok.stdout.startswith("OK:")

    over = _cli(tmp_path, _report(tmp_path, {"scripts/a.py": 3}), {"scripts/a.py": 2})
    assert over.returncode == 1
    assert "NEW:" in over.stderr + over.stdout
    assert (over.stdout + over.stderr).isascii()

    stale = _cli(tmp_path, _report(tmp_path, {"scripts/a.py": 1}), {"scripts/a.py": 2})
    assert stale.returncode == 1 and "STALE:" in stale.stderr + stale.stdout

    src = _cli(tmp_path, _report(tmp_path, {"worker/src/dna_entropy/x.py": 1}), {})
    assert src.returncode == 1


def test_cli_lower_rewrites_the_baseline_file_in_place(tmp_path: Path) -> None:
    _touch(tmp_path, "scripts/a.py", "scripts/b.py")
    done = _cli(
        tmp_path,
        _report(tmp_path, {"scripts/a.py": 1}),
        {"scripts/a.py": 4, "scripts/b.py": 2},
        "--lower",
    )
    assert done.returncode == 0, done.stdout + done.stderr
    text = (tmp_path / "baseline.json").read_bytes()
    assert b"\r" not in text
    assert json.loads(text)["files"] == {"scripts/a.py": 1}


# --- the real thing -----------------------------------------------------------------------------


@pytest.mark.skipif(shutil.which("uvx") is None, reason="needs uvx to fetch the pinned pyright")
def test_the_real_pinned_pyright_report_has_the_shape_the_gate_reads(tmp_path: Path) -> None:
    (tmp_path / "pkg").mkdir()
    (tmp_path / "pkg" / "bad.py").write_text("def f(x: int) -> int:\n    return x\n\n\nf('a')\nf(None)\n", encoding="utf-8", newline="\n")
    (tmp_path / "pkg" / "good.py").write_text("y: int = 1\n", encoding="utf-8", newline="\n")
    (tmp_path / "pyrightconfig.json").write_text(
        json.dumps({"include": ["pkg"], "pythonVersion": "3.12", "typeCheckingMode": "basic"}),
        encoding="utf-8",
        newline="\n",
    )
    report = cp.run_pyright(tmp_path, sys.executable)
    assert cp.errors_by_file(report, tmp_path) == {"pkg/bad.py": 2}
    assert report["summary"]["filesAnalyzed"] == 2
