"""scripts/land_wt.py -- land one committed worktree branch as a PR, verified in a throwaway worktree (#510).

    worker\\.venv\\Scripts\\python.exe scripts\\land_wt.py --branch feat/510-x --sha <sha> \\
        --title "feat: ..." --body-file body.md [--full] [--land-branch NAME]

WHY A SEPARATE SCRIPT FROM land_pr.py: land_pr.py takes uncommitted edits from a shared checkout,
stages exact paths and returns that checkout to main. Here the input is a COMMIT a lane already made
in its own worktree, nothing is staged and no checkout is touched, so none of its machinery applies.

WHAT IT DOES, IN ORDER (a refusal before step 4 changes nothing; no step ever force-pushes)
1. `git fetch origin`; the sha must be the tip of `refs/heads/<branch>` (or `origin/<branch>`).
2. Scan every changed text file for control bytes (MEASURED 2026-10-02: an agent's `scripts\\v`
   reached disk as `\\x0b`). Binary files (git's own numstat verdict) are skipped.
3. Clean up any worktree an earlier killed run left under the temp root (junction first).
4. `git worktree add --detach <temp>/<slug>-<time> <sha>`, outside the repo, and a JUNCTION
   worker\\.venv -> the real venv so Hard Rule 20 tooling finds it (never a copy).
5. Merge origin/main when the branch is behind. The only conflict resolved automatically is
   docs/ToTest.md (append-only rows: `git merge-file --union`); any other conflict aborts the merge
   and refuses with the file list, so the lane resolves it.
6. premerge (`--fast`, or full with `--full`) inside the worktree with PYTHONPATH=<wt>\\worker\\src
   (the venv's editable install points at the shared tree) and its dotnet obj/bin under the
   worktree. Red refuses; the log path is printed.
7. Push HEAD to `refs/heads/<land-branch>` (default: the branch name) and `gh pr create`.
   Merging stays a human call (`gh pr merge <n> --merge`) unless `--merge-when-green` is passed (#666):
   then, after the PR opens, it polls `gh pr checks <pr> --json name,bucket,state` every `--checks-poll`
   seconds up to `--checks-timeout`, refuses at once when any check is fail/cancel (naming them), refuses
   when anything is still pending (or no check exists) at the timeout, and only when every check is
   pass/skipping runs `gh pr merge <pr> --merge` (a merge commit). Either refusal leaves the PR open.
   `--rerun-failed-once` (default on, `--no-rerun-failed-once` to disable): the first red result re-runs each failed
   run once (`gh run rerun <run> --failed`, after the other checks settle), appends a line to
   `<git-dir>/flaky-checks.log` and waits again; a second red refuses.
8. ALWAYS, success or not: unlink the junction with `os.rmdir`, check it is gone, and only then
   `git worktree remove --force`. MEASURED: an earlier tool deleted the real venv through a junction.
   If the junction cannot be removed the worktree is LEFT IN PLACE (and named) rather than removed.

Exit codes: 0 PR opened (and merged, with --merge-when-green), 1 refused (nothing pushed), 2 bad usage, 3 premerge red,
4 failed after work began, 5 a PR check failed (PR open, not merged), 6 a PR check still pending at the timeout (PR open, not merged).
Console output is ASCII (`OK:` / `ERROR:` / `-`).
"""

from __future__ import annotations

import argparse
import contextlib
import json
import os
import re
import signal
import stat
import subprocess
import sys
import tempfile
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from functools import partial
from pathlib import Path
from typing import Any

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

import check_totest_format as ctf  # noqa: E402

REMOTE = "origin"
BASE = "main"
TOTEST = "docs/ToTest.md"
VENV_REL = Path("worker") / ".venv"
# NUL..BS, VT, FF and SO..US except ESC. ESC (0x1b) is allowed on purpose: captured logs and test output carry ANSI
# colour and no measured corruption produced it. VT (0x0b, the MEASURED `\v` case) and FF are refused; TAB, LF, CR are text.
CONTROL_BYTES = re.compile(rb"[\x00-\x08\x0b\x0c\x0e-\x1a\x1c-\x1f]")
PROTECTED = {"main", "master", "head"}
LOCK_DIR = "locks"
LOCK_MAX_AGE_SECONDS = 24 * 3600

EXIT_OK, EXIT_REFUSED, EXIT_USAGE, EXIT_RED, EXIT_FAILED = 0, 1, 2, 3, 4
EXIT_CHECKS_RED, EXIT_CHECKS_PENDING = 5, 6
CHECKS_TIMEOUT_SECONDS = 30 * 60
CHECKS_POLL_SECONDS = 20.0
# `gh pr checks` buckets: pass and skipping are green, fail and cancel are red, anything else (pending) is not done.
GREEN_BUCKETS = {"pass", "skipping"}
RED_BUCKETS = {"fail", "cancel"}

# premerge(worktree, venv, full) -> (exit code, combined output). Injectable so tests never run the real gates.
PremergeFn = Callable[[Path, Path, bool], "tuple[int, str]"]


class LandError(Exception):
    def __init__(self, message: str, code: int = EXIT_FAILED) -> None:
        super().__init__(message)
        self.code = code


class LandTimeout(LandError):
    """A git or gh call hit its timeout (its whole process tree was killed)."""


def _text(raw: bytes) -> str:
    return raw.decode("utf-8", errors="replace").strip()


GH_TIMEOUT_SECONDS = 300.0  # one gh call (pr checks / view / create / merge / run rerun)
GIT_TIMEOUT_SECONDS = 600.0  # one git call (fetch / push / worktree add...)
HEAD_READ_ATTEMPTS = 3  # reads of the PR head before the merge
HEAD_READ_BACKOFF_SECONDS = 2.0  # sleep before read n+1 is this times n
PREMERGE_TIMEOUT_SECONDS = 120 * 60.0  # the whole premerge run, heavy.py slot wait included


_K32: list[object] = []


def _kernel32() -> Any:  # noqa: ANN401  (a ctypes library object)
    """kernel32 with explicit prototypes: a HANDLE is pointer sized, and ctypes' default int return truncates it on 64-bit Windows.
    A private WinDLL, so the prototypes never leak into other code that shares `ctypes.windll.kernel32`."""
    if not _K32:
        import ctypes  # noqa: PLC0415
        from ctypes import wintypes  # noqa: PLC0415

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)  # type: ignore[attr-defined]
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k32.OpenProcess.restype = ctypes.c_void_p
        k32.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD)]
        k32.GetExitCodeProcess.restype = wintypes.BOOL
        k32.GetProcessTimes.argtypes = [ctypes.c_void_p] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        k32.GetProcessTimes.restype = wintypes.BOOL
        k32.CloseHandle.argtypes = [ctypes.c_void_p]
        k32.CloseHandle.restype = wintypes.BOOL
        _K32.append(k32)
    return _K32[0]


def _taskkill_tree(pid: int) -> None:
    """`taskkill /T /F /PID <pid>`: that PID and its descendants only, never by image name."""
    with contextlib.suppress(OSError, subprocess.SubprocessError):
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)], capture_output=True, check=False, timeout=30)


def _proc_fields(pid: int | str) -> tuple[str, int, int] | None:
    """(state, ppid, start time in ticks) from /proc/<pid>/stat, or None (gone, or no /proc). comm (field 2) may contain spaces
    and parens, so split after its LAST closing paren: the rest starts at field 3 (state), ppid is field 4, starttime field 22."""
    try:
        rest = Path(f"/proc/{pid}/stat").read_text(errors="replace").rpartition(")")[2].split()
        return rest[0], int(rest[1]), int(rest[19])
    except (OSError, ValueError, IndexError):
        return None


def _proc_ident(pid: int) -> int | None:
    """The process's start time in ticks: who this pid is (pid + start time). A recycled pid has a different start time. The
    ppid is NOT part of the identity: a stopped victim whose parent dies is reparented and must still be killed."""
    fields = _proc_fields(pid)
    return None if fields is None else fields[2]


def _proc_table() -> dict[int, tuple[int, int]]:
    """pid -> (ppid, start time) for every live process; {} where /proc is absent (macOS)."""
    table: dict[int, tuple[int, int]] = {}
    with contextlib.suppress(OSError):
        for entry in os.listdir("/proc"):
            if entry.isdigit() and (fields := _proc_fields(entry)) is not None:
                table[int(entry)] = (fields[1], fields[2])
    return table


def _kill_if_same(pid: int, start: int, sig: int | None = None) -> bool | None:
    """Signal `pid` only if it is still the process recorded at scan time (same start time): a pid recycled since the scan is
    skipped. True: signalled. False: not the recorded process (gone or recycled), nothing sent. None: it IS the recorded process
    but the signal raised OSError (e.g. EPERM). The recheck-then-kill window is microseconds (no pidfd on every kernel); stopped
    victims cannot exit on their own, so it is only open for a victim that died anyway, and a pid reuse inside it is vanishingly
    unlikely."""
    if _proc_ident(pid) != start:
        return False
    try:
        os.kill(pid, signal.SIGKILL if sig is None else sig)
    except ProcessLookupError:
        return False
    except OSError:
        return None
    return True


def _await_stopped(stopped: dict[int, int], timeout: float = 2.0) -> None:
    """SIGSTOP is delivered asynchronously: poll until each victim shows state T (or is a zombie/gone), bounded. A scan taken
    before the stop lands could miss a fork that is still in flight."""
    end = time.monotonic() + timeout
    for victim, start in stopped.items():
        while time.monotonic() < end:
            fields = _proc_fields(victim)
            if fields is None or fields[2] != start or fields[0] in ("T", "t", "Z", "X", "x"):
                break
            time.sleep(0.005)


def _posix_kill_tree(pid: int) -> None:
    """SIGSTOP the root, then SIGSTOP every descendant found (confirming each stop landed), rescanning until none is new (a
    stopped process cannot fork, so nothing escapes by reparenting to init between the scan and the kill), then SIGKILL them all,
    children before the root. A `finally` kills everything registered so an exception never leaves a frozen tree. After the round
    cap (a fork storm outrunning the stops) whatever was found is still killed; the rest is out of reach.
    Every signal is identity-checked (pid + start time): a root that exited or whose pid was recycled is never signalled."""
    root = _proc_ident(pid)
    if root is None:
        if not os.path.isdir("/proc"):  # no /proc (macOS): nothing to identify or walk, the plain kill of the pid
            with contextlib.suppress(OSError):
                os.kill(pid, signal.SIGKILL)
        return  # /proc exists: None means gone (or a transient parse failure), never a blind kill
    registered: dict[int, int] = {}  # everything we may have stopped, registered BEFORE the signal (a KeyboardInterrupt between
    confirmed: dict[int, int] = {}  # the signal and the bookkeeping must not leave it frozen); confirmed: the stop was delivered
    try:
        registered[pid] = root
        sent = _kill_if_same(pid, root, signal.SIGSTOP)
        if sent is False:  # exited or recycled between the record and the stop: not ours to touch
            registered.clear()
            return
        if sent:
            confirmed[pid] = root
        for _ in range(20):
            _await_stopped(confirmed)
            new = {c: start for c, (ppid, start) in _proc_table().items() if ppid in registered and c not in registered}
            if not new:
                break
            for child, start in new.items():
                registered[child] = start
                sent = _kill_if_same(child, start, signal.SIGSTOP)
                if sent is False:
                    del registered[child]
                elif sent:
                    confirmed[child] = start
    finally:
        for victim, start in reversed(list(registered.items())):
            _kill_if_same(victim, start)


def kill_pid_tree(pid: int, wait: float = 10.0) -> bool:
    """Kill an unrelated process (one we did not start, e.g. a stuck queue runner) and its descendants, then wait up to `wait`
    seconds for it to be gone. True when it is gone. The caller has verified pid + creation time first.
    On POSIX the tree is frozen with SIGSTOP and walked via /proc (the group may be the user's shell, so killpg is wrong), then killed."""
    if pid <= 1:  # never signal a process group (0), everything (-1) or init (1)
        return False
    if os.name == "nt":
        _taskkill_tree(pid)
    else:
        _posix_kill_tree(pid)
    end = time.monotonic() + wait
    while pid_alive(pid):
        if time.monotonic() >= end:
            return False
        time.sleep(0.1)
    return True


def kill_tree(proc: subprocess.Popen[bytes]) -> None:
    """Kill `proc` and everything it started: taskkill /T on exactly this PID (never by image name), or the process group."""
    if os.name == "nt":
        _taskkill_tree(proc.pid)
    else:
        with contextlib.suppress(OSError):
            os.killpg(proc.pid, signal.SIGKILL)
    with contextlib.suppress(OSError):
        proc.kill()


def run_bounded(
    argv: Sequence[str],
    *,
    cwd: Path | None = None,
    env: dict[str, str] | None = None,
    stdin: bytes | None = None,
    timeout: float | None = None,
) -> subprocess.CompletedProcess[bytes]:
    """`subprocess.run(capture_output=True)` whose timeout kills the WHOLE process tree (a grandchild holding the pipes open would
    otherwise make `run` wait forever after its own kill). Raises subprocess.TimeoutExpired (after a bounded wait) and OSError."""
    extra: dict[str, object] = {} if os.name == "nt" else {"start_new_session": True}
    proc = subprocess.Popen(
        list(argv),
        cwd=cwd,
        env=env,
        stdin=subprocess.PIPE if stdin is not None else subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        **extra,  # type: ignore[arg-type]
    )
    try:
        out, err = proc.communicate(stdin, timeout=timeout)
    except subprocess.TimeoutExpired:
        kill_tree(proc)
        try:
            out, err = proc.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            out, err = b"", b""  # something escaped the kill and still holds a pipe: give up on its output, never wait on it
        raise subprocess.TimeoutExpired(list(argv), timeout or 0, out, err) from None
    return subprocess.CompletedProcess(list(argv), proc.returncode, out, err)


def git(
    cwd: Path, *args: str, check: bool = True, stdin: bytes | None = None, timeout: float | None = GIT_TIMEOUT_SECONDS
) -> subprocess.CompletedProcess[bytes]:
    """Bytes in, bytes out (never text=True on Windows: it re-encodes). Module-level so tests can spy on it. Bounded by `timeout`."""
    env = dict(os.environ)
    env["GIT_TERMINAL_PROMPT"] = "0"
    try:
        proc = run_bounded(["git", "-c", "core.quotepath=off", *args], cwd=cwd, env=env, stdin=stdin, timeout=timeout)
    except subprocess.TimeoutExpired:
        raise LandTimeout(
            f"git {' '.join(args[:3])} timed out after {int(timeout or 0)} s. Check the network and git, then land again"
        ) from None
    if check and proc.returncode != 0:
        raise LandError(f"git {' '.join(args[:3])} failed ({proc.returncode}): {_text(proc.stderr or proc.stdout)}")
    return proc


# ---------------------------------------------------------------------------
# Directory links (the venv junction). Removal is os.rmdir, NEVER shutil.rmtree.
# ---------------------------------------------------------------------------


def make_dir_link(link: Path, target: Path) -> None:
    try:
        link.parent.mkdir(parents=True, exist_ok=True)
        if os.name == "nt":
            import _winapi  # noqa: PLC0415  (Windows only)

            _winapi.CreateJunction(str(target), str(link))
        else:
            os.symlink(target, link, target_is_directory=True)
    except OSError as err:
        raise LandError(f"could not link {link} to {target}: {err}") from err


def unlink_dir_link(link: Path) -> None:
    """Remove a junction or directory symlink without touching its target. Refuses a real directory."""
    if not os.path.lexists(link):
        return
    if os.path.isjunction(link) or os.path.islink(link):
        if os.name == "nt":
            os.rmdir(link)  # removes the reparse point only
        else:
            os.unlink(link)
    else:
        raise LandError(f"{link} is a real directory, not a link; refusing to remove it")
    if os.path.lexists(link):
        raise LandError(f"could not remove the link {link}")


# ---------------------------------------------------------------------------
# Pure helpers
# ---------------------------------------------------------------------------


def find_control_bytes(files: dict[str, bytes]) -> list[str]:
    return sorted(name for name, data in files.items() if CONTROL_BYTES.search(data))


def split_conflicts(unmerged: Sequence[str]) -> tuple[list[str], list[str]]:
    """(the ones we union-resolve, the ones the lane must resolve)."""
    return [f for f in unmerged if f == TOTEST], [f for f in unmerged if f != TOTEST]


def slugify(branch: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]", "-", branch)


def check_branch_name(label: str, name: str) -> None:
    """Refuse the names that must never be a landing target, before any work starts."""
    if not name or name.startswith(("-", "refs/")) or name.lower() in PROTECTED:
        raise LandError(f"{label} {name!r} is protected or malformed; pass a lane branch such as feat/123-slug", EXIT_REFUSED)


def pid_alive(pid: int) -> bool:
    """Is a process with this pid running? Never os.kill on Windows (any signal there TERMINATES the process)."""
    if os.name == "nt":
        import ctypes  # noqa: PLC0415
        from ctypes import wintypes  # noqa: PLC0415

        kernel32 = _kernel32()
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        try:
            code = wintypes.DWORD()
            return bool(kernel32.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == 259  # STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    fields = _proc_fields(pid)  # a zombie (killed, not yet reaped by its parent) is not running; kill(pid, 0) still succeeds on it
    if fields is None:
        return True
    return fields[0] not in ("Z", "X", "x")


def proc_start_time(pid: int) -> float | None:
    """The process creation time of `pid` as a float (an opaque but stable, comparable number: FILETIME ticks on Windows,
    /proc starttime ticks on Linux), or None when it cannot be read (no such process, no /proc, no permission).
    A recorded pid whose creation time now differs is a REUSED pid, not the process we recorded."""
    if os.name == "nt":
        import ctypes  # noqa: PLC0415
        from ctypes import wintypes  # noqa: PLC0415

        kernel32 = _kernel32()
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return None
        try:
            created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
            if not kernel32.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)):
                return None
            return float((created.dwHighDateTime << 32) | created.dwLowDateTime)
        finally:
            kernel32.CloseHandle(handle)
    try:
        text = Path(f"/proc/{pid}/stat").read_text(encoding="utf-8", errors="replace")
        # the command name is in parentheses and may hold spaces: the fields after the LAST `)` start at field 3
        return float(text.rsplit(")", 1)[1].split()[19])  # field 22, starttime
    except (OSError, IndexError, ValueError):
        return None


def remove_tree_no_follow(path: Path) -> None:
    """Delete a directory tree WITHOUT ever entering a junction or symlink: those are unlinked, not recursed into."""
    if os.path.isjunction(path) or os.path.islink(path):
        unlink_dir_link(path)
        return
    for entry in os.scandir(path):
        child = Path(entry.path)
        if entry.is_symlink() or os.path.isjunction(child):
            unlink_dir_link(child)
        elif entry.is_dir(follow_symlinks=False):
            remove_tree_no_follow(child)
        else:
            os.chmod(child, stat.S_IWRITE)
            child.unlink()
    os.rmdir(path)


# ---------------------------------------------------------------------------
# The landing
# ---------------------------------------------------------------------------


def run_premerge(wt: Path, venv: Path, full: bool, timeout: float | None = None) -> tuple[int, str]:
    """The real gate run: the venv's python reached THROUGH the worktree's junction, the worktree's sources first.
    Bounded by `timeout` (default PREMERGE_TIMEOUT_SECONDS, which includes the wait for heavy.py's machine-wide slot): on expiry the
    whole tree is killed and the result is red (124), so nothing is pushed or merged."""
    limit = PREMERGE_TIMEOUT_SECONDS if timeout is None else timeout
    py = wt / VENV_REL / "Scripts" / "python.exe"
    if not py.is_file():
        py = wt / VENV_REL / "bin" / "python"
    argv = [str(py), str(wt / "scripts" / "premerge.py"), "--root", str(wt), "--python", str(py)]
    if not full:
        argv.append("--fast")
    heavy = wt / "scripts" / "heavy.py"
    if heavy.is_file():  # one machine-wide heavy slot for the whole run (RAM)
        argv = [str(py), str(heavy), "--lane", f"land-{wt.name}"[:64], "--", *argv]
    env = dict(os.environ)
    env["PYTHONPATH"] = str(wt / "worker" / "src")
    try:
        proc = run_bounded(argv, cwd=wt, env=env, timeout=limit)
    except subprocess.TimeoutExpired as err:
        tail = _text((err.stdout or b"")[-2000:]) if isinstance(err.stdout, bytes) else ""
        return 124, f"ERROR: premerge timed out after {int(limit)} s and was killed (a hung gate or heavy.py slot wait)\n{tail}"
    return proc.returncode, _text(proc.stdout) + "\n" + _text(proc.stderr)


@dataclass
class Ctx:
    repo: Path
    venv: Path
    tmp_root: Path = field(default_factory=lambda: Path(tempfile.gettempdir()) / "landwt")
    gh: list[str] = field(default_factory=lambda: ["gh"])
    premerge: PremergeFn = run_premerge
    pid_alive: Callable[[int], bool] = pid_alive
    sleep: Callable[[float], None] = time.sleep  # injectable: tests never wait on a real clock
    monotonic: Callable[[], float] = time.monotonic
    log: list[str] = field(default_factory=list)
    gh_timeout: float = GH_TIMEOUT_SECONDS
    git_timeout: float = GIT_TIMEOUT_SECONDS  # the push (fetch and the rest use git()'s own default)
    checks_error: str = ""  # why the last `gh pr checks` could not be read ("" = it could, or gh simply had no checks yet)

    def say(self, message: str) -> None:
        print(message)
        self.log.append(message)


def _resolve_tip(ctx: Ctx, branch: str, sha: str) -> str:
    proc = git(ctx.repo, "rev-parse", "--verify", "-q", f"{sha}^{{commit}}", check=False)
    if proc.returncode != 0:
        raise LandError(f"no commit {sha} in this repository; fetch it or fix --sha", EXIT_REFUSED)
    full = _text(proc.stdout)
    for ref in (f"refs/heads/{branch}", f"refs/remotes/{REMOTE}/{branch}"):
        tip = git(ctx.repo, "rev-parse", "--verify", "-q", ref, check=False)
        if tip.returncode == 0:
            if _text(tip.stdout) != full:
                raise LandError(
                    f"{branch} is at {_text(tip.stdout)[:12]}, not {full[:12]}; "
                    "the lane moved on or the sha is stale. Re-read the branch tip and retry",
                    EXIT_REFUSED,
                )
            return full
    raise LandError(f"no branch {branch} locally or on {REMOTE}; check --branch", EXIT_REFUSED)


def _changed_text_files(ctx: Ctx, sha: str) -> dict[str, bytes]:
    # -z: NUL-terminated records with unquoted paths, so a name holding a tab, quote or newline is read exactly.
    raw = git(ctx.repo, "diff", "--numstat", "-z", "--no-renames", "--diff-filter=AM", f"{REMOTE}/{BASE}...{sha}").stdout
    files: dict[str, bytes] = {}
    for record in raw.split(b"\0"):
        if not record:
            continue
        added, _deleted, name_bytes = record.split(b"\t", 2)
        if added == b"-":  # git's verdict: binary
            continue
        name = name_bytes.decode("utf-8", errors="surrogateescape")
        files[name] = git(ctx.repo, "show", f"{sha}:{name}").stdout
    return files


def _remove_worktree(ctx: Ctx, wt: Path) -> None:
    """Junction first, verified gone, THEN git removes the worktree. Never the other way round."""
    unlink_dir_link(wt / VENV_REL)
    if os.path.lexists(wt / VENV_REL):
        raise LandError(f"venv link still present in {wt}; leaving the worktree in place")
    git(ctx.repo, "worktree", "remove", "--force", str(wt))


def _lock_path(ctx: Ctx, name: str) -> Path:
    return ctx.tmp_root / LOCK_DIR / f"{name}.json"


def _write_lock(ctx: Ctx, name: str, sha: str | None = None) -> None:
    """`sha` is the commit the throwaway checks out: scripts/prune_worktrees.py (#794) reads it to judge a leftover."""
    path = _lock_path(ctx, name)
    path.parent.mkdir(parents=True, exist_ok=True)
    record: dict[str, object] = {"pid": os.getpid(), "start": time.time()}
    if sha:
        record["sha"] = sha
    path.write_text(json.dumps(record), encoding="utf-8", newline="\n")


def _run_is_live(ctx: Ctx, name: str) -> bool:
    try:
        info = json.loads(_lock_path(ctx, name).read_text(encoding="utf-8"))
        return ctx.pid_alive(int(info["pid"])) and time.time() - float(info["start"]) < LOCK_MAX_AGE_SECONDS
    except (OSError, ValueError, KeyError, TypeError):
        return False  # no readable lock: nothing is holding this directory


def _sweep_stale_dir(ctx: Ctx, stale: Path) -> None:
    """Junction first; then git; if git does not know the directory, prune and delete it without following links."""
    unlink_dir_link(stale / VENV_REL)
    if os.path.lexists(stale / VENV_REL):
        raise LandError(f"venv link still present in {stale}")
    if git(ctx.repo, "worktree", "remove", "--force", str(stale), check=False).returncode != 0 or stale.exists():
        git(ctx.repo, "worktree", "prune", check=False)
        if stale.exists():
            remove_tree_no_follow(stale)
    _lock_path(ctx, stale.name).unlink(missing_ok=True)


def _clean_stale(ctx: Ctx, slug: str) -> None:
    """Sweep only directories named exactly `<slug>-<14 digit timestamp>` that no live run holds. A failure to sweep one
    is a warning, never a reason to refuse this landing."""
    if ctx.tmp_root.is_dir():
        pattern = re.compile(re.escape(slug) + r"-\d{14}")
        for stale in sorted(ctx.tmp_root.iterdir()):
            if not (stale.is_dir() and pattern.fullmatch(stale.name)):
                continue
            if _run_is_live(ctx, stale.name):
                ctx.say(f"- leaving {stale}: a run of this branch is still using it")
                continue
            ctx.say(f"- removing a worktree left by an earlier run: {stale}")
            try:
                _sweep_stale_dir(ctx, stale)
            except (LandError, OSError) as err:
                ctx.say(f"WARNING: could not remove {stale}: {err}")
    git(ctx.repo, "worktree", "prune", check=False)


def _ident(ctx: Ctx) -> list[str]:
    """`-c` identity args for the merge commit, from the repo config or a fallback, so an empty global config cannot fail it."""

    def configured(key: str, fallback: str) -> str:
        return _text(git(ctx.repo, "config", key, check=False).stdout) or fallback

    return ["-c", f"user.name={configured('user.name', 'land_wt')}", "-c", f"user.email={configured('user.email', 'land_wt@localhost')}"]


RESW_PATH = "app/src/DnaEntropyGraph.App/Strings/en-US/Resources.resw"


def _users_global_attributes(repo: Path) -> str:
    """The text of the user's global git attributes file (core.attributesFile, else the XDG default), or ''."""
    configured = _text(git(repo, "config", "--path", "--get", "core.attributesFile", check=False).stdout).strip()
    if configured:
        path = Path(configured)
    else:
        xdg = os.environ.get("XDG_CONFIG_HOME")
        path = (Path(xdg) if xdg else Path.home() / ".config") / "git" / "attributes"
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return ""


def resw_driver_flags(repo: Path) -> tuple[list[str], Path]:
    """(`-c` flags that route Resources.resw through scripts/resw_merge.py for ONE git command, the temp
    attributes file they name). The caller deletes the file when the command is done.

    Passed per command instead of persisted, so landing needs no one-time setup and nothing here leaks into
    the shared repo config. (scripts/setup_git_drivers.py persists the same routing for ordinary merges: git reads
    info/attributes from the COMMON dir only, MEASURED 2026-10-16, a linked worktree's own copy is ignored, so that
    setup writes the common dir's file.) The attribute line goes in a unique temp file used
    as core.attributesFile; that setting replaces the user's global attributes, so the file is the user's
    own global attributes text plus the resw line. The script is the one beside this file (the lane
    branch may not have it) and the interpreter is the one running land_wt (the venv's).
    """
    script = (Path(__file__).resolve().parent / "resw_merge.py").as_posix()
    mine = _users_global_attributes(repo)
    handle, name = tempfile.mkstemp(prefix="landwt-resw-", suffix=".gitattributes")
    attrs = Path(name)
    with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(mine if mine.endswith("\n") or not mine else mine + "\n")
        fh.write(f"{RESW_PATH} merge=resw\n")
    driver = f'"{Path(sys.executable).as_posix()}" "{script}" %O %A %B'
    return ["-c", f"merge.resw.driver={driver}", "-c", f"core.attributesFile={attrs.as_posix()}"], attrs


def _prefix(row: ctf.Row) -> str:
    return repr(row.cells[3][:40]) if len(row.cells) > 3 else ""


def normalize_totest(text: str, main_text: str) -> tuple[str, int, int, list[str]]:
    """After the union: close `## Queue` table rows missing their trailing `|`, then drop a row that duplicates a row
    main carries. A row is dropped ONLY when its cells (stripped) equal the kept row's; the kept copy is main's (the
    union puts main's lines after the lane's, so the LAST copy whose cells main also has). A row that shares the
    duplicate key (check_totest_format's: issue + "Do this") but differs in any cell, or a duplicate main does not
    carry, is left alone and reported, so premerge refuses it and the lane reconciles it instead of losing an edit.
    Other lines are byte-identical. Returns (text, rows dropped, rows closed, one message per unresolved pair)."""
    lines = text.splitlines(keepends=True)
    closed = 0
    for row in ctf.parse_rows(text):
        body = lines[row.line_no - 1]
        content = body.splitlines()[0] if body.splitlines() else body
        if not content.rstrip().endswith("|"):
            lines[row.line_no - 1] = content.rstrip() + " |" + body[len(content) :]
            closed += 1
    main_cells = {row.cells for row in ctf.parse_rows(main_text)} if main_text else set()
    by_key: dict[tuple[str, str], list[ctf.Row]] = {}
    for row in ctf.parse_rows("".join(lines)):
        key = ctf.duplicate_key(row)
        if key is not None:
            by_key.setdefault(key, []).append(row)
    drop: set[int] = set()
    problems: list[str] = []
    for group in by_key.values():
        if len(group) < 2:
            continue
        keep = next((r for r in reversed(group) if r.cells in main_cells), None)
        if keep is None:
            first = group[0]
            problems.append(
                f"{TOTEST} has {len(group)} rows for {first.cells[0]} ({_prefix(first)}) and main does not carry them: "
                "reconcile the rows (keep one) before landing"
            )
            continue
        for other in group:
            if other is keep:
                continue
            if other.cells == keep.cells:
                drop.add(other.line_no - 1)
                continue
            differing = [name for name, a, b in zip(ctf.COLUMNS, keep.cells, other.cells, strict=False) if a != b]
            problems.append(
                f"{TOTEST} row {keep.cells[0]} ({_prefix(keep)}) differs from main's copy in: "
                f"{', '.join(differing)}: reconcile the row (keep one) before landing"
            )
    kept = "".join(ln for i, ln in enumerate(lines) if i not in drop)
    return kept, len(drop), closed, problems


def _normalise_union_totest(ctx: Ctx, name: str, merged: bytes, main: bytes) -> bytes:
    """Normalise the union-resolved ToTest.md (see normalize_totest); a file that is not UTF-8 is returned untouched."""
    try:
        text, main_text = merged.decode("utf-8"), main.decode("utf-8")
    except UnicodeDecodeError:
        ctx.say(f"- {name} is not UTF-8: ToTest normalisation skipped, the union is left as git made it")
        return merged
    fixed, dropped, closed, unresolved = normalize_totest(text, main_text)
    for message in unresolved:
        ctx.say(f"WARNING: {message}")
    if not (dropped or closed):
        return merged
    ctx.say(f"- normalised {name}: dropped {dropped} duplicate row(s), closed {closed} row(s) missing a trailing pipe")
    return fixed.encode("utf-8")


def _merge_origin_main(ctx: Ctx, wt: Path) -> None:
    if git(wt, "merge-base", "--is-ancestor", f"{REMOTE}/{BASE}", "HEAD", check=False).returncode == 0:
        return
    ident = _ident(ctx)
    flags, attrs = resw_driver_flags(ctx.repo)
    try:
        merged = git(wt, *ident, *flags, "merge", "--no-edit", f"{REMOTE}/{BASE}", check=False)
    finally:
        attrs.unlink(missing_ok=True)
    if merged.returncode == 0:
        ctx.say(f"- merged {REMOTE}/{BASE} into the branch")
        return
    unmerged = _text(git(wt, "diff", "--name-only", "-z", "--diff-filter=U").stdout.replace(b"\0", b"\n")).splitlines()
    if not unmerged:  # failed for a reason that is not a conflict (identity, hooks, a locked index...)
        git(wt, "merge", "--abort", check=False)
        raise LandError(f"merge failed: {_text(merged.stderr or merged.stdout)}", EXIT_REFUSED)
    union, others = split_conflicts(unmerged)
    if others:
        git(wt, "merge", "--abort", check=False)
        raise LandError(
            f"merge conflict with {REMOTE}/{BASE} in: {', '.join(others)}. "
            "Merge origin/main into the lane branch, resolve these files, and land again",
            EXIT_REFUSED,
        )
    for name in union:
        parts = []
        for stage in (2, 1, 3):  # ours, base, theirs (merge-file order); a missing base (add/add) is empty
            shown = git(wt, "show", f":{stage}:{name}", check=False)
            parts.append(shown.stdout if shown.returncode == 0 else b"")
        with tempfile.TemporaryDirectory() as tmp:
            ours, base, theirs = (Path(tmp) / n for n in ("ours", "base", "theirs"))
            for path, data in zip((ours, base, theirs), parts, strict=True):
                path.write_bytes(data)
            git(wt, "merge-file", "--union", str(ours), str(base), str(theirs), check=False)
            merged_bytes = ours.read_bytes()
        if name == TOTEST:
            merged_bytes = _normalise_union_totest(ctx, name, merged_bytes, parts[2])
        (wt / name).write_bytes(merged_bytes)
        git(wt, "add", "--", name)
        ctx.say(f"- union-resolved {name}")
    git(wt, *ident, "commit", "--no-edit", "-q")


def _push(ctx: Ctx, wt: Path, land_branch: str, sha: str) -> None:
    """Push HEAD. A timed-out push is not a verdict: ask origin whether the branch arrived (a killed git can have finished remotely)."""
    try:
        git(wt, "push", REMOTE, f"HEAD:refs/heads/{land_branch}", timeout=ctx.git_timeout)
        return
    except LandTimeout as err:
        pushed_err = err
    try:
        listing = _text(git(wt, "ls-remote", "--heads", REMOTE, f"refs/heads/{land_branch}", timeout=60.0).stdout)
    except LandError:
        raise LandError(
            f"git push timed out and origin could not be asked; branch may exist on origin: {land_branch}. "
            "Run `git ls-remote --heads origin` to check, delete it if it is wrong, then land again"
        ) from None
    if listing.split()[:1] == [sha]:
        ctx.say(f"- push timed out but origin already has {land_branch} at {sha[:12]}; continuing")
        return
    if listing:
        raise LandError(
            f"push timed out and {land_branch} on origin is at {listing.split()[0][:12]}, not {sha[:12]}. "
            "Check who pushed it, then land again with a different --land-branch"
        ) from pushed_err
    raise LandError(f"push timed out and {land_branch} is not on origin ({pushed_err}). Check the network, then land again") from None


def _create_pr(ctx: Ctx, wt: Path, land_branch: str, title: str, body_file: Path) -> str:
    argv = [*ctx.gh, "pr", "create", "--base", BASE, "--head", land_branch, "--title", title, "--body-file", str(body_file)]
    try:
        proc = run_bounded(argv, cwd=wt, timeout=ctx.gh_timeout)
    except subprocess.TimeoutExpired:
        raise LandError(
            f"{land_branch} was pushed but gh pr create timed out after {int(ctx.gh_timeout)} s. Check the PR list, else open it by hand"
        ) from None
    except OSError as err:  # gh missing or not executable
        raise LandError(f"{land_branch} was pushed but gh could not run ({err}). Install gh or open the PR by hand") from err
    if proc.returncode != 0:
        raise LandError(f"{land_branch} was pushed but gh pr create failed: {_text(proc.stderr or proc.stdout)}. Open the PR by hand")
    return _text(proc.stdout).splitlines()[-1] if proc.stdout.strip() else "(no url printed)"


def _read_checks(ctx: Ctx, pr: str) -> list[dict[str, str]] | None:
    """The PR's checks as `[{"name", "bucket", "state"}]`, or None when gh has none to report yet (it prints
    "no checks reported" and exits 1 right after a PR opens). Exit 8 means "pending" and still carries the JSON."""
    argv = [*ctx.gh, "pr", "checks", pr, "--json", "name,bucket,state,link"]
    try:
        proc = run_bounded(argv, timeout=ctx.gh_timeout)
    except subprocess.TimeoutExpired:
        ctx.checks_error = f"gh pr checks timed out after {int(ctx.gh_timeout)} s"
        return None
    except OSError as err:
        raise LandError(f"gh could not run ({err}). Install gh, then run `gh pr checks {pr} --watch` and merge by hand") from err
    try:
        data = json.loads(_text(proc.stdout))
    except ValueError:
        text = _text(proc.stderr) or _text(proc.stdout)
        # gh's own "no checks reported" is normal right after a PR opens; anything else is gh failing (MEASURED 2026-10-16: PR #811
        # was all green but gh failed with 0xC0000142 on every poll, and that was reported as "no checks were reported").
        ctx.checks_error = "" if "no checks reported" in text else f"rc={proc.returncode}: {text[:200] or '(no output)'}"
        return None
    ctx.checks_error = ""
    return [c for c in data if isinstance(c, dict)] if isinstance(data, list) else None


def _names(checks: Sequence[dict[str, str]]) -> str:
    return ", ".join(str(c.get("name", "?")) for c in checks)


RUN_ID = re.compile(r"/actions/runs/(\d+)")


def _run_ids(checks: Sequence[dict[str, str]]) -> list[str]:
    """The distinct GitHub Actions run ids behind these checks (from their `link`), in first-seen order."""
    ids: list[str] = []
    for c in checks:
        match = RUN_ID.search(str(c.get("link", "")))
        if match and match.group(1) not in ids:
            ids.append(match.group(1))
    return ids


def _log_flaky(ctx: Ctx, pr: str, red: Sequence[dict[str, str]], runs: Sequence[str]) -> None:
    """Append one line to `<git-dir>/flaky-checks.log`: a failed check that a rerun was spent on. Best effort."""
    try:
        git_dir = Path(_text(git(ctx.repo, "rev-parse", "--absolute-git-dir").stdout))
        line = f"{time.strftime('%Y-%m-%dT%H:%M:%S')} pr={pr} checks={_names(red).replace(', ', ',')} runs={','.join(runs)}\n"
        with (git_dir / "flaky-checks.log").open("a", encoding="utf-8", newline="\n") as fh:
            fh.write(line)
    except (LandError, OSError) as err:
        ctx.say(f"WARNING: could not write flaky-checks.log: {err}")


def _rerun_failed(ctx: Ctx, pr: str, red: Sequence[dict[str, str]]) -> bool:
    """`gh run rerun <run> --failed` once for each failed run. False when it could not be done for every run."""
    runs = _run_ids(red)
    if not runs:
        return False
    _log_flaky(ctx, pr, red, runs)
    ok = True
    for run in runs:
        try:
            proc = run_bounded([*ctx.gh, "run", "rerun", run, "--failed"], timeout=ctx.gh_timeout)
        except subprocess.TimeoutExpired:
            ctx.say(f"WARNING: `gh run rerun {run} --failed` timed out after {int(ctx.gh_timeout)} s")
            return False
        except OSError:
            return False
        if proc.returncode != 0:
            ctx.say(f"WARNING: `gh run rerun {run} --failed` failed: {_text(proc.stderr or proc.stdout)}")
            ok = False
    if ok:
        ctx.say(f"- failed check(s) {_names(red)} re-run once (flaky-checks.log); waiting again")
    return ok


def _read_pr_head(ctx: Ctx, pr: str) -> str:
    """The PR's head oid, or "" after HEAD_READ_ATTEMPTS failed reads (non-zero exit, no oid, or a timeout) with a growing backoff."""
    for attempt in range(1, HEAD_READ_ATTEMPTS + 1):
        try:
            view = run_bounded([*ctx.gh, "pr", "view", pr, "--json", "headRefOid"], timeout=ctx.gh_timeout)
        except subprocess.TimeoutExpired:
            view = None
        except OSError as err:
            raise LandError(f"gh could not run ({err}). Merge {pr} by hand with `gh pr merge --merge`") from err
        if view is not None and view.returncode == 0:
            try:
                oid = str(json.loads(_text(view.stdout)).get("headRefOid", ""))
            except (ValueError, AttributeError):
                oid = ""
            if oid:
                return oid
        if attempt < HEAD_READ_ATTEMPTS:
            ctx.sleep(HEAD_READ_BACKOFF_SECONDS * attempt)
    return ""


TEST_PATH_PREFIXES = ("app/tests/", "worker/tests/", "scripts/tests/")


def _is_codeql(check: dict[str, str]) -> bool:
    name = str(check.get("name", "")).strip().lower()
    return name == "codeql" or name.startswith("analyze ")


def _alert_pages(raw: str) -> list[dict[str, Any]]:
    """`gh api --paginate` prints one JSON array per page back to back; decode them all."""
    dec, pos, out = json.JSONDecoder(), 0, []
    raw = raw.strip()
    while pos < len(raw):
        page, pos = dec.raw_decode(raw, pos)
        while pos < len(raw) and raw[pos].isspace():
            pos += 1
        out.extend(a for a in page if isinstance(a, dict)) if isinstance(page, list) else None
    return out


def _report_codeql_alerts(ctx: Ctx, pr: str) -> None:
    """Read-only triage (#920): summarise the PR's open code-scanning alerts after a red CodeQL check. Never dismisses
    anything, never changes the exit code; any failure is one `- could not list` line."""
    number = pr.rstrip("/").rsplit("/", 1)[-1]
    m = re.match(r"https?://[^/]+/([^/]+/[^/]+)/pull/\d+", pr)
    slug = m.group(1) if m else "{owner}/{repo}"
    api = f"repos/{slug}/code-scanning/alerts?ref=refs/pull/{number}/merge&state=open&per_page=100"
    try:
        proc = run_bounded([*ctx.gh, "api", api, "--paginate"], timeout=ctx.gh_timeout)
        if proc.returncode != 0:
            raise ValueError(f"rc={proc.returncode}: {(_text(proc.stderr) or _text(proc.stdout))[:200] or '(no output)'}")
        alerts = _alert_pages(_text(proc.stdout))
    except subprocess.TimeoutExpired:
        ctx.say(f"- could not list CodeQL alerts: gh timed out after {int(ctx.gh_timeout)} s")
        return
    except (OSError, ValueError) as err:
        ctx.say(f"- could not list CodeQL alerts: {' '.join(str(err).split())}")
        return
    rules: dict[str, list[str]] = {}
    paths: list[str] = []
    for a in alerts:
        rule = a.get("rule")
        rid = str(rule.get("id", "?")) if isinstance(rule, dict) else "?"
        inst = a.get("most_recent_instance")
        loc = inst.get("location") if isinstance(inst, dict) else None
        path = str(loc.get("path", "")) if isinstance(loc, dict) else ""
        rules.setdefault(rid, []).append(path)
        paths.append(path)
    parts = []
    for rid, plist in sorted(rules.items(), key=lambda kv: (-len(kv[1]), kv[0])):
        dirs: dict[str, int] = {}
        for p in plist:
            top = p.split("/", 1)[0] if "/" in p else (p or "?")
            dirs[top] = dirs.get(top, 0) + 1
        dtxt = ", ".join(f"{d} {n}" for d, n in sorted(dirs.items(), key=lambda kv: (-kv[1], kv[0])))
        parts.append(f"{rid} {len(plist)} ({dtxt})")
    ctx.say(f"CodeQL: {len(alerts)} open alert(s) on PR {number}: " + ", ".join(parts))
    if alerts:
        if all(p.startswith(TEST_PATH_PREFIXES) for p in paths):
            ctx.say("- every alert is under a test path (app/tests/, worker/tests/, scripts/tests/)")
        else:
            ctx.say("- not all under a test path (app/tests/, worker/tests/, scripts/tests/)")


def _merge_when_green(ctx: Ctx, pr: str, pushed_head: str, timeout: float, poll: float, rerun_failed_once: bool = False) -> int:
    """Wait for every check on `pr`, then merge. Never merges unless at least one check exists and ALL are green.

    With `rerun_failed_once`, the first red result (once no check is still running, because `gh run rerun --failed`
    needs a finished run) re-runs the failed runs a single time and waits again with a fresh deadline; a second red refuses."""
    deadline = ctx.monotonic() + timeout
    rerun_spent = False
    gh_warned = False
    while True:
        checks = _read_checks(ctx, pr)
        if checks is None and ctx.checks_error and not gh_warned:
            gh_warned = True
            ctx.say(f"WARNING: gh pr checks failed ({ctx.checks_error}); still polling")
        waiting: list[dict[str, str]] = []
        if checks:
            red = [c for c in checks if c.get("bucket") in RED_BUCKETS]
            waiting = [c for c in checks if c.get("bucket") not in GREEN_BUCKETS and c not in red]
            if red and rerun_failed_once and not rerun_spent and waiting:
                pass  # a rerun needs the run finished: keep polling until the rest settle (still bounded by the deadline)
            elif red and rerun_failed_once and not rerun_spent:
                rerun_spent = True
                if _rerun_failed(ctx, pr, red):
                    deadline = ctx.monotonic() + timeout
                    ctx.sleep(poll)  # let the rerun replace the stale red before it is read again
                    continue
                ctx.say(f"ERROR: not merging {pr}: failed check(s) {_names(red)} could not be re-run. Re-run them by hand; the PR stays open")
                return EXIT_CHECKS_RED
            elif red:
                again = " (still red after one re-run)" if rerun_spent else ""
                ctx.say(f"ERROR: not merging {pr}: failed check(s){again}: {_names(red)}. Fix them and push; the PR stays open")
                if any(_is_codeql(c) for c in red):
                    _report_codeql_alerts(ctx, pr)
                return EXIT_CHECKS_RED
            if not waiting and not red:
                break
        if ctx.monotonic() >= deadline:
            if waiting:
                what = f"still pending: {_names(waiting)}"
            elif ctx.checks_error:
                what = f"gh could not read the checks ({ctx.checks_error})"
            else:
                what = "no checks were reported"
            ctx.say(
                f"ERROR: not merging {pr}: {what} after {int(timeout)} s. Run `gh pr checks {pr} --watch`, then merge by hand; the PR stays open"
            )
            return EXIT_CHECKS_PENDING
        ctx.sleep(min(poll, deadline - ctx.monotonic()))
    # Never merge a head other than the one that was verified and pushed: read the PR's head now and pin the merge to it.
    current = _read_pr_head(ctx, pr)
    if not current:
        ctx.say(
            f"ERROR: not merging {pr}: gh could not read the PR head after {HEAD_READ_ATTEMPTS} tries. Merge {pr} by hand; the PR stays open"
        )
        return EXIT_CHECKS_PENDING
    if current != pushed_head:
        ctx.say(
            f"ERROR: not merging {pr}: its head changed since it was verified and pushed ({pushed_head[:12]} -> {current[:12]}). "
            "Check who pushed to it; the PR stays open"
        )
        return EXIT_CHECKS_RED
    argv = [*ctx.gh, "pr", "merge", pr, "--merge", "--match-head-commit", pushed_head]
    try:
        proc = run_bounded(argv, timeout=ctx.gh_timeout)
    except subprocess.TimeoutExpired:
        raise LandError(f"gh pr merge timed out after {int(ctx.gh_timeout)} s. Check whether {pr} merged, then merge it by hand") from None
    except OSError as err:
        raise LandError(f"gh could not run ({err}). Merge {pr} by hand with `gh pr merge --merge`") from err
    if proc.returncode != 0:
        raise LandError(f"all checks passed but `gh pr merge` failed: {_text(proc.stderr or proc.stdout)}. Merge {pr} by hand")
    ctx.say(f"OK: all checks passed; merged {pr}")
    return EXIT_OK


def land(
    ctx: Ctx,
    branch: str,
    sha: str,
    title: str,
    body_file: Path,
    *,
    full: bool = False,
    land_branch: str | None = None,
    merge_when_green: bool = False,
    checks_timeout: float = CHECKS_TIMEOUT_SECONDS,
    checks_poll: float = CHECKS_POLL_SECONDS,
    rerun_failed_once: bool = True,
) -> int:
    land_branch = land_branch if land_branch is not None else branch
    slug = slugify(branch)
    wt: Path | None = None
    pushed = False
    rc = EXIT_FAILED
    try:
        check_branch_name("--branch", branch)
        check_branch_name("--land-branch", land_branch)
        git(ctx.repo, "fetch", "-q", REMOTE)
        full_sha = _resolve_tip(ctx, branch, sha)
        bad = find_control_bytes(_changed_text_files(ctx, full_sha))
        if bad:
            raise LandError(
                f"control byte(s) in: {', '.join(bad)}. A backslash escape was probably mangled "
                "on write; fix those files in the lane and land again",
                EXIT_REFUSED,
            )
        ctx.tmp_root.mkdir(parents=True, exist_ok=True)
        _clean_stale(ctx, slug)
        wt = ctx.tmp_root / f"{slug}-{time.strftime('%Y%m%d%H%M%S')}"
        _write_lock(ctx, wt.name, full_sha)
        git(ctx.repo, "worktree", "add", "-q", "--detach", str(wt), full_sha)
        make_dir_link(wt / VENV_REL, ctx.venv)
        _merge_origin_main(ctx, wt)
        code, output = ctx.premerge(wt, ctx.venv, full)
        if code != 0:
            log = ctx.tmp_root / "logs" / f"{wt.name}.premerge.log"
            log.parent.mkdir(exist_ok=True)
            log.write_text(output, encoding="utf-8", newline="\n")
            fails = [ln for ln in output.splitlines() if re.search(r"\b(FAIL|ERROR)\b", ln)][:30]
            raise LandError("premerge is red; nothing was pushed. " + "; ".join(s.strip() for s in fails) + f" (full log: {log})", EXIT_RED)
        pushed_head = _text(git(wt, "rev-parse", "HEAD").stdout)
        _push(ctx, wt, land_branch, pushed_head)
        pushed = True
        url = _create_pr(ctx, wt, land_branch, title, body_file)
        _record_landed(ctx, url, pushed_head)  # the PR head the hook must allow, not the pre-merge lane tip (#904)
        if merge_when_green:
            ctx.say(f"OK: pushed {land_branch} and opened {url}; waiting for its checks")
            rc = _merge_when_green(ctx, url, pushed_head, checks_timeout, checks_poll, rerun_failed_once)
        else:
            ctx.say(f"OK: pushed {land_branch} and opened {url}. Merge with: gh pr merge {url.rsplit('/', 1)[-1]} --merge")
            rc = EXIT_OK
    except LandError as err:
        ctx.say(f"ERROR: {err}")
        rc = err.code
    except Exception as err:  # noqa: BLE001  (anything unexpected must still clean up and exit non-zero)
        after = f"; {land_branch} was already pushed, open the PR by hand" if pushed else ""
        ctx.say(f"ERROR: unexpected {type(err).__name__}: {err}{after}")
        rc = EXIT_FAILED
    finally:
        # A cleanup failure never rewrites the outcome: it is a warning that names what was left behind.
        if wt is not None and wt.exists():
            try:
                _remove_worktree(ctx, wt)
            except LandError as err:
                ctx.say(f"WARNING: left {wt} in place: {err}. Unlink its worker\\.venv with rmdir, then `git worktree remove --force` it")
        if wt is not None and not wt.exists():
            _lock_path(ctx, wt.name).unlink(missing_ok=True)
    return rc


def _record_landed(ctx: Ctx, url: str, sha: str) -> None:
    """Tell the `gh pr merge` hook (#451) this PR passed premerge in the throwaway worktree. Advisory only."""
    pr = url.rsplit("/", 1)[-1].strip()
    if not pr.isdigit():
        return
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import premerge_stamp

        premerge_stamp.record_landed(ctx.repo, pr, sha)
    except Exception as err:  # noqa: BLE001  (a missing marker only makes the hook ask for premerge by hand)
        ctx.say(f"WARNING: could not record the landed PR for the merge hook ({err})")


def _default_venv(repo: Path) -> Path:
    if (Path(sys.prefix) / "pyvenv.cfg").is_file():
        return Path(sys.prefix)
    return repo / VENV_REL


def main(argv: Sequence[str] | None = None, *, repo: Path | None = None, premerge: PremergeFn | None = None) -> int:
    repo = repo or Path(__file__).resolve().parent.parent
    ap = argparse.ArgumentParser(description="Land a committed worktree branch: verify in a throwaway worktree, then PR.")
    ap.add_argument("--branch", required=True)
    ap.add_argument("--sha", required=True, help="must be the branch tip")
    ap.add_argument("--title", required=True)
    ap.add_argument("--body-file", required=True, type=Path)
    ap.add_argument("--full", action="store_true", help="full premerge (default --fast)")
    ap.add_argument("--land-branch", default=None, help="remote branch to push (default: --branch)")
    ap.add_argument(
        "--merge-when-green", action="store_true", help="after the PR opens, wait for every check and merge (merge commit) only if none failed"
    )
    ap.add_argument(
        "--checks-timeout", type=float, default=CHECKS_TIMEOUT_SECONDS, help="seconds to wait for checks before refusing (default 1800)"
    )
    ap.add_argument("--checks-poll", type=float, default=CHECKS_POLL_SECONDS, help="seconds between check polls (default 20)")
    ap.add_argument(
        "--rerun-failed-once",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="with --merge-when-green: re-run failed checks once (flaky runners) before refusing; --no-rerun-failed-once disables",
    )
    ap.add_argument(
        "--premerge-timeout",
        type=float,
        default=PREMERGE_TIMEOUT_SECONDS,
        help="seconds before a hung premerge (heavy.py slot wait included) is killed and refused (default 7200)",
    )
    ap.add_argument("--venv", type=Path, default=None, help="real venv to junction (default: the running venv)")
    ap.add_argument("--gh", default=os.environ.get("LAND_WT_GH", "gh"), help="path of the gh executable (one path, spaces allowed)")
    args = ap.parse_args(argv)
    if not args.body_file.is_file():
        print(f"ERROR: --body-file {args.body_file} does not exist")
        return EXIT_USAGE
    ctx = Ctx(repo=repo, venv=args.venv or _default_venv(repo), gh=[args.gh])
    ctx.premerge = premerge if premerge is not None else partial(run_premerge, timeout=args.premerge_timeout)
    return land(
        ctx,
        args.branch,
        args.sha,
        args.title,
        args.body_file.resolve(),
        full=args.full,
        land_branch=args.land_branch,
        merge_when_green=args.merge_when_green,
        checks_timeout=args.checks_timeout,
        checks_poll=args.checks_poll,
        rerun_failed_once=args.rerun_failed_once,
    )


if __name__ == "__main__":
    sys.exit(main())
