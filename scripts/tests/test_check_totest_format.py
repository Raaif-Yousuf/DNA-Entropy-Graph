"""Tests for scripts/check_totest_format.py."""

from __future__ import annotations

import subprocess
import sys
from datetime import UTC, datetime
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
    now = datetime(2026, 9, 19, tzinfo=UTC)
    closed = datetime(2026, 8, 1, tzinfo=UTC)
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


def _row(line_no, issue, do_this):
    return ctf.Row(line_no=line_no, cells=(issue, "abc1234", "installer", do_this, "ok", "not ok"))


def test_find_duplicate_rows_names_both_lines_after_whitespace_normalisation():
    rows = [_row(10, "#31", "Open  a PR\nthen merge"), _row(20, "#31", "open a pr then merge")]
    problems = ctf.find_duplicate_rows(rows)
    assert len(problems) == 1
    assert "L10" in problems[0] and "L20" in problems[0]


def test_find_duplicate_rows_allows_one_issue_with_different_checks():
    rows = [_row(10, "#31", "Open a PR"), _row(20, "#31", "Close the PR")]
    assert ctf.find_duplicate_rows(rows) == []


def test_find_duplicate_rows_allows_same_text_on_different_issues():
    rows = [_row(10, "#31", "Open a PR"), _row(20, "#32", "Open a PR")]
    assert ctf.find_duplicate_rows(rows) == []


def test_check_reports_an_appended_duplicate_row(tmp_path):
    table = ctf._GOOD_TABLE.format(sha="abc1234")
    last = table.strip().splitlines()[-1]
    (tmp_path / "docs").mkdir()
    (tmp_path / ctf.TOTEST_RELATIVE).write_text(table + last + "\n", encoding="utf-8", newline="\n")
    problems = ctf.check(tmp_path, 45)
    assert any("duplicate" in p.lower() for p in problems), problems
