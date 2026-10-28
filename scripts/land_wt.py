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
   Merging stays a human call (`gh pr merge <n> --merge`) unless `--merge-when-green` is passed (#666):
   then, after the PR opens, it polls `gh pr checks <pr> --json name,bucket,state` every `--checks-poll`
   seconds up to `--checks-timeout`, refuses at once when any check is fail/cancel (naming them), refuses
   when anything is still pending (or no check exists) at the timeout, and only when every check is
   pass/skipping runs `gh pr merge <pr> --merge` (a merge commit). Either refusal leaves the PR open.
   `--rerun-failed-once` (default on, `--no-rerun-failed-once` to disable): the first red result re-runs each failed
   run once (`gh run rerun <run> --failed`, after the other checks settle), appends a line to
   `<git-dir>/flaky-checks.log` and waits again; a second red refuses.
8. ALWAYS, success or not: unlink the junction with `os.rmdir`, check it is gone, and only then
   `git worktree remove --force`. MEASURED: an earlier tool deleted the real venv through a junction.
   If the junction cannot be removed the worktree is LEFT IN PLACE (and named) rather than removed.

Exit codes: 0 PR opened (and merged, with --merge-when-green), 1 refused (nothing pushed), 2 bad usage, 3 premerge red,
4 failed after work began, 5 a PR check failed (PR open, not merged), 6 a PR check still pending at the timeout (PR open, not merged).
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
EXIT_CHECKS_RED, EXIT_CHECKS_PENDING = 5, 6
CHECKS_TIMEOUT_SECONDS = 30 * 60
CHECKS_POLL_SECONDS = 20.0
# `gh pr checks` buckets: pass and skipping are green, fail and cancel are red, anything else (pending) is not done.
GREEN_BUCKETS = {"pass", "skipping"}
RED_BUCKETS = {"fail", "cancel"}

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
    sleep: Callable[[float], None] = time.sleep  # injectable: tests never wait on a real clock
    monotonic: Callable[[], float] = time.monotonic
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


def _write_lock(ctx: Ctx, name: str, sha: str | None = None) -> None:
    """`sha` is the commit the throwaway checks out: scripts/prune_worktrees.py (#794) reads it to judge a leftover."""
    path = _lock_path(ctx, name)
    path.parent.mkdir(parents=True, exist_ok=True)
    record: dict[str, object] = {"pid": os.getpid(), "start": time.time()}
    if sha:
        record["sha"] = sha
    path.write_text(json.dumps(record), encoding="utf-8", newline="\n")


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


RESW_PATH = "app/src/DnaEntropyGraph.App/Strings/en-US/Resources.resw"


def _users_global_attributes(repo: Path) -> str:
    """The text of the user's global git attributes file (core.attributesFile, else the XDG default), or ''."""
    configured = _text(git(repo, "config", "--path", "--get", "core.attributesFile", check=False).stdout).strip()
    if configured:
        path = Path(configured)
    else:
        xdg = os.environ.get("XDG_CONFIG_HOME")
        path = (Path(xdg) if xdg else Path.home() / ".config") / "git" / "attributes"
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return ""


def resw_driver_flags(repo: Path) -> tuple[list[str], Path]:
    """(`-c` flags that route Resources.resw through scripts/resw_merge.py for ONE git command, the temp
    attributes file they name). The caller deletes the file when the command is done.

    Passed per command instead of persisted, so landing needs no one-time setup and nothing here leaks into
    the shared repo config. (scripts/setup_git_drivers.py persists the same routing for ordinary merges: git reads
    info/attributes from the COMMON dir only, MEASURED 2026-10-16, a linked worktree's own copy is ignored, so that
    setup writes the common dir's file.) The attribute line goes in a unique temp file used
    as core.attributesFile; that setting replaces the user's global attributes, so the file is the user's
    own global attributes text plus the resw line. The script is the one beside this file (the lane
    branch may not have it) and the interpreter is the one running land_wt (the venv's).
    """
    script = (Path(__file__).resolve().parent / "resw_merge.py").as_posix()
    mine = _users_global_attributes(repo)
    handle, name = tempfile.mkstemp(prefix="landwt-resw-", suffix=".gitattributes")
    attrs = Path(name)
    with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(mine if mine.endswith("\n") or not mine else mine + "\n")
        fh.write(f"{RESW_PATH} merge=resw\n")
    driver = f'"{Path(sys.executable).as_posix()}" "{script}" %O %A %B'
    return ["-c", f"merge.resw.driver={driver}", "-c", f"core.attributesFile={attrs.as_posix()}"], attrs


def _merge_origin_main(ctx: Ctx, wt: Path) -> None:
    if git(wt, "merge-base", "--is-ancestor", f"{REMOTE}/{BASE}", "HEAD", check=False).returncode == 0:
        return
    ident = _ident(ctx)
    flags, attrs = resw_driver_flags(ctx.repo)
    try:
        merged = git(wt, *ident, *flags, "merge", "--no-edit", f"{REMOTE}/{BASE}", check=False)
    finally:
        attrs.unlink(missing_ok=True)
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


def _read_checks(ctx: Ctx, pr: str) -> list[dict[str, str]] | None:
    """The PR's checks as `[{"name", "bucket", "state"}]`, or None when gh has none to report yet (it prints
    "no checks reported" and exits 1 right after a PR opens). Exit 8 means "pending" and still carries the JSON."""
    argv = [*ctx.gh, "pr", "checks", pr, "--json", "name,bucket,state,link"]
    try:
        proc = subprocess.run(argv, capture_output=True, check=False)
    except OSError as err:
        raise LandError(f"gh could not run ({err}). Install gh, then run `gh pr checks {pr} --watch` and merge by hand") from err
    try:
        data = json.loads(_text(proc.stdout))
    except ValueError:
        return None
    return [c for c in data if isinstance(c, dict)] if isinstance(data, list) else None


def _names(checks: Sequence[dict[str, str]]) -> str:
    return ", ".join(str(c.get("name", "?")) for c in checks)


RUN_ID = re.compile(r"/actions/runs/(\d+)")


def _run_ids(checks: Sequence[dict[str, str]]) -> list[str]:
    """The distinct GitHub Actions run ids behind these checks (from their `link`), in first-seen order."""
    ids: list[str] = []
    for c in checks:
        match = RUN_ID.search(str(c.get("link", "")))
        if match and match.group(1) not in ids:
            ids.append(match.group(1))
    return ids


def _log_flaky(ctx: Ctx, pr: str, red: Sequence[dict[str, str]], runs: Sequence[str]) -> None:
    """Append one line to `<git-dir>/flaky-checks.log`: a failed check that a rerun was spent on. Best effort."""
    try:
        git_dir = Path(_text(git(ctx.repo, "rev-parse", "--absolute-git-dir").stdout))
        line = f"{time.strftime('%Y-%m-%dT%H:%M:%S')} pr={pr} checks={_names(red).replace(', ', ',')} runs={','.join(runs)}\n"
        with (git_dir / "flaky-checks.log").open("a", encoding="utf-8", newline="\n") as fh:
            fh.write(line)
    except (LandError, OSError) as err:
        ctx.say(f"WARNING: could not write flaky-checks.log: {err}")


def _rerun_failed(ctx: Ctx, pr: str, red: Sequence[dict[str, str]]) -> bool:
    """`gh run rerun <run> --failed` once for each failed run. False when it could not be done for every run."""
    runs = _run_ids(red)
    if not runs:
        return False
    _log_flaky(ctx, pr, red, runs)
    ok = True
    for run in runs:
        try:
            proc = subprocess.run([*ctx.gh, "run", "rerun", run, "--failed"], capture_output=True, check=False)
        except OSError:
            return False
        if proc.returncode != 0:
            ctx.say(f"WARNING: `gh run rerun {run} --failed` failed: {_text(proc.stderr or proc.stdout)}")
            ok = False
    if ok:
        ctx.say(f"- failed check(s) {_names(red)} re-run once (flaky-checks.log); waiting again")
    return ok


def _merge_when_green(ctx: Ctx, pr: str, timeout: float, poll: float, rerun_failed_once: bool = False) -> int:
    """Wait for every check on `pr`, then merge. Never merges unless at least one check exists and ALL are green.

    With `rerun_failed_once`, the first red result (once no check is still running, because `gh run rerun --failed`
    needs a finished run) re-runs the failed runs a single time and waits again with a fresh deadline; a second red refuses."""
    deadline = ctx.monotonic() + timeout
    rerun_spent = False
    while True:
        checks = _read_checks(ctx, pr)
        waiting: list[dict[str, str]] = []
        if checks:
            red = [c for c in checks if c.get("bucket") in RED_BUCKETS]
            waiting = [c for c in checks if c.get("bucket") not in GREEN_BUCKETS and c not in red]
            if red and rerun_failed_once and not rerun_spent and waiting:
                pass  # a rerun needs the run finished: keep polling until the rest settle (still bounded by the deadline)
            elif red and rerun_failed_once and not rerun_spent:
                rerun_spent = True
                if _rerun_failed(ctx, pr, red):
                    deadline = ctx.monotonic() + timeout
                    ctx.sleep(poll)  # let the rerun replace the stale red before it is read again
                    continue
                ctx.say(f"ERROR: not merging {pr}: failed check(s) {_names(red)} could not be re-run. Re-run them by hand; the PR stays open")
                return EXIT_CHECKS_RED
            elif red:
                again = " (still red after one re-run)" if rerun_spent else ""
                ctx.say(f"ERROR: not merging {pr}: failed check(s){again}: {_names(red)}. Fix them and push; the PR stays open")
                return EXIT_CHECKS_RED
            if not waiting and not red:
                break
        if ctx.monotonic() >= deadline:
            what = f"still pending: {_names(waiting)}" if waiting else "no checks were reported"
            ctx.say(
                f"ERROR: not merging {pr}: {what} after {int(timeout)} s. Run `gh pr checks {pr} --watch`, then merge by hand; the PR stays open"
            )
            return EXIT_CHECKS_PENDING
        ctx.sleep(min(poll, deadline - ctx.monotonic()))
    argv = [*ctx.gh, "pr", "merge", pr, "--merge"]
    try:
        proc = subprocess.run(argv, capture_output=True, check=False)
    except OSError as err:
        raise LandError(f"gh could not run ({err}). Merge {pr} by hand with `gh pr merge --merge`") from err
    if proc.returncode != 0:
        raise LandError(f"all checks passed but `gh pr merge` failed: {_text(proc.stderr or proc.stdout)}. Merge {pr} by hand")
    ctx.say(f"OK: all checks passed; merged {pr}")
    return EXIT_OK


def land(
    ctx: Ctx,
    branch: str,
    sha: str,
    title: str,
    body_file: Path,
    *,
    full: bool = False,
    land_branch: str | None = None,
    merge_when_green: bool = False,
    checks_timeout: float = CHECKS_TIMEOUT_SECONDS,
    checks_poll: float = CHECKS_POLL_SECONDS,
    rerun_failed_once: bool = True,
) -> int:
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
        _write_lock(ctx, wt.name, full_sha)
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
        _record_landed(ctx, url, full_sha)
        if merge_when_green:
            ctx.say(f"OK: pushed {land_branch} and opened {url}; waiting for its checks")
            rc = _merge_when_green(ctx, url, checks_timeout, checks_poll, rerun_failed_once)
        else:
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


def _record_landed(ctx: Ctx, url: str, sha: str) -> None:
    """Tell the `gh pr merge` hook (#451) this PR passed premerge in the throwaway worktree. Advisory only."""
    pr = url.rsplit("/", 1)[-1].strip()
    if not pr.isdigit():
        return
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import premerge_stamp

        premerge_stamp.record_landed(ctx.repo, pr, sha)
    except Exception as err:  # noqa: BLE001  (a missing marker only makes the hook ask for premerge by hand)
        ctx.say(f"WARNING: could not record the landed PR for the merge hook ({err})")


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
    ap.add_argument(
        "--merge-when-green", action="store_true", help="after the PR opens, wait for every check and merge (merge commit) only if none failed"
    )
    ap.add_argument(
        "--checks-timeout", type=float, default=CHECKS_TIMEOUT_SECONDS, help="seconds to wait for checks before refusing (default 1800)"
    )
    ap.add_argument("--checks-poll", type=float, default=CHECKS_POLL_SECONDS, help="seconds between check polls (default 20)")
    ap.add_argument(
        "--rerun-failed-once",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="with --merge-when-green: re-run failed checks once (flaky runners) before refusing; --no-rerun-failed-once disables",
    )
    ap.add_argument("--venv", type=Path, default=None, help="real venv to junction (default: the running venv)")
    ap.add_argument("--gh", default=os.environ.get("LAND_WT_GH", "gh"), help="path of the gh executable (one path, spaces allowed)")
    args = ap.parse_args(argv)
    if not args.body_file.is_file():
        print(f"ERROR: --body-file {args.body_file} does not exist")
        return EXIT_USAGE
    ctx = Ctx(repo=repo, venv=args.venv or _default_venv(repo), gh=[args.gh])
    if premerge is not None:
        ctx.premerge = premerge
    return land(
        ctx,
        args.branch,
        args.sha,
        args.title,
        args.body_file.resolve(),
        full=args.full,
        land_branch=args.land_branch,
        merge_when_green=args.merge_when_green,
        checks_timeout=args.checks_timeout,
        checks_poll=args.checks_poll,
        rerun_failed_once=args.rerun_failed_once,
    )


if __name__ == "__main__":
    sys.exit(main())
