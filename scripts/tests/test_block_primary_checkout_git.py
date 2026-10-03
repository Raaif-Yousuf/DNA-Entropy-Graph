"""Tests for scripts/hooks/block_primary_checkout_git.py (issue #542).

The hook denies history-changing git when the effective directory is the
PRIMARY checkout. Every case runs against a REAL throwaway repo (never this
repo) with a real linked worktree, so "primary" and "linked worktree" are
git's own answer, not a path pattern.
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
import block_primary_checkout_git as hook  # noqa: E402

DENIED = [
    "git checkout feat/x",
    "git checkout -b feat/x origin/main",
    "git checkout -B feat/x origin/main",
    "git checkout feat/main",
    "git switch feat/x",
    "git switch -c x origin/main",
    "git switch -C x origin/main",
    "git merge feat/x",
    "git merge --no-ff feat/x",
    "git commit -m wip",
    "git commit --amend --no-edit",
    "git reset --hard origin/main",
    "git reset --hard",
    "git reset --soft HEAD~1",
    "git reset --mixed HEAD~1",
    "git reset HEAD~1",
    "git reset abc1234",
    "git rebase origin/main",
    "git cherry-pick abc1234",
    "git revert abc1234",
    "git pull",
    "git pull origin main",
    "git pull --rebase",
    "git am patch.mbox",
    "git --no-pager switch -c x",
    "git -c core.editor=true commit",
    "GIT_PAGER=cat git switch -c x",
    "rtk git switch -c x",
    "git fetch origin && git switch -c x origin/main",
    "git status; git commit -m x",
    "git log | cat; git checkout feat/x",
    "git status\ngit switch -c x",
    "bash -c 'git switch -c x'",
    "& git switch -c x",
    # review round 1: `--` with no path after it is not a path restore
    "git reset origin/main --",
    "git checkout feat/x --",
    # only `main` is a recovery target, and only from origin/main
    "git checkout -B main origin/feat/x",
    "git switch -C main",
    "git switch -C main origin/feat/x",
    "git switch main --detach",
    "git switch feat/main",
    # wrappers and keywords before the head token
    "if true; then git switch -c x; fi",
    "for i in 1; do git commit -m x; done",
    "true && { git switch -c x; }",
    "command git switch -c x",
    "env GIT_PAGER=cat git switch -c x",
    "env -i git switch -c x",
    "nohup git commit -m x",
    "time git commit -m x",
    "cmd /c git switch -c x",
    'cmd /c "git switch -c x"',
    "bash -lc 'git switch -c x'",
    "sh -lc 'git switch -c x'",
    "bash -ic 'git switch -c x'",
    'pwsh -NoProfile -Command "git switch -c x"',
    # a heredoc fed to a shell is executed, so its body counts
    "bash <<'EOF'\ngit switch -c x\nEOF",
]

ALLOWED = [
    "git merge --ff-only origin/main",
    "git merge --ff-only",
    "git merge --abort",
    "git pull --ff-only",
    "git pull --ff-only origin main",
    "git fetch",
    "git fetch origin",
    "git worktree add ../wt -b feat/x origin/main",
    "git worktree remove ../wt",
    "git worktree list",
    "git worktree prune",
    "git checkout -- README.md",
    "git checkout origin/main -- README.md",
    "git reset -- README.md",
    "git reset HEAD README.md",
    "git reset HEAD -- README.md",
    "git reset",
    "git status",
    "git log --oneline -5",
    "git diff origin/main",
    "git show HEAD",
    "git rev-parse --show-toplevel",
    "git branch --list",
    "git branch --show-current",
    "git merge-base HEAD origin/main",
    "git merge-tree HEAD origin/main",
    "git for-each-ref refs/heads",
    "git reflog",
    "git ls-files",
    "git grep hello",
    "git blame README.md",
    "git config --get user.name",
    "git stash list",  # blocked/allowed by block_git_stash, never by this hook
    "echo 'git switch -c x'",
    "grep 'git commit' README.md",
    "git log --grep='git commit' --oneline",
    "cat <<'EOF'\ngit commit -m x\nEOF",
    "git commit-tree HEAD^{tree}",
    "git status 2>&1",
    "",
    # recovery: the primary checkout can always be put back on main
    "git switch main",
    "git checkout main",
    "git checkout -B main origin/main",
    "git switch -C main origin/main",
    "git checkout -f main",
    # a path restore
    "git checkout .",
    "git checkout -f .",
    "git reset origin/main -- README.md",
    # heredoc bodies are data for any consumer except a shell
    "python - <<'EOF'\ngit commit -m x\nEOF",
    "tee notes.txt <<EOF\ngit switch -c x\nEOF",
    "gh pr create --body-file - <<'EOF'\ngit checkout feat/x\nEOF",
    "cat > f.txt <<-EOF\n\tgit commit -m x\n\tEOF",
]


@pytest.fixture()
def repos(tmp_path):
    primary, worktree = agent_hook._make_git_repo_with_worktree(tmp_path)
    return primary, worktree


def _in(path: Path) -> str:
    return path.as_posix()


def test_denied_vacuity_guard():
    assert len(DENIED) >= 30 and len(ALLOWED) >= 30


@pytest.mark.parametrize("command", DENIED)
def test_denies_in_primary_checkout(repos, command):
    primary, _ = repos
    assert hook.verdict(command, _in(primary)) is not None


@pytest.mark.parametrize("command", ALLOWED)
def test_allows_in_primary_checkout(repos, command):
    primary, _ = repos
    assert hook.verdict(command, _in(primary)) is None


@pytest.mark.parametrize("command", DENIED + ALLOWED)
def test_everything_allowed_in_linked_worktree(repos, command):
    _, worktree = repos
    assert hook.verdict(command, _in(worktree)) is None


def test_deny_message_names_the_worktree_alternative(repos):
    primary, _ = repos
    reason = hook.verdict("git switch -c x origin/main", _in(primary))
    assert "git rev-parse --show-toplevel" in reason
    assert "git worktree add" in reason


def test_git_dash_c_primary_from_a_worktree_cwd_denies(repos):
    primary, worktree = repos
    assert hook.verdict(f'git -C "{_in(primary)}" switch -c x', _in(worktree)) is not None


def test_git_dash_c_worktree_from_a_primary_cwd_allows(repos):
    primary, worktree = repos
    assert hook.verdict(f"git -C {_in(worktree)} switch -c x", _in(primary)) is None


def test_relative_git_dash_c_resolves_against_cwd(repos):
    primary, worktree = repos
    rel = Path("..") / ".." / "primary"
    assert hook.verdict(f"git -C {rel.as_posix()} commit -m x", _in(worktree)) is not None


@pytest.mark.parametrize("prefix", ["cd", "Set-Location", "Set-Location -Path", "Push-Location", "pushd", "sl"])
def test_cd_prefix_into_primary_from_a_worktree_denies(repos, prefix):
    primary, worktree = repos
    for sep in (" && ", "; ", "\n"):
        command = f'{prefix} "{_in(primary)}"{sep}git switch -c x'
        assert hook.verdict(command, _in(worktree)) is not None, command


def test_cd_prefix_into_a_worktree_from_primary_allows(repos):
    primary, worktree = repos
    assert hook.verdict(f"cd {_in(worktree)} && git switch -c x", _in(primary)) is None


def test_pop_location_returns_to_the_previous_directory(repos):
    primary, worktree = repos
    command = f"Push-Location {_in(worktree)}; git switch -c x; Pop-Location; git commit -m y"
    assert hook.verdict(command, _in(primary)) is not None
    command = f"Push-Location {_in(worktree)}; git switch -c x; Pop-Location; git status"
    assert hook.verdict(command, _in(primary)) is None


def test_backslash_windows_path_is_taken_literally(repos):
    """The Git Bash backslash trap: bash would eat the backslashes of an
    unquoted C:\\... path. The hook must treat them as literal path chars."""
    primary, worktree = repos
    win = str(primary).replace("/", "\\")
    assert hook.verdict(f"cd {win}; git switch -c x", _in(worktree)) is not None
    assert hook.verdict(f'git -C "{win}" switch -c x', _in(worktree)) is not None


def test_git_bash_style_drive_path_is_understood(repos):
    primary, worktree = repos
    posix = _in(primary)
    if len(posix) > 2 and posix[1] == ":":
        gitbash = "/" + posix[0].lower() + posix[2:]
        assert hook.verdict(f"cd {gitbash} && git switch -c x", _in(worktree)) is not None


def test_unresolvable_cwd_fails_open(tmp_path):
    not_a_repo = tmp_path / "plain"
    not_a_repo.mkdir()
    assert hook.verdict("git switch -c x", _in(not_a_repo)) is None
    assert hook.verdict("git switch -c x", _in(tmp_path / "missing")) is None


def test_mangled_backslash_cwd_fails_open():
    assert hook.verdict("git switch -c x", "C:Usersnobodyprimary") is None


def test_unresolvable_cd_target_fails_open(repos):
    primary, _ = repos
    assert hook.verdict("cd $SOMEWHERE && git switch -c x", _in(primary)) is None


def _run(payload: dict) -> tuple[int, dict | None]:
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "block_primary_checkout_git.py")],
        input=json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=30,
    )
    return proc.returncode, (json.loads(proc.stdout) if proc.stdout.strip() else None)


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_end_to_end_deny_and_allow_for_both_tools(repos, tool):
    primary, worktree = repos
    deny = {"tool_name": tool, "tool_input": {"command": "git switch -c x origin/main"}, "cwd": _in(primary)}
    code, out = _run(deny)
    assert code == 0
    assert out["hookSpecificOutput"]["permissionDecision"] == "deny"

    allow = {"tool_name": tool, "tool_input": {"command": "git merge --ff-only origin/main"}, "cwd": _in(primary)}
    assert _run(allow) == (0, None)

    in_wt = {"tool_name": tool, "tool_input": {"command": "git switch -c x origin/main"}, "cwd": _in(worktree)}
    assert _run(in_wt) == (0, None)


def test_malformed_payload_fails_open():
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "block_primary_checkout_git.py")],
        input="not json",
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert proc.returncode == 0 and proc.stdout.strip() == ""


def test_internal_error_fails_open(monkeypatch, repos):
    primary, _ = repos

    def boom(*_a, **_k):
        raise RuntimeError("bug in the hook")

    monkeypatch.setattr(hook, "verdict", boom)
    assert hook.decide({"tool_input": {"command": "git switch -c x"}, "cwd": _in(primary)}) is None


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "block_primary_checkout_git.py"), "--self-test"],
        capture_output=True,
        text=True,
        timeout=120,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr


def test_registered_in_settings_for_bash_and_powershell():
    settings = json.loads((HOOKS_DIR.parent.parent / ".claude" / "settings.json").read_text(encoding="utf-8"))
    matchers = [
        entry["matcher"] for entry in settings["hooks"]["PreToolUse"] for h in entry["hooks"] if "block_primary_checkout_git" in h["command"]
    ]
    assert len(matchers) == 1
    assert "Bash" in matchers[0] and "PowerShell" in matchers[0]


# ---------------------------------------------------------------------------
# review round 1
# ---------------------------------------------------------------------------


def test_missing_sibling_module_fails_open_instead_of_crashing(tmp_path):
    """run_hook.py turns a non-zero exit into a deny of EVERY Bash/PowerShell
    call, so an ImportError of a sibling module must exit 0 and stay silent."""
    lone = tmp_path / "block_primary_checkout_git.py"
    lone.write_text((HOOKS_DIR / "block_primary_checkout_git.py").read_text(encoding="utf-8"), encoding="utf-8")
    proc = subprocess.run(
        [sys.executable, str(lone)],
        input=json.dumps({"tool_input": {"command": "git switch -c x"}, "cwd": str(tmp_path)}),
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert proc.returncode == 0
    assert proc.stdout.strip() == ""


def test_deny_message_says_switching_to_main_recovers(repos):
    primary, _ = repos
    assert "git switch main" in hook.verdict("git switch -c x", _in(primary))


def test_classify_is_cached_per_directory_and_uses_a_short_timeout(repos, monkeypatch):
    _primary, worktree = repos
    calls = []

    def counting(directory, timeout=10.0):
        calls.append((directory, timeout))
        return "linked_worktree"

    monkeypatch.setattr(hook, "classify", counting)
    assert hook.verdict("git commit -m a; git commit -m b; git switch -c x", _in(worktree)) is None
    assert len(calls) == 1
    assert all(timeout <= 3 for _, timeout in calls)


def test_agent_hook_classify_accepts_a_timeout(repos):
    primary, _ = repos
    assert agent_hook.classify(_in(primary), timeout=3) == "primary"


def test_glued_dash_C_is_honoured(repos):
    primary, worktree = repos
    assert hook.verdict(f"git -C{_in(primary)} switch -c x", _in(worktree)) is not None
    assert hook.verdict(f"git -C{_in(worktree)} switch -c x", _in(primary)) is None


@pytest.mark.parametrize("form", ["--git-dir={}/.git", "--work-tree={}", "--git-dir {}/.git", "--work-tree {}"])
def test_git_dir_and_work_tree_targets_are_classified(repos, form):
    primary, worktree = repos
    assert hook.verdict(f"git {form.format(_in(primary))} switch -c x", _in(worktree)) is not None


def test_git_dir_of_a_linked_worktree_is_allowed(repos):
    primary, worktree = repos
    gitdir = subprocess.run(
        ["git", "-C", str(worktree), "rev-parse", "--path-format=absolute", "--git-dir"],
        capture_output=True,
        text=True,
        check=True,
    ).stdout.strip()
    assert hook.verdict(f"git --git-dir={Path(gitdir).as_posix()} switch -c x", _in(primary)) is None


def test_unresolvable_cd_does_not_poison_a_later_absolute_dash_C(repos):
    primary, worktree = repos
    assert hook.verdict(f'cd $SOMEWHERE && git -C "{_in(primary)}" switch -c x', _in(worktree)) is not None
    assert hook.verdict(f"cd $SOMEWHERE; cd {_in(primary)}; git switch -c x", _in(worktree)) is not None


def test_unresolvable_relative_target_after_unresolvable_cd_still_fails_open(repos):
    primary, _ = repos
    assert hook.verdict("cd $SOMEWHERE && git -C sub switch -c x", _in(primary)) is None
