"""scripts/heavy.py -- a machine-wide slot lock for heavy commands (#487).

    python scripts/heavy.py [--slots N] [--lane NAME] -- <cmd...>
    python scripts/heavy.py --lane mylane -- dotnet test tests/X/X.csproj     (cwd app/)
    python scripts/heavy.py --set-slots N      (the machine-wide limit, stored in <lock-dir>/slots.txt)
    python scripts/heavy.py -- worker\\.venv\\Scripts\\python.exe -m pytest worker/tests -m "not gpu"

Why: several agents running full suites in one shared checkout froze the laptop once, and two
concurrent `dotnet build`s of overlapping projects fight over the shared obj/ and bin/. This takes
one of N slots before running <cmd>, so at most N heavy commands run at once, and for
`dotnet build|test|run|pack|publish` it adds `--artifacts-path <repo>/app/.artifacts/<lane>` unless one is
already given, so lanes never share obj/. --lane is required for those (no shared default lane). The
folder is inside the repo because tests that find the repo by walking up from the test binary fail under
%TEMP%. (`dotnet format` is left alone: it takes no such option.)

N is: --slots flag (tests, one-offs) > <lock-dir>/slots.txt (set by --set-slots) > $HEAVY_SLOTS > 2.

How: slot files `slot-0.lock` .. `slot-{N-1}.lock` under `%TEMP%\\deg-heavy\\` (override with
--lock-dir or $HEAVY_LOCK_DIR), each locked non-blocking with msvcrt.locking (Windows) or
fcntl.flock (elsewhere). The OS drops the lock when the holder dies, however it dies, so a killed
agent frees its slot with no cleanup. The lock is per user (it lives in that user's %TEMP%). A holder
also writes slot-N.info (pid, lane, start, command head); while every slot is busy the one ASCII "waiting"
line names the holders whose lock is held right now and says to stop a stuck one by PID, then it polls.

The child's stdout/stderr are inherited (a long run streams). Its exit code is this script's exit
code. Ctrl-C ends the command's whole tree (its own job object, then taskkill /T; #930) and exits 130. `--status` lists the
slot holders (#996). Every `heavy:` diagnostic (waiting, still queued, note, warning) goes to stderr; stdout is the command's own.
`--status` and the waiting line probe a slot by briefly taking its lock, so a launch racing a probe can print one spurious waiting
line, and a slot's .info can be stale for the moment between taking the lock and writing it (the row then shows the previous
holder or `?`). A snapshot, not a transaction; no redesign planned. Exit 2 is
a usage error; 127 means the command could not be started.

Hard kill: on Windows the wrapper puts itself in a job object with KILL_ON_JOB_CLOSE before it starts
the child, so a hard-killed wrapper (taskkill /F, an agent's tool timeout) takes the child and its
descendants with it and the slot is never freed while work still runs. If the job cannot be set up
(a parent job forbids it) one `heavy: note:` line is printed and the run continues without it. On
other platforms there is no such guarantee: a SIGKILLed wrapper frees the slot while its child may
keep running, uncounted. The same job also ends any process the command left behind when the wrapper
exits, so for dotnet commands the child env defaults MSBUILDDISABLENODEREUSE=1,
DOTNET_CLI_USE_MSBUILD_SERVER=0 and UseSharedCompilation=false (a caller-set value wins): no MSBuild node
or compiler server outlives a build for another lane to connect to and lose.
"""

from __future__ import annotations

import argparse
import contextlib
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)
import wt_venv  # noqa: E402  (the one kill-on-close job helper, #958; heavy.py is run by path, so its folder is on sys.path already)
from dotnet_env import DOTNET_ENV_DEFAULTS, _is_dotnet, dotnet_env  # noqa: E402,F401  (the one definition, shared with premerge.py)

DEFAULT_SLOTS = 2
REPO_ROOT = Path(__file__).resolve().parents[1]
LANE_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}")
DEFAULT_POLL = 1.0
SLN_NAME = "DnaEntropyGraph.sln"
DOTNET_VERBS = frozenset({"build", "test", "run", "pack", "publish"})

IS_WINDOWS = sys.platform == "win32"
if IS_WINDOWS:
    import msvcrt
else:
    import fcntl


def default_lock_dir() -> Path:
    env = os.environ.get("HEAVY_LOCK_DIR")
    return Path(env) if env else Path(tempfile.gettempdir()) / "deg-heavy"


def repo_toplevel(cwd: str | os.PathLike[str] | None = None) -> Path | None:
    """The git toplevel of `cwd` (default: the current directory), or None outside any repo / without git.

    In a linked worktree this is the worktree root, not the primary checkout, which is the point (#874).
    """
    try:
        out = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            cwd=str(cwd) if cwd is not None else None,
            capture_output=True,
            text=True,
            check=False,
            timeout=20,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    top = out.stdout.strip()
    if out.returncode != 0 or not top:
        return None
    return Path(top)


def default_artifacts_base(cwd: str | os.PathLike[str] | None = None) -> str:
    """`<repo>/app/.artifacts`, where <repo> is the git toplevel of the CWD (the checkout the command runs
    in), falling back (with a stderr warning naming both paths) to this script's own repo
    when the CWD is not inside a repo that has app/DnaEntropyGraph.sln. Worktree lanes run the
    PRIMARY's heavy.py, so deriving it from __file__ put their build output under the primary's app/, and
    Guards.Tests then scanned the primary's tree (#874).

    Not %TEMP%: tests that find the repo by walking up from AppContext.BaseDirectory (Cloud.Tests
    FixturePaths, Core.Tests StartupMetadataTests) fail with the output outside the repo. Its bin/ obj/
    publish/ subfolders are gitignored by `app/**/bin/` etc."""
    top = repo_toplevel(cwd)
    if top is not None and (top / "app" / SLN_NAME).is_file():
        return str(top / "app" / ".artifacts")
    why = "the CWD is not inside a git repo" if top is None else f"{top} has no app/{SLN_NAME}"
    print(
        f"heavy: warning: {why}; building under heavy.py's own repo {REPO_ROOT} "
        "(a worktree lane's build then lands in the wrong tree; run from inside your checkout)",
        file=sys.stderr,
        flush=True,
    )
    return str(REPO_ROOT / "app" / ".artifacts")


def _positive_int(raw: str) -> int | None:
    try:
        value = int(raw.strip())
    except ValueError:
        return None
    return value if value >= 1 else None


def resolve_slots(flag: int | None, lock_dir: Path | None = None) -> int:
    """--slots flag (tests, one-offs) > `<lock_dir>/slots.txt` (set by --set-slots) > $HEAVY_SLOTS > 2.

    The file is the one place the machine limit lives, so callers that disagree on N cannot
    run more than the file allows. A missing, junk or non-positive value falls through.
    """
    if flag is not None:
        return flag
    if lock_dir is not None:
        try:
            from_file = _positive_int((lock_dir / "slots.txt").read_text(encoding="utf-8"))
        except OSError:
            from_file = None
        if from_file is not None:
            return from_file
    from_env = _positive_int(os.environ.get("HEAVY_SLOTS", ""))
    return from_env if from_env is not None else DEFAULT_SLOTS


def _has_artifacts_path(argv: list[str]) -> bool:
    return any(a == "--artifacts-path" or a.startswith("--artifacts-path=") for a in argv[2:])


def needs_lane(argv: list[str]) -> bool:
    """True for a dotnet build|test|run|pack|publish that has no --artifacts-path of its own.

    Such a command gets one injected, so it must say which lane it is: a shared default lane
    would put two agents back in the same obj/.
    """
    return len(argv) >= 2 and _is_dotnet(argv) and argv[1].lower() in DOTNET_VERBS and not _has_artifacts_path(argv)


def inject_artifacts_path(argv: list[str], lane: str, base: str) -> list[str]:
    """Append `--artifacts-path <base>/<lane>` to a dotnet build|test|run|pack|publish argv.

    Pure. Returns argv unchanged for anything else, or when an --artifacts-path (either
    `--artifacts-path X` or `--artifacts-path=X`) is already present. Inserted before a
    `--` separator so `dotnet run ... -- app args` still reaches the app unchanged.
    """
    if len(argv) < 2:
        return argv
    if not needs_lane(argv):
        return argv
    extra = ["--artifacts-path", str(Path(base) / lane)]
    if "--" in argv[2:]:
        i = argv.index("--", 2)
        return argv[:i] + extra + argv[i:]
    return argv + extra


_JOB_HANDLE: wt_venv.KillJob | None = None  # kept open for the process lifetime: closing it is what kills the group


def kill_children_with_me() -> str | None:
    """Windows: join a kill-on-close job object (the shared `wt_venv.KillJob`, #958/#930). Returns None on success, else a short ASCII reason."""
    global _JOB_HANDLE
    if not IS_WINDOWS:
        return None
    try:
        job = wt_venv.create_kill_job()
        if job is None:
            code = wt_venv.last_job_error()
            return f"CreateJobObject/SetInformationJobObject failed, error {code}" if code else "CreateJobObject/SetInformationJobObject failed"
        if not job.assign_self():
            job.close()
            return "AssignProcessToJobObject failed"
        _JOB_HANDLE = job
        return None
    except Exception as e:  # never let the safety net stop the run
        return f"{type(e).__name__}"


def resolve_executable(command: list[str]) -> list[str]:
    """Resolve command[0] the way a shell would (PATH, PATHEXT, relative paths with `/`).

    MEASURED 2026-10-02: on Windows `subprocess.Popen(["worker/.venv/Scripts/python.exe"])` raises
    WinError 2 although the file exists (backslashes and `./` work), and a bare `npm` shim needs PATHEXT.
    """
    found = shutil.which(command[0])
    return [found, *command[1:]] if found else command


class Slot:
    """A held slot. Release is optional: process exit releases it too."""

    def __init__(self, fh, index: int, lock_dir: Path | None = None):
        self._fh = fh
        self.index = index
        self._lock_dir = lock_dir

    def release(self) -> None:
        fh, self._fh = self._fh, None
        if fh is None:
            return
        if self._lock_dir is not None:  # before unlocking, so a new holder's info is never deleted
            with contextlib.suppress(OSError):
                (self._lock_dir / f"slot-{self.index}.info").unlink()
        with contextlib.suppress(OSError):
            if IS_WINDOWS:
                fh.seek(0)
                msvcrt.locking(fh.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(fh.fileno(), fcntl.LOCK_UN)
        fh.close()


def _try_lock(fh) -> bool:
    try:
        if IS_WINDOWS:
            fh.seek(0)
            msvcrt.locking(fh.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            fcntl.flock(fh.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        return False
    return True


def try_acquire(lock_dir: Path, slots: int) -> Slot | None:
    """Take the first free slot without blocking, or None when all are held."""
    lock_dir.mkdir(parents=True, exist_ok=True)
    for i in range(slots):
        fh = open(lock_dir / f"slot-{i}.lock", "a+b")  # noqa: SIM115 (held for the process lifetime)
        if _try_lock(fh):
            return Slot(fh, i, lock_dir)
        fh.close()
    return None


def write_info(lock_dir: Path, index: int, lane: str, command: list[str]) -> None:
    """Record who holds slot `index` (ASCII, one key=value per line). Best effort."""
    cmd = ascii(" ".join(command))[1:-1][:80]
    text = f"pid={os.getpid()}\nlane={lane}\nstart={time.strftime('%Y-%m-%dT%H:%M:%S')}\ncmd={cmd}\n"
    with contextlib.suppress(OSError):
        (lock_dir / f"slot-{index}.info").write_text(text, encoding="ascii", newline="\n")


def _read_info(path: Path) -> dict[str, str]:
    info: dict[str, str] = {}
    with contextlib.suppress(OSError, UnicodeDecodeError):
        for line in path.read_text(encoding="ascii").splitlines():
            key, sep, value = line.partition("=")
            if sep:
                info[key] = value
    return info


def holders(lock_dir: Path, slots: int) -> list[dict[str, str]]:
    """The holders of the slots whose lock is held RIGHT NOW. A leftover .info next to a free
    slot (its holder was killed) is ignored. A held slot with no readable info is listed as pid '?'."""
    found: list[dict[str, str]] = []
    for i in range(slots):
        lock_file = lock_dir / f"slot-{i}.lock"
        if not lock_file.exists():
            continue
        try:
            fh = open(lock_file, "a+b")  # noqa: SIM115
        except OSError:
            continue
        try:
            if _try_lock(fh):
                if IS_WINDOWS:
                    fh.seek(0)
                    msvcrt.locking(fh.fileno(), msvcrt.LK_UNLCK, 1)
                continue  # free: not a holder
        finally:
            fh.close()
        info = _read_info(lock_dir / f"slot-{i}.info")
        found.append(
            {
                "slot": str(i),
                "pid": info.get("pid", "?"),
                "lane": info.get("lane", "?"),
                "cmd": info.get("cmd", ""),
                "start": info.get("start", ""),
            }
        )
    return found


# --- status: who holds the slots, how long, and what the tree has burned (#996) -------------------


def _win_k32():
    import ctypes
    from ctypes import wintypes

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    k32.OpenProcess.restype = wintypes.HANDLE
    k32.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    k32.GetExitCodeProcess.restype = wintypes.BOOL
    k32.GetProcessTimes.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    k32.GetProcessTimes.restype = wintypes.BOOL
    k32.CloseHandle.argtypes = [wintypes.HANDLE]
    k32.CloseHandle.restype = wintypes.BOOL
    k32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    k32.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
    return k32


def _win_process_table() -> dict[int, int]:
    """pid -> parent pid for every process, from a Toolhelp32 snapshot. Empty when the snapshot fails."""
    import ctypes
    from ctypes import wintypes

    class ProcessEntry(ctypes.Structure):
        _fields_ = (
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ProcessID", wintypes.DWORD),
            ("th32DefaultHeapID", ctypes.c_size_t),
            ("th32ModuleID", wintypes.DWORD),
            ("cntThreads", wintypes.DWORD),
            ("th32ParentProcessID", wintypes.DWORD),
            ("pcPriClassBase", wintypes.LONG),
            ("dwFlags", wintypes.DWORD),
            ("szExeFile", ctypes.c_wchar * 260),
        )

    k32 = _win_k32()
    k32.Process32FirstW.argtypes = [ctypes.c_void_p, ctypes.POINTER(ProcessEntry)]
    k32.Process32FirstW.restype = wintypes.BOOL
    k32.Process32NextW.argtypes = [ctypes.c_void_p, ctypes.POINTER(ProcessEntry)]
    k32.Process32NextW.restype = wintypes.BOOL
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)  # TH32CS_SNAPPROCESS
    if snap is None or snap == ctypes.c_void_p(-1).value:
        return {}
    table: dict[int, int] = {}
    try:
        entry = ProcessEntry()
        entry.dwSize = ctypes.sizeof(ProcessEntry)
        more = k32.Process32FirstW(snap, ctypes.byref(entry))
        while more:
            table[entry.th32ProcessID] = entry.th32ParentProcessID
            more = k32.Process32NextW(snap, ctypes.byref(entry))
    finally:
        k32.CloseHandle(snap)
    return table


def _win_cpu_seconds(pid: int) -> float | None:
    import ctypes
    from ctypes import wintypes

    k32 = _win_k32()
    handle = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        times = [wintypes.FILETIME() for _ in range(4)]
        if not k32.GetProcessTimes(handle, *(ctypes.byref(t) for t in times)):
            return None
        kernel, user = times[2], times[3]
        ticks = ((kernel.dwHighDateTime << 32) | kernel.dwLowDateTime) + ((user.dwHighDateTime << 32) | user.dwLowDateTime)
        return ticks / 1e7  # 100 ns units
    finally:
        k32.CloseHandle(handle)


def pid_alive(pid: int) -> bool:
    """True while a process with this pid is running (not merely a handle someone still holds)."""
    if IS_WINDOWS:
        import ctypes
        from ctypes import wintypes

        k32 = _win_k32()
        handle = k32.OpenProcess(0x1000, False, pid)
        if not handle:
            return ctypes.get_last_error() == 5  # ERROR_ACCESS_DENIED: it exists, we may not query it (87 = no such pid)
        try:
            code = wintypes.DWORD()
            return bool(k32.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == 259  # STILL_ACTIVE
        finally:
            k32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def _posix_process_table() -> dict[int, tuple[int, float]]:
    """pid -> (parent pid, cpu seconds) from /proc. Empty where /proc is absent."""
    table: dict[int, tuple[int, float]] = {}
    ticks = os.sysconf("SC_CLK_TCK") if hasattr(os, "sysconf") else 100
    for entry in Path("/proc").glob("[0-9]*"):
        with contextlib.suppress(OSError, ValueError, IndexError):
            fields = (entry / "stat").read_text(encoding="utf-8", errors="replace").rpartition(")")[2].split()
            table[int(entry.name)] = (int(fields[1]), (int(fields[11]) + int(fields[12])) / ticks)
    return table


def _descendants(root: int, parents: dict[int, int]) -> set[int]:
    members = {root}
    grew = True
    while grew:  # a parent can appear after its child in the table, so repeat until nothing new joins
        grew = False
        for child, parent in parents.items():
            if parent in members and child not in members:
                members.add(child)
                grew = True
    return members


def tree_cpu_seconds(pid: int) -> float | None:
    """Cumulative CPU seconds (user + kernel) of `pid` and every descendant still running, or None when `pid` is not running.
    Exited descendants are not counted (their times are gone), so this is a lower bound for a long run."""
    if not pid_alive(pid):
        return None
    if IS_WINDOWS:
        times = [_win_cpu_seconds(p) for p in _descendants(pid, _win_process_table())]
        return sum(t for t in times if t is not None)
    table = _posix_process_table()
    if pid not in table:
        return None
    return sum(table[p][1] for p in _descendants(pid, {c: v[0] for c, v in table.items()}))


def _age_seconds(start: str, now: float | None = None) -> float | None:
    """Seconds since a `write_info` start stamp (local time), or None when the stamp does not parse."""
    try:
        began = time.mktime(time.strptime(start, "%Y-%m-%dT%H:%M:%S"))
    except (ValueError, OverflowError):
        return None
    return max(0.0, (time.time() if now is None else now) - began)


def _fmt_age(seconds: float | None) -> str:
    if seconds is None:
        return "?"
    whole = int(seconds)
    return f"{whole // 60}m{whole % 60:02d}s" if whole >= 60 else f"{whole}s"


def slot_status(lock_dir: Path, slots: int) -> list[dict]:
    """One row per slot: state free|held, and for a held slot pid, lane, start, age_s, alive, cpu_s (the holder's process tree)."""
    by_slot = {int(h["slot"]): h for h in holders(lock_dir, slots)}
    rows: list[dict] = []
    for i in range(slots):
        held = by_slot.get(i)
        if held is None:
            rows.append({"slot": str(i), "state": "free"})
            continue
        start = _read_info(lock_dir / f"slot-{i}.info").get("start", "?")
        pid = int(held["pid"]) if held["pid"].isdigit() else None
        alive = pid_alive(pid) if pid is not None else None
        rows.append(
            {
                "slot": str(i),
                "state": "held",
                "pid": pid,
                "lane": held["lane"],
                "start": start,
                "age_s": _age_seconds(start),
                "alive": alive,
                "cpu_s": tree_cpu_seconds(pid) if pid is not None and alive else None,
                "cmd": held["cmd"],
            }
        )
    return rows


def format_status(rows: list[dict]) -> list[str]:
    lines: list[str] = []
    for row in rows:
        if row["state"] == "free":
            lines.append(f"slot {row['slot']}: free")
            continue
        alive = {True: "yes", False: "NO", None: "?"}[row["alive"]]
        cpu = "?" if row["cpu_s"] is None else f"{row['cpu_s']:.1f}s"
        pid = "?" if row["pid"] is None else row["pid"]
        age = "?" if row["age_s"] is None else f"{int(row['age_s'])}s"
        lines.append(
            f"slot {row['slot']}: held lane {row['lane']} pid {pid} start {row['start']} age {age} alive {alive} cpu {cpu} cmd {row['cmd']}"
        )
    return lines


def _holder_phrase(held: list[dict[str, str]]) -> str:
    parts = []
    for h in held:
        age = _age_seconds(h.get("start", ""))
        parts.append(f"lane {h['lane']} pid {h['pid']}" + ("" if age is None else f" for {_fmt_age(age)}"))
    return ", ".join(parts) or "unknown"


def describe_wait(slots: int, held: list[dict[str, str]]) -> str:
    return (
        f"heavy: waiting for a free slot (all {slots} busy; held by {_holder_phrase(held)}). "
        "To free a stuck one, stop that process (taskkill /F /PID <pid>); `heavy.py --status` shows who holds what."
    )


def describe_still_waiting(held: list[dict[str, str]], waited: float) -> str:
    return f"heavy: still queued after {_fmt_age(waited)} (held by {_holder_phrase(held)})"


TICK_SECONDS = 60.0


def acquire_slot(lock_dir: Path, slots: int, poll: float, on_wait, on_tick=None, tick_interval: float = TICK_SECONDS) -> Slot:
    """Block until a slot is free; call on_wait(holders) once, the first time every slot is busy, then
    on_tick(holders, seconds_waited) every `tick_interval` seconds while still queued (#996)."""
    announced = False
    began = time.monotonic()
    next_tick = tick_interval
    while True:
        slot = try_acquire(lock_dir, slots)
        if slot is not None:
            return slot
        if not announced:
            on_wait(holders(lock_dir, slots))
            announced = True
        waited = time.monotonic() - began
        if on_tick is not None and waited >= next_tick:
            on_tick(holders(lock_dir, slots), waited)
            next_tick = waited + tick_interval
        time.sleep(poll)


def _wait_child(proc: subprocess.Popen[bytes]) -> int:
    return proc.wait()  # a seam: tests replace it to simulate Ctrl-C


def _kill_child_tree(proc: subprocess.Popen[bytes], job: wt_venv.KillJob | None) -> None:
    """Ctrl-C: end the command's whole tree (#930), including a grandchild whose parent already exited. The job first
    (Windows); taskkill /T by PID or the process group only when there was no job or it did not accept the call.
    Each step is guarded on its own, so one failing never skips the next. Never raises."""
    terminated = False
    if job is not None:
        with contextlib.suppress(Exception):
            terminated = job.terminate()
    if not terminated:
        with contextlib.suppress(Exception):
            wt_venv.kill_tree(proc)
    with contextlib.suppress(Exception):
        proc.wait(timeout=10)


def _split_command(argv: list[str]) -> tuple[list[str], list[str]]:
    if "--" in argv:
        i = argv.index("--")
        return argv[:i], argv[i + 1 :]
    return argv, []


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    own, command = _split_command(argv)
    parser = argparse.ArgumentParser(prog="heavy.py", description="Run a heavy command under a machine-wide slot lock.")
    parser.add_argument(
        "--slots", type=int, default=None, help="concurrent heavy commands for THIS call only (tests, one-offs); overrides slots.txt"
    )
    parser.add_argument(
        "--set-slots", type=int, default=None, metavar="N", help="record the machine-wide limit in <lock-dir>/slots.txt and exit"
    )
    parser.add_argument(
        "--status",
        action="store_true",
        help="list each slot's holder (pid, lane, start, age, alive, tree CPU) and exit 0; a snapshot, see the docstring",
    )
    parser.add_argument("--lane", default=None, help="required for dotnet build|test|run|pack|publish: names the --artifacts-path dir")
    parser.add_argument("--lock-dir", type=Path, default=None, help="slot directory (default $HEAVY_LOCK_DIR or %%TEMP%%\\deg-heavy)")
    parser.add_argument("--poll", type=float, default=DEFAULT_POLL, help="seconds between slot checks while waiting")
    try:
        args = parser.parse_args(own)
    except SystemExit as e:
        return int(e.code) if isinstance(e.code, int) else 2
    lock_dir = args.lock_dir or default_lock_dir()
    if args.set_slots is not None:
        if args.set_slots < 1:
            print("ERROR: --set-slots must be at least 1. Pass a whole number such as --set-slots 2.", file=sys.stderr)
            return 2
        try:
            lock_dir.mkdir(parents=True, exist_ok=True)
            (lock_dir / "slots.txt").write_text(f"{args.set_slots}\n", encoding="utf-8", newline="\n")
        except OSError as e:
            print(
                f"ERROR: could not write slots.txt in {ascii(str(lock_dir))} ({type(e).__name__}). Pass --lock-dir with a writable folder.",
                file=sys.stderr,
            )
            return 2
        print(f"OK: machine-wide slots set to {args.set_slots} in {ascii(str(lock_dir))}")
        return 0
    if args.status:
        for line in format_status(slot_status(lock_dir, resolve_slots(args.slots, lock_dir))):
            print(line)
        return 0
    if not command:
        print("ERROR: no command given. Usage: heavy.py [--slots N] [--lane NAME] -- <cmd...>", file=sys.stderr)
        return 2
    slots = resolve_slots(args.slots, lock_dir)
    if slots < 1:
        print("ERROR: --slots must be at least 1.", file=sys.stderr)
        return 2
    if needs_lane(command):
        if not args.lane:
            print(
                "ERROR: a dotnet build or test needs its own build folder. Add --lane <name> (any short name, one per agent).", file=sys.stderr
            )
            return 2
        if not LANE_RE.fullmatch(args.lane):
            print("ERROR: --lane may use only letters, digits, dot, dash and underscore (start with a letter or digit).", file=sys.stderr)
            return 2
    lane = args.lane or "-"
    command = inject_artifacts_path(command, lane, default_artifacts_base())

    slot = None
    proc = None
    child_job: wt_venv.KillJob | None = None
    job_problem = kill_children_with_me()
    if job_problem:
        print(f"heavy: note: no kill-on-close job ({job_problem}); a hard-killed wrapper will not stop its child", file=sys.stderr, flush=True)
    try:
        try:
            slot = acquire_slot(
                lock_dir,
                slots,
                args.poll,
                lambda held: print(describe_wait(slots, held), file=sys.stderr, flush=True),
                lambda held, waited: print(describe_still_waiting(held, waited), file=sys.stderr, flush=True),
            )
        except OSError as e:
            print(
                f"ERROR: cannot use the lock folder {ascii(str(lock_dir))} ({type(e).__name__}). Pass --lock-dir with a writable folder.",
                file=sys.stderr,
            )
            return 2
        write_info(lock_dir, slot.index, lane, command)
        child_job = wt_venv.create_kill_job()
        try:
            proc, assigned = wt_venv.spawn_in_job(resolve_executable(command), dotnet_env(command, dict(os.environ)), Path.cwd(), child_job)
        except OSError as e:
            why = f"errno {e.errno}" if e.errno is not None else ascii(str(e))
            print(
                f"ERROR: could not start {ascii(command[0])} ({type(e).__name__}, {why}). Check the command name and that it is on PATH.",
                file=sys.stderr,
            )
            return 127
        if wt_venv.needs_taskkill_fallback(assigned):
            print(
                "heavy: WARNING: no job object for the command's tree; Ctrl-C falls back to taskkill /T by PID "
                "(a grandchild whose parent already exited may survive)",
                file=sys.stderr,
                flush=True,
            )
            if child_job is not None:  # an empty job's terminate() returns TRUE and would skip the taskkill fallback
                child_job.close()
                child_job = None
        return _wait_child(proc)
    except KeyboardInterrupt:
        if proc is not None:
            _kill_child_tree(proc, child_job)
        return 130
    finally:
        if child_job is not None:
            child_job.close()  # KILL_ON_JOB_CLOSE: nothing the command left behind outlives this wrapper
        if slot is not None:
            slot.release()


if __name__ == "__main__":
    sys.exit(main())
