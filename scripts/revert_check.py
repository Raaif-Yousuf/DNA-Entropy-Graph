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
indistinguishable from a failing assertion and still counts as RED. The command's output goes to a
FILE next to the patch (`<timestamp>-<pid>.out`), not a pipe: `dotnet test` leaves MSBuild node-reuse
and VBCSCompiler processes that inherit a pipe, and a pipe read would block on EOF long after the
tests end. When the command is pytest, exit code 2 (collection error) counts as RED with a
"collection error; read the tail" warning (it is the NORMAL red when the fix adds a symbol a test
imports); exit codes 3 (internal error), 4 (usage error) and 5 (no tests collected, for example a
mistyped `-k`) are refused with exit 15: they prove nothing about the fix. Output containing
`No module named pytest` (the wrong interpreter; Hard Rule 20) is also refused with exit 15.

Name individual tracked SOURCE files only:
  - a directory (including `.` or `app/src`) is refused, never expanded (exit 2);
  - a file absent from HEAD (untracked, or only added to the index) is refused up front (exit 13):
    `git checkout HEAD -- <path>` fails for a path HEAD lacks. Commit it first or leave it out;
  - a file with STAGED changes is refused up front (exit 13): the restore would drop the staging
    (this repo stages hunks on purpose). Unstage it (`git restore --staged <file>`) first;
  - every git call runs with `GIT_LITERAL_PATHSPECS=1`, so a name like `[id].cs` is a file, not a glob.

Known limits (L1, L3, L4): a hard kill (power loss, `taskkill /F`) mid-run leaves the named files at
HEAD; the patch path printed at the start is the recovery (`git apply <patch>`). Ctrl-C restores and
re-prints the patch path and the two recovery commands (exit 130). Old patches and output files under
`<git-dir>/revert-check/` accumulate; they are tiny and live in the git dir, so nothing tracks them.

Exit codes:
   0  tests went RED and the restore is exact (the fix is proven)
   2  usage error (missing `--`, no files, no command, or a directory argument)
  10  tests stayed GREEN with the fix reverted (the fix is not proven); restore still done
  11  no diff in the named files (nothing to revert)
  12  restore mismatch or restore failed (the patch path and recovery commands are in the message)
  13  a named file is not in HEAD or has staged changes, or git itself failed
  14  the test command could not be started; restore still done
  15  pytest exited 3, 4 or 5, or the output says `No module named pytest`: not a failing assertion; restore still done
 130  interrupted (Ctrl-C); restore done, recovery commands re-printed
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
PYTEST_NOT_A_FAILURE = {3, 4, 5}
PYTEST_COLLECTION_ERROR = 2
EXIT_INTERRUPTED = 130
WRONG_INTERPRETER_MARKER = "No module named pytest"

# Literal pathspecs: `[id].cs` is a file name, never a glob (also covers `*` and `?`).
GIT_ENV = {**os.environ, "GIT_LITERAL_PATHSPECS": "1"}

# The saved patch must not depend on the user's git config.
DIFF_FLAGS = ["--binary", "--no-color", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/"]


class GitFailed(Exception):
    pass


def _git(root: Path, *args: str, stdin: bytes | None = None) -> bytes:
    proc = subprocess.run(["git", *args], cwd=root, input=stdin, capture_output=True, check=False, env=GIT_ENV)
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


def _is_pytest_name(part: str) -> bool:
    return Path(part).stem.lower() in ("pytest", "py.test")


def is_pytest(command: list[str]) -> bool:
    """True when the command RUNS pytest: the executable is pytest, `-m pytest`, `uv run pytest`, or the
    command after a `--` wrapper (`heavy.py --lane x -- python -m pytest`). An argument that merely has
    the stem pytest (`python -c ... pytest`, `--filter pytest`) does not count."""
    for start in [0] + [i + 1 for i, part in enumerate(command) if part == "--"]:
        tail = command[start:]
        if not tail:
            continue
        if _is_pytest_name(tail[0]):
            return True
        if any(a == "-m" and b == "pytest" for a, b in zip(tail, tail[1:], strict=False)):
            return True
        if Path(tail[0]).stem.lower() == "uv" and len(tail) > 2 and tail[1] == "run" and _is_pytest_name(tail[2]):
            return True
    return False


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


def _run_tests(command: list[str], cwd: Path, log: Path) -> int:
    """Run the command with its combined output in `log` (a file, not a pipe: an inherited pipe handle held
    by a detached grandchild such as an MSBuild node would block the wait long after the tests end)."""
    with log.open("wb") as sink:
        proc = subprocess.Popen(command, cwd=cwd, stdout=sink, stderr=subprocess.STDOUT)
        try:
            return proc.wait()
        except BaseException:
            proc.kill()  # only the PID this script started
            raise


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
        probe = subprocess.run(["git", "cat-file", "-e", f"HEAD:{rel}"], cwd=root, capture_output=True, check=False, env=GIT_ENV)
        if probe.returncode != 0:
            print(
                f"ERROR: {rel} is not in HEAD (it is untracked, or only added to the index): git checkout cannot revert it. "
                "Commit it first, or leave it out of the list.",
                file=sys.stderr,
            )
            return EXIT_GIT

    for rel in files:
        staged = subprocess.run(["git", "diff", "--cached", "--quiet", "--", rel], cwd=root, capture_output=True, check=False, env=GIT_ENV)
        if staged.returncode != 0:
            print(
                f"ERROR: {rel} has staged changes and the restore would drop the staging (this repo stages hunks on purpose). "
                f"Unstage it first (git restore --staged {shlex.quote(rel)}), or leave it out of the list.",
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
    stamp = f"{time.strftime('%Y%m%d-%H%M%S')}-{os.getpid()}"
    patch = patch_dir / f"{stamp}.patch"
    log = patch_dir / f"{stamp}.out"
    patch.write_bytes(before)
    print(f"revert_check: fix saved to {patch}")

    result_code = EXIT_OK
    reverted = False
    try:
        _git(root, "checkout", "HEAD", "--", *files)
        reverted = True
        print(f"revert_check: reverted {', '.join(files)}; running: {' '.join(command)}")
        try:
            returncode = _run_tests(command, here, log)
        except OSError as exc:
            print(f"ERROR: could not start the test command: {exc}", file=sys.stderr)
            result_code = EXIT_CANNOT_RUN
        else:
            output = log.read_bytes()
            print(_tail(output))
            text = output.decode("utf-8", errors="replace")
            if returncode == 0:
                result_code = EXIT_GREEN
            elif WRONG_INTERPRETER_MARKER in text:
                print(
                    f"ERROR: the output says '{WRONG_INTERPRETER_MARKER}': the wrong interpreter (the PATH python has no wheels; use "
                    "worker\\.venv\\Scripts\\python.exe). That is not RED and proves nothing about the fix. Fix restored below.",
                    file=sys.stderr,
                )
                result_code = EXIT_NOT_A_TEST_FAILURE
            elif is_pytest(command) and returncode in PYTEST_NOT_A_FAILURE:
                print(
                    f"ERROR: pytest exited {returncode} (internal error, usage error, or no tests collected): that is not RED and "
                    "proves nothing about the fix. Check the test path and -k filter. Fix restored below.",
                    file=sys.stderr,
                )
                result_code = EXIT_NOT_A_TEST_FAILURE
            else:
                if is_pytest(command) and returncode == PYTEST_COLLECTION_ERROR:
                    print(
                        "WARNING: pytest exited 2 (collection error; read the tail): that is the normal red when the fix adds a symbol "
                        "a test imports, but a typo in the test path or an import error in the test itself looks the same.",
                        file=sys.stderr,
                    )
                print(f"revert_check: RED (test command exited {returncode}) with the fix reverted")
    except GitFailed as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        result_code = EXIT_GIT
    except KeyboardInterrupt:
        print("ERROR: interrupted; restoring.", file=sys.stderr)
        result_code = EXIT_INTERRUPTED
    finally:
        if reverted or _working_diff_safe(root, files) != before:
            result_code = _restore(root, files, patch, before, hashes_before, result_code)

    if result_code == EXIT_INTERRUPTED:
        print(
            _restore_message(patch, files, "ran after the interrupt; if `git diff` shows your fix is missing, the restore did not finish"),
            file=sys.stderr,
        )
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
