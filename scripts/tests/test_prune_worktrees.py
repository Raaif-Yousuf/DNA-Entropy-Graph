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

    def add_worktree(self, name: str, branch: str | None = None, *, commit: bool = False, detach: bool = False) -> Path:
        wt = self.root / "wts" / name
        if detach:
            _git(self.clone, "worktree", "add", "-q", "--detach", str(wt), "origin/main")
        else:
            _git(self.clone, "worktree", "add", "-q", "-b", branch or name, str(wt), "origin/main")
        if commit:
            (wt / f"{name}.txt").write_bytes(b"work\n")
            _git(wt, "add", "--", f"{name}.txt")
            _git(wt, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", f"work {name}")
        return wt

    def advance_main_to_include(self, wt: Path) -> None:
        """Make origin/main contain wt's HEAD (what a merged PR does)."""
        sha = _git(wt, "rev-parse", "HEAD")
        _git(self.clone, "push", "-q", "origin", f"{sha}:refs/heads/main")
        _git(self.clone, "fetch", "-q", "origin")

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
    world.advance_main_to_include(wt)
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
    live = world.add_worktree("det-new", detach=True)
    (live / "x.txt").write_bytes(b"x\n")
    _git(live, "add", "--", "x.txt")
    _git(live, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", "detached work")
    report = _run(world, apply=True)
    assert not gone.exists() and live.is_dir()
    assert any(k.path.name == "det-new" and "not merged" in k.reason for k in report.kept)


# --- gh ---------------------------------------------------------------------------------------------


def test_use_gh_removes_merged_and_closed_pr_branches_keeps_open_ones_in_one_call(world):
    merged = world.add_worktree("pr-merged", commit=True)
    closed = world.add_worktree("pr-closed", commit=True)
    opened = world.add_worktree("pr-open", commit=True)
    plain = world.add_worktree("no-pr", commit=True)
    prs = [
        {"headRefName": "pr-merged", "state": "MERGED"},
        {"headRefName": "pr-closed", "state": "CLOSED"},
        {"headRefName": "pr-open", "state": "OPEN"},
    ]
    report = pw.prune(world.ctx(apply=True, use_gh=True, gh=world.gh_stub(prs)))
    assert not merged.exists() and not closed.exists()
    assert opened.is_dir() and plain.is_dir()
    assert any(k.path.name == "pr-open" and "open PR" in k.reason for k in report.kept)
    assert len(world.gh_calls()) == 1  # batched, not one call per worktree
    assert "--state all" in world.gh_calls()[0]


def test_without_use_gh_gh_is_never_run(world):
    world.add_worktree("pr-merged", commit=True)
    pw.prune(world.ctx(apply=True, gh=world.gh_stub([{"headRefName": "pr-merged", "state": "MERGED"}])))
    assert world.gh_calls() == []


def test_a_gh_failure_degrades_to_ancestor_only_with_a_warning(world):
    wt = world.add_worktree("pr-merged", commit=True)
    report = pw.prune(world.ctx(apply=True, use_gh=True, gh=[sys.executable, "-c", "import sys; sys.exit(1)"]))
    assert wt.is_dir() and report.removed == []
    assert "WARNING" in world.text()


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
    _junction(wt / "deep" / "er" / "link", world.sentinel)
    # an untracked junction is untracked content: git sees it, so it is kept as dirty unless ignored
    (wt / ".git_info_exclude_probe").write_bytes(b"")
    exclude = Path(_git(wt, "rev-parse", "--git-path", "info/exclude"))
    exclude = exclude if exclude.is_absolute() else wt / exclude
    exclude.parent.mkdir(parents=True, exist_ok=True)
    exclude.write_text("deep/\n.git_info_exclude_probe\n", encoding="utf-8", newline="\n")
    report = pw.prune(world.ctx(apply=True))
    assert [r.path.name for r in report.removed] == ["lane-j2"]
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


def _orphan(world: World, name: str, with_junction: bool = True) -> Path:
    d = world.tmp_root / name
    (d / "scripts").mkdir(parents=True)
    (d / "scripts" / "f.py").write_bytes(b"x\n")
    if with_junction:
        _junction(d / "worker" / ".venv", world.sentinel)
    return d


def test_orphan_landwt_dir_is_removed_without_following_the_junction(world):
    orphan = _orphan(world, "feat-1-x-20261001120000", with_junction=os.name == "nt")
    locks = world.tmp_root / "locks"
    logs = world.tmp_root / "logs"
    locks.mkdir()
    logs.mkdir()
    (locks / "feat-1-x-20261001120000.json").write_text(json.dumps({"pid": 0, "start": 0}), encoding="utf-8", newline="\n")
    (logs / "keep.log").write_bytes(b"log\n")
    report = _run(world, apply=True)
    assert not orphan.exists()
    assert (world.sentinel / "marker.txt").is_file()
    assert locks.is_dir() and logs.is_dir() and (logs / "keep.log").is_file()
    assert [o.path.name for o in report.orphans_removed] == ["feat-1-x-20261001120000"]


def test_orphan_dry_run_leaves_it(world):
    orphan = _orphan(world, "feat-2-x-20261001120000", with_junction=False)
    report = _run(world, apply=False)
    assert orphan.is_dir() and [o.path.name for o in report.orphans_removed] == [orphan.name]


def test_orphan_with_a_live_lock_or_recent_touch_is_kept(world):
    live = _orphan(world, "feat-3-x-20261001120000", with_junction=False)
    (world.tmp_root / "locks").mkdir()
    (world.tmp_root / "locks" / f"{live.name}.json").write_text(
        json.dumps({"pid": os.getpid(), "start": time.time()}), encoding="utf-8", newline="\n"
    )
    fresh = _orphan(world, "feat-4-x-20261001120000", with_junction=False)
    report = pw.prune(world.ctx(apply=True, min_age_hours=6.0, pid_alive=lambda pid: True))
    assert live.is_dir() and fresh.is_dir() and report.orphans_removed == []


def test_a_landwt_dir_that_git_still_lists_is_not_an_orphan(world):
    wt = world.tmp_root / "feat-5-x-20261001120000"
    wt.parent.mkdir(parents=True)
    _git(world.clone, "worktree", "add", "-q", "--detach", str(wt), "origin/main")
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
    _git(world.clone, "branch", "stale-merged", "origin/main")
    _git(world.clone, "branch", "checked-out-merged", "origin/main")
    kept_wt = world.add_worktree("co", branch="checked-out-merged-wt", commit=True)  # unmerged, stays
    _git(kept_wt, "branch", "unmerged-extra")  # same commit as the unmerged worktree head: not an ancestor
    lane = world.add_worktree("lane-x")  # merged, removed: its branch becomes free
    dirty = world.add_worktree("lane-dirty")
    (dirty / "z.txt").write_bytes(b"z\n")  # dirty: stays, so its branch stays
    report = _run(world, apply=False, delete_merged_branches=True)
    assert "stale-merged" in world.branches()  # dry run
    assert sorted(b.branch for b in report.branches_deleted) == sorted(["stale-merged", "checked-out-merged", "lane-x"])
    report = _run(world, apply=True, delete_merged_branches=True)
    left = world.branches()
    for gone in ("stale-merged", "checked-out-merged", "lane-x"):
        assert gone not in left
    for kept in (_git(world.clone, "branch", "--show-current"), "lane-dirty", "checked-out-merged-wt", "unmerged-extra"):
        assert kept in left
    del lane


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
