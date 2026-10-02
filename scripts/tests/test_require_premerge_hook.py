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
        capture_output=True, text=True, timeout=60, check=False,
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


@pytest.mark.parametrize("command", ["gh pr create --title x", "gh pr merge 3 --merge --delete-branch",
                                     "cd app && gh pr create", "GH_TOKEN=x gh pr create", "gh -R a/b pr merge 1"])
def test_denied_without_a_stamp(repo, command):
    reason = hook.verdict(command, repo)
    assert reason is not None
    assert "premerge.py" in reason


@pytest.mark.parametrize("command", ["gh pr view 3", "gh pr checks 3", "gh issue create -t x", "grep 'gh pr create' x.md",
                                     "echo gh pr merge is blocked", ""])
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
        capture_output=True, text=True, timeout=30, check=False,
    )
    assert proc.returncode == 0
    assert json.loads(proc.stdout)["hookSpecificOutput"]["permissionDecision"] == "deny"
