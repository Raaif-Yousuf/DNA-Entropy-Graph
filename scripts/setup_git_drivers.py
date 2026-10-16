"""scripts/setup_git_drivers.py -- one-time per clone: register the repo's git merge drivers.

Currently one driver: `merge.resw.driver` (scripts/resw_merge.py, routed for Resources.resw). Git only runs a driver
that is defined in config, and worktrees share the primary repo's .git/config, so running this once covers every
worktree.

Why the driver is installed rather than pointed at scripts/ (issue #800): git runs it with the MERGED branch's tree
checked out, and a branch older than the driver has neither `scripts/resw_merge.py` nor the `merge=resw` line in its
`.gitattributes`. So setup

- copies origin/main's `scripts/resw_merge.py` (never this checkout's, which may be a stale branch) to
  `<git-common-dir>/deg-drivers/` and points the config at the copy;
- runs the copy with the PRIMARY checkout's `worker/.venv` python (the git common dir's parent), falling back to the
  interpreter running setup when that venv is absent, so a removed lane worktree cannot break it;
- appends `<RESW_PATH> merge=resw` to `<git-common-dir>/info/attributes` (read for every worktree), keeping every
  other line byte for byte, and warns about a different merge attribute for the same path.

Re-run it after resw_merge.py changes on origin/main (`git fetch` first); `scripts/check_resw_driver.py` says when the
installed copy has drifted.

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
# Same value as land_wt.RESW_PATH (a test keeps them equal); hardcoded so a stale checkout's files cannot change it.
RESW_PATH = "app/src/DnaEntropyGraph.App/Strings/en-US/Resources.resw"
ATTRIBUTE_LINE = f"{RESW_PATH} merge=resw"
SCRIPT_IN_REPO = "scripts/resw_merge.py"
FIX_COMMAND = r"worker\.venv\Scripts\python.exe scripts\setup_git_drivers.py"


def git_common_dir(repo: Path) -> Path:
    proc = subprocess.run(["git", "rev-parse", "--git-common-dir"], cwd=repo, capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr.strip() or "not a git repository")
    common = Path(proc.stdout.strip())
    return (common if common.is_absolute() else repo / common).resolve()


def origin_main_script(repo: Path) -> bytes | None:
    """origin/main's resw_merge.py bytes, or None when there is no origin/main or it has no such file."""
    proc = subprocess.run(["git", "show", f"origin/main:{SCRIPT_IN_REPO}"], cwd=repo, capture_output=True, check=False)
    return proc.stdout if proc.returncode == 0 else None


def install_driver_script(repo: Path) -> Path:
    """Write the script git will run into <common>/deg-drivers/ and return its path.

    The content is origin/main's blob; only a repo with no origin/main (a fresh clone being set up offline, a test)
    falls back to this checkout's file, so a stale branch's older script can never replace a newer installed one.
    """
    common = git_common_dir(repo)
    content = origin_main_script(repo)
    if content is None:
        content = (SCRIPTS_DIR / "resw_merge.py").read_bytes()
    target = common / "deg-drivers" / "resw_merge.py"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(content)
    return target


def route_attributes(common: Path) -> list[str]:
    """Make sure <common>/info/attributes routes RESW_PATH to the driver; return warnings (never raises on odd bytes).

    Works on bytes: every existing line is kept as it is. A line for the same path with another `merge=` value is
    reported, and ours is appended after it (the last matching line wins in one file).
    """
    info = common / "info" / "attributes"
    info.parent.mkdir(parents=True, exist_ok=True)
    existing = info.read_bytes() if info.exists() else b""
    lines = existing.split(b"\n")
    warnings: list[str] = []
    for raw in lines:
        text = raw.decode("utf-8", errors="replace").strip()
        parts = text.split()
        if parts and parts[0] == RESW_PATH and any(p.startswith("merge") for p in parts[1:]):
            if text == ATTRIBUTE_LINE:
                return warnings
            warnings.append(f"{info} already has '{text}' for {RESW_PATH}; the merge=resw line added after it takes precedence")
    separator = b"" if not existing or existing.endswith(b"\n") else b"\n"
    info.write_bytes(existing + separator + ATTRIBUTE_LINE.encode("ascii") + b"\n")
    return warnings


def stable_python(common: Path) -> str:
    """The primary checkout's worker venv python when it exists, else the interpreter running this script."""
    venv = common.parent / "worker" / ".venv" / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
    return str(venv) if venv.exists() else sys.executable


def resw_driver_command(script: Path | None = None, python: str | None = None) -> str:
    """The merge.resw.driver value: absolute forward-slash paths, quoted for the `sh` git runs it with."""
    py = Path(python or sys.executable).resolve().as_posix()
    return f'"{py}" "{(script or SCRIPTS_DIR / "resw_merge.py").as_posix()}" %O %A %B'


def _fail(message: str) -> int:
    print(f"ERROR: could not set merge.resw.driver: {message}. Run this from inside the DNA-Entropy-Graph checkout.")
    return 1


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Register this repo's git merge drivers in its local config.")
    ap.add_argument("--repo", type=Path, default=SCRIPTS_DIR.parent, help="repo (or any worktree of it); default: this checkout")
    args = ap.parse_args(argv)
    try:
        common = git_common_dir(args.repo)
        script = install_driver_script(args.repo)
        warnings = route_attributes(common)
    except (RuntimeError, OSError) as exc:
        return _fail(str(exc))
    for warning in warnings:
        print(f"WARNING: {warning}")
    value = resw_driver_command(script, stable_python(common))
    proc = subprocess.run(["git", "config", "merge.resw.driver", value], cwd=args.repo, capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        return _fail(proc.stderr.strip())
    routed = subprocess.run(["git", "check-attr", "merge", "--", RESW_PATH], cwd=args.repo, capture_output=True, text=True, check=False)
    if not routed.stdout.strip().endswith("merge: resw"):
        return _fail(f"git does not route {RESW_PATH} to the driver after the write ({routed.stdout.strip() or routed.stderr.strip()})")
    print(f"OK: merge.resw.driver = {value}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
