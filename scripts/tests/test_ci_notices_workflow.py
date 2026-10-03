"""Structure test for .github/workflows/ci-notices.yml (#416).

The job cannot be run locally (it needs a windows-latest runner with dotnet), so this pins what
makes it a REAL check rather than a vacuous one: both prerequisites are set up before the
checker runs, the venv is the locked one, and the checker runs on that venv's python.

Text based on purpose, like test_ci_worker_workflow.py: PyYAML is not in worker\\.venv, which is what
premerge runs the scripts tests with, so a `pytest.importorskip("yaml")` skipped this whole file there
(MEASURED 2026-10-02) and nothing ever ran it. A step is a chunk of text starting at `      - `.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

WORKFLOW = Path(__file__).resolve().parent.parent.parent / ".github" / "workflows" / "ci-notices.yml"


def _steps(text: str) -> list[str]:
    """Each step of the `notices` job as its own block of text (comments inside a step included)."""
    job = re.search(r"^  notices:\n(.*)\Z", text, re.DOTALL | re.MULTILINE)
    assert job, "the `notices` job was not found; the scanner would otherwise vacuously pass"
    steps_part = job.group(1).split("    steps:\n", 1)[1]
    steps = re.split(r"(?m)^(?=      - )", steps_part)
    return [s for s in steps if s.startswith("      - ")]


def _code(step: str) -> str:
    """The step with comment-only lines dropped, so a word in a comment never satisfies a check."""
    return "\n".join(ln for ln in step.splitlines() if not ln.strip().startswith("#"))


@pytest.fixture(scope="module")
def steps() -> list[str]:
    found = [_code(s) for s in _steps(WORKFLOW.read_text(encoding="utf-8"))]
    assert len(found) >= 6, f"scanner sees only {len(found)} steps"
    return found


def _index(steps: list[str], needle: str) -> int:
    for i, text in enumerate(steps):
        if needle in text:
            return i
    raise AssertionError(f"no step mentions {needle!r}")


def _run_of(step: str) -> str:
    m = re.search(r"^\s+run:\s*(.*)$", step, re.MULTILINE)
    return m.group(1).strip() if m else ""


def test_runs_on_windows_because_app_targets_windows():
    assert re.search(r"^    runs-on: windows-latest$", WORKFLOW.read_text(encoding="utf-8"), re.MULTILINE)


def test_the_workflow_has_exactly_one_job():
    text = WORKFLOW.read_text(encoding="utf-8")
    assert re.findall(r"^  (\w[\w-]*):\n", text.split("\njobs:\n", 1)[1], re.MULTILINE) == ["notices"]


def test_dotnet_comes_from_global_json(steps):
    setup = steps[_index(steps, "actions/setup-dotnet@")]
    assert re.search(r"global-json-file: app/global\.json$", setup, re.MULTILINE)


def test_worker_venv_is_the_locked_one_with_the_dev_boxs_extras(steps):
    step = steps[_index(steps, "uv sync")]
    run = _run_of(step)
    assert "--frozen" in run and "--extra dev" in run and "--extra genes" in run and "--extra evo" not in run
    assert re.search(r"^\s+working-directory: worker$", step, re.MULTILINE)


def test_app_is_restored_inside_app(steps):
    step = steps[_index(steps, "dotnet restore")]
    assert re.search(r"^\s+working-directory: app$", step, re.MULTILINE)


def _checker_index(steps: list[str]) -> int:
    """Index of THE checker step, found by name, and required to be the last step.

    The self-test step also names the script, so it is excluded by its --self-test flag. Exactly one
    step may qualify, otherwise a duplicate or a missing checker would pass unnoticed.
    """
    hits = [i for i, step in enumerate(steps) if "check_third_party_notices.py" in _run_of(step) and "--self-test" not in _run_of(step)]
    assert len(hits) == 1, f"expected exactly one checker step, found {len(hits)}"
    assert hits[0] == len(steps) - 1, f"the checker step is #{hits[0]} of {len(steps)}; it must be last"
    return hits[0]


def test_checker_lookup_by_name_rejects_a_misplaced_or_missing_checker():
    checker = "      - name: c\n        run: worker\\.venv\\Scripts\\python.exe scripts/check_third_party_notices.py\n"
    selftest = checker.rstrip("\n") + " --self-test\n"
    other = "      - run: echo hi\n"
    assert _checker_index([other, selftest, checker]) == 2
    with pytest.raises(AssertionError, match="must be last"):
        _checker_index([checker, other])
    with pytest.raises(AssertionError, match="found 0"):
        _checker_index([other, selftest])
    with pytest.raises(AssertionError, match="found 2"):
        _checker_index([checker, checker])


def test_checker_runs_last_on_the_venv_python_after_both_prerequisites(steps):
    check = _checker_index(steps)
    last = _run_of(steps[check])
    assert "worker\\.venv\\Scripts\\python.exe" in last
    assert last.endswith("check_third_party_notices.py")
    assert _index(steps, "uv sync") < check
    assert _index(steps, "dotnet restore") < check


def test_it_guards_against_the_vacuous_pass_and_runs_the_self_test(steps):
    assert _index(steps, "THIRD-PARTY-NOTICES.md") < len(steps) - 1
    assert "--self-test" in _run_of(steps[-2])


def test_path_filter_covers_every_input_of_the_notices():
    text = WORKFLOW.read_text(encoding="utf-8")
    block = re.search(r"^on:\n  pull_request:\n    paths:\n((?:      - .*\n)+)", text, re.MULTILINE)
    assert block, "the pull_request paths filter was not found"
    paths = [ln.strip()[2:].strip().strip("\"'") for ln in block.group(1).splitlines()]
    for needed in (
        "worker/uv.lock",
        "worker/pyproject.toml",
        "app/Directory.Packages.props",
        "THIRD-PARTY-NOTICES.md",
        "scripts/gen_third_party_notices.py",
    ):
        assert needed in paths
