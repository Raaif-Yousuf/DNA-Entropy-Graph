"""scripts/setup_git_drivers.py -- one-time per clone: register the repo's git merge drivers.

Currently one driver: `merge.resw.driver` (scripts/resw_merge.py, routed by .gitattributes for
Resources.resw). Git only runs a driver that is defined in config, and worktrees share the primary
repo's .git/config, so running this once in the primary checkout covers every worktree.

Run it with the venv python so the driver uses the same interpreter:

    worker\\.venv\\Scripts\\python.exe scripts\\setup_git_drivers.py [--repo <path>]

The value is passed to git as an argument list (no shell quoting), so it works the same from Windows
PowerShell 5.1, PowerShell 7 and bash. Safe to re-run.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent


def resw_driver_command(python: str | None = None) -> str:
    """The merge.resw.driver value: absolute forward-slash paths, quoted for the `sh` git runs it with."""
    py = Path(python or sys.executable).resolve().as_posix()
    return f'"{py}" "{(SCRIPTS_DIR / "resw_merge.py").as_posix()}" %O %A %B'


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Register this repo's git merge drivers in its local config.")
    ap.add_argument("--repo", type=Path, default=SCRIPTS_DIR.parent, help="repo (or any worktree of it); default: this checkout")
    args = ap.parse_args(argv)
    value = resw_driver_command()
    proc = subprocess.run(["git", "config", "merge.resw.driver", value], cwd=args.repo, capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        print(f"ERROR: could not set merge.resw.driver: {proc.stderr.strip()}. Run this from inside the DNA-Entropy-Graph checkout.")
        return 1
    print(f"OK: merge.resw.driver = {value}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
