"""scripts/revert_check.py -- prove a fix with its tests RED, then restore it exactly (#1075).

    python scripts/revert_check.py <src files...> -- <test command...>
    python scripts/revert_check.py app/src/X/Y.cs -- dotnet test app/tests/Foo.Tests --filter Bar

The revert-check step of `fixing-a-bug`: undo the fix, run the new tests, they must fail,
restore. The manual recipe wrote the patch to a path shared by every worker of a wave and
`git checkout -- .` wiped the tree; one worker lost its fix (MEASURED 2026-10-31). This script
has no shared path:

  1. saves `git diff HEAD --binary -- <src files>` as bytes under THIS worktree's own git dir
     (`<git-dir>/revert-check/<timestamp>-<pid>.patch`; `git rev-parse --absolute-git-dir` is
     unique per worktree), with the user's git config (colour, prefixes, external diff, textconv)
     neutralised so the patch is always a plain `a/` `b/` patch `git apply` accepts,
  2. `git checkout HEAD -- <src files>` (ONLY the named files, never `.`),
  3. runs the test command as given (no shell) and expects a NON-ZERO exit (RED),
  4. ALWAYS restores (a finally block, so a crash still restores): `git checkout HEAD -- <src
     files>` again (so a test command or formatter that touched a named file cannot make the
     restore fail), then `git apply` of the saved patch,
  5. verifies the restore twice: `git diff HEAD --binary` of the named files is identical to step 1
     AND the SHA-256 of each working file's raw bytes equals what it was before (under
     `core.autocrlf` or eol attributes the diff can match while the bytes differ); a failure prints
     the patch path and two commands that bring the fix back.

What "RED" means here: any non-zero exit from the test command. That is a weak signal, so the last
20 lines of its output are always printed: read them. A BUILD failure (dotnet, tsc) is
indistinguishable from a failing assertion and still counts as RED. When the command is pytest,
exit codes 2 (interrupted or collection error), 3 (internal error), 4 (usage error) and 5 (no tests
collected, for example a mistyped `-k`) are refused with exit 15: they prove nothing about the fix.

Name individual tracked SOURCE files only:
  - a directory (including `.` or `app/src`) is refused, never expanded (exit 2);
  - a file absent from HEAD (untracked, or only added to the index) is refused up front (exit 13):
    `git checkout HEAD -- <path>` fails for a path HEAD lacks. Commit it first or leave it out;
  - staged edits are restored as unstaged edits (the index is not restored).

Known limits (L1, L3, L4): a hard kill (power loss, `taskkill /F`) mid-run leaves the named files at
HEAD; the patch path printed at the start is the recovery (`git apply <patch>`). Old patches under
`<git-dir>/revert-check/` accumulate; they are tiny and live in the git dir, so nothing tracks them.

Exit codes:
   0  tests went RED and the restore is exact (the fix is proven)
   2  usage error (missing `--`, no files, no command, or a directory argument)
  10  tests stayed GREEN with the fix reverted (the fix is not proven); restore still done
  11  no diff in the named files (nothing to revert)
  12  restore mismatch or restore failed (the patch path and recovery commands are in the message)
  13  a named file is not in HEAD, or git itself failed
  14  the test command could not be started; restore still done
  15  the command is pytest and exited 2, 3, 4 or 5: not a failing assertion; restore still done
"""

from __future__ import annotations

import hashlib
import os
import shlex
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
EXIT_NOT_A_TEST_FAILURE = 15

TAIL_LINES = 20
PYTEST_NOT_A_FAILURE = {2, 3, 4, 5}

# The saved patch must not depend on the user's git config.
DIFF_FLAGS = ["--binary", "--no-color", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/"]


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


def is_pytest(command: list[str]) -> bool:
    """True for `pytest ...`, `python -m pytest ...`, `uv run pytest ...`, `heavy.py ... -- python -m pytest ...`."""
    return any(Path(part).stem.lower() in ("pytest", "py.test") for part in command)


def _working_diff(root: Path, files: list[str]) -> bytes:
    return _git(root, "diff", "HEAD", *DIFF_FLAGS, "--", *files)


def _working_diff_safe(root: Path, files: list[str]) -> bytes | None:
    try:
        return _working_diff(root, files)
    except GitFailed:
        return None


def _file_hashes(root: Path, files: list[str]) -> dict[str, str | None]:
    """SHA-256 of each working file's raw bytes (None when the file is absent)."""
    hashes: dict[str, str | None] = {}
    for rel in files:
        try:
            hashes[rel] = hashlib.sha256((root / rel).read_bytes()).hexdigest()
        except OSError:
            hashes[rel] = None
    return hashes


def _restore_message(patch: Path, files: list[str], why: str) -> str:
    checkout = "git checkout HEAD -- " + " ".join(shlex.quote(rel) for rel in files)
    apply = "git apply " + shlex.quote(patch.as_posix())
    return (
        f"ERROR: restore {why}. Your fix is saved byte-for-byte in {patch}. Bring it back by running, from the repo root:\n"
        f"  {checkout}\n"
        f"  {apply}"
    )


def _tail(output: bytes) -> str:
    lines = output.decode("utf-8", errors="replace").splitlines()
    shown = lines[-TAIL_LINES:]
    header = f"revert_check: last {len(shown)} of {len(lines)} lines of the test output:"
    return "\n".join([header, *(f"  | {line}" for line in shown)])


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
        if resolved.is_dir():
            print(
                f"ERROR: {raw!r} is a directory; name individual source files (a directory would revert everything under it)",
                file=sys.stderr,
            )
            return EXIT_USAGE
        files.append(resolved.relative_to(root).as_posix())

    for rel in files:
        probe = subprocess.run(["git", "cat-file", "-e", f"HEAD:{rel}"], cwd=root, capture_output=True, check=False)
        if probe.returncode != 0:
            print(
                f"ERROR: {rel} is not in HEAD (it is untracked, or only added to the index): git checkout cannot revert it. "
                "Commit it first, or leave it out of the list.",
                file=sys.stderr,
            )
            return EXIT_GIT

    try:
        before = _working_diff(root, files)
    except GitFailed as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return EXIT_GIT
    if not before:
        print(f"ERROR: no diff in {', '.join(files)} against HEAD: nothing to revert-check", file=sys.stderr)
        return EXIT_NO_DIFF
    hashes_before = _file_hashes(root, files)

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
            proc = subprocess.run(command, cwd=here, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
        except OSError as exc:
            print(f"ERROR: could not start the test command: {exc}", file=sys.stderr)
            result_code = EXIT_CANNOT_RUN
        else:
            print(_tail(proc.stdout))
            if proc.returncode == 0:
                result_code = EXIT_GREEN
            elif is_pytest(command) and proc.returncode in PYTEST_NOT_A_FAILURE:
                print(
                    f"ERROR: pytest exited {proc.returncode} (interrupted or collection error, internal error, usage error, or no tests "
                    "collected): that is not RED and proves nothing about the fix. Check the test path and -k filter. Fix restored below.",
                    file=sys.stderr,
                )
                result_code = EXIT_NOT_A_TEST_FAILURE
            else:
                print(f"revert_check: RED (test command exited {proc.returncode}) with the fix reverted")
    except GitFailed as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        result_code = EXIT_GIT
    finally:
        if reverted or _working_diff_safe(root, files) != before:
            result_code = _restore(root, files, patch, before, hashes_before, result_code)

    if result_code == EXIT_GREEN:
        print(
            "ERROR: tests stayed GREEN with the fix reverted: the fix is not proven (the tests cannot see the difference). Fix restored.",
            file=sys.stderr,
        )
    return result_code


def _restore(root: Path, files: list[str], patch: Path, before: bytes, hashes_before: dict[str, str | None], result_code: int) -> int:
    try:
        # Re-checkout first: a test command or formatter may have touched a named file, and the patch applies to HEAD's bytes.
        _git(root, "checkout", "HEAD", "--", *files)
        _git(root, "apply", "--whitespace=nowarn", str(patch))
    except GitFailed as exc:
        print(_restore_message(patch, files, f"failed ({exc})"), file=sys.stderr)
        return EXIT_RESTORE
    after = _working_diff_safe(root, files)
    if after != before:
        print(_restore_message(patch, files, "mismatch: the working diff is not identical to the saved one"), file=sys.stderr)
        return EXIT_RESTORE
    if _file_hashes(root, files) != hashes_before:
        print(
            _restore_message(patch, files, "mismatch: the diff matches but the file bytes differ from before (line endings or eol attributes)"),
            file=sys.stderr,
        )
        return EXIT_RESTORE
    print("revert_check: restore verified byte-identical (diff and file bytes)")
    return result_code


def main() -> int:
    return run(sys.argv[1:])


if __name__ == "__main__":
    sys.exit(main())
