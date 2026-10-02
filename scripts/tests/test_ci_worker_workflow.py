"""Structure test for .github/workflows/ci-worker.yml's test job (#466).

The workflow cannot run locally, so this pins what makes the job test the LOCKED versions: a named lock
check, a frozen sync from worker/uv.lock, pytest through `uv run --frozen`, and no re-resolving
`uv pip install` left in the job. Text based on purpose: PyYAML is not in the worker venv that premerge
runs the scripts tests with, and a `pytest.importorskip("yaml")` here would skip silently, which is the
vacuous-guard shape this repo keeps meeting.
"""

from __future__ import annotations

import re
from pathlib import Path

WORKFLOW = Path(__file__).resolve().parent.parent.parent / ".github" / "workflows" / "ci-worker.yml"


def _test_job_text() -> str:
    text = WORKFLOW.read_text(encoding="utf-8")
    match = re.search(r"^  test:\n(.*?)(?=^  \w[\w-]*:\n|\Z)", text, re.DOTALL | re.MULTILINE)
    assert match, "the `test` job was not found; the scanner would otherwise vacuously pass"
    body = match.group(1)
    assert "pytest" in body and "setup-uv" in body, "scanner sees a different job than the test job"
    return body


def _commands(body: str) -> list[str]:
    """Every shell line of every `run:` in the job, comments dropped."""
    lines: list[str] = []
    in_block = False
    for raw in body.splitlines():
        stripped = raw.strip().removeprefix("- ")
        if stripped.startswith("run:"):
            rest = stripped[4:].strip()
            if rest and rest != "|":
                lines.append(rest)
            in_block = rest == "|"
            continue
        if in_block:
            if raw.startswith("          ") and stripped and not stripped.startswith("#"):
                lines.append(stripped)
            elif stripped and not raw.startswith("          "):
                in_block = False
    return lines


def test_the_test_job_installs_from_the_lock_not_by_re_resolving():
    cmds = _commands(_test_job_text())
    assert any(re.search(r"\buv sync\b.*--frozen", c) for c in cmds), cmds
    assert not any("uv pip install" in c for c in cmds), cmds


def test_the_frozen_sync_keeps_the_dev_boxs_extras_and_leaves_evo_out():
    sync = next(c for c in _commands(_test_job_text()) if "uv sync" in c)
    assert "--extra dev" in sync and "--extra genes" in sync and "--extra evo" not in sync


def test_a_named_lock_check_step_runs_before_the_sync_and_fails_on_drift():
    body = _test_job_text()
    check = re.search(r"- name: ([^\n]*lock[^\n]*)\n(?:(?!\n      - ).)*?run: uv lock --check", body, re.DOTALL | re.IGNORECASE)
    assert check, "no named step runs `uv lock --check`"
    assert body.index("uv lock --check") < body.index("uv sync"), "the lock check must come before the sync"


def test_pytest_runs_through_uv_run_frozen_so_it_cannot_rewrite_the_lock():
    pytest_cmds = [c for c in _commands(_test_job_text()) if "pytest" in c]
    assert pytest_cmds and all("uv run --frozen" in c for c in pytest_cmds), pytest_cmds


def test_the_scanner_sees_a_pip_install_when_one_is_planted():
    planted = "      - run: |\n          uv venv\n          uv pip install -e .\n"
    assert any("uv pip install" in c for c in _commands(planted))
