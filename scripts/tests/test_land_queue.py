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


def test_process_item_merges_when_green(env: lq.Env) -> None:
    res = lq.process_item(env, item())
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
    res = lq.process_item(env, item())
    assert res.startswith("MERGED #7")
    assert r.matching("gh", "run", "rerun", "100", "--failed")
    assert "PR#7 run 100 rerun after: build and test" in (env.state / "flakes.log").read_text(encoding="utf-8")


def test_red_after_the_rerun_is_refused(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "checks", "7", "--json", answer=checks(("build and test", "fail")))
    assert lq.process_item(env, item()) == f"REFUSED #7 failing: build and test feat/1-x @ {SHA_A}"
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
    assert lq.process_item(env, item()) == f"MERGED #8 feat/1-x @ {SHA_A}"
    assert r.matching("gh", "pr", "close", "7")
    lands = r.matching("py")
    assert len(lands) == 2 and all(SHA_A in c for c in lands)
    assert lands[1][lands[1].index("--land-branch") + 1].endswith("-r2")
    assert not r.matching("gh", "pr", "merge", "7")


def test_unknown_mergeability_is_polled_not_merged_blind(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "view", answer=[view("UNKNOWN", "UNKNOWN"), view("CLEAN")])
    assert lq.process_item(env, item()).startswith("MERGED #7")
    assert len(r.matching("gh", "pr", "view")) == 2


def test_still_dirty_after_the_reland_is_refused(env: lq.Env) -> None:
    r = runner_of(env)
    r.first("gh", "pr", "view", answer=view("DIRTY", "CONFLICTING"))
    res = lq.process_item(env, item())
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
