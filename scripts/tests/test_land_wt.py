"""Tests for scripts/land_wt.py (#510).

Every repository is a throwaway bare "origin" plus a clone under tmp_path, the venv is a fake
directory with marker files, `gh` is a stub script and premerge is an injected callable. GitHub and
the real venv are never touched.
"""

from __future__ import annotations

import contextlib
import importlib
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
hang = Path(str(log) + ".hang")
if hang.exists() and " ".join(sys.argv[2:4]) in hang.read_text(encoding="utf-8").split(","):
    import time
    time.sleep(60)
if sys.argv[2:4] == ["pr", "view"]:
    import subprocess
    failing = Path(str(log) + ".viewfail")  # the first N `pr view` calls exit 1 with no output
    if failing.exists():
        seen = sum(1 for ln in log.read_text(encoding="utf-8").splitlines() if json.loads(ln)[:2] == ["pr", "view"])
        if seen <= int(failing.read_text()):
            sys.stderr.write("HTTP 502")
            sys.exit(1)
    fixed = Path(str(log) + ".headoid")
    oid = fixed.read_text(encoding="utf-8").strip() if fixed.exists() else ""
    if not oid:
        head = ""
        for ln in log.read_text(encoding="utf-8").splitlines():
            call = json.loads(ln)
            if call[:2] == ["pr", "create"]:
                head = call[call.index("--head") + 1]
        origin = Path(str(log) + ".origin").read_text(encoding="utf-8").strip()
        out = subprocess.run(["git", "ls-remote", origin, "refs/heads/" + head], capture_output=True, text=True).stdout.split()
        oid = out[0] if out else ""
    print(json.dumps({} if oid == "NONE" else {"headRefOid": oid}))
    sys.exit(0)
if sys.argv[2:4] == ["pr", "checks"]:
    # scripted replies: the Nth `pr checks` call gets replies[N] (the last one repeats); a string is raw stdout
    replies = json.loads(Path(str(log) + ".checks").read_text(encoding="utf-8"))
    seen = sum(1 for ln in log.read_text(encoding="utf-8").splitlines() if '"checks"' in ln) - 1
    reply = replies[min(seen, len(replies) - 1)]
    if isinstance(reply, dict):  # {"exit": N, "stderr": "..."}: gh itself failed (no JSON on stdout)
        sys.stderr.write(reply.get("stderr", ""))
        sys.exit(reply["exit"])
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
        Path(str(self.gh_log) + ".origin").write_text(str(self.origin), encoding="utf-8")

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
    landed_file = premerge_stamp.landed_path(world.clone)
    assert landed_file is not None
    landed = json.loads(landed_file.read_text(encoding="utf-8"))
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


# --- ToTest.md normalisation after the union (#892) ------------------------------------------------------

TT_HEAD = (
    "## Queue\n\n| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |\n"
    "| --- | --- | --- | --- | --- | --- |\n"
)


def _tt(issue: int, do: str = "do a", sha: str = "abc1234", needs: str = "gpu-vm", close: bool = True) -> str:
    return f"| #{issue} | {sha} | {needs} | {do} | p | f |" + ("\n" if close else "")


def _land_totest(world, lane: str, main: str) -> list[str]:
    """Land a lane and main that both append to the Queue table; return the landed ToTest.md lines."""
    world.advance_main({"docs/ToTest.md": TT_HEAD.encode()})
    sha = world.commit_on_branch("feat/1-x", {"docs/ToTest.md": (TT_HEAD + lane).encode()})
    world.advance_main({"docs/ToTest.md": (TT_HEAD + main).encode()})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    return _git(world.origin, "show", "feat/1-x:docs/ToTest.md").splitlines()


def test_a_row_both_sides_carry_is_kept_once_and_mains_copy_wins(world):
    # a stacked chain whose bottom just landed: the lane has #91 and #92, main has #5 and the same #91
    lines = _land_totest(world, _tt(91) + _tt(92, "do b"), _tt(5, "do x") + _tt(91))
    rows = [ln for ln in lines if ln.startswith("| #") and ln[3].isdigit()]
    assert rows == [_tt(92, "do b").strip(), _tt(5, "do x").strip(), _tt(91).strip()], rows


def test_a_row_with_the_same_key_but_a_different_cell_is_kept_and_named_for_the_lane_to_reconcile(world, capsys):
    import check_totest_format as ctf

    lane = _tt(91, "do a", sha="def5678") + _tt(92, "do b")
    lines = _land_totest(world, lane, _tt(5, "do x") + _tt(91, "do a"))
    out = capsys.readouterr().out
    assert _tt(91, "do a", sha="def5678").strip() in lines and _tt(91, "do a").strip() in lines  # the lane's edit survives
    assert "#91" in out and "Closed by (commit sha)" in out and "reconcile" in out
    assert len(ctf.find_duplicate_rows(ctf.parse_rows("\n".join(lines) + "\n"))) == 1  # premerge will refuse it


def test_a_lane_only_duplicate_is_left_and_named_not_collapsed(world, capsys):
    lines = _land_totest(world, _tt(93, "do c") + _tt(93, "do c"), _tt(5, "do x"))
    out = capsys.readouterr().out
    assert [ln for ln in lines if ln.startswith("| #93")] == [_tt(93, "do c").strip()] * 2
    assert "#93" in out and "reconcile" in out


def test_identical_copies_of_a_row_main_carries_collapse_to_mains_copy(world):
    lines = _land_totest(world, _tt(91) + _tt(91) + _tt(92, "do b"), _tt(5, "do x") + _tt(91))
    assert [ln for ln in lines if ln.startswith("| #91")] == [_tt(91).strip()]


def test_mains_copy_wins_even_when_main_lacks_the_closing_pipe():
    text = TT_HEAD + _tt(91, sha="def5678") + _tt(5, "do x") + _tt(91)
    out, dropped, _closed, conflicts = lw.normalize_totest(text, TT_HEAD + _tt(5, "do x") + _tt(91).rstrip(" |\n") + "\n")
    assert dropped == 0 and conflicts  # different sha cell: not identical, so nothing is dropped
    text = TT_HEAD + _tt(91) + _tt(5, "do x") + _tt(91)
    out, dropped, _closed, conflicts = lw.normalize_totest(text, TT_HEAD + _tt(5, "do x") + _tt(91).rstrip(" |\n") + "\n")
    assert dropped == 1 and not conflicts
    assert out == TT_HEAD + _tt(5, "do x") + _tt(91)


def test_a_row_missing_its_closing_pipe_is_closed(world):
    lines = _land_totest(world, _tt(93, "do c").rstrip("\n |") + "\n" + _tt(94, "do d"), _tt(5, "do x"))
    assert _tt(93, "do c").strip() in lines
    assert all(ln.rstrip().endswith("|") for ln in lines if ln.startswith("|"))


def test_a_genuinely_distinct_row_is_kept(world):
    # same issue, different "Do this"; and another issue with the same "Do this": neither is a duplicate
    lines = _land_totest(world, _tt(91, "do b") + _tt(7, "do a"), _tt(91, "do a"))
    rows = [ln for ln in lines if ln.startswith("| #") and ln[3].isdigit()]
    assert sorted(rows) == sorted([_tt(91, "do b").strip(), _tt(7, "do a").strip(), _tt(91, "do a").strip()])


def test_normalize_totest_touches_only_queue_table_rows():
    tail = "prose | with a pipe\n## Other\n| #1 | x | y\n"
    text = "intro | not a row\n" + TT_HEAD + _tt(1) + _tt(1) + _tt(2).rstrip(" |\n") + "\n" + tail
    out, dropped, closed, conflicts = lw.normalize_totest(text, TT_HEAD + _tt(1))
    assert (dropped, closed, conflicts) == (1, 1, [])
    assert out == "intro | not a row\n" + TT_HEAD + _tt(1) + _tt(2) + tail  # lines outside the table are byte-identical
    out, dropped, closed, _ = lw.normalize_totest(TT_HEAD + _tt(2).rstrip(" |\n") + "\r\n", "")
    assert (dropped, closed) == (0, 1) and out.endswith(" |\r\n")


def test_a_row_ending_in_a_unicode_line_break_does_not_glue_the_next_line():
    row = _tt(2).rstrip(" |\n")
    text = TT_HEAD + row + "\x0c" + _tt(3)
    out, _dropped, closed, _ = lw.normalize_totest(text, "")
    assert closed == 1 and out == TT_HEAD + row + " |\x0c" + _tt(3)


# --- the sha recorded for the merge hook is the one pushed (#904) ----------------------------------------


def test_a_lane_behind_main_records_the_pushed_merge_commit_not_the_lane_tip(world):
    import premerge_stamp

    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    world.advance_main({"a.txt": b"two\n"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    pushed = _git(world.origin, "rev-parse", "refs/heads/feat/1-x")
    assert pushed != sha
    landed_file = premerge_stamp.landed_path(world.clone)
    assert landed_file is not None
    assert list(json.loads(landed_file.read_text(encoding="utf-8")).values())[0]["head"] == pushed
    sys.path.insert(0, str(SCRIPTS_DIR / "hooks"))
    hook = importlib.import_module("require_premerge_before_pr")

    assert hook.verdict(f"gh pr merge 7 --merge --match-head-commit {pushed}", world.clone) is None
    assert hook.verdict(f"gh pr merge 7 --merge --match-head-commit {sha}", world.clone) is not None


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
    world: World,
    replies: list,
    *,
    timeout: float = 100.0,
    poll: float = 10.0,
    merge_rc: int = 0,
    rerun: bool = False,
    rerun_rc: int = 0,
    gh_timeout: float | None = None,
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
    if gh_timeout is not None:
        ctx.gh_timeout = gh_timeout
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


# MEASURED 2026-10-16: PR #811 had every check green, yet land_wt logged "no checks were reported after 1800 s". The same log's
# worktree cleanup failed with 3221225794 (0xC0000142, a process that could not start), so gh itself was failing on every poll and
# the failure was read as "no checks yet".
GH_DLL_FAIL = {"exit": 66, "stderr": ""}  # the real code was 3221225794, which does not fit a portable exit status


def test_gh_failing_on_every_poll_is_not_reported_as_no_checks(world, capsys):
    rc, _ = _land_green(world, [GH_DLL_FAIL], timeout=30.0, poll=10.0)
    out = capsys.readouterr().out
    assert rc == lw.EXIT_CHECKS_PENDING and _calls(world, "merge") == []
    assert "gh could not read the checks" in out and "rc=66" in out
    assert "no checks were reported" not in out


def test_the_gh_error_text_is_shown(world, capsys):
    _land_green(world, [{"exit": 1, "stderr": "HTTP 502: Bad Gateway"}], timeout=20.0, poll=10.0)
    assert "HTTP 502" in capsys.readouterr().out


def test_a_gh_failure_is_warned_about_once_not_every_poll(world, capsys):
    _land_green(world, [GH_DLL_FAIL], timeout=50.0, poll=10.0)
    assert capsys.readouterr().out.count("WARNING: gh pr checks failed") == 1


def test_a_transient_gh_failure_still_merges_once_checks_are_green(world):
    rc, _ = _land_green(world, [GH_DLL_FAIL, [_check("a", "pass"), _check("dependency review", "skipping")]])
    assert rc == lw.EXIT_OK
    assert len(_calls(world, "merge")) == 1


def test_all_pass_plus_skipping_merges(world):
    rc, _ = _land_green(world, [[_check("a", "pass"), _check("dependency review", "skipping")]])
    assert rc == lw.EXIT_OK and len(_calls(world, "merge")) == 1


def test_genuine_no_checks_reported_keeps_its_own_message(world, capsys):
    _land_green(world, ["no checks reported on the 'feat/66-x' branch"], timeout=20.0, poll=10.0)
    out = capsys.readouterr().out
    assert "no checks were reported" in out and "gh could not read" not in out


# --- round 6: every subprocess is bounded, and a timeout kills the whole tree ---------------------------------------


def _pid_gone(pid: int, wait: float = 10.0) -> bool:
    end = time.time() + wait
    while time.time() < end:
        if not lw.pid_alive(pid):
            return True
        time.sleep(0.1)
    return False


GRANDCHILD = """\
import subprocess, sys, time
from pathlib import Path
gc = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
Path(sys.argv[1]).write_text(str(gc.pid))
time.sleep(60)
"""


def test_run_bounded_kills_the_whole_tree_on_timeout(tmp_path):
    pidfile = tmp_path / "gc.pid"
    script = tmp_path / "parent.py"
    script.write_text(GRANDCHILD, encoding="utf-8", newline="\n")
    start = time.time()
    with pytest.raises(subprocess.TimeoutExpired):
        lw.run_bounded([sys.executable, str(script), str(pidfile)], timeout=2.0)
    assert time.time() - start < 15  # the grandchild holds the pipes open: only a tree kill returns promptly
    assert _pid_gone(int(pidfile.read_text()))


def test_run_bounded_returns_output_and_stdin_like_run():
    proc = lw.run_bounded([sys.executable, "-c", "import sys; sys.stdout.write(sys.stdin.read()); sys.exit(3)"], stdin=b"hi", timeout=30)
    assert proc.returncode == 3 and proc.stdout == b"hi"


def test_git_has_a_timeout_and_names_it(tmp_path):
    hang = f"alias.zz=!{Path(sys.executable).as_posix()} -c 'import time; time.sleep(60)'"
    with pytest.raises(lw.LandError, match="timed out"):
        lw.git(tmp_path, "-c", hang, "zz", timeout=1.0)


def test_a_hung_gh_pr_checks_is_a_read_failure_not_a_hang(world):
    (world.root / "gh.log.hang").write_text("pr checks", encoding="utf-8")
    ctx = world.ctx()
    ctx.gh_timeout = 1.0
    start = time.time()
    assert lw._read_checks(ctx, "7") is None
    assert time.time() - start < 15
    assert "timed out" in ctx.checks_error


def test_hung_check_reads_still_reach_the_deadline(world, capsys):
    (world.root / "gh.log.hang").write_text("pr checks", encoding="utf-8")
    ctx = world.ctx()
    ctx.gh_timeout = 0.5
    clock = Clock()
    ctx.sleep, ctx.monotonic = clock.sleep, clock.monotonic
    assert lw._merge_when_green(ctx, "https://github.com/o/r/pull/7", "a" * 40, 30.0, 10.0) == lw.EXIT_CHECKS_PENDING
    assert "timed out" in capsys.readouterr().out
    assert _calls(world, "merge") == []


def test_a_hung_gh_pr_merge_is_a_failure_that_names_the_pr(world, capsys):
    (world.root / "gh.log.hang").write_text("pr merge", encoding="utf-8")
    rc, _ = _land_green(world, [[_check("a", "pass")]], gh_timeout=1.0)
    assert rc == lw.EXIT_FAILED
    assert "timed out" in capsys.readouterr().out


def test_a_hung_gh_pr_create_is_a_failure(world):
    (world.root / "gh.log.hang").write_text("pr create", encoding="utf-8")
    ctx = world.ctx()
    ctx.gh_timeout = 1.0
    with pytest.raises(lw.LandError, match="timed out"):
        lw._create_pr(ctx, world.clone, "feat/x", "t", world.root / "body.md")


def test_a_hung_gh_run_rerun_is_not_a_successful_rerun(world):
    (world.root / "gh.log.hang").write_text("run rerun", encoding="utf-8")
    ctx = world.ctx()
    ctx.gh_timeout = 1.0
    assert lw._rerun_failed(ctx, "7", [_check("a", "fail", run=5)]) is False


def test_proc_start_time_is_stable_for_a_live_pid_and_none_for_a_dead_one():
    first = lw.proc_start_time(os.getpid())
    assert isinstance(first, float) and first > 0
    assert lw.proc_start_time(os.getpid()) == first
    assert lw.proc_start_time(_dead_pid()) is None


# --- round 7 ---------------------------------------------------------------------------------------------------------


def _origin_sha(world: World, branch: str) -> str:
    return _git(world.origin, "rev-parse", f"refs/heads/{branch}")


def test_the_merge_is_pinned_to_the_head_that_was_pushed(world):
    rc, _ = _land_green(world, [[_check("a", "pass")]])
    assert rc == lw.EXIT_OK
    merge = _calls(world, "merge")[0]
    assert merge[merge.index("--match-head-commit") + 1] == _origin_sha(world, "feat/66-x")


def test_a_pr_head_that_moved_since_the_push_is_never_merged(world, capsys):
    (world.root / "gh.log.headoid").write_text("f" * 40, encoding="utf-8")
    rc, _ = _land_green(world, [[_check("a", "pass")]])
    assert rc == lw.EXIT_CHECKS_RED
    assert _calls(world, "merge") == []
    out = capsys.readouterr().out
    assert "head" in out and "changed" in out and "https://github.com/o/r/pull/7" in out and out.isascii()


def test_an_unreadable_pr_head_is_never_merged_blind(world):
    (world.root / "gh.log.hang").write_text("pr view", encoding="utf-8")
    rc, _ = _land_green(world, [[_check("a", "pass")]], gh_timeout=1.0)
    assert rc == lw.EXIT_CHECKS_PENDING
    assert _calls(world, "merge") == []


def test_a_pr_view_with_no_head_is_never_merged_blind(world):
    (world.root / "gh.log.headoid").write_text("NONE", encoding="utf-8")  # the stub prints {}
    rc, _ = _land_green(world, [[_check("a", "pass")]])
    assert rc == lw.EXIT_CHECKS_PENDING
    assert _calls(world, "merge") == []


def test_premerge_is_bounded_and_a_timeout_is_a_red_result(tmp_path, monkeypatch):
    seen = {}

    def fake(argv, **kw):
        seen.update(kw)
        raise subprocess.TimeoutExpired(argv, kw["timeout"], b"", b"")

    monkeypatch.setattr(lw, "run_bounded", fake)
    code, out = lw.run_premerge(tmp_path, tmp_path, False, timeout=42.0)
    assert seen["timeout"] == 42.0
    assert code == 124 and "ERROR" in out and "timed out after 42 s" in out
    lw.run_premerge(tmp_path, tmp_path, False)
    assert seen["timeout"] == lw.PREMERGE_TIMEOUT_SECONDS


def test_the_premerge_timeout_flag_reaches_the_real_premerge(world, monkeypatch):
    seen = []
    monkeypatch.setattr(lw, "land", lambda ctx, *a, **k: seen.append(ctx.premerge) or 0)
    body = world.root / "body.md"
    body.write_text("b\n", encoding="utf-8", newline="\n")
    args = ["--branch", "feat/1-x", "--sha", "a" * 40, "--title", "t", "--body-file", str(body)]
    assert lw.main([*args, "--premerge-timeout", "5"], repo=world.clone) == 0
    assert lw.main(args, repo=world.clone) == 0
    assert seen[0].keywords["timeout"] == 5.0
    assert seen[1].keywords["timeout"] == lw.PREMERGE_TIMEOUT_SECONDS


def _hook(path: Path, body: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(f"#!/bin/sh\n{body}\n".encode())
    path.chmod(0o755)  # git skips a hook that is not executable (Windows ignores the mode bit; Linux CI does not)


def _land_with_push_timeout(world, branch):
    sha = world.commit_on_branch(branch, {"b.txt": b"new\n"})
    body = world.root / "body.md"
    body.write_text("body\n", encoding="utf-8", newline="\n")
    ctx = world.ctx()
    ctx.git_timeout = 6.0
    return lw.land(ctx, branch, sha, "feat: t", body), ctx


def test_a_push_that_timed_out_but_reached_origin_carries_on(world, capsys):
    _hook(world.origin / "hooks" / "post-receive", "sleep 25")  # the ref is updated, then the client waits on the hook
    rc, _ = _land_with_push_timeout(world, "feat/77-x")
    out = capsys.readouterr().out
    assert rc == lw.EXIT_OK, out
    assert "push timed out" in out and "origin already has" in out
    assert "feat/77-x" in world.remote_branches()
    assert _calls(world, "create")


def test_a_push_that_timed_out_and_did_not_arrive_says_so(world, capsys):
    _hook(world.clone / ".git" / "hooks" / "pre-push", "sleep 25")
    rc, _ = _land_with_push_timeout(world, "feat/77-y")
    out = capsys.readouterr().out
    assert rc == lw.EXIT_FAILED
    assert "push timed out" in out and "not on origin" in out
    assert "feat/77-y" not in world.remote_branches()
    assert not _calls(world, "create")


def test_a_push_timeout_with_an_unanswerable_ls_remote_names_the_branch(world, capsys, monkeypatch):
    real = lw.git

    def spy(cwd, *args, **kw):
        if args[:1] in (("push",), ("ls-remote",)):
            raise lw.LandTimeout("git timed out after 1 s", lw.EXIT_FAILED)
        return real(cwd, *args, **kw)

    monkeypatch.setattr(lw, "git", spy)
    sha = world.commit_on_branch("feat/77-z", {"b.txt": b"new\n"})
    body = world.root / "body.md"
    body.write_text("body\n", encoding="utf-8", newline="\n")
    rc = lw.land(world.ctx(), "feat/77-z", sha, "feat: t", body)
    assert rc == lw.EXIT_FAILED
    assert "branch may exist on origin: feat/77-z" in capsys.readouterr().out


def test_a_lock_left_in_the_throwaway_worktree_goes_with_the_worktree(world):
    sha = world.commit_on_branch("feat/78-x", {"b.txt": b"new\n"})
    wt = world.tmp_root / "feat-78-x-20200101000001"
    wt.parent.mkdir(parents=True, exist_ok=True)
    _git(world.clone, "worktree", "add", "-q", "--detach", str(wt), sha)
    gitdir = Path(_git(wt, "rev-parse", "--absolute-git-dir"))
    (gitdir / "index.lock").write_bytes(b"")  # what a killed git can leave behind
    lw._remove_worktree(world.ctx(), wt)
    assert not wt.exists() and not gitdir.exists()


@pytest.mark.skipif(os.name != "nt", reason="ctypes prototypes are a Windows concern")
def test_the_win32_calls_have_pointer_safe_prototypes():
    import ctypes

    k32 = lw._kernel32()
    assert k32.OpenProcess.restype is ctypes.c_void_p
    assert k32.CloseHandle.argtypes == [ctypes.c_void_p]
    assert k32.GetProcessTimes.argtypes[0] is ctypes.c_void_p
    assert k32.GetExitCodeProcess.argtypes[0] is ctypes.c_void_p
    assert lw.pid_alive(os.getpid()) is True


def test_kill_pid_tree_ends_a_process_and_its_child_and_reports_it(tmp_path):
    pidfile = tmp_path / "gc.pid"
    script = tmp_path / "parent.py"
    script.write_text(GRANDCHILD, encoding="utf-8", newline="\n")
    proc = subprocess.Popen([sys.executable, str(script), str(pidfile)])
    try:
        end = time.time() + 10
        while not pidfile.exists() and time.time() < end:
            time.sleep(0.1)
        assert lw.kill_pid_tree(proc.pid) is True
        assert _pid_gone(int(pidfile.read_text()))
        assert not lw.pid_alive(proc.pid)
    finally:
        proc.kill()
        proc.wait()


FORKER = """import subprocess, sys, time
end = time.time() + 20
while time.time() < end:
    subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
"""

POSIX_ONLY = pytest.mark.skipif(os.name == "nt", reason="the POSIX /proc walk; Windows uses taskkill /T")


def _session_members(sid: int) -> list[int]:
    """Live (non-zombie) pids in session `sid`, from /proc itself rather than anything a process reports about itself."""
    found = []
    for entry in os.listdir("/proc"):
        if entry.isdigit():
            try:
                rest = Path(f"/proc/{entry}/stat").read_text(errors="replace").rpartition(")")[2].split()
            except OSError:
                continue
            if int(rest[3]) == sid and rest[0] not in ("Z", "X", "x"):
                found.append(int(entry))
    return found


@POSIX_ONLY
def test_kill_pid_tree_leaves_no_child_forked_while_it_was_scanning(tmp_path):
    script = tmp_path / "forker.py"
    script.write_text(FORKER, encoding="utf-8", newline="\n")
    proc = subprocess.Popen([sys.executable, str(script)], start_new_session=True)  # pid == sid: every descendant shares it
    try:
        end = time.time() + 10
        while len(_session_members(proc.pid)) < 4 and time.time() < end:
            time.sleep(0.05)
        assert len(_session_members(proc.pid)) >= 4
        assert lw.kill_pid_tree(proc.pid) is True
        proc.wait()
        end = time.time() + 5
        while _session_members(proc.pid) and time.time() < end:
            time.sleep(0.1)
        assert _session_members(proc.pid) == []
    finally:
        proc.kill()
        proc.wait()


@POSIX_ONLY
def test_a_posix_kill_skips_a_pid_whose_start_time_changed():
    victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    try:
        start = lw._proc_ident(victim.pid)
        assert isinstance(start, int)
        assert lw._kill_if_same(victim.pid, start + 1) is False  # a reused pid has a different start time
        assert victim.poll() is None
        assert lw._kill_if_same(victim.pid, start) is True
        victim.wait(timeout=10)
    finally:
        victim.kill()
        victim.wait()


ORPHANER = """import subprocess, sys, time
from pathlib import Path
gc = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
Path(sys.argv[1]).write_text(str(gc.pid))
while not Path(sys.argv[2]).exists():
    time.sleep(0.05)
"""


@POSIX_ONLY
def test_a_posix_kill_still_reaches_a_victim_whose_parent_died_after_the_scan(tmp_path):
    pidfile, gofile = tmp_path / "gc.pid", tmp_path / "go"
    script = tmp_path / "orphaner.py"
    script.write_text(ORPHANER, encoding="utf-8", newline="\n")
    parent = subprocess.Popen([sys.executable, str(script), str(pidfile), str(gofile)])
    gc = 0
    try:
        end = time.time() + 10
        while not (pidfile.exists() and pidfile.read_text()) and time.time() < end:
            time.sleep(0.05)
        gc = int(pidfile.read_text())
        start = lw._proc_ident(gc)  # recorded while the parent is alive, like a scan
        assert start is not None
        gofile.write_text("x")
        parent.wait(timeout=10)  # the parent dies after the scan: gc is reparented to init
        fields = lw._proc_fields(gc)
        assert fields is not None
        assert fields[1] != parent.pid
        assert lw._kill_if_same(gc, start) is True  # identity is pid + start time only, never the ppid
        assert _pid_gone(gc)
    finally:
        parent.kill()
        parent.wait()
        if gc:
            with contextlib.suppress(OSError):
                os.kill(gc, 9)


@POSIX_ONLY
def test_a_posix_kill_freezes_the_root_before_it_scans_for_descendants(monkeypatch):
    victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    states = []
    real = lw._proc_table

    def spy():
        fields = lw._proc_fields(victim.pid)
        states.append(fields[0] if fields else None)
        return real()

    monkeypatch.setattr(lw, "_proc_table", spy)
    try:
        assert lw.kill_pid_tree(victim.pid) is True
        assert states and set(states) == {"T"}  # stopped (and the stop confirmed) at EVERY scan, so nothing can fork unseen
    finally:
        victim.kill()
        victim.wait()


@POSIX_ONLY
def test_a_posix_kill_waits_for_a_late_stop_before_trusting_a_scan(monkeypatch):
    import threading

    victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    real = lw._kill_if_same
    states = []

    timers = []

    def late_stop(pid, start, sig=None):  # SIGSTOP "returns" at once but only lands 0.15 s later
        if pid == victim.pid and sig == lw.signal.SIGSTOP:
            timer = threading.Timer(0.15, lambda: real(pid, start, sig))  # identity-checked, so never a recycled pid
            timer.daemon = True
            timers.append(timer)
            timer.start()
            return True
        return real(pid, start, sig)

    real_table = lw._proc_table

    def spy():
        fields = lw._proc_fields(victim.pid)
        states.append(fields[0] if fields else None)
        return real_table()

    monkeypatch.setattr(lw, "_kill_if_same", late_stop)
    monkeypatch.setattr(lw, "_proc_table", spy)
    try:
        assert lw.kill_pid_tree(victim.pid) is True
        assert states and set(states) == {"T"}  # the scan waited for the stop to land
    finally:
        for timer in timers:
            timer.cancel()
        victim.kill()
        victim.wait()


@POSIX_ONLY
def test_a_posix_kill_without_proc_still_kills_the_root(monkeypatch):
    victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    real_isdir = os.path.isdir
    monkeypatch.setattr(lw, "_proc_ident", lambda pid: None)
    monkeypatch.setattr(lw.os.path, "isdir", lambda p: False if p == "/proc" else real_isdir(p))  # what macOS looks like
    try:
        assert lw.kill_pid_tree(victim.pid, wait=3.0) is True
        assert victim.wait(timeout=10) != 0
    finally:
        victim.kill()
        victim.wait()


@POSIX_ONLY
def test_a_posix_kill_with_proc_but_an_unreadable_root_does_not_blind_kill(monkeypatch):
    victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    monkeypatch.setattr(lw, "_proc_ident", lambda pid: None)  # /proc exists: None means gone (or a transient parse failure)
    try:
        assert lw.kill_pid_tree(victim.pid, wait=0.5) is False
        assert victim.poll() is None
    finally:
        victim.kill()
        victim.wait()


@POSIX_ONLY
def test_a_posix_kill_interrupted_right_after_the_stop_does_not_leave_it_frozen(monkeypatch):
    victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    real = lw._kill_if_same

    def stop_then_interrupt(pid, start, sig=None):
        result = real(pid, start, sig)
        if pid == victim.pid and sig == lw.signal.SIGSTOP:
            raise KeyboardInterrupt  # between the signal and the caller's bookkeeping
        return result

    monkeypatch.setattr(lw, "_kill_if_same", stop_then_interrupt)
    try:
        with pytest.raises(KeyboardInterrupt):
            lw.kill_pid_tree(victim.pid)
        monkeypatch.undo()
        assert victim.wait(timeout=10) != 0  # killed by the finally (it was registered before the stop), not left in state T
    finally:
        victim.kill()
        victim.wait()


@POSIX_ONLY
def test_a_posix_kill_never_signals_a_process_group_or_everything(monkeypatch):
    sent = []
    monkeypatch.setattr(lw.os, "kill", lambda pid, sig: sent.append((pid, sig)))  # a real os.kill(0/-1) would be a disaster
    for bad in (0, 1, -1):
        assert lw.kill_pid_tree(bad, wait=0.1) is False
    assert sent == []


@POSIX_ONLY
def test_a_posix_kill_leaves_a_recycled_root_alone(monkeypatch):
    victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    real = lw._proc_ident
    calls = []

    def ident(pid):  # the first read (the caller's record of who the root was) predates a pid recycle
        calls.append(pid)
        value = real(pid)
        return value + 1 if len(calls) == 1 and value is not None else value

    monkeypatch.setattr(lw, "_proc_ident", ident)
    try:
        assert lw.kill_pid_tree(victim.pid, wait=0.5) is False
        assert victim.poll() is None  # a different process than the one recorded: never signalled
    finally:
        victim.kill()
        victim.wait()


@POSIX_ONLY
def test_a_posix_kill_whose_root_stop_failed_still_kills_the_descendants(tmp_path, monkeypatch):
    pidfile = tmp_path / "gc.pid"
    script = tmp_path / "parent.py"
    script.write_text(GRANDCHILD, encoding="utf-8", newline="\n")
    proc = subprocess.Popen([sys.executable, str(script), str(pidfile)])
    real = lw._kill_if_same

    def fake(pid, start, sig=None):
        return None if (pid == proc.pid and sig == lw.signal.SIGSTOP) else real(pid, start, sig)  # None: the stop raised OSError

    monkeypatch.setattr(lw, "_kill_if_same", fake)
    try:
        end = time.time() + 10
        while not pidfile.exists() and time.time() < end:
            time.sleep(0.1)
        assert lw.kill_pid_tree(proc.pid) is True
        assert _pid_gone(int(pidfile.read_text()))
    finally:
        proc.kill()
        proc.wait()


@POSIX_ONLY
def test_a_posix_kill_that_hits_an_exception_does_not_leave_the_tree_stopped(monkeypatch):
    victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])

    def boom():
        raise RuntimeError("scan failed")

    monkeypatch.setattr(lw, "_proc_table", boom)
    try:
        with pytest.raises(RuntimeError):
            lw.kill_pid_tree(victim.pid)
        victim.wait(timeout=10)  # killed by the finally, not left in state T
    finally:
        victim.kill()
        victim.wait()


def test_pid_alive_treats_a_lowercase_x_state_as_dead(monkeypatch):
    if os.name == "nt":
        pytest.skip("POSIX /proc")
    real = Path.read_text
    monkeypatch.setattr(
        Path, "read_text", lambda self, *a, **k: "1 (a b) x " + " ".join(["0"] * 25) if str(self).startswith("/proc/") else real(self, *a, **k)
    )
    assert lw.pid_alive(os.getpid()) is False


def test_a_transient_pr_view_failure_is_retried_with_backoff_then_merged(world):
    (world.root / "gh.log.viewfail").write_text("2", encoding="utf-8")
    rc, clock = _land_green(world, [[_check("a", "pass")]])
    assert rc == lw.EXIT_OK
    assert len(_calls(world, "view")) == 3 and len(_calls(world, "merge")) == 1
    assert clock.sleeps == [lw.HEAD_READ_BACKOFF_SECONDS, 2 * lw.HEAD_READ_BACKOFF_SECONDS]


def test_a_pr_view_that_keeps_failing_gives_up_after_three_reads_without_merging(world):
    (world.root / "gh.log.viewfail").write_text("99", encoding="utf-8")
    rc, _ = _land_green(world, [[_check("a", "pass")]])
    assert rc == lw.EXIT_CHECKS_PENDING
    assert len(_calls(world, "view")) == 3 and _calls(world, "merge") == []


def test_a_failing_record_landed_only_warns_and_the_landing_still_succeeds(world, monkeypatch, capsys):
    import premerge_stamp

    def boom(*_a, **_k):
        raise PermissionError("held open")

    monkeypatch.setattr(premerge_stamp, "record_landed", boom)
    sha = world.commit_on_branch("feat/1-x", {"b.txt": b"new\n"})
    assert _land(world, "feat/1-x", sha) == lw.EXIT_OK
    assert "WARNING: could not record the landed PR" in capsys.readouterr().out
