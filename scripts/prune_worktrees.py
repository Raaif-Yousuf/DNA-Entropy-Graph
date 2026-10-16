"""scripts/prune_worktrees.py -- remove agent worktrees whose branch already landed (#794).

    worker\\.venv\\Scripts\\python.exe scripts\\prune_worktrees.py                  # dry run: prints what it would do
    worker\\.venv\\Scripts\\python.exe scripts\\prune_worktrees.py --apply          # acts (fetches origin first)
    ... [--min-age-hours 6] [--use-gh] [--delete-merged-branches] [--fetch|--no-fetch] [--tmp-root DIR]

WHY: MEASURED 2026-10-16: 151 registered worktrees, most from finished waves. Each holds a branch checked out, so a
repair lane cannot `git checkout` the original branch, and they cost disk and slow `git worktree list`.

This is a deletion tool: every ambiguity is resolved toward KEEP, and the reason goes in the table.

A worktree is REMOVED only when ALL of these hold:
- it is not the primary checkout (the first entry of `git worktree list`) and not the checkout this script runs from
  (paths are compared through realpath, so 8.3 short names and junction aliases match);
- it is not locked, its directory exists, and it does not CONTAIN another worktree, this checkout or the primary one
  (agent worktrees nest under .claude/worktrees/, and removing the outer one would delete the inner one);
- it has landed: its HEAD is an ancestor of origin/main (this also covers a detached HEAD), OR with --use-gh the branch
  has a MERGED pull request whose head commit contains the worktree HEAD, and no OPEN one (ONE batched `gh pr list`).
  A CLOSED PR is not evidence of anything landing;
- nothing in it was modified within --min-age-hours (default 6): the directory, its top-level entries, and git's own
  HEAD/index/logs for it (checked BEFORE `git status`);
- it has no uncommitted or untracked changes, no ignored file other than the explicit build-output list (worker/.venv
  link, bin, obj, .artifacts, __pycache__, .pytest_cache, .ruff_cache, .mypy_cache, *.egg-info), and no file under
  app/secrets;
- every directory in it can be scanned (long-path aware) and none is a nested repository.

LIMITATION (not detected): a live process whose current directory is inside a worktree. Windows exposes no cheap way
to read another process's cwd (it needs NtQueryInformationProcess plus ReadProcessMemory), so --min-age-hours is the
protection: a worktree an agent is working in has recent file or index changes. A shell that merely sits in an idle,
old, clean, landed worktree can lose its cwd.

REMOVAL ORDER (MEASURED: an earlier tool deleted the real venv through a junction): every junction or symlink found
anywhere in the worktree (a walk that never enters one) is unlinked with os.rmdir/os.unlink, verified gone, and only
then `git worktree remove --force`. If a link will not unlink, or git then fails to remove the worktree, the links are
recreated and the worktree is left in place.

ORPHANS: directories under %TEMP%\\landwt (land_wt.py's throwaway worktrees) that git no longer lists. The root must be
named `landwt` (--allow-any-tmp-root overrides). A directory is deleted (without following links) only when it is named
`<slug>-<14 digits>`, holds a `.git` file, git can read its HEAD, that HEAD is an ancestor of origin/main, it has the
same cleanliness as a worktree, no live run holds its lock and it is older than --min-age-hours. `locks` and `logs`
are never touched. A directory git cannot read is kept: nothing can prove it holds no unpushed work.

Apply mode ends with `git worktree prune`. --delete-merged-branches then deletes local branches that are STRICT
ancestors of origin/main (or whose PR MERGED with a head containing the tip, with --use-gh) and are not checked out in
any worktree that survives; main, master, develop and release/* are never deleted. A dry run changes nothing: it does
not fetch (pass --fetch to), and it runs git with --no-optional-locks.

Exit codes: 0 done, 1 refused (no origin/main), 2 bad usage. Console output is ASCII (`OK:` / `ERROR:` / `-`).
"""

from __future__ import annotations

import argparse
import contextlib
import fnmatch
import json
import os
import re
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
TMP_ROOT_NAME = "landwt"
LANDWT_NAME = re.compile(r"^.+-\d{14}$")
PROTECTED_BRANCHES = ("main", "master", "develop", "release/*")
BUILD_OUTPUT_DIRS = {"bin", "obj", ".artifacts", "__pycache__", ".pytest_cache", ".ruff_cache", ".mypy_cache"}
GH_LIMIT = 1000
NO_LOCKS = "--no-optional-locks"


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
    tmp_root: Path = field(default_factory=lambda: Path(tempfile.gettempdir()) / TMP_ROOT_NAME)
    allow_any_tmp_root: bool = False
    min_age_hours: float = 6.0
    apply: bool = False
    use_gh: bool = False
    gh: list[str] = field(default_factory=lambda: ["gh"])
    gh_limit: int = GH_LIMIT
    delete_merged_branches: bool = False
    fetch: bool = False
    now: Callable[[], float] = time.time
    pid_alive: Callable[[int], bool] = lw.pid_alive
    say: Callable[[str], None] = print
    git: Callable[..., object] = lw.git  # injectable: tests spy on the calls


def _text(proc: object) -> str:
    return lw._text(proc.stdout)  # type: ignore[attr-defined]


def _rc(proc: object) -> int:
    return proc.returncode  # type: ignore[attr-defined]


def _norm(path: Path | str) -> str:
    """realpath + normcase: 8.3 short names, junction aliases and case all compare equal."""
    return os.path.normcase(os.path.realpath(str(path)))


def _under(child: str, parent: str) -> bool:
    return child != parent and child.startswith(parent.rstrip("\\/") + os.sep)


def _long(path: Path | str) -> str:
    """The \\\\?\\ form on Windows so a deep tree past MAX_PATH can still be scanned."""
    text = os.path.abspath(str(path))
    if os.name != "nt" or text.startswith("\\\\?\\"):
        return text
    return "\\\\?\\UNC\\" + text[2:] if text.startswith("\\\\") else "\\\\?\\" + text


def _plain(path: Path | str) -> str:
    text = str(path)
    for prefix in ("\\\\?\\UNC\\", "\\\\?\\", "\\??\\"):
        if text.startswith(prefix):
            return ("\\\\" + text[len(prefix) :]) if prefix.endswith("UNC\\") else text[len(prefix) :]
    return text


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


def is_ancestor(ctx: Ctx, sha: str, of: str) -> bool:
    return bool(sha) and bool(of) and _rc(ctx.git(ctx.repo, "merge-base", "--is-ancestor", sha, of, check=False)) == 0


def pr_states(ctx: Ctx) -> dict[str, list[tuple[str, str]]] | None:
    """branch -> [(state, headRefOid)] from ONE `gh pr list` call. None (with a warning) when gh cannot answer."""
    argv = [*ctx.gh, "pr", "list", "--state", "all", "--limit", str(ctx.gh_limit), "--json", "headRefName,state,headRefOid"]
    try:
        proc = lw.subprocess.run(argv, cwd=ctx.repo, capture_output=True, check=False)
        data = json.loads(lw._text(proc.stdout)) if proc.returncode == 0 else None
    except (OSError, ValueError):
        data = None
    if not isinstance(data, list):
        ctx.say("WARNING: `gh pr list` failed; judging by ancestry only. Run `gh auth status`, or drop --use-gh")
        return None
    if len(data) >= ctx.gh_limit:
        ctx.say(f"WARNING: gh returned {len(data)} PRs, the --limit; older PRs may be missing, so their branches are kept")
    states: dict[str, list[tuple[str, str]]] = {}
    for item in data:
        if isinstance(item, dict) and item.get("headRefName"):
            states.setdefault(str(item["headRefName"]), []).append((str(item.get("state", "")).upper(), str(item.get("headRefOid", ""))))
    return states


def pr_merged_containing(ctx: Ctx, states: dict[str, list[tuple[str, str]]] | None, branch: str | None, sha: str) -> bool:
    """A MERGED PR for the branch whose head commit contains `sha` (equal counts)."""
    if states is None or branch is None:
        return False
    return any(state == "MERGED" and is_ancestor(ctx, sha, oid) for state, oid in states.get(branch, []))


def ignored_is_build_output(path: str) -> bool:
    p = path.replace("\\", "/").strip("/")
    if p == VENV_PREFIX or p.startswith(VENV_PREFIX + "/"):
        return True
    return any(part in BUILD_OUTPUT_DIRS or part.endswith(".egg-info") for part in p.split("/"))


def dirt_reason(ctx: Ctx, wt: Path) -> str | None:
    """Why this checkout holds something that is not safely disposable, or None. Runs `git status` read-only."""
    secrets = wt / "app" / "secrets"
    if secrets.is_dir() and any(secrets.iterdir()):
        return "app/secrets holds files that cannot be recovered"
    proc = ctx.git(wt, NO_LOCKS, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored", check=False)
    if _rc(proc) != 0:
        return "git status failed"
    records = proc.stdout.split(b"\0")  # type: ignore[attr-defined]
    changes = 0
    ignored: list[str] = []
    i = 0
    while i < len(records):
        record = records[i]
        i += 1
        if len(record) < 4:
            continue
        xy = record[:2].decode("ascii", errors="replace")
        path = record[3:].decode("utf-8", errors="replace").replace("\\", "/")
        if "R" in xy or "C" in xy:
            i += 1  # a rename/copy is followed by its original path as a separate record
        if xy == "!!":
            if not ignored_is_build_output(path):
                ignored.append(path)
        elif path != VENV_PREFIX and not path.startswith(VENV_PREFIX + "/"):
            changes += 1
    if changes:
        return f"uncommitted or untracked changes ({changes})"
    if ignored:
        return f"ignored files that may matter ({len(ignored)}), e.g. {ignored[0]}"
    return None


def _mtime(path: Path | str) -> float:
    try:
        return os.stat(path).st_mtime
    except OSError:
        return 0.0


def newest_touch(ctx: Ctx, wt: Path) -> float:
    """Newest mtime among the directory, its top-level entries, and git's HEAD/index/reflog for this worktree."""
    stamps = [_mtime(wt)]
    with contextlib.suppress(OSError):
        stamps += [_mtime(e.path) for e in os.scandir(wt) if e.name != ".git"]
    proc = ctx.git(wt, "rev-parse", "--absolute-git-dir", check=False)
    if _rc(proc) == 0:
        gd = Path(_text(proc))
        stamps += [_mtime(gd / n) for n in ("HEAD", "index", "logs/HEAD")]
    return max(stamps)


def touched_recently(ctx: Ctx, wt: Path) -> float | None:
    """Hours since the newest touch when that is under the minimum age, else None."""
    age = (ctx.now() - newest_touch(ctx, wt)) / 3600.0
    return age if age < ctx.min_age_hours else None


def scan_links(root: Path) -> tuple[list[Path], list[str]]:
    """(every junction or symlink under root, reasons the tree cannot be trusted). Never enters a link or a nested
    repository; skips the top-level .git file. A directory that cannot be listed is a problem, never a silent skip."""
    links: list[Path] = []
    problems: list[str] = []
    top = _long(root)
    stack = [top]
    while stack:
        cur = stack.pop()
        try:
            entries = list(os.scandir(cur))
        except OSError as err:
            problems.append(f"could not scan {_plain(cur)}: {err.__class__.__name__}")
            continue
        for entry in entries:
            if cur == top and entry.name == ".git":
                continue
            if os.path.isjunction(entry.path) or entry.is_symlink():
                links.append(Path(entry.path))
            elif entry.is_dir(follow_symlinks=False):
                if os.path.lexists(os.path.join(entry.path, ".git")):
                    problems.append(f"nested repository or worktree at {_plain(entry.path)}")
                else:
                    stack.append(entry.path)
    return links, problems


def _restore(saved: list[tuple[Path, str]]) -> str:
    """Recreate unlinked links. Returns '' or a loud description of the ones that could not be."""
    failed = []
    for link, target in saved:
        try:
            lw.make_dir_link(Path(_plain(link)), Path(_plain(target)))
        except (lw.LandError, OSError):
            failed.append(_plain(link))
    return f" ERROR: could not recreate link(s) {', '.join(failed)}; recreate them by hand (target in the log)" if failed else ""


def remove_worktree(ctx: Ctx, wt: Worktree) -> str | None:
    """Links first, verified; then git. None on success, else why the worktree was left (links restored)."""
    links, problems = scan_links(wt.path)
    if problems:
        return f"{problems[0]}" + (f" (+{len(problems) - 1} more)" if len(problems) > 1 else "")
    saved: list[tuple[Path, str]] = []
    for link in links:
        try:
            target = os.readlink(link)
            lw.unlink_dir_link(link)
        except (lw.LandError, OSError):
            return f"junction could not be unlinked: {_plain(link)}" + _restore(saved)
        if os.path.lexists(link):
            return f"junction could not be unlinked: {_plain(link)}" + _restore(saved)
        saved.append((link, target))
    proc = ctx.git(ctx.repo, "worktree", "remove", "--force", str(wt.path), check=False)
    if _rc(proc) != 0 or wt.path.exists():
        note = _restore(saved) if wt.path.exists() else ""
        return f"git worktree remove failed: {lw._text(proc.stderr)[:160]}{note}"  # type: ignore[attr-defined]
    return None


# ---------------------------------------------------------------------------
# Decisions
# ---------------------------------------------------------------------------


def decide(ctx: Ctx, wt: Worktree, primary: Worktree, all_paths: list[Path], states: dict[str, list[tuple[str, str]]] | None) -> str | None:
    """None when the worktree may be removed, else the reason it is kept."""
    me = _norm(wt.path)
    if me == _norm(primary.path):
        return "primary checkout"
    if me == _norm(ctx.repo):
        return "this checkout (the script runs from it)"
    if wt.bare:
        return "bare"
    if wt.locked:
        return "locked (git worktree unlock it to allow pruning)"
    if wt.prunable or not wt.path.is_dir():
        return "directory missing (git worktree prune clears the registration)"
    if any(_under(_norm(p), me) for p in [*all_paths, ctx.repo]):
        return "contains another worktree or this checkout (it would be deleted with it)"
    if wt.branch is not None and states is not None and any(s == "OPEN" for s, _ in states.get(wt.branch, [])):
        return "open PR"
    if not (is_ancestor(ctx, wt.head, REMOTE_BASE) or pr_merged_containing(ctx, states, wt.branch, wt.head)):
        return "not merged (detached, commit not in origin/main)" if wt.branch is None else "not merged into origin/main"
    age = touched_recently(ctx, wt.path)
    if age is not None:
        return f"touched {age:.1f} h ago (under {ctx.min_age_hours:g} h)"
    return dirt_reason(ctx, wt.path)


def _lock_live(ctx: Ctx, name: str) -> bool:
    lock = ctx.tmp_root / lw.LOCK_DIR / f"{name}.json"
    try:
        info = json.loads(lock.read_text(encoding="utf-8"))
        return ctx.pid_alive(int(info["pid"])) and ctx.now() - float(info["start"]) < lw.LOCK_MAX_AGE_SECONDS
    except (OSError, ValueError, KeyError, TypeError):
        return False


def newest_dir_touch(d: Path) -> float:
    stamps = [_mtime(d)]
    with contextlib.suppress(OSError):
        stamps += [_mtime(e.path) for e in os.scandir(d)]
    return max(stamps)


def orphan_keep_reason(ctx: Ctx, d: Path) -> str | None:
    if os.path.isjunction(d) or d.is_symlink():
        return "is itself a link; left alone"
    if not LANDWT_NAME.match(d.name):
        return "name is not <slug>-<14 digits>; not a landing directory"
    if not (d / ".git").is_file():
        return "no .git file; not a worktree checkout"
    if _lock_live(ctx, d.name):
        return "a live run holds it"
    age = (ctx.now() - newest_dir_touch(d)) / 3600.0
    if age < ctx.min_age_hours:
        return f"touched {age:.1f} h ago (under {ctx.min_age_hours:g} h)"
    head = ctx.git(d, "rev-parse", "HEAD", check=False)
    if _rc(head) != 0:
        return "git cannot read it, so unpushed work cannot be ruled out; look, then delete it by hand"
    if not is_ancestor(ctx, _text(head), REMOTE_BASE):
        return "not merged into origin/main"
    return dirt_reason(ctx, d)


def sweep_orphans(ctx: Ctx, listed: set[str], report: Report) -> None:
    if not ctx.tmp_root.is_dir():
        return
    if ctx.tmp_root.name.lower() != TMP_ROOT_NAME and not ctx.allow_any_tmp_root:
        ctx.say(f"WARNING: --tmp-root {ctx.tmp_root} is not named {TMP_ROOT_NAME}; orphan sweep skipped (--allow-any-tmp-root to override)")
        return
    for d in sorted(ctx.tmp_root.iterdir()):
        if not d.is_dir() or d.name in SKIP_TMP_DIRS or _norm(d) in listed:
            continue
        reason = orphan_keep_reason(ctx, d)
        if reason is not None:
            report.orphans_kept.append(Decision(d, reason))
            continue
        if ctx.apply:
            links, problems = scan_links(d)
            if problems:
                report.orphans_kept.append(Decision(d, problems[0]))
                continue
            try:
                for link in links:
                    lw.unlink_dir_link(link)
                    if os.path.lexists(link):
                        raise lw.LandError(f"could not unlink {_plain(link)}")
                lw.remove_tree_no_follow(d)
            except (lw.LandError, OSError) as err:
                report.orphans_kept.append(Decision(d, f"delete failed: {err}"))
                continue
            (ctx.tmp_root / lw.LOCK_DIR / f"{d.name}.json").unlink(missing_ok=True)
        report.orphans_removed.append(Decision(d, "orphan: git no longer lists it"))


def deletable_branches(ctx: Ctx, still_checked_out: set[str], states: dict[str, list[tuple[str, str]]] | None) -> list[str]:
    base = _text(ctx.git(ctx.repo, "rev-parse", REMOTE_BASE, check=False))
    merged_proc = ctx.git(ctx.repo, "for-each-ref", "--merged", REMOTE_BASE, "--format=%(refname:short)", "refs/heads", check=False)
    merged = set(_text(merged_proc).splitlines()) if _rc(merged_proc) == 0 else set()
    refs = ctx.git(ctx.repo, "for-each-ref", "--format=%(refname:short)%09%(objectname)", "refs/heads", check=False)
    out: list[str] = []
    for line in _text(refs).splitlines():
        name, _, sha = line.partition("\t")
        if not name or name in still_checked_out or any(fnmatch.fnmatchcase(name, pat) for pat in PROTECTED_BRANCHES):
            continue
        strict_ancestor = name in merged and sha != base
        if strict_ancestor or pr_merged_containing(ctx, states, name, sha):
            out.append(name)
    return out


# ---------------------------------------------------------------------------
# Orchestration and output
# ---------------------------------------------------------------------------


def prune(ctx: Ctx) -> Report:
    report = Report()
    if ctx.fetch and _rc(ctx.git(ctx.repo, "fetch", "-q", lw.REMOTE, check=False)) != 0:
        ctx.say("WARNING: `git fetch origin` failed; judging against the last fetched origin/main")
    worktrees = list_worktrees(ctx)
    if not worktrees:
        return report
    primary = worktrees[0]
    states = pr_states(ctx) if ctx.use_gh else None
    all_paths = [w.path for w in worktrees]
    for wt in worktrees:
        reason = decide(ctx, wt, primary, [p for p in all_paths if p != wt.path], states)
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
        ctx.git(ctx.repo, "worktree", "prune", check=False)
    if ctx.delete_merged_branches:
        keep_out = {d.branch for d in report.kept if d.branch}
        for name in deletable_branches(ctx, keep_out, states):
            if ctx.apply and _rc(ctx.git(ctx.repo, "branch", "-D", name, check=False)) != 0:
                continue
            report.branches_deleted.append(Decision(ctx.repo, "landed, not checked out", name))
        if ctx.apply:
            ctx.git(ctx.repo, "worktree", "prune", check=False)
    return report


def print_report(ctx: Ctx, report: Report) -> None:
    say = ctx.say
    say(f"prune_worktrees: {'applied' if ctx.apply else 'DRY RUN (nothing changed; pass --apply to act)'}")
    verb = "removed" if ctx.apply else "would remove"
    for title, rows, is_branch in (
        ("worktrees", report.removed, False),
        ("orphan temp dirs", report.orphans_removed, False),
        ("local branches", report.branches_deleted, True),
    ):
        say(f"{verb} ({title}): {len(rows)}")
        for r in rows:
            say(f"  {verb}  {r.branch if is_branch else r.path}")
    kept = report.kept + report.orphans_kept
    say(f"kept: {len(kept)}")
    for r in kept:
        say(f"  kept     {r.path}  -- {r.reason}")
    say(
        f"{'OK' if ctx.apply else '-'}: {len(report.removed)} worktree(s), {len(report.orphans_removed)} orphan dir(s), "
        f"{len(report.branches_deleted)} branch(es) {verb}; {len(kept)} kept"
    )


def parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    ap = argparse.ArgumentParser(description="Remove worktrees whose branch already landed (dry run unless --apply).")
    ap.add_argument("--apply", action="store_true", help="act (default is a dry run)")
    ap.add_argument("--min-age-hours", type=float, default=6.0, help="keep anything touched more recently than this (default 6)")
    ap.add_argument("--use-gh", action="store_true", help="also treat a branch with a MERGED PR (head contains the worktree HEAD) as landed")
    ap.add_argument("--delete-merged-branches", action="store_true", help="also delete local branches landed in origin/main and not checked out")
    ap.add_argument("--fetch", dest="fetch_flag", action="store_true", help="`git fetch origin` first (implied by --apply)")
    ap.add_argument("--no-fetch", action="store_true", help="never fetch, even with --apply")
    ap.add_argument("--tmp-root", type=Path, default=None, help="land_wt temp root (default %%TEMP%%\\landwt)")
    ap.add_argument("--allow-any-tmp-root", action="store_true", help="allow a --tmp-root not named landwt (its old subdirs may be deleted)")
    ap.add_argument("--gh", default=os.environ.get("LAND_WT_GH", "gh"), help="path of the gh executable")
    return ap.parse_args(argv)


def ctx_from_args(args: argparse.Namespace, repo: Path) -> Ctx:
    ctx = Ctx(
        repo=repo,
        min_age_hours=args.min_age_hours,
        apply=args.apply,
        use_gh=args.use_gh,
        gh=[args.gh],
        delete_merged_branches=args.delete_merged_branches,
        fetch=(args.fetch_flag or args.apply) and not args.no_fetch,
        allow_any_tmp_root=args.allow_any_tmp_root,
    )
    if args.tmp_root is not None:
        ctx.tmp_root = args.tmp_root
    return ctx


def main(argv: Sequence[str] | None = None, *, repo: Path | None = None) -> int:
    args = parse_args(argv)
    if args.min_age_hours < 0:
        print("ERROR: --min-age-hours must be 0 or more")
        return EXIT_USAGE
    if args.tmp_root is not None and args.tmp_root.name.lower() != TMP_ROOT_NAME and not args.allow_any_tmp_root:
        print(f"ERROR: --tmp-root must be a directory named {TMP_ROOT_NAME}. Pass the real one, or add --allow-any-tmp-root if you mean it")
        return EXIT_USAGE
    repo = (repo or Path(__file__).resolve().parent.parent).resolve()
    ctx = ctx_from_args(args, repo)
    if git(repo, "rev-parse", "--verify", "-q", f"{REMOTE_BASE}^{{commit}}", check=False).returncode != 0:
        print(f"ERROR: no {REMOTE_BASE} in this repository. Run `git fetch origin` and retry")
        return EXIT_REFUSED
    print_report(ctx, prune(ctx))
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
