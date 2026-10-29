"""Tests for scripts/land_queue.py. No network, no git: every external command goes through an injected fake runner."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

import land_queue as lq

SHA_A = "a" * 40
SHA_B = "b" * 40
SHA_C = "c" * 40


class FakeRunner:
    """Scripted `(argv, cwd) -> (rc, out)`. Rules match an argv prefix (with or without the leading executable),
    first match wins; a list answer is consumed one per call and its last element repeats."""

    def __init__(self) -> None:
        self.calls: list[list[str]] = []
        self.rules: list[tuple[tuple[str, ...], object]] = []

    def on(self, *prefix: str, answer: object) -> None:
        self.rules.append((prefix, answer))

    def first(self, *prefix: str, answer: object) -> None:
        self.rules.insert(0, (prefix, answer))

    @staticmethod
    def _hit(argv: list[str], prefix: tuple[str, ...]) -> bool:
        n = len(prefix)
        return tuple(argv[:n]) == prefix or tuple(argv[1 : 1 + n]) == prefix

    def __call__(self, argv: list[str], cwd: Path | None = None) -> tuple[int, str]:
        self.calls.append(list(argv))
        for prefix, answer in self.rules:
            if self._hit(argv, prefix):
                if isinstance(answer, list):
                    return answer[0] if len(answer) == 1 else answer.pop(0)
                return answer  # type: ignore[return-value]
        return 0, ""

    def matching(self, *prefix: str) -> list[list[str]]:
        return [c for c in self.calls if self._hit(c, prefix)]


def checks(*pairs: tuple[str, str]) -> tuple[int, str]:
    return 0, json.dumps(
        [{"name": n, "bucket": b, "link": f"https://github.com/o/r/actions/runs/{i + 100}/job/1"} for i, (n, b) in enumerate(pairs)]
    )


def view(state: str, mergeable: str = "MERGEABLE") -> tuple[int, str]:
    return 0, json.dumps({"mergeStateStatus": state, "mergeable": mergeable})


def runner_of(env: lq.Env) -> FakeRunner:
    assert isinstance(env.runner, FakeRunner)
    return env.runner


@pytest.fixture
def env(tmp_path: Path) -> lq.Env:
    runner = FakeRunner()
    runner.on("git", "ls-remote", answer=(0, ""))
    runner.on("py", answer=(0, "OK: pushed x and opened https://github.com/o/r/pull/7. Merge with: gh pr merge 7 --merge\n"))
    runner.on("gh", "pr", "checks", "7", "--watch", answer=(0, ""))
    runner.on("gh", "pr", "checks", "7", answer=checks(("build and test", "pass")))
    runner.on("gh", "pr", "view", "7", "--json", "headRefOid", answer=(0, json.dumps({"headRefOid": SHA_A})))
    runner.on("gh", "pr", "view", answer=view("CLEAN"))
    return lq.Env(
        repo=tmp_path / "repo", state=tmp_path / "state", python="py", runner=runner, sleep=lambda s: None, now=lambda: "2026-10-15T00:00:00"
    )


def item(branch: str = "feat/1-x", sha: str = SHA_A, full: str = "0", red: str = "") -> lq.Item:
    line = "|".join([branch, sha, "1", full, "feat: x (#1)"] + ([f"red={red}"] if red else []))
    parsed = lq.parse_line(line)
    assert parsed is not None
    return parsed


def parsed(line: str) -> lq.Item:
    got = lq.parse_line(line)
    assert got is not None
    return got


# ---- parsing and keys (defect a) ----


def test_parse_line_fields_and_key() -> None:
    it = parsed("feat/1-x|abc1234|1|1|feat: a | b (#1)")
    assert (it.branch, it.sha, it.issue, it.full, it.title) == ("feat/1-x", "abc1234", "1", True, "feat: a | b (#1)")
    assert it.key == "feat/1-x@abc1234"
    assert it.known_red == ()


def test_parse_line_skips_comments_and_blank() -> None:
    assert lq.parse_line("# note") is None
    assert lq.parse_line("   ") is None
    assert lq.parse_line("only|three|fields") is None


def test_known_red_is_an_optional_sixth_field() -> None:
    it = parsed("b|s|1|0|t (#1)|red=build and test, lint")
    assert it.title == "t (#1)" and it.known_red == ("build and test", "lint")


def test_editing_a_queued_title_does_not_retake_a_landed_branch(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_queue("feat/1-x|abc|1|0|old title")
    st.mark_taken(parsed("feat/1-x|abc|1|0|old title"))
    (env.state / "queue.txt").write_text("feat/1-x|abc|1|0|new title \n", encoding="utf-8")  # edited, trailing space
    assert st.pending() == []


def test_a_new_sha_of_the_same_branch_is_pending() -> None:
    assert lq.pending_items("b|aaa|1|0|t\nb|bbb|1|0|t\n", {"b@aaa"}) == [parsed("b|bbb|1|0|t")]


# ---- landing and merge ----


def test_process_item_merges_when_green_on_the_legacy_path(env: lq.Env) -> None:
    res = lq.process_item(env, item(red="unrelated"))
    assert res == f"MERGED #7 feat/1-x @ {SHA_A}"
    r = runner_of(env)
    assert r.matching("gh", "pr", "merge", "7", "--merge")
    land = r.matching("py")[0]
    assert "--land-branch" not in land and "--full" not in land
    body = (env.state / f"body-feat-1-x-{SHA_A[:7]}.md").read_text(encoding="utf-8")
    assert body.startswith("Closes #1.")


def test_full_flag_is_forwarded(env: lq.Env) -> None:
    lq.process_item(env, item(full="1"))
    assert "--full" in runner_of(env).matching("py")[0]


def test_land_wt_failure_is_recorded_and_nothing_is_merged(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("py", answer=(3, "ERROR: premerge is red"))
    res = lq.process_item(env, item())
    assert res.startswith("FAILED land_wt rc=3 feat/1-x")
    assert not r.matching("gh", "pr", "merge")


# ---- defect b: a rerun of the same branch ----


def test_existing_remote_branch_uses_a_sha_suffixed_land_branch(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("git", "ls-remote", answer=(0, "deadbeef\trefs/heads/feat/1-x\n"))
    lq.process_item(env, item(sha=SHA_B))
    land = r.matching("py")[0]
    assert land[land.index("--land-branch") + 1] == f"feat/1-x-{SHA_B[:7]}"


# ---- merge when green: one rerun, flakes log, known red (defect d) ----


def test_a_red_check_is_rerun_once_and_logged_then_merged(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "checks", "7", "--json", answer=[checks(("build and test", "fail")), checks(("build and test", "pass"))])
    res = lq.process_item(env, item(red="unrelated"))
    assert res.startswith("MERGED #7")
    assert r.matching("gh", "run", "rerun", "100", "--failed")
    assert "PR#7 run 100 rerun after: build and test" in (env.state / "flakes.log").read_text(encoding="utf-8")


def test_red_after_the_rerun_is_refused(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "checks", "7", "--json", answer=checks(("build and test", "fail")))
    assert lq.process_item(env, item(red="unrelated")) == f"REFUSED #7 failing: build and test feat/1-x @ {SHA_A}"
    assert len(r.matching("gh", "run", "rerun")) == 1
    assert not r.matching("gh", "pr", "merge")


def test_known_red_per_item_is_ignored_but_other_checks_are_not(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "checks", "7", "--json", answer=checks(("lint", "fail"), ("build and test", "pass")))
    assert lq.process_item(env, item(red="lint")).startswith("MERGED #7")
    assert not r.matching("gh", "run", "rerun")
    r.first("gh", "pr", "checks", "7", "--json", answer=checks(("lint", "fail"), ("other", "fail")))
    assert lq.process_item(env, item(sha=SHA_B, red="lint")) == f"REFUSED #7 failing: other feat/1-x @ {SHA_B}"


# ---- defect c: a PR made DIRTY by a hand merge ----


def test_a_dirty_pr_is_closed_and_relanded_from_the_same_sha(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "view", answer=[view("DIRTY", "CONFLICTING"), view("CLEAN")])
    r.first("gh", "pr", "view", "7", "--json", "headRefOid", answer=(0, json.dumps({"headRefOid": SHA_A})))
    r.first("gh", "pr", "view", "8", "--json", "headRefOid", answer=(0, json.dumps({"headRefOid": SHA_A})))
    r.first("py", answer=[(0, "opened https://github.com/o/r/pull/7\n"), (0, "opened https://github.com/o/r/pull/8\n")])
    r.on("gh", "pr", "checks", "8", "--watch", answer=(0, ""))
    r.on("gh", "pr", "checks", "8", answer=checks(("build and test", "pass")))
    assert lq.process_item(env, item(red="unrelated")) == f"MERGED #8 feat/1-x @ {SHA_A}"
    assert r.matching("gh", "pr", "close", "7")
    lands = r.matching("py")
    assert len(lands) == 2 and all(SHA_A in c for c in lands)
    assert lands[1][lands[1].index("--land-branch") + 1].endswith("-r2")
    assert not r.matching("gh", "pr", "merge", "7")


def test_unknown_mergeability_is_polled_not_merged_blind(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "view", answer=[view("UNKNOWN", "UNKNOWN"), view("CLEAN")])
    r.first("gh", "pr", "view", "7", "--json", "headRefOid", answer=(0, json.dumps({"headRefOid": SHA_A})))
    assert lq.process_item(env, item(red="unrelated")).startswith("MERGED #7")
    assert len(r.matching("gh", "pr", "view", "7", "--json", "mergeStateStatus,mergeable")) == 2


def test_still_dirty_after_the_reland_is_refused(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "view", answer=view("DIRTY", "CONFLICTING"))
    r.first("gh", "pr", "view", "7", "--json", "headRefOid", answer=(0, json.dumps({"headRefOid": SHA_A})))
    res = lq.process_item(env, item(red="unrelated"))
    assert res.startswith("REFUSED") and "conflict" in res
    assert len(r.matching("py")) == 2
    assert not r.matching("gh", "pr", "merge")


# ---- the loop: stop file, done log, in-flight, status (defect e) ----


def test_drain_processes_each_pending_item_once_and_logs(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    st.append_queue("feat/2-y|bbb|2|0|t (#2)")
    r = runner_of(env)
    r.first("py", answer=[(0, "pull/7\n"), (0, "pull/9\n")])
    r.on("gh", "pr", "checks", "9", "--watch", answer=(0, ""))
    r.on("gh", "pr", "checks", "9", answer=checks(("build and test", "pass")))
    lq.drain(env)
    done = (env.state / "done.log").read_text(encoding="utf-8").splitlines()
    assert [d.split()[0] for d in done] == ["MERGED", "MERGED"]
    lq.drain(env)
    assert len((env.state / "done.log").read_text(encoding="utf-8").splitlines()) == 2
    assert not (env.state / "inflight.json").exists()


def test_stop_file_stops_before_taking_anything(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    (env.state / "runner.stop").write_text("", encoding="utf-8")
    lq.drain(env)
    assert "STOPPED" in (env.state / "done.log").read_text(encoding="utf-8")
    assert len(st.pending()) == 1


def test_status_shows_queue_inflight_and_last_results(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    st.append_queue("feat/2-y|bbb|2|0|t (#2)")
    st.append_done("MERGED #5 feat/0-z @ ccc")
    st.set_inflight(parsed("feat/3-w|ddd|3|0|t (#3)"), "waiting for checks on #11")
    text = lq.status(env)
    assert "pending (2)" in text and "feat/1-x @ aaa" in text and "feat/2-y @ bbb" in text
    assert "in flight: feat/3-w @ ddd" in text and "waiting for checks on #11" in text
    assert "MERGED #5 feat/0-z @ ccc" in text


def test_retry_makes_a_taken_key_pending_again(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    st.mark_taken(parsed("feat/1-x|aaa|1|0|t (#1)"))
    assert st.pending() == []
    assert st.retry("feat/1-x@aaa") is True
    assert len(st.pending()) == 1
    assert st.retry("nope@zzz") is False


# ---- delegation to land_wt --merge-when-green (#666) ----


def test_default_path_delegates_the_merge_to_land_wt(env: lq.Env) -> None:
    res = lq.process_item(env, item())
    assert res == f"MERGED #7 feat/1-x @ {SHA_A}"
    r = runner_of(env)
    assert "--merge-when-green" in r.matching("py")[0]
    assert not r.matching("gh", "pr", "merge")  # land_wt merged it, the queue does not merge twice
    assert not r.matching("gh", "pr", "checks")


def test_known_red_items_keep_the_in_queue_merge_loop(env: lq.Env) -> None:
    lq.process_item(env, item(red="lint"))
    assert "--merge-when-green" not in runner_of(env).matching("py")[0]


def test_checks_red_or_pending_leaves_the_pr_open_and_is_recorded(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("py", answer=(5, "ERROR: not merging x: failed check(s): lint\nopened https://github.com/o/r/pull/7\n"))
    assert lq.process_item(env, item()) == f"FAILED land_wt rc=5 (PR #7 stays open) feat/1-x @ {SHA_A}"
    r.first("py", answer=(6, "opened https://github.com/o/r/pull/8\n"))
    assert lq.process_item(env, item(sha=SHA_B)) == f"FAILED land_wt rc=6 (PR #8 stays open) feat/1-x @ {SHA_B}"


def test_a_merge_failure_on_a_dirty_pr_is_relanded_once(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "view", answer=view("DIRTY", "CONFLICTING"))
    r.first("py", answer=[(4, "opened https://github.com/o/r/pull/7\nERROR: merge failed"), (0, "opened https://github.com/o/r/pull/8\n")])
    assert lq.process_item(env, item()) == f"MERGED #8 feat/1-x @ {SHA_A}"
    assert r.matching("gh", "pr", "close", "7")
    lands = r.matching("py")
    assert lands[1][lands[1].index("--land-branch") + 1].endswith("-r2")


def test_rc4_on_a_clean_pr_is_a_plain_failure(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("py", answer=(4, "opened https://github.com/o/r/pull/7\nERROR: boom"))
    assert lq.process_item(env, item()).startswith("FAILED land_wt rc=4")
    assert not r.matching("gh", "pr", "close")


def test_each_item_gets_its_own_land_log(env: lq.Env) -> None:
    lq.process_item(env, item())
    log = env.state / f"land-feat-1-x-{SHA_A[:7]}.log"
    assert "pull/7" in log.read_text(encoding="utf-8")


# ---- append-only retry (a rewrite of a log makes `tail -F` monitors replay it) ----


def test_retry_appends_and_never_rewrites_logs(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    st.mark_taken(parsed("feat/1-x|aaa|1|0|t (#1)"))
    st.append_done("FAILED land_wt rc=3 feat/1-x @ aaa")
    before_taken = (env.state / "taken.log").read_text(encoding="utf-8")
    before_done = (env.state / "done.log").read_text(encoding="utf-8")
    assert st.retry("feat/1-x@aaa") is True
    assert (env.state / "taken.log").read_text(encoding="utf-8").startswith(before_taken)
    done_after = (env.state / "done.log").read_text(encoding="utf-8")
    assert done_after.startswith(before_done) and done_after.splitlines()[-1] == "RETRY feat/1-x @ aaa"
    assert len(st.pending()) == 1


def test_a_retried_key_can_be_taken_and_retried_again(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_queue("b|aaa|1|0|t")
    it = parsed("b|aaa|1|0|t")
    st.mark_taken(it)
    assert st.retry("b@aaa")
    st.mark_taken(it)
    assert st.pending() == []
    assert st.retry("b@aaa") and len(st.pending()) == 1


# ---- stacked branches ----


def _ancestry(env: lq.Env, parent: str, child: str) -> None:
    runner_of(env).first("git", "merge-base", "--is-ancestor", parent, child, answer=(0, ""))
    runner_of(env).on("git", "merge-base", "--is-ancestor", answer=(1, ""))


def test_a_stacked_item_is_skipped_when_its_base_failed(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    st.append_queue("feat/2-y|bbb|2|0|t (#2)")
    st.append_queue("feat/3-z|ccc|3|0|t (#3)")
    _ancestry(env, "aaa", "bbb")
    r = runner_of(env)
    r.first("py", answer=[(3, "ERROR: premerge is red"), (0, "opened https://github.com/o/r/pull/9")])
    lq.drain(env)
    done = (env.state / "done.log").read_text(encoding="utf-8").splitlines()
    assert done[0].startswith("FAILED land_wt rc=3 feat/1-x @ aaa")
    assert done[1].startswith("SKIPPED-STACK") and done[1].endswith("feat/2-y @ bbb") and "feat/1-x @ aaa" in done[1]
    assert done[2].startswith("MERGED") and "feat/3-z @ ccc" in done[2]
    assert len(r.matching("py")) == 2  # the stacked one was never attempted


def test_skip_covers_a_stack_failed_in_an_earlier_session(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_done("FAILED land_wt rc=3 feat/1-x @ aaa")
    st.append_queue("feat/2-y|bbb|2|0|t (#2)")
    _ancestry(env, "aaa", "bbb")
    lq.drain(env)
    assert (env.state / "done.log").read_text(encoding="utf-8").splitlines()[-1].endswith("SKIPPED-STACK") or "SKIPPED-STACK" in (
        env.state / "done.log"
    ).read_text(encoding="utf-8")


def test_a_retried_failure_no_longer_blocks_its_stack(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_done("FAILED land_wt rc=3 feat/1-x @ aaa")
    st.append_done("RETRY feat/1-x @ aaa")
    assert st.failed_keys() == set()


def test_merged_branch_is_not_a_failure(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_done("FAILED land_wt rc=3 feat/1-x @ aaa")
    st.append_done("MERGED #4 feat/1-x @ aaa")
    assert st.failed_keys() == set()


# ---- stop file, pid and heartbeat ----


def test_the_stop_file_is_consumed_so_a_restart_is_not_instantly_stopped(env: lq.Env) -> None:
    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.stop").write_text("", encoding="utf-8")
    lq.drain(env)
    assert not (env.state / "runner.stop").exists()


def test_run_writes_pid_and_heartbeat_and_clears_pid_on_exit(env: lq.Env) -> None:
    env.pid = 4242
    env.clock = lambda: 1000.0
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    seen: list[dict[str, float]] = []
    r = runner_of(env)
    r.first("git", "fetch", answer=(0, ""))
    original = r.__call__

    def spy(argv: list[str], cwd: Path | None = None) -> tuple[int, str]:
        if argv[:1] == ["py"]:
            seen.append(json.loads((env.state / "runner.json").read_text(encoding="utf-8")))
        return original(argv, cwd)

    env.runner = spy
    lq.drain(env)
    assert seen and seen[0]["pid"] == 4242 and seen[0]["ts"] == 1000.0
    assert not (env.state / "runner.json").exists()


def test_a_second_runner_is_refused_while_the_first_is_alive(env: lq.Env) -> None:
    import time

    env.alive = lambda pid: True
    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": time.time()}), encoding="utf-8")
    with pytest.raises(lq.AlreadyRunning):
        lq.drain(env)


def test_a_stale_runner_file_does_not_block_a_new_runner(env: lq.Env) -> None:
    env.alive = lambda pid: False
    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0}), encoding="utf-8")
    lq.drain(env)  # empty queue, --once semantics: returns


def test_status_reports_alive_dead_and_never_started(env: lq.Env) -> None:
    env.clock = lambda: 1030.0
    assert "runner: not running" in lq.status(env)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1000.0}), encoding="utf-8")
    env.alive = lambda pid: True
    assert "runner: alive pid 99, heartbeat 30 s ago" in lq.status(env)
    env.alive = lambda pid: False
    assert "runner: DEAD (pid 99 is gone; last heartbeat 30 s ago)" in lq.status(env)


def test_main_status_exit_code_is_one_when_the_runner_is_dead(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    import time

    state = tmp_path / "s"
    state.mkdir()
    (state / "runner.json").write_text(json.dumps({"pid": 99, "ts": time.time()}), encoding="utf-8")
    assert lq.main(["--state", str(state), "status", "--check"], alive=lambda pid: False) == 1
    assert lq.main(["--state", str(state), "status", "--check"], alive=lambda pid: True) == 0


# ---- main checkout fast-forward after a merge (the shell runner did this) ----


def test_main_checkout_is_fast_forwarded_only_when_on_main(env: lq.Env) -> None:
    env.ff_main = True
    r = runner_of(env)
    r.on("git", "rev-parse", "--abbrev-ref", "HEAD", answer=(0, "main"))
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    lq.drain(env)
    assert r.matching("git", "merge", "-q", "--ff-only", "origin/main")


def test_a_feature_branch_checkout_is_left_alone(env: lq.Env) -> None:
    env.ff_main = True
    r = runner_of(env)
    r.on("git", "rev-parse", "--abbrev-ref", "HEAD", answer=(0, "feat/other"))
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    lq.drain(env)
    assert not r.matching("git", "merge")


# ---- cold-review fixes (round 2) ----


def _close_calls(env: lq.Env) -> list[list[str]]:
    return runner_of(env).matching("gh", "pr", "close")


def test_dirty_reland_never_deletes_the_lanes_own_branch(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "view", answer=view("DIRTY", "CONFLICTING"))
    r.first("py", answer=[(4, "opened https://github.com/o/r/pull/7\n"), (0, "opened https://github.com/o/r/pull/8\n")])
    lq.process_item(env, item())  # no remote branch yet: the PR head IS the lane's branch
    assert "--delete-branch" not in _close_calls(env)[0]


def test_dirty_reland_deletes_only_a_land_branch_it_created(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("git", "ls-remote", answer=(0, "deadbeef\trefs/heads/feat/1-x\n"))
    r.first("gh", "pr", "view", answer=view("DIRTY", "CONFLICTING"))
    r.first("py", answer=[(4, "opened https://github.com/o/r/pull/7\n"), (0, "opened https://github.com/o/r/pull/8\n")])
    lq.process_item(env, item())
    assert "--delete-branch" in _close_calls(env)[0]


def test_a_failed_close_stops_the_reland(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "close", answer=(1, "boom"))
    r.first("gh", "pr", "view", answer=view("DIRTY", "CONFLICTING"))
    r.first("py", answer=(4, "opened https://github.com/o/r/pull/7\n"))
    res = lq.process_item(env, item())
    assert res.startswith("REFUSED #7") and "could not close" in res
    assert len(r.matching("py")) == 1


def test_a_new_sha_of_the_same_branch_is_not_stacked_on_its_own_failure(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_done("FAILED land_wt rc=3 feat/1-x @ aaa")
    st.append_queue("feat/1-x|bbb|1|0|t (#1)")
    runner_of(env).first("git", "merge-base", "--is-ancestor", answer=(0, ""))
    lq.drain(env)
    assert (env.state / "done.log").read_text(encoding="utf-8").splitlines()[-1].startswith("MERGED")


def _ticking(env: lq.Env) -> None:
    t = [0.0]
    env.clock = lambda: t[0]

    def sleep(s: float) -> None:
        t[0] += max(s, 1.0)

    env.sleep = sleep


def test_known_red_path_never_merges_with_checks_pending(env: lq.Env) -> None:
    _ticking(env)
    env.checks_timeout = 100.0
    r = runner_of(env)
    r.first("gh", "pr", "checks", "7", "--json", answer=checks(("build and test", "pending")))
    res = lq.process_item(env, item(red="lint"))
    assert res.startswith("FAILED rc=6 (PR #7 stays open)") and "build and test" in res
    assert not r.matching("gh", "pr", "merge")


def test_ff_main_is_off_by_default(env: lq.Env) -> None:
    r = runner_of(env)
    r.on("git", "rev-parse", "--abbrev-ref", "HEAD", answer=(0, "main"))
    lq.State(env.state).append_queue("feat/1-x|aaa|1|0|t (#1)")
    lq.drain(env)
    assert not r.matching("git", "merge")


def test_ff_main_skips_a_dirty_tree_and_logs_it(env: lq.Env) -> None:
    env.ff_main = True
    r = runner_of(env)
    r.on("git", "rev-parse", "--abbrev-ref", "HEAD", answer=(0, "main"))
    r.on("git", "status", "--porcelain", answer=(0, " M x.py"))
    lq.State(env.state).append_queue("feat/1-x|aaa|1|0|t (#1)")
    lq.drain(env)
    assert not r.matching("git", "merge")
    assert "FF-MAIN skipped: working tree is dirty" in (env.state / "done.log").read_text(encoding="utf-8")


def test_ff_main_skips_when_index_lock_exists(env: lq.Env, tmp_path: Path) -> None:
    env.ff_main = True
    gitdir = tmp_path / "gd"
    gitdir.mkdir()
    (gitdir / "index.lock").write_text("", encoding="utf-8")
    r = runner_of(env)
    r.on("git", "rev-parse", "--abbrev-ref", "HEAD", answer=(0, "main"))
    r.on("git", "rev-parse", "--absolute-git-dir", answer=(0, str(gitdir)))
    lq.State(env.state).append_queue("feat/1-x|aaa|1|0|t (#1)")
    lq.drain(env)
    assert not r.matching("git", "merge")
    assert "FF-MAIN skipped: index.lock" in (env.state / "done.log").read_text(encoding="utf-8")


def test_ff_main_failure_is_logged(env: lq.Env) -> None:
    env.ff_main = True
    r = runner_of(env)
    r.on("git", "rev-parse", "--abbrev-ref", "HEAD", answer=(0, "main"))
    r.on("git", "merge", answer=(1, "not possible to fast-forward"))
    lq.State(env.state).append_queue("feat/1-x|aaa|1|0|t (#1)")
    lq.drain(env)
    assert "FF-MAIN failed" in (env.state / "done.log").read_text(encoding="utf-8")


def test_an_interrupted_item_is_recorded_failed_on_the_next_start(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    it = parsed("feat/1-x|aaa|1|0|t (#1)")
    st.mark_taken(it)
    st.set_inflight(it, "waiting for checks on #7")
    lq.drain(env)
    assert "FAILED interrupted feat/1-x @ aaa" in (env.state / "done.log").read_text(encoding="utf-8")
    assert st.inflight() is None
    assert st.retry("feat/1-x@aaa") is True


def test_an_inflight_item_that_already_has_a_verdict_is_not_failed_again(env: lq.Env) -> None:
    st = lq.State(env.state)
    it = parsed("feat/1-x|aaa|1|0|t (#1)")
    st.append_done("MERGED #7 feat/1-x @ aaa")
    st.set_inflight(it, "x")
    lq.drain(env)
    assert "interrupted" not in (env.state / "done.log").read_text(encoding="utf-8")


def test_ls_remote_matches_the_exact_ref_and_checks_rc(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("git", "ls-remote", answer=(0, "deadbeef\trefs/heads/feat/1-x-extra\n"))
    lq.process_item(env, item())
    assert "--land-branch" not in r.matching("py")[0]  # a longer branch name is not this branch
    r.first("git", "ls-remote", answer=(128, "fatal: unable to access"))
    res = lq.process_item(env, item(sha=SHA_B))
    assert res.startswith("FAILED ls-remote rc=128") and len(r.matching("py")) == 1


def test_json_is_parsed_from_stdout_only_and_a_parse_failure_is_logged(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "checks", "7", "--json", answer=(0, "warning: not json"))
    assert lq._checks(env, "7") == []
    assert "PARSE-FAIL gh pr checks 7" in (env.state / "flakes.log").read_text(encoding="utf-8")


def test_run_command_keeps_stderr_out_of_a_successful_stdout(tmp_path: Path) -> None:
    import sys

    rc, out = lq.run_command([sys.executable, "-c", "import sys; print('[1]'); print('warn', file=sys.stderr)"], tmp_path)
    assert rc == 0 and out == "[1]"
    rc, out = lq.run_command([sys.executable, "-c", "import sys; print('o'); print('e', file=sys.stderr); sys.exit(2)"], tmp_path)
    assert rc == 2 and "o" in out and "e" in out


def test_stop_with_no_live_runner_writes_nothing(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    import time

    state = tmp_path / "s"
    assert lq.main(["--state", str(state), "stop"], alive=lambda pid: False) == 1
    assert not (state / "runner.stop").exists()
    assert "no live runner" in capsys.readouterr().out
    (state / "runner.json").write_text(json.dumps({"pid": 5, "ts": time.time()}), encoding="utf-8")
    assert lq.main(["--state", str(state), "stop"], alive=lambda pid: True) == 0
    assert (state / "runner.stop").exists()


@pytest.mark.parametrize(
    "args",
    [
        ["feat/x", "zzzz", "1", "t"],
        ["feat/x", "abc", "1", "t"],
        ["feat/x|y", "abc1234", "1", "t"],
        ["feat/x\ny", "abc1234", "1", "t"],
        ["feat/x", "abc1234", "1", "a|red=lint"],
    ],
)
def test_add_rejects_malformed_input(tmp_path: Path, args: list[str], capsys: pytest.CaptureFixture[str]) -> None:
    state = tmp_path / "s"
    assert lq.main(["--state", str(state), "add", *args]) == 1
    assert not (state / "queue.txt").exists()
    assert capsys.readouterr().out.startswith("ERROR:")


def test_add_accepts_a_good_line(tmp_path: Path) -> None:
    state = tmp_path / "s"
    assert lq.main(["--state", str(state), "add", "feat/x", "abc1234", "1", "t (#1)"]) == 0
    assert (state / "queue.txt").read_text(encoding="utf-8") == "feat/x|abc1234|1|0|t (#1)\n"


def test_runner_file_is_created_exclusively(env: lq.Env, monkeypatch: pytest.MonkeyPatch) -> None:
    import os

    flags: list[int] = []
    real = os.open

    def spy(path: object, fl: int, *a: object, **k: object) -> int:
        flags.append(fl)
        return real(path, fl, *a, **k)  # type: ignore[arg-type]

    monkeypatch.setattr(os, "open", spy)
    lq.drain(env)
    assert any(f & os.O_EXCL for f in flags)


def test_a_stale_runner_file_is_replaced_by_the_new_pid(env: lq.Env) -> None:
    env.alive = lambda pid: False
    env.pid = 77
    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0}), encoding="utf-8")
    seen: list[int] = []
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    original = runner_of(env).__call__

    def spy(argv: list[str], cwd: Path | None = None) -> tuple[int, str]:
        if argv[:1] == ["py"]:
            seen.append(int(json.loads((env.state / "runner.json").read_text(encoding="utf-8"))["pid"]))
        return original(argv, cwd)

    env.runner = spy
    lq.drain(env)
    assert seen == [77]


# ---- cold-review round 2b ----


def test_an_empty_runner_file_is_a_claim_in_progress_not_stale(env: lq.Env) -> None:
    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text("", encoding="utf-8")  # created by O_EXCL, content not written yet
    with pytest.raises(lq.AlreadyRunning):
        lq.drain(env)
    assert (env.state / "runner.json").exists()


def test_an_old_empty_runner_file_is_a_dead_claim_and_is_replaced(env: lq.Env) -> None:
    import os
    import time

    (env.state).mkdir(parents=True, exist_ok=True)
    f = env.state / "runner.json"
    f.write_text("", encoding="utf-8")
    old = time.time() - 3600
    os.utime(f, (old, old))
    lq.drain(env)  # empty queue: claims, returns, clears


def test_a_dead_pid_file_is_renamed_aside_not_unlinked(env: lq.Env) -> None:
    env.alive = lambda pid: False
    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0}), encoding="utf-8")
    lq.drain(env)
    assert list(env.state.glob("runner.json.stale.*"))


def test_a_permission_error_while_claiming_is_already_running_not_a_crash(env: lq.Env, monkeypatch: pytest.MonkeyPatch) -> None:
    import os

    env.alive = lambda pid: False
    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0}), encoding="utf-8")

    def boom(*a: object, **k: object) -> None:
        raise PermissionError("held open")

    monkeypatch.setattr(os, "replace", boom)
    monkeypatch.setattr(os, "rename", boom)
    with pytest.raises(lq.AlreadyRunning):
        lq.drain(env)


def test_a_heartbeat_failure_never_ends_the_runner(env: lq.Env, monkeypatch: pytest.MonkeyPatch) -> None:
    import os

    lq.State(env.state).append_queue("feat/1-x|aaa|1|0|t (#1)")
    lq.drain(env)  # claim works first
    (env.state / "done.log").unlink()
    (env.state / "taken.log").unlink()

    def boom(*a: object, **k: object) -> None:
        raise PermissionError("status has it open")

    monkeypatch.setattr(os, "replace", boom)
    lq.drain(env)
    assert "MERGED" in (env.state / "done.log").read_text(encoding="utf-8")


def test_a_heartbeat_retries_a_transient_failure(env: lq.Env, monkeypatch: pytest.MonkeyPatch) -> None:
    import os

    real = os.replace
    n = [0]

    def flaky(a: object, b: object) -> None:
        n[0] += 1
        if n[0] < 3:
            raise PermissionError("busy")
        real(a, b)  # type: ignore[arg-type]

    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": env.pid, "ts": 1.0}), encoding="utf-8")
    monkeypatch.setattr(os, "replace", flaky)
    lq._heartbeat(env)
    assert json.loads((env.state / "runner.json").read_text(encoding="utf-8"))["pid"] == env.pid


def _prs(*rows: dict[str, int | str]) -> tuple[int, str]:
    return 0, json.dumps(list(rows))


def test_a_retry_resumes_the_open_pr_instead_of_landing_again(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("git", "ls-remote", answer=(0, "deadbeef\trefs/heads/feat/1-x\n"))
    r.first("gh", "pr", "list", "--state", "all", "--head", "feat/1-x", answer=_prs({"number": 7, "headRefOid": SHA_A, "state": "OPEN"}))
    res = lq.process_item(env, item())
    assert res == f"MERGED #7 feat/1-x @ {SHA_A}"
    assert not r.matching("py")
    assert r.matching("gh", "pr", "merge", "7", "--merge")


def test_a_pr_that_merged_already_is_recovered_not_relanded(env: lq.Env) -> None:
    r = runner_of(env)
    r.first(
        "gh",
        "pr",
        "list",
        "--state",
        "all",
        "--head",
        f"feat/1-x-{SHA_A[:7]}",
        answer=_prs({"number": 9, "headRefOid": SHA_A, "state": "MERGED"}),
    )
    assert lq.process_item(env, item()) == f"MERGED-RECOVERED #9 feat/1-x @ {SHA_A}"
    assert not r.matching("py")


def test_an_open_pr_that_contains_the_sha_through_a_merged_main_is_resumed(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "list", "--state", "all", "--head", "feat/1-x", answer=_prs({"number": 7, "headRefOid": SHA_B, "state": "OPEN"}))
    r.first("git", "rev-list", answer=(0, f"{SHA_B} {SHA_A} {SHA_C}"))
    r.first("git", "merge-base", "--is-ancestor", SHA_C, "origin/main", answer=(0, ""))
    assert lq.process_item(env, item()).startswith("MERGED #7")
    assert not r.matching("py")


def test_an_open_pr_for_another_sha_blocks_a_second_pr(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "list", "--state", "all", "--head", "feat/1-x", answer=_prs({"number": 7, "headRefOid": SHA_B, "state": "OPEN"}))
    r.first("git", "merge-base", "--is-ancestor", answer=(1, ""))
    assert lq.process_item(env, item()).startswith("FAILED open PR #7 has unverified commits")
    assert not r.matching("py")


def test_a_failed_pr_lookup_is_a_failure_not_a_duplicate_pr(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "list", answer=(1, "HTTP 502"))
    assert lq.process_item(env, item()).startswith("FAILED gh pr list rc=1")
    assert not r.matching("py")


def test_a_resumed_pr_on_the_lanes_own_branch_is_never_deleted_on_dirty(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("git", "ls-remote", answer=(0, "deadbeef\trefs/heads/feat/1-x\n"))
    r.first("gh", "pr", "list", "--state", "all", "--head", "feat/1-x", answer=_prs({"number": 7, "headRefOid": SHA_A, "state": "OPEN"}))
    r.first("gh", "pr", "view", answer=[view("DIRTY", "CONFLICTING"), view("CLEAN")])
    lq.process_item(env, item())
    assert "--delete-branch" not in _close_calls(env)[0]


def test_json_is_parsed_even_when_gh_exits_nonzero(env: lq.Env) -> None:
    r = runner_of(env)
    body = json.dumps([{"name": "lint", "bucket": "fail", "link": ""}])
    r.first("gh", "pr", "checks", "7", "--json", answer=(8, body + "\n" + lq.STDERR_MARK + "\nsome checks are pending"))
    assert lq._checks(env, "7")[0]["name"] == "lint"


def test_run_command_separates_stderr_on_failure(tmp_path: Path) -> None:
    import sys

    rc, out = lq.run_command([sys.executable, "-c", "import sys; print('[1]'); print('warn', file=sys.stderr); sys.exit(8)"], tmp_path)
    assert rc == 8 and out.split(lq.STDERR_MARK)[0].strip() == "[1]" and "warn" in out


def test_a_live_pid_with_an_ancient_heartbeat_is_never_replaced_automatically(env: lq.Env) -> None:
    env.alive = lambda pid: True
    env.clock = lambda: 1_000_000.0
    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0}), encoding="utf-8")
    with pytest.raises(lq.AlreadyRunning):
        lq.drain(env)
    assert json.loads((env.state / "runner.json").read_text(encoding="utf-8"))["pid"] == 99


def test_status_marks_an_ancient_heartbeat_as_no_progress_but_alive(env: lq.Env) -> None:
    env.alive = lambda pid: True
    env.state.mkdir(parents=True, exist_ok=True)
    env.clock = lambda: 1_000_000.0
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0}), encoding="utf-8")
    text, alive = lq.runner_line(env)
    assert alive is True
    assert "alive, NO PROGRESS for 16666 min - inspect; replace with run --replace" in text


def test_interrupted_recovery_does_not_match_a_longer_branch_suffix(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_done("MERGED #3 xfeat/1-x @ aaa")  # a different branch ending in the same text
    st.set_inflight(parsed("feat/1-x|aaa|1|0|t"), "landing")
    lq.drain(env)
    assert "FAILED interrupted feat/1-x @ aaa" in (env.state / "done.log").read_text(encoding="utf-8")


def test_an_empty_claim_is_waited_on_and_a_vanished_one_is_taken(env: lq.Env) -> None:
    (env.state).mkdir(parents=True, exist_ok=True)
    f = env.state / "runner.json"
    f.write_text("", encoding="utf-8")
    env.sleep = lambda s: f.unlink(missing_ok=True)  # the claimer gave up while we waited
    lq.drain(env)  # claims instead of refusing


# ---- round 3 ----


def test_the_heartbeat_keeps_beating_during_a_long_item(env: lq.Env) -> None:
    import itertools
    import time

    env.heartbeat_seconds = 0.02
    tick = itertools.count(1)
    env.clock = lambda: float(next(tick))
    lq.State(env.state).append_queue("feat/1-x|aaa|1|0|t (#1)")
    seen: list[float] = []
    original = runner_of(env).__call__

    def slow(argv: list[str], cwd: Path | None = None) -> tuple[int, str]:
        if argv[:1] == ["py"]:
            seen.append(json.loads((env.state / "runner.json").read_text(encoding="utf-8"))["beat"])
            time.sleep(0.4)
            seen.append(json.loads((env.state / "runner.json").read_text(encoding="utf-8"))["beat"])
        return original(argv, cwd)

    env.runner = slow
    lq.drain(env)
    assert seen[1] > seen[0]  # beat from the thread while the item was blocked


def test_the_heartbeat_thread_stops_when_the_runner_ends(env: lq.Env) -> None:
    import threading

    env.heartbeat_seconds = 0.01
    lq.drain(env)
    assert not [th for th in threading.enumerate() if th.name == "land-queue-heartbeat"]


def test_resume_refuses_a_head_with_unverified_commits(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "list", "--state", "all", "--head", "feat/1-x", answer=_prs({"number": 7, "headRefOid": SHA_B, "state": "OPEN"}))
    r.first("git", "rev-list", answer=(0, f"{SHA_B} {SHA_A}"))  # one parent: a real commit on top, not a merge of main
    assert lq.process_item(env, item()).startswith("FAILED open PR #7 has unverified commits")
    assert not r.matching("py")
    assert not r.matching("gh", "pr", "merge", "7")


def test_resume_refuses_a_merge_of_something_other_than_main(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "list", "--state", "all", "--head", "feat/1-x", answer=_prs({"number": 7, "headRefOid": SHA_B, "state": "OPEN"}))
    r.first("git", "rev-list", answer=(0, f"{SHA_B} {SHA_A} {SHA_C}"))
    r.first("git", "merge-base", "--is-ancestor", SHA_C, "origin/main", answer=(1, ""))
    assert lq.process_item(env, item()).startswith("FAILED open PR #7 has unverified commits")
    assert not r.matching("py")


def test_merge_pins_the_head_commit_it_verified(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "view", "7", "--json", "headRefOid", answer=(0, json.dumps({"headRefOid": SHA_A})))
    lq.process_item(env, item(red="unrelated"))
    merge = r.matching("gh", "pr", "merge", "7")[0]
    assert merge[merge.index("--match-head-commit") + 1] == SHA_A


def test_a_merged_pr_with_an_unfetchable_head_is_recovered_through_origin_main(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "list", "--state", "all", "--head", "feat/1-x", answer=_prs({"number": 9, "headRefOid": SHA_B, "state": "MERGED"}))
    r.first("git", "merge-base", "--is-ancestor", SHA_A, SHA_B, answer=(128, "fatal: bad object"))
    r.first("git", "merge-base", "--is-ancestor", SHA_A, "origin/main", answer=(0, ""))
    assert lq.process_item(env, item()) == f"MERGED-RECOVERED #9 feat/1-x @ {SHA_A}"
    assert not r.matching("py")


def test_an_unverifiable_merged_pr_is_refused_not_duplicated(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "list", "--state", "all", "--head", "feat/1-x", answer=_prs({"number": 9, "headRefOid": SHA_B, "state": "MERGED"}))
    r.first("git", "merge-base", "--is-ancestor", answer=(128, "fatal: bad object"))
    assert lq.process_item(env, item()).startswith("FAILED cannot verify merged PR #9")
    assert not r.matching("py")


def test_every_done_line_the_code_writes_ends_with_branch_at_sha(env: lq.Env) -> None:
    st = lq.State(env.state)
    r = runner_of(env)
    outcomes = []
    r.first("py", answer=[(3, "x"), (5, "pull/7"), (6, "pull/7"), (4, "pull/7"), (0, "pull/7"), (0, "")])
    r.first("gh", "pr", "view", answer=view("CLEAN"))
    for sha in ("a1", "b2", "c3", "d4", "e5", "f6"):
        outcomes.append(lq.process_item(env, item(sha=sha * 3)))
    r.first("gh", "pr", "list", answer=_prs({"number": 9, "headRefOid": SHA_A, "state": "MERGED"}))
    outcomes.append(lq.process_item(env, item(sha=SHA_A)))
    st.append_done("FAILED interrupted feat/1-x @ aaa")
    st.append_queue("feat/1-x|aaa|1|0|t")
    st.append_queue("feat/2-y|bbb|1|0|t")
    st.append_done("FAILED land_wt rc=3 feat/1-x @ aaa")
    runner_of(env).first("git", "merge-base", "--is-ancestor", answer=(0, ""))
    st.mark_taken(parsed("feat/1-x|aaa|1|0|t"))
    lq.drain(env)
    lines = outcomes + (env.state / "done.log").read_text(encoding="utf-8").splitlines()
    for ln in lines:
        if ln.split()[0] in {"STOPPED", "FF-MAIN"}:
            continue
        assert lq._DONE_KEY.search(ln), ln


def test_a_resumed_r2_head_keeps_the_single_reland_budget(env: lq.Env) -> None:
    r = runner_of(env)
    r.first(
        "gh",
        "pr",
        "list",
        "--state",
        "all",
        "--head",
        f"feat/1-x-{SHA_A[:7]}-r2",
        answer=_prs({"number": 7, "headRefOid": SHA_A, "state": "OPEN"}),
    )
    r.first("gh", "pr", "view", answer=view("DIRTY", "CONFLICTING"))
    res = lq.process_item(env, item())
    assert res.startswith("REFUSED #7 merge conflict") and not r.matching("py")


def test_a_read_error_on_runner_json_is_not_an_empty_claim(env: lq.Env, monkeypatch: pytest.MonkeyPatch) -> None:
    (env.state).mkdir(parents=True, exist_ok=True)
    f = env.state / "runner.json"
    f.write_text(json.dumps({"pid": 99, "ts": 1.0}), encoding="utf-8")
    import os
    import time

    os.utime(f, (time.time() - 3600, time.time() - 3600))  # old, so an "empty" misreading would rename it aside
    real = Path.read_text

    def deny(self: Path, *a: object, **k: object) -> str:
        if self.name == "runner.json":
            raise PermissionError("mid-replace")
        return real(self, *a, **k)  # type: ignore[arg-type]

    monkeypatch.setattr(Path, "read_text", deny)
    with pytest.raises(lq.AlreadyRunning):
        lq.drain(env)
    assert f.exists() and not list(env.state.glob("runner.json.stale.*"))


@pytest.mark.parametrize("content", ['{"pid": "abc", "ts": 1}', '{"pid": null}', '{"pid": 5, "ts": "x"}', "[1]"])
def test_a_hand_edited_runner_file_never_crashes_run_or_status(env: lq.Env, content: str) -> None:
    import os
    import time

    (env.state).mkdir(parents=True, exist_ok=True)
    f = env.state / "runner.json"
    f.write_text(content, encoding="utf-8")
    assert "runner: not running" in lq.status(env)
    old = time.time() - 3600
    os.utime(f, (old, old))
    lq.drain(env)  # an old unparsable claim is replaced, no ValueError


def test_resume_refuses_a_head_whose_chain_does_not_reach_the_sha(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "list", "--state", "all", "--head", "feat/1-x", answer=_prs({"number": 7, "headRefOid": SHA_B, "state": "OPEN"}))
    r.first("git", "rev-list", answer=(0, f"{SHA_B} {SHA_C} {SHA_C}"))  # a merge of main, but on top of some other base
    r.first("git", "merge-base", "--is-ancestor", SHA_C, "origin/main", answer=(0, ""))
    assert lq.process_item(env, item()).startswith("FAILED open PR #7 has unverified commits")
    assert not r.matching("py")


# ---- round 4 ----


def _hb(env: lq.Env) -> dict[str, float]:
    return json.loads((env.state / "runner.json").read_text(encoding="utf-8"))


def test_the_beater_writes_the_main_threads_progress_not_its_own_clock(env: lq.Env) -> None:
    import itertools
    import time

    env.heartbeat_seconds = 0.02
    tick = itertools.count(1000)
    env.clock = lambda: float(next(tick))
    lq.State(env.state).append_queue("feat/1-x|aaa|1|0|t (#1)")
    seen: list[dict[str, float]] = []
    original = runner_of(env).__call__

    def hung(argv: list[str], cwd: Path | None = None) -> tuple[int, str]:
        if argv[:1] == ["py"]:
            time.sleep(0.15)  # let the beater write the latest progress once
            seen.append(_hb(env))
            time.sleep(0.3)  # the main thread makes no progress while blocked here
            seen.append(_hb(env))
        return original(argv, cwd)

    env.runner = hung
    lq.drain(env)
    assert seen[1]["beat"] > seen[0]["beat"]  # the beater is alive
    assert seen[1]["ts"] == seen[0]["ts"]  # but progress did not move, so staleness can fire


def test_no_progress_is_judged_on_progress_not_on_the_beat(env: lq.Env) -> None:
    env.alive = lambda pid: True
    env.clock = lambda: 1_000_000.0
    env.state.mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0, "beat": 999_990.0}), encoding="utf-8")
    assert "NO PROGRESS" in lq.status(env)


def test_progress_moves_when_a_subprocess_returns(env: lq.Env) -> None:
    t = [10.0]
    env.clock = lambda: t[0]
    env.progress = 0.0
    t[0] = 55.0
    env.run("git", "status")
    assert env.progress == 55.0


def test_a_beater_never_overwrites_a_new_owners_claim(env: lq.Env) -> None:
    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 4321, "ts": 5.0}), encoding="utf-8")
    assert lq.State(env.state).write_runner(env.pid, 9.0) is False
    assert _hb(env)["pid"] == 4321
    (env.state / "runner.json").write_text(json.dumps({"pid": env.pid, "ts": 5.0}), encoding="utf-8")
    assert lq.State(env.state).write_runner(env.pid, 9.0) is True
    assert _hb(env)["ts"] == 9.0


def test_a_beater_error_is_logged_and_the_beating_goes_on(env: lq.Env, monkeypatch: pytest.MonkeyPatch) -> None:
    import time

    env.heartbeat_seconds = 0.01
    calls = [0]
    real = lq.State.write_runner

    def flaky(self: lq.State, *a: object, **k: object) -> bool:
        calls[0] += 1
        if 2 <= calls[0] <= 4:
            raise RuntimeError("boom")
        return real(self, *a, **k)  # type: ignore[arg-type]

    monkeypatch.setattr(lq.State, "write_runner", flaky)
    lq.State(env.state).append_queue("feat/1-x|aaa|1|0|t (#1)")
    original = runner_of(env).__call__

    def slow(argv: list[str], cwd: Path | None = None) -> tuple[int, str]:
        if argv[:1] == ["py"]:
            time.sleep(0.3)
        return original(argv, cwd)

    env.runner = slow
    lq.drain(env)
    assert calls[0] > 5
    assert "HEARTBEAT-FAIL RuntimeError" in (env.state / "flakes.log").read_text(encoding="utf-8")


def test_run_command_times_out_a_hung_gh_or_git(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    import sys

    monkeypatch.setattr(lq, "COMMAND_TIMEOUT_SECONDS", 0.5)
    rc, out = lq.run_command([sys.executable, "-c", "import time; time.sleep(30)"], tmp_path)
    assert rc == 124 and "TIMEOUT after" in out


def test_land_wt_gets_a_bound_that_covers_premerge_and_the_checks_wait(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    import subprocess

    import land_wt

    seen: list[object] = []

    def fake(argv: list[str], **kw: object) -> subprocess.CompletedProcess[bytes]:
        seen.append(kw.get("timeout"))
        return subprocess.CompletedProcess(argv, 0, b"", b"")

    monkeypatch.setattr(lq, "run_bounded", fake)
    lq.run_command(["py", "scripts/land_wt.py", "--branch", "b"], tmp_path)
    lq.run_command(["gh", "pr", "view", "7"], tmp_path)
    assert seen[1] == lq.COMMAND_TIMEOUT_SECONDS
    assert seen[0] == lq.LAND_WT_TIMEOUT_SECONDS
    assert lq.LAND_WT_TIMEOUT_SECONDS > land_wt.PREMERGE_TIMEOUT_SECONDS + land_wt.CHECKS_TIMEOUT_SECONDS


def test_a_timeout_is_logged_to_flakes(env: lq.Env) -> None:
    runner_of(env).first("gh", "pr", "view", answer=(124, "TIMEOUT after 600 s: gh pr view 7"))
    env.run("gh", "pr", "view", "7")
    assert "TIMEOUT gh pr view 7" in (env.state / "flakes.log").read_text(encoding="utf-8")


# ---- round 6 (orchestrator DECISION): a live pid is never replaced automatically; --replace is explicit ----


def _stuck_runner(env: lq.Env, started: float | None = 100.0) -> list[int]:
    """A live runner (pid 99) in runner.json whose process creation time matches; returns the list the fake killer appends to."""
    killed: list[int] = []
    env.alive = lambda pid: True
    env.start_time = lambda pid: 100.0 if pid == 99 else None
    env.kill_runner = lambda pid: killed.append(pid) or True
    env.clock = lambda: 1_000_000.0
    env.state.mkdir(parents=True, exist_ok=True)
    rec: dict[str, float] = {"pid": 99, "ts": 1.0}
    if started is not None:
        rec["started"] = started
    (env.state / "runner.json").write_text(json.dumps(rec), encoding="utf-8")
    return killed


def test_replace_kills_the_old_runner_then_takes_over_and_records_it(env: lq.Env) -> None:
    killed = _stuck_runner(env)
    lq.drain(env, replace=True)
    assert killed == [99]
    assert "REPLACED pid 99 (killed)" in (env.state / "done.log").read_text(encoding="utf-8")
    assert list(env.state.glob("runner.json.stale.*"))


def test_replace_recovers_the_killed_runners_inflight_item(env: lq.Env) -> None:
    _stuck_runner(env)
    st = lq.State(env.state)
    st.append_queue("feat/9-z|ccc|9|0|t (#9)")
    st.mark_taken(parsed("feat/9-z|ccc|9|0|t"))
    st.set_inflight(parsed("feat/9-z|ccc|9|0|t"), "waiting for checks on #5")
    lq.drain(env, replace=True)
    done = (env.state / "done.log").read_text(encoding="utf-8")
    assert "FAILED interrupted feat/9-z @ ccc" in done and done.index("REPLACED") < done.index("FAILED interrupted")


def test_replace_never_takes_over_when_the_old_runner_survives_the_kill(env: lq.Env) -> None:
    _stuck_runner(env)
    env.kill_runner = lambda pid: False
    with pytest.raises(lq.AlreadyRunning):
        lq.drain(env, replace=True)
    assert _hb(env)["pid"] == 99 and not list(env.state.glob("runner.json.stale.*"))


def test_replace_never_kills_a_pid_it_cannot_verify(env: lq.Env) -> None:
    killed = _stuck_runner(env, started=None)  # no recorded creation time: it could be any program
    with pytest.raises(lq.AlreadyRunning):
        lq.drain(env, replace=True)
    assert killed == [] and _hb(env)["pid"] == 99


def test_replace_never_kills_a_pid_whose_creation_time_differs(env: lq.Env) -> None:
    killed = _stuck_runner(env, started=5.0)  # pid 99 now belongs to another program
    lq.drain(env, replace=True)
    assert killed == []
    assert "REPLACED" not in (env.state / "done.log").read_text(encoding="utf-8") if (env.state / "done.log").exists() else True


def test_replace_of_a_dead_runner_kills_and_logs_nothing(env: lq.Env) -> None:
    killed = _stuck_runner(env)
    env.alive = lambda pid: False
    lq.drain(env, replace=True)
    assert killed == []
    assert "REPLACED" not in (env.state / "done.log").read_text(encoding="utf-8") if (env.state / "done.log").exists() else True


def test_the_cli_replace_flag_reaches_the_claim(tmp_path: Path) -> None:
    state = tmp_path / "s"
    state.mkdir()
    (state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0, "started": 100.0}), encoding="utf-8")
    killed: list[int] = []
    kw = {"alive": lambda pid: True, "start_time": lambda pid: 100.0 if pid == 99 else None, "kill": lambda pid: killed.append(pid) or True}
    assert lq.main(["--state", str(state), "run", "--once"], **kw) == 1
    assert killed == []
    assert lq.main(["--state", str(state), "run", "--once", "--replace"], **kw) == 0
    assert killed == [99]
    assert "REPLACED pid 99 (killed)" in (state / "done.log").read_text(encoding="utf-8")


def test_a_pid_with_a_different_creation_time_is_dead(env: lq.Env) -> None:
    env.alive = lambda pid: True  # Windows reused pid 99 for another program
    env.start_time = lambda pid: 5_000.0
    env.state.mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0, "started": 100.0}), encoding="utf-8")
    text, alive = lq.runner_line(env)
    assert "DEAD" in text and alive is False
    lq.drain(env)  # claims without --replace, and does not call it a replacement
    assert "REPLACED" not in (env.state / "done.log").read_text(encoding="utf-8") if (env.state / "done.log").exists() else True


def test_a_pid_with_the_same_creation_time_is_alive(env: lq.Env) -> None:
    env.alive = lambda pid: True
    env.start_time = lambda pid: 100.0
    env.state.mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0, "started": 100.0}), encoding="utf-8")
    assert lq.runner_line(env)[1] is True
    with pytest.raises(lq.AlreadyRunning):
        lq.drain(env)


def test_the_claim_records_our_creation_time_and_the_heartbeat_keeps_it(env: lq.Env) -> None:
    env.start_time = lambda pid: 777.0
    lq.State(env.state).append_queue("feat/1-x|aaa|1|0|t (#1)")
    seen: list[dict[str, float]] = []
    original = runner_of(env).__call__

    def spy(argv: list[str], cwd: Path | None = None) -> tuple[int, str]:
        if argv[:1] == ["py"]:
            seen.append(_hb(env))
        return original(argv, cwd)

    env.runner = spy
    lq.drain(env)
    assert seen and seen[0]["started"] == 777.0


def test_a_displaced_runner_stops_before_taking_another_item(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    st.append_queue("feat/2-y|bbb|2|0|u (#2)")
    original = runner_of(env).__call__

    def steal(argv: list[str], cwd: Path | None = None) -> tuple[int, str]:
        if argv[:1] == ["py"]:
            (env.state / "runner.json").write_text(json.dumps({"pid": 4321, "ts": 5.0}), encoding="utf-8")
        return original(argv, cwd)

    env.runner = steal
    lq.drain(env)
    done = (env.state / "done.log").read_text(encoding="utf-8")
    assert "DISPLACED" in done and "feat/2-y" not in done
    assert "feat/2-y@bbb" not in st.taken()
    assert _hb(env)["pid"] == 4321  # and it left the new owner's file alone


@pytest.mark.parametrize("content", ["", "not json", '{"pid": "x"}'])
def test_a_heartbeat_never_overwrites_an_empty_or_bad_runner_file(env: lq.Env, content: str) -> None:
    env.state.mkdir(parents=True, exist_ok=True)
    f = env.state / "runner.json"
    f.write_text(content, encoding="utf-8")
    assert lq.State(env.state).write_runner(env.pid, 9.0, lambda s: None) is False
    assert f.read_text(encoding="utf-8") == content


def test_a_heartbeat_skips_an_unreadable_file_without_overwriting_or_stopping(env: lq.Env, monkeypatch: pytest.MonkeyPatch) -> None:
    env.state.mkdir(parents=True, exist_ok=True)
    f = env.state / "runner.json"
    f.write_text(json.dumps({"pid": env.pid, "ts": 1.0}), encoding="utf-8")
    real = Path.read_text

    def deny(self: Path, *a: object, **k: object) -> str:
        if self.name == "runner.json":
            raise PermissionError("mid-replace")
        return real(self, *a, **k)  # type: ignore[arg-type]

    monkeypatch.setattr(Path, "read_text", deny)
    assert lq.State(env.state).write_runner(env.pid, 9.0, lambda s: None) is True
    monkeypatch.undo()
    assert json.loads(f.read_text(encoding="utf-8"))["ts"] == 1.0


@pytest.mark.parametrize("answer", [(0, "{}"), (0, "not json"), (1, ""), (0, json.dumps({"headRefOid": ""}))])
def test_an_unreadable_head_commit_never_merges_unpinned(env: lq.Env, answer: tuple[int, str]) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "view", "7", "--json", "headRefOid", answer=answer)
    verdict, detail = lq.merge_green(env, "7")
    assert verdict == "PENDING" and "head commit" in detail
    assert not r.matching("gh", "pr", "merge")


def test_the_merge_is_always_pinned_to_the_head_commit(env: lq.Env) -> None:
    r = runner_of(env)
    assert lq.merge_green(env, "7")[0] == "MERGED"
    merge = r.matching("gh", "pr", "merge")[0]
    assert merge[merge.index("--match-head-commit") + 1] == SHA_A


def test_run_command_timeout_kills_the_grandchild_too(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    import sys
    import time

    import land_wt

    pidfile = tmp_path / "gc.pid"
    script = tmp_path / "parent.py"
    script.write_text(
        "import subprocess, sys, time\n"
        "from pathlib import Path\n"
        "gc = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'])\n"
        "Path(sys.argv[1]).write_text(str(gc.pid))\n"
        "time.sleep(60)\n",
        encoding="utf-8",
        newline="\n",
    )
    monkeypatch.setattr(lq, "COMMAND_TIMEOUT_SECONDS", 2.0)
    start = time.time()
    rc, out = lq.run_command([sys.executable, str(script), str(pidfile)], tmp_path)
    assert rc == 124 and "TIMEOUT after" in out
    assert time.time() - start < 15
    pid = int(pidfile.read_text())
    end = time.time() + 10
    while land_wt.pid_alive(pid) and time.time() < end:
        time.sleep(0.1)
    assert not land_wt.pid_alive(pid)


def test_the_land_wt_bound_covers_the_worst_case_chain_of_git_and_gh_calls() -> None:
    import land_wt

    chain = lq.LAND_WT_GIT_CALLS * land_wt.GIT_TIMEOUT_SECONDS + lq.LAND_WT_GH_CALLS * land_wt.GH_TIMEOUT_SECONDS
    assert land_wt.PREMERGE_TIMEOUT_SECONDS + 2 * land_wt.CHECKS_TIMEOUT_SECONDS + chain + 30 * 60 <= lq.LAND_WT_TIMEOUT_SECONDS
    assert lq.LAND_WT_GIT_CALLS >= 15 and lq.LAND_WT_GH_CALLS >= 10
