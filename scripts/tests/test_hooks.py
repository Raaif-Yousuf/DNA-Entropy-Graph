"""Tests for scripts/hooks/*.py.

Every hook here is a PreToolUse guard: JSON payload in on stdin, a JSON deny
decision (or silence) out on stdout, exit 0 either way (see each hook's own
module docstring for why exit 0 always -- a hook that fails the session over
its own bug is worse than the thing it guards).

Per the AGENT_BRIEF for this port: these are tested with crafted stdin
payloads, never by wiring the hook into a real Claude Code session and
triggering it live. Most tests call each hook's pure `verdict()` function
directly (fast, and the real unit under test); a `test_*_end_to_end` test per
hook additionally drives the real subprocess contract (stdin JSON in, stdout
JSON out) at least once, to prove the stdin/stdout wiring itself and not just
the decision function.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

HOOKS_DIR = Path(__file__).resolve().parent.parent / "hooks"
sys.path.insert(0, str(HOOKS_DIR))

import block_agent_dispatch_in_worktree as agent_hook  # noqa: E402
import block_git_stash as stash_hook  # noqa: E402
import block_recursive_delete as rm_hook  # noqa: E402
import block_unlabelled_vm_create as vm_hook  # noqa: E402


def _run_hook(hook_path: Path, payload: dict) -> tuple[int, dict | None]:
    """Invoke a hook the real way: JSON on stdin, parse whatever JSON (if
    any) it writes to stdout. Returns (exit_code, parsed_stdout_or_None)."""
    proc = subprocess.run(
        [sys.executable, str(hook_path)],
        input=json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=15,
    )
    out = None
    if proc.stdout.strip():
        out = json.loads(proc.stdout)
    return proc.returncode, out


def _denied(decision: dict | None) -> bool:
    if decision is None:
        return False
    return (
        decision.get("hookSpecificOutput", {}).get("permissionDecision") == "deny"
    )


# ---------------------------------------------------------------------------
# block_git_stash.py
# ---------------------------------------------------------------------------

@pytest.mark.parametrize(
    "command",
    [
        "git stash",
        "git stash push -m wip",
        "git stash pop",
        "git stash drop",
        "GIT_PAGER=cat git stash",
        "git -C worker stash",
        "cd worker && git stash",
    ],
)
def test_block_git_stash_denies_mutating_subcommands(command):
    assert stash_hook.verdict(command) is not None


@pytest.mark.parametrize(
    "command",
    [
        "git stash list",
        "git stash show",
        "grep 'git stash' README.md",
        "echo 'never run git stash here'",
        "",
    ],
)
def test_block_git_stash_allows_read_only_and_prose(command):
    assert stash_hook.verdict(command) is None


def test_block_git_stash_ignores_cat_heredoc_body():
    # A commit message heredoc that quotes the ban is data `cat` echoes back,
    # not a command -- see the hook's own module docstring.
    command = (
        "git commit -m \"$(cat <<'EOF'\n"
        "fix: document why we never git stash here\n"
        "EOF\n"
        ")\""
    )
    assert stash_hook.verdict(command) is None


def test_block_git_stash_end_to_end_denies():
    code, decision = _run_hook(
        HOOKS_DIR / "block_git_stash.py",
        {"tool_name": "Bash", "tool_input": {"command": "git stash"}},
    )
    assert code == 0  # the hook itself always exits 0; the deny is IN the JSON
    assert _denied(decision)


def test_block_git_stash_fails_open_on_malformed_stdin(tmp_path):
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "block_git_stash.py")],
        input="not json at all",
        capture_output=True,
        text=True,
        timeout=15,
    )
    assert proc.returncode == 0
    assert proc.stdout.strip() == ""


# ---------------------------------------------------------------------------
# block_recursive_delete.py
# ---------------------------------------------------------------------------

@pytest.mark.parametrize(
    "command",
    [
        "rm -rf .scratch",
        "rm -fr .scratch/",
        "rm -r -f .scratch",
        "rm --recursive --force .scratch",
        "Remove-Item -Recurse -Force .scratch",
        "ri -Recurse -Force .scratch",
        "Remove-Item -Recurse -For .scratch",  # PowerShell prefix matching
    ],
)
def test_block_recursive_delete_denies_inside_repo(command):
    assert rm_hook.verdict(command) is not None


@pytest.mark.parametrize(
    "command",
    [
        "rm .scratch/one_file.log",
        "rm -rf /tmp/some-scratch-dir",
        "git clean -fd",
        "git checkout -- .scratch/",
        "rm -rf",  # no target
    ],
)
def test_block_recursive_delete_allows_named_files_and_outside_repo(command):
    assert rm_hook.verdict(command) is None


def test_block_recursive_delete_end_to_end_denies():
    code, decision = _run_hook(
        HOOKS_DIR / "block_recursive_delete.py",
        {"tool_name": "Bash", "tool_input": {"command": "rm -rf .scratch"}},
    )
    assert code == 0
    assert _denied(decision)


# ---------------------------------------------------------------------------
# block_agent_dispatch_in_worktree.py
# ---------------------------------------------------------------------------

@pytest.fixture()
def git_repo_with_worktree(tmp_path):
    """A throwaway git repo (NOT this repo) with one commit and one linked
    worktree, so classify()/verdict() can be exercised against a REAL
    primary checkout and a REAL linked worktree without touching
    DNA-Entropy-Graph's own working tree at all."""

    def run(*args, cwd):
        subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)

    primary = tmp_path / "primary"
    primary.mkdir()
    run("init", "-q", cwd=primary)
    run("config", "user.email", "test@example.com", cwd=primary)
    run("config", "user.name", "Test", cwd=primary)
    (primary / "README.md").write_text("hello\n", encoding="utf-8")
    run("add", "README.md", cwd=primary)
    run("commit", "-q", "-m", "initial", cwd=primary)

    worktree = tmp_path / "primary-wt" / "branch-a"
    worktree.parent.mkdir(parents=True, exist_ok=True)
    run("worktree", "add", "-q", "-b", "branch-a", str(worktree), cwd=primary)

    return primary, worktree


def test_agent_dispatch_allows_primary_checkout(git_repo_with_worktree):
    primary, _worktree = git_repo_with_worktree
    assert agent_hook.classify(str(primary)) == "primary"
    assert agent_hook.verdict(str(primary)) is None


def test_agent_dispatch_denies_linked_worktree(git_repo_with_worktree):
    _primary, worktree = git_repo_with_worktree
    assert agent_hook.classify(str(worktree)) == "linked_worktree"
    assert agent_hook.verdict(str(worktree)) is not None


def test_agent_dispatch_fails_open_on_unresolvable_cwd(tmp_path):
    not_a_repo = tmp_path / "not-a-repo"
    not_a_repo.mkdir()
    assert agent_hook.classify(str(not_a_repo)) == "unknown"
    # Doubt means allow: never block the orchestrator on a guess.
    assert agent_hook.verdict(str(not_a_repo)) is None


def test_agent_dispatch_explain_distinguishes_all_three_branches(git_repo_with_worktree, tmp_path):
    primary, worktree = git_repo_with_worktree
    not_a_repo = tmp_path / "definitely-not-a-repo"
    not_a_repo.mkdir()

    assert "PRIMARY CHECKOUT" in agent_hook.explain(str(primary))
    assert "LINKED WORKTREE" in agent_hook.explain(str(worktree))
    assert "UNKNOWN" in agent_hook.explain(str(not_a_repo))


def test_agent_dispatch_end_to_end_denies_from_worktree(git_repo_with_worktree):
    _primary, worktree = git_repo_with_worktree
    code, decision = _run_hook(
        HOOKS_DIR / "block_agent_dispatch_in_worktree.py",
        {"tool_name": "Agent", "tool_input": {}, "cwd": str(worktree)},
    )
    assert code == 0
    assert _denied(decision)


def test_agent_dispatch_end_to_end_allows_from_primary(git_repo_with_worktree):
    primary, _worktree = git_repo_with_worktree
    code, decision = _run_hook(
        HOOKS_DIR / "block_agent_dispatch_in_worktree.py",
        {"tool_name": "Agent", "tool_input": {}, "cwd": str(primary)},
    )
    assert code == 0
    assert decision is None


# ---------------------------------------------------------------------------
# block_unlabelled_vm_create.py
# ---------------------------------------------------------------------------

@pytest.mark.parametrize(
    "command",
    [
        "gcloud compute instances create deg-job-1 --zone us-central1-a",
        "gcloud compute instances create deg-job-1 --labels=app=dna-entropy-graph",  # missing max-run-duration
        "gcloud compute instances create deg-job-1 --max-run-duration=14400s",  # missing labels
        "CloudCli vm create --zone us-central1-a",
        "CloudCli.exe vm create --labels app=x",
    ],
)
def test_block_unlabelled_vm_create_denies_missing_flags(command):
    assert vm_hook.verdict(command) is not None


@pytest.mark.parametrize(
    "command",
    [
        "gcloud compute instances create deg-job-1 --labels=app=dna-entropy-graph,job-id=1 "
        "--max-run-duration=14400s",
        "CloudCli vm create --labels app=x --max-run-duration 14400s",
        "gcloud compute instances list",
        "gcloud compute instances delete deg-job-1",
        "echo 'gcloud compute instances create is blocked without labels'",  # prose, not command position
    ],
)
def test_block_unlabelled_vm_create_allows_when_flags_present_or_not_a_create(command):
    assert vm_hook.verdict(command) is None


def test_block_unlabelled_vm_create_end_to_end_denies():
    code, decision = _run_hook(
        HOOKS_DIR / "block_unlabelled_vm_create.py",
        {
            "tool_name": "Bash",
            "tool_input": {"command": "gcloud compute instances create deg-job-1"},
        },
    )
    assert code == 0
    assert _denied(decision)


# ---------------------------------------------------------------------------
# --self-test (issue #310): every hook here already has its failing/passing
# arm proven above via crafted stdin; this section proves the standalone
# `--self-test` flag itself works, runnable with no pytest on PATH, matching
# scripts/check_*.py and scripts/hooks/run_hook.py's own shape.
# ---------------------------------------------------------------------------

@pytest.mark.parametrize(
    "hook_name",
    [
        "block_git_stash.py",
        "block_recursive_delete.py",
        "block_unlabelled_vm_create.py",
        "block_agent_dispatch_in_worktree.py",
    ],
)
def test_hook_self_test_flag_passes(hook_name):
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / hook_name), "--self-test"],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout
