"""Directory-link helpers for the shared worker venv (#958). Used by land_wt.py and premerge_wt.py.

Removal is `os.rmdir` (a Windows junction) or `os.unlink` (a symlink): it takes the reparse point only and
NEVER follows it. Never shutil.rmtree or Remove-Item -Recurse on a path that may be a link to the shared venv.
"""

from __future__ import annotations

import os
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
        if os.name == "nt":
            os.rmdir(link)  # removes the reparse point only
        else:
            os.unlink(link)
    else:
        raise LinkError(f"{link} is a real directory, not a link; refusing to remove it")
    if os.path.lexists(link):
        raise LinkError(f"could not remove the link {link}")
