"""Tests for scripts/check_third_party_notices.py."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_third_party_notices as ctpn  # noqa: E402


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_third_party_notices.py"), "--self-test"],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_third_party_notices.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0


def test_check_notices_missing_gives_clean_notice(tmp_path):
    problems, notices = ctpn.check(tmp_path)
    assert problems == []
    assert any(f"#{ctpn.NOTICES_ISSUE}" in n for n in notices)


def test_check_notices_present_generator_missing_gives_clean_notice(tmp_path):
    (tmp_path / ctpn.NOTICES_RELATIVE).write_text("# notices\n", encoding="utf-8")
    problems, notices = ctpn.check(tmp_path)
    assert problems == []
    assert notices


def test_check_both_present_reports_unimplemented_diff(tmp_path):
    (tmp_path / ctpn.NOTICES_RELATIVE).write_text("# notices\n", encoding="utf-8")
    (tmp_path / "scripts").mkdir(parents=True, exist_ok=True)
    (tmp_path / ctpn.GENERATOR_RELATIVE).write_text("# placeholder\n", encoding="utf-8")
    problems, _notices = ctpn.check(tmp_path)
    assert problems  # never silently claims a check it cannot yet perform


def test_check_against_this_repos_real_tree_notices_missing_file():
    """THIRD-PARTY-NOTICES.md genuinely does not exist yet as of this
    session; issue #34 (its generator + staleness check) is a separate,
    larger task nobody is doing tonight."""
    repo_root = SCRIPTS_DIR.parent
    problems, notices = ctpn.check(repo_root)
    assert problems == []
    assert any(f"#{ctpn.NOTICES_ISSUE}" in n for n in notices)
