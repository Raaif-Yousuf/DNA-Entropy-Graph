"""scripts/setup_git_drivers.py -- one-time per clone: register the repo's git merge drivers.

Currently one driver: `merge.resw.driver` (scripts/resw_merge.py, routed by .gitattributes for
Resources.resw). Git only runs a driver that is defined in config, and worktrees share the primary
repo's .git/config, so running this once in the primary checkout covers every worktree. The script is copied into
`<git-common-dir>/deg-drivers/` and the config points at the copy, because git runs the driver with the merged
branch's tree checked out and an old branch has no scripts/resw_merge.py (issue #800). Re-run after resw_merge.py changes.

Run it with the venv python so the driver uses the same interpreter:

    worker\\.venv\\Scripts\\python.exe scripts\\setup_git_drivers.py [--repo <path>]

The value is passed to git as an argument list (no shell quoting), so it works the same from Windows
PowerShell 5.1, PowerShell 7 and bash. Safe to re-run.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent


def install_driver_script(repo: Path) -> Path:
    """Copy resw_merge.py into the repo's git common dir and return the copy (issue #800).

    Git runs the driver with the merged branch's tree checked out, so a path into this checkout's scripts/ does not
    exist when the branch predates the driver (or the worktree was removed). The git common dir is shared by every
    worktree and never changes with the checked-out branch. Re-running setup refreshes the copy.
    """
    proc = subprocess.run(["git", "rev-parse", "--git-common-dir"], cwd=repo, capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr.strip())
    common = Path(proc.stdout.strip())
    if not common.is_absolute():
        common = repo / common
    common = common.resolve()
    target = common / "deg-drivers" / "resw_merge.py"
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(SCRIPTS_DIR / "resw_merge.py", target)
    _route_attributes(common)
    return target


def _route_attributes(common: Path) -> None:
    """Also route the file to the driver through <common>/info/attributes (read for every worktree).

    Git takes `merge=resw` from the checked-out tree's .gitattributes, so merging two branches that both predate the
    attribute line fell back to the text merge (issue #800). The lines are the repo's own `merge=resw` lines.
    """
    source = SCRIPTS_DIR.parent / ".gitattributes"
    if not source.exists():
        return
    wanted = [
        ln for ln in source.read_text(encoding="utf-8").splitlines() if ln.strip() and not ln.lstrip().startswith("#") and "merge=resw" in ln
    ]
    info = common / "info" / "attributes"
    info.parent.mkdir(parents=True, exist_ok=True)
    have = info.read_text(encoding="utf-8").splitlines() if info.exists() else []
    missing = [ln for ln in wanted if ln not in have]
    if missing:
        info.write_text("\n".join(have + missing) + "\n", encoding="utf-8", newline="\n")


def resw_driver_command(script: Path | None = None, python: str | None = None) -> str:
    """The merge.resw.driver value: absolute forward-slash paths, quoted for the `sh` git runs it with."""
    py = Path(python or sys.executable).resolve().as_posix()
    return f'"{py}" "{(script or SCRIPTS_DIR / "resw_merge.py").as_posix()}" %O %A %B'


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Register this repo's git merge drivers in its local config.")
    ap.add_argument("--repo", type=Path, default=SCRIPTS_DIR.parent, help="repo (or any worktree of it); default: this checkout")
    args = ap.parse_args(argv)
    try:
        script = install_driver_script(args.repo)
    except (RuntimeError, OSError) as exc:
        print(f"ERROR: could not set merge.resw.driver: {exc}. Run this from inside the DNA-Entropy-Graph checkout.")
        return 1
    value = resw_driver_command(script)
    proc = subprocess.run(["git", "config", "merge.resw.driver", value], cwd=args.repo, capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        print(f"ERROR: could not set merge.resw.driver: {proc.stderr.strip()}. Run this from inside the DNA-Entropy-Graph checkout.")
        return 1
    print(f"OK: merge.resw.driver = {value}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
