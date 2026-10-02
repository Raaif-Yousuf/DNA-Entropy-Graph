"""Tests for scripts/check_write_newline.py (Hard Rule 5, #444)."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_write_newline as cwn  # noqa: E402


def _scan(source: str):
    return cwn.scan_source(source, "x.py")


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_write_newline.py"), "--self-test"],
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_write_text_without_newline_is_flagged_with_line():
    found = _scan('x = 1\np.write_text("a", encoding="utf-8")\n')
    assert [f.line for f in found] == [2]


def test_write_text_with_newline_passes():
    assert not _scan('p.write_text("a", encoding="utf-8", newline="\\n")\n')


def test_blob_store_style_two_positional_write_text_passes():
    assert not _scan("store.write_text(path, text)\n")


def test_text_open_without_newline_is_flagged():
    assert _scan('open(p, "w", encoding="utf-8")\n')
    assert _scan('with open(p, mode="a") as fh:\n    pass\n')
    assert _scan('p.open("w")\n')


def test_open_binary_read_and_newline_pass():
    assert not _scan('open(p, "wb")\n')
    assert not _scan('open(p, "r")\n')
    assert not _scan('open(p, "w", newline="\\n")\n')
    assert not _scan("open(p, mode)\n")


def test_exempt_marker_and_fixture_functions_pass():
    assert not _scan('p.write_text("a")  # newline-ok: CRLF wanted\n')
    assert not _scan('def self_test():\n    p.write_text("a")\n')
    assert _scan('def run():\n    p.write_text("a")\n')


def test_tests_directory_is_not_scanned(tmp_path):
    (tmp_path / "scripts" / "tests").mkdir(parents=True)
    (tmp_path / "scripts" / "tests" / "t.py").write_text("p.write_text('a')\n", encoding="utf-8", newline="\n")
    assert cwn.check(tmp_path) == []


def test_cli_exits_nonzero_naming_file_and_line(tmp_path):
    (tmp_path / "scripts").mkdir()
    (tmp_path / "scripts" / "bad.py").write_text("def f(p):\n    p.write_text('a')\n", encoding="utf-8", newline="\n")
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_write_newline.py"), "--root", str(tmp_path)],
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert proc.returncode == 1
    assert "scripts/bad.py:2" in proc.stderr


def test_this_repo_is_clean():
    assert cwn.check(REPO_ROOT) == []
