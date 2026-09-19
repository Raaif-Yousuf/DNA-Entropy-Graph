"""Tests for scripts/agent_wave.ps1.

Deliberately network-free and never touches this repo's own real git state:
every -Status test below runs against a small, throwaway git repo built in
tmp_path, never `SCRIPTS_DIR.parent` (the real DNA-Entropy-Graph checkout).
`-Repo ""` is passed wherever a real `gh` call would otherwise be attempted,
matching the network-free discipline `test_sync_labels.py` and
`test_issue_precheck.py`'s `--no-gh` already established.

Requires `pwsh` (PowerShell 7+) on PATH; skipped, not failed, if absent.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
SCRIPT = SCRIPTS_DIR / "agent_wave.ps1"

PWSH = shutil.which("pwsh")
pytestmark = pytest.mark.skipif(PWSH is None, reason="pwsh (PowerShell 7+) not found on PATH")


def _run(*args: str, timeout: int = 60) -> subprocess.CompletedProcess:
    return subprocess.run(
        [PWSH, "-NoProfile", "-NonInteractive", "-File", str(SCRIPT), *args],
        capture_output=True, text=True, timeout=timeout,
    )


def _write_spec(path: Path, spec: dict) -> Path:
    path.write_text(json.dumps(spec), encoding="utf-8")
    return path


def _init_repo(root: Path) -> None:
    def run(*args):
        subprocess.run(["git", *args], cwd=root, check=True, capture_output=True, text=True)

    root.mkdir(parents=True, exist_ok=True)
    run("init", "-q")
    run("config", "user.email", "test@example.com")
    run("config", "user.name", "Test")
    (root / ".gitignore").write_text("worker/.venv/\n", encoding="utf-8")
    (root / "README.md").write_text("hello\n", encoding="utf-8")
    run("add", ".")
    run("commit", "-q", "-m", "initial")


# ---------------------------------------------------------------------------
# Self-test bridge and --help
# ---------------------------------------------------------------------------

def test_self_test_passes():
    proc = _run("-SelfTest", timeout=120)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero_and_documents_every_command():
    proc = _run("-Help")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    for flag in ("-Start", "-Status", "-Down", "-Apply", "-WhatIf", "-Isolation", "-SpecFile"):
        assert flag in proc.stdout, f"{flag} not documented in -Help output"


def test_no_command_given_is_a_usage_error():
    proc = _run()
    assert proc.returncode == 2


# ---------------------------------------------------------------------------
# -Start: spec validation, overlap refusal, dry run vs -Apply
# ---------------------------------------------------------------------------

def test_start_dry_run_on_disjoint_spec_succeeds_and_writes_nothing(tmp_path):
    spec_path = _write_spec(tmp_path / "spec.json", {
        "name": "pytest-wave-a",
        "agents": [
            {"name": "a", "issues": [1], "ownedPaths": ["scripts/**"]},
            {"name": "b", "issues": [], "ownedPaths": ["docs/**"]},
        ],
    })
    out_dir = tmp_path / "out"
    proc = _run("-Start", "-SpecFile", str(spec_path), "-OutputDir", str(out_dir))
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "no path overlaps" in proc.stdout
    assert not out_dir.exists()


def test_start_refuses_an_overlapping_spec(tmp_path):
    spec_path = _write_spec(tmp_path / "spec.json", {
        "name": "pytest-wave-overlap",
        "agents": [
            {"name": "worker-a", "issues": [], "ownedPaths": ["worker/**"]},
            {"name": "worker-b", "issues": [], "ownedPaths": ["worker/src/dna_entropy/worker/**"]},
        ],
    })
    proc = _run("-Start", "-SpecFile", str(spec_path), "-OutputDir", str(tmp_path / "out"))
    assert proc.returncode == 1
    assert "REFUSING" in proc.stdout
    assert "worker-a" in proc.stdout and "worker-b" in proc.stdout


def test_start_excluded_path_carve_out_is_not_a_false_overlap(tmp_path):
    """The real shape this repo hit: one agent owns a whole directory except
    a single file another agent owns for this round."""
    spec_path = _write_spec(tmp_path / "spec.json", {
        "name": "pytest-wave-carveout",
        "agents": [
            {"name": "scripts", "issues": [], "ownedPaths": ["scripts/**"],
             "excludedPaths": ["scripts/gen_manifest_schema.py"]},
            {"name": "worker", "issues": [], "ownedPaths": ["worker/**", "scripts/gen_manifest_schema.py"]},
        ],
    })
    proc = _run("-Start", "-SpecFile", str(spec_path), "-OutputDir", str(tmp_path / "out"))
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "no path overlaps" in proc.stdout


def test_start_apply_writes_briefs_and_reserves_basetemp_then_down_cleans_up(tmp_path):
    spec_path = _write_spec(tmp_path / "spec.json", {
        "name": "pytest-wave-apply",
        "agents": [
            {"name": "a", "issues": [7], "ownedPaths": ["scripts/**"], "notes": "do the thing"},
            {"name": "b", "issues": [], "ownedPaths": ["docs/**"]},
        ],
    })
    out_dir = tmp_path / "out"

    proc = _run("-Start", "-SpecFile", str(spec_path), "-OutputDir", str(out_dir), "-Apply")
    assert proc.returncode == 0, proc.stdout + proc.stderr

    brief_a = out_dir / "briefs" / "a-brief.md"
    brief_b = out_dir / "briefs" / "b-brief.md"
    basetemp_a = out_dir / "basetemp" / "a"
    basetemp_b = out_dir / "basetemp" / "b"
    assert brief_a.is_file()
    assert brief_b.is_file()
    assert basetemp_a.is_dir()
    assert basetemp_b.is_dir()

    text_a = brief_a.read_text(encoding="utf-8")
    assert "scripts/**" in text_a
    assert "docs/**" in text_a  # listed as forbidden to agent a
    assert "#7" in text_a
    assert "do the thing" in text_a
    assert str(basetemp_a) in text_a
    assert "You do not touch git" in text_a
    assert "fixing-a-bug" in text_a and "wired-to-nothing" in text_a
    assert "working-an-issue" in text_a and "tests-first" in text_a

    proc = _run("-Down", "-SpecFile", str(spec_path), "-OutputDir", str(out_dir), "-All")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert not brief_a.exists()
    assert not basetemp_a.exists()


def test_start_whatif_overrides_apply(tmp_path):
    spec_path = _write_spec(tmp_path / "spec.json", {
        "name": "pytest-wave-whatif",
        "agents": [{"name": "a", "issues": [], "ownedPaths": ["scripts/**"]}],
    })
    out_dir = tmp_path / "out"
    proc = _run("-Start", "-SpecFile", str(spec_path), "-OutputDir", str(out_dir), "-Apply", "-WhatIf")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert not (out_dir / "briefs").exists()


def test_start_missing_spec_file_errors():
    proc = _run("-Start", "-SpecFile", "definitely-does-not-exist.json")
    assert proc.returncode == 1


def test_start_malformed_spec_errors(tmp_path):
    bad = tmp_path / "bad.json"
    bad.write_text("not json at all {{{", encoding="utf-8")
    proc = _run("-Start", "-SpecFile", str(bad))
    assert proc.returncode == 1


# ---------------------------------------------------------------------------
# -Status: the recovery report, against a throwaway repo (never this one)
# ---------------------------------------------------------------------------

def test_status_buckets_uncommitted_files_by_owner_and_flags_unclaimed(tmp_path):
    repo = tmp_path / "repo"
    _init_repo(repo)

    (repo / "scripts").mkdir()
    (repo / "scripts" / "new_tool.py").write_text("# new\n", encoding="utf-8")
    (repo / "docs").mkdir()
    (repo / "docs" / "notes.md").write_text("notes\n", encoding="utf-8")
    (repo / "random_top_level_file.txt").write_text("nobody owns this\n", encoding="utf-8")

    spec_path = _write_spec(tmp_path / "spec.json", {
        "name": "pytest-wave-status",
        "agents": [
            {"name": "scripts", "issues": [], "ownedPaths": ["scripts/**"]},
            {"name": "docs", "issues": [], "ownedPaths": ["docs/**"]},
        ],
    })

    proc = _run("-Status", "-SpecFile", str(spec_path), "-RepoRoot", str(repo), "-Repo", "")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "scripts/new_tool.py" in proc.stdout
    assert "docs/notes.md" in proc.stdout
    assert "UNCLAIMED" in proc.stdout
    assert "random_top_level_file.txt" in proc.stdout


def test_status_reports_no_unclaimed_when_everything_is_owned(tmp_path):
    repo = tmp_path / "repo"
    _init_repo(repo)
    (repo / "scripts").mkdir()
    (repo / "scripts" / "a.py").write_text("# a\n", encoding="utf-8")

    spec_path = _write_spec(tmp_path / "spec.json", {
        "name": "pytest-wave-status-clean",
        "agents": [{"name": "scripts", "issues": [], "ownedPaths": ["scripts/**"]}],
    })

    proc = _run("-Status", "-SpecFile", str(spec_path), "-RepoRoot", str(repo), "-Repo", "")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "UNCLAIMED: none" in proc.stdout
