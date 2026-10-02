"""Tests for scripts/land_pr.py (#445).

Every repository here is a throwaway bare remote plus clone under tmp_path, and `gh` is a stub
script. The real checkout and GitHub are never touched.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import land_pr as lp  # noqa: E402


@pytest.fixture()
def world(tmp_path):
    return lp.make_world(tmp_path)


def _land(world, branch="fix/x", paths=(), hunks=None, checks="pass", **kwargs):
    kwargs.setdefault("allow_no_checks", False)
    kwargs.setdefault("dry_run", False)
    kwargs.setdefault("grace", 0.0)  # tests never wait for real; the grace tests below use a fake clock
    return lp.land(world.ctx(checks), branch, list(paths), hunks or {}, "fix: the thing", None,
                   poll=0.0, timeout=kwargs.pop("timeout", 30.0), **kwargs)


def _status(world) -> str:
    return world.g("status", "--porcelain")


def _merge_called(world) -> bool:
    return any(call[:2] == ["pr", "merge"] for call in world.calls())


def test_self_test_passes():
    proc = subprocess.run([sys.executable, str(SCRIPTS_DIR / "land_pr.py"), "--self-test"],
                          capture_output=True, text=True, timeout=120, check=False)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


# --- the observable from the issue ---------------------------------------------------------------


def test_one_file_landing_leaves_an_unrelated_modified_file_untouched_and_adds_one_merge(world):
    (world.work / "other.txt").write_bytes(b"changed for the lane\n")
    (world.work / "unrelated.txt").write_bytes(b"another lane is editing this\n")
    (world.work / "brand_new_from_other_lane.txt").write_bytes(b"new\n")
    assert _land(world, paths=["other.txt"]) == lp.EXIT_OK
    assert (world.work / "unrelated.txt").read_bytes() == b"another lane is editing this\n"
    assert (world.work / "brand_new_from_other_lane.txt").exists()
    assert sorted(line.split()[-1] for line in _status(world).splitlines()) == ["brand_new_from_other_lane.txt", "unrelated.txt"]
    assert world.g("rev-list", "--merges", "--count", "main") == "1"
    assert world.g("show", "origin/main:other.txt") == "changed for the lane"
    assert world.g("branch", "--show-current") == "main"


def test_a_new_untracked_file_and_a_deletion_can_be_landed(world):
    (world.work / "added.txt").write_bytes(b"added\n")
    (world.work / "other.txt").unlink()
    assert _land(world, paths=["added.txt", "other.txt"]) == lp.EXIT_OK
    assert world.g("show", "origin/main:added.txt") == "added"
    assert world.g("ls-tree", "--name-only", "origin/main", "other.txt") == ""


def test_two_issues_in_one_file_land_one_hunk_at_a_time_and_main_return_works(world):
    lp.edit_two_hunks(world)
    original = (world.work / "f.txt").read_bytes()
    assert _land(world, "fix/a", hunks={"f.txt": [0]}) == lp.EXIT_OK
    assert (world.work / "f.txt").read_bytes() == original  # both edits still in the working tree
    # The trap this tool exists for: a plain `git switch main` is refused here, `checkout -B` is not.
    assert world.g("branch", "--show-current") == "main"
    assert _land(world, "fix/b", hunks={"f.txt": [0]}) == lp.EXIT_OK  # hunk B is hunk 0 of what remains
    assert _status(world) == ""
    merged = world.g("show", "origin/main:f.txt")
    assert "issue A" in merged and "issue B" in merged and "§" in merged


def test_non_ascii_context_survives_hunk_staging_byte_for_byte(world):
    lp.edit_two_hunks(world)
    ctx = world.ctx()
    lp.stage_hunks(ctx, "f.txt", [1])
    staged = lp.git(ctx, "diff", "--cached").stdout
    assert "§".encode() in staged and "Ł".encode() in staged  # U+0141 has an undefined byte in cp1252
    assert b"issue B" in staged and b"issue A" not in staged


def test_list_hunks_prints_index_header_and_first_change(world):
    lp.edit_two_hunks(world)
    rows = lp.list_hunks(world.ctx(), "f.txt")
    assert [r[0] for r in rows] == [0, 1]
    assert rows[0][1].startswith("@@") and "line 03" in rows[0][2]
    proc = subprocess.run([sys.executable, str(SCRIPTS_DIR / "land_pr.py"), "--repo", str(world.work), "--list-hunks", "f.txt"],
                          capture_output=True, text=True, timeout=60, check=False)
    assert proc.returncode == 0 and "line 36" in proc.stdout


# --- refusals change nothing ----------------------------------------------------------------------


def _assert_untouched(world, head_before):
    assert world.g("rev-parse", "HEAD") == head_before
    assert world.g("branch", "--show-current") == "main"
    assert world.g("branch", "--list", "fix/x") == ""


@pytest.mark.parametrize("scenario", ["directory", "unchanged", "staged_foreign", "bad_hunk", "untracked_hunk", "both", "empty"])
def test_preflight_refusals(world, scenario, capsys):
    (world.work / "sub").mkdir()
    (world.work / "sub" / "new.txt").write_bytes(b"x\n")
    (world.work / "other.txt").write_bytes(b"edited\n")
    head = world.g("rev-parse", "HEAD")
    paths, hunks = ["other.txt"], {}
    if scenario == "directory":
        paths = ["sub"]
    elif scenario == "unchanged":
        paths = ["unrelated.txt"]
    elif scenario == "staged_foreign":
        world.g("add", "sub/new.txt")
    elif scenario == "bad_hunk":
        paths, hunks = [], {"other.txt": [5]}
    elif scenario == "untracked_hunk":
        paths, hunks = [], {"sub/new.txt": [0]}
    elif scenario == "both":
        hunks = {"other.txt": [0]}
    elif scenario == "empty":
        paths = []
    assert _land(world, paths=paths, hunks=hunks) == lp.EXIT_REFUSED
    assert "REFUSED" in capsys.readouterr().err
    _assert_untouched(world, head)
    assert not world.calls()  # gh was never reached


def test_existing_branch_name_is_refused(world):
    world.g("branch", "fix/x")
    (world.work / "other.txt").write_bytes(b"edited\n")
    assert _land(world, paths=["other.txt"]) == lp.EXIT_REFUSED


def test_dry_run_changes_nothing(world, capsys):
    (world.work / "other.txt").write_bytes(b"edited\n")
    head = world.g("rev-parse", "HEAD")
    assert _land(world, paths=["other.txt"], dry_run=True) == lp.EXIT_OK
    _assert_untouched(world, head)
    assert not world.calls()
    assert "dry run" in capsys.readouterr().out


# --- checks -----------------------------------------------------------------------------------------


def test_a_red_check_stops_on_the_branch_and_names_it_without_merging(world, capsys):
    (world.work / "other.txt").write_bytes(b"edited\n")
    assert _land(world, paths=["other.txt"], checks="fail") == lp.EXIT_RED
    assert "ci-docs" in capsys.readouterr().err
    assert not _merge_called(world)
    assert world.g("branch", "--show-current") == "fix/x"


def test_no_checks_is_refused_by_default_and_allowed_on_request(world, capsys):
    (world.work / "other.txt").write_bytes(b"edited\n")
    assert _land(world, "fix/n1", paths=["other.txt"], checks="none") == lp.EXIT_RED
    assert "no checks" in capsys.readouterr().err
    assert not _merge_called(world)
    world.g("checkout", "-q", "main")
    (world.work / "other.txt").write_bytes(b"edited again\n")
    assert _land(world, "fix/n2", paths=["other.txt"], checks="none", allow_no_checks=True) == lp.EXIT_OK
    assert _merge_called(world)


class FakeTime:
    """A clock whose sleep advances it, so a 120 s grace period costs no wall time."""

    def __init__(self):
        self.now = 0.0
        self.sleeps: list[float] = []

    def clock(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.sleeps.append(seconds)
        self.now += seconds


def _fake_ctx(world, checks):
    ft = FakeTime()
    ctx = world.ctx(checks)
    ctx.sleep, ctx.clock = ft.sleep, ft.clock
    return ctx, ft


def test_checks_that_appear_on_the_third_poll_are_waited_for_and_land(world):
    (world.work / "other.txt").write_bytes(b"edited\n")
    ctx, ft = _fake_ctx(world, "appear_third")
    code = lp.land(ctx, "fix/g", ["other.txt"], {}, "fix: grace", None, False, False, 5.0, 1800.0, grace=120.0)
    assert code == lp.EXIT_OK
    polls = [c for c in world.calls() if c[:2] == ["pr", "checks"]]
    assert len(polls) == 3
    assert _merge_called(world)
    assert ft.now < 120.0  # stopped waiting as soon as the first check showed up


def test_no_checks_after_the_grace_period_reports_how_long_it_waited(world, capsys):
    (world.work / "other.txt").write_bytes(b"edited\n")
    ctx, ft = _fake_ctx(world, "none")
    code = lp.land(ctx, "fix/g2", ["other.txt"], {}, "fix: grace", None, False, False, 5.0, 1800.0, grace=120.0)
    assert code == lp.EXIT_RED
    err = capsys.readouterr().err
    assert "after waiting 120 s" in err
    assert ft.now >= 120.0
    assert not _merge_called(world)


def test_allow_no_checks_still_waits_out_the_grace_period_first(world):
    (world.work / "other.txt").write_bytes(b"edited\n")
    ctx, ft = _fake_ctx(world, "appear_third")
    code = lp.land(ctx, "fix/g3", ["other.txt"], {}, "fix: grace", None, True, False, 5.0, 1800.0, grace=120.0)
    assert code == lp.EXIT_OK
    assert len([c for c in world.calls() if c[:2] == ["pr", "checks"]]) == 3  # did not merge on poll 1


def test_pending_checks_are_polled_until_green(world):
    (world.work / "other.txt").write_bytes(b"edited\n")
    assert _land(world, paths=["other.txt"], checks="pending_then_pass") == lp.EXIT_OK
    polls = [c for c in world.calls() if c[:2] == ["pr", "checks"]]
    assert len(polls) >= 3


def test_timeout_while_pending_stops_without_merging(world):
    (world.work / "other.txt").write_bytes(b"edited\n")
    assert _land(world, paths=["other.txt"], checks="pending_then_pass", timeout=-1.0) == lp.EXIT_TIMEOUT
    assert not _merge_called(world)


def test_local_main_ahead_of_origin_is_not_orphaned(world, capsys):
    (world.work / "other.txt").write_bytes(b"edited\n")
    # A commit on local main that origin lacks: moving main to origin/main would orphan it.
    # land_pr cuts its branch from HEAD, so that commit rides into the PR; make the remote differ instead.
    other_clone = world.root / "second"
    subprocess.run(["git", "clone", "-q", str(world.remote), str(other_clone)], check=True, capture_output=True)
    for key, value in (("user.name", "O"), ("user.email", "o@example.com")):
        subprocess.run(["git", "config", key, value], cwd=other_clone, check=True, capture_output=True)
    (other_clone / "x.txt").write_bytes(b"x\n")
    subprocess.run(["git", "add", "-A"], cwd=other_clone, check=True, capture_output=True)
    subprocess.run(["git", "commit", "-q", "-m", "someone else"], cwd=other_clone, check=True, capture_output=True)
    subprocess.run(["git", "push", "-q", "origin", "main"], cwd=other_clone, check=True, capture_output=True)
    assert _land(world, paths=["other.txt"]) == lp.EXIT_OK  # an origin that moved ahead is fine
    assert world.g("rev-parse", "main") == world.g("rev-parse", "origin/main")
    assert (world.work / "x.txt").exists()


# --- bytes, not text --------------------------------------------------------------------------------


def test_cli_refuses_without_branch_and_title(world):
    proc = subprocess.run([sys.executable, str(SCRIPTS_DIR / "land_pr.py"), "--repo", str(world.work), "--paths", "other.txt"],
                          capture_output=True, text=True, timeout=60, check=False)
    assert proc.returncode == 2


def test_cli_end_to_end_with_the_stub_gh(world):
    (world.work / "other.txt").write_bytes(b"cli edit\n")
    env = world.env()
    body = world.root / "body.md"
    body.write_text("evidence goes here\n", encoding="utf-8", newline="\n")
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "land_pr.py"), "--repo", str(world.work), "--branch", "fix/cli", "--no-premerge-check",
         "--paths", "other.txt", "--title", "fix: cli", "--body-file", str(body), "--poll-seconds", "0",
         "--gh", f"{Path(sys.executable).as_posix()} {world.stub.as_posix()}"],
        capture_output=True, text=True, timeout=120, check=False, env=env,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "LANDED" in proc.stdout
    log = world.g("log", "-1", "--format=%B", "origin/main^2")
    assert "fix: cli" in log and "evidence goes here" in log
    create = next(c for c in world.calls() if c[:2] == ["pr", "create"])
    assert "--body-file" in create


def test_stub_state_file_is_valid_json(world):
    (world.work / "other.txt").write_bytes(b"e\n")
    _land(world, paths=["other.txt"])
    assert isinstance(json.loads(world.state.read_text(encoding="utf-8"))["calls"], list)


def test_land_refuses_without_a_premerge_stamp_and_accepts_one(world, capsys):
    (world.work / "other.txt").write_bytes(b"edited\n")
    head = world.g("rev-parse", "HEAD")
    assert _land(world, paths=["other.txt"], require_premerge=True) == lp.EXIT_REFUSED
    assert "premerge" in capsys.readouterr().err
    _assert_untouched(world, head)
    lp.premerge_stamp.write_stamp(world.work, "fast")
    assert _land(world, paths=["other.txt"], require_premerge=True) == lp.EXIT_OK
