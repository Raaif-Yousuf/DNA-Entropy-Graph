"""Run `premerge.py --fast` in an agent worktree with the shared worker venv, safely (#958).

An agent worktree has no `worker\\.venv`. Run this from the worktree root, with the PRIMARY checkout's venv python:

    <primary checkout>\\worker\\.venv\\Scripts\\python.exe scripts\\premerge_wt.py [--junction] [-- <extra premerge args>]

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
Exit code: premerge's. 2 refused, 4 shared venv damaged (overrides everything else), 5 link could not be removed,
124 timed out (tree killed), 130 interrupted.
"""

from __future__ import annotations

import argparse
import contextlib
import math
import os
import subprocess
import sys
from collections.abc import Callable, Sequence
from pathlib import Path

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

import wt_venv  # noqa: E402

_RECREATE = 'cd worker; uv venv --python 3.12; uv pip install -e ".[dev,genes]"'
EXIT_REFUSED = 2
EXIT_VENV_GONE = 4
EXIT_CLEANUP_FAILED = 5
EXIT_TIMEOUT = 124
EXIT_INTERRUPTED = 130
TIMEOUT_SECONDS = 120 * 60.0  # the whole run, heavy.py slot wait included (same bound as land_wt.py)

Runner = Callable[[list[str], dict[str, str], Path, "float | None"], int]


def default_runner(argv: list[str], env: dict[str, str], cwd: Path, timeout: float | None = None) -> int:
    """Run the gate with inherited stdio. On timeout or Ctrl-C the tree THIS call started (by PID) is killed first."""
    job = wt_venv.create_kill_job()  # Windows: the whole tree dies with the job, even if heavy.py exits before we kill it
    try:
        proc, assigned = wt_venv.spawn_in_job(argv, env, cwd, job)
    except BaseException:
        if job is not None:
            job.close()
        raise
    if os.name == "nt" and not assigned:
        print("WARNING: no job object for the gate tree; falling back to taskkill /T by PID (a Ctrl-C may orphan grandchildren)")
        if job is not None:
            job.close()
        job = None

    def kill_all() -> None:
        if job is not None:
            job.terminate()
        wt_venv.kill_tree(proc)  # by PID /T too: covers anything that escaped the job, and the POSIX process group
        with contextlib.suppress(subprocess.TimeoutExpired):
            proc.wait(timeout=10)

    try:
        return proc.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        kill_all()
        print(f"ERROR: premerge timed out after {int(timeout or 0)} s and its process tree was killed")
        return EXIT_TIMEOUT
    except BaseException:
        kill_all()  # Ctrl-C or anything else: no orphan gate keeps running after the link is gone
        raise
    finally:
        if job is not None:
            job.close()


def is_primary_checkout(root: Path) -> bool:
    """The primary checkout has a `.git` directory; a linked worktree has a `.git` file."""
    return (root / ".git").is_dir()


def primary_venv(root: Path) -> Path:
    """`<primary checkout>\\worker\\.venv`, from git's common dir. Raises wt_venv.LinkError when git cannot say."""
    try:
        out = subprocess.run(
            ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=root, capture_output=True, text=True, check=True
        )
    except (subprocess.CalledProcessError, OSError) as err:
        raise wt_venv.LinkError(f"could not ask git for the primary checkout ({err})") from err
    return Path(out.stdout.strip()).parent / wt_venv.VENV_REL


def _through_link(venv: Path, link: Path, wt: Path) -> bool:
    """Is `venv` the worktree's own link path (or under it, or resolving into the worktree)? Checked as spelled AND resolved,
    because a stale junction at the link path resolves to the shared venv and would hide the spelling."""
    spelled = os.path.normcase(os.path.abspath(venv))
    own = os.path.normcase(os.path.abspath(link))
    if spelled == own or spelled.startswith(own + os.sep):
        return True
    real_wt = os.path.normcase(os.path.realpath(wt))
    return os.path.normcase(os.path.realpath(venv)).startswith(real_wt + os.sep)


def _say_gone(venv: Path) -> None:
    print(f"ERROR: THE SHARED VENV IS DAMAGED: no python in {venv}. Something followed a link into it.")
    print(f"Recreate it: {_RECREATE}")


def run(
    wt: Path,
    venv: Path,
    *,
    junction: bool = False,
    runner: Runner = default_runner,
    extra: Sequence[str] = (),
    fast: bool = True,
    timeout: float | None = TIMEOUT_SECONDS,
) -> int:
    link = wt / wt_venv.VENV_REL
    shared_py = wt_venv.venv_python(venv)
    if shared_py is None:
        print(f"ERROR: no python in the shared venv {venv}. Create it in the primary checkout: {_RECREATE}")
        return EXIT_REFUSED
    if _through_link(venv, link, wt):
        print(f"ERROR: --venv {venv} is inside this worktree's own {wt_venv.VENV_REL} path, which this script links and unlinks.")
        print("Pass the PRIMARY checkout's venv instead")
        return EXIT_REFUSED
    try:
        wt_venv.unlink_dir_link(link)  # a stale junction from a killed run; refuses a real directory
    except wt_venv.LinkError as err:
        print(f"ERROR: {err}. This worktree has its own venv: run scripts\\premerge.py --fast directly")
        return EXIT_REFUSED
    env = dict(os.environ)
    src = str(wt / "worker" / "src")
    env["PYTHONPATH"] = src + (os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else "")
    code: int | None = None
    cleanup_error: wt_venv.LinkError | None = None
    try:
        py: Path | None = shared_py
        if junction:
            try:
                wt_venv.make_dir_link(link, venv)
            except wt_venv.LinkError as err:
                print(f"ERROR: {err}. Run without --junction, or fix the link and retry")
                code = EXIT_REFUSED
                py = None
            else:
                py = wt_venv.venv_python(link)
                if py is None:
                    print(f"ERROR: the venv link {link} does not reach a python")
                    code = EXIT_REFUSED
        if py is not None:
            argv = wt_venv.premerge_argv(wt, py, full=not fast, lane_prefix="premerge", extra=extra)
            try:
                code = runner(argv, env, wt, timeout)
            except KeyboardInterrupt:
                print("ERROR: interrupted")
                code = EXIT_INTERRUPTED
    finally:
        is_link = os.path.isjunction(link) or os.path.islink(link)
        if not junction and os.path.lexists(link) and not is_link:
            print(f"WARNING: a real directory appeared at {link} during the run; left untouched, premerge's code is forwarded")
        elif junction or is_link:
            try:
                wt_venv.unlink_dir_link(link)
            except wt_venv.LinkError as err:
                cleanup_error = err
                shown = "unknown (premerge raised)" if code is None else str(code)
                print(f"ERROR: could not remove {link}: {err}. premerge's exit code {shown}.")
                print("Remove the link by hand (rmdir, never a recursive delete)")
    if wt_venv.venv_python(venv) is None:
        _say_gone(venv)
        return EXIT_VENV_GONE
    if cleanup_error is not None:
        return EXIT_CLEANUP_FAILED
    return EXIT_INTERRUPTED if code is None else code


def main(argv: Sequence[str] | None = None, *, runner: Runner = default_runner) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0] if __doc__ else None)
    ap.add_argument("--junction", action="store_true", help="link <wt>\\worker\\.venv to the shared venv for the run (always unlinked)")
    ap.add_argument("--venv", type=Path, default=None, help="the shared venv (default: the primary checkout's worker/.venv)")
    ap.add_argument("--full", action="store_true", help="run the full gate, not --fast (only with no agent mid-edit)")
    ap.add_argument("--timeout", type=float, default=TIMEOUT_SECONDS, help="seconds before the gate tree is killed (default 7200)")
    ap.add_argument("extra", nargs="*", help="further premerge.py arguments, after --")
    args = ap.parse_args(argv)
    if not math.isfinite(args.timeout) or args.timeout <= 0:
        print("ERROR: --timeout must be a positive, finite number of seconds")
        return EXIT_REFUSED
    wt = Path.cwd().resolve()
    if not (wt / "scripts" / "premerge.py").is_file():
        print("ERROR: run this from a worktree root (scripts/premerge.py not found here)")
        return EXIT_REFUSED
    if is_primary_checkout(wt):
        print("ERROR: this is the primary checkout, which has a real venv. Run scripts\\premerge.py --fast directly")
        return EXIT_REFUSED
    try:
        venv = args.venv or primary_venv(wt)
    except wt_venv.LinkError as err:
        print(f"ERROR: {err}. Pass --venv <primary checkout>\\worker\\.venv")
        return EXIT_REFUSED
    return run(wt, venv, junction=args.junction, extra=args.extra, fast=not args.full, runner=runner, timeout=args.timeout)


if __name__ == "__main__":
    sys.exit(main())
