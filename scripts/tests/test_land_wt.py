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
from dataclasses import dataclass
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import land_wt as lw  # noqa: E402

GH_STUB = """\
import json, sys
from pathlib import Path
Path(sys.argv[1]).open("a", encoding="utf-8", newline="\\n").write(json.dumps(sys.argv[2:]) + "\\n")
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
        return [p.name for p in self.tmp_root.iterdir() if p.name != "logs"] if self.tmp_root.exists() else []


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
    assert "worktree" not in _git(world.clone, "worktree", "list").split("\n", 1)[-1]


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
    assert _land(world, "feat/1-x", sha) == lw.EXIT_FAILED
    assert world.venv_intact()
    assert len(world.leftover_worktrees()) == 1  # kept on purpose: removing it could reach the venv


def test_a_worktree_left_by_a_killed_run_is_cleaned_junction_first(world):
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    stale = world.tmp_root / "feat-1-x-000000"
    world.tmp_root.mkdir()
    _git(world.clone, "worktree", "add", "-q", "--detach", str(stale), sha)
    lw.make_dir_link(stale / "worker" / ".venv", world.venv)
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert world.venv_intact() and not stale.exists()


# --- cli ------------------------------------------------------------------------------------------------


def test_cli_requires_the_core_arguments():
    proc = subprocess.run([sys.executable, str(SCRIPTS_DIR / "land_wt.py")], capture_output=True, text=True, check=False)
    assert proc.returncode == lw.EXIT_USAGE
