"""scripts/check_resw_driver.py -- the installed Resources.resw merge driver must be origin/main's script (issue #800).

`setup_git_drivers.py` installs a copy of resw_merge.py under the git common dir and points `merge.resw.driver` at
it. That copy is not updated by `git pull`, so it can drift from origin/main. This check fails when the configured driver's script does not exist, and
WARNS (exit 0) when its bytes differ from `git show origin/main:scripts/resw_merge.py`: landing runs premerge in throwaway worktrees sharing the config, so a
drift failure would block every landing after a driver change on main.

It skips (exit 0) when the driver is not configured (CI, a fresh clone) or there is no origin/main to compare with.
The fix is always one command, printed in the message.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent
FIX = r"worker\.venv\Scripts\python.exe scripts\setup_git_drivers.py"


def _git(repo: Path, *args: str) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(["git", *args], cwd=repo, capture_output=True, check=False)


def configured_script(repo: Path) -> str | None:
    """The script path inside `merge.resw.driver` (the second quoted token), or None when unset."""
    got = _git(repo, "config", "--get", "merge.resw.driver")
    if got.returncode != 0:
        return None
    match = re.match(r'^"[^"]*"\s+"([^"]+)"', got.stdout.decode("utf-8", errors="replace").strip())
    return match.group(1) if match else ""


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--repo", type=Path, default=SCRIPTS_DIR.parent)
    args = ap.parse_args(argv)
    script = configured_script(args.repo)
    if script is None:
        print("OK: merge.resw.driver is not configured, skipped (run scripts/setup_git_drivers.py once per clone).")
        return 0
    if not script or not Path(script).is_file():
        print(f"ERROR: merge.resw.driver points at a script that does not exist ({script or 'unparsable value'}). Run: {FIX}")
        return 1
    origin = _git(args.repo, "show", "origin/main:scripts/resw_merge.py")
    if origin.returncode != 0:
        print("OK: no origin/main to compare the installed merge driver with, skipped.")
        return 0
    if Path(script).read_bytes() != origin.stdout:
        # A warning, not a failure: land_wt runs premerge in throwaway worktrees that share this config, so a failing drift
        # check would block every landing after any resw_merge.py change on main until someone re-ran setup.
        print(f"WARNING: the installed merge driver ({script}) differs from origin/main's scripts/resw_merge.py. Run: {FIX}")
        return 0
    print("OK: the installed Resources.resw merge driver matches origin/main.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
