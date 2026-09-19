"""Tests for scripts/new_issue.ps1.

Network-free throughout: every test here stops at the dry-run report or the
self-test's own fixtures. Nothing calls `gh issue create` for real -- that
would file a real issue on Raaif-Yousuf/DNA-Entropy-Graph on every test run,
which is not what a test suite is for.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
SCRIPT = SCRIPTS_DIR / "new_issue.ps1"

PWSH = shutil.which("pwsh")
pytestmark = pytest.mark.skipif(PWSH is None, reason="pwsh (PowerShell 7+) not found on PATH")


def _run(*args: str, timeout: int = 30) -> subprocess.CompletedProcess:
    return subprocess.run(
        [PWSH, "-NoProfile", "-NonInteractive", "-File", str(SCRIPT), *args],
        capture_output=True, text=True, timeout=timeout,
    )


def test_self_test_passes():
    proc = _run("-SelfTest")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = _run("-Help")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "-DoneWhen" in proc.stdout


def test_missing_title_is_a_usage_error():
    proc = _run("-DoneWhen", "x")
    assert proc.returncode == 2


def test_missing_done_when_is_a_usage_error():
    proc = _run("-Title", "scripts: x")
    assert proc.returncode == 2
    assert "needs-criteria" in (proc.stdout + proc.stderr)


def test_dry_run_prints_the_full_template_and_files_nothing():
    proc = _run(
        "-Title", "scripts: example",
        "-Why", "Users cannot do the thing.",
        "-DoneWhen", "a runs shows the thing",
        "-Observable", "the history row",
        "-Labels", "P3,area:docs",
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "[dry-run]" in proc.stdout
    assert "## Why" in proc.stdout
    assert "Users cannot do the thing." in proc.stdout
    assert "- [ ] a runs shows the thing" in proc.stdout
    assert "## Cloud money" in proc.stdout
    assert "none" in proc.stdout


def test_dry_run_defaults_cloud_money_to_none():
    proc = _run("-Title", "scripts: example", "-DoneWhen", "x", "-Observable", "y")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    lines = proc.stdout.splitlines()
    idx = lines.index("## Cloud money")
    assert lines[idx + 1].strip() == "none"


def test_whatif_overrides_apply_and_files_nothing():
    proc = _run(
        "-Title", "scripts: example", "-DoneWhen", "x", "-Observable", "y",
        "-Apply", "-WhatIf",
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "[dry-run]" in proc.stdout


def test_unrecognised_title_shape_warns_but_does_not_block_a_dry_run():
    proc = _run("-Title", "not a valid shape at all", "-DoneWhen", "x", "-Observable", "y")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "WARNING" in proc.stdout
    assert "[dry-run]" in proc.stdout


def test_multiple_done_when_items_each_become_their_own_checklist_line():
    # A raw-argv invocation (subprocess.run's list form, no shell involved --
    # what _run() above does, and what a non-PowerShell caller would do) can
    # only ever bind ONE string to an array parameter: PowerShell's own
    # comma-array literal syntax (`-DoneWhen "a","b"`) is parsed by the
    # PowerShell PARSER, which never runs when argv is handed to the process
    # directly. Exercising the real, documented usage means going through
    # `pwsh -Command`, so PowerShell itself parses the array literal, exactly
    # as it would for an interactive caller or an orchestrator's own .ps1.
    command = (
        f"& '{SCRIPT}' -Title 'scripts: example' -Observable 'y' "
        "-DoneWhen 'first thing','second thing'"
    )
    proc = subprocess.run(
        [PWSH, "-NoProfile", "-NonInteractive", "-Command", command],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "- [ ] first thing" in proc.stdout
    assert "- [ ] second thing" in proc.stdout
