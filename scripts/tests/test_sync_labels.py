"""Tests for scripts/sync_labels.ps1.

Deliberately network-free, same discipline as scripts/tests/test_issue_precheck.py's
`--no-gh` tests: nothing here calls `gh api` against the real GitHub repo, so
nothing here is flaky over network/auth or able to mutate real labels. The
PowerShell script's own `-SelfTest` (parser + diff logic, embedded fixtures)
and `-ParseOnly` (real .github/labels.yml, no `gh` call) are what make that
possible; see their own `.PARAMETER` help text in the script.

Requires `pwsh` (PowerShell 7+) on PATH. Skipped, not failed, if it is not
found -- this repo's CI and dev machines are Windows with pwsh, but a
contributor's shell should not fail the whole suite over a missing binary
this test happens to need.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
SCRIPT = SCRIPTS_DIR / "sync_labels.ps1"

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


def test_help_exits_zero_and_documents_every_switch():
    proc = _run("-Help")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    for flag in ("-Apply", "-Prune", "-Force", "-WhatIf", "-SelfTest", "-Repo"):
        assert flag in proc.stdout, f"{flag} not documented in -Help output"


def test_parse_only_reads_the_real_labels_yaml_cleanly():
    """The real .github/labels.yml -- 22 labels, source of truth for this
    repo -- must itself parse without error, with no `gh` call at all."""
    proc = _run("-ParseOnly")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    labels = json.loads(proc.stdout)
    assert len(labels) == 22
    names = {label["Name"] for label in labels}
    for expected in ("P0", "P1", "P2", "P3", "area:app", "area:worker", "area:docs",
                      "DECISION", "owner", "spike", "needs-criteria", "post-v1"):
        assert expected in names
    for label in labels:
        assert label["Color"], f"{label['Name']} has no color"
        assert label["Description"], f"{label['Name']} has no description"


def test_parse_only_labels_use_six_digit_hex_colors():
    proc = _run("-ParseOnly")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    labels = json.loads(proc.stdout)
    for label in labels:
        color = label["Color"]
        assert len(color) == 6, f"{label['Name']}: color {color!r} is not 6 hex digits"
        int(color, 16)  # raises ValueError if not valid hex


def test_malformed_labels_file_exits_nonzero_without_any_network_call():
    """A label entry missing color/description must be refused before this
    script ever tries to reach GitHub -- the parse step runs before the API
    call, so this is exercisable with a bogus --Repo and no network."""
    tmp_dir = REPO_ROOT / ".scratch-test-sync-labels"
    tmp_dir.mkdir(exist_ok=True)
    bad_file = tmp_dir / "bad-labels.yml"
    try:
        bad_file.write_text("- name: only-a-name\n", encoding="utf-8")
        proc = _run("-LabelsFile", str(bad_file), "-Repo", "nobody/does-not-exist")
        assert proc.returncode == 1
        combined = proc.stdout + proc.stderr
        assert "malformed entry" in combined
        # Proof no network call happened: a real gh 404 has a distinct message
        # ("gh api failed listing labels"); a malformed-file failure must
        # never get that far.
        assert "gh api failed listing labels" not in combined
    finally:
        bad_file.unlink(missing_ok=True)
        tmp_dir.rmdir()


def test_duplicate_label_name_is_rejected():
    tmp_dir = REPO_ROOT / ".scratch-test-sync-labels-dup"
    tmp_dir.mkdir(exist_ok=True)
    dup_file = tmp_dir / "dup-labels.yml"
    try:
        dup_file.write_text(
            "- name: P0\n  color: \"111111\"\n  description: one\n"
            "- name: P0\n  color: \"222222\"\n  description: two\n",
            encoding="utf-8",
        )
        proc = _run("-LabelsFile", str(dup_file))
        assert proc.returncode == 1
        assert "duplicate" in (proc.stdout + proc.stderr).lower()
    finally:
        dup_file.unlink(missing_ok=True)
        tmp_dir.rmdir()
