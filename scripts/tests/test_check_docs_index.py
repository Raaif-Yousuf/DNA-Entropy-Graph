"""Tests for scripts/check_docs_index.py."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_docs_index as cdi  # noqa: E402


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_docs_index.py"), "--self-test"],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_docs_index.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "--root" in proc.stdout


def test_extract_links_ignores_external_and_anchor_only():
    text = "[a](a.md) [ext](https://example.com) [anchor](#s) [mail](mailto:x@y.com)"
    assert cdi.extract_links(text) == ["a.md"]


def test_extract_links_strips_trailing_anchor_and_title():
    text = '[a](a.md#section "A title")'
    assert cdi.extract_links(text) == ["a.md"]


def test_extract_links_handles_angle_bracket_targets():
    text = "[a](<path with spaces.md>)"
    assert cdi.extract_links(text) == ["path with spaces.md"]


def test_find_doc_files_excludes_configured_prefixes(tmp_path):
    docs = tmp_path / "docs"
    (docs / "migration").mkdir(parents=True)
    (docs / "superpowers" / "specs").mkdir(parents=True)
    (docs / "changelog.d").mkdir(parents=True)
    (docs / "kept").mkdir(parents=True)
    (docs / "migration" / "a.md").write_text("x", encoding="utf-8")
    (docs / "superpowers" / "specs" / "b.md").write_text("x", encoding="utf-8")
    (docs / "changelog.d" / "c.md").write_text("x", encoding="utf-8")
    (docs / "kept" / "d.md").write_text("x", encoding="utf-8")
    (docs / "top.md").write_text("x", encoding="utf-8")

    found = {p.as_posix() for p in cdi.find_doc_files(docs)}
    assert found == {"kept/d.md", "top.md"}


def test_check_reports_missing_index(tmp_path):
    (tmp_path / "docs").mkdir()
    problems = cdi.check(tmp_path)
    assert any("does not exist" in p for p in problems)


def test_check_against_this_repos_real_tree_runs_without_crashing():
    """Not an assertion about the CURRENT pass/fail state -- other agents
    are actively filling docs/ tonight (see this script's own report for
    the real, current result) -- only that the check runs cleanly end to
    end against real content and returns a list, never raises."""
    repo_root = SCRIPTS_DIR.parent
    problems = cdi.check(repo_root)
    assert isinstance(problems, list)
