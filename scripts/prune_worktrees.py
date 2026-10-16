"""scripts/prune_worktrees.py -- remove agent worktrees whose branch already landed (#794).

    worker\\.venv\\Scripts\\python.exe scripts\\prune_worktrees.py                  # dry run: prints what it would do
    worker\\.venv\\Scripts\\python.exe scripts\\prune_worktrees.py --apply          # acts
    ... [--min-age-hours 6] [--use-gh] [--delete-merged-branches] [--no-fetch] [--tmp-root DIR]

WHY: MEASURED 2026-10-16: 151 registered worktrees, most from finished waves. Each holds a branch checked out, so a
repair lane cannot `git checkout` the original branch, and they cost disk and slow `git worktree list`.

A worktree is REMOVED only when ALL of these hold (otherwise it is KEPT, with the reason in the table):
- it is not the primary checkout (the first entry of `git worktree list`) and not the checkout this script runs from;
- it is not locked, and its directory exists;
- it has no uncommitted or untracked changes (the `worker/.venv` link is ignored for this check);
- nothing in it was modified within --min-age-hours (default 6): the worktree directory, its top-level entries, and git's
  own HEAD/index/logs for it;
- it has landed: its HEAD is an ancestor of origin/main (this also covers a detached HEAD), OR with --use-gh the
  branch has a MERGED or CLOSED pull request and no OPEN one (ONE batched `gh pr list --state all` call).

LIMITATION (not detected): a live process whose current directory is inside a worktree. Windows exposes no cheap way
to read another process's cwd (it needs NtQueryInformationProcess plus ReadProcessMemory), so --min-age-hours is the
protection: a worktree an agent is working in has recent file or index changes. A shell that merely sits in an idle,
old, clean, landed worktree can lose its cwd.

REMOVAL ORDER (MEASURED: an earlier tool deleted the real venv through a junction): every junction or symlink found
anywhere in the worktree (bounded walk that never enters one) is unlinked with os.rmdir/os.unlink, verified gone, and
only then `git worktree remove --force`. If any link cannot be unlinked the worktree is LEFT IN PLACE.

ORPHANS: directories under %TEMP%\\landwt (land_wt.py's throwaway worktrees) that git no longer lists are deleted
without following links, unless a live run holds a lock for them or they were touched within --min-age-hours.
`locks` and `logs` there are never touched.

Apply mode ends with `git worktree prune`. --delete-merged-branches then deletes local branches that are ancestors of
origin/main and are not checked out in any worktree that survives. The dry run prints the same table and changes nothing.

Exit codes: 0 done, 1 refused (no origin/main), 2 bad usage. Console output is ASCII (`OK:` / `ERROR:` / `-`).
"""

from __future__ import annotations

import argparse
import contextlib
import json
import os
import sys
import tempfile
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import land_wt as lw  # noqa: E402

git = lw.git
REMOTE_BASE = f"{lw.REMOTE}/{lw.BASE}"
EXIT_OK, EXIT_REFUSED, EXIT_USAGE = 0, 1, 2
VENV_PREFIX = "worker/.venv"
SKIP_TMP_DIRS = {lw.LOCK_DIR, "logs"}
PROTECTED_BRANCHES = {"main", "master"}


@dataclass
class Worktree:
    path: Path
    head: str = ""
    branch: str | None = None  # short name, None when detached
    locked: bool = False
    prunable: bool = False
    bare: bool = False


@dataclass
class Decision:
    path: Path
    reason: str = ""
    branch: str | None = None


@dataclass
class Report:
    removed: list[Decision] = field(default_factory=list)
    kept: list[Decision] = field(default_factory=list)
    orphans_removed: list[Decision] = field(default_factory=list)
    orphans_kept: list[Decision] = field(default_factory=list)
    branches_deleted: list[Decision] = field(default_factory=list)


@dataclass
class Ctx:
    repo: Path
    tmp_root: Path = field(default_factory=lambda: Path(tempfile.gettempdir()) / "landwt")
    min_age_hours: float = 6.0
    apply: bool = False
    use_gh: bool = False
    gh: list[str] = field(default_factory=lambda: ["gh"])
    delete_merged_branches: bool = False
    fetch: bool = True
    now: Callable[[], float] = time.time
    pid_alive: Callable[[int], bool] = lw.pid_alive
    say: Callable[[str], None] = print
    git: Callable[..., object] = lw.git  # injectable: tests spy on the calls


def _text(proc: object) -> str:
    return lw._text(proc.stdout)  # type: ignore[attr-defined]


def _norm(path: Path | str) -> str:
    return os.path.normcase(os.path.normpath(str(path)))


def list_worktrees(ctx: Ctx) -> list[Worktree]:
    raw = _text(ctx.git(ctx.repo, "worktree", "list", "--porcelain", check=True))
    out: list[Worktree] = []
    for block in raw.split("\n\n"):
        wt: Worktree | None = None
        for line in block.splitlines():
            key, _, value = line.partition(" ")
            if key == "worktree":
                wt = Worktree(Path(value))
            elif wt is None:
                continue
            elif key == "HEAD":
                wt.head = value
            elif key == "branch":
                wt.branch = value.removeprefix("refs/heads/")
            elif key == "locked":
                wt.locked = True
            elif key == "prunable":
                wt.prunable = True
            elif key == "bare":
                wt.bare = True
        if wt is not None:
            out.append(wt)
    return out


def is_ancestor_of_base(ctx: Ctx, sha: str) -> bool:
    return bool(sha) and ctx.git(ctx.repo, "merge-base", "--is-ancestor", sha, REMOTE_BASE, check=False).returncode == 0  # type: ignore[attr-defined]


def pr_states(ctx: Ctx) -> dict[str, set[str]] | None:
    """branch -> the set of its PR states, from ONE `gh pr list` call. None (with a warning) when gh cannot answer."""
    argv = [*ctx.gh, "pr", "list", "--state", "all", "--limit", "1000", "--json", "headRefName,state"]
    try:
        proc = lw.subprocess.run(argv, cwd=ctx.repo, capture_output=True, check=False)
        data = json.loads(lw._text(proc.stdout)) if proc.returncode == 0 else None
    except (OSError, ValueError):
        data = None
    if not isinstance(data, list):
        ctx.say("WARNING: `gh pr list` failed; judging by ancestry only. Run `gh auth status`, or drop --use-gh")
        return None
    states: dict[str, set[str]] = {}
    for item in data:
        if isinstance(item, dict) and item.get("headRefName"):
            states.setdefault(str(item["headRefName"]), set()).add(str(item.get("state", "")).upper())
    return states


def change_count(ctx: Ctx, wt: Path) -> int | None:
    """Uncommitted plus untracked entries, ignoring the worker/.venv link. None when git cannot say."""
    proc = ctx.git(wt, "status", "--porcelain=v1", "-z", "--untracked-files=all", check=False)
    if proc.returncode != 0:  # type: ignore[attr-defined]
        return None
    count = 0
    for record in proc.stdout.split(b"\0"):  # type: ignore[attr-defined]
        if not record:
            continue
        path = record[3:].decode("utf-8", errors="replace").replace("\\", "/")
        if path == VENV_PREFIX or path.startswith(VENV_PREFIX + "/"):
            continue
        count += 1
    return count


def _mtime(path: Path) -> float:
    try:
        return path.stat().st_mtime
    except OSError:
        return 0.0


def newest_touch(ctx: Ctx, wt: Path) -> float:
    """Newest mtime among the directory, its top-level entries, and git's HEAD/index/reflog for this worktree."""
    stamps = [_mtime(wt)]
    with contextlib.suppress(OSError):
        stamps += [_mtime(Path(e.path)) for e in os.scandir(wt) if e.name != ".git"]
    proc = ctx.git(wt, "rev-parse", "--absolute-git-dir", check=False)
    if proc.returncode == 0:  # type: ignore[attr-defined]
        gd = Path(_text(proc))
        stamps += [_mtime(gd / n) for n in ("HEAD", "index", "logs/HEAD")]
    return max(stamps)


def touched_recently(ctx: Ctx, wt: Path) -> float | None:
    """Hours since the newest touch when that is under the minimum age, else None."""
    age = (ctx.now() - newest_touch(ctx, wt)) / 3600.0
    return age if age < ctx.min_age_hours else None


def find_links(root: Path) -> list[Path]:
    """Every junction or symlink under root. Never enters one; skips the top-level .git file."""
    found: list[Path] = []
    stack = [root]
    while stack:
        cur = stack.pop()
        try:
            entries = list(os.scandir(cur))
        except OSError:
            continue
        for entry in entries:
            child = Path(entry.path)
            if cur == root and entry.name == ".git":
                continue
            if os.path.isjunction(child) or entry.is_symlink():
                found.append(child)
            elif entry.is_dir(follow_symlinks=False):
                stack.append(child)
    return found


def unlink_all_links(root: Path) -> str | None:
    """Unlink every link under root and verify. None on success, else the name of the one that would not go."""
    for link in find_links(root):
        try:
            lw.unlink_dir_link(link)
        except (lw.LandError, OSError):
            return str(link)
        if os.path.lexists(link):
            return str(link)
    return None


# ---------------------------------------------------------------------------
# Decisions
# ---------------------------------------------------------------------------


def decide(ctx: Ctx, wt: Worktree, primary: Worktree, states: dict[str, set[str]] | None) -> str | None:
    """None when the worktree may be removed, else the reason it is kept."""
    if _norm(wt.path) == _norm(primary.path):
        return "primary checkout"
    if _norm(wt.path) == _norm(ctx.repo):
        return "this checkout (the script runs from it)"
    if wt.bare:
        return "bare"
    if wt.locked:
        return "locked (git worktree unlock it to allow pruning)"
    if wt.prunable or not wt.path.is_dir():
        return "directory missing (git worktree prune clears the registration)"
    if wt.branch is not None and states is not None and "OPEN" in states.get(wt.branch, set()):
        return "open PR"
    landed = is_ancestor_of_base(ctx, wt.head)
    if not landed and wt.branch is not None and states is not None:
        landed = bool(states.get(wt.branch, set()) & {"MERGED", "CLOSED"})
    if not landed:
        return "not merged (detached, commit not in origin/main)" if wt.branch is None else "not merged into origin/main"
    count = change_count(ctx, wt.path)
    if count is None:
        return "git status failed"
    if count:
        return f"uncommitted or untracked changes ({count})"
    age = touched_recently(ctx, wt.path)
    if age is not None:
        return f"touched {age:.1f} h ago (under {ctx.min_age_hours:g} h)"
    return None


def remove_worktree(ctx: Ctx, wt: Worktree) -> str | None:
    """Junctions first, verified; then git. None on success, else why the worktree was left."""
    stuck = unlink_all_links(wt.path)
    if stuck is not None:
        return f"junction could not be unlinked: {stuck}"
    proc = ctx.git(ctx.repo, "worktree", "remove", "--force", str(wt.path), check=False)
    if proc.returncode != 0 or wt.path.exists():  # type: ignore[attr-defined]
        return f"git worktree remove failed: {lw._text(proc.stderr)[:160]}"  # type: ignore[attr-defined]
    return None


def _lock_live(ctx: Ctx, name: str) -> bool:
    lock = ctx.tmp_root / lw.LOCK_DIR / f"{name}.json"
    try:
        info = json.loads(lock.read_text(encoding="utf-8"))
        return ctx.pid_alive(int(info["pid"])) and ctx.now() - float(info["start"]) < lw.LOCK_MAX_AGE_SECONDS
    except (OSError, ValueError, KeyError, TypeError):
        return False


def sweep_orphans(ctx: Ctx, listed: set[str], report: Report) -> None:
    if not ctx.tmp_root.is_dir():
        return
    for d in sorted(ctx.tmp_root.iterdir()):
        if not d.is_dir() or d.name in SKIP_TMP_DIRS or _norm(d) in listed:
            continue
        if os.path.isjunction(d) or d.is_symlink():
            report.orphans_kept.append(Decision(d, "is itself a link; left alone"))
            continue
        if _lock_live(ctx, d.name):
            report.orphans_kept.append(Decision(d, "a live run holds it"))
            continue
        age = (ctx.now() - newest_dir_touch(d)) / 3600.0
        if age < ctx.min_age_hours:
            report.orphans_kept.append(Decision(d, f"touched {age:.1f} h ago (under {ctx.min_age_hours:g} h)"))
            continue
        if ctx.apply:
            stuck = unlink_all_links(d)
            if stuck is not None:
                report.orphans_kept.append(Decision(d, f"junction could not be unlinked: {stuck}"))
                continue
            try:
                lw.remove_tree_no_follow(d)
            except (lw.LandError, OSError) as err:
                report.orphans_kept.append(Decision(d, f"delete failed: {err}"))
                continue
            (ctx.tmp_root / lw.LOCK_DIR / f"{d.name}.json").unlink(missing_ok=True)
        report.orphans_removed.append(Decision(d, "orphan: git no longer lists it"))


def newest_dir_touch(d: Path) -> float:
    stamps = [_mtime(d)]
    with contextlib.suppress(OSError):
        stamps += [_mtime(Path(e.path)) for e in os.scandir(d)]
    return max(stamps)


def merged_free_branches(ctx: Ctx, still_checked_out: set[str]) -> list[str]:
    proc = ctx.git(ctx.repo, "for-each-ref", "--merged", REMOTE_BASE, "--format=%(refname:short)", "refs/heads", check=False)
    if proc.returncode != 0:  # type: ignore[attr-defined]
        return []
    return [b for b in _text(proc).splitlines() if b and b not in PROTECTED_BRANCHES and b not in still_checked_out]


# ---------------------------------------------------------------------------
# Orchestration and output
# ---------------------------------------------------------------------------


def prune(ctx: Ctx) -> Report:
    report = Report()
    if ctx.fetch and ctx.git(ctx.repo, "fetch", "-q", lw.REMOTE, check=False).returncode != 0:  # type: ignore[attr-defined]
        ctx.say("WARNING: `git fetch origin` failed; judging against the last fetched origin/main")
    worktrees = list_worktrees(ctx)
    if not worktrees:
        return report
    primary = worktrees[0]
    states = pr_states(ctx) if ctx.use_gh else None
    for wt in worktrees:
        reason = decide(ctx, wt, primary, states)
        if reason is not None:
            report.kept.append(Decision(wt.path, reason, wt.branch))
            continue
        if ctx.apply:
            failure = remove_worktree(ctx, wt)
            if failure is not None:
                report.kept.append(Decision(wt.path, failure, wt.branch))
                continue
        report.removed.append(Decision(wt.path, "landed and clean", wt.branch))
    sweep_orphans(ctx, {_norm(w.path) for w in worktrees}, report)
    if ctx.apply:
        ctx.git(ctx.repo, "worktree", "prune", check=False)  # the last git call of the run, before any branch deletion
    if ctx.delete_merged_branches:
        keep_out = {d.branch for d in report.kept if d.branch}
        keep_out |= {w.branch for w in worktrees if w.branch and w.path == primary.path}
        for name in merged_free_branches(ctx, keep_out):
            if ctx.apply and ctx.git(ctx.repo, "branch", "-D", name, check=False).returncode != 0:  # type: ignore[attr-defined]
                continue
            report.branches_deleted.append(Decision(ctx.repo, "ancestor of origin/main, not checked out", name))
        if ctx.apply:
            ctx.git(ctx.repo, "worktree", "prune", check=False)
    return report


def print_report(ctx: Ctx, report: Report) -> None:
    mode = "applied" if ctx.apply else "DRY RUN (nothing changed; pass --apply to act)"
    say = ctx.say
    say(f"prune_worktrees: {mode}")
    verb = "removed" if ctx.apply else "would remove"
    for title, rows in (
        (f"{verb} (worktrees)", report.removed),
        (f"{verb} (orphan temp dirs)", report.orphans_removed),
        (f"{verb} (local branches)", report.branches_deleted),
    ):
        say(f"{title}: {len(rows)}")
        for r in rows:
            say(f"  removed  {r.branch or r.path.name}" if title.endswith("(local branches)") else f"  removed  {r.path}")
    kept = report.kept + report.orphans_kept
    say(f"kept: {len(kept)}")
    for r in kept:
        say(f"  kept     {r.path}  -- {r.reason}")
    prefix = "OK" if ctx.apply else "-"
    say(
        f"{prefix}: {len(report.removed)} worktree(s), {len(report.orphans_removed)} orphan dir(s), "
        f"{len(report.branches_deleted)} branch(es) {verb}; {len(kept)} kept"
    )


def main(argv: Sequence[str] | None = None, *, repo: Path | None = None) -> int:
    ap = argparse.ArgumentParser(description="Remove worktrees whose branch already landed (dry run unless --apply).")
    ap.add_argument("--apply", action="store_true", help="act (default is a dry run)")
    ap.add_argument("--min-age-hours", type=float, default=6.0, help="keep anything touched more recently than this (default 6)")
    ap.add_argument("--use-gh", action="store_true", help="also treat a branch with a MERGED/CLOSED PR (and no OPEN one) as landed")
    ap.add_argument(
        "--delete-merged-branches", action="store_true", help="also delete local branches merged into origin/main and not checked out"
    )
    ap.add_argument("--no-fetch", dest="fetch", action="store_false", help="do not `git fetch origin` first")
    ap.add_argument("--tmp-root", type=Path, default=None, help="land_wt temp root (default %%TEMP%%\\landwt)")
    ap.add_argument("--gh", default=os.environ.get("LAND_WT_GH", "gh"), help="path of the gh executable")
    args = ap.parse_args(argv)
    if args.min_age_hours < 0:
        print("ERROR: --min-age-hours must be 0 or more")
        return EXIT_USAGE
    repo = (repo or Path(__file__).resolve().parent.parent).resolve()
    ctx = Ctx(
        repo=repo,
        min_age_hours=args.min_age_hours,
        apply=args.apply,
        use_gh=args.use_gh,
        gh=[args.gh],
        delete_merged_branches=args.delete_merged_branches,
        fetch=args.fetch,
    )
    if args.tmp_root is not None:
        ctx.tmp_root = args.tmp_root
    if git(repo, "rev-parse", "--verify", "-q", f"{REMOTE_BASE}^{{commit}}", check=False).returncode != 0:
        print(f"ERROR: no {REMOTE_BASE} in this repository. Run `git fetch origin` and retry")
        return EXIT_REFUSED
    print_report(ctx, prune(ctx))
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
