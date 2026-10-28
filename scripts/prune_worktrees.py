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
- it is not locked, its path is not itself a link, its directory exists, and it does not CONTAIN another worktree, this
  checkout or the primary one (agent worktrees nest under .claude/worktrees/, and removing the outer one would delete
  the inner one);
- it has landed: its HEAD is an ancestor of origin/main (this also covers a detached HEAD), OR with --use-gh the branch
  has a MERGED pull request INTO main whose head commit contains the worktree HEAD, and no OPEN one (ONE batched
  `gh pr list`). A CLOSED PR, or a PR merged into another branch (a stacked PR), is not evidence of anything landing;
- it has a commit of its own, or is no longer fresh: a HEAD that sits ON origin/main's first-parent history (a lane
  created from main that has not committed yet) is KEPT while it was touched within --fresh-lane-hours (default 48),
  because nothing distinguishes it from a lane that was just started; older than that it is a stale checkout and every
  other rule still applies. Real feature branches merge in by a merge commit, so their tips are off that line;
- no rebase, merge, cherry-pick, revert or bisect is in progress;
- nothing in it was modified within --min-age-hours (default 6): the directory, its entries and THEIR entries (two
  levels), and git's HEAD/index/ORIG_HEAD/logs for it. A timestamp that cannot be read means "age unknown" = kept.
  Deeper in-place edits are NOT seen (a tracked-file edit shows up in `git status` anyway; an edit of an ignored file
  deeper than two levels does not, and the ignored-file rule below is what protects it);
- it has no uncommitted or untracked changes, no skip-worktree or assume-unchanged file, no ignored file other than
  build output at its known roots (app/{src,tests,tools}/<project>/{bin,obj}, .hypothesis, .artifacts, __pycache__ under worker or scripts,
  .pytest_cache/.ruff_cache/.mypy_cache at the root, worker or scripts, worker/*.egg-info), `worker/.venv` only when it
  is a link (a real directory there is kept), and no file under app/secrets;
- every directory in it can be scanned (long-path aware) and none is a nested repository. A dry run scans too, so it
  predicts apply.

LIMITATION (not detected): a live process whose current directory is inside a worktree. Windows exposes no cheap way
to read another process's cwd (it needs NtQueryInformationProcess plus ReadProcessMemory), so --min-age-hours is the
protection: a worktree an agent is working in has recent file or index changes. A shell that merely sits in an idle,
old, clean, landed worktree can lose its cwd.

REMOVAL ORDER (MEASURED: an earlier tool deleted the real venv through a junction): every junction or symlink found
anywhere in the worktree (a walk that never enters one) is unlinked with os.rmdir/os.unlink, verified gone, and only
then `git worktree remove --force`. If a link will not unlink, git then fails, or the run is interrupted, the links are
recreated and the worktree is left in place. If git already gutted the tree before failing (a link's parent directory
is gone) nothing is recreated inside it and the reason says PARTIAL, naming the link to recreate by hand.

ORPHANS: directories under %TEMP%\\landwt (land_wt.py's throwaway worktrees) that git no longer lists. The root must be
named `landwt` (--allow-any-tmp-root overrides). A directory is a candidate only when it is named `<slug>-<14 digits>`,
holds a `.git` file, no live run holds its lock and it is older than --min-age-hours; it is then deleted (without
following links) when EITHER git can read it and it is clean with HEAD in origin/main, OR (the real case: `git worktree
prune` already deleted its admin dir, so git cannot read it) land_wt's own lock record names the commit it checked out
(`sha`, written by land_wt since #794) and that commit is reachable from a remote-tracking ref, i.e. pushed. By
construction a land_wt throwaway is a detached checkout of an existing commit plus a local merge commit that is rebuilt
from origin/main, so the only thing deleting it can lose is a half-finished merge. Leftovers from before the lock
recorded a sha, or whose commit is not pushed, are kept for a human. `locks` and `logs` are never touched.

Apply mode ends with `git worktree prune`. --delete-merged-branches then deletes local branches whose tip is merged
into origin/main but is NOT on origin/main's first-parent history (a pre-created lane from an older main is, a real
merged feature branch is not), or whose PR MERGED into main with a head containing the tip (--use-gh), and that are not
checked out in any worktree that survives; main, master, develop and release/* are never deleted. A dry run changes
nothing: it does not fetch (pass --fetch to), and it runs git with --no-optional-locks.

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
EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"
EXIT_OK, EXIT_REFUSED, EXIT_USAGE = 0, 1, 2
VENV_PREFIX = "worker/.venv"
SKIP_TMP_DIRS = {lw.LOCK_DIR, "logs"}
TMP_ROOT_NAME = "landwt"
LANDWT_NAME = re.compile(r"^.+-\d{14}$")
PROTECTED_BRANCHES = ("main", "master", "develop", "release/*")
BUILD_OUTPUT = re.compile(
    r"^(?:app/(?:src|tests|tools)/[^/]+/(?:bin|obj)"
    r"|(?:app/)?\.artifacts"
    r"|(?:(?:worker|scripts)/(?:[^/]+/)*)?__pycache__"
    r"|(?:(?:worker|scripts)/)?\.(?:pytest|ruff|mypy)_cache"
    r"|(?:worker/)?\.hypothesis"
    r"|worker/(?:src/)?[^/]+\.egg-info)(?:/|$)"
)
IN_PROGRESS = ("rebase-merge", "rebase-apply", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG")
GIT_STATE_FILES = ("HEAD", "index", "ORIG_HEAD", "logs/HEAD")
GH_LIMIT = 1000
PrStates = dict[str, list[tuple[str, str, str]]]  # branch -> [(state, headRefOid, baseRefName)]
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
    sha: str = ""  # what was deleted, so a delete can be undone with `git branch <name> <sha>`


@dataclass
class Report:
    removed: list[Decision] = field(default_factory=list)
    kept: list[Decision] = field(default_factory=list)
    orphans_removed: list[Decision] = field(default_factory=list)
    orphans_kept: list[Decision] = field(default_factory=list)
    branches_deleted: list[Decision] = field(default_factory=list)
    remote_deleted: list[Decision] = field(default_factory=list)


@dataclass
class Ctx:
    repo: Path
    tmp_root: Path = field(default_factory=lambda: Path(tempfile.gettempdir()) / TMP_ROOT_NAME)
    allow_any_tmp_root: bool = False
    min_age_hours: float = 6.0
    fresh_lane_hours: float = 48.0
    apply: bool = False
    use_gh: bool = False
    gh: list[str] = field(default_factory=lambda: ["gh"])
    gh_limit: int = GH_LIMIT
    delete_merged_branches: bool = False
    contained_in: list[str] = field(default_factory=list)  # extra refs (queued or held tips) whose content counts as 'landed'
    remote: bool = False  # also delete contained branches on origin (dry run unless apply)
    fetch: bool = False
    now: Callable[[], float] = time.time
    pid_alive: Callable[[int], bool] = lw.pid_alive
    say: Callable[[str], None] = print
    git: Callable[..., object] = lw.git  # injectable: tests spy on the calls
    _main_line: set[str] | None = field(default=None, init=False, repr=False)
    _targets: dict[str, str] | None = field(default=None, init=False, repr=False)  # ref -> sha: origin/main first, then the keep tips
    _contained: dict[tuple[str, bool], bool] = field(default_factory=dict, init=False, repr=False)
    _remote_targets: dict[str, str] | None = field(default=None, init=False, repr=False)
    _merge_neutralizers: list[str] | None = field(default=None, init=False, repr=False)
    _neutralizers_ready: bool = field(default=False, init=False, repr=False)
    _repo_slug: str | None = field(default=None, init=False, repr=False)
    _repo_slug_known: bool = field(default=False, init=False, repr=False)


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


def gh_repo_args(ctx: Ctx) -> list[str]:
    """`--repo owner/name` taken from origin's configured URL, so gh asks about the repository we are about to delete from and not
    whatever `gh repo set-default` (or a fork) says. An origin that is not a GitHub URL adds nothing: gh then decides alone."""
    if not ctx._repo_slug_known:
        proc = ctx.git(ctx.repo, "config", "--get", f"remote.{lw.REMOTE}.url", check=False)
        match = re.search(r"github\.com[:/]+([^/\s]+)/([^/\s]+?)(?:\.git)?/?$", _text(proc)) if _rc(proc) == 0 else None
        ctx._repo_slug = f"{match.group(1)}/{match.group(2)}" if match else None
        ctx._repo_slug_known = True
    return ["--repo", ctx._repo_slug] if ctx._repo_slug else []


def pr_states(ctx: Ctx) -> PrStates | None:
    """branch -> [(state, headRefOid, baseRefName)] from ONE `gh pr list` call. None (with a warning) when gh cannot answer."""
    argv = [
        *ctx.gh,
        "pr",
        "list",
        *gh_repo_args(ctx),
        "--state",
        "all",
        "--limit",
        str(ctx.gh_limit),
        "--json",
        "headRefName,state,headRefOid,baseRefName",
    ]
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
    states: PrStates = {}
    for item in data:
        if isinstance(item, dict) and item.get("headRefName"):
            row = (str(item.get("state", "")).upper(), str(item.get("headRefOid", "")), str(item.get("baseRefName", "")))
            states.setdefault(str(item["headRefName"]), []).append(row)
    return states


def open_prs(ctx: Ctx) -> tuple[set[str], set[str]] | None:
    """(head branches, base branches) of every OPEN PR, from one `gh pr list --state open`. Only open PRs are asked for, so
    the list is small; if it still reaches --limit it may be truncated and None is returned (the caller refuses all remote
    deletion rather than treat a missing entry as 'no open PR'). None, with a warning, when gh cannot answer."""
    argv = [
        *ctx.gh,
        "pr",
        "list",
        *gh_repo_args(ctx),
        "--state",
        "open",
        "--limit",
        str(ctx.gh_limit),
        "--json",
        "headRefName,baseRefName,state",
    ]
    try:
        proc = lw.subprocess.run(argv, cwd=ctx.repo, capture_output=True, check=False)
        data = json.loads(lw._text(proc.stdout)) if proc.returncode == 0 else None
    except (OSError, ValueError):
        data = None
    if not isinstance(data, list):
        ctx.say("WARNING: remote branches skipped: `gh pr list` could not say which have an open PR. Run `gh auth status`, then retry")
        return None
    if len(data) >= ctx.gh_limit:
        ctx.say(
            f"WARNING: remote branches skipped: gh returned {len(data)} open PRs (--limit), so the list may be cut off. Close some, then retry"
        )
        return None
    heads: set[str] = set()
    bases: set[str] = set()
    for item in data:
        if isinstance(item, dict) and str(item.get("state", "OPEN")).upper() == "OPEN":
            heads.add(str(item.get("headRefName", "")))
            bases.add(str(item.get("baseRefName", "")))
    return heads, bases


def pr_merged_containing(ctx: Ctx, states: PrStates | None, branch: str | None, sha: str) -> bool:
    """A MERGED PR into main for the branch whose head commit contains `sha` (equal counts). A PR merged into
    another branch (a stacked PR) landed nothing on main."""
    if states is None or branch is None:
        return False
    return any(state == "MERGED" and base == lw.BASE and is_ancestor(ctx, sha, oid) for state, oid, base in states.get(branch, []))


def main_line(ctx: Ctx) -> set[str]:
    """origin/main's first-parent history. A lane created from main and never committed to sits on it; a feature
    branch that was merged in by a merge commit does not (its tip is a second parent)."""
    if ctx._main_line is None:
        proc = ctx.git(ctx.repo, "rev-list", "--first-parent", REMOTE_BASE, check=False)
        ctx._main_line = set(_text(proc).split()) if _rc(proc) == 0 else set()
    return ctx._main_line


def _commit_of(ctx: Ctx, ref: str) -> str | None:
    proc = ctx.git(ctx.repo, "rev-parse", "--verify", "-q", f"{ref}^{{commit}}", check=False)
    return _text(proc) if _rc(proc) == 0 and _text(proc) else None


def content_targets(ctx: Ctx) -> dict[str, str]:
    """ref -> sha for origin/main and every --contained-in tip that resolves (an unresolvable one warns once)."""
    if ctx._targets is None:
        targets: dict[str, str] = {}
        for ref in [REMOTE_BASE, *ctx.contained_in]:
            sha = _commit_of(ctx, ref)
            if sha is None:
                ctx.say(f"WARNING: --contained-in {ref} does not name a commit; ignored. Check the ref with `git rev-parse {ref}`")
            else:
                targets[ref] = sha
        ctx._targets = targets
    return ctx._targets


def keep_tip_shas(ctx: Ctx) -> set[str]:
    return {sha for ref, sha in content_targets(ctx).items() if ref != REMOTE_BASE}


def _on_origin_at(ctx: Ctx, ref: str, sha: str) -> bool:
    """Ask origin itself (not the local remote-tracking ref, which `fetch` without --prune leaves behind after a branch is deleted
    there) whether the branch behind `ref` is still at `sha`."""
    name = ref.removeprefix(f"{lw.REMOTE}/")
    proc = ctx.git(ctx.repo, "ls-remote", lw.REMOTE, f"refs/heads/{name}", check=False)
    return _rc(proc) == 0 and _text(proc).split("\t", 1)[0].strip() == sha


def remote_targets(ctx: Ctx) -> dict[str, str]:
    """The targets that may justify deleting a REMOTE branch: origin/main, and a --contained-in tip only when origin has it
    at the same sha (a remote-tracking ref, or a local branch whose `origin/<name>` is the same commit) AND `git ls-remote` says
    origin still has it there (a stale tracking ref of a tip deleted on origin must not justify anything). A tip that fails
    either test is skipped with a warning: it can justify a local delete, but deleting a remote branch because of content that
    may exist only on this laptop could destroy the only pushed copy."""
    if ctx._remote_targets is None:
        out: dict[str, str] = {}
        for ref, sha in content_targets(ctx).items():
            backed = (ref.startswith(f"{lw.REMOTE}/") or _commit_of(ctx, f"{lw.REMOTE}/{ref}") == sha) and _on_origin_at(ctx, ref, sha)
            if ref == REMOTE_BASE or backed:
                out[ref] = sha
            else:
                ctx.say(
                    f"WARNING: --contained-in {ref} is not on origin at the same commit, so it cannot justify a remote delete. Push it first"
                )
        ctx._remote_targets = out
    return ctx._remote_targets


def merge_neutralizers(ctx: Ctx) -> list[str] | None:
    """`git` options that stop attribute-driven merges (#812). A merge driver (`merge=ours`, or a configured custom driver such
    as this repo's resw driver) can make a conflicting edit merge 'cleanly' to the target's own side, which would read as
    'contained'. Attributes are read from the empty tree and no attributes file, every configured merge driver is replaced by
    one that fails, and merge.renormalize / core.autocrlf are forced off so line-ending rewriting cannot hide a difference; a
    file that needs a driver is therefore a conflict, i.e. not contained. Not covered: the builtin `merge=union` set in
    $GIT_DIR/info/attributes (it is read even with --attr-source).
    None (with ONE warning) when this git rejects --attr-source (older than 2.40): the content test is then off and only
    ancestry counts, which is said out loud rather than reported as 'not contained'."""
    if not ctx._neutralizers_ready:
        opts = [
            f"--attr-source={EMPTY_TREE}",
            "-c",
            f"core.attributesFile={os.devnull}",
            "-c",
            "merge.renormalize=false",
            "-c",
            "core.autocrlf=false",
        ]
        proc = ctx.git(ctx.repo, "config", "--get-regexp", r"^merge\..*\.driver$", check=False)
        if _rc(proc) == 0:
            for line in _text(proc).splitlines():
                key = line.split(None, 1)[0] if line.strip() else ""
                if key.startswith("merge.") and key.endswith(".driver") and len(key) > len("merge..driver"):
                    opts += ["-c", f"{key}=exit 1"]
        probe = ctx.git(ctx.repo, *opts, "rev-parse", "--git-dir", check=False)
        if _rc(probe) == 0:
            ctx._merge_neutralizers = opts
        else:
            ctx.say(
                "WARNING: this git rejects --attr-source (needs 2.40 or newer), so the content test is off and only ancestry counts. Update git"
            )
        ctx._neutralizers_ready = True
    return ctx._merge_neutralizers


def content_contained(ctx: Ctx, sha: str, *, remote: bool = False) -> bool:
    """True when merging `sha` into origin/main (or a --contained-in tip) changes nothing: `git merge-tree --write-tree K sha`
    yields K's own tree (#812). Ancestry misses a superseded attempt whose content landed under another name. A tip that IS a
    keep tip is never contained (it is the thing being kept); a conflict, an unrelated history or any git error is 'not contained'.
    remote=True judges only against targets origin also has (see remote_targets). The merge runs with attribute-driven merges
    disabled (see merge_neutralizers)."""
    if not sha:
        return False
    if (sha, remote) in ctx._contained:
        return ctx._contained[(sha, remote)]
    result = False
    if sha not in keep_tip_shas(ctx):
        for target in (remote_targets(ctx) if remote else content_targets(ctx)).values():
            if target == sha:
                continue
            if is_ancestor(ctx, sha, target):
                result = True
                break
            safe = merge_neutralizers(ctx)
            if safe is None:
                break
            merged = ctx.git(ctx.repo, *safe, "merge-tree", "--write-tree", target, sha, check=False)
            if _rc(merged) != 0:
                continue
            tree = ctx.git(ctx.repo, "rev-parse", f"{target}^{{tree}}", check=False)
            first = _text(merged).splitlines()[:1]
            if _rc(tree) == 0 and first and first[0].strip() == _text(tree):
                result = True
                break
    ctx._contained[(sha, remote)] = result
    return result


def landed_by_content(ctx: Ctx, sha: str) -> bool:
    """content_contained, except a tip on origin/main's first-parent line (a lane made from main, no commit of its own) is
    never judged by it: that case has its own freshness rule."""
    return sha not in main_line(ctx) and content_contained(ctx, sha)


def ignored_is_build_output(path: str) -> bool:
    return bool(BUILD_OUTPUT.match(path.replace("\\", "/").strip("/")))


def _is_link(path: Path) -> bool:
    return os.path.isjunction(path) or path.is_symlink()


def in_progress_reason(ctx: Ctx, wt: Path) -> str | None:
    proc = ctx.git(wt, "rev-parse", "--absolute-git-dir", check=False)
    if _rc(proc) != 0:
        return "git cannot read its admin directory"
    gitdir = Path(_text(proc))
    for name in IN_PROGRESS:
        if os.path.lexists(gitdir / name):
            return f"a rebase, merge, cherry-pick, revert or bisect is in progress ({name})"
    return None


def hidden_edit_reason(ctx: Ctx, wt: Path) -> str | None:
    """Files marked skip-worktree or assume-unchanged are invisible to `git status`: their edits would be lost."""
    proc = ctx.git(wt, "ls-files", "-v", "-z", check=False)
    if _rc(proc) != 0:
        return "git ls-files failed"
    for record in proc.stdout.split(b"\0"):  # type: ignore[attr-defined]
        if len(record) > 2:
            tag = record[:1].decode("ascii", errors="replace")
            if tag == "S" or tag.islower():
                return f"skip-worktree or assume-unchanged file hides edits ({record[2:].decode('utf-8', errors='replace')})"
    return None


def dirt_reason(ctx: Ctx, wt: Path) -> str | None:
    """Why this checkout holds something that is not safely disposable, or None. Runs `git status` read-only."""
    secrets = wt / "app" / "secrets"
    if secrets.is_dir() and any(secrets.iterdir()):
        return "app/secrets holds files that cannot be recovered"
    venv = wt / "worker" / ".venv"
    venv_is_link = _is_link(venv)
    if venv.is_dir() and not venv_is_link:
        return "worker/.venv is a real directory (not the shared-venv link); look before deleting"
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
        is_venv_link = venv_is_link and (path == VENV_PREFIX or path.startswith(VENV_PREFIX + "/"))
        if is_venv_link:
            continue
        if xy == "!!":
            if not ignored_is_build_output(path):
                ignored.append(path)
        else:
            changes += 1
    if changes:
        return f"uncommitted or untracked changes ({changes})"
    if ignored:
        return f"ignored files that may matter ({len(ignored)}), e.g. {ignored[0]}"
    return hidden_edit_reason(ctx, wt)


def _stamps(path: Path | str) -> list[float]:
    """mtimes of a directory, its entries and THEIR entries (never following links). Raises OSError: the caller
    treats an unreadable timestamp as unknown age, never as old."""
    out = [os.lstat(path).st_mtime]
    with os.scandir(path) as top:
        for entry in list(top):
            if entry.name == ".git":
                continue
            out.append(entry.stat(follow_symlinks=False).st_mtime)
            if entry.is_dir(follow_symlinks=False) and not os.path.isjunction(entry.path):
                with os.scandir(entry.path) as inner:
                    out.extend(child.stat(follow_symlinks=False).st_mtime for child in list(inner))
    return out


def newest_touch(ctx: Ctx, wt: Path) -> float | None:
    """Newest mtime over the checkout (two levels) and git's own state files for it; None when anything is unreadable."""
    try:
        stamps = _stamps(wt)
        proc = ctx.git(wt, "rev-parse", "--absolute-git-dir", check=False)
        if _rc(proc) != 0:
            return None
        gd = Path(_text(proc))
        for name in GIT_STATE_FILES:
            with contextlib.suppress(FileNotFoundError):
                stamps.append(os.lstat(gd / name).st_mtime)
    except OSError:
        return None
    return max(stamps)


def newest_dir_touch(d: Path) -> float | None:
    try:
        return max(_stamps(d))
    except OSError:
        return None


def recent_reason(ctx: Ctx, newest: float | None) -> str | None:
    """Why the age rules out removal (touched too recently, or unknown), else None."""
    if newest is None:
        return "age unknown (a timestamp could not be read); kept"
    age = (ctx.now() - newest) / 3600.0
    return f"touched {age:.1f} h ago (under {ctx.min_age_hours:g} h)" if age < ctx.min_age_hours else None


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
    """Recreate unlinked links, but never inside a tree git has already gutted (the link's parent is gone).
    Returns '' or a loud description of what could not be put back."""
    failed: list[str] = []
    gutted: list[str] = []
    for link, target in saved:
        plain = Path(_plain(link))
        if not plain.parent.exists():
            gutted.append(f"{plain} -> {_plain(target)}")
            continue
        try:
            lw.make_dir_link(plain, Path(_plain(target)))
        except (lw.LandError, OSError):
            failed.append(f"{plain} -> {_plain(target)}")
    note = ""
    if gutted:
        note += f" PARTIAL: git removed part of the tree before failing; nothing was recreated inside it. Recreate by hand: {'; '.join(gutted)}"
    if failed:
        note += f" ERROR: could not recreate link(s) {'; '.join(failed)}; recreate them by hand"
    return note


def scan_problem(path: Path) -> str | None:
    """The first reason the tree cannot be trusted to be scanned, or None. Read-only, so a dry run can predict apply."""
    _links, problems = scan_links(path)
    return problems[0] + (f" (+{len(problems) - 1} more)" if len(problems) > 1 else "") if problems else None


def remove_worktree(ctx: Ctx, wt: Worktree) -> str | None:
    """Links first, verified; then git. None on success, else why the worktree was left (links restored, an
    interrupt included)."""
    links, problems = scan_links(wt.path)
    if problems:
        return scan_problem(wt.path)
    saved: list[tuple[Path, str]] = []
    try:
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
    except BaseException:  # KeyboardInterrupt included: never leave the shared venv unlinked from a surviving tree
        _restore(saved)
        raise
    return None


# ---------------------------------------------------------------------------
# Decisions
# ---------------------------------------------------------------------------


def decide(ctx: Ctx, wt: Worktree, primary: Worktree, all_paths: list[Path], states: PrStates | None) -> str | None:
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
    if _is_link(wt.path):
        return "the worktree path is itself a link (junction or symlink); left alone"
    if any(_under(_norm(p), me) for p in [*all_paths, ctx.repo]):
        return "contains another worktree or this checkout (it would be deleted with it)"
    if wt.branch is not None and states is not None and any(s == "OPEN" for s, _, _ in states.get(wt.branch, [])):
        return "open PR"
    if not (is_ancestor(ctx, wt.head, REMOTE_BASE) or pr_merged_containing(ctx, states, wt.branch, wt.head) or landed_by_content(ctx, wt.head)):
        return "not merged (detached, commit not in origin/main)" if wt.branch is None else "not merged into origin/main"
    newest = newest_touch(ctx, wt.path)
    if wt.head in main_line(ctx):
        # a lane created from main that has not committed: fresh only for a while, then it is just a stale checkout
        if newest is None:
            return recent_reason(ctx, None)
        if (ctx.now() - newest) / 3600.0 < ctx.fresh_lane_hours:
            return f"no commit of its own and touched within {ctx.fresh_lane_hours:g} h (a lane just started from main)"
    return in_progress_reason(ctx, wt.path) or recent_reason(ctx, newest) or dirt_reason(ctx, wt.path) or scan_problem(wt.path)


def _lock_record(ctx: Ctx, name: str) -> dict[str, object]:
    try:
        info = json.loads((ctx.tmp_root / lw.LOCK_DIR / f"{name}.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    return info if isinstance(info, dict) else {}


def _lock_live(ctx: Ctx, name: str) -> bool:
    info = _lock_record(ctx, name)
    try:
        return ctx.pid_alive(int(info["pid"])) and ctx.now() - float(info["start"]) < lw.LOCK_MAX_AGE_SECONDS  # type: ignore[arg-type]
    except (KeyError, ValueError, TypeError):
        return False


def _gitdir_readable(d: Path) -> bool:
    """Does the checkout's `.git` file point at an admin directory that still exists? (Never ask git to discover it:
    a stray parent repository would answer for the wrong checkout.)"""
    try:
        text = (d / ".git").read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return False
    match = re.match(r"gitdir:\s*(.+)", text)
    return bool(match) and (d / match.group(1).strip()).is_dir()


def unreadable_orphan_reason(ctx: Ctx, d: Path) -> str | None:
    """The real land_wt leftover: `git worktree prune` already deleted its admin dir. Safe only when land_wt's own
    lock record names the commit it checked out and that commit is pushed (reachable from a remote-tracking ref)."""
    sha = str(_lock_record(ctx, d.name).get("sha", ""))
    if not re.fullmatch(r"[0-9a-f]{40,64}", sha):
        return "no record of the commit land_wt checked out (an older run); look, then delete it by hand"
    pushed = ctx.git(ctx.repo, "for-each-ref", "--contains", sha, "--count=1", "--format=%(refname)", "refs/remotes", check=False)
    if _rc(pushed) != 0 or not _text(pushed):
        return f"its commit {sha[:12]} is not pushed to origin, so it may hold the only copy; look, then delete it by hand"
    return None


def orphan_keep_reason(ctx: Ctx, d: Path) -> str | None:
    if os.path.isjunction(d) or d.is_symlink():
        return "is itself a link; left alone"
    if not LANDWT_NAME.match(d.name):
        return "name is not <slug>-<14 digits>; not a landing directory"
    if not (d / ".git").is_file():
        return "no .git file; not a worktree checkout"
    if _lock_live(ctx, d.name):
        return "a live run holds it"
    reason = recent_reason(ctx, newest_dir_touch(d))
    if reason is not None:
        return reason
    if not _gitdir_readable(d):
        return unreadable_orphan_reason(ctx, d)
    head = ctx.git(d, "rev-parse", "HEAD", check=False)
    if _rc(head) != 0:
        return "git cannot read its HEAD; look, then delete it by hand"
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
        links, problems = scan_links(d) if reason is None else ([], [])
        if reason is None and problems:
            reason = problems[0]
        if reason is not None:
            report.orphans_kept.append(Decision(d, reason))
            continue
        if ctx.apply:
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


def deletable_branches(ctx: Ctx, still_checked_out: set[str], states: PrStates | None) -> list[tuple[str, str]]:
    merged_proc = ctx.git(ctx.repo, "for-each-ref", "--merged", REMOTE_BASE, "--format=%(refname:short)", "refs/heads", check=False)
    merged = set(_text(merged_proc).splitlines()) if _rc(merged_proc) == 0 else set()
    refs = ctx.git(ctx.repo, "for-each-ref", "--format=%(refname:short)%09%(objectname)", "refs/heads", check=False)
    line_of_main = main_line(ctx)
    out: list[tuple[str, str]] = []
    for line in _text(refs).splitlines():
        name, _, sha = line.partition("\t")
        if not name or name in still_checked_out or any(fnmatch.fnmatchcase(name, pat) for pat in PROTECTED_BRANCHES):
            continue
        # a tip ON main's first-parent history is a lane created from an older main, not a merged feature branch
        merged_feature = name in merged and sha not in line_of_main
        if merged_feature or pr_merged_containing(ctx, states, name, sha) or landed_by_content(ctx, sha):
            out.append((name, sha))
    return out


def deletable_remote_branches(ctx: Ctx, open_heads: set[str], open_bases: set[str], still_checked_out: set[str]) -> list[tuple[str, str]]:
    """(name, sha) of origin branches whose content is already in origin/main or a kept tip that origin also has, with no open
    PR. Never main/develop/release/*, never a keep tip itself, never a branch sitting on main's first-parent line (a lane just
    started), never the BASE of an open PR (deleting a base closes the PR stacked on it), never a branch checked out in a
    kept worktree."""
    refs = ctx.git(ctx.repo, "for-each-ref", "--format=%(refname:short)%09%(objectname)", f"refs/remotes/{lw.REMOTE}", check=False)
    prefix = f"{lw.REMOTE}/"
    line_of_main = main_line(ctx)
    out: list[tuple[str, str]] = []
    for line in _text(refs).splitlines():
        ref, _, sha = line.partition("\t")
        name = ref.removeprefix(prefix)
        if not ref.startswith(prefix) or name in ("HEAD", "") or any(fnmatch.fnmatchcase(name, pat) for pat in PROTECTED_BRANCHES):
            continue
        if sha in keep_tip_shas(ctx) or name in open_heads or name in open_bases or name in still_checked_out:
            continue
        if sha in line_of_main:
            continue
        if is_ancestor(ctx, sha, REMOTE_BASE) or content_contained(ctx, sha, remote=True):
            out.append((name, sha))
    return out


# ---------------------------------------------------------------------------
# Orchestration and output
# ---------------------------------------------------------------------------


def prune(ctx: Ctx) -> Report:
    report = Report()
    if ctx.fetch and _rc(ctx.git(ctx.repo, "fetch", "-q", "--prune", lw.REMOTE, check=False)) != 0:
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
        report.removed.append(Decision(wt.path, "landed and clean", wt.branch, wt.head))
    sweep_orphans(ctx, {_norm(w.path) for w in worktrees}, report)
    if ctx.apply:
        ctx.git(ctx.repo, "worktree", "prune", check=False)
    keep_out = {d.branch for d in report.kept if d.branch}
    if ctx.delete_merged_branches:
        for name, sha in deletable_branches(ctx, keep_out, states):
            if ctx.apply and _rc(ctx.git(ctx.repo, "update-ref", "-d", f"refs/heads/{name}", sha, check=False)) != 0:
                # `update-ref -d <ref> <old>` is a lease on the sha we judged: a branch that moved since survives
                ctx.say(f"WARNING: could not delete local branch {name}@{sha} (it moved, or git refused); leaving it")
                continue
            report.branches_deleted.append(Decision(ctx.repo, "landed, not checked out", name, sha))
        if ctx.apply:
            ctx.git(ctx.repo, "worktree", "prune", check=False)
    if ctx.remote:
        opened = open_prs(ctx)
        if opened is not None:
            for name, sha in deletable_remote_branches(ctx, opened[0], opened[1], keep_out):
                if ctx.apply:
                    ref = f"refs/heads/{name}"
                    # a lease on the sha we judged: if someone pushed to the branch meanwhile, git refuses
                    push = ctx.git(ctx.repo, "push", lw.REMOTE, f"--force-with-lease={ref}:{sha}", "--delete", ref, check=False)
                    if _rc(push) != 0:
                        ctx.say(f"WARNING: could not delete {lw.REMOTE}/{name}@{sha} (it moved, or the push was refused); leaving it")
                        continue
                report.remote_deleted.append(Decision(ctx.repo, "content already in main or a kept tip, no open PR", name, sha))
    return report


def print_report(ctx: Ctx, report: Report) -> None:
    say = ctx.say
    say(f"prune_worktrees: {'applied' if ctx.apply else 'DRY RUN (nothing changed; pass --apply to act)'}")
    if not ctx.fetch:
        say("note: origin not fetched; judged against the last fetched origin/main (--apply fetches first; pass --fetch for parity)")
    verb = "removed" if ctx.apply else "would remove"
    for title, rows, is_branch in (
        ("worktrees", report.removed, False),
        ("orphan temp dirs", report.orphans_removed, False),
        ("local branches", report.branches_deleted, True),
        ("remote branches", report.remote_deleted, True),
    ):
        say(f"{verb} ({title}): {len(rows)}")
        for r in rows:
            what = r.branch if is_branch else r.path
            say(f"  {verb}  {what}@{r.sha}" if r.sha else f"  {verb}  {what}")
            if ctx.apply and not is_branch and r.branch is None and r.sha:
                say(f"    detached HEAD, undo with: git worktree add --detach {r.path} {r.sha}")
    kept = report.kept + report.orphans_kept
    say(f"kept: {len(kept)}")
    for r in kept:
        say(f"  kept     {r.path}  -- {r.reason}")
    say(
        f"{'OK' if ctx.apply else '-'}: {len(report.removed)} worktree(s), {len(report.orphans_removed)} orphan dir(s), "
        f"{len(report.branches_deleted)} branch(es), {len(report.remote_deleted)} remote branch(es) {verb}; {len(kept)} kept"
    )


def parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    ap = argparse.ArgumentParser(description="Remove worktrees whose branch already landed (dry run unless --apply).")
    ap.add_argument("--apply", action="store_true", help="act (default is a dry run)")
    ap.add_argument("--min-age-hours", type=float, default=6.0, help="keep anything touched more recently than this (default 6)")
    ap.add_argument(
        "--fresh-lane-hours",
        type=float,
        default=48.0,
        help="keep a worktree with no commit of its own (HEAD on origin/main's history) only while touched within this many hours (default 48)",
    )
    ap.add_argument("--use-gh", action="store_true", help="also treat a branch with a MERGED PR (head contains the worktree HEAD) as landed")
    ap.add_argument("--delete-merged-branches", action="store_true", help="also delete local branches landed in origin/main and not checked out")
    ap.add_argument(
        "--contained-in",
        nargs="+",
        default=[],
        metavar="REF",
        help="also treat a branch whose content is already inside any of these refs (queued or held tips) as landed; origin/main always counts",
    )
    ap.add_argument(
        "--remote",
        action="store_true",
        help="also delete origin branches already contained in origin/main or a --contained-in tip with no open PR (needs gh)",
    )
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
        fresh_lane_hours=args.fresh_lane_hours,
        apply=args.apply,
        use_gh=args.use_gh,
        gh=[args.gh],
        delete_merged_branches=args.delete_merged_branches,
        contained_in=list(args.contained_in),
        remote=args.remote,
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
