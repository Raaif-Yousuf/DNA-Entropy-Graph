"""Tests for scripts/check_totest_format.py."""

from __future__ import annotations

import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_totest_format as ctf  # noqa: E402


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_totest_format.py"), "--self-test"],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero_and_documents_max_age():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_totest_format.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "--max-age-days" in proc.stdout


def test_row_age_days_pure_arithmetic():
    now = datetime(2026, 9, 19, tzinfo=timezone.utc)
    closed = datetime(2026, 8, 1, tzinfo=timezone.utc)
    assert ctf.row_age_days(closed, now) == 49


def test_is_stale_boundary():
    assert ctf.is_stale(46, 45) is True
    assert ctf.is_stale(45, 45) is False
    assert ctf.is_stale(0, 45) is False


def test_parse_rows_skips_header_separator_and_sentinel():
    text = ctf._EMPTY_TABLE
    assert ctf.parse_rows(text) == []


def test_parse_rows_reads_a_real_row():
    text = ctf._GOOD_TABLE.format(sha="deadbee")
    rows = ctf.parse_rows(text)
    assert len(rows) == 1
    assert rows[0].cells[0] == "#12"
    assert rows[0].cells[2] == "installer"


def test_validate_row_shape_wrong_column_count():
    row = ctf.Row(line_no=1, cells=("#1", "abc1234", "installer"))
    problems = ctf.validate_row_shape(row)
    assert any("column(s)" in p for p in problems)


def test_validate_row_shape_bad_needs_value():
    row = ctf.Row(line_no=1, cells=("#1", "abc1234", "bogus-need", "do it", "ok", "not ok"))
    problems = ctf.validate_row_shape(row)
    assert any("Needs" in p for p in problems)


def test_validate_row_shape_accepts_well_formed_row():
    row = ctf.Row(line_no=1, cells=("#1", "abc1234", "installer", "do it", "ok", "not ok"))
    assert ctf.validate_row_shape(row) == []


def test_extract_sha_finds_hex_token():
    assert ctf.extract_sha("abc1234 (build check)") == "abc1234"
    assert ctf.extract_sha("no sha here") is None


def test_check_against_this_repos_real_tree_runs_without_crashing():
    """Not an assertion about the current queue's contents -- ToTest.md's
    real state changes as issues close -- only that the check runs cleanly
    end to end and returns a list."""
    repo_root = SCRIPTS_DIR.parent
    problems = ctf.check(repo_root, max_age_days=ctf.DEFAULT_MAX_AGE_DAYS)
    assert isinstance(problems, list)
