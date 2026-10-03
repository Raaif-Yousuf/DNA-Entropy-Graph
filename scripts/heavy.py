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
code. Ctrl-C terminates the child by its PID and exits 130 (that path has no automated test). Exit 2 is
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

DEFAULT_SLOTS = 2
REPO_ROOT = Path(__file__).resolve().parents[1]
LANE_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}")
DEFAULT_POLL = 1.0
DOTNET_VERBS = frozenset({"build", "test", "run", "pack", "publish"})

IS_WINDOWS = sys.platform == "win32"
if IS_WINDOWS:
    import msvcrt
else:
    import fcntl


def default_lock_dir() -> Path:
    env = os.environ.get("HEAVY_LOCK_DIR")
    return Path(env) if env else Path(tempfile.gettempdir()) / "deg-heavy"


def default_artifacts_base() -> str:
    """`<repo>/app/.artifacts`. Not %TEMP%: tests that find the repo by walking up from
    AppContext.BaseDirectory (Cloud.Tests FixturePaths, Core.Tests StartupMetadataTests) fail with the
    output outside the repo. Its bin/ obj/ publish/ subfolders are gitignored by `app/**/bin/` etc."""
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


_JOB_HANDLE = None  # kept open for the process lifetime: closing it is what kills the group


def kill_children_with_me() -> str | None:
    """Windows: join a kill-on-close job object. Returns None on success, else a short ASCII reason."""
    global _JOB_HANDLE
    if not IS_WINDOWS:
        return None
    try:
        import ctypes
        from ctypes import wintypes

        class _Basic(ctypes.Structure):
            _fields_ = [
                ("PerProcessUserTimeLimit", ctypes.c_int64),
                ("PerJobUserTimeLimit", ctypes.c_int64),
                ("LimitFlags", wintypes.DWORD),
                ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t),
                ("ActiveProcessLimit", wintypes.DWORD),
                ("Affinity", ctypes.c_size_t),
                ("PriorityClass", wintypes.DWORD),
                ("SchedulingClass", wintypes.DWORD),
            ]

        class _Io(ctypes.Structure):
            _fields_ = [(n, ctypes.c_uint64) for n in ("ro", "wo", "oo", "rb", "wb", "ob")]

        class _Extended(ctypes.Structure):
            _fields_ = [
                ("BasicLimitInformation", _Basic),
                ("IoInfo", _Io),
                ("ProcessMemoryLimit", ctypes.c_size_t),
                ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t),
                ("PeakJobMemoryUsed", ctypes.c_size_t),
            ]

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.CreateJobObjectW.restype = wintypes.HANDLE
        k32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        k32.SetInformationJobObject.restype = wintypes.BOOL
        k32.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        k32.AssignProcessToJobObject.restype = wintypes.BOOL
        k32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        k32.GetCurrentProcess.restype = wintypes.HANDLE
        k32.CloseHandle.argtypes = [wintypes.HANDLE]

        job = k32.CreateJobObjectW(None, None)
        if not job:
            return f"CreateJobObject error {ctypes.get_last_error()}"
        info = _Extended()
        info.BasicLimitInformation.LimitFlags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not k32.SetInformationJobObject(job, 9, ctypes.byref(info), ctypes.sizeof(info)):  # 9 = ExtendedLimitInformation
            err = ctypes.get_last_error()
            k32.CloseHandle(job)
            return f"SetInformationJobObject error {err}"
        if not k32.AssignProcessToJobObject(job, k32.GetCurrentProcess()):
            err = ctypes.get_last_error()
            k32.CloseHandle(job)
            return f"AssignProcessToJobObject error {err}"
        _JOB_HANDLE = job
        return None
    except Exception as e:  # never let the safety net stop the run
        return f"{type(e).__name__}"


DOTNET_ENV_DEFAULTS = {"MSBUILDDISABLENODEREUSE": "1", "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "UseSharedCompilation": "false"}


def _is_dotnet(argv: list[str]) -> bool:
    return bool(argv) and Path(argv[0].replace("\\", "/")).name.lower() in ("dotnet", "dotnet.exe")


def dotnet_env(argv: list[str], env: dict[str, str]) -> dict[str, str]:
    """For a dotnet command, return a copy of env with node reuse and shared compilation off.

    Pure. A value the caller already set wins. Non-dotnet commands get env back unchanged.
    Why: the kill-on-close job ends any MSBuild node or VBCSCompiler server left by this wrapper, and a
    reusable per-user node could be picked up by another lane's concurrent build and then killed under it.
    THEORY (unverified): that cross-lane failure was never reproduced; the defaults just make it impossible.
    """
    if not _is_dotnet(argv):
        return env
    out = dict(env)
    for key, value in DOTNET_ENV_DEFAULTS.items():
        out.setdefault(key, value)
    return out


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
        found.append({"slot": str(i), "pid": info.get("pid", "?"), "lane": info.get("lane", "?"), "cmd": info.get("cmd", "")})
    return found


def describe_wait(slots: int, held: list[dict[str, str]]) -> str:
    who = ", ".join(f"pid {h['pid']} lane {h['lane']}" for h in held) or "unknown"
    return f"heavy: waiting for a free slot (all {slots} busy; holders: {who}). To free a stuck one, stop that process (taskkill /F /PID <pid>)."


def acquire_slot(lock_dir: Path, slots: int, poll: float, on_wait) -> Slot:
    """Block until a slot is free; call on_wait(holders) once, the first time every slot is busy."""
    announced = False
    while True:
        slot = try_acquire(lock_dir, slots)
        if slot is not None:
            return slot
        if not announced:
            on_wait(holders(lock_dir, slots))
            announced = True
        time.sleep(poll)


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
    job_problem = kill_children_with_me()
    if job_problem:
        print(f"heavy: note: no kill-on-close job ({job_problem}); a hard-killed wrapper will not stop its child", flush=True)
    try:
        try:
            slot = acquire_slot(lock_dir, slots, args.poll, lambda held: print(describe_wait(slots, held), flush=True))
        except OSError as e:
            print(
                f"ERROR: cannot use the lock folder {ascii(str(lock_dir))} ({type(e).__name__}). Pass --lock-dir with a writable folder.",
                file=sys.stderr,
            )
            return 2
        write_info(lock_dir, slot.index, lane, command)
        try:
            proc = subprocess.Popen(resolve_executable(command), env=dotnet_env(command, dict(os.environ)))
        except OSError as e:
            print(
                f"ERROR: could not start {ascii(command[0])} ({type(e).__name__}, errno {e.errno}). "
                "Check the command name and that it is on PATH.",
                file=sys.stderr,
            )
            return 127
        return proc.wait()
    except KeyboardInterrupt:
        if proc is not None and proc.poll() is None:
            proc.kill()
            with contextlib.suppress(Exception):
                proc.wait(timeout=10)
        return 130
    finally:
        if slot is not None:
            slot.release()


if __name__ == "__main__":
    sys.exit(main())
