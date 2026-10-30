"""Run `premerge.py --fast` in an agent worktree with the shared worker venv, safely (#958).

An agent worktree has no `worker\\.venv`. Run this from the worktree root, with the PRIMARY checkout's venv python:

    C:\\Users\\raaif\\DNA-Entropy-Graph\\worker\\.venv\\Scripts\\python.exe scripts\\premerge_wt.py [--junction] [-- <extra premerge args>]

What it does:
- Default (junction-free): premerge's own `--python` points every Python gate at the shared venv's interpreter and
  `PYTHONPATH=<wt>\\worker\\src` makes the shared venv's editable install import THIS worktree's code (#937).
  Nothing is linked, so nothing can be left behind or followed by a later cleanup.
  MEASURED 2026-10-30: premerge.py finds the venv only through `find_venv_python(root)`, which `--python` overrides,
  and check_pyright.py uses `sys.executable`; no gate needs `<wt>\\worker\\.venv` to exist.
- `--junction`: the land_wt.py shape instead (a junction `<wt>\\worker\\.venv` -> the shared venv, python reached
  through it), always unlinked in `finally` (also on Ctrl-C), for a gate that insists on the in-tree path.
- Either way a stale junction left by an earlier killed run is unlinked first; a REAL directory at
  `<wt>\\worker\\.venv` is refused and untouched; afterwards it asserts the shared venv's python still exists.
- Removal is `os.rmdir`/`os.unlink` of the link only (wt_venv.py), never a recursive delete.
- The primary checkout has a real venv: run premerge.py there directly (this script refuses).
Exit code: premerge's. 2 refused, 4 shared venv damaged, 130 interrupted.
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from collections.abc import Callable, Sequence
from pathlib import Path

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

import wt_venv  # noqa: E402

EXIT_REFUSED = 2
EXIT_VENV_GONE = 4
EXIT_INTERRUPTED = 130

Runner = Callable[[list[str], dict[str, str], Path], int]


def default_runner(argv: list[str], env: dict[str, str], cwd: Path) -> int:
    return subprocess.call(argv, env=env, cwd=cwd)


def is_primary_checkout(root: Path) -> bool:
    """The primary checkout has a `.git` directory; a linked worktree has a `.git` file."""
    return (root / ".git").is_dir()


def primary_venv(root: Path) -> Path:
    """`<primary checkout>\\worker\\.venv`, from git's common dir."""
    out = subprocess.run(
        ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=root, capture_output=True, text=True, check=True
    )
    return Path(out.stdout.strip()).parent / wt_venv.VENV_REL


def _say_gone(venv: Path) -> None:
    print(f"ERROR: THE SHARED VENV IS DAMAGED: no python in {venv}. Something followed a link into it. Recreate it: cd worker; uv venv --python 3.12; uv pip install -e \".[dev,genes]\"")


def run(
    wt: Path,
    venv: Path,
    *,
    junction: bool = False,
    runner: Runner = default_runner,
    extra: Sequence[str] = (),
    fast: bool = True,
) -> int:
    link = wt / wt_venv.VENV_REL
    shared_py = wt_venv.venv_python(venv)
    if shared_py is None:
        print(f"ERROR: no python in the shared venv {venv}. Create it in the primary checkout: cd worker; uv venv --python 3.12; uv pip install -e \".[dev,genes]\"")
        return EXIT_REFUSED
    try:
        wt_venv.unlink_dir_link(link)  # a stale junction from a killed run; refuses a real directory
    except wt_venv.LinkError as err:
        print(f"ERROR: {err}. This worktree has its own venv: run scripts\\premerge.py --fast directly")
        return EXIT_REFUSED
    env = dict(os.environ)
    src = str(wt / "worker" / "src")
    env["PYTHONPATH"] = src + (os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else "")
    code = EXIT_INTERRUPTED
    try:
        if junction:
            wt_venv.make_dir_link(link, venv)
            py = wt_venv.venv_python(link)
            if py is None:
                print(f"ERROR: the venv link {link} does not reach a python")
                return EXIT_REFUSED
        else:
            py = shared_py
        argv = [str(py), str(wt / "scripts" / "premerge.py"), "--root", str(wt), "--python", str(py)]
        if fast:
            argv.append("--fast")
        argv += list(extra)
        heavy = wt / "scripts" / "heavy.py"
        if heavy.is_file():  # one machine-wide heavy slot for the whole run (RAM)
            argv = [str(py), str(heavy), "--lane", f"premerge-{wt.name}"[:64], "--", *argv]
        try:
            code = runner(argv, env, wt)
        except KeyboardInterrupt:
            print("ERROR: interrupted")
            code = EXIT_INTERRUPTED
    finally:
        if junction or os.path.lexists(link):
            wt_venv.unlink_dir_link(link)
    if wt_venv.venv_python(venv) is None:
        _say_gone(venv)
        return EXIT_VENV_GONE
    return code


def main(argv: Sequence[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0] if __doc__ else None)
    ap.add_argument("--junction", action="store_true", help="link <wt>\\worker\\.venv to the shared venv for the run (always unlinked)")
    ap.add_argument("--venv", type=Path, default=None, help="the shared venv (default: the primary checkout's worker/.venv)")
    ap.add_argument("--full", action="store_true", help="run the full gate, not --fast (only with no agent mid-edit)")
    ap.add_argument("extra", nargs="*", help="further premerge.py arguments, after --")
    args = ap.parse_args(argv)
    wt = Path.cwd().resolve()
    if not (wt / "scripts" / "premerge.py").is_file():
        print("ERROR: run this from a worktree root (scripts/premerge.py not found here)")
        return EXIT_REFUSED
    if is_primary_checkout(wt):
        print("ERROR: this is the primary checkout, which has a real venv. Run scripts\\premerge.py --fast directly")
        return EXIT_REFUSED
    venv = args.venv or primary_venv(wt)
    return run(wt, venv, junction=args.junction, extra=args.extra, fast=not args.full)


if __name__ == "__main__":
    sys.exit(main())
