"""Tests for scripts/check_repo_hygiene.py (#449). Temp repos only."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_repo_hygiene as crh  # noqa: E402


def _git(repo: Path, *args: str) -> None:
    subprocess.run(["git", *args], cwd=repo, check=True, capture_output=True)


@pytest.fixture()
def repo(tmp_path):
    _git(tmp_path, "init", "-q")
    (tmp_path / "AGENTS.md").write_text("pointer\n", encoding="utf-8", newline="\n")
    return tmp_path


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_repo_hygiene.py"), "--self-test"],
        capture_output=True, text=True, timeout=60, check=False,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_clean_repo_passes(repo):
    assert crh.check(repo) == []


@pytest.mark.parametrize("lines,flagged", [(1, False), (19, False), (20, True), (40, True)])
def test_agents_md_line_limit_matches_the_old_inline_rule(repo, lines, flagged):
    (repo / "AGENTS.md").write_text("x\n" * lines, encoding="utf-8", newline="\n")
    assert bool(crh.check(repo)) is flagged


def test_missing_agents_md_fails(repo):
    (repo / "AGENTS.md").unlink()
    assert any("missing" in p for p in crh.check(repo))


def test_tracked_donor_file_fails_but_untracked_passes(repo):
    donor = repo / "legacy" / "clair"
    donor.mkdir(parents=True)
    (donor / "x.md").write_text("private\n", encoding="utf-8", newline="\n")
    assert crh.check(repo) == []
    _git(repo, "add", "-f", "legacy/clair/x.md")
    assert any("legacy/clair/x.md" in p for p in crh.check(repo))


def test_cli_exit_codes(repo, tmp_path_factory):
    script = str(SCRIPTS_DIR / "check_repo_hygiene.py")
    ok = subprocess.run([sys.executable, script, "--root", str(repo)], capture_output=True, text=True, check=False)
    assert ok.returncode == 0
    (repo / "AGENTS.md").write_text("x\n" * 30, encoding="utf-8", newline="\n")
    bad = subprocess.run([sys.executable, script, "--root", str(repo)], capture_output=True, text=True, check=False)
    assert bad.returncode == 1 and "30 lines" in bad.stderr
    elsewhere = tmp_path_factory.mktemp("notrepo")
    usage = subprocess.run([sys.executable, script, "--root", str(elsewhere)], capture_output=True, text=True, check=False)
    assert usage.returncode == 2
