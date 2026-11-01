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
CRASH = ["-B", "-c", "import sys\nprint('boom-marker', file=sys.stderr)\nsys.exit(70)"]
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
    assert proc.returncode == 14, proc.stdout + proc.stderr
    assert "could not start" in proc.stderr
    assert "restore verified byte-identical" in proc.stdout, "the restore must have run, not merely left the file as it was"
    assert (repo / "src.py").read_bytes() == b"VALUE = 2\n"
    assert _diff(repo, "src.py") == before


def test_nonzero_exit_counts_as_red_and_the_output_tail_is_shown(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    proc = _run(repo, ["src.py"], CRASH)
    assert proc.returncode == 0
    assert "boom-marker" in proc.stdout + proc.stderr, "the caller must be able to see WHY it was red (a build failure looks the same as RED)"
    assert (repo / "src.py").read_bytes() == b"VALUE = 2\n"


def test_output_tail_is_limited_to_the_last_lines(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    chatty = ["-B", "-c", "import sys\nfor i in range(200): print('line-%03d' % i)\nsys.exit(1)"]
    proc = _run(repo, ["src.py"], chatty)
    assert proc.returncode == 0
    assert "line-199" in proc.stdout
    assert "line-100" not in proc.stdout


def _commit_pytest_file(repo: Path) -> None:
    (repo / "test_src.py").write_bytes(b"import src\n\n\ndef test_value():\n    assert src.VALUE == 2\n")
    _git(repo, "add", "test_src.py")
    _git(repo, "commit", "-q", "-m", "test")


def test_a_real_pytest_failure_is_red(repo: Path):
    _commit_pytest_file(repo)
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    proc = _run(repo, ["src.py"], ["-B", "-m", "pytest", "-q", "-p", "no:cacheprovider", "test_src.py"])
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert (repo / "src.py").read_bytes() == b"VALUE = 2\n"


@pytest.mark.parametrize(
    "pytest_args",
    [
        ["-k", "nomatchxyz"],  # exit 5: nothing selected
        ["--no-such-flag"],  # exit 4: usage error
        ["does_not_exist.py"],  # exit 4: file not found
    ],
)
def test_pytest_usage_collection_or_no_tests_exits_never_prove_a_fix(repo: Path, pytest_args: list[str]):
    _commit_pytest_file(repo)
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    proc = _run(repo, ["src.py"], ["-B", "-m", "pytest", "-q", "-p", "no:cacheprovider", *pytest_args])
    assert proc.returncode == 15, proc.stdout + proc.stderr
    assert "not RED" in proc.stderr
    assert (repo / "src.py").read_bytes() == b"VALUE = 2\n"


def test_pytest_collection_error_never_proves_a_fix(repo: Path):
    _commit_pytest_file(repo)
    (repo / "test_broken.py").write_bytes(b"import module_that_does_not_exist\n")
    _git(repo, "add", "test_broken.py")
    _git(repo, "commit", "-q", "-m", "broken")
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    proc = _run(repo, ["src.py"], ["-B", "-m", "pytest", "-q", "-p", "no:cacheprovider", "test_broken.py"])
    assert proc.returncode == 15, proc.stdout + proc.stderr


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
    assert "not in HEAD" in proc.stderr
    assert "git add -N" not in proc.stderr, "that advice does not work: checkout of a path HEAD lacks fails"
    assert "Commit it first" in proc.stderr
    assert (repo / "new_src.py").read_bytes() == b"VALUE = 2\n"


def test_a_file_only_added_to_the_index_is_refused_up_front(repo: Path):
    (repo / "new_src.py").write_bytes(b"VALUE = 2\n")
    _git(repo, "add", "new_src.py")
    proc = _run(repo, ["new_src.py"], ALWAYS_GREEN)
    assert proc.returncode == 13, proc.stdout + proc.stderr
    assert "not in HEAD" in proc.stderr
    assert (repo / "new_src.py").read_bytes() == b"VALUE = 2\n"
    assert "revert-check" not in proc.stdout, "refused before anything was saved or reverted"


def test_a_directory_argument_is_refused_not_expanded(repo: Path):
    (repo / "pkg").mkdir()
    (repo / "pkg" / "a.py").write_bytes(b"A = 1\n")
    _git(repo, "add", "pkg")
    _git(repo, "commit", "-q", "-m", "pkg")
    (repo / "pkg" / "a.py").write_bytes(b"A = 2\n")
    proc = _run(repo, ["pkg"], ALWAYS_GREEN)
    assert proc.returncode == 2, proc.stdout + proc.stderr
    assert "directory" in proc.stderr
    assert (repo / "pkg" / "a.py").read_bytes() == b"A = 2\n"


def test_binary_safe_restore(repo: Path):
    blob = bytes(range(256)) + b"\r\n\x00\xff"
    (repo / "blob.bin").write_bytes(b"\x00\x01")
    _git(repo, "add", "blob.bin")
    _git(repo, "commit", "-q", "-m", "blob")
    (repo / "blob.bin").write_bytes(blob)
    proc = _run(repo, ["blob.bin"], CRASH)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert (repo / "blob.bin").read_bytes() == blob


def test_a_test_command_that_touches_a_named_file_cannot_break_the_restore(repo: Path):
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    # A formatter or the test itself rewrites the reverted file: the restore re-checks it out before applying the patch.
    tamper = ["-B", "-c", "open('src.py', 'w').write('VALUE = 3\\n'); raise SystemExit(1)"]
    proc = _run(repo, ["src.py"], tamper)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert (repo / "src.py").read_bytes() == b"VALUE = 2\n"


def test_a_failed_restore_is_loud_and_the_printed_recovery_commands_work(
    repo: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
):
    import shlex

    sys.path.insert(0, str(SCRIPTS_DIR))
    import revert_check as rc

    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    real_git = rc._git
    failed = {"done": False}

    def flaky_git(root: Path, *args: str, **kwargs: bytes | None) -> bytes:
        if args and args[0] == "apply" and not failed["done"]:
            failed["done"] = True
            raise rc.GitFailed("simulated apply failure")
        return real_git(root, *args, **kwargs)

    monkeypatch.setattr(rc, "_git", flaky_git)
    code = rc.run(["src.py", "--", sys.executable, "-B", "-c", "raise SystemExit(1)"], cwd=repo)
    err = capsys.readouterr().err

    assert code == 12
    assert (repo / "src.py").read_bytes() == b"VALUE = 1\n", "the apply failed, so the file is still the committed one"
    assert ".patch" in err
    recovery = [line.strip() for line in err.splitlines() if line.strip().startswith("git ")]
    assert len(recovery) == 2, err
    for command in recovery:
        subprocess.run(shlex.split(command), cwd=repo, check=True, capture_output=True)
    assert (repo / "src.py").read_bytes() == b"VALUE = 2\n", "the printed recovery must really bring the fix back"


def test_user_git_config_cannot_change_the_saved_patch(repo: Path):
    _git(repo, "config", "diff.noprefix", "true")
    _git(repo, "config", "color.ui", "always")
    _git(repo, "config", "diff.mnemonicPrefix", "true")
    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    proc = _run(repo, ["src.py"], CHECK_VALUE)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    git_dir = Path(_git(repo, "rev-parse", "--absolute-git-dir").strip())
    patch = next((git_dir / "revert-check").glob("*.patch")).read_bytes()
    assert b"\x1b[" not in patch
    assert patch.startswith(b"diff --git a/src.py b/src.py")


def test_crlf_working_file_under_autocrlf_true_restores_byte_for_byte(repo: Path):
    _git(repo, "config", "core.autocrlf", "true")
    (repo / "crlf.py").write_bytes(b"A = 1\r\nB = 1\r\n")
    _git(repo, "add", "crlf.py")
    _git(repo, "commit", "-q", "-m", "crlf")
    fixed = b"A = 2\r\nB = 1\r\n"
    (repo / "crlf.py").write_bytes(fixed)
    proc = _run(repo, ["crlf.py"], CRASH)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert (repo / "crlf.py").read_bytes() == fixed
    assert "byte-identical" in proc.stdout


def test_a_file_whose_bytes_differ_after_restore_fails_even_when_the_diff_text_matches(
    repo: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
):
    sys.path.insert(0, str(SCRIPTS_DIR))
    import revert_check as rc

    (repo / "src.py").write_bytes(b"VALUE = 2\n")
    real_git = rc._git

    def lossy_git(root: Path, *args: str, **kwargs: bytes | None) -> bytes:
        out = real_git(root, *args, **kwargs)
        if args and args[0] == "apply":
            (root / "src.py").write_bytes(b"VALUE = 2\r\n")  # same diff to the eye of a line-ending-blind compare, different bytes
        return out

    monkeypatch.setattr(rc, "_git", lossy_git)
    real_diff = rc._working_diff
    remembered: dict[str, bytes] = {}

    def blind_diff(root: Path, files: list[str]) -> bytes:
        # Always answers with the first diff it saw: a line-ending-blind compare that cannot tell the bytes changed.
        return remembered.setdefault("first", real_diff(root, files))

    monkeypatch.setattr(rc, "_working_diff", blind_diff)
    code = rc.run(["src.py", "--", sys.executable, "-B", "-c", "raise SystemExit(1)"], cwd=repo)

    assert code == 12
    assert "file bytes differ" in capsys.readouterr().err


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
