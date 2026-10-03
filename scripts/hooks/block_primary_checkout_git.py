"""PreToolUse hook: refuse history-changing git in the PRIMARY checkout.

WHY THIS EXISTS AS CODE AND NOT AS A SENTENCE
---------------------------------------------
MEASURED 2026-10-03 (issue #542): during a three-layer wave every Sonnet was
meant to work in its own worktree. Sonnets from two lanes instead ran
`git checkout main -> feat/51-...`, `-> fix/438-...` and `-> fix/536-...` in the
PRIMARY checkout. It was left on a feature branch and the orchestrator's landing
script then failed its `git merge --ff-only origin/main` there. The primary
checkout is where the orchestrator lands and dispatches from; it must only ever
be on `main`. The rule was in the briefs. This hook is the enforcement, and the
deny message is the replacement (a ban with no alternative loses to the reflex).

HOW IT DECIDES "PRIMARY"
------------------------
Exactly like `block_agent_dispatch_in_worktree.py`, whose `classify()` it
imports: `git rev-parse --git-dir` equals `--git-common-dir` in the primary
checkout and differs in a linked worktree. No path is hard-coded. The directory
asked about is the EFFECTIVE directory of each command segment: the session
`cwd`, moved by a preceding `cd <dir>` / `Set-Location` / `Push-Location` /
`pushd` (and back by `popd` / `Pop-Location`) in the same command, and by
`git -C <path>`. Anything that cannot be resolved (a `$variable` target, a path
that does not exist, git unavailable) is "unknown", which allows: doubt never
blocks.

WHAT IT DENIES (in the primary checkout only)
---------------------------------------------
`checkout` and `switch` (without `--` paths), `merge` (unless `--ff-only`),
`commit`, `am`, `rebase`, `cherry-pick`, `revert`, `pull` (unless `--ff-only`),
and `reset` in any mode that moves HEAD or touches the work tree (`--hard`,
`--soft`, `--mixed`, `--merge`, `--keep`, or a commit other than HEAD).

WHAT IT ALLOWS
--------------
`merge --ff-only` (the orchestrator runs it in the primary checkout after every
landing), `pull --ff-only`, `fetch`, `worktree add/remove/list/prune`,
`checkout [<tree>] -- <paths>`, path-only `reset -- <paths>` / `reset HEAD
<paths>`, `merge|rebase|cherry-pick|revert --abort|--quit` (recovery), and every
read-only command. Anything in a linked worktree. `git stash` is not handled
here: `block_git_stash.py` owns it. Branch deletion/`branch -f` is left alone.

LIMITS (honest)
---------------
This is a command-text check: `bash -c '<cmd>'` / `pwsh -Command '<cmd>'` are
looked into, but a command assembled at runtime, a script that runs git inside
itself, or a tool other than Bash/PowerShell is not seen.

CONTRACT
--------
Reads the PreToolUse payload on stdin, writes a JSON deny decision on stdout, or
stays silent (exit 0) for "no opinion". Registered for both `Bash` and
`PowerShell`. It never blocks on its own failure: a malformed payload or any
unexpected exception exits 0 quietly.

    python scripts/hooks/block_primary_checkout_git.py --self-test

builds a real throwaway repo with a real linked worktree and proves every denied
and allowed shape in both, with no pytest on PATH; exits 0 on pass, 1 on failure.
"""

from __future__ import annotations

import json
import os
import re
import sys

from block_agent_dispatch_in_worktree import classify
from block_git_stash import _strip_cat_heredoc_bodies

MESSAGE = """This is the PRIMARY checkout, and `git {sub}` is blocked here.

The primary checkout is where the orchestrator lands and dispatches from. It must
only ever be on `main`; an agent that switches branches or commits here strands
the next landing.

Work in your own worktree instead. To check where you are:

    git rev-parse --show-toplevel

If that prints the primary checkout, create a worktree and work there:

    git worktree add <path> -b <type>/<issue>-<slug> origin/main

then run your git commands from <path> (or with `git -C <path> ...`).

Still allowed here: `git merge --ff-only`, `git pull --ff-only`, `git fetch`,
`git worktree ...`, `git checkout -- <paths>`, `git reset -- <paths>`, and every
read-only command (status, log, diff, show, rev-parse, branch --list ...)."""

_CD_COMMANDS = {"cd", "chdir", "set-location", "sl"}
_PUSH_COMMANDS = {"pushd", "push-location"}
_POP_COMMANDS = {"popd", "pop-location"}
_SHELLS = {"bash", "sh", "zsh", "dash"}
_POWERSHELLS = {"pwsh", "powershell"}
# Git global options that consume the next argument.
_GIT_VALUE_OPTS = {"-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--super-prefix"}
_ENV_ASSIGN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")
_RESET_MODES = {"--hard", "--soft", "--mixed", "--merge", "--keep"}
_RECOVERY = {"--abort", "--quit"}
_UNKNOWN = None  # an unresolvable directory


# ---------------------------------------------------------------------------
# Tokenising. Backslashes are LITERAL (a C:\\path must survive; this is the Git
# Bash backslash trap in the README), quotes group, `&& || ; | & ( )` and
# newlines separate segments.
# ---------------------------------------------------------------------------


def _split_segments(command: str) -> list[list[str]]:
    segments: list[list[str]] = []
    tokens: list[str] = []
    buf: list[str] = []
    has_buf = False
    quote = ""
    i, n = 0, len(command)

    def end_token() -> None:
        nonlocal buf, has_buf
        if has_buf:
            tokens.append("".join(buf))
        buf, has_buf = [], False

    def end_segment() -> None:
        nonlocal tokens
        end_token()
        if tokens:
            segments.append(tokens)
        tokens = []

    while i < n:
        c = command[i]
        if quote:
            if c == quote:
                quote = ""
            else:
                buf.append(c)
            i += 1
            continue
        if c in "'\"":
            quote, has_buf = c, True
        elif c in " \t\r":
            end_token()
        elif c in "\n;|()":
            end_segment()
        elif c == "&":
            # `&&`, a backgrounding `&`, but not a redirect (`2>&1`, `&>`) and not
            # PowerShell's call operator (handled as a leading token below).
            prev = command[i - 1] if i else ""
            nxt = command[i + 1] if i + 1 < n else ""
            if prev in "<>" or nxt == ">":
                buf.append(c)
                has_buf = True
            else:
                end_segment()
        else:
            buf.append(c)
            has_buf = True
        i += 1
    end_segment()
    return segments


def _resolve(current: str | None, target: str) -> str | None:
    """Resolve a `cd`/`-C` target against `current`; None when it cannot be."""
    if current is _UNKNOWN or not target or target == "-" or any(ch in target for ch in "$`%"):
        return _UNKNOWN
    # Git Bash drive form: /c/Users/x -> C:/Users/x
    if os.name == "nt" and re.match(r"^/[A-Za-z](/|$)", target):
        target = f"{target[1].upper()}:{target[2:] or '/'}"
    target = os.path.expanduser(target)
    return os.path.normpath(os.path.join(current, target))


def _leading_call(tokens: list[str]) -> list[str]:
    """Drop env assignments, PowerShell's `&`, and an `rtk` proxy prefix."""
    i = 0
    while i < len(tokens) and (_ENV_ASSIGN.match(tokens[i]) or tokens[i] in {"&", "."} or tokens[i].lower() == "rtk"):
        i += 1
    return tokens[i:]


def _dir_argument(args: list[str]) -> str | None:
    """The directory operand of cd / Set-Location / Push-Location."""
    skip_next = False
    for idx, arg in enumerate(args):
        if skip_next:
            skip_next = False
            continue
        low = arg.lower()
        if low in {"-path", "-literalpath"}:
            return args[idx + 1] if idx + 1 < len(args) else None
        if arg.startswith("-") or low == "/d":
            continue
        return arg
    return None


# ---------------------------------------------------------------------------
# The git policy
# ---------------------------------------------------------------------------


def _parse_git(tokens: list[str], current: str | None) -> tuple[str | None, str, list[str]]:
    """(effective dir after -C, subcommand, subcommand args). `tokens[0]` is git."""
    directory = current
    i = 1
    while i < len(tokens):
        tok = tokens[i]
        if tok == "-C":
            directory = _resolve(directory, tokens[i + 1]) if i + 1 < len(tokens) else _UNKNOWN
            i += 2
        elif tok in _GIT_VALUE_OPTS:
            i += 2
        elif tok.startswith("-"):
            i += 1
        else:
            return directory, tok, tokens[i + 1 :]
    return directory, "", []


def _denied_subcommand(sub: str, args: list[str]) -> bool:
    flags = set(args)
    positionals = [a for a in args if not a.startswith("-")]
    if sub == "checkout":
        if "--" in flags:
            return False  # path restore; HEAD does not move
        return bool(args)  # bare `git checkout` only lists status
    if sub == "switch":
        return True
    if sub in {"merge", "rebase", "cherry-pick", "revert"}:
        if sub == "merge" and "--ff-only" in flags:
            return False
        return not (flags & _RECOVERY)
    if sub == "pull":
        return "--ff-only" not in flags
    if sub in {"commit", "am"}:
        return "--dry-run" not in flags
    if sub == "reset":
        if flags & _RESET_MODES:
            return True
        if "--" in flags:
            return False  # path-only reset; HEAD does not move
        # `reset`, `reset HEAD [paths]` do not move HEAD; any other first
        # positional is a commit (bare `reset <commit>`).
        return bool(positionals) and positionals[0] != "HEAD"
    return False


def _check_segment(tokens: list[str], current: str | None) -> str | None:
    """Deny reason for a `git ...` segment, else None."""
    directory, sub, args = _parse_git(tokens, current)
    if not sub or not _denied_subcommand(sub, args):
        return None
    if directory is _UNKNOWN or classify(directory) != "primary":
        return None
    return MESSAGE.format(sub=sub)


def _verdict(command: str, cwd: str | None, depth: int) -> str | None:
    current: str | None = cwd
    stack: list[str | None] = []
    for raw in _split_segments(_strip_cat_heredoc_bodies(command or "")):
        tokens = _leading_call(raw)
        if not tokens:
            continue
        head = os.path.basename(tokens[0].replace("\\", "/")).lower()
        head = head[:-4] if head.endswith(".exe") else head
        if head in _CD_COMMANDS or head in _PUSH_COMMANDS:
            if head in _PUSH_COMMANDS:
                stack.append(current)
            current = _resolve(current, _dir_argument(tokens[1:]) or "")
        elif head in _POP_COMMANDS:
            current = stack.pop() if stack else _UNKNOWN
        elif head == "git":
            reason = _check_segment(tokens, current)
            if reason:
                return reason
        elif depth < 2 and head in _SHELLS | _POWERSHELLS:
            flag = {"-c"} if head in _SHELLS else {"-c", "-command"}
            for idx, tok in enumerate(tokens[:-1]):
                if tok.lower() in flag:
                    reason = _verdict(" ".join(tokens[idx + 1 :]) if head in _POWERSHELLS else tokens[idx + 1], current, depth + 1)
                    if reason:
                        return reason
                    break
    return None


def verdict(command: str, cwd: str | None) -> str | None:
    """Return the reason to deny, or None to stay silent."""
    return _verdict(command, cwd, 0)


def decide(payload: dict) -> str | None:
    """Payload -> deny reason or None. Any internal error is None (fail open)."""
    try:
        command = (payload.get("tool_input") or {}).get("command") or ""
        return verdict(command, payload.get("cwd") or os.getcwd())
    except Exception:
        return None


# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------

_SELF_DENY = (
    "git checkout feat/x",
    "git checkout -b feat/x origin/main",
    "git checkout -B feat/x origin/main",
    "git switch feat/x",
    "git switch -c x origin/main",
    "git switch -C x origin/main",
    "git merge feat/x",
    "git commit -m wip",
    "git reset --hard origin/main",
    "git reset --soft HEAD~1",
    "git reset HEAD~1",
    "git rebase origin/main",
    "git cherry-pick abc1234",
    "git revert abc1234",
    "git pull",
    "git pull --rebase",
    "git fetch origin && git switch -c x origin/main",
    "git status; git commit -m x",
    "git status\ngit switch -c x",
    "bash -c 'git switch -c x'",
)
_SELF_ALLOW = (
    "git merge --ff-only origin/main",
    "git pull --ff-only",
    "git fetch origin",
    "git worktree add ../wt -b feat/x origin/main",
    "git worktree remove ../wt",
    "git worktree list",
    "git worktree prune",
    "git checkout -- README.md",
    "git reset -- README.md",
    "git reset HEAD README.md",
    "git status",
    "git log --oneline -5",
    "git diff origin/main",
    "git show HEAD",
    "git rev-parse --show-toplevel",
    "git branch --list",
    "git merge-base HEAD origin/main",
    "git merge-tree HEAD origin/main",
    "git for-each-ref refs/heads",
    "git reflog",
    "git ls-files",
    "git grep hello",
    "git blame README.md",
    "git config --get user.name",
    "echo 'git switch -c x'",
)


def self_test() -> int:
    import pathlib
    import subprocess
    import tempfile

    from block_agent_dispatch_in_worktree import _make_git_repo_with_worktree

    failures = 0

    def check(label: str, condition: bool) -> None:
        nonlocal failures
        if condition:
            print(f"ok    {label}")
        else:
            print(f"FAIL  {label}", file=sys.stderr)
            failures += 1

    def run_hook(command: str, cwd: str, tool: str = "Bash") -> tuple[int, dict | None]:
        proc = subprocess.run(
            [sys.executable, __file__],
            input=json.dumps({"tool_name": tool, "tool_input": {"command": command}, "cwd": cwd}),
            capture_output=True,
            text=True,
            timeout=30,
        )
        return proc.returncode, (json.loads(proc.stdout) if proc.stdout.strip() else None)

    def is_deny(out: dict | None) -> bool:
        return bool(out) and out.get("hookSpecificOutput", {}).get("permissionDecision") == "deny"

    try:
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            primary, worktree = _make_git_repo_with_worktree(tmp)
            p, w = primary.as_posix(), worktree.as_posix()

            for cmd in _SELF_DENY:
                check(f"primary denies: {cmd!r}", verdict(cmd, p) is not None)
                check(f"worktree allows: {cmd!r}", verdict(cmd, w) is None)
            for cmd in _SELF_ALLOW:
                check(f"primary allows: {cmd!r}", verdict(cmd, p) is None)

            check("git -C <primary> from a worktree denies", verdict(f'git -C "{p}" switch -c x', w) is not None)
            check("git -C <worktree> from the primary allows", verdict(f"git -C {w} switch -c x", p) is None)
            for prefix in ("cd", "Set-Location", "Push-Location"):
                for sep in (" && ", "; ", "\n"):
                    cmd = f'{prefix} "{p}"{sep}git switch -c x'
                    check(f"{prefix!r} into the primary then switch denies ({sep!r})", verdict(cmd, w) is not None)
            check("cd into a worktree from the primary allows", verdict(f"cd {w} && git switch -c x", p) is None)

            # The Git Bash backslash trap: literal backslashes must survive,
            # and a mangled path must fail open rather than guess.
            win = str(primary).replace("/", "\\")
            check("unquoted backslash path in cd denies", verdict(f"cd {win}; git switch -c x", w) is not None)
            check("mangled (backslashes eaten) cwd fails open", verdict("git switch -c x", "C:Usersnobodyprimary") is None)

            not_a_repo = pathlib.Path(tmp) / "not-a-repo"
            not_a_repo.mkdir()
            check("a cwd that is not a repo fails open", verdict("git switch -c x", not_a_repo.as_posix()) is None)

            for tool in ("Bash", "PowerShell"):
                code, out = run_hook("git switch -c x origin/main", p, tool)
                check(f"end-to-end {tool}: switch in primary denies", code == 0 and is_deny(out))
                code, out = run_hook("git merge --ff-only origin/main", p, tool)
                check(f"end-to-end {tool}: merge --ff-only in primary is silent", code == 0 and out is None)
                code, out = run_hook("git switch -c x origin/main", w, tool)
                check(f"end-to-end {tool}: switch in a worktree is silent", code == 0 and out is None)
    except Exception as exc:
        check(f"building the throwaway repo/worktree did not raise ({exc!r})", False)

    proc = subprocess.run([sys.executable, __file__], input="not json at all", capture_output=True, text=True, timeout=30)
    check("malformed stdin fails open", proc.returncode == 0 and proc.stdout.strip() == "")

    if failures:
        print(f"\nFAIL: block_primary_checkout_git self-test ({failures} failure(s))", file=sys.stderr)
        return 1
    print("\nPASS: block_primary_checkout_git self-test")
    return 0


def main() -> int:
    if len(sys.argv) >= 2 and sys.argv[1] == "--self-test":
        return self_test()

    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0

    try:
        reason = decide(payload)
        if reason is None:
            return 0
        json.dump(
            {
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": reason,
                },
                "systemMessage": "Blocked a history-changing git command in the primary checkout.",
            },
            sys.stdout,
        )
    except Exception:
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
