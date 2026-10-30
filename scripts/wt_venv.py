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
