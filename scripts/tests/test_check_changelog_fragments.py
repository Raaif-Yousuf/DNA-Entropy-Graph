"""Tests for scripts/check_changelog_fragments.py."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_changelog_fragments as ccf  # noqa: E402


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_changelog_fragments.py"), "--self-test"],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_changelog_fragments.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0


def test_find_fragments_ignores_readme(tmp_path):
    changelog_dir = tmp_path / "changelog.d"
    changelog_dir.mkdir()
    (changelog_dir / "README.md").write_text("# convention\n", encoding="utf-8")
    (changelog_dir / "a.md").write_text("- x\n", encoding="utf-8")
    found = ccf.find_fragments(changelog_dir)
    assert [p.name for p in found] == ["a.md"]


def test_validate_fragment_accepts_bullet(tmp_path):
    p = tmp_path / "a.md"
    p.write_text("- did a thing\n", encoding="utf-8")
    assert ccf.validate_fragment(p) is None


def test_validate_fragment_rejects_heading_with_hint(tmp_path):
    p = tmp_path / "a.md"
    p.write_text("## did a thing\n", encoding="utf-8")
    problem = ccf.validate_fragment(p)
    assert problem is not None
    assert "heading" in problem


def test_validate_fragment_rejects_empty(tmp_path):
    p = tmp_path / "a.md"
    p.write_text("   \n", encoding="utf-8")
    problem = ccf.validate_fragment(p)
    assert problem is not None
    assert "empty" in problem


def test_check_against_this_repos_real_tree_runs_without_crashing():
    """Not an assertion that every fragment several concurrent agents write
    tonight is well-formed -- that is exactly the kind of real finding this
    check exists to surface, not something to bake into a fixed pytest
    assertion -- only that the check runs cleanly end to end and returns a
    list. See this script's own report for the real, current result."""
    repo_root = SCRIPTS_DIR.parent
    problems = ccf.check(repo_root)
    assert isinstance(problems, list)
