"""Directory-link helpers for the shared worker venv (#958). Used by land_wt.py and premerge_wt.py.

Removal is `os.rmdir` (a Windows junction) or `os.unlink` (a symlink): it takes the reparse point only and
NEVER follows it. Never shutil.rmtree or Remove-Item -Recurse on a path that may be a link to the shared venv.
"""

from __future__ import annotations

import contextlib
import os
import signal
import subprocess
from collections.abc import Sequence
from pathlib import Path
from typing import Any

VENV_REL = Path("worker") / ".venv"


class LinkError(Exception):
    pass


def venv_python(venv: Path) -> Path | None:
    """The interpreter inside a venv directory (Windows layout first), or None when there is none."""
    for rel in (Path("Scripts") / "python.exe", Path("bin") / "python"):
        if (venv / rel).is_file():
            return venv / rel
    return None


def make_dir_link(link: Path, target: Path) -> None:
    try:
        link.parent.mkdir(parents=True, exist_ok=True)
        if os.name == "nt":
            import _winapi  # noqa: PLC0415  (Windows only)

            _winapi.CreateJunction(str(target), str(link))
        else:
            os.symlink(target, link, target_is_directory=True)
    except OSError as err:
        raise LinkError(f"could not link {link} to {target}: {err}") from err


def unlink_dir_link(link: Path) -> None:
    """Remove a junction or directory symlink without touching its target. Refuses a real directory."""
    if not os.path.lexists(link):
        return
    if os.path.isjunction(link) or os.path.islink(link):
        _remove_link_only(link)
    else:
        raise LinkError(f"{link} is a real directory, not a link; refusing to remove it")
    if os.path.lexists(link):
        raise LinkError(f"could not remove the link {link}")


def _remove_link_only(link: Path) -> None:
    """Take the reparse point / symlink only, never its target. A junction or directory link goes with rmdir on Windows,
    a file symlink only with unlink: try the first, fall back to the second, and report any OSError as a LinkError."""
    try:
        if os.name == "nt":
            try:
                os.rmdir(link)
            except OSError:
                os.unlink(link)  # an NT file symlink: rmdir says NotADirectoryError
        else:
            os.unlink(link)
    except OSError as err:
        raise LinkError(f"could not remove the link {link}: {err}") from err


def taskkill_tree(pid: int) -> None:
    """`taskkill /T /F /PID <pid>`: that PID and its descendants only, never by image name."""
    with contextlib.suppress(OSError, subprocess.SubprocessError):
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)], capture_output=True, check=False, timeout=30)


def kill_tree(proc: subprocess.Popen[bytes]) -> None:
    """Kill `proc` and everything it started: taskkill /T on exactly this PID (never by image name), or the process group."""
    if os.name == "nt":
        taskkill_tree(proc.pid)
    else:
        with contextlib.suppress(OSError):
            os.killpg(proc.pid, signal.SIGKILL)
    with contextlib.suppress(OSError):
        proc.kill()


def premerge_argv(wt: Path, py: Path, *, full: bool, lane_prefix: str, extra: Sequence[str] = ()) -> list[str]:
    """The premerge command line, wrapped in heavy.py's one machine-wide slot when the worktree has it."""
    argv = [str(py), str(wt / "scripts" / "premerge.py"), "--root", str(wt), "--python", str(py)]
    if not full:
        argv.append("--fast")
    argv += list(extra)
    heavy = wt / "scripts" / "heavy.py"
    if heavy.is_file():
        argv = [str(py), str(heavy), "--lane", f"{lane_prefix}-{wt.name}"[:64], "--", *argv]
    return argv


class KillJob:
    """A Windows Job Object with KILL_ON_JOB_CLOSE: every process assigned to it, and the ones those start, die on
    `terminate()` or when the last handle closes, even if the owner is hard-killed. Start the child with `spawn_in_job`
    (suspended, assigned, then resumed) so it cannot start a grandchild before it is inside the job."""

    KILL_ON_JOB_CLOSE = 0x2000
    EXTENDED_LIMIT_INFORMATION = 9

    def __init__(self, handle: int, k32: Any) -> None:  # noqa: ANN401  (a ctypes library object)
        self._handle: int | None = handle
        self._k32 = k32

    def assign(self, proc: subprocess.Popen[bytes]) -> bool:
        if self._handle is None:
            return False
        child = getattr(proc, "_handle", None)  # the Popen's own process handle, no OpenProcess race
        if child is None:
            return False
        return bool(self._k32.AssignProcessToJobObject(self._handle, int(child)))

    def terminate(self) -> None:
        if self._handle is not None:
            self._k32.TerminateJobObject(self._handle, 1)

    def close(self) -> None:
        if self._handle is not None:
            handle, self._handle = self._handle, None
            self._k32.CloseHandle(handle)


def resume_process_threads(pid: int) -> int:
    """Resume every thread owned by `pid` (a CREATE_SUSPENDED process has exactly one, suspended once). Returns how many
    were resumed; every handle is closed. Windows only."""
    import ctypes  # noqa: PLC0415
    from ctypes import wintypes  # noqa: PLC0415

    class ThreadEntry(ctypes.Structure):
        _fields_ = (
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ThreadID", wintypes.DWORD),
            ("th32OwnerProcessID", wintypes.DWORD),
            ("tpBasePri", wintypes.LONG),
            ("tpDeltaPri", wintypes.LONG),
            ("dwFlags", wintypes.DWORD),
        )

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    k32.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
    k32.Thread32First.argtypes = [ctypes.c_void_p, ctypes.POINTER(ThreadEntry)]
    k32.Thread32First.restype = wintypes.BOOL
    k32.Thread32Next.argtypes = [ctypes.c_void_p, ctypes.POINTER(ThreadEntry)]
    k32.Thread32Next.restype = wintypes.BOOL
    k32.OpenThread.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    k32.OpenThread.restype = ctypes.c_void_p
    k32.ResumeThread.argtypes = [ctypes.c_void_p]
    k32.ResumeThread.restype = wintypes.DWORD
    k32.CloseHandle.argtypes = [ctypes.c_void_p]
    k32.CloseHandle.restype = wintypes.BOOL
    snapshot = k32.CreateToolhelp32Snapshot(0x4, 0)  # TH32CS_SNAPTHREAD
    if snapshot is None or snapshot == ctypes.c_void_p(-1).value:
        return 0
    resumed = 0
    try:
        entry = ThreadEntry()
        entry.dwSize = ctypes.sizeof(ThreadEntry)
        more = k32.Thread32First(snapshot, ctypes.byref(entry))
        while more:
            if entry.th32OwnerProcessID == pid:
                thread = k32.OpenThread(0x2, False, entry.th32ThreadID)  # THREAD_SUSPEND_RESUME
                if thread:
                    try:
                        if k32.ResumeThread(thread) != 0xFFFFFFFF:
                            resumed += 1
                    finally:
                        k32.CloseHandle(thread)
            more = k32.Thread32Next(snapshot, ctypes.byref(entry))
    finally:
        k32.CloseHandle(snapshot)
    return resumed


def spawn_in_job(argv: Sequence[str], env: dict[str, str], cwd: Path, job: KillJob | None) -> tuple[subprocess.Popen[bytes], bool]:
    """Start `argv` with inherited stdio and put it in `job` BEFORE it runs (Windows: CREATE_SUSPENDED, assign, resume), so
    nothing it starts can be outside the job. Returns (process, assigned). An assignment that fails still resumes the child
    (assigned=False, the caller falls back to taskkill). POSIX: its own process group, no job."""
    if os.name != "nt":
        return subprocess.Popen(list(argv), env=env, cwd=cwd, start_new_session=True), False
    proc = subprocess.Popen(list(argv), env=env, cwd=cwd, creationflags=0x4)  # CREATE_SUSPENDED
    try:
        assigned = job.assign(proc) if job is not None else False
    except BaseException:
        proc.kill()
        raise
    if resume_process_threads(proc.pid) == 0:
        proc.kill()  # it would sit suspended forever
        raise OSError(f"could not resume the suspended process {proc.pid}")
    return proc, assigned


def create_kill_job() -> KillJob | None:
    """A kill-on-close job object, or None (not Windows, or the API refused: the caller falls back to taskkill /T)."""
    if os.name != "nt":
        return None
    try:
        import ctypes  # noqa: PLC0415
        from ctypes import wintypes  # noqa: PLC0415

        class BasicLimits(ctypes.Structure):
            _fields_ = (
                ("PerProcessUserTimeLimit", ctypes.c_int64),
                ("PerJobUserTimeLimit", ctypes.c_int64),
                ("LimitFlags", wintypes.DWORD),
                ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t),
                ("ActiveProcessLimit", wintypes.DWORD),
                ("Affinity", ctypes.c_size_t),
                ("PriorityClass", wintypes.DWORD),
                ("SchedulingClass", wintypes.DWORD),
            )

        class IoCounters(ctypes.Structure):
            _fields_ = tuple((name, ctypes.c_uint64) for name in ("ro", "wo", "oo", "rb", "wb", "ob"))

        class ExtendedLimits(ctypes.Structure):
            _fields_ = (
                ("BasicLimitInformation", BasicLimits),
                ("IoInfo", IoCounters),
                ("ProcessMemoryLimit", ctypes.c_size_t),
                ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t),
                ("PeakJobMemoryUsed", ctypes.c_size_t),
            )

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.CreateJobObjectW.argtypes = [wintypes.LPVOID, wintypes.LPCWSTR]
        k32.CreateJobObjectW.restype = wintypes.HANDLE
        k32.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.LPVOID, wintypes.DWORD]
        k32.SetInformationJobObject.restype = wintypes.BOOL
        k32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        k32.AssignProcessToJobObject.restype = wintypes.BOOL
        k32.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
        k32.TerminateJobObject.restype = wintypes.BOOL
        k32.CloseHandle.argtypes = [wintypes.HANDLE]
        k32.CloseHandle.restype = wintypes.BOOL
        handle = k32.CreateJobObjectW(None, None)
        if not handle:
            return None
        info = ExtendedLimits()
        info.BasicLimitInformation.LimitFlags = KillJob.KILL_ON_JOB_CLOSE
        if not k32.SetInformationJobObject(handle, KillJob.EXTENDED_LIMIT_INFORMATION, ctypes.byref(info), ctypes.sizeof(info)):
            k32.CloseHandle(handle)
            return None
        return KillJob(int(handle), k32)
    except (OSError, AttributeError, ValueError):
        return None
