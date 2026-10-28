"""Tests for scripts/prune_worktrees.py (#794).

Every repository is a throwaway bare "origin" plus a clone under tmp_path; linked worktrees live beside it.
`gh` is a stub script that prints a scripted JSON list. The real repository, its venv and GitHub are never touched.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import prune_worktrees as pw  # noqa: E402

GH_STUB = """\
import sys
from pathlib import Path
log = Path(sys.argv[1])
log.open("a", encoding="utf-8", newline="\\n").write(" ".join(sys.argv[2:]) + "\\n")
print(Path(str(log) + ".reply").read_text(encoding="utf-8"))
"""

needs_windows = pytest.mark.skipif(os.name != "nt", reason="NTFS junctions are Windows only")


def _git(cwd: Path, *args: str) -> str:
    proc = subprocess.run(["git", *args], cwd=cwd, capture_output=True, check=False)
    assert proc.returncode == 0, proc.stderr.decode(errors="replace")
    return proc.stdout.decode("utf-8", errors="replace").strip()


def _junction(link: Path, target: Path) -> None:
    link.parent.mkdir(parents=True, exist_ok=True)
    if os.name == "nt":
        import _winapi

        _winapi.CreateJunction(str(target), str(link))
    else:
        os.symlink(target, link, target_is_directory=True)


@dataclass
class World:
    root: Path
    origin: Path
    clone: Path
    tmp_root: Path
    sentinel: Path
    gh_log: Path
    git_calls: list[tuple[str, ...]] = field(default_factory=list)
    out: list[str] = field(default_factory=list)

    def ctx(self, **kw) -> pw.Ctx:
        def spy(cwd: Path, *args: str, **kwargs):
            self.git_calls.append(args)
            return pw.git(cwd, *args, **kwargs)

        defaults = dict(
            repo=self.clone,
            tmp_root=self.tmp_root,
            min_age_hours=0.0,
            apply=False,
            fetch=False,
            say=self.out.append,
        )
        defaults.update(kw)
        ctx = pw.Ctx(**defaults)
        ctx.git = kw.get("git", spy)
        return ctx

    def add_worktree(self, name: str, branch: str | None = None, *, commit: bool = False, detach: bool = False, fresh: bool = False) -> Path:
        """By default a LANDED lane: one commit of its own, merged into origin/main by a merge commit.
        commit=True: the same commit but never merged. fresh=True: exactly at origin/main, no commit (a new lane)."""
        wt = self.root / "wts" / name
        if detach:
            _git(self.clone, "worktree", "add", "-q", "--detach", str(wt), "origin/main")
        else:
            _git(self.clone, "worktree", "add", "-q", "-b", branch or name, str(wt), "origin/main")
        if not fresh:
            (wt / f"{name}.txt").write_bytes(b"work\n")
            _git(wt, "add", "--", f"{name}.txt")
            _git(wt, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", f"work {name}")
            if not commit:
                self.merge_into_main(_git(wt, "rev-parse", "HEAD"))
        return wt

    def merge_into_main(self, sha: str) -> None:
        """What a merged PR does: a merge commit on origin/main with `sha` as its second parent."""
        main = _git(self.clone, "rev-parse", "origin/main")
        merge = _git(self.clone, "commit-tree", f"{sha}^{{tree}}", "-p", main, "-p", sha, "-m", "merge")
        _git(self.clone, "push", "-q", "origin", f"{merge}:refs/heads/main")
        _git(self.clone, "fetch", "-q", "origin")

    def merged_branch(self, name: str) -> str:
        """A local branch with its own commit, merged into origin/main, checked out nowhere. Returns the tip."""
        main = _git(self.clone, "rev-parse", "origin/main")
        tip = _git(self.clone, "commit-tree", f"{main}^{{tree}}", "-p", main, "-m", f"feature {name}")
        _git(self.clone, "branch", name, tip)
        self.merge_into_main(tip)
        return tip

    def exclude(self, wt: Path, *patterns: str) -> None:
        """Ignore patterns for every worktree of this repo (info/exclude lives in the common dir)."""
        common = Path(_git(wt, "rev-parse", "--git-common-dir"))
        common = common if common.is_absolute() else wt / common
        (common / "info").mkdir(parents=True, exist_ok=True)
        with (common / "info" / "exclude").open("a", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(patterns) + "\n")

    def advance_main(self) -> None:
        _git(self.clone, "switch", "-q", "--detach", "origin/main")
        (self.clone / "adv.txt").write_bytes(b"adv\n")
        _git(self.clone, "add", "--", "adv.txt")
        _git(self.clone, "commit", "-q", "-m", "main moves")
        _git(self.clone, "push", "-q", "origin", "HEAD:refs/heads/main")
        _git(self.clone, "fetch", "-q", "origin")
        _git(self.clone, "switch", "-q", "-")

    def registered(self) -> list[str]:
        return [ln[len("worktree ") :] for ln in _git(self.clone, "worktree", "list", "--porcelain").splitlines() if ln.startswith("worktree ")]

    def is_registered(self, wt: Path) -> bool:
        return any(os.path.normcase(os.path.normpath(p)) == os.path.normcase(os.path.normpath(str(wt))) for p in self.registered())

    def branches(self) -> list[str]:
        return _git(self.clone, "for-each-ref", "--format=%(refname:short)", "refs/heads").split()

    def gh_stub(self, prs: list[dict]) -> list[str]:
        (self.root / "gh_stub.py").write_text(GH_STUB, encoding="utf-8", newline="\n")
        Path(str(self.gh_log) + ".reply").write_text(json.dumps(prs), encoding="utf-8", newline="\n")
        return [sys.executable, str(self.root / "gh_stub.py"), str(self.gh_log)]

    def gh_calls(self) -> list[str]:
        return self.gh_log.read_text(encoding="utf-8").splitlines() if self.gh_log.exists() else []

    def text(self) -> str:
        return "\n".join(self.out)


@pytest.fixture()
def world(tmp_path_factory) -> World:
    root = tmp_path_factory.mktemp("pw")
    origin = root / "o.git"
    clone = root / "c"
    _git(root, "init", "-q", "--bare", "-b", "main", str(origin))
    _git(root, "clone", "-q", str(origin), str(clone))
    _git(clone, "config", "user.email", "t@example.invalid")
    _git(clone, "config", "user.name", "t")
    _git(clone, "config", "core.autocrlf", "false")
    (clone / "worker").mkdir()
    (clone / "worker" / "README.md").write_bytes(b"worker\n")
    (clone / ".gitignore").write_bytes(b".venv/\n")
    (clone / "a.txt").write_bytes(b"one\n")
    _git(clone, "add", "--", "worker/README.md", "a.txt", ".gitignore")
    _git(clone, "commit", "-q", "-m", "init")
    _git(clone, "push", "-q", "origin", "HEAD:refs/heads/main")
    _git(clone, "fetch", "-q", "origin")
    sentinel = root / "real_venv"
    sentinel.mkdir()
    (sentinel / "marker.txt").write_bytes(b"the real venv\n")
    (root / "wts").mkdir()
    return World(root, origin, clone, root / "t" / "landwt", sentinel, root / "gh.log")


def _run(world: World, *, apply: bool = False, **kw) -> pw.Report:
    ctx = world.ctx(apply=apply, **kw)
    report = pw.prune(ctx)
    pw.print_report(ctx, report)
    return report


# --- dry run --------------------------------------------------------------------------------------


def test_dry_run_changes_nothing_but_reports_the_removal(world):
    wt = world.add_worktree("lane-a")
    report = _run(world, apply=False, delete_merged_branches=True)
    assert [r.path.name for r in report.removed] == ["lane-a"]
    assert wt.is_dir() and world.is_registered(wt)
    assert "lane-a" in world.branches()
    assert not any(call[:2] in (("worktree", "remove"), ("worktree", "prune"), ("branch", "-D")) for call in world.git_calls)
    assert "dry run" in world.text().lower()


def test_apply_removes_a_worktree_whose_head_is_an_ancestor_of_origin_main(world):
    wt = world.add_worktree("lane-a")
    report = _run(world, apply=True)
    assert [r.path.name for r in report.removed] == ["lane-a"]
    assert not wt.exists() and not world.is_registered(wt)
    assert "lane-a" in world.branches()  # the branch itself is kept without --delete-merged-branches


def test_apply_removes_a_worktree_whose_commits_were_merged_into_main(world):
    wt = world.add_worktree("lane-m", commit=True)
    assert _run(world).removed == []  # not yet merged
    world.merge_into_main(_git(wt, "rev-parse", "HEAD"))
    report = _run(world, apply=True)
    assert [r.path.name for r in report.removed] == ["lane-m"]


def test_unmerged_branch_is_kept_with_a_reason(world):
    wt = world.add_worktree("lane-u", commit=True)
    report = _run(world, apply=True)
    assert wt.is_dir()
    assert [(k.path.name, k.reason) for k in report.kept if k.path.name == "lane-u"][0][1].startswith("not merged")


# --- the never-list ---------------------------------------------------------------------------------


def test_primary_checkout_is_never_touched_even_when_it_is_merged_and_clean(world):
    report = _run(world, apply=True)
    assert world.clone.is_dir() and world.is_registered(world.clone)
    assert report.removed == []
    assert any(k.path == world.clone and "primary" in k.reason for k in report.kept)


def test_the_checkout_the_script_runs_from_is_kept(world):
    wt = world.add_worktree("lane-self")
    report = pw.prune(world.ctx(apply=True, repo=wt))
    assert wt.is_dir()
    assert any(k.path.name == "lane-self" and "this checkout" in k.reason for k in report.kept)


@pytest.mark.parametrize("dirt", ["tracked", "untracked", "staged"])
def test_dirty_worktree_is_kept(world, dirt):
    wt = world.add_worktree("lane-d")
    if dirt == "tracked":
        (wt / "a.txt").write_bytes(b"changed\n")
    elif dirt == "untracked":
        (wt / "new.txt").write_bytes(b"x\n")
    else:
        (wt / "s.txt").write_bytes(b"x\n")
        _git(wt, "add", "--", "s.txt")
    report = _run(world, apply=True)
    assert wt.is_dir() and report.removed == []
    assert any(k.path.name == "lane-d" and "uncommitted" in k.reason for k in report.kept)


def test_a_recently_touched_worktree_is_kept_and_an_old_one_is_not(world):
    wt = world.add_worktree("lane-r")
    report = pw.prune(world.ctx(apply=True, min_age_hours=6.0))
    assert wt.is_dir()
    assert any(k.path.name == "lane-r" and "touched" in k.reason for k in report.kept)
    later = time.time() + 7 * 3600
    report = pw.prune(world.ctx(apply=True, min_age_hours=6.0, now=lambda: later))
    assert [r.path.name for r in report.removed] == ["lane-r"]


def test_locked_worktree_is_kept(world):
    wt = world.add_worktree("lane-l")
    _git(world.clone, "worktree", "lock", str(wt))
    report = _run(world, apply=True)
    assert wt.is_dir()
    assert any(k.path.name == "lane-l" and "locked" in k.reason for k in report.kept)


def test_detached_head_at_an_ancestor_is_removed_and_a_detached_unmerged_one_is_kept(world):
    gone = world.add_worktree("det-old", detach=True)
    live = world.add_worktree("det-new", detach=True, commit=True)
    report = _run(world, apply=True)
    assert not gone.exists() and live.is_dir()
    assert any(k.path.name == "det-new" and "not merged" in k.reason for k in report.kept)


# --- gh ---------------------------------------------------------------------------------------------


def _pr(wt: Path, state: str, name: str | None = None, oid: str | None = None, base: str = "main") -> dict:
    return {"headRefName": name or wt.name, "state": state, "headRefOid": oid or _git(wt, "rev-parse", "HEAD"), "baseRefName": base}


def test_a_merged_pr_into_another_branch_is_not_landed(world):
    wt = world.add_worktree("stacked", commit=True)
    report = pw.prune(world.ctx(apply=True, use_gh=True, gh=world.gh_stub([_pr(wt, "MERGED", base="feat/x")])))
    assert wt.is_dir() and report.removed == []
    assert any(k.path.name == "stacked" and "not merged" in k.reason for k in report.kept)


def test_use_gh_removes_merged_pr_branches_keeps_closed_and_open_ones_in_one_call(world):
    merged = world.add_worktree("pr-merged", commit=True)
    closed = world.add_worktree("pr-closed", commit=True)
    opened = world.add_worktree("pr-open", commit=True)
    plain = world.add_worktree("no-pr", commit=True)
    prs = [_pr(merged, "MERGED"), _pr(closed, "CLOSED"), _pr(opened, "OPEN")]
    report = pw.prune(world.ctx(apply=True, use_gh=True, gh=world.gh_stub(prs)))
    assert not merged.exists()
    assert closed.is_dir() and opened.is_dir() and plain.is_dir()  # a CLOSED PR proves nothing landed
    assert any(k.path.name == "pr-open" and "open PR" in k.reason for k in report.kept)
    assert len(world.gh_calls()) == 1  # batched, not one call per worktree
    assert "--state all" in world.gh_calls()[0]


def test_a_merged_pr_whose_head_does_not_contain_the_worktree_head_is_not_enough(world):
    wt = world.add_worktree("pr-stale", commit=True)
    other = world.add_worktree("pr-other", commit=True)
    stale_oid = _git(other, "rev-parse", "HEAD")  # a different commit: the lane moved on after the PR merged
    report = pw.prune(world.ctx(apply=True, use_gh=True, gh=world.gh_stub([_pr(wt, "MERGED", oid=stale_oid)])))
    assert wt.is_dir() and report.removed == []
    assert any(k.path.name == "pr-stale" and "not merged" in k.reason for k in report.kept)


def test_hitting_the_gh_list_limit_warns(world):
    a = world.add_worktree("pr-a", commit=True)
    b = world.add_worktree("pr-b", commit=True)
    pw.prune(world.ctx(apply=False, use_gh=True, gh_limit=2, gh=world.gh_stub([_pr(a, "OPEN"), _pr(b, "OPEN")])))
    assert "WARNING" in world.text() and "limit" in world.text()


def test_without_use_gh_gh_is_never_run(world):
    world.add_worktree("pr-merged", commit=True)
    pw.prune(world.ctx(apply=True, gh=world.gh_stub([{"headRefName": "pr-merged", "state": "MERGED"}])))
    assert world.gh_calls() == []


def test_a_gh_failure_degrades_to_ancestor_only_with_a_warning(world):
    wt = world.add_worktree("pr-merged", commit=True)
    pr = [_pr(wt, "MERGED")]
    report = pw.prune(world.ctx(apply=False, use_gh=True, gh=[sys.executable, "-c", "import sys; sys.exit(1)"]))
    assert report.removed == [] and "WARNING" in world.text()
    # control: the very same worktree IS removable when gh answers MERGED, so the keep above is the gh failure's doing
    report = pw.prune(world.ctx(apply=False, use_gh=True, gh=world.gh_stub(pr)))
    assert [r.path.name for r in report.removed] == ["pr-merged"]


# --- junctions ----------------------------------------------------------------------------------------


@needs_windows
def test_venv_junction_does_not_make_a_worktree_dirty_and_is_unlinked_before_git_removes_it(world):
    wt = world.add_worktree("lane-j")
    _junction(wt / "worker" / ".venv", world.sentinel)
    seen: dict[str, bool] = {}

    def spy(cwd: Path, *args: str, **kwargs):
        if args[:2] == ("worktree", "remove"):
            seen["link_present_at_remove"] = os.path.lexists(wt / "worker" / ".venv")
        return pw.git(cwd, *args, **kwargs)

    report = pw.prune(world.ctx(apply=True, git=spy))
    assert [r.path.name for r in report.removed] == ["lane-j"]
    assert seen == {"link_present_at_remove": False}
    assert not wt.exists()
    assert (world.sentinel / "marker.txt").read_bytes() == b"the real venv\n"


@needs_windows
def test_another_junction_deep_in_the_worktree_is_unlinked_first_too(world):
    wt = world.add_worktree("lane-j2")
    link = wt / "app" / "src" / "App" / "obj" / "er" / "link"
    _junction(link, world.sentinel)
    world.exclude(wt, "obj/")  # ignored build output: allowed, so the junction inside it is what must be handled
    seen: dict[str, bool] = {}

    def spy(cwd: Path, *args: str, **kwargs):
        if args[:2] == ("worktree", "remove"):
            seen["link_present_at_remove"] = os.path.lexists(link)
        return pw.git(cwd, *args, **kwargs)

    report = pw.prune(world.ctx(apply=True, git=spy))
    assert [r.path.name for r in report.removed] == ["lane-j2"]
    assert seen == {"link_present_at_remove": False}
    assert (world.sentinel / "marker.txt").is_file()


@needs_windows
def test_if_a_junction_cannot_be_unlinked_the_worktree_is_left_in_place(world, monkeypatch):
    wt = world.add_worktree("lane-j3")
    _junction(wt / "worker" / ".venv", world.sentinel)
    monkeypatch.setattr(pw.lw, "unlink_dir_link", lambda link: None)  # unlink silently does nothing
    report = pw.prune(world.ctx(apply=True))
    assert report.removed == [] and wt.is_dir()
    assert (world.sentinel / "marker.txt").is_file()
    assert any(k.path.name == "lane-j3" and "junction" in k.reason for k in report.kept)


# --- orphan landwt dirs --------------------------------------------------------------------------------


def _leftover(world: World, name: str, *, record_sha: str | bool = True, with_junction: bool = False) -> Path:
    """What a killed land_wt run really leaves: a detached worktree under the temp root whose admin dir
    `git worktree prune` has already deleted (so git cannot read it), plus the lock file land_wt wrote.
    record_sha True: the lock records a commit that is pushed (on origin); False: no sha recorded; a str: that sha."""
    tip = world.merged_branch(f"b-{name}") if record_sha is True else None
    sha = tip if record_sha is True else (record_sha if isinstance(record_sha, str) else None)
    wt = world.tmp_root / name
    world.tmp_root.mkdir(parents=True, exist_ok=True)
    _git(world.clone, "worktree", "add", "-q", "--detach", str(wt), tip or "origin/main")
    admin = Path((wt / ".git").read_text(encoding="utf-8").split("gitdir:", 1)[1].strip())
    shutil.rmtree(admin)
    _git(world.clone, "worktree", "prune")
    assert not world.is_registered(wt)
    assert (
        subprocess.run(["git", "rev-parse", "HEAD"], cwd=wt, capture_output=True, check=False).returncode != 0
    )  # unreadable, like the real thing
    (world.tmp_root / "locks").mkdir(exist_ok=True)
    record = {"pid": 0, "start": 0}
    if sha:
        record["sha"] = sha
    (world.tmp_root / "locks" / f"{name}.json").write_text(json.dumps(record), encoding="utf-8", newline="\n")
    if with_junction:
        _junction(wt / "worker" / ".venv", world.sentinel)
    return wt


def _orphan(world: World, name: str, with_junction: bool = True, seed_commit: bool = False) -> Path:
    """A directory git does not list but whose `.git` file still reads: a copy of a LOCKED registered worktree."""
    seed = world.add_worktree(f"seed-{name[:12]}", commit=seed_commit)
    _git(world.clone, "worktree", "lock", str(seed))
    d = world.tmp_root / name
    shutil.copytree(seed, d)
    if with_junction:
        _junction(d / "worker" / ".venv", world.sentinel)
    return d


def test_a_real_unreadable_landwt_leftover_is_removed_without_following_the_junction(world):
    orphan = _leftover(world, "feat-1-x-20261001120000", with_junction=os.name == "nt")
    logs = world.tmp_root / "logs"
    logs.mkdir()
    (logs / "keep.log").write_bytes(b"log\n")
    report = _run(world, apply=True)
    assert not orphan.exists()
    assert (world.sentinel / "marker.txt").is_file()
    assert (world.tmp_root / "locks").is_dir() and logs.is_dir() and (logs / "keep.log").is_file()
    assert [o.path.name for o in report.orphans_removed] == ["feat-1-x-20261001120000"]
    assert not (world.tmp_root / "locks" / "feat-1-x-20261001120000.json").exists()


def test_a_leftover_is_kept_when_land_wt_recorded_no_sha_or_the_sha_is_not_pushed(world):
    no_sha = _leftover(world, "feat-9-x-20261001120000", record_sha=False)
    dangling = _git(world.clone, "commit-tree", "origin/main^{tree}", "-p", "origin/main", "-m", "never pushed")
    unpushed = _leftover(world, "feat-a-x-20261001120000", record_sha=dangling)
    missing = _leftover(world, "feat-b-x-20261001120000", record_sha="1" * 40)
    report = _run(world, apply=True)
    for d in (no_sha, unpushed, missing):
        assert d.is_dir(), d
    assert report.orphans_removed == []
    reasons = {o.path.name: o.reason for o in report.orphans_kept}
    assert "no record" in reasons[no_sha.name] and "not pushed" in reasons[unpushed.name] and "not pushed" in reasons[missing.name]


def test_orphan_dry_run_leaves_it(world):
    orphan = _leftover(world, "feat-2-x-20261001120000")
    report = _run(world, apply=False)
    assert orphan.is_dir() and [o.path.name for o in report.orphans_removed] == [orphan.name]


def test_orphan_with_a_live_lock_or_recent_touch_is_kept(world):
    live = _leftover(world, "feat-3-x-20261001120000")
    record = json.loads((world.tmp_root / "locks" / f"{live.name}.json").read_text(encoding="utf-8"))
    (world.tmp_root / "locks" / f"{live.name}.json").write_text(
        json.dumps({**record, "pid": os.getpid(), "start": time.time()}), encoding="utf-8", newline="\n"
    )
    fresh = _leftover(world, "feat-4-x-20261001120000")
    report = pw.prune(world.ctx(apply=True, min_age_hours=6.0, pid_alive=lambda pid: True))
    assert live.is_dir() and fresh.is_dir() and report.orphans_removed == []


def test_orphan_that_is_dirty_unmerged_misnamed_or_has_no_git_file_is_kept(world):
    dirty = _orphan(world, "feat-6-x-20261001120000", with_junction=False)
    (dirty / "mine.txt").write_bytes(b"unsaved\n")
    unmerged = _orphan(world, "feat-7-x-20261001120000", with_junction=False, seed_commit=True)
    misnamed = _orphan(world, "not-a-landing-dir", with_junction=False)
    no_git = world.tmp_root / "feat-8-x-20261001120000"
    (no_git / "f").mkdir(parents=True)
    report = _run(world, apply=True)
    for d in (dirty, unmerged, misnamed, no_git):
        assert d.is_dir(), d
    assert report.orphans_removed == []
    reasons = {o.path.name: o.reason for o in report.orphans_kept}
    assert "uncommitted" in reasons[dirty.name] and "not merged" in reasons[unmerged.name]
    assert no_git.name in reasons and misnamed.name in reasons


def test_a_tmp_root_not_named_landwt_is_refused_unless_overridden(world, tmp_path):
    victim = tmp_path / "Documents"
    keep = victim / "old-project-20200101000000"
    keep.mkdir(parents=True)
    assert pw.main(["--no-fetch", "--tmp-root", str(victim), "--apply", "--min-age-hours", "0"], repo=world.clone) == pw.EXIT_USAGE
    assert keep.is_dir()
    report = pw.prune(world.ctx(apply=True, tmp_root=victim))  # the library guard holds too
    assert keep.is_dir() and report.orphans_removed == []


def test_a_landwt_dir_that_git_still_lists_is_not_an_orphan(world):
    wt = world.tmp_root / "feat-5-x-20261001120000"
    wt.parent.mkdir(parents=True)
    _git(world.clone, "worktree", "add", "-q", "--detach", str(wt), world.merged_branch("b5"))
    report = _run(world, apply=False)
    assert report.orphans_removed == []
    assert [r.path.name for r in report.removed] == [wt.name]  # handled as a worktree (merged, clean)


# --- prune, branches, output ---------------------------------------------------------------------------


def test_apply_ends_with_git_worktree_prune_which_clears_a_dangling_registration(world):
    wt = world.add_worktree("lane-gone")
    shutil.rmtree(wt)  # the directory was deleted by hand; git still lists it
    assert world.is_registered(wt)
    _run(world, apply=False)
    assert world.is_registered(wt)
    _run(world, apply=True)
    assert not world.is_registered(wt)
    assert world.git_calls[-1][:2] == ("worktree", "prune")


def test_delete_merged_branches_deletes_only_merged_unchecked_out_branches(world):
    _git(world.clone, "branch", "pre-created", "origin/main")  # a lane made from an older main: no commit of its own
    _git(world.clone, "branch", "develop", "origin/main")
    _git(world.clone, "branch", "release/1.0", "origin/main")
    kept_wt = world.add_worktree("co", branch="checked-out-merged-wt", commit=True)  # unmerged, stays
    _git(kept_wt, "branch", "unmerged-extra")  # same commit as the unmerged worktree head: not an ancestor
    lane = world.add_worktree("lane-x")  # landed and removed: its branch becomes free
    world.merged_branch("feat-merged")  # a real merged feature branch checked out nowhere
    dirty = world.add_worktree("lane-dirty")
    (dirty / "z.txt").write_bytes(b"z\n")  # dirty: stays, so its branch stays
    world.advance_main()  # pre-created is now strictly behind origin/main, on its first-parent history
    _git(world.clone, "branch", "fresh-lane", "origin/main")  # pre-created lane: points exactly at origin/main
    report = _run(world, apply=False, delete_merged_branches=True)
    assert "feat-merged" in world.branches()  # dry run
    assert sorted(b.branch for b in report.branches_deleted) == sorted(["feat-merged", "lane-x"])
    report = _run(world, apply=True, delete_merged_branches=True)
    left = world.branches()
    for gone in ("feat-merged", "lane-x"):
        assert gone not in left
    for kept in (
        _git(world.clone, "branch", "--show-current"),
        "pre-created",
        "lane-dirty",
        "checked-out-merged-wt",
        "unmerged-extra",
        "fresh-lane",
        "develop",
        "release/1.0",
    ):
        assert kept in left
    del lane


def test_delete_merged_branches_also_takes_a_branch_whose_pr_merged_when_gh_is_used(world):
    wt = world.add_worktree("sq", commit=True)
    tip = _git(wt, "rev-parse", "HEAD")
    wt2 = world.add_worktree("sq2")  # a second worktree so `sq` can be removed and freed
    del wt2
    pr = [{"headRefName": "sq", "state": "MERGED", "headRefOid": tip, "baseRefName": "main"}]
    report = pw.prune(world.ctx(apply=True, use_gh=True, delete_merged_branches=True, gh=world.gh_stub(pr)))
    assert "sq" in [b.branch for b in report.branches_deleted] and "sq" not in world.branches()


def test_without_the_flag_no_branch_is_deleted(world):
    _git(world.clone, "branch", "stale-merged", "origin/main")
    _run(world, apply=True)
    assert "stale-merged" in world.branches()


def test_output_is_ascii_and_names_removed_and_kept(world):
    world.add_worktree("lane-a")
    dirty = world.add_worktree("lane-b")
    (dirty / "new.txt").write_bytes(b"x\n")
    _run(world, apply=True)
    text = world.text()
    assert text.isascii()
    assert "lane-a" in text and "lane-b" in text and "removed" in text and "kept" in text


def test_main_exit_codes_and_default_is_dry(world):
    wt = world.add_worktree("lane-a")
    assert pw.main(["--no-fetch", "--tmp-root", str(world.tmp_root), "--min-age-hours", "0"], repo=world.clone) == 0
    assert wt.is_dir()
    assert pw.main(["--no-fetch", "--tmp-root", str(world.tmp_root), "--min-age-hours", "0", "--apply"], repo=world.clone) == 0
    assert not wt.exists()


def test_missing_origin_main_refuses_without_changing_anything(world):
    wt = world.add_worktree("lane-a")
    _git(world.clone, "remote", "rename", "origin", "elsewhere")
    assert pw.main(["--no-fetch", "--apply", "--min-age-hours", "0"], repo=world.clone) == pw.EXIT_REFUSED
    assert wt.is_dir()


# --- fix round: cold review findings ---------------------------------------------------------------------


def test_a_worktree_that_contains_another_worktree_is_kept_so_the_inner_one_is_never_deleted_with_it(world):
    outer = world.add_worktree("outer")
    world.exclude(outer, ".claude/")
    inner = outer / ".claude" / "worktrees" / "inner"
    _git(outer, "worktree", "add", "-q", "-b", "inner", str(inner), "origin/main")
    (inner / "work.txt").write_bytes(b"precious\n")  # the inner agent's uncommitted work
    report = _run(world, apply=True)
    assert outer.is_dir() and (inner / "work.txt").is_file()
    assert any(k.path.name == "outer" and "contains" in k.reason for k in report.kept)


def test_find_links_reports_a_nested_repository_and_does_not_descend_into_it(world, tmp_path):
    root = tmp_path / "wt"
    (root / "sub" / "nested").mkdir(parents=True)
    (root / "sub" / "nested" / ".git").write_bytes(b"gitdir: elsewhere\n")
    (root / "sub" / "nested" / "inner.txt").write_bytes(b"x\n")
    links, problems = pw.scan_links(root)
    assert links == [] and any("nested" in p for p in problems)


def test_an_unscannable_directory_keeps_the_worktree_and_says_so(world, monkeypatch):
    wt = world.add_worktree("lane-acl")
    (wt / "app" / "src" / "App" / "obj" / "bad").mkdir(parents=True)
    world.exclude(wt, "obj/")
    real = os.scandir

    def flaky(path="."):
        if str(path).replace("\\", "/").endswith("obj/bad"):
            raise PermissionError("denied")
        return real(path)

    monkeypatch.setattr(pw.os, "scandir", flaky)
    report = _run(world, apply=True)
    assert wt.is_dir() and report.removed == []
    assert any(k.path.name == "lane-acl" and "could not scan" in k.reason for k in report.kept)


def test_ignored_files_that_are_not_known_build_output_keep_the_worktree(world):
    wt = world.add_worktree("lane-ign")
    world.exclude(wt, "scratch.local", "obj/", "__pycache__/", "*.egg-info/")
    (wt / "scratch.local").write_bytes(b"my notes\n")
    report = _run(world, apply=True)
    assert wt.is_dir()
    assert any(k.path.name == "lane-ign" and "ignored" in k.reason and "scratch.local" in k.reason for k in report.kept)


def test_known_build_output_alone_does_not_keep_a_worktree(world):
    wt = world.add_worktree("lane-build")
    world.exclude(wt, "obj/", "__pycache__/", "*.egg-info/")
    (wt / "app" / "src" / "App" / "obj").mkdir(parents=True)
    (wt / "app" / "src" / "App" / "obj" / "x.dll").write_bytes(b"x")
    (wt / "worker" / "pkg.egg-info").mkdir()
    (wt / "worker" / "pkg.egg-info" / "PKG-INFO").write_bytes(b"x")
    (wt / "app" / ".artifacts" / "lane" / "bin").mkdir(parents=True)  # MEASURED 2026-10-16: the real build output root
    (wt / "app" / ".artifacts" / "lane" / "bin" / "App.xbf").write_bytes(b"x")
    world.exclude(wt, ".artifacts/")
    (wt / "worker" / "sub").mkdir()
    (wt / "worker" / "sub" / "__pycache__").mkdir()
    (wt / "worker" / "sub" / "__pycache__" / "m.pyc").write_bytes(b"x")
    assert [r.path.name for r in _run(world, apply=True).removed] == ["lane-build"]


def test_app_secrets_keep_the_worktree_even_when_git_would_not_notice(world):
    wt = world.add_worktree("lane-sec")
    (wt / "app" / "secrets").mkdir(parents=True)
    (wt / "app" / "secrets" / "oauth_client.local.json").write_bytes(b"{}")
    world.exclude(wt, "app/secrets/")
    report = _run(world, apply=True)
    assert wt.is_dir()
    assert any(k.path.name == "lane-sec" and k.reason.startswith("app/secrets holds") for k in report.kept)


def test_a_staged_rename_counts_once(world):
    wt = world.add_worktree("lane-mv")
    _git(wt, "mv", "a.txt", "b.txt")
    report = _run(world, apply=True)
    assert any(k.path.name == "lane-mv" and k.reason.endswith("(1)") for k in report.kept)


def test_dry_run_rows_say_would_remove_and_apply_rows_say_removed(world):
    world.add_worktree("lane-a")
    _run(world, apply=False)
    assert any(ln.strip().startswith("would remove") and "lane-a" in ln for ln in world.out)
    assert not any(ln.strip().startswith("removed ") for ln in world.out)
    world.out.clear()
    _run(world, apply=True)
    assert any(ln.strip().startswith("removed ") and "lane-a" in ln for ln in world.out)


def test_status_runs_without_optional_locks_and_after_the_age_check(world):
    world.add_worktree("lane-fresh")
    pw.prune(world.ctx(apply=False, min_age_hours=6.0))
    assert not any(c[:1] == ("status",) or "status" in c[:2] for c in world.git_calls)  # fresh: never inspected
    world.git_calls.clear()
    pw.prune(world.ctx(apply=False))
    status = [c for c in world.git_calls if "status" in c[:2]]
    assert status and all(c[0] == "--no-optional-locks" for c in status)


def test_a_dry_run_does_not_fetch_but_apply_and_the_fetch_flag_do():
    def fetch_of(argv):
        return pw.ctx_from_args(pw.parse_args(argv), Path(".")).fetch

    assert fetch_of([]) is False
    assert fetch_of(["--apply"]) is True
    assert fetch_of(["--apply", "--no-fetch"]) is False
    assert fetch_of(["--fetch"]) is True


@needs_windows
def test_a_repo_path_given_through_a_junction_still_counts_as_this_checkout(world, tmp_path):
    wt = world.add_worktree("lane-alias")
    alias = tmp_path / "alias"
    _junction(alias, wt)
    report = pw.prune(world.ctx(apply=True, repo=alias))
    assert wt.is_dir()
    assert any(k.path.name == "lane-alias" and "this checkout" in k.reason for k in report.kept)
    os.rmdir(alias)


@needs_windows
def test_the_junction_is_recreated_when_git_then_fails_to_remove_the_worktree(world):
    wt = world.add_worktree("lane-fail")
    _junction(wt / "worker" / ".venv", world.sentinel)

    class Failed:
        returncode = 1
        stdout = b""
        stderr = b"boom"

    def spy(cwd: Path, *args: str, **kwargs):
        if args[:2] == ("worktree", "remove"):
            return Failed()
        return pw.git(cwd, *args, **kwargs)

    report = pw.prune(world.ctx(apply=True, git=spy))
    assert wt.is_dir() and report.removed == []
    assert os.path.isjunction(wt / "worker" / ".venv")
    assert (wt / "worker" / ".venv" / "marker.txt").is_file()
    assert any("remove failed" in k.reason for k in report.kept if k.path.name == "lane-fail")


# --- fix round 2 ------------------------------------------------------------------------------------------


def test_a_fresh_lane_exactly_at_origin_main_is_kept(world):
    wt = world.add_worktree("fresh", fresh=True)
    report = _run(world, apply=True)
    assert wt.is_dir()
    assert any(k.path.name == "fresh" and "no commit of its own" in k.reason for k in report.kept)


def test_a_fresh_lane_is_only_fresh_for_fresh_lane_hours_then_it_is_a_stale_checkout(world):
    wt = world.add_worktree("stale-fresh", fresh=True)
    base = time.time()
    young = pw.prune(world.ctx(apply=False, now=lambda: base + 47 * 3600))
    assert wt.is_dir() and any(k.path.name == "stale-fresh" and "no commit of its own" in k.reason for k in young.kept)
    old = pw.prune(world.ctx(apply=False, now=lambda: base + 49 * 3600))
    assert [r.path.name for r in old.removed] == ["stale-fresh"]
    custom = pw.prune(world.ctx(apply=False, fresh_lane_hours=1.0, now=lambda: base + 2 * 3600))
    assert [r.path.name for r in custom.removed] == ["stale-fresh"]


def test_a_stale_fresh_lane_is_still_kept_when_dirty_or_mid_operation(world):
    dirty = world.add_worktree("stale-dirty", fresh=True)
    (dirty / "mine.txt").write_bytes(b"unsaved\n")
    busy = world.add_worktree("stale-busy", fresh=True)
    gitdir = Path(_git(busy, "rev-parse", "--absolute-git-dir"))
    (gitdir / "MERGE_HEAD").write_text(_git(busy, "rev-parse", "HEAD") + "\n", encoding="utf-8", newline="\n")
    report = pw.prune(world.ctx(apply=True, now=lambda: time.time() + 100 * 3600))
    assert dirty.is_dir() and busy.is_dir() and report.removed == []


def test_a_lane_created_from_an_older_main_with_no_commits_is_kept_after_main_moves(world):
    wt = world.add_worktree("old-fresh", fresh=True)
    world.advance_main()
    report = _run(world, apply=True)
    assert wt.is_dir()
    assert any(k.path.name == "old-fresh" and "no commit of its own" in k.reason for k in report.kept)


def test_unknown_age_keeps_the_worktree(world, monkeypatch):
    wt = world.add_worktree("lane-age")
    real = os.scandir

    def flaky(path="."):
        if os.path.normcase(str(path)) == os.path.normcase(str(wt)):
            raise PermissionError("denied")
        return real(path)

    monkeypatch.setattr(pw.os, "scandir", flaky)
    report = _run(world, apply=True)
    assert wt.is_dir() and report.removed == []
    assert any(k.path.name == "lane-age" and "age unknown" in k.reason for k in report.kept)


def test_a_deep_recent_edit_two_levels_down_counts_as_a_recent_touch(world):
    wt = world.add_worktree("lane-deep")
    base = time.time()
    readme = wt / "worker" / "README.md"
    readme.write_bytes(readme.read_bytes())  # same content: git calls it clean, but the file was just written
    os.utime(readme, (base + 2 * 3600, base + 2 * 3600))  # level 2 (worker/README.md); everything else is 7 h old
    report = pw.prune(world.ctx(apply=False, min_age_hours=6.0, now=lambda: base + 7 * 3600))
    assert any(k.path.name == "lane-deep" and "touched" in k.reason for k in report.kept)


@needs_windows
def test_a_worktree_path_that_is_itself_a_junction_is_kept(world, tmp_path):
    # git registers the REAL path of a worktree, so a listed path that is a link can only come from a hand-edited
    # registration; decide() must still refuse it rather than rely on that
    real = tmp_path / "real_empty"
    real.mkdir()
    link = tmp_path / "linked"
    _junction(link, real)
    head = _git(world.clone, "rev-parse", "origin/main")
    wt = pw.Worktree(link, head=head, branch="x")
    reason = pw.decide(world.ctx(), wt, pw.Worktree(world.clone, head=head), [], None)
    assert reason is not None and reason.startswith("the worktree path is itself a link")
    os.rmdir(link)


@needs_windows
def test_a_git_remove_that_guts_the_tree_is_reported_as_partial_and_no_link_is_recreated_inside_it(world):
    wt = world.add_worktree("lane-gut")
    _junction(wt / "worker" / ".venv", world.sentinel)

    class Failed:
        returncode = 1
        stdout = b""
        stderr = b"Directory not empty"

    def spy(cwd: Path, *args: str, **kwargs):
        if args[:2] == ("worktree", "remove"):
            for child in (wt / "worker").iterdir():
                if not os.path.isjunction(child):
                    child.unlink()
            os.rmdir(wt / "worker")  # git deleted most of the tree before failing
            return Failed()
        return pw.git(cwd, *args, **kwargs)

    report = pw.prune(world.ctx(apply=True, git=spy))
    assert report.removed == []
    reason = next(k.reason for k in report.kept if k.path.name == "lane-gut")
    assert "PARTIAL" in reason
    assert not (wt / "worker").exists()  # nothing was recreated inside the gutted tree
    assert (world.sentinel / "marker.txt").is_file()


@needs_windows
def test_an_interrupt_during_removal_puts_the_junctions_back(world):
    wt = world.add_worktree("lane-int")
    _junction(wt / "worker" / ".venv", world.sentinel)

    def spy(cwd: Path, *args: str, **kwargs):
        if args[:2] == ("worktree", "remove"):
            raise KeyboardInterrupt
        return pw.git(cwd, *args, **kwargs)

    with pytest.raises(KeyboardInterrupt):
        pw.prune(world.ctx(apply=True, git=spy))
    assert os.path.isjunction(wt / "worker" / ".venv")
    assert (world.sentinel / "marker.txt").is_file()


def test_dry_run_predicts_apply_for_an_unscannable_tree(world, monkeypatch):
    wt = world.add_worktree("lane-dry")
    (wt / "app" / "src" / "App" / "obj" / "bad").mkdir(parents=True)
    world.exclude(wt, "obj/")
    real = os.scandir

    def flaky(path="."):
        if str(path).replace("\\", "/").endswith("obj/bad"):
            raise PermissionError("denied")
        return real(path)

    monkeypatch.setattr(pw.os, "scandir", flaky)
    report = _run(world, apply=False)
    assert report.removed == [] and any("could not scan" in k.reason for k in report.kept)


def test_build_output_names_only_count_at_known_roots(world):
    wt = world.add_worktree("lane-data")
    world.exclude(wt, "obj/", "bin/")
    (wt / "data" / "obj").mkdir(parents=True)
    (wt / "data" / "obj" / "results.csv").write_bytes(b"precious\n")
    report = _run(world, apply=True)
    assert wt.is_dir()
    assert any(k.path.name == "lane-data" and "ignored" in k.reason and "data/obj" in k.reason for k in report.kept)


@pytest.mark.parametrize(
    "rel",
    ["app/tools/DnaEntropyGraph.CloudCli/obj/x.cs", ".hypothesis/examples/a", "worker/.hypothesis/constants/b"],
)
def test_tool_project_output_and_hypothesis_cache_count_as_build_output(world, rel):
    wt = world.add_worktree("lane-tools")
    world.exclude(wt, "obj/", ".hypothesis/")
    (wt / rel).parent.mkdir(parents=True)
    (wt / rel).write_bytes(b"generated\n")
    _run(world, apply=True)
    assert not wt.exists()


def test_a_real_directory_at_worker_venv_is_not_waved_through(world):
    wt = world.add_worktree("lane-realvenv")
    (wt / "worker" / ".venv").mkdir()
    (wt / "worker" / ".venv" / "pyvenv.cfg").write_bytes(b"x")
    report = _run(world, apply=True)
    assert wt.is_dir()
    assert any(k.path.name == "lane-realvenv" and "real directory" in k.reason for k in report.kept)


def test_skip_worktree_bits_keep_the_worktree(world):
    wt = world.add_worktree("lane-skip")
    _git(wt, "update-index", "--skip-worktree", "a.txt")
    (wt / "a.txt").write_bytes(b"hidden edit\n")  # git status will not show this
    report = _run(world, apply=True)
    assert wt.is_dir()
    assert any(k.path.name == "lane-skip" and "skip-worktree" in k.reason for k in report.kept)


@pytest.mark.parametrize("marker", ["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG", "rebase-merge", "rebase-apply"])
def test_an_operation_in_progress_keeps_the_worktree(world, marker):
    wt = world.add_worktree(f"lane-{marker.lower()}")
    gitdir = Path(_git(wt, "rev-parse", "--absolute-git-dir"))
    if marker.startswith("rebase"):
        (gitdir / marker).mkdir()
    else:
        (gitdir / marker).write_text(_git(wt, "rev-parse", "HEAD") + "\n", encoding="utf-8", newline="\n")
    report = _run(world, apply=True)
    assert wt.is_dir()
    assert any(k.path == wt and "in progress" in k.reason for k in report.kept)


# --- content-based "landed" (#812) ---------------------------------------------------------------------


def _branch_with(world: World, name: str, files: dict[str, bytes], base: str = "origin/main") -> str:
    """A local branch (checked out nowhere) with one commit of its own adding `files`. Returns the tip."""
    tmp = world.root / "scratch" / name.replace("/", "_")
    _git(world.clone, "worktree", "add", "-q", "--detach", str(tmp), base)
    for rel, data in files.items():
        (tmp / rel).write_bytes(data)
        _git(tmp, "add", "--", rel)
    _git(tmp, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", f"work {name}")
    tip = _git(tmp, "rev-parse", "HEAD")
    _git(world.clone, "worktree", "remove", "--force", str(tmp))
    _git(world.clone, "branch", name, tip)
    return tip


def test_a_superseded_attempt_whose_content_landed_under_another_name_is_deleted(world):
    _branch_with(world, "feat-r1", {"x.txt": b"the fix\n"})
    r2 = _branch_with(world, "feat-r2", {"x.txt": b"the fix\n"})  # different commit, same content
    world.merge_into_main(r2)
    report = _run(world, apply=True, delete_merged_branches=True)
    assert sorted(b.branch for b in report.branches_deleted) == ["feat-r1", "feat-r2"]
    assert "feat-r1" not in world.branches()


def test_a_branch_with_one_unique_hunk_is_kept(world):
    _branch_with(world, "feat-r1", {"x.txt": b"the fix\n", "y.txt": b"only here\n"})
    r2 = _branch_with(world, "feat-r2", {"x.txt": b"the fix\n"})
    world.merge_into_main(r2)
    report = _run(world, apply=True, delete_merged_branches=True)
    assert "feat-r1" in world.branches()
    assert [b.branch for b in report.branches_deleted] == ["feat-r2"]


def _commit_on_main(world: World, files: dict[str, bytes]) -> str:
    """A plain commit on origin/main (not a merge). Returns the new main tip."""
    _git(world.clone, "switch", "-q", "--detach", "origin/main")
    for rel, data in files.items():
        (world.clone / rel).write_bytes(data)
        _git(world.clone, "add", "--", rel)
    _git(world.clone, "commit", "-q", "-m", "main edit")
    tip = _git(world.clone, "rev-parse", "HEAD")
    _git(world.clone, "push", "-q", "origin", "HEAD:refs/heads/main")
    _git(world.clone, "fetch", "-q", "origin")
    _git(world.clone, "switch", "-q", "-")
    return tip


def test_a_branch_whose_change_main_later_undid_is_not_contained(world):
    _branch_with(world, "feat-old", {"a.txt": b"changed\n"})
    r2 = _branch_with(world, "feat-new", {"a.txt": b"changed\n"})  # same content, different commit
    world.merge_into_main(r2)
    report = _run(world, apply=False, delete_merged_branches=True)
    assert "feat-old" in [b.branch for b in report.branches_deleted]  # precondition: while main holds the change it IS contained
    _commit_on_main(world, {"a.txt": b"one\n"})  # main undoes it
    report = _run(world, apply=True, delete_merged_branches=True)
    assert "feat-old" in world.branches()  # not an ancestor, and merging it would re-apply the change
    assert "feat-old" not in [b.branch for b in report.branches_deleted]


def test_a_merge_driver_in_info_attributes_cannot_make_an_edit_look_contained(world):
    common = Path(_git(world.clone, "rev-parse", "--git-common-dir"))
    common = common if common.is_absolute() else world.clone / common
    (common / "info").mkdir(parents=True, exist_ok=True)
    (common / "info" / "attributes").write_text("a.txt merge=keepmine\n", encoding="utf-8", newline="\n")
    _git(world.clone, "config", "merge.keepmine.driver", "true")  # `true` leaves %A (main's side) in place: a silent 'ours'
    _branch_with(world, "feat-edit", {"a.txt": b"X\n"})
    _commit_on_main(world, {"a.txt": b"Y\n"})
    report = _run(world, apply=True, delete_merged_branches=True)
    assert "feat-edit" in world.branches() and report.branches_deleted == []


def test_a_committed_gitattributes_merge_rule_cannot_make_an_edit_look_contained(world):
    _commit_on_main(world, {".gitattributes": b"a.txt merge=keepmine\n"})
    _git(world.clone, "config", "merge.keepmine.driver", "true")
    _branch_with(world, "feat-edit", {"a.txt": b"X\n"})
    _commit_on_main(world, {"a.txt": b"Y\n"})
    (world.clone / ".gitattributes").write_bytes(b"a.txt merge=keepmine\n")  # the primary checkout (on main) has the rule in its tree
    report = _run(world, apply=True, delete_merged_branches=True)
    assert "feat-edit" in world.branches() and report.branches_deleted == []


def test_contained_in_a_held_tip_makes_a_superseded_branch_landed_and_the_tip_itself_is_kept(world):
    _branch_with(world, "queued", {"z.txt": b"z\n", "w.txt": b"w\n"})
    _branch_with(world, "attempt-r1", {"z.txt": b"z\n"})  # a subset of the queued branch
    report = _run(world, apply=False, delete_merged_branches=True)
    assert report.branches_deleted == []  # without --contained-in nothing holds it
    report = _run(world, apply=True, delete_merged_branches=True, contained_in=["queued"])
    assert [b.branch for b in report.branches_deleted] == ["attempt-r1"]
    assert "queued" in world.branches() and "attempt-r1" not in world.branches()


def test_an_unresolvable_contained_in_ref_is_a_warning_not_a_crash(world):
    _branch_with(world, "attempt-r1", {"z.txt": b"z\n"})
    report = _run(world, apply=True, delete_merged_branches=True, contained_in=["no-such-ref"])
    assert report.branches_deleted == [] and "no-such-ref" in world.text()


def test_a_content_contained_branch_checked_out_in_a_dirty_worktree_is_never_deleted(world):
    _branch_with(world, "feat-r1", {"x.txt": b"the fix\n"})
    r2 = _branch_with(world, "feat-r2", {"x.txt": b"the fix\n"})
    world.merge_into_main(r2)
    wt = world.root / "wts" / "r1-wt"
    _git(world.clone, "worktree", "add", "-q", str(wt), "feat-r1")
    (wt / "unsaved.txt").write_bytes(b"work in progress\n")
    report = _run(world, apply=True, delete_merged_branches=True)
    assert wt.is_dir() and "feat-r1" in world.branches()
    assert any(k.path == wt for k in report.kept)


def test_a_clean_worktree_on_a_content_contained_branch_is_removed_and_its_branch_freed(world):
    _branch_with(world, "feat-r1", {"x.txt": b"the fix\n"})
    r2 = _branch_with(world, "feat-r2", {"x.txt": b"the fix\n"})
    world.merge_into_main(r2)
    wt = world.root / "wts" / "r1-wt"
    _git(world.clone, "worktree", "add", "-q", str(wt), "feat-r1")
    report = _run(world, apply=True, delete_merged_branches=True)
    assert not wt.exists() and "feat-r1" not in world.branches()
    assert [r.path.name for r in report.removed] == ["r1-wt"]


def test_a_fresh_lane_sitting_on_main_is_not_swept_up_by_the_content_test(world):
    _git(world.clone, "branch", "fresh-lane", "origin/main")
    _run(world, apply=True, delete_merged_branches=True)
    assert "fresh-lane" in world.branches()


def _push_remote(world: World, name: str, files: dict[str, bytes]) -> str:
    tip = _branch_with(world, name, files)
    _git(world.clone, "push", "-q", "origin", f"{name}:refs/heads/{name}")
    _git(world.clone, "branch", "-D", name)
    _git(world.clone, "fetch", "-q", "origin")
    return tip


def _remote_heads(world: World) -> list[str]:
    return sorted(_git(world.origin, "for-each-ref", "--format=%(refname:short)", "refs/heads").split())


def test_remote_mode_is_a_dry_run_by_default_and_names_the_contained_branches(world):
    r2 = _push_remote(world, "feat-r2", {"x.txt": b"the fix\n"})
    _push_remote(world, "feat-r1", {"x.txt": b"the fix\n"})
    _push_remote(world, "feat-uniq", {"u.txt": b"unique\n"})
    world.merge_into_main(r2)
    report = _run(world, apply=False, remote=True, gh=world.gh_stub([]))
    assert sorted(d.branch for d in report.remote_deleted) == ["feat-r1", "feat-r2"]
    assert _remote_heads(world) == ["feat-r1", "feat-r2", "feat-uniq", "main"]


def test_remote_apply_deletes_contained_remote_branches_only(world):
    r2 = _push_remote(world, "feat-r2", {"x.txt": b"the fix\n"})
    _push_remote(world, "feat-r1", {"x.txt": b"the fix\n"})
    _push_remote(world, "feat-uniq", {"u.txt": b"unique\n"})
    _push_remote(world, "feat-open", {"x.txt": b"the fix\n"})
    _push_remote(world, "queued", {"q.txt": b"q\n"})
    _push_remote(world, "feat-sub", {"q.txt": b"q\n"})
    world.merge_into_main(r2)
    prs = [{"headRefName": "feat-open", "state": "OPEN", "headRefOid": "0" * 40, "baseRefName": "main"}]
    report = _run(world, apply=True, remote=True, contained_in=["origin/queued"], gh=world.gh_stub(prs))
    assert sorted(d.branch for d in report.remote_deleted) == ["feat-r1", "feat-r2", "feat-sub"]
    assert _remote_heads(world) == ["feat-open", "feat-uniq", "main", "queued"]


def test_without_the_remote_flag_origin_is_never_touched(world):
    r2 = _push_remote(world, "feat-r2", {"x.txt": b"the fix\n"})
    world.merge_into_main(r2)
    report = _run(world, apply=True, delete_merged_branches=True)
    assert report.remote_deleted == []
    assert "feat-r2" in _remote_heads(world)


def test_remote_mode_without_gh_answers_deletes_nothing(world):
    r2 = _push_remote(world, "feat-r2", {"x.txt": b"the fix\n"})
    world.merge_into_main(r2)
    report = _run(world, apply=True, remote=True, gh=[sys.executable, "-c", "import sys; sys.exit(1)"])
    assert report.remote_deleted == [] and "feat-r2" in _remote_heads(world)
    assert "gh" in world.text()


def test_cli_flags_reach_the_context(world):
    args = pw.parse_args(["--contained-in", "a", "b", "--remote", "--delete-merged-branches"])
    ctx = pw.ctx_from_args(args, world.clone)
    assert ctx.contained_in == ["a", "b"] and ctx.remote is True


def test_a_keep_tip_that_is_a_subset_of_another_keep_tip_is_still_kept(world):
    _branch_with(world, "queued", {"z.txt": b"z\n", "w.txt": b"w\n"})
    _branch_with(world, "queued-sub", {"z.txt": b"z\n"})
    report = _run(world, apply=True, delete_merged_branches=True, contained_in=["queued", "queued-sub"])
    assert report.branches_deleted == []
    assert {"queued", "queued-sub"} <= set(world.branches())


# --- review round 2 (#812): remote safety, undo information ---------------------------------------------


def _open_pr(head: str, base: str = "main") -> dict:
    return {"headRefName": head, "state": "OPEN", "headRefOid": "0" * 40, "baseRefName": base}


def test_a_remote_branch_that_is_the_base_of_an_open_stacked_pr_is_kept(world):
    r2 = _push_remote(world, "feat-base", {"x.txt": b"the fix\n"})
    _push_remote(world, "feat-top", {"t.txt": b"top\n"})
    world.merge_into_main(r2)
    report = _run(world, apply=True, remote=True, gh=world.gh_stub([_open_pr("feat-top", base="feat-base")]))
    assert report.remote_deleted == [] and "feat-base" in _remote_heads(world)  # deleting a base auto-closes the PR on top of it


def test_the_remote_open_check_asks_gh_for_open_prs_only(world):
    r2 = _push_remote(world, "feat-r2", {"x.txt": b"the fix\n"})
    world.merge_into_main(r2)
    _run(world, apply=False, remote=True, gh=world.gh_stub([]))
    assert world.gh_calls() and all("--state open" in c for c in world.gh_calls())


def test_a_saturated_open_pr_list_refuses_all_remote_deletion(world):
    r2 = _push_remote(world, "feat-r2", {"x.txt": b"the fix\n"})
    world.merge_into_main(r2)
    report = _run(world, apply=True, remote=True, gh_limit=1, gh=world.gh_stub([_open_pr("some-other")]))
    assert report.remote_deleted == [] and "feat-r2" in _remote_heads(world)
    assert "--limit" in world.text()


def test_remote_delete_is_a_lease_on_the_sha_we_read(world):
    r2 = _push_remote(world, "feat-r2", {"x.txt": b"the fix\n"})
    world.merge_into_main(r2)
    old = _git(world.clone, "rev-parse", "refs/remotes/origin/feat-r2")
    newer = _git(world.clone, "commit-tree", f"{old}^{{tree}}", "-p", old, "-m", "pushed by someone else meanwhile")
    _git(world.clone, "push", "-q", "-f", "origin", f"{newer}:refs/heads/feat-r2")
    _git(world.clone, "update-ref", "refs/remotes/origin/feat-r2", old)  # our view of origin is stale
    report = _run(world, apply=True, remote=True, gh=world.gh_stub([]))
    assert "feat-r2" in _remote_heads(world) and report.remote_deleted == []
    assert "feat-r2" in world.text()


def test_every_deleted_branch_and_worktree_is_printed_with_its_sha(world):
    wt = world.add_worktree("lane-a")
    wt_head = _git(wt, "rev-parse", "HEAD")
    local = world.merged_branch("feat-local")
    r2 = _push_remote(world, "feat-r2", {"x.txt": b"the fix\n"})
    world.merge_into_main(r2)
    _run(world, apply=True, delete_merged_branches=True, remote=True, gh=world.gh_stub([]))
    text = world.text()
    assert f"lane-a@{wt_head}" in text and f"feat-local@{local}" in text and f"feat-r2@{r2}" in text


def test_a_local_only_keep_tip_cannot_justify_a_remote_delete_but_still_justifies_a_local_one(world):
    _branch_with(world, "queued", {"q.txt": b"q\n"})  # never pushed
    _push_remote(world, "feat-sub", {"q.txt": b"q\n"})
    _branch_with(world, "attempt-local", {"q.txt": b"q\n"})
    report = _run(world, apply=True, delete_merged_branches=True, remote=True, contained_in=["queued"], gh=world.gh_stub([]))
    assert "feat-sub" in _remote_heads(world) and report.remote_deleted == []
    assert "queued" in world.text()  # the skipped tip is named
    assert "attempt-local" not in world.branches()  # local deletion may still use the tip


def test_a_pushed_keep_tip_at_the_same_sha_justifies_a_remote_delete(world):
    _push_remote(world, "queued", {"q.txt": b"q\n"})
    _git(world.clone, "branch", "queued", "origin/queued")  # a local branch at the same sha as origin/queued
    _push_remote(world, "feat-sub", {"q.txt": b"q\n"})
    report = _run(world, apply=True, remote=True, contained_in=["queued"], gh=world.gh_stub([]))
    assert [d.branch for d in report.remote_deleted] == ["feat-sub"]


def test_a_remote_branch_checked_out_in_a_kept_worktree_is_not_deleted(world):
    r2 = _push_remote(world, "feat-r2", {"x.txt": b"the fix\n"})
    _push_remote(world, "feat-r1", {"x.txt": b"the fix\n"})
    world.merge_into_main(r2)
    wt = world.root / "wts" / "r1-wt"
    _git(world.clone, "worktree", "add", "-q", "-b", "feat-r1", str(wt), "origin/feat-r1")
    (wt / "unsaved.txt").write_bytes(b"work in progress\n")
    report = _run(world, apply=True, remote=True, gh=world.gh_stub([]))
    assert "feat-r1" in _remote_heads(world) and "feat-r2" not in _remote_heads(world)
    assert [d.branch for d in report.remote_deleted] == ["feat-r2"]


def test_a_dry_run_says_it_did_not_fetch(world):
    _run(world, apply=False)
    assert "not fetched" in world.text()
    world.out.clear()
    _run(world, apply=False, fetch=True)
    assert "not fetched" not in world.text()


def test_the_remote_path_does_not_blame_use_gh(world):
    _run(world, apply=False, remote=True, gh=[sys.executable, "-c", "import sys; sys.exit(1)"])
    assert "drop --use-gh" not in world.text()
