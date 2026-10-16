"""Tests for scripts/land_wt.py (#510).

Every repository is a throwaway bare "origin" plus a clone under tmp_path, the venv is a fake
directory with marker files, `gh` is a stub script and premerge is an injected callable. GitHub and
the real venv are never touched.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import land_wt as lw  # noqa: E402

GH_STUB = """\
import json, sys
from pathlib import Path
log = Path(sys.argv[1])
log.open("a", encoding="utf-8", newline="\\n").write(json.dumps(sys.argv[2:]) + "\\n")
if sys.argv[2:4] == ["pr", "checks"]:
    # scripted replies: the Nth `pr checks` call gets replies[N] (the last one repeats); a string is raw stdout
    replies = json.loads(Path(str(log) + ".checks").read_text(encoding="utf-8"))
    seen = sum(1 for ln in log.read_text(encoding="utf-8").splitlines() if '"checks"' in ln) - 1
    reply = replies[min(seen, len(replies) - 1)]
    print(reply if isinstance(reply, str) else json.dumps(reply))
    sys.exit(1 if isinstance(reply, str) else 0)
if sys.argv[2:4] == ["run", "rerun"]:
    rc_file = Path(str(log) + ".rerunrc")
    sys.exit(int(rc_file.read_text()) if rc_file.exists() else 0)
if sys.argv[2:4] == ["pr", "merge"]:
    rc_file = Path(str(log) + ".mergerc")
    sys.exit(int(rc_file.read_text()) if rc_file.exists() else 0)
print("https://github.com/o/r/pull/7")
"""


def _git(cwd: Path, *args: str) -> str:
    proc = subprocess.run(["git", *args], cwd=cwd, capture_output=True, check=False)
    assert proc.returncode == 0, proc.stderr.decode(errors="replace")
    return proc.stdout.decode("utf-8", errors="replace").strip()


@dataclass
class World:
    root: Path
    origin: Path
    clone: Path
    venv: Path
    tmp_root: Path
    gh_log: Path
    premerge_calls: list[tuple[Path, bool]]
    premerge_result: list[int]
    seen_in_premerge: list[bool]

    def ctx(self) -> lw.Ctx:
        stub = self.root / "gh_stub.py"

        def fake_premerge(wt: Path, venv: Path, full: bool) -> tuple[int, str]:
            self.premerge_calls.append((wt, full))
            # the junction must be live and point at the fake venv while premerge runs
            self.seen_in_premerge.append((wt / "worker" / ".venv" / "marker.txt").is_file())
            code = self.premerge_result[0]
            return code, ("  FAIL  ruff-worker-check\n" if code else "PREMERGE: PASS\n")

        return lw.Ctx(
            repo=self.clone, venv=self.venv, tmp_root=self.tmp_root, gh=[sys.executable, str(stub), str(self.gh_log)], premerge=fake_premerge
        )

    def commit_on_branch(self, branch: str, files: dict[str, bytes], base: str = "origin/main") -> str:
        _git(self.clone, "switch", "-q", "-c", branch, base)
        for rel, data in files.items():
            path = self.clone / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            _git(self.clone, "add", "--", rel)
        _git(self.clone, "commit", "-q", "-m", f"work on {branch}")
        sha = _git(self.clone, "rev-parse", "HEAD")
        _git(self.clone, "switch", "-q", "--detach", "origin/main")
        return sha

    def advance_main(self, files: dict[str, bytes]) -> None:
        _git(self.clone, "switch", "-q", "--detach", "origin/main")
        for rel, data in files.items():
            (self.clone / rel).write_bytes(data)
            _git(self.clone, "add", "--", rel)
        _git(self.clone, "commit", "-q", "-m", "main moves")
        _git(self.clone, "push", "-q", "origin", "HEAD:refs/heads/main")
        _git(self.clone, "fetch", "-q", "origin")

    def remote_branches(self) -> list[str]:
        return sorted(_git(self.origin, "for-each-ref", "--format=%(refname:short)", "refs/heads").split())

    def gh_calls(self) -> list[list[str]]:
        if not self.gh_log.exists():
            return []
        return [json.loads(line) for line in self.gh_log.read_text(encoding="utf-8").splitlines()]

    def venv_intact(self) -> bool:
        return (self.venv / "marker.txt").is_file() and (self.venv / "Scripts" / "python.exe").is_file()

    def leftover_worktrees(self) -> list[str]:
        return [p.name for p in self.tmp_root.iterdir() if p.name not in ("logs", "locks")] if self.tmp_root.exists() else []


@pytest.fixture()
def world(tmp_path_factory) -> World:
    root = tmp_path_factory.mktemp("lw")
    origin = root / "o.git"
    clone = root / "c"
    _git(root, "init", "-q", "--bare", "-b", "main", str(origin))
    _git(root, "clone", "-q", str(origin), str(clone))
    _git(clone, "config", "user.email", "t@example.invalid")
    _git(clone, "config", "user.name", "t")
    _git(clone, "config", "core.autocrlf", "false")
    (clone / "docs").mkdir()
    (clone / "worker").mkdir()
    (clone / "docs" / "ToTest.md").write_bytes(b"| header |\n| row0 |\n")
    (clone / "worker" / "README.md").write_bytes(b"worker\n")
    (clone / "a.txt").write_bytes(b"one\n")
    _git(clone, "add", "--", "docs/ToTest.md", "worker/README.md", "a.txt")
    _git(clone, "commit", "-q", "-m", "init")
    _git(clone, "push", "-q", "origin", "HEAD:refs/heads/main")
    _git(clone, "fetch", "-q", "origin")
    venv = root / "venv"
    (venv / "Scripts").mkdir(parents=True)
    (venv / "marker.txt").write_bytes(b"real venv\n")
    (venv / "Scripts" / "python.exe").write_bytes(b"not really python\n")
    (root / "gh_stub.py").write_text(GH_STUB, encoding="utf-8", newline="\n")
    return World(root, origin, clone, venv, root / "t", root / "gh.log", [], [0], [])


def _land(world: World, branch: str, sha: str, **kwargs) -> int:
    body = world.root / "body.md"
    body.write_text("body\n", encoding="utf-8", newline="\n")
    return lw.land(world.ctx(), branch, sha, "feat: the thing", body, **kwargs)


# --- the happy path and what it must leave behind ---------------------------------------------------


def test_green_landing_pushes_one_branch_opens_one_pr_and_leaves_the_venv_and_no_worktree(world):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert "feat/1-x" in world.remote_branches()
    assert _git(world.origin, "rev-parse", "refs/heads/feat/1-x") == sha  # main had not moved: no merge commit
    creates = [c for c in world.gh_calls() if c[:2] == ["pr", "create"]]
    assert len(creates) == 1 and "feat/1-x" in creates[0]
    assert world.venv_intact()
    assert world.leftover_worktrees() == []
    wt = world.premerge_calls[0][0]
    registered = _git(world.clone, "worktree", "list", "--porcelain").replace("\\", "/").lower()
    assert wt.as_posix().lower() not in registered  # this run's own worktree is unregistered, not just empty
    assert not wt.exists()


def test_green_landing_records_the_pr_for_the_merge_hook(world):
    import premerge_stamp

    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    landed = json.loads(premerge_stamp.landed_path(world.clone).read_text(encoding="utf-8"))
    assert list(landed.values())[0]["head"] == sha


def test_premerge_runs_inside_the_worktree_with_the_venv_junction_live_and_fast_by_default(world):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert world.seen_in_premerge == [True]
    wt, full = world.premerge_calls[0]
    assert full is False and wt.parent == world.tmp_root
    world.premerge_calls.clear()
    assert _land(world, "feat/1-x", sha, full=True, land_branch="land/1") == lw.EXIT_OK
    assert world.premerge_calls[0][1] is True
    assert "land/1" in world.remote_branches()


def test_the_pushed_head_includes_origin_main_when_the_branch_is_behind(world):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    world.advance_main({"a.txt": b"two\n"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    pushed = _git(world.origin, "rev-parse", "refs/heads/feat/1-x")
    assert pushed != sha
    assert _git(world.origin, "merge-base", "--is-ancestor", "refs/heads/main", pushed) == ""
    assert _git(world.origin, "show", "feat/1-x:a.txt") == "two"


# --- refusals -----------------------------------------------------------------------------------------


def test_a_sha_that_is_not_the_branch_tip_is_refused_before_anything_is_created(world):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    stale = _git(world.clone, "rev-parse", "origin/main")
    assert _land(world, "feat/1-x", stale) == lw.EXIT_REFUSED
    assert world.remote_branches() == ["main"]
    assert not world.premerge_calls and not world.tmp_root.exists()
    assert sha != stale


def test_an_unknown_branch_or_sha_is_refused(world):
    assert _land(world, "feat/nope", "0" * 40) == lw.EXIT_REFUSED
    assert world.remote_branches() == ["main"]


def test_a_control_byte_in_a_changed_text_file_is_refused_before_a_worktree_exists(world):
    sha = world.commit_on_branch("feat/1-x", {"scripts/run.ps1": b"cd scripts\x0bverify\n"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_REFUSED
    assert world.remote_branches() == ["main"]
    assert not world.premerge_calls and not world.tmp_root.exists()


def test_a_binary_file_with_nul_bytes_is_not_a_control_byte_finding(world):
    sha = world.commit_on_branch("feat/1-x", {"logo.png": b"\x89PNG\r\n\x1a\n\x00\x00\x0b\x01"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK


def test_find_control_bytes_names_the_file_and_tolerates_tab_lf_cr():
    assert lw.find_control_bytes({"ok.txt": b"a\tb\r\nc\n", "bad.txt": b"a\x0bb", "nul.txt": b"\x00"}) == ["bad.txt", "nul.txt"]


def test_red_premerge_refuses_pushes_nothing_and_leaves_the_real_venv_alone(world):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    world.premerge_result[0] = 1
    assert _land(world, "feat/1-x", sha) == lw.EXIT_RED
    assert world.remote_branches() == ["main"]
    assert world.gh_calls() == []
    assert world.venv_intact()
    assert world.leftover_worktrees() == []


# --- merging origin/main ------------------------------------------------------------------------------


def test_a_totest_only_conflict_is_union_resolved_and_both_rows_survive(world):
    sha = world.commit_on_branch("feat/1-x", {"docs/ToTest.md": b"| header |\n| row0 |\n| lane row |\n"})
    world.advance_main({"docs/ToTest.md": b"| header |\n| row0 |\n| main row |\n"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    merged = _git(world.origin, "show", "feat/1-x:docs/ToTest.md")
    assert "| lane row |" in merged and "| main row |" in merged and "<<<<" not in merged
    assert _git(world.origin, "rev-list", "--parents", "-n", "1", "feat/1-x").count(" ") == 2  # a merge commit
    assert world.venv_intact() and world.leftover_worktrees() == []


def test_any_other_conflict_refuses_names_the_files_and_pushes_nothing(world, capsys):
    sha = world.commit_on_branch("feat/1-x", {"a.txt": b"lane\n", "docs/ToTest.md": b"| header |\n| row0 |\n| lane |\n"})
    world.advance_main({"a.txt": b"main\n", "docs/ToTest.md": b"| header |\n| row0 |\n| main |\n"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_REFUSED
    out = capsys.readouterr().out
    assert "a.txt" in out and "ToTest" not in out.split("a.txt")[0].split("conflict")[-1]
    assert world.remote_branches() == ["main"]
    assert not world.premerge_calls
    assert world.venv_intact() and world.leftover_worktrees() == []


def test_split_unmerged_names_totest_apart_from_the_rest():
    assert lw.split_conflicts(["docs/ToTest.md", "a.txt", "b/c.cs"]) == (["docs/ToTest.md"], ["a.txt", "b/c.cs"])


# --- the junction ordering and the real-venv safety -----------------------------------------------------


def test_the_junction_is_gone_at_the_moment_git_worktree_remove_runs(world, monkeypatch):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    seen: list[bool] = []
    real_git = lw.git

    def spy(cwd, *args, **kw):
        if args[:2] == ("worktree", "remove"):
            wt = Path(args[-1])
            seen.append(os.path.lexists(wt / "worker" / ".venv"))
        return real_git(cwd, *args, **kw)

    monkeypatch.setattr(lw, "git", spy)
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert seen == [False]


def test_the_junction_is_gone_before_worktree_remove_on_a_failed_run_too(world, monkeypatch):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    world.premerge_result[0] = 1
    seen: list[bool] = []
    real_git = lw.git

    def spy(cwd, *args, **kw):
        if args[:2] == ("worktree", "remove"):
            seen.append(os.path.lexists(Path(args[-1]) / "worker" / ".venv"))
        return real_git(cwd, *args, **kw)

    monkeypatch.setattr(lw, "git", spy)
    assert _land(world, "feat/1-x", sha) == lw.EXIT_RED
    assert seen == [False] and world.venv_intact()


def test_unlink_dir_link_removes_the_link_only_and_never_recurses(tmp_path):
    target = tmp_path / "real"
    (target / "sub").mkdir(parents=True)
    (target / "sub" / "keep.txt").write_bytes(b"x")
    link = tmp_path / "link"
    lw.make_dir_link(link, target)
    assert (link / "sub" / "keep.txt").is_file()
    lw.unlink_dir_link(link)
    assert not os.path.lexists(link)
    assert (target / "sub" / "keep.txt").is_file()


def test_unlink_dir_link_refuses_a_real_directory(tmp_path):
    real = tmp_path / "plain"
    real.mkdir()
    (real / "f.txt").write_bytes(b"x")
    with pytest.raises(lw.LandError):
        lw.unlink_dir_link(real)
    assert (real / "f.txt").is_file()


def test_if_the_junction_cannot_be_removed_the_worktree_is_left_in_place(world, monkeypatch):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    world.premerge_result[0] = 1

    def boom(link):
        raise lw.LandError("cannot unlink")

    monkeypatch.setattr(lw, "unlink_dir_link", boom)
    assert _land(world, "feat/1-x", sha) == lw.EXIT_RED  # a cleanup failure never rewrites the real outcome
    assert world.venv_intact()
    assert len(world.leftover_worktrees()) == 1  # kept on purpose: removing it could reach the venv


def test_a_worktree_left_by_a_killed_run_is_cleaned_junction_first(world):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    stale = world.tmp_root / "feat-1-x-20200101000000"
    world.tmp_root.mkdir()
    _git(world.clone, "worktree", "add", "-q", "--detach", str(stale), sha)
    lw.make_dir_link(stale / "worker" / ".venv", world.venv)
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert world.venv_intact() and not stale.exists()


# --- cli ------------------------------------------------------------------------------------------------


def test_cli_requires_the_core_arguments():
    proc = subprocess.run([sys.executable, str(SCRIPTS_DIR / "land_wt.py")], capture_output=True, text=True, check=False)
    assert proc.returncode == lw.EXIT_USAGE


# --- round 2 ---------------------------------------------------------------------------------------


def _dead_pid() -> int:
    proc = subprocess.Popen([sys.executable, "-c", "pass"])
    proc.wait()
    return proc.pid


def _write_lock(world: World, name: str, pid: int) -> None:
    locks = world.tmp_root / "locks"
    locks.mkdir(parents=True, exist_ok=True)
    (locks / f"{name}.json").write_text(json.dumps({"pid": pid, "start": time.time()}), encoding="utf-8", newline="\n")


def _make_run_dir(world: World, name: str, sha: str) -> Path:
    path = world.tmp_root / name
    world.tmp_root.mkdir(parents=True, exist_ok=True)
    _git(world.clone, "worktree", "add", "-q", "--detach", str(path), sha)
    lw.make_dir_link(path / "worker" / ".venv", world.venv)
    return path


def test_a_sibling_branchs_live_worktree_is_not_swept_by_a_prefix_match(world):
    sha = world.commit_on_branch("feat/1", {"b.txt": b"new\n"})
    sibling = _make_run_dir(world, "feat-1-x-20200101000000", sha)  # slug of feat/1-x, prefix-matches feat-1
    assert _land(world, "feat/1", sha) == lw.EXIT_OK
    assert sibling.is_dir() and (sibling / "worker" / ".venv" / "marker.txt").is_file()


def test_a_live_same_branch_run_is_not_swept_and_a_dead_one_is(world):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    live = _make_run_dir(world, "feat-1-x-20200101000000", sha)
    _write_lock(world, live.name, os.getpid())
    dead = _make_run_dir(world, "feat-1-x-20200102000000", sha)
    _write_lock(world, dead.name, _dead_pid())
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert live.is_dir() and not dead.exists()
    assert world.venv_intact()


def test_an_unregistered_leftover_dir_does_not_block_and_its_junction_is_unlinked_not_followed(world):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    stale = world.tmp_root / "feat-1-x-20200101000000"
    (stale / "sub").mkdir(parents=True)
    (stale / "sub" / "f.txt").write_bytes(b"x")
    lw.make_dir_link(stale / "worker" / ".venv", world.venv)
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert not stale.exists()
    assert world.venv_intact()


def test_remove_tree_no_follow_never_enters_a_junction(tmp_path):
    real = tmp_path / "real"
    real.mkdir()
    (real / "keep.txt").write_bytes(b"k")
    tree = tmp_path / "tree"
    (tree / "d").mkdir(parents=True)
    ro = tree / "d" / "ro.txt"
    ro.write_bytes(b"r")
    os.chmod(ro, 0o444)
    lw.make_dir_link(tree / "d" / "link", real)
    lw.remove_tree_no_follow(tree)
    assert not tree.exists()
    assert (real / "keep.txt").is_file()


def test_a_cleanup_failure_after_a_green_run_is_a_warning_and_exit_zero(world, monkeypatch, capsys):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    real_git = lw.git

    def failing(cwd, *args, **kw):
        if args[:2] == ("worktree", "remove"):
            raise lw.LandError("remove blew up")
        return real_git(cwd, *args, **kw)

    monkeypatch.setattr(lw, "git", failing)
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    out = capsys.readouterr().out
    leftover = world.premerge_calls[0][0]
    assert "WARNING" in out and str(leftover) in out and "OK:" in out
    assert "feat/1-x" in world.remote_branches()
    assert world.venv_intact()


@pytest.mark.parametrize("bad", ["main", "master", "HEAD", "refs/heads/x", "refs/tags/v1", "-x", ""])
@pytest.mark.parametrize("which", ["branch", "land_branch"])
def test_protected_or_malformed_branch_names_are_refused_before_any_work(world, which, bad):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    branch, land_branch = ("feat/1-x", bad) if which == "land_branch" else (bad, None)
    assert _land(world, branch, sha, land_branch=land_branch) == lw.EXIT_REFUSED
    assert world.remote_branches() == ["main"] and not world.tmp_root.exists() and not world.premerge_calls


def test_a_merge_that_fails_without_conflicts_reports_its_real_stderr(world, monkeypatch, capsys):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    world.advance_main({"a.txt": b"two\n"})
    real_git = lw.git

    def spy(cwd, *args, **kw):
        if "merge" in args and "--no-edit" in args:
            return subprocess.CompletedProcess(args, 1, b"", b"boom: cannot merge")
        return real_git(cwd, *args, **kw)

    monkeypatch.setattr(lw, "git", spy)
    assert _land(world, "feat/1-x", sha) == lw.EXIT_REFUSED
    out = capsys.readouterr().out
    assert "merge failed: boom: cannot merge" in out and "conflict" not in out
    assert world.remote_branches() == ["main"] and world.leftover_worktrees() == []


def test_the_merge_commit_gets_an_identity_even_with_no_git_identity_configured(world, monkeypatch, tmp_path):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    world.advance_main({"a.txt": b"two\n"})
    _git(world.clone, "config", "--unset", "user.name")
    _git(world.clone, "config", "--unset", "user.email")
    empty = tmp_path / "global.cfg"
    empty.write_text("[user]\n\tuseConfigOnly = true\n", encoding="utf-8", newline="\n")
    monkeypatch.setenv("GIT_CONFIG_GLOBAL", str(empty))
    monkeypatch.setenv("GIT_CONFIG_NOSYSTEM", "1")
    for var in ("GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "EMAIL"):
        monkeypatch.delenv(var, raising=False)
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert _git(world.origin, "show", "feat/1-x:a.txt") == "two"


def test_a_missing_gh_after_the_push_exits_4_and_names_the_pushed_branch(world, capsys):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    ctx = world.ctx()
    ctx.gh = ["gh-that-does-not-exist-510"]
    body = world.root / "body.md"
    body.write_text("b\n", encoding="utf-8", newline="\n")
    assert lw.land(ctx, "feat/1-x", sha, "t", body) == lw.EXIT_FAILED
    assert "feat/1-x" in world.remote_branches()
    assert "pushed" in capsys.readouterr().out and "feat/1-x" in "".join(ctx.log)
    assert world.venv_intact() and world.leftover_worktrees() == []


def test_an_unexpected_error_after_the_push_is_exit_4_not_1(world, capsys):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    ctx = world.ctx()
    ctx.gh = 5  # not iterable: raises TypeError once the push is done
    body = world.root / "body.md"
    body.write_text("b\n", encoding="utf-8", newline="\n")
    assert lw.land(ctx, "feat/1-x", sha, "t", body) == lw.EXIT_FAILED
    assert "feat/1-x" in world.remote_branches()
    assert "feat/1-x" in capsys.readouterr().out


def test_make_dir_link_turns_an_os_error_into_a_land_error(tmp_path):
    blocker = tmp_path / "file"
    blocker.write_bytes(b"x")
    with pytest.raises(lw.LandError):
        lw.make_dir_link(blocker / "x" / ".venv", tmp_path)


def _commit_with_name(world: World, name: str, data: bytes) -> str:
    _git(world.clone, "switch", "-q", "-c", "feat/odd", "origin/main")
    blob_file = world.root / "blob.bin"
    blob_file.write_bytes(data)
    blob = _git(world.clone, "hash-object", "-w", str(blob_file))
    _git(world.clone, "-c", "core.protectNTFS=false", "update-index", "--add", "--cacheinfo", f"100644,{blob},{name}")
    _git(world.clone, "commit", "-q", "-m", "odd name")
    sha = _git(world.clone, "rev-parse", "HEAD")
    _git(world.clone, "update-index", "--force-remove", "--", name)
    _git(world.clone, "switch", "-q", "--detach", "origin/main")
    return sha


def test_a_file_name_with_space_quote_and_tab_is_read_exactly_and_scanned(world):
    name = 'we ird"n\tame.txt'
    sha = _commit_with_name(world, name, b"a\x0bb")
    assert lw._changed_text_files(world.ctx(), sha) == {name: b"a\x0bb"}
    assert _land(world, "feat/odd", sha) == lw.EXIT_REFUSED
    assert world.remote_branches() == ["main"]


def test_esc_is_allowed_because_logs_carry_colour_but_vt_and_ff_are_not():
    assert lw.find_control_bytes({"log.txt": b"\x1b[31mred\x1b[0m\n"}) == []
    assert lw.find_control_bytes({"a": b"\x0b", "b": b"\x0c"}) == ["a", "b"]


def test_gh_is_taken_as_one_executable_path_even_with_spaces(world, monkeypatch):
    seen = []
    monkeypatch.setattr(lw, "land", lambda ctx, *a, **k: seen.append(ctx.gh) or 0)
    body = world.root / "body.md"
    body.write_text("b\n", encoding="utf-8", newline="\n")
    gh = r"C:\Program Files\GitHub CLI\gh.exe"
    argv = ["--branch", "feat/1-x", "--sha", "abc", "--title", "t", "--body-file", str(body), "--venv", str(world.venv), "--gh", gh]
    assert lw.main(argv, repo=world.clone) == 0
    assert seen == [[gh]]


def test_worktree_remove_is_never_called_while_the_venv_link_is_still_present(world, monkeypatch):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    removes: list[Path] = []
    real_git = lw.git

    def spy(cwd, *args, **kw):
        if args[:2] == ("worktree", "remove"):
            removes.append(Path(args[-1]))
        return real_git(cwd, *args, **kw)

    monkeypatch.setattr(lw, "git", spy)
    monkeypatch.setattr(lw, "unlink_dir_link", lambda link: None)  # the unlink silently does nothing
    _land(world, "feat/1-x", sha)
    assert world.seen_in_premerge == [True]  # the link existed, so the guard below is not vacuous
    assert removes == []
    assert world.venv_intact()


# --- --merge-when-green (#666) ----------------------------------------------------------------------


class Clock:
    """A fake clock: sleep() advances it, so a timeout costs no wall time and no assertion reads a real clock."""

    def __init__(self) -> None:
        self.now = 0.0
        self.sleeps: list[float] = []

    def monotonic(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.sleeps.append(seconds)
        self.now += seconds


def _check(name: str, bucket: str, run: int | None = None) -> dict[str, str]:
    link = f"https://github.com/o/r/actions/runs/{run}/job/9" if run else ""
    return {"name": name, "bucket": bucket, "state": bucket.upper(), "link": link}


def _land_green(
    world: World, replies: list, *, timeout: float = 100.0, poll: float = 10.0, merge_rc: int = 0, rerun: bool = False, rerun_rc: int = 0
) -> tuple[int, Clock]:
    if rerun_rc:
        (world.root / "gh.log.rerunrc").write_text(str(rerun_rc), encoding="utf-8")
    (world.root / "gh.log.checks").write_text(json.dumps(replies), encoding="utf-8", newline="\n")
    if merge_rc:
        (world.root / "gh.log.mergerc").write_text(str(merge_rc), encoding="utf-8")
    sha = world.commit_on_branch("feat/66-x", {"b.txt": b"new\n"})
    body = world.root / "body.md"
    body.write_text("body\n", encoding="utf-8", newline="\n")
    ctx = world.ctx()
    clock = Clock()
    ctx.sleep, ctx.monotonic = clock.sleep, clock.monotonic
    rc = lw.land(
        ctx, "feat/66-x", sha, "feat: t", body, merge_when_green=True, checks_timeout=timeout, checks_poll=poll, rerun_failed_once=rerun
    )
    return rc, clock


def _calls(world: World, verb: str) -> list[list[str]]:
    return [c for c in world.gh_calls() if c[:2] == ["pr", verb]]


def test_merge_when_green_merges_with_a_merge_commit_once_every_check_passes(world, capsys):
    rc, _ = _land_green(world, [[_check("scripts tests", "pass"), _check("codeql", "skipping")]])
    assert rc == lw.EXIT_OK
    merges = _calls(world, "merge")
    assert len(merges) == 1
    assert "--merge" in merges[0] and merges[0][2].endswith("/pull/7")
    assert "--squash" not in merges[0] and "--rebase" not in merges[0]
    assert "merged https://github.com/o/r/pull/7" in capsys.readouterr().out


def test_merge_when_green_refuses_and_names_every_failed_check(world, capsys):
    rc, _ = _land_green(world, [[_check("scripts tests", "fail"), _check("lint", "cancel"), _check("fine-one", "pass")]])
    assert rc == lw.EXIT_CHECKS_RED
    assert _calls(world, "merge") == []
    out = capsys.readouterr().out
    assert "scripts tests" in out and "lint" in out and "fine-one" not in out
    assert out.isascii()
    assert "https://github.com/o/r/pull/7" in out  # the PR stays open and is named


def test_a_failure_is_reported_at_once_while_other_checks_are_still_pending(world):
    rc, clock = _land_green(world, [[_check("slow", "pending"), _check("bad", "fail")]])
    assert rc == lw.EXIT_CHECKS_RED
    assert clock.sleeps == []
    assert _calls(world, "merge") == []


def test_merge_when_green_waits_through_pending_then_merges(world):
    pending = [_check("a", "pass"), _check("b", "pending")]
    rc, clock = _land_green(world, [pending, pending, [_check("a", "pass"), _check("b", "pass")]], poll=7.0)
    assert rc == lw.EXIT_OK
    assert clock.sleeps == [7.0, 7.0]
    assert len(_calls(world, "checks")) == 3
    assert len(_calls(world, "merge")) == 1


def test_a_check_still_pending_at_the_timeout_refuses_and_never_merges(world, capsys):
    rc, clock = _land_green(world, [[_check("a", "pass"), _check("slow", "pending")]], timeout=30.0, poll=10.0)
    assert rc == lw.EXIT_CHECKS_PENDING
    assert _calls(world, "merge") == []
    assert clock.now >= 30.0
    out = capsys.readouterr().out
    assert "slow" in out and out.isascii()


def test_no_checks_reported_is_pending_not_green(world):
    # gh prints "no checks reported" (exit 1, not JSON) right after the PR opens
    rc, _ = _land_green(world, ["no checks reported on the 'feat/66-x' branch"], timeout=20.0, poll=10.0)
    assert rc == lw.EXIT_CHECKS_PENDING
    assert _calls(world, "merge") == []


def test_an_empty_check_list_never_merges(world):
    rc, _ = _land_green(world, [[]], timeout=20.0, poll=10.0)
    assert rc == lw.EXIT_CHECKS_PENDING
    assert _calls(world, "merge") == []


def test_a_failed_merge_call_is_reported_as_a_failure(world, capsys):
    rc, _ = _land_green(world, [[_check("a", "pass")]], merge_rc=1)
    assert rc == lw.EXIT_FAILED
    assert "merge" in capsys.readouterr().out


def test_without_the_flag_gh_pr_checks_is_never_called(world):
    sha = world.commit_on_branch("feat/66-y", {"b.txt": b"new\n"})
    assert _land(world, "feat/66-y", sha) == lw.EXIT_OK
    assert _calls(world, "checks") == [] and _calls(world, "merge") == []


def test_cli_flags_reach_land(world, monkeypatch):
    seen = {}
    monkeypatch.setattr(lw, "land", lambda ctx, *a, **k: seen.update(k) or 0)
    body = world.root / "body.md"
    body.write_text("b\n", encoding="utf-8", newline="\n")
    base = ["--branch", "feat/1-x", "--sha", "abc", "--title", "t", "--body-file", str(body), "--venv", str(world.venv)]
    assert lw.main([*base, "--merge-when-green", "--checks-timeout", "90", "--checks-poll", "3"], repo=world.clone) == 0
    assert (seen["merge_when_green"], seen["checks_timeout"], seen["checks_poll"]) == (True, 90.0, 3.0)
    seen.clear()
    assert lw.main(base, repo=world.clone) == 0
    assert seen["merge_when_green"] is False


# --- --rerun-failed-once (on by default with --merge-when-green) -------------------------------------


def _flaky_log(world: World) -> list[str]:
    path = world.clone / ".git" / "flaky-checks.log"
    return path.read_text(encoding="utf-8").splitlines() if path.exists() else []


def test_a_red_check_is_rerun_once_and_the_pr_merges_when_the_rerun_is_green(world):
    red = [_check("scripts tests", "fail", 111), _check("lint", "pass", 222)]
    green = [_check("scripts tests", "pass", 111), _check("lint", "pass", 222)]
    rc, clock = _land_green(world, [red, green], rerun=True, poll=5.0)
    assert rc == lw.EXIT_OK
    reruns = [c for c in world.gh_calls() if c[:2] == ["run", "rerun"]]
    assert reruns == [["run", "rerun", "111", "--failed"]]  # only the failed run, once
    assert len(_calls(world, "merge")) == 1
    assert clock.sleeps == [5.0]  # one poll lets the rerun replace the stale red
    (line,) = _flaky_log(world)
    assert "pr=https://github.com/o/r/pull/7" in line and "checks=scripts tests" in line and "runs=111" in line


def test_each_failed_run_is_rerun_exactly_once_even_when_it_stays_red(world, capsys):
    red = [_check("a", "fail", 111), _check("b", "fail", 222), _check("c", "fail", 111)]
    rc, _ = _land_green(world, [red], rerun=True)
    assert rc == lw.EXIT_CHECKS_RED
    reruns = [c for c in world.gh_calls() if c[:2] == ["run", "rerun"]]
    assert reruns == [["run", "rerun", "111", "--failed"], ["run", "rerun", "222", "--failed"]]
    assert _calls(world, "merge") == []
    out = capsys.readouterr().out
    assert "still red after one re-run" in out and out.isascii()
    assert len(_flaky_log(world)) == 1


def test_the_rerun_waits_for_the_other_checks_to_finish_first(world):
    # gh refuses `run rerun --failed` while a run is in progress, so a red beside a pending check keeps polling
    mixed = [_check("bad", "fail", 111), _check("slow", "pending", 222)]
    settled = [_check("bad", "fail", 111), _check("slow", "pass", 222)]
    rc, _ = _land_green(world, [mixed, settled, [_check("bad", "pass", 111), _check("slow", "pass", 222)]], rerun=True)
    assert rc == lw.EXIT_OK
    assert [c for c in world.gh_calls() if c[:2] == ["run", "rerun"]] == [["run", "rerun", "111", "--failed"]]
    assert len(_calls(world, "checks")) == 3


def test_a_red_check_with_no_run_link_cannot_be_rerun_and_refuses(world):
    rc, _ = _land_green(world, [[_check("bad", "fail")]], rerun=True)
    assert rc == lw.EXIT_CHECKS_RED
    assert [c for c in world.gh_calls() if c[:2] == ["run", "rerun"]] == []
    assert _calls(world, "merge") == []


def test_a_rerun_that_gh_rejects_refuses_without_waiting(world, capsys):
    rc, _ = _land_green(world, [[_check("bad", "fail", 111)]], rerun=True, rerun_rc=1)
    assert rc == lw.EXIT_CHECKS_RED
    assert _calls(world, "merge") == []
    assert "could not be re-run" in capsys.readouterr().out


def test_rerun_can_be_switched_off(world):
    rc, _ = _land_green(world, [[_check("bad", "fail", 111)]], rerun=False)
    assert rc == lw.EXIT_CHECKS_RED
    assert [c for c in world.gh_calls() if c[:2] == ["run", "rerun"]] == []
    assert _flaky_log(world) == []


def test_rerun_flag_defaults_on_and_can_be_negated(world, monkeypatch):
    seen = {}
    monkeypatch.setattr(lw, "land", lambda ctx, *a, **k: seen.update(k) or 0)
    body = world.root / "body.md"
    body.write_text("b\n", encoding="utf-8", newline="\n")
    base = ["--branch", "feat/1-x", "--sha", "abc", "--title", "t", "--body-file", str(body), "--venv", str(world.venv), "--merge-when-green"]
    assert lw.main(base, repo=world.clone) == 0
    assert seen["rerun_failed_once"] is True
    assert lw.main([*base, "--no-rerun-failed-once"], repo=world.clone) == 0
    assert seen["rerun_failed_once"] is False


def test_the_run_lock_records_the_commit_so_a_killed_run_leftover_can_be_judged_later(world):
    """scripts/prune_worktrees.py (#794) decides whether an unreadable leftover is safe to delete from this sha."""
    sha = world.commit_on_branch("feat/794-x", {"b.txt": b"new\n"})
    ctx = world.ctx()
    seen: list[dict] = []

    def premerge(wt: Path, venv: Path, full: bool) -> tuple[int, str]:
        seen.append(json.loads((ctx.tmp_root / "locks" / f"{wt.name}.json").read_text(encoding="utf-8")))
        return 0, "PREMERGE: PASS\n"

    ctx.premerge = premerge
    body = world.root / "body.md"
    body.write_text("body\n", encoding="utf-8", newline="\n")
    assert lw.land(ctx, "feat/794-x", sha, "feat: x", body) == lw.EXIT_OK
    assert seen[0]["sha"] == sha and seen[0]["pid"] == os.getpid()


def test_a_failing_record_landed_only_warns_and_the_landing_still_succeeds(world, monkeypatch, capsys):
    import premerge_stamp

    def boom(*_a, **_k):
        raise PermissionError("held open")

    monkeypatch.setattr(premerge_stamp, "record_landed", boom)
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert "WARNING: could not record the landed PR" in capsys.readouterr().out
