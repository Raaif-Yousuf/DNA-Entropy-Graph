"""Tests for scripts/land_queue.py. No network, no git: every external command goes through an injected fake runner."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

import land_queue as lq

SHA_A = "a" * 40
SHA_B = "b" * 40


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
    assert lq.process_item(env, item(red="unrelated")).startswith("MERGED #7")
    assert len(r.matching("gh", "pr", "view")) == 2


def test_still_dirty_after_the_reland_is_refused(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "view", answer=view("DIRTY", "CONFLICTING"))
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
    assert done[1].startswith("SKIPPED-STACK feat/2-y @ bbb") and "feat/1-x @ aaa" in done[1]
    assert done[2].startswith("MERGED") and "feat/3-z @ ccc" in done[2]
    assert len(r.matching("py")) == 2  # the stacked one was never attempted


def test_skip_covers_a_stack_failed_in_an_earlier_session(env: lq.Env) -> None:
    st = lq.State(env.state)
    st.append_done("FAILED land_wt rc=3 feat/1-x @ aaa")
    st.append_queue("feat/2-y|bbb|2|0|t (#2)")
    _ancestry(env, "aaa", "bbb")
    lq.drain(env)
    assert "SKIPPED-STACK feat/2-y @ bbb" in (env.state / "done.log").read_text(encoding="utf-8")


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
    env.alive = lambda pid: True
    (env.state).mkdir(parents=True, exist_ok=True)
    (env.state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0}), encoding="utf-8")
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
    state = tmp_path / "s"
    state.mkdir()
    (state / "runner.json").write_text(json.dumps({"pid": 99, "ts": 1.0}), encoding="utf-8")
    assert lq.main(["--state", str(state), "status", "--check"], alive=lambda pid: False) == 1
    assert lq.main(["--state", str(state), "status", "--check"], alive=lambda pid: True) == 0


# ---- main checkout fast-forward after a merge (the shell runner did this) ----


def test_main_checkout_is_fast_forwarded_only_when_on_main(env: lq.Env) -> None:
    r = runner_of(env)
    r.on("git", "rev-parse", "--abbrev-ref", "HEAD", answer=(0, "main"))
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    lq.drain(env)
    assert r.matching("git", "merge", "-q", "--ff-only", "origin/main")


def test_a_feature_branch_checkout_is_left_alone(env: lq.Env) -> None:
    r = runner_of(env)
    r.on("git", "rev-parse", "--abbrev-ref", "HEAD", answer=(0, "feat/other"))
    st = lq.State(env.state)
    st.append_queue("feat/1-x|aaa|1|0|t (#1)")
    lq.drain(env)
    assert not r.matching("git", "merge")
