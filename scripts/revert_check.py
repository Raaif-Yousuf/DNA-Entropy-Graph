"""scripts/revert_check.py -- prove a fix with its tests RED, then restore it exactly (#1075).

    python scripts/revert_check.py <src files...> -- <test command...>
    python scripts/revert_check.py app/src/X/Y.cs -- dotnet test app/tests/Foo.Tests --filter Bar

The revert-check step of `fixing-a-bug`: undo the fix, run the new tests, they must fail,
restore. The manual recipe wrote the patch to a path shared by every worker of a wave and
`git checkout -- .` wiped the tree; one worker lost its fix (MEASURED 2026-10-31). This script
has no shared path:

  1. saves `git diff HEAD --binary -- <src files>` as bytes under THIS worktree's own git dir
     (`<git-dir>/revert-check/<timestamp>-<pid>.patch`; `git rev-parse --absolute-git-dir` is
     unique per worktree),
  2. `git checkout HEAD -- <src files>` (ONLY the named files, never `.`),
  3. runs the test command as given (no shell) and expects a NON-ZERO exit (RED),
  4. ALWAYS restores with `git apply` (a finally block, so a crash or Ctrl-C still restores),
  5. verifies `git diff HEAD --binary -- <src files>` is byte-identical to step 1 and says so
     loudly, with the patch path, if not.

Name SOURCE files only. A new untracked source file is refused (exit 13): `git checkout` cannot
revert a file git does not track, so delete or `git add -N` it yourself first. Staged edits are
restored as unstaged edits (the byte-for-byte working diff against HEAD is what is verified).

Exit codes:
   0  tests went RED and the restore is exact (the fix is proven)
   2  usage error (missing `--`, no files, no command, or a directory such as `.`)
  10  tests stayed GREEN with the fix reverted (the fix is not proven); restore still done
  11  no diff in the named files (nothing to revert)
  12  restore mismatch or restore failed (the patch path is in the message)
  13  a named file is untracked, or git itself failed
  14  the test command could not be started; restore still done
"""

from __future__ import annotations

import os
import subprocess
import sys
import time
from pathlib import Path

EXIT_OK = 0
EXIT_USAGE = 2
EXIT_GREEN = 10
EXIT_NO_DIFF = 11
EXIT_RESTORE = 12
EXIT_GIT = 13
EXIT_CANNOT_RUN = 14


class GitFailed(Exception):
    pass


def _git(root: Path, *args: str, stdin: bytes | None = None) -> bytes:
    proc = subprocess.run(["git", *args], cwd=root, input=stdin, capture_output=True, check=False)
    if proc.returncode != 0:
        detail = proc.stderr.decode("utf-8", errors="replace").strip()
        raise GitFailed(f"git {' '.join(args)} failed: {detail}")
    return proc.stdout


def split_argv(argv: list[str]) -> tuple[list[str], list[str]] | None:
    """(files, test command) around the first `--`, or None when the shape is wrong."""
    if "--" not in argv:
        return None
    index = argv.index("--")
    files, command = argv[:index], argv[index + 1 :]
    if not files or not command:
        return None
    return files, command


def _working_diff(root: Path, files: list[str]) -> bytes:
    return _git(root, "diff", "HEAD", "--binary", "--", *files)


def _restore_message(patch: Path, why: str) -> str:
    return f'ERROR: restore {why}. Your fix is saved byte-for-byte in {patch}; recover it with: git apply "{patch}"'


def run(argv: list[str], cwd: Path | None = None) -> int:
    parsed = split_argv(argv)
    if parsed is None:
        print("ERROR: usage: revert_check.py <src files...> -- <test command...>", file=sys.stderr)
        return EXIT_USAGE
    raw_files, command = parsed
    here = (cwd or Path.cwd()).resolve()

    try:
        root = Path(_git(here, "rev-parse", "--show-toplevel").decode("utf-8").strip()).resolve()
        git_dir = Path(_git(root, "rev-parse", "--absolute-git-dir").decode("utf-8").strip())
    except GitFailed as exc:
        print(f"ERROR: not inside a git worktree: {exc}", file=sys.stderr)
        return EXIT_GIT

    files: list[str] = []
    for raw in raw_files:
        resolved = (here / raw).resolve()
        if resolved == root or not resolved.is_relative_to(root):
            print(
                f"ERROR: {raw!r} is the repo root or outside it; name individual source files, "
                "never '.' (a checkout of the whole tree is how a fix gets lost)",
                file=sys.stderr,
            )
            return EXIT_USAGE
        files.append(resolved.relative_to(root).as_posix())

    try:
        for rel in files:
            if not _git(root, "ls-files", "--", rel).strip():
                print(
                    f"ERROR: {rel} is untracked: git checkout cannot revert a file git does not track. "
                    "Delete it yourself for the RED run, or `git add -N` it first.",
                    file=sys.stderr,
                )
                return EXIT_GIT
        before = _working_diff(root, files)
    except GitFailed as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return EXIT_GIT
    if not before:
        print(f"ERROR: no diff in {', '.join(files)} against HEAD: nothing to revert-check", file=sys.stderr)
        return EXIT_NO_DIFF

    patch_dir = git_dir / "revert-check"
    patch_dir.mkdir(parents=True, exist_ok=True)
    patch = patch_dir / f"{time.strftime('%Y%m%d-%H%M%S')}-{os.getpid()}.patch"
    patch.write_bytes(before)
    print(f"revert_check: fix saved to {patch}")

    result_code = EXIT_OK
    reverted = False
    try:
        _git(root, "checkout", "HEAD", "--", *files)
        reverted = True
        print(f"revert_check: reverted {', '.join(files)}; running: {' '.join(command)}")
        try:
            tests_exit: int | None = subprocess.run(command, cwd=here, check=False).returncode
        except OSError as exc:
            print(f"ERROR: could not start the test command: {exc}", file=sys.stderr)
            tests_exit = None
            result_code = EXIT_CANNOT_RUN
        if tests_exit == 0:
            result_code = EXIT_GREEN
        elif tests_exit is not None:
            print(f"revert_check: RED (test command exited {tests_exit}) with the fix reverted")
    except GitFailed as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        result_code = EXIT_GIT
    finally:
        if reverted or _working_diff_safe(root, files) != before:
            try:
                _git(root, "apply", "--whitespace=nowarn", str(patch))
            except GitFailed as exc:
                print(_restore_message(patch, f"failed ({exc})"), file=sys.stderr)
                result_code = EXIT_RESTORE
            else:
                try:
                    after = _working_diff(root, files)
                except GitFailed:
                    after = b""
                if after != before:
                    print(_restore_message(patch, "mismatch: the working diff is not byte-identical to the saved one"), file=sys.stderr)
                    result_code = EXIT_RESTORE
                else:
                    print("revert_check: restore verified byte-identical")

    if result_code == EXIT_GREEN:
        print(
            "ERROR: tests stayed GREEN with the fix reverted: the fix is not proven (the tests cannot see the difference). Fix restored.",
            file=sys.stderr,
        )
    return result_code


def _working_diff_safe(root: Path, files: list[str]) -> bytes | None:
    try:
        return _working_diff(root, files)
    except GitFailed:
        return None


def main() -> int:
    return run(sys.argv[1:])


if __name__ == "__main__":
    sys.exit(main())
