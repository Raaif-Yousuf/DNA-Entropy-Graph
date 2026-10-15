"""Tests for scripts/hooks/block_primary_checkout_git.py (issue #542).

The hook denies history-changing git when the effective directory is the
PRIMARY checkout. Every case runs against a REAL throwaway repo (never this
repo) with a real linked worktree, so "primary" and "linked worktree" are
git's own answer, not a path pattern.
"""

from __future__ import annotations

import json
import os
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
    # review round 2: only the exact recovery shapes switch branches
    "git checkout -bfeat main",
    "git switch -cfeat main",
    "git switch --create=feat main",
    "git switch --create feat main",
    "git switch --det main",
    "git checkout -b main origin/main",
    "git checkout -fB main origin/main",
    "git checkout -B main origin/main -t",
    "git switch -C main origin/main --track",
    "git checkout --orphan main",
    # last of --ff-only / --ff / --no-ff wins, as in git
    "git merge --ff-only --no-ff feat",
    "git merge --ff-only --ff feat",
    "git pull --ff-only --no-ff",
    # heredoc scanner: here-strings, quotes, arithmetic and a missing terminator hide nothing
    "cat <<'EOF' | bash\ngit switch -c x\nEOF",
    "cat <<< hi\ngit switch -c x",
    "echo $((1<<2))\ngit switch -c x",
    "echo '<<EOF'\ngit switch -c x\nEOF",
    "cat <<EOF\ngit switch -c x",
    # pwsh flag prefixes
    "pwsh -co 'git switch -c x'",
    'powershell -Comm "git switch -c x"',
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
    "git merge --no-ff --ff-only feat",
    "git pull --no-ff --ff-only",
    "git checkout -q main",
    "git switch --quiet main",
    "git switch --force main",
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


@pytest.mark.skipif(os.name != "nt", reason="a backslash path is only a path on Windows (#662)")
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
        timeout=600,  # a hang guard only; no assertion depends on elapsed time (#629)
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


# ---------------------------------------------------------------------------
# review round 2
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "prefix",
    [
        "GIT_DIR={p}/.git",
        "GIT_WORK_TREE={p}",
        "env GIT_DIR={p}/.git",
        "env -i GIT_WORK_TREE={p}",
    ],
)
def test_git_dir_env_assignments_are_classified(repos, prefix):
    primary, worktree = repos
    command = f"{prefix.format(p=_in(primary))} git switch -c x"
    assert hook.verdict(command, _in(worktree)) is not None


def test_total_git_time_budget_fails_open(repos, monkeypatch):
    primary, worktree = repos
    calls = []

    def counting(directory, timeout=10.0):
        calls.append(timeout)
        return "primary"

    monkeypatch.setattr(hook, "classify", counting)
    monkeypatch.setattr(hook, "TOTAL_BUDGET_SECONDS", 0.0)
    assert hook.verdict("git switch -c x", _in(primary)) is None
    assert calls == []


def test_each_classify_timeout_is_capped_by_the_remaining_budget(repos, monkeypatch):
    primary, worktree = repos
    calls = []

    def counting(directory, timeout=10.0):
        calls.append(timeout)
        return "linked_worktree"

    monkeypatch.setattr(hook, "classify", counting)
    monkeypatch.setattr(hook, "TOTAL_BUDGET_SECONDS", 1.0)
    hook.verdict(f"git -C {_in(primary)} commit -m a; git -C {_in(worktree)} commit -m b", _in(worktree))
    assert len(calls) == 2
    assert all(timeout <= 1.0 for timeout in calls)


def test_budget_is_shorter_than_the_dispatcher_kill():
    assert hook.TOTAL_BUDGET_SECONDS < 8.0


# ---------------------------------------------------------------------------
# #629 follow-ups
# ---------------------------------------------------------------------------

DENIED_629 = [
    # (M, bypass) an apostrophe in a `#` comment must not open a quote
    "# don't\ngit switch -c x",
    "echo hi # it's fine\ngit switch -c x",
    "git status # don't\ngit commit -m x",
    # shell consumers of a heredoc: the body is executed
    "/bin/bash <<EOF\ngit switch -c x\nEOF",
    '"C:\\Program Files\\Git\\bin\\bash.exe" <<EOF\ngit switch -c x\nEOF',
    "'/usr/bin/bash' <<'EOF'\ngit switch -c x\nEOF",
    ". /dev/stdin <<EOF\ngit switch -c x\nEOF",
    "source /dev/stdin <<'EOF'\ngit switch -c x\nEOF",
    "eval \"$(cat <<'EOF'\ngit switch -c x\nEOF\n)\"",
    'eval "git switch -c x"',
    # a `#` word INSIDE a backtick span ends at the closing backtick, not at the end of the line
    "echo `foo #bar`; git switch -c x",
    "echo `foo #bar` && git switch -c x",
    # a `#` right after `$( ... )` continues the word; it is not a comment start
    "echo $(true)#; git switch -c x",
    "echo $(echo a)#x\ngit switch -c x",
    "echo $((1+1))#; git switch -c x",
    # no space before the heredoc operator
    "bash<<EOF\ngit switch -c x\nEOF",
    "sh<<'EOF'\ngit switch -c x\nEOF",
    "/bin/bash<<-EOF\ngit switch -c x\nEOF",
    ". /dev/stdin<<EOF\ngit switch -c x\nEOF",
    # git am recovery covers only --abort/--quit/--show-current-patch; --skip applies more patches
    "git am --skip",
    "git am --continue",
    # git global options that take a value
    "git --config-env a=B switch -c x",
    "git --config-env=a=B switch -c x",
    # abbreviated reset modes
    "git reset --har HEAD",
    "git reset --so HEAD~1",
    "git reset --mi HEAD~1",
    "git reset --kee HEAD~1",
]

ALLOWED_629 = [
    "git am --abort",
    "git am --quit",
    "git am --show-current-patch",
    "# don't\ngit status",
    "git log --grep='#1'",
    "echo a#b\ngit status",
    "git --config-env a=B status",
    "git reset --quiet HEAD README.md",
    # a `#` at a word start IS a comment, also right after a plain subshell or `$( )`
    "(true) # it's\ngit status",
    "echo $(true) # don't\ngit status",
    "echo $(true); # don't\ngit status",
    "cat<<EOF\ngit switch -c x\nEOF",
]


@pytest.mark.parametrize("command", DENIED_629)
def test_629_denies_in_primary(repos, command):
    primary, worktree = repos
    assert hook.verdict(command, _in(primary)) is not None
    assert hook.verdict(command, _in(worktree)) is None


@pytest.mark.parametrize("command", ALLOWED_629)
def test_629_allows_in_primary(repos, command):
    primary, _ = repos
    assert hook.verdict(command, _in(primary)) is None


def test_629_vacuity_guard():
    assert len(DENIED_629) >= 15 and len(ALLOWED_629) >= 6


@pytest.mark.parametrize(
    "template",
    [
        "env -C {p} git switch -c x",
        "env --chdir={p} git switch -c x",
        "env --chdir {p} git switch -c x",
        "env -i -C {p} git switch -c x",
        "pwsh -WorkingDirectory {p} -Command 'git switch -c x'",
        "pwsh -wd {p} -Command 'git switch -c x'",
        "pwsh -NoProfile -WorkingDirectory {p} -c 'git switch -c x'",
    ],
)
def test_629_directory_changing_wrappers_into_primary_deny(repos, template):
    primary, worktree = repos
    assert hook.verdict(template.format(p=_in(primary)), _in(worktree)) is not None
    # and out of the primary into a worktree allows
    assert hook.verdict(template.format(p=_in(worktree)), _in(primary)) is None


def test_629_env_chdir_does_not_persist_to_the_next_segment(repos):
    primary, worktree = repos
    command = f"env -C {_in(primary)} true; git switch -c x"
    assert hook.verdict(command, _in(worktree)) is None


# -- the Windows pipe hang: a child that outlives its parent holding the pipes --

_GRANDCHILD = (
    "import os, sys, time\n"
    "release, pidfile = sys.argv[1], sys.argv[2]\n"
    "open(pidfile, 'w').write(str(os.getpid()))\n"
    "deadline = time.time() + 90\n"
    "while not os.path.exists(release) and time.time() < deadline:\n"
    "    time.sleep(0.05)\n"
)
_PARENT = (
    "import subprocess, sys, time\n"
    "subprocess.Popen([sys.executable, '-c', sys.argv[1], sys.argv[2], sys.argv[3]])\n"
    "time.sleep(90)\n"
)


def _pid_alive(pid: int) -> bool:
    if sys.platform == "win32":
        out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True).stdout
        return str(pid) in out
    import os

    try:
        os.kill(pid, 0)
    except OSError:
        return False
    return True


def test_629_run_bounded_returns_on_timeout_and_kills_the_grandchild(tmp_path):
    """The hang shape: a parent that starts a grandchild (which inherits the stdout
    handle) and then stalls. Nothing here depends on the clock. `run_bounded` takes
    its wait as a seam: the test's waiter blocks on a SIGNAL (the grandchild wrote its
    pid) and then reports the timeout by waiting 0.2 s on a parent that sleeps 90 s,
    so the timeout always fires and the kill path always runs, however slow the box.
    The 60 s bound exists only so a regression fails instead of hanging the suite."""
    import threading
    import time

    release, pidfile = tmp_path / "release", tmp_path / "pid"

    def expire_once_the_grandchild_runs(proc, _timeout):
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline and proc.poll() is None:
            if pidfile.exists() and pidfile.stat().st_size > 0:
                break
            time.sleep(0.05)
        proc.wait(timeout=0.2)  # the parent sleeps 90 s: always raises TimeoutExpired

    result: list = []
    argv = [sys.executable, "-c", _PARENT, _GRANDCHILD, str(release), str(pidfile)]
    worker = threading.Thread(
        target=lambda: result.append(agent_hook.run_bounded(argv, timeout=1.0, _wait=expire_once_the_grandchild_runs)),
        daemon=True,
    )
    worker.start()
    worker.join(60)
    hung = worker.is_alive()
    grandchild_pid = int(pidfile.read_text()) if pidfile.exists() and pidfile.stat().st_size else None
    alive_after = False
    if not hung and grandchild_pid is not None:
        for _ in range(100):  # let the OS reap the killed tree
            if not _pid_alive(grandchild_pid):
                break
            time.sleep(0.1)
        alive_after = _pid_alive(grandchild_pid)
    release.write_text("go")  # always release, so a failing run leaves nothing behind
    assert not hung, "run_bounded blocked on the grandchild that holds the stdout handle"
    assert result == [None]
    assert grandchild_pid is not None, "vacuity: the grandchild never started"
    assert not alive_after, "the grandchild survived the timeout"


def test_629_run_bounded_returns_output_and_code():
    code, out = agent_hook.run_bounded([sys.executable, "-c", "print('hi')"], timeout=60)
    assert code == 0 and out.strip() == "hi"
    code, _ = agent_hook.run_bounded([sys.executable, "-c", "raise SystemExit(3)"], timeout=60)
    assert code == 3
    assert agent_hook.run_bounded(["definitely-not-a-real-binary-629"], timeout=5) is None


def test_629_stalled_git_classifies_unknown_and_allows(repos, monkeypatch):
    """A timeout inside classify is 'unknown', which allows (fail open, on purpose)."""
    primary, _ = repos
    monkeypatch.setattr(agent_hook, "run_bounded", lambda argv, timeout: None)
    assert agent_hook.classify(_in(primary), timeout=1) == "unknown"
    assert hook.verdict("git switch -c x", _in(primary)) is None


# -- the dispatcher kills a guard at 8 s: every worst case must fit, with margin --


def _run_hook_budget() -> float:
    sys.path.insert(0, str(Path(agent_hook.__file__).parent))
    import run_hook

    return run_hook.DEFAULT_GUARD_TIMEOUT_SECONDS


def test_629_budget_mirrors_the_dispatcher_kill():
    assert _run_hook_budget() == agent_hook.DISPATCHER_BUDGET_SECONDS


def test_629_worst_case_of_one_bounded_git_includes_the_kill_tail():
    assert agent_hook.KILL_TAIL_SECONDS == agent_hook.TASKKILL_TIMEOUT_SECONDS + agent_hook.POST_KILL_WAIT_SECONDS
    assert agent_hook.worst_case_seconds(2.0) == 2.0 + agent_hook.KILL_TAIL_SECONDS


MARGIN = 1.0  # interpreter start-up and the JSON read, never measured, so reserved


def test_629_dispatch_hook_worst_case_fits_the_dispatcher_budget():
    # verdict() runs exactly one bounded git (no second _git_paths call anywhere).
    worst = agent_hook.worst_case_seconds(agent_hook.GIT_TIMEOUT)
    assert worst + MARGIN <= agent_hook.DISPATCHER_BUDGET_SECONDS


def test_629_primary_checkout_hook_worst_case_fits_the_dispatcher_budget():
    # The deadline caps each timeout to the remaining budget, and a kill tail can follow
    # the last call, so the worst case is the budget plus one tail.
    worst = hook.TOTAL_BUDGET_SECONDS + agent_hook.KILL_TAIL_SECONDS
    assert worst + MARGIN <= agent_hook.DISPATCHER_BUDGET_SECONDS
    assert hook.CLASSIFY_TIMEOUT_SECONDS <= hook.TOTAL_BUDGET_SECONDS


def test_629_explain_runs_one_git_not_two(repos, monkeypatch):
    primary, _ = repos
    calls = []
    real = agent_hook._git_paths

    def counting(cwd, timeout=agent_hook.GIT_TIMEOUT):
        calls.append(timeout)
        return real(cwd, timeout)

    monkeypatch.setattr(agent_hook, "_git_paths", counting)
    assert "PRIMARY CHECKOUT" in agent_hook.explain(_in(primary))
    assert len(calls) == 1
