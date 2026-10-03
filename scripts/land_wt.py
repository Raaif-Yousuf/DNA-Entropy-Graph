"""scripts/land_wt.py -- land one committed worktree branch as a PR, verified in a throwaway worktree (#510).

    worker\\.venv\\Scripts\\python.exe scripts\\land_wt.py --branch feat/510-x --sha <sha> \\
        --title "feat: ..." --body-file body.md [--full] [--land-branch NAME]

WHY A SEPARATE SCRIPT FROM land_pr.py: land_pr.py takes uncommitted edits from a shared checkout,
stages exact paths and returns that checkout to main. Here the input is a COMMIT a lane already made
in its own worktree, nothing is staged and no checkout is touched, so none of its machinery applies.

WHAT IT DOES, IN ORDER (a refusal before step 4 changes nothing; no step ever force-pushes)
1. `git fetch origin`; the sha must be the tip of `refs/heads/<branch>` (or `origin/<branch>`).
2. Scan every changed text file for control bytes (MEASURED 2026-10-02: an agent's `scripts\\v`
   reached disk as `\\x0b`). Binary files (git's own numstat verdict) are skipped.
3. Clean up any worktree an earlier killed run left under the temp root (junction first).
4. `git worktree add --detach <temp>/<slug>-<time> <sha>`, outside the repo, and a JUNCTION
   worker\\.venv -> the real venv so Hard Rule 20 tooling finds it (never a copy).
5. Merge origin/main when the branch is behind. The only conflict resolved automatically is
   docs/ToTest.md (append-only rows: `git merge-file --union`); any other conflict aborts the merge
   and refuses with the file list, so the lane resolves it.
6. premerge (`--fast`, or full with `--full`) inside the worktree with PYTHONPATH=<wt>\\worker\\src
   (the venv's editable install points at the shared tree) and its dotnet obj/bin under the
   worktree. Red refuses; the log path is printed.
7. Push HEAD to `refs/heads/<land-branch>` (default: the branch name) and `gh pr create`.
   Merging stays a human call: `gh pr merge <n> --merge`.
8. ALWAYS, success or not: unlink the junction with `os.rmdir`, check it is gone, and only then
   `git worktree remove --force`. MEASURED: an earlier tool deleted the real venv through a junction.
   If the junction cannot be removed the worktree is LEFT IN PLACE (and named) rather than removed.

Exit codes: 0 PR opened, 1 refused (nothing pushed), 2 bad usage, 3 premerge red, 4 failed after work began.
Console output is ASCII (`OK:` / `ERROR:` / `-`).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import stat
import subprocess
import sys
import tempfile
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from pathlib import Path

REMOTE = "origin"
BASE = "main"
TOTEST = "docs/ToTest.md"
VENV_REL = Path("worker") / ".venv"
# NUL..BS, VT, FF and SO..US except ESC. ESC (0x1b) is allowed on purpose: captured logs and test output carry ANSI
# colour and no measured corruption produced it. VT (0x0b, the MEASURED `\v` case) and FF are refused; TAB, LF, CR are text.
CONTROL_BYTES = re.compile(rb"[\x00-\x08\x0b\x0c\x0e-\x1a\x1c-\x1f]")
PROTECTED = {"main", "master", "head"}
LOCK_DIR = "locks"
LOCK_MAX_AGE_SECONDS = 24 * 3600

EXIT_OK, EXIT_REFUSED, EXIT_USAGE, EXIT_RED, EXIT_FAILED = 0, 1, 2, 3, 4

# premerge(worktree, venv, full) -> (exit code, combined output). Injectable so tests never run the real gates.
PremergeFn = Callable[[Path, Path, bool], "tuple[int, str]"]


class LandError(Exception):
    def __init__(self, message: str, code: int = EXIT_FAILED) -> None:
        super().__init__(message)
        self.code = code


def _text(raw: bytes) -> str:
    return raw.decode("utf-8", errors="replace").strip()


def git(cwd: Path, *args: str, check: bool = True, stdin: bytes | None = None) -> subprocess.CompletedProcess[bytes]:
    """Bytes in, bytes out (never text=True on Windows: it re-encodes). Module-level so tests can spy on it."""
    env = dict(os.environ)
    env["GIT_TERMINAL_PROMPT"] = "0"
    proc = subprocess.run(["git", "-c", "core.quotepath=off", *args], cwd=cwd, input=stdin, capture_output=True, check=False, env=env)
    if check and proc.returncode != 0:
        raise LandError(f"git {' '.join(args[:3])} failed ({proc.returncode}): {_text(proc.stderr or proc.stdout)}")
    return proc


# ---------------------------------------------------------------------------
# Directory links (the venv junction). Removal is os.rmdir, NEVER shutil.rmtree.
# ---------------------------------------------------------------------------


def make_dir_link(link: Path, target: Path) -> None:
    try:
        link.parent.mkdir(parents=True, exist_ok=True)
        if os.name == "nt":
            import _winapi  # noqa: PLC0415  (Windows only)

            _winapi.CreateJunction(str(target), str(link))
        else:
            os.symlink(target, link, target_is_directory=True)
    except OSError as err:
        raise LandError(f"could not link {link} to {target}: {err}") from err


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
        raise LandError(f"{link} is a real directory, not a link; refusing to remove it")
    if os.path.lexists(link):
        raise LandError(f"could not remove the link {link}")


# ---------------------------------------------------------------------------
# Pure helpers
# ---------------------------------------------------------------------------


def find_control_bytes(files: dict[str, bytes]) -> list[str]:
    return sorted(name for name, data in files.items() if CONTROL_BYTES.search(data))


def split_conflicts(unmerged: Sequence[str]) -> tuple[list[str], list[str]]:
    """(the ones we union-resolve, the ones the lane must resolve)."""
    return [f for f in unmerged if f == TOTEST], [f for f in unmerged if f != TOTEST]


def slugify(branch: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]", "-", branch)


def check_branch_name(label: str, name: str) -> None:
    """Refuse the names that must never be a landing target, before any work starts."""
    if not name or name.startswith(("-", "refs/")) or name.lower() in PROTECTED:
        raise LandError(f"{label} {name!r} is protected or malformed; pass a lane branch such as feat/123-slug", EXIT_REFUSED)


def pid_alive(pid: int) -> bool:
    """Is a process with this pid running? Never os.kill on Windows (any signal there TERMINATES the process)."""
    if os.name == "nt":
        import ctypes  # noqa: PLC0415

        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        try:
            code = ctypes.c_ulong()
            return bool(kernel32.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == 259  # STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def remove_tree_no_follow(path: Path) -> None:
    """Delete a directory tree WITHOUT ever entering a junction or symlink: those are unlinked, not recursed into."""
    if os.path.isjunction(path) or os.path.islink(path):
        unlink_dir_link(path)
        return
    for entry in os.scandir(path):
        child = Path(entry.path)
        if entry.is_symlink() or os.path.isjunction(child):
            unlink_dir_link(child)
        elif entry.is_dir(follow_symlinks=False):
            remove_tree_no_follow(child)
        else:
            os.chmod(child, stat.S_IWRITE)
            child.unlink()
    os.rmdir(path)


# ---------------------------------------------------------------------------
# The landing
# ---------------------------------------------------------------------------


def run_premerge(wt: Path, venv: Path, full: bool) -> tuple[int, str]:
    """The real gate run: the venv's python reached THROUGH the worktree's junction, the worktree's sources first."""
    py = wt / VENV_REL / "Scripts" / "python.exe"
    if not py.is_file():
        py = wt / VENV_REL / "bin" / "python"
    argv = [str(py), str(wt / "scripts" / "premerge.py"), "--root", str(wt), "--python", str(py)]
    if not full:
        argv.append("--fast")
    heavy = wt / "scripts" / "heavy.py"
    if heavy.is_file():  # one machine-wide heavy slot for the whole run (RAM)
        argv = [str(py), str(heavy), "--lane", f"land-{wt.name}"[:64], "--", *argv]
    env = dict(os.environ)
    env["PYTHONPATH"] = str(wt / "worker" / "src")
    proc = subprocess.run(argv, cwd=wt, capture_output=True, check=False, env=env)
    return proc.returncode, _text(proc.stdout) + "\n" + _text(proc.stderr)


@dataclass
class Ctx:
    repo: Path
    venv: Path
    tmp_root: Path = field(default_factory=lambda: Path(tempfile.gettempdir()) / "landwt")
    gh: list[str] = field(default_factory=lambda: ["gh"])
    premerge: PremergeFn = run_premerge
    pid_alive: Callable[[int], bool] = pid_alive
    log: list[str] = field(default_factory=list)

    def say(self, message: str) -> None:
        print(message)
        self.log.append(message)


def _resolve_tip(ctx: Ctx, branch: str, sha: str) -> str:
    proc = git(ctx.repo, "rev-parse", "--verify", "-q", f"{sha}^{{commit}}", check=False)
    if proc.returncode != 0:
        raise LandError(f"no commit {sha} in this repository; fetch it or fix --sha", EXIT_REFUSED)
    full = _text(proc.stdout)
    for ref in (f"refs/heads/{branch}", f"refs/remotes/{REMOTE}/{branch}"):
        tip = git(ctx.repo, "rev-parse", "--verify", "-q", ref, check=False)
        if tip.returncode == 0:
            if _text(tip.stdout) != full:
                raise LandError(
                    f"{branch} is at {_text(tip.stdout)[:12]}, not {full[:12]}; "
                    "the lane moved on or the sha is stale. Re-read the branch tip and retry",
                    EXIT_REFUSED,
                )
            return full
    raise LandError(f"no branch {branch} locally or on {REMOTE}; check --branch", EXIT_REFUSED)


def _changed_text_files(ctx: Ctx, sha: str) -> dict[str, bytes]:
    # -z: NUL-terminated records with unquoted paths, so a name holding a tab, quote or newline is read exactly.
    raw = git(ctx.repo, "diff", "--numstat", "-z", "--no-renames", "--diff-filter=AM", f"{REMOTE}/{BASE}...{sha}").stdout
    files: dict[str, bytes] = {}
    for record in raw.split(b"\0"):
        if not record:
            continue
        added, _deleted, name_bytes = record.split(b"\t", 2)
        if added == b"-":  # git's verdict: binary
            continue
        name = name_bytes.decode("utf-8", errors="surrogateescape")
        files[name] = git(ctx.repo, "show", f"{sha}:{name}").stdout
    return files


def _remove_worktree(ctx: Ctx, wt: Path) -> None:
    """Junction first, verified gone, THEN git removes the worktree. Never the other way round."""
    unlink_dir_link(wt / VENV_REL)
    if os.path.lexists(wt / VENV_REL):
        raise LandError(f"venv link still present in {wt}; leaving the worktree in place")
    git(ctx.repo, "worktree", "remove", "--force", str(wt))


def _lock_path(ctx: Ctx, name: str) -> Path:
    return ctx.tmp_root / LOCK_DIR / f"{name}.json"


def _write_lock(ctx: Ctx, name: str) -> None:
    path = _lock_path(ctx, name)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"pid": os.getpid(), "start": time.time()}), encoding="utf-8", newline="\n")


def _run_is_live(ctx: Ctx, name: str) -> bool:
    try:
        info = json.loads(_lock_path(ctx, name).read_text(encoding="utf-8"))
        return ctx.pid_alive(int(info["pid"])) and time.time() - float(info["start"]) < LOCK_MAX_AGE_SECONDS
    except (OSError, ValueError, KeyError, TypeError):
        return False  # no readable lock: nothing is holding this directory


def _sweep_stale_dir(ctx: Ctx, stale: Path) -> None:
    """Junction first; then git; if git does not know the directory, prune and delete it without following links."""
    unlink_dir_link(stale / VENV_REL)
    if os.path.lexists(stale / VENV_REL):
        raise LandError(f"venv link still present in {stale}")
    if git(ctx.repo, "worktree", "remove", "--force", str(stale), check=False).returncode != 0 or stale.exists():
        git(ctx.repo, "worktree", "prune", check=False)
        if stale.exists():
            remove_tree_no_follow(stale)
    _lock_path(ctx, stale.name).unlink(missing_ok=True)


def _clean_stale(ctx: Ctx, slug: str) -> None:
    """Sweep only directories named exactly `<slug>-<14 digit timestamp>` that no live run holds. A failure to sweep one
    is a warning, never a reason to refuse this landing."""
    if ctx.tmp_root.is_dir():
        pattern = re.compile(re.escape(slug) + r"-\d{14}")
        for stale in sorted(ctx.tmp_root.iterdir()):
            if not (stale.is_dir() and pattern.fullmatch(stale.name)):
                continue
            if _run_is_live(ctx, stale.name):
                ctx.say(f"- leaving {stale}: a run of this branch is still using it")
                continue
            ctx.say(f"- removing a worktree left by an earlier run: {stale}")
            try:
                _sweep_stale_dir(ctx, stale)
            except (LandError, OSError) as err:
                ctx.say(f"WARNING: could not remove {stale}: {err}")
    git(ctx.repo, "worktree", "prune", check=False)


def _ident(ctx: Ctx) -> list[str]:
    """`-c` identity args for the merge commit, from the repo config or a fallback, so an empty global config cannot fail it."""

    def configured(key: str, fallback: str) -> str:
        return _text(git(ctx.repo, "config", key, check=False).stdout) or fallback

    return ["-c", f"user.name={configured('user.name', 'land_wt')}", "-c", f"user.email={configured('user.email', 'land_wt@localhost')}"]


def _merge_origin_main(ctx: Ctx, wt: Path) -> None:
    if git(wt, "merge-base", "--is-ancestor", f"{REMOTE}/{BASE}", "HEAD", check=False).returncode == 0:
        return
    ident = _ident(ctx)
    merged = git(wt, *ident, "merge", "--no-edit", f"{REMOTE}/{BASE}", check=False)
    if merged.returncode == 0:
        ctx.say(f"- merged {REMOTE}/{BASE} into the branch")
        return
    unmerged = _text(git(wt, "diff", "--name-only", "-z", "--diff-filter=U").stdout.replace(b"\0", b"\n")).splitlines()
    if not unmerged:  # failed for a reason that is not a conflict (identity, hooks, a locked index...)
        git(wt, "merge", "--abort", check=False)
        raise LandError(f"merge failed: {_text(merged.stderr or merged.stdout)}", EXIT_REFUSED)
    union, others = split_conflicts(unmerged)
    if others:
        git(wt, "merge", "--abort", check=False)
        raise LandError(
            f"merge conflict with {REMOTE}/{BASE} in: {', '.join(others)}. "
            "Merge origin/main into the lane branch, resolve these files, and land again",
            EXIT_REFUSED,
        )
    for name in union:
        parts = []
        for stage in (2, 1, 3):  # ours, base, theirs (merge-file order); a missing base (add/add) is empty
            shown = git(wt, "show", f":{stage}:{name}", check=False)
            parts.append(shown.stdout if shown.returncode == 0 else b"")
        with tempfile.TemporaryDirectory() as tmp:
            ours, base, theirs = (Path(tmp) / n for n in ("ours", "base", "theirs"))
            for path, data in zip((ours, base, theirs), parts, strict=True):
                path.write_bytes(data)
            git(wt, "merge-file", "--union", str(ours), str(base), str(theirs), check=False)
            (wt / name).write_bytes(ours.read_bytes())
        git(wt, "add", "--", name)
        ctx.say(f"- union-resolved {name}")
    git(wt, *ident, "commit", "--no-edit", "-q")


def _create_pr(ctx: Ctx, wt: Path, land_branch: str, title: str, body_file: Path) -> str:
    argv = [*ctx.gh, "pr", "create", "--base", BASE, "--head", land_branch, "--title", title, "--body-file", str(body_file)]
    try:
        proc = subprocess.run(argv, cwd=wt, capture_output=True, check=False)
    except OSError as err:  # gh missing or not executable
        raise LandError(f"{land_branch} was pushed but gh could not run ({err}). Install gh or open the PR by hand") from err
    if proc.returncode != 0:
        raise LandError(f"{land_branch} was pushed but gh pr create failed: {_text(proc.stderr or proc.stdout)}. Open the PR by hand")
    return _text(proc.stdout).splitlines()[-1] if proc.stdout.strip() else "(no url printed)"


def land(ctx: Ctx, branch: str, sha: str, title: str, body_file: Path, *, full: bool = False, land_branch: str | None = None) -> int:
    land_branch = land_branch if land_branch is not None else branch
    slug = slugify(branch)
    wt: Path | None = None
    pushed = False
    rc = EXIT_FAILED
    try:
        check_branch_name("--branch", branch)
        check_branch_name("--land-branch", land_branch)
        git(ctx.repo, "fetch", "-q", REMOTE)
        full_sha = _resolve_tip(ctx, branch, sha)
        bad = find_control_bytes(_changed_text_files(ctx, full_sha))
        if bad:
            raise LandError(
                f"control byte(s) in: {', '.join(bad)}. A backslash escape was probably mangled "
                "on write; fix those files in the lane and land again",
                EXIT_REFUSED,
            )
        ctx.tmp_root.mkdir(parents=True, exist_ok=True)
        _clean_stale(ctx, slug)
        wt = ctx.tmp_root / f"{slug}-{time.strftime('%Y%m%d%H%M%S')}"
        _write_lock(ctx, wt.name)
        git(ctx.repo, "worktree", "add", "-q", "--detach", str(wt), full_sha)
        make_dir_link(wt / VENV_REL, ctx.venv)
        _merge_origin_main(ctx, wt)
        code, output = ctx.premerge(wt, ctx.venv, full)
        if code != 0:
            log = ctx.tmp_root / "logs" / f"{wt.name}.premerge.log"
            log.parent.mkdir(exist_ok=True)
            log.write_text(output, encoding="utf-8", newline="\n")
            fails = [ln for ln in output.splitlines() if re.search(r"\b(FAIL|ERROR)\b", ln)][:30]
            raise LandError("premerge is red; nothing was pushed. " + "; ".join(s.strip() for s in fails) + f" (full log: {log})", EXIT_RED)
        git(wt, "push", REMOTE, f"HEAD:refs/heads/{land_branch}")
        pushed = True
        url = _create_pr(ctx, wt, land_branch, title, body_file)
        ctx.say(f"OK: pushed {land_branch} and opened {url}. Merge with: gh pr merge {url.rsplit('/', 1)[-1]} --merge")
        rc = EXIT_OK
    except LandError as err:
        ctx.say(f"ERROR: {err}")
        rc = err.code
    except Exception as err:  # noqa: BLE001  (anything unexpected must still clean up and exit non-zero)
        after = f"; {land_branch} was already pushed, open the PR by hand" if pushed else ""
        ctx.say(f"ERROR: unexpected {type(err).__name__}: {err}{after}")
        rc = EXIT_FAILED
    finally:
        # A cleanup failure never rewrites the outcome: it is a warning that names what was left behind.
        if wt is not None and wt.exists():
            try:
                _remove_worktree(ctx, wt)
            except LandError as err:
                ctx.say(f"WARNING: left {wt} in place: {err}. Unlink its worker\\.venv with rmdir, then `git worktree remove --force` it")
        if wt is not None and not wt.exists():
            _lock_path(ctx, wt.name).unlink(missing_ok=True)
    return rc


def _default_venv(repo: Path) -> Path:
    if (Path(sys.prefix) / "pyvenv.cfg").is_file():
        return Path(sys.prefix)
    return repo / VENV_REL


def main(argv: Sequence[str] | None = None, *, repo: Path | None = None, premerge: PremergeFn | None = None) -> int:
    repo = repo or Path(__file__).resolve().parent.parent
    ap = argparse.ArgumentParser(description="Land a committed worktree branch: verify in a throwaway worktree, then PR.")
    ap.add_argument("--branch", required=True)
    ap.add_argument("--sha", required=True, help="must be the branch tip")
    ap.add_argument("--title", required=True)
    ap.add_argument("--body-file", required=True, type=Path)
    ap.add_argument("--full", action="store_true", help="full premerge (default --fast)")
    ap.add_argument("--land-branch", default=None, help="remote branch to push (default: --branch)")
    ap.add_argument("--venv", type=Path, default=None, help="real venv to junction (default: the running venv)")
    ap.add_argument("--gh", default=os.environ.get("LAND_WT_GH", "gh"), help="path of the gh executable (one path, spaces allowed)")
    args = ap.parse_args(argv)
    if not args.body_file.is_file():
        print(f"ERROR: --body-file {args.body_file} does not exist")
        return EXIT_USAGE
    ctx = Ctx(repo=repo, venv=args.venv or _default_venv(repo), gh=[args.gh])
    if premerge is not None:
        ctx.premerge = premerge
    return land(ctx, args.branch, args.sha, args.title, args.body_file.resolve(), full=args.full, land_branch=args.land_branch)


if __name__ == "__main__":
    sys.exit(main())
