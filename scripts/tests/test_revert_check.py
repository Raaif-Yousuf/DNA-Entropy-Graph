"""Tests for scripts/revert_check.py (#1075): a per-worktree revert-check with no shared path."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
SCRIPT = SCRIPTS_DIR / "revert_check.py"

# A test command that is RED on the committed bytes (VALUE == 1) and GREEN on the fix (VALUE == 2).
CHECK_VALUE = ["-B", "-c", "import sys, src; sys.exit(0 if src.VALUE == 2 else 1)"]
CRASH = ["-B", "-c", "raise SystemExit(70)"]
ALWAYS_GREEN = ["-B", "-c", "raise SystemExit(0)"]


def _git(root: Path, *args: str) -> str:
    proc = subprocess.run(["git", *args], cwd=root, check=True, capture_output=True)
    return proc.stdout.decode("utf-8")


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    _git(tmp_path, "init", "-q", "-b", "main")
    _git(tmp_path, "config", "user.email", "t@example.com")
    _git(tmp_path, "config", "user.name", "T")
    _git(tmp_path, "config", "core.autocrlf", "false")  # the real repo is an LF tree; keep byte asserts exact
    (tmp_path / "src.py").write_bytes(b"VALUE = 1\n")
    (tmp_path / "other.py").write_bytes(b"OTHER = 1\n")
    _git(tmp_path, "add", "-A")
    _git(tmp_path, "commit", "-q", "-m", "base")
    return tmp_path


def _run(root: Path, files: list[str], test_cmd: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(SCRIPT), *files, "--", sys.executable, *test_cmd],
        cwd=root,
        capture_output=True,
        text=True,
        timeout=60,
        check=False,
    )


def _diff(root: Path, *files: str) -> bytes:
    return subprocess.run(["git", "diff", "--binary", "--", *files], cwd=root, check=True, capture_output=True).stdout


def test_red_path_exits_zero_and_restores_the_fix_exactly(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    before = _diff(repo, "src.py")
    proc = _run(repo, ["src.py"], CHECK_VALUE)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert (repo / "src.py").read_bytes() == b"VALUE = 2\n"
    assert _diff(repo, "src.py") == before
    assert "RED" in proc.stdout


def test_green_path_means_fix_not_proven_and_still_restores(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    before = _diff(repo, "src.py")
    proc = _run(repo, ["src.py"], ALWAYS_GREEN)
    assert proc.returncode == 10, proc.stdout + proc.stderr
    assert "not proven" in proc.stderr
    assert _diff(repo, "src.py") == before


def test_crashing_test_command_still_restores(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    before = _diff(repo, "src.py")
    proc = subprocess.run(
        [sys.executable, str(SCRIPT), "src.py", "--", "this-command-does-not-exist-xyz"],
        cwd=repo,
        capture_output=True,
        text=True,
        timeout=60,
        check=False,
    )
    assert proc.returncode not in (0, 10)
    assert (repo / "src.py").read_bytes() == b"VALUE = 2\n"
    assert _diff(repo, "src.py") == before


def test_nonzero_crash_exit_counts_as_red(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    proc = _run(repo, ["src.py"], CRASH)
    assert proc.returncode == 0
    assert (repo / "src.py").read_bytes() == b"VALUE = 2\n"


def test_only_named_files_are_reverted(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    (repo / "other.py").write_bytes(b"OTHER = 99\n")
    other_before = _diff(repo, "other.py")
    seen = ["-B", "-c", "import sys; sys.exit(0 if open('other.py').read() == 'OTHER = 99\\n' else 1)"]
    proc = _run(repo, ["src.py"], seen)
    # other.py was never reverted, so the command saw it intact and returned 0: tests "stayed green".
    assert proc.returncode == 10, proc.stdout + proc.stderr
    assert _diff(repo, "other.py") == other_before


def test_patch_is_stored_under_the_worktrees_own_git_dir(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    proc = _run(repo, ["src.py"], CHECK_VALUE)
    assert proc.returncode == 0
    git_dir = Path(_git(repo, "rev-parse", "--absolute-git-dir").strip())
    patches = list((git_dir / "revert-check").glob("*.patch"))
    assert len(patches) == 1
    assert str(git_dir / "revert-check") in proc.stdout
    assert patches[0].read_bytes() == _diff(repo, "src.py")


def test_two_runs_never_share_a_patch_path(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    assert _run(repo, ["src.py"], CHECK_VALUE).returncode == 0
    assert _run(repo, ["src.py"], CHECK_VALUE).returncode == 0
    git_dir = Path(_git(repo, "rev-parse", "--absolute-git-dir").strip())
    assert len(list((git_dir / "revert-check").glob("*.patch"))) == 2


def test_no_diff_in_named_files_is_its_own_code(repo: Path):
    proc = _run(repo, ["src.py"], CHECK_VALUE)
    assert proc.returncode == 11, proc.stdout + proc.stderr
    assert "no diff" in proc.stderr


def test_untracked_source_file_is_refused_with_a_clear_message(repo: Path):
    (repo / "new_src.py").write_bytes(b"VALUE = 2\n")
    proc = _run(repo, ["new_src.py"], ALWAYS_GREEN)
    assert proc.returncode == 13, proc.stdout + proc.stderr
    assert "untracked" in proc.stderr
    assert (repo / "new_src.py").read_bytes() == b"VALUE = 2\n"


def test_binary_safe_restore(repo: Path):
    blob = bytes(range(256)) + b"\r\n\x00\xff"
    (repo / "blob.bin").write_bytes(b"\x00\x01")
    _git(repo, "add", "blob.bin")
    _git(repo, "commit", "-q", "-m", "blob")
    (repo / "blob.bin").write_bytes(blob)
    proc = _run(repo, ["blob.bin"], CRASH)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert (repo / "blob.bin").read_bytes() == blob


def test_restore_mismatch_is_loud_and_keeps_the_patch_path(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    # The test command itself edits the reverted file, so the restore cannot reproduce the diff.
    tamper = ["-B", "-c", "open('src.py', 'w').write('VALUE = 3\\n'); raise SystemExit(1)"]
    proc = _run(repo, ["src.py"], tamper)
    assert proc.returncode == 12, proc.stdout + proc.stderr
    assert "restore" in proc.stderr
    assert "revert-check" in proc.stderr
    assert ".patch" in proc.stderr


def test_usage_errors(repo: Path):
    no_sep = subprocess.run([sys.executable, str(SCRIPT), "src.py"], cwd=repo, capture_output=True, text=True, check=False)
    assert no_sep.returncode == 2
    no_files = subprocess.run([sys.executable, str(SCRIPT), "--", "x"], cwd=repo, capture_output=True, text=True, check=False)
    assert no_files.returncode == 2
    no_cmd = subprocess.run([sys.executable, str(SCRIPT), "src.py", "--"], cwd=repo, capture_output=True, text=True, check=False)
    assert no_cmd.returncode == 2
    dot = subprocess.run([sys.executable, str(SCRIPT), ".", "--", "x"], cwd=repo, capture_output=True, text=True, check=False)
    assert dot.returncode == 2
    assert "never" in dot.stderr
