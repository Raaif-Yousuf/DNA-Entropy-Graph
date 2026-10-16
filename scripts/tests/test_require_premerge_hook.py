"""Tests for scripts/premerge_stamp.py and scripts/hooks/require_premerge_before_pr.py (#451).

Every git repo here is a throwaway under tmp_path; the real checkout is never touched.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
HOOKS_DIR = SCRIPTS_DIR / "hooks"
sys.path.insert(0, str(SCRIPTS_DIR))
sys.path.insert(0, str(HOOKS_DIR))

import require_premerge_before_pr as hook  # noqa: E402

import premerge_stamp as stamp  # noqa: E402

HOOK = HOOKS_DIR / "require_premerge_before_pr.py"


def _git(repo: Path, *args: str) -> None:
    subprocess.run(["git", *args], cwd=repo, check=True, capture_output=True)


def _commit(repo: Path, text: str) -> None:
    (repo / "a.txt").write_text(text, encoding="utf-8", newline="\n")
    _git(repo, "add", "-A")
    _git(repo, "commit", "-q", "-m", text.strip() or "c")


@pytest.fixture()
def repo(tmp_path, monkeypatch):
    monkeypatch.delenv(hook.BYPASS_VAR, raising=False)
    _git(tmp_path, "init", "-q")
    _git(tmp_path, "config", "user.email", "t@example.com")
    _git(tmp_path, "config", "user.name", "T")
    _commit(tmp_path, "1")
    return tmp_path


def _run_hook(payload: dict | str) -> tuple[int, dict | None]:
    proc = subprocess.run(
        [sys.executable, str(HOOK)],
        input=payload if isinstance(payload, str) else json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=30,
        env={k: v for k, v in os.environ.items() if k != hook.BYPASS_VAR},
        check=False,
    )
    return proc.returncode, json.loads(proc.stdout) if proc.stdout.strip() else None


@pytest.mark.parametrize("module", ["premerge_stamp.py", "hooks/require_premerge_before_pr.py"])
def test_self_tests_pass(module):
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / module), "--self-test"],
        capture_output=True,
        text=True,
        timeout=60,
        check=False,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr


def test_stamp_lifecycle(repo):
    assert stamp.check_stamp(repo)[0] is False
    path = stamp.write_stamp(repo, "fast")
    assert path.parent == repo / ".git"
    assert stamp.check_stamp(repo)[0] is True
    assert stamp.check_stamp(repo, now=time.time() + stamp.MAX_AGE_SECONDS + 5)[0] is False
    _commit(repo, "2")
    ok, reason = stamp.check_stamp(repo)
    assert not ok and "HEAD is now" in reason


def test_writing_a_stamp_does_not_dirty_the_tree(repo):
    stamp.write_stamp(repo, "full")
    status = subprocess.run(["git", "status", "--porcelain"], cwd=repo, capture_output=True, text=True, check=True)
    assert status.stdout.strip() == ""


def test_bad_mode_is_rejected(repo):
    with pytest.raises(ValueError):
        stamp.write_stamp(repo, "partial")


@pytest.mark.parametrize(
    "command",
    [
        "gh pr create --title x",
        "gh pr merge 3 --merge --delete-branch",
        "cd app && gh pr create",
        "GH_TOKEN=x gh pr create",
        "gh -R a/b pr merge 1",
    ],
)
def test_denied_without_a_stamp(repo, command):
    reason = hook.verdict(command, repo)
    assert reason is not None
    assert "premerge.py" in reason


@pytest.mark.parametrize(
    "command", ["gh pr view 3", "gh pr checks 3", "gh issue create -t x", "grep 'gh pr create' x.md", "echo gh pr merge is blocked", ""]
)
def test_unrelated_commands_are_allowed(repo, command):
    assert hook.verdict(command, repo) is None


def test_allowed_with_a_stamp_on_head_and_denied_after_a_new_commit(repo):
    stamp.write_stamp(repo, "fast")
    assert hook.verdict("gh pr create", repo) is None
    _commit(repo, "2")
    assert hook.verdict("gh pr create", repo) is not None


def test_inline_bypass_allows_and_is_logged(repo):
    assert hook.verdict("DEG_SKIP_PREMERGE=hotfix gh pr create", repo) is None
    log = repo / ".git" / "premerge-bypass.log"
    assert "hotfix" in log.read_text(encoding="utf-8")


def test_environment_bypass_allows(repo, monkeypatch):
    monkeypatch.setenv(hook.BYPASS_VAR, "owner said so")
    assert hook.verdict("gh pr merge 1", repo) is None


def test_non_repo_cwd_fails_open(tmp_path):
    assert hook.verdict("gh pr create", tmp_path) is None


def test_end_to_end_denies_then_allows(repo):
    payload = {"tool_name": "Bash", "tool_input": {"command": "gh pr create --title x"}, "cwd": str(repo)}
    code, decision = _run_hook(payload)
    assert code == 0
    assert decision["hookSpecificOutput"]["permissionDecision"] == "deny"
    stamp.write_stamp(repo, "fast")
    code, decision = _run_hook(payload)
    assert code == 0 and decision is None


def test_malformed_stdin_fails_open():
    code, decision = _run_hook("not json at all")
    assert code == 0 and decision is None


def test_runs_under_the_dispatcher(repo):
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "run_hook.py"), "require_premerge_before_pr"],
        input=json.dumps({"tool_input": {"command": "gh pr create"}, "cwd": str(repo)}),
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )
    assert proc.returncode == 0
    assert json.loads(proc.stdout)["hookSpecificOutput"]["permissionDecision"] == "deny"


SHA = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"


@pytest.mark.parametrize(
    "command",
    [
        'gh issue comment 5 --body "see gh pr merge"',
        'gh issue create --title "fix pr create hook"',
        "gh issue create --title 'gh pr create'",
        'gh issue comment 5 --body "x" && echo "gh pr merge"',
        'gh pr view 5 --json body --jq ".body | test(\\"pr create\\")"',
        "echo gh pr create",
        'Write-Output "gh pr merge 3"',
    ],
)
def test_quoted_mentions_are_not_commands(repo, command):
    assert hook.verdict(command, repo) is None


@pytest.mark.parametrize(
    "command",
    [
        "gh pr create",
        "gh --repo a/b pr create",
        "gh -R a/b pr merge 1",
        "gh --repo=a/b pr create",
        'gh pr create --title "x; y" --body "z"',
        "git status && gh pr merge 3",
        "& gh pr create",
        "gh.exe pr create",
        "FOO=1 gh pr create",
    ],
)
def test_real_commands_are_still_denied(repo, command):
    assert hook.verdict(command, repo) is not None


def test_landed_merge_needs_the_recorded_head_in_match_head_commit(repo):
    assert hook.verdict(f"gh pr merge 77 --match-head-commit {SHA}", repo) is not None
    stamp.record_landed(repo, "77", SHA)
    assert hook.verdict(f"gh pr merge 77 --merge --match-head-commit {SHA}", repo) is None
    assert hook.verdict(f"gh pr merge --match-head-commit={SHA} 77", repo) is None
    assert hook.verdict(f"gh pr merge https://github.com/o/r/pull/77 --match-head-commit {SHA[:12]}", repo) is None
    denied = hook.verdict("gh pr merge 77 --merge", repo)
    assert denied is not None and SHA in denied and "--match-head-commit" in denied
    assert hook.verdict("gh pr merge 77 --match-head-commit deadbeefcafe", repo) is not None
    assert hook.verdict(f"gh pr merge 78 --match-head-commit {SHA}", repo) is not None
    assert hook.verdict("gh pr create", repo) is not None


@pytest.mark.parametrize(
    "command",
    [
        "gh pr merge -R o/r 77 --match-head-commit {sha}",
        "gh pr merge --repo o/r 77 --match-head-commit {sha}",
        'gh pr merge --subject "x y" 77 --match-head-commit {sha}',
        "gh pr merge --body 123 77 --match-head-commit {sha}",
        "gh pr merge -b 123 77 --match-head-commit {sha}",
        "gh --repo o/r pr merge 77 --merge --delete-branch --match-head-commit {sha}",
    ],
)
def test_merge_flag_values_are_not_taken_for_the_pr_number(repo, command):
    stamp.record_landed(repo, "77", SHA)
    assert hook.verdict(command.format(sha=SHA), repo) is None


def test_body_value_is_not_the_pr(repo):
    stamp.record_landed(repo, "123", SHA)
    assert hook.verdict(f"gh pr merge --body 123 --match-head-commit {SHA}", repo) is not None


def test_bare_merge_is_denied_with_a_clear_message(repo):
    stamp.record_landed(repo, "77", SHA)
    reason = hook.verdict("gh pr merge --merge", repo)
    assert reason is not None and "pull request number" in reason


def test_landed_record_survives_overnight_and_expires_after_72_hours(repo):
    stamp.record_landed(repo, "5", SHA, now=time.time() - 40 * 3600)
    assert stamp.landed_head(repo, "5") == SHA
    stamp.record_landed(repo, "6", SHA, now=time.time() - stamp.LANDED_MAX_AGE_SECONDS - 60)
    assert stamp.landed_head(repo, "6") is None


def test_record_landed_prunes_expired_merges_and_leaves_no_temp_or_lock(repo):
    stamp.record_landed(repo, "1", SHA, now=time.time() - stamp.LANDED_MAX_AGE_SECONDS - 60)
    stamp.record_landed(repo, "2", SHA)
    stamp.record_landed(repo, "3", SHA)
    data = json.loads(stamp.landed_path(repo).read_text(encoding="utf-8"))
    assert sorted(data) == ["2", "3"]
    assert sorted(p.name for p in (repo / ".git").glob("landed-prs*")) == ["landed-prs.json"]


def test_concurrent_record_landed_loses_nothing(repo):
    import threading

    threads = [threading.Thread(target=stamp.record_landed, args=(repo, str(n), SHA)) for n in range(1, 13)]
    for th in threads:
        th.start()
    for th in threads:
        th.join()
    data = json.loads(stamp.landed_path(repo).read_text(encoding="utf-8"))
    assert sorted(data, key=int) == [str(n) for n in range(1, 13)]


def test_a_stale_lock_does_not_wedge_record_landed(repo):
    lock = stamp.landed_path(repo).with_name("landed-prs.lock")
    lock.write_text("x", encoding="utf-8")
    old = time.time() - 300
    os.utime(lock, (old, old))
    stamp.record_landed(repo, "9", SHA)
    assert stamp.landed_head(repo, "9") == SHA


def test_powershell_env_bypass_allows_and_is_logged(repo):
    assert hook.verdict('$env:DEG_SKIP_PREMERGE="hotfix ps"; gh pr create --title x', repo) is None
    assert "hotfix ps" in (repo / ".git" / "premerge-bypass.log").read_text(encoding="utf-8")
    assert hook.verdict("$env:DEG_SKIP_PREMERGE = 'x'; gh pr merge 3", repo) is None


def test_deny_message_shows_both_bypass_syntaxes(repo):
    reason = hook.verdict("gh pr create", repo)
    assert 'DEG_SKIP_PREMERGE="<reason>" gh pr create' in reason
    assert '$env:DEG_SKIP_PREMERGE="<reason>"; gh pr create' in reason


def test_settings_registers_the_hook():
    settings = json.loads((SCRIPTS_DIR.parent / ".claude" / "settings.json").read_text(encoding="utf-8"))
    commands = [h["command"] for entry in settings["hooks"]["PreToolUse"] for h in entry["hooks"]]
    assert any('run_hook.py" require_premerge_before_pr' in c for c in commands)


def test_record_landed_publishes_with_an_atomic_replace(repo, monkeypatch):
    calls = []
    real = os.replace
    monkeypatch.setattr(stamp.os, "replace", lambda src, dst: (calls.append((Path(src).name, Path(dst).name)), real(src, dst))[1])
    stamp.record_landed(repo, "4", SHA)
    assert len(calls) == 1 and calls[0][1] == "landed-prs.json" and calls[0][0].endswith(".tmp")


# --- round 2 ---------------------------------------------------------------------------------------


@pytest.mark.parametrize(
    "command",
    [
        "gh pr merge 77 `\n  --match-head-commit {sha}",
        "gh pr merge 77 \\\n  --merge \\\n  --match-head-commit {sha}",
        "gh pr merge 77 `\r\n  --match-head-commit {sha}",
    ],
)
def test_line_continuations_join_before_tokenizing(repo, command):
    stamp.record_landed(repo, "77", SHA)
    assert hook.verdict(command.format(sha=SHA), repo) is None
    assert hook.verdict(command.format(sha="deadbeefcafe"), repo) is not None


@pytest.mark.parametrize(
    "command",
    [
        "gh pr merge $pr --match-head-commit {sha}",
        "gh pr merge 77 --match-head-commit $sha",
        "gh pr merge 77 --match-head-commit (git rev-parse HEAD)",
        "gh pr merge 77 --match-head-commit $(git rev-parse HEAD)",
        'gh pr merge "$pr" --match-head-commit {sha}',
    ],
)
def test_non_literal_target_or_head_is_denied_with_a_literal_values_message(repo, command):
    stamp.record_landed(repo, "77", SHA)
    reason = hook.verdict(command.format(sha=SHA), repo)
    assert reason is not None and "literal" in reason


def test_parentheses_in_args_do_not_split_the_command(repo):
    stamp.record_landed(repo, "77", SHA)
    assert hook.verdict(f"gh pr merge 77 --match-head-commit {SHA} --subject (Get-Date)", repo) is None
    assert hook.verdict(f"gh pr merge 77 --match-head-commit {SHA} --subject $(date)", repo) is None
    assert hook.verdict("(gh pr create)", repo) is not None
    assert hook.verdict("echo $(gh pr create)", repo) is not None


def test_os_replace_is_retried_on_permission_error(repo, monkeypatch):
    real, fails = os.replace, [3]

    def flaky(src, dst):
        if fails[0] > 0:
            fails[0] -= 1
            raise PermissionError("held open")
        return real(src, dst)

    monkeypatch.setattr(stamp.os, "replace", flaky)
    monkeypatch.setattr(stamp.time, "sleep", lambda _s: None)
    stamp.record_landed(repo, "4", SHA)
    assert stamp.landed_head(repo, "4") == SHA


def test_os_replace_that_never_succeeds_raises_and_leaves_no_temp(repo, monkeypatch):
    def always(src, dst):
        raise PermissionError("held open")

    monkeypatch.setattr(stamp.os, "replace", always)
    monkeypatch.setattr(stamp.time, "sleep", lambda _s: None)
    with pytest.raises(PermissionError):
        stamp.record_landed(repo, "4", SHA)
    assert sorted(p.name for p in (repo / ".git").glob("landed-prs*")) == []


def test_unreadable_landed_file_is_no_record_not_a_crash(repo, monkeypatch):
    def boom(_repo):
        raise OSError("disk gone")

    monkeypatch.setattr(stamp, "landed_path", boom)
    assert stamp.landed_head(repo, "77") is None
    assert hook.verdict(f"gh pr merge 77 --match-head-commit {SHA}", repo) is not None


def test_lock_waiter_outlasts_the_stale_age():
    assert stamp._LOCK_WAIT_SECONDS > stamp._LOCK_STALE_SECONDS


def test_stale_lock_takeover_renames_it_away_first(repo, monkeypatch):
    lock = stamp.landed_path(repo).with_name("landed-prs.lock")
    lock.write_text("x", encoding="utf-8")
    old = time.time() - 300
    os.utime(lock, (old, old))
    renames = []
    real = os.rename
    monkeypatch.setattr(stamp.os, "rename", lambda a, b: (renames.append((Path(a).name, Path(b).name)), real(a, b))[1])
    stamp.record_landed(repo, "9", SHA)
    assert len(renames) == 1 and renames[0][0] == "landed-prs.lock" and renames[0][1] != "landed-prs.lock"
    assert sorted(p.name for p in (repo / ".git").glob("landed-prs*")) == ["landed-prs.json"]


def test_a_fresh_lock_is_never_taken_over(repo, monkeypatch):
    lock = stamp.landed_path(repo).with_name("landed-prs.lock")
    lock.write_text("x", encoding="utf-8")
    monkeypatch.setattr(stamp, "_LOCK_WAIT_SECONDS", 0.2)
    with pytest.raises(TimeoutError):
        stamp.record_landed(repo, "9", SHA)
    assert lock.exists()


@pytest.mark.parametrize("sha", ["abc", "a1b2c3d4e5f6", "z" * 40, SHA + "0", ""])
def test_record_landed_refuses_anything_but_a_full_hex_sha(repo, sha):
    with pytest.raises(ValueError):
        stamp.record_landed(repo, "77", sha)
    assert not stamp.landed_path(repo).exists()


@pytest.mark.parametrize(
    "command",
    [
        'echo "$env:DEG_SKIP_PREMERGE=x"; gh pr create',
        "gh issue comment 1 --body '$env:DEG_SKIP_PREMERGE=\"x\"'; gh pr create",
        "Write-Output \"$env:DEG_SKIP_PREMERGE = 'x'\"; gh pr merge 3",
    ],
)
def test_quoted_powershell_bypass_text_is_not_a_bypass(repo, command):
    assert hook.verdict(command, repo) is not None


@pytest.mark.parametrize(
    "command",
    ['$env:DEG_SKIP_PREMERGE="why"; gh pr create', "$env:DEG_SKIP_PREMERGE = 'why'; gh pr create", "$env:deg_skip_premerge=why; gh pr create"],
)
def test_unquoted_powershell_bypass_still_works(repo, command):
    assert hook.verdict(command, repo) is None
