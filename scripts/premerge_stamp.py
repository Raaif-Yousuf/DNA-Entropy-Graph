"""scripts/premerge_stamp.py -- the record that premerge.py ran green on this HEAD (#451).

    python scripts/premerge_stamp.py --write fast        # what premerge.py calls after a green run
    python scripts/premerge_stamp.py --check             # exit 0 when a fresh stamp matches HEAD
    python scripts/premerge_stamp.py --self-test

The PreToolUse hook `scripts/hooks/require_premerge_before_pr.py` refuses `gh pr create` and
`gh pr merge` unless this module says a green premerge run exists for the CURRENT HEAD.

THE STAMP
---------
A JSON file at `<git-dir>/premerge-stamp.json`: `{"head": <sha>, "mode": "fast"|"full",
"time": <unix seconds>}`. It lives inside `.git`, so it is never tracked, never shows in
`git status`, and writing it does not change HEAD (a stamp in the working tree would
dirty the tree it is vouching for). It is per worktree (`--git-dir`, not the common dir),
because each worktree has its own HEAD.

KEYED BY HEAD, NOT BY THE WORKING TREE
--------------------------------------
Committing moves HEAD, which invalidates the stamp, so the sequence is: commit, run
premerge, `gh pr create`. That is deliberate: a stamp from before the commit vouches for a
tree that may not be the one being pushed. In a shared checkout the working tree holds other
lanes' uncommitted edits too; premerge then runs over those as well, which is the same
conservative direction (it can only be stricter than the commit).

A stamp expires after MAX_AGE_SECONDS even for an unchanged HEAD, so a stamp cannot vouch for
a tree whose gates have since gone stale (a changed guard, a moved origin/main).

premerge.py must write a stamp only for an UNFILTERED run (no --only, no --skip) that
finished with exit code 0; a partial run proves nothing about the rest.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

STAMP_NAME = "premerge-stamp.json"
LANDED_NAME = "landed-prs.json"
MAX_AGE_SECONDS = 12 * 3600
MODES = ("fast", "full")


def _git(repo: Path, *args: str) -> str | None:
    try:
        proc = subprocess.run(["git", *args], cwd=repo, capture_output=True, check=False, timeout=20)
    except (OSError, subprocess.SubprocessError):
        return None
    if proc.returncode != 0:
        return None
    return proc.stdout.decode("utf-8", errors="replace").strip()


def head_sha(repo: Path) -> str | None:
    return _git(repo, "rev-parse", "HEAD")


def stamp_path(repo: Path) -> Path | None:
    git_dir = _git(repo, "rev-parse", "--absolute-git-dir")
    return Path(git_dir) / STAMP_NAME if git_dir else None


def landed_path(repo: Path) -> Path | None:
    """`<common-git-dir>/landed-prs.json`: shared by every worktree, because the orchestrator merges a PR
    from a different checkout than the one land_wt.py verified it in."""
    common = _git(repo, "rev-parse", "--path-format=absolute", "--git-common-dir")
    return Path(common) / LANDED_NAME if common else None


def _read_landed(path: Path, attempts: int = 20, pause: float = 0.05) -> dict[str, dict]:
    """The records. A missing or corrupt file is {} (nothing to lose); any other OSError (Windows sharing
    violation, a scanner holding the file) is retried and then RAISED, never read as empty: record_landed
    would otherwise publish only its own record over everyone else's (MEASURED 2026-10-29 as a unit repro)."""
    for attempt in range(attempts):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (FileNotFoundError, ValueError):
            return {}
        except OSError:
            if attempt == attempts - 1:
                raise
            time.sleep(pause)
        else:
            return data if isinstance(data, dict) else {}
    return {}


LANDED_MAX_AGE_SECONDS = 72 * 3600  # a lane landed in the evening is merged the next morning
_LOCK_WAIT_SECONDS = 120.0  # a waiter raises TimeoutError (loudly); it never breaks a live holder's lock
_SHA = re.compile(r"^[0-9a-f]{40}$")


def _try_lock(fd: int) -> bool:
    """One non-blocking attempt at an OS byte-range lock on `fd`. The OS drops it when the owner dies, so
    there is no stale-lock age and nothing to break. MEASURED 2026-10-29: the old 30 s stale takeover broke
    the lock of a live but stalled holder and two writers then published over each other."""
    try:
        if sys.platform == "win32":
            import msvcrt

            os.lseek(fd, 0, os.SEEK_SET)
            msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)  # type: ignore[attr-defined]
        else:
            import fcntl

            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        return False
    return True


def _unlock(fd: int) -> None:
    with contextlib.suppress(OSError):
        if sys.platform == "win32":
            import msvcrt

            os.lseek(fd, 0, os.SEEK_SET)
            msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)  # type: ignore[attr-defined]
        else:
            import fcntl

            fcntl.flock(fd, fcntl.LOCK_UN)


@contextlib.contextmanager
def _landed_lock(path: Path):
    """An OS-level lock on `landed-prs.lock` next to the records, retried until _LOCK_WAIT_SECONDS. The file
    is never deleted (deleting a lock file is what makes lock files racy); an unlocked leftover is not held."""
    lock = path.with_name("landed-prs.lock")
    fd = os.open(lock, os.O_CREAT | os.O_RDWR)
    try:
        deadline = time.monotonic() + _LOCK_WAIT_SECONDS
        while not _try_lock(fd):
            if time.monotonic() > deadline:
                raise TimeoutError(f"could not take {lock}")
            time.sleep(0.02)
        try:
            yield
        finally:
            _unlock(fd)
    finally:
        os.close(fd)


def _fresh(record: object, now: float) -> bool:
    try:
        return now - float(record["time"]) <= LANDED_MAX_AGE_SECONDS  # type: ignore[index]
    except (KeyError, TypeError, ValueError):
        return False


def record_landed(repo: Path, pr: str, sha: str, now: float | None = None) -> None:
    """scripts/land_wt.py calls this after it opened PR `pr` from a premerge-green throwaway worktree.

    Read-modify-write under a lock file, written to a temp file and os.replace()d so a reader never sees
    half a file; expired records are pruned on every write."""
    if not _SHA.match(sha or ""):
        raise ValueError(f"record_landed needs the full 40-hex head sha, got {sha!r}")
    path = landed_path(repo)
    if path is None:
        return
    stamp_time = time.time() if now is None else now
    with _landed_lock(path):
        records = {k: v for k, v in _read_landed(path).items() if _fresh(v, stamp_time)}
        records[str(pr)] = {"head": sha, "time": stamp_time}
        tmp = path.with_name(f"landed-prs.{os.getpid()}.{threading.get_ident()}.tmp")
        tmp.write_text(json.dumps(records), encoding="utf-8", newline="\n")
        _replace_with_retry(tmp, path)


def _replace_with_retry(tmp: Path, dest: Path, attempts: int = 10, pause: float = 0.05) -> None:
    """os.replace, retried on PermissionError (Windows: a reader or scanner holds the file open for a moment);
    the temp file is deleted when the last attempt fails."""
    for attempt in range(attempts):
        try:
            os.replace(tmp, dest)
            return
        except PermissionError:
            if attempt == attempts - 1:
                tmp.unlink(missing_ok=True)
                raise
            time.sleep(pause)
        except BaseException:
            tmp.unlink(missing_ok=True)
            raise


def landed_head(repo: Path, pr: str, now: float | None = None) -> str | None:
    """The head sha land_wt.py verified for PR `pr` (within LANDED_MAX_AGE_SECONDS), else None."""
    try:
        path = landed_path(repo)
        record = _read_landed(path).get(str(pr)) if path is not None else None
    except OSError:
        return None  # unreadable means no record; the hook then denies with its clear message
    if not isinstance(record, dict) or not _fresh(record, time.time() if now is None else now):
        return None
    head = record.get("head")
    return head if isinstance(head, str) and head else None


def write_stamp(repo: Path, mode: str, now: float | None = None) -> Path:
    if mode not in MODES:
        raise ValueError(f"mode must be one of {MODES}, got {mode!r}")
    sha, path = head_sha(repo), stamp_path(repo)
    if sha is None or path is None:
        raise RuntimeError(f"{repo} is not a git repository with a commit; cannot write a premerge stamp")
    record = {"head": sha, "mode": mode, "time": time.time() if now is None else now}
    path.write_text(json.dumps(record), encoding="utf-8", newline="\n")
    return path


def check_stamp(repo: Path, now: float | None = None) -> tuple[bool, str]:
    """(ok, reason). `ok` is False only when a stamp is definitely missing, stale or for another
    HEAD. A repo this module cannot read at all returns ok=True (callers fail open: a gate that
    cannot tell must not wedge a session)."""
    sha, path = head_sha(repo), stamp_path(repo)
    if sha is None or path is None:
        return True, "not a git repository with a commit; nothing to check"
    if not path.is_file():
        return False, "premerge has not been run in this checkout"
    try:
        record = json.loads(path.read_text(encoding="utf-8"))
        stamp_head, stamp_time = str(record["head"]), float(record["time"])
    except (OSError, ValueError, KeyError, TypeError):
        return False, "the premerge stamp is unreadable"
    if stamp_head != sha:
        return False, f"the last premerge run was for {stamp_head[:9]}, but HEAD is now {sha[:9]}"
    age = (time.time() if now is None else now) - stamp_time
    if age > MAX_AGE_SECONDS:
        return False, f"the last premerge run on this HEAD is {age / 3600:.1f} hours old"
    return True, f"premerge ({record.get('mode', '?')}) passed on {sha[:9]}"


def self_test() -> bool:
    ok = True

    def git(repo: Path, *args: str) -> None:
        subprocess.run(["git", *args], cwd=repo, check=True, capture_output=True)

    def expect(label: str, condition: bool, extra: str = "") -> None:
        nonlocal ok
        print(("ok    " if condition else "FAIL: ") + label + ("" if condition else f" {extra}"))
        ok = ok and condition

    with tempfile.TemporaryDirectory() as tmp:
        repo = Path(tmp)
        git(repo, "init", "-q")
        git(repo, "config", "user.email", "t@example.com")
        git(repo, "config", "user.name", "T")
        (repo / "a.txt").write_text("1\n", encoding="utf-8", newline="\n")
        git(repo, "add", "-A")
        git(repo, "commit", "-q", "-m", "one")

        expect("no stamp is not ok", check_stamp(repo)[0] is False)
        write_stamp(repo, "fast")
        expect("a fresh stamp on HEAD is ok", check_stamp(repo)[0] is True)
        expect("a stale stamp is not ok", check_stamp(repo, now=time.time() + MAX_AGE_SECONDS + 60)[0] is False)
        (repo / "a.txt").write_text("2\n", encoding="utf-8", newline="\n")
        git(repo, "add", "-A")
        git(repo, "commit", "-q", "-m", "two")
        ok_after, reason = check_stamp(repo)
        expect("a new commit invalidates the stamp", ok_after is False and "HEAD is now" in reason, reason)
        (repo / ".git" / STAMP_NAME).write_text("{not json", encoding="utf-8", newline="\n")
        expect("a corrupt stamp is not ok", check_stamp(repo)[0] is False)
    with tempfile.TemporaryDirectory() as tmp:
        expect("a directory that is not a repo fails open", check_stamp(Path(tmp))[0] is True)
    print(f"\n{'PASS' if ok else 'FAIL'}: premerge_stamp self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Record or check that premerge.py passed on this HEAD.")
    group = ap.add_mutually_exclusive_group(required=True)
    group.add_argument("--write", choices=MODES, metavar="MODE", help="record a green run (fast or full)")
    group.add_argument("--check", action="store_true", help="exit 0 when a fresh stamp matches HEAD")
    group.add_argument("--self-test", action="store_true")
    ap.add_argument("--repo", type=Path, default=Path.cwd())
    args = ap.parse_args(argv)
    if args.self_test:
        return 0 if self_test() else 1
    if args.write:
        try:
            print(f"wrote {write_stamp(args.repo, args.write)}")
        except RuntimeError as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2
        return 0
    ok, reason = check_stamp(args.repo)
    print(reason)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
