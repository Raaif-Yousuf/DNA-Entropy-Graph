"""Tests for scripts/check_em_dash.py (#425)."""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_em_dash as ced  # noqa: E402

DASH = chr(0x2014)

pytestmark = pytest.mark.skipif(shutil.which("git") is None, reason="git not installed")


def _repo(tmp_path: Path, files: dict[str, str]) -> Path:
    subprocess.run(["git", "init", "-q"], cwd=tmp_path, check=True, capture_output=True)
    for rel, text in files.items():
        target = tmp_path / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8", newline="\n")
    subprocess.run(["git", "add", "-A"], cwd=tmp_path, check=True, capture_output=True)
    return tmp_path


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_em_dash.py"), "--self-test"],
        capture_output=True,
        text=True,
        timeout=60,
        check=False,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


@pytest.mark.parametrize(
    "rel",
    ["docs/user_guide/start.md", "app/Strings/en-US/Resources.resw", ".github/ISSUE_TEMPLATE/bug.md", "README.md"],
)
def test_planted_dash_in_each_watched_glob_is_named_with_file_and_line(tmp_path, rel):
    root = _repo(tmp_path, {rel: f"fine\nbad {DASH} here\n"})
    found = ced.check(root)
    assert [(f.path, f.line) for f in found] == [(rel, 2)]


def test_unwatched_path_and_hyphen_pass(tmp_path):
    root = _repo(tmp_path, {"docs/hard_rules.md": f"x {DASH} y\n", "README.md": "plain - hyphen\n"})
    assert ced.check(root) == []


def test_untracked_file_is_not_scanned_like_git_grep(tmp_path):
    root = _repo(tmp_path, {"README.md": "ok\n"})
    (root / "obj").mkdir()
    (root / "obj" / "Resources.resw").write_text(f"{DASH}\n", encoding="utf-8", newline="\n")
    assert ced.check(root) == []


def test_cli_exits_one_naming_the_file(tmp_path):
    root = _repo(tmp_path, {"README.md": f"a {DASH} b\n"})
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_em_dash.py"), "--root", str(root)],
        capture_output=True,
        text=True,
        timeout=60,
        check=False,
    )
    assert proc.returncode == 1
    assert "README.md:1" in proc.stderr


def test_this_repo_is_clean():
    assert ced.check(REPO_ROOT) == []
