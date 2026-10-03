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
`checkout [<tree>] -- <paths>` and `checkout .` (path restores), path-only `reset -- <paths>` / `reset HEAD
<paths>`, `merge|rebase|cherry-pick|revert --abort|--quit` (recovery), recovery of the
primary checkout onto `main` (`switch main`, `checkout main`, `checkout -B main origin/main`,
`switch -C main origin/main`; no other branch), and every read-only command. Anything in a linked worktree. `git stash` is not handled
here: `block_git_stash.py` owns it. Branch deletion/`branch -f` is left alone.

LIMITS (honest)
---------------
This is a command-text check. It looks through `bash|sh -c/-lc`, `pwsh -Command`,
`cmd /c`, `env`, `time`, `nohup`, `command`, `then`/`do`/`{` and `git -C<path>`,
`--git-dir`, `--work-tree`. Out of scope, documented as known limits: `gh pr checkout`,
`git update-ref`, `git symbolic-ref`, git run inside a script, `-c alias.x=...` aliases,
a command assembled at runtime, and any tool other than Bash/PowerShell. The overall
git time budget is 5 seconds per invocation (3 per directory); past it the answer is
"unknown" and the command is allowed.

A branch change is allowed only in the exact recovery shapes (`switch main`, `checkout
main`, `checkout -B main origin/main`, `switch -C main origin/main`, plus `-f`/`-q`
and their long forms); any other dash token (glued `-bfeat`, `--create=x`, an
abbreviated `--det`) is refused. `--ff-only` counts only when it is the last of
`--ff-only` / `--ff` / `--no-ff`, as in git.

A `--` counts as a path separator only when a path follows it, so `git checkout
feat/x --` and `git reset origin/main --` (no path) are still branch/commit changes.

CONTRACT
--------
Reads the PreToolUse payload on stdin, writes a JSON deny decision on stdout, or
stays silent (exit 0) for "no opinion". Registered for both `Bash` and
`PowerShell`. It never blocks on its own failure: a malformed payload or any
unexpected exception exits 0 quietly. So does a failed import of the sibling
`block_agent_dispatch_in_worktree.py` (a stderr note, exit 0), because run_hook.py
turns any non-zero exit into a deny of every Bash/PowerShell call.

    python scripts/hooks/block_primary_checkout_git.py --self-test

builds a real throwaway repo with a real linked worktree and proves every denied
and allowed shape in both, with no pytest on PATH; exits 0 on pass, 1 on failure.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time

# A hook that cannot import its sibling must not crash: run_hook.py turns any
# non-zero exit into a deny of EVERY Bash/PowerShell call. Fail open instead.
try:
    from block_agent_dispatch_in_worktree import classify
except Exception as _import_error:  # noqa: BLE001 -- deliberately blanket, see above
    classify = None
    _IMPORT_ERROR: str | None = repr(_import_error)
else:
    _IMPORT_ERROR = None

# git's own answer for a directory is fast; a slow git must FAIL OPEN (unknown)
# well before run_hook.py's 8s guard kill would turn the hang into a deny. Each
# call is capped, and so is the SUM over one invocation, so several distinct
# directories with a hung git cannot add up to the kill either.
CLASSIFY_TIMEOUT_SECONDS = 3.0
TOTAL_BUDGET_SECONDS = 5.0

MESSAGE = """This is the PRIMARY checkout, and `git {sub}` is blocked here.

The primary checkout is where the orchestrator lands and dispatches from. It must
only ever be on `main`; an agent that switches branches or commits here strands
the next landing.

Work in your own worktree instead. To check where you are:

    git rev-parse --show-toplevel

If that prints the primary checkout, create a worktree and work there:

    git worktree add <path> -b <type>/<issue>-<slug> origin/main

then run your git commands from <path> (or with `git -C <path> ...`).

If this checkout was already left on another branch, `git switch main` (or
`git checkout -B main origin/main`) is allowed, to put it back.

Still allowed here: `git merge --ff-only`, `git pull --ff-only`, `git fetch`,
`git worktree ...`, `git checkout -- <paths>`, `git reset -- <paths>`, and every
read-only command (status, log, diff, show, rev-parse, branch --list ...)."""

_CD_COMMANDS = {"cd", "chdir", "set-location", "sl"}
_PUSH_COMMANDS = {"pushd", "push-location"}
_POP_COMMANDS = {"popd", "pop-location"}
_SHELLS = {"bash", "sh", "zsh", "dash"}
_POWERSHELLS = {"pwsh", "powershell"}
# Words that may precede a command without changing which command runs.
_WRAPPERS = {
    "&",
    ".",
    "rtk",
    "then",
    "do",
    "else",
    "elif",
    "if",
    "while",
    "until",
    "{",
    "!",
    "time",
    "nohup",
    "command",
    "builtin",
    "exec",
}
# Git global options that consume the next argument.
_GIT_VALUE_OPTS = {"-c", "--namespace", "--exec-path", "--super-prefix"}
_GIT_DIR_OPTS = ("--git-dir", "--work-tree")
_ENV_ASSIGN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")
_SHELL_C_FLAG = re.compile(r"^-[A-Za-z]*c[A-Za-z]*$")
_RESET_MODES = {"--hard", "--soft", "--mixed", "--merge", "--keep"}
_RECOVERY = {"--abort", "--quit"}
_RECOVERY_BRANCH = "main"
_RECOVERY_START = "origin/main"

_HEREDOC = re.compile(r"<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")
_FF_FLAGS = ("--ff-only", "--ff", "--no-ff")
_QUIET_FLAGS = {"-f", "--force", "-q", "--quiet"}  # the only extra flags a recovery may carry
_GIT_ENV_TARGETS = {"GIT_DIR", "GIT_WORK_TREE"}
_SHELL_CONSUMER = re.compile(r"(?:^|[\s;&|(])(?:bash|sh|zsh|dash|pwsh|powershell)(?:\.exe)?(?=\s|$)", re.IGNORECASE)


# ---------------------------------------------------------------------------
# Tokenising. Backslashes are LITERAL (a C:\\path must survive; this is the Git
# Bash backslash trap in the README), quotes group, `&& || ; | & ( )` and
# newlines separate segments.
# ---------------------------------------------------------------------------


def _strip_heredoc_bodies(command: str) -> str:
    """Blank the body of every heredoc, whatever consumes it: the body of
    `python - <<EOF`, `tee f <<EOF` or `gh ... --body-file - <<EOF` is data, not
    a command. The one exception is a heredoc fed to a shell (`bash <<EOF`),
    whose body IS executed and therefore stays visible."""
    lines = command.split("\n")
    out: list[str] = []
    i, n = 0, len(lines)
    while i < n:
        line = lines[i]
        out.append(line)
        i += 1
        opener = _find_heredoc(line)
        # The shell consumer may sit on either side of the pipe: `cat <<EOF | bash`.
        if not opener or _SHELL_CONSUMER.search(line):
            continue
        delimiter = opener.group(2)
        end = i
        while end < n and lines[end].strip() != delimiter:
            end += 1
        if end >= n:
            continue  # no terminator: not a heredoc as far as we can tell, hide nothing
        out.extend([""] * (end - i))
        out.append(lines[end])
        i = end + 1
    return "\n".join(out)


def _find_heredoc(line: str) -> re.Match[str] | None:
    """The first real heredoc opener on `line`, or None. A `<<` inside quotes, a
    `<<<` here-string and a `<<` inside `$(( ... ))` arithmetic are not openers."""
    quote = ""
    arithmetic = 0
    i, n = 0, len(line)
    while i < n:
        c = line[i]
        if quote:
            if c == quote:
                quote = ""
        elif c in "'\"":
            quote = c
        elif line.startswith("((", i):
            arithmetic += 1
            i += 2
            continue
        elif line.startswith("))", i) and arithmetic:
            arithmetic -= 1
            i += 2
            continue
        elif line.startswith("<<", i) and not arithmetic and (i == 0 or line[i - 1] != "<") and not line.startswith("<<<", i):
            match = _HEREDOC.match(line, i)
            if match:
                return match
            i += 2
            continue
        i += 1
    return None


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
            # PowerShell's call operator (a leading wrapper token, see _WRAPPERS).
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


def _resolve(current: str | None, target: str | None) -> str | None:
    """Resolve a `cd`/`-C` target against `current`; None when it cannot be.

    An absolute target resolves even when `current` is unknown, so an
    unresolvable `cd $var` cannot poison a later absolute path."""
    if not target or target == "-" or any(ch in target for ch in "$`%"):
        return None
    # Git Bash drive form: /c/dir/x -> C:/dir/x
    if os.name == "nt" and re.match(r"^/[A-Za-z](/|$)", target):
        target = f"{target[1].upper()}:{target[2:] or '/'}"
    target = os.path.expanduser(target)
    if os.path.isabs(target):
        return os.path.normpath(target)
    if current is None:
        return None
    return os.path.normpath(os.path.join(current, target))


def _leading_call(tokens: list[str]) -> tuple[list[str], list[str]]:
    """(directories named by GIT_DIR / GIT_WORK_TREE assignments, the command proper).

    Drops what precedes the command: env assignments, `env [-i] [VAR=x]`, shell
    keywords (`then`, `do`, `{`), `time`, `nohup`, `command`, PowerShell's `&` and
    an `rtk` proxy prefix."""
    env_targets: list[str] = []

    def note(assignment: str) -> None:
        name, _, value = assignment.partition("=")
        if name in _GIT_ENV_TARGETS:
            env_targets.append(value)

    i = 0
    while i < len(tokens):
        low = tokens[i].lower()
        if _ENV_ASSIGN.match(tokens[i]):
            note(tokens[i])
            i += 1
        elif low in _WRAPPERS:
            i += 1
        elif low == "env":
            i += 1
            while i < len(tokens) and (tokens[i].startswith("-") or _ENV_ASSIGN.match(tokens[i])):
                if _ENV_ASSIGN.match(tokens[i]):
                    note(tokens[i])
                i += 2 if tokens[i] in {"-u", "-C", "-S"} else 1
        else:
            break
    return env_targets, tokens[i:]


def _dir_argument(args: list[str]) -> str | None:
    """The directory operand of cd / Set-Location / Push-Location."""
    for idx, arg in enumerate(args):
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


def _parse_git(tokens: list[str], current: str | None, env_targets: list[str]) -> tuple[list[str | None], str, list[str]]:
    """(directories git will act on, subcommand, subcommand args). `tokens[0]` is git.

    Normally one directory: `current` moved by any `-C`. With `--git-dir`,
    `--work-tree` (or the GIT_DIR / GIT_WORK_TREE assignments before the command)
    it is those targets instead."""
    directory = current
    targets: list[str | None] = list(env_targets)
    i = 1
    while i < len(tokens):
        tok = tokens[i]
        nxt = tokens[i + 1] if i + 1 < len(tokens) else None
        if tok == "-C":
            directory = _resolve(directory, nxt)
            i += 2
        elif tok.startswith("-C") and not tok.startswith("--"):
            directory = _resolve(directory, tok[2:])  # glued: -C<path>
            i += 1
        elif tok in _GIT_DIR_OPTS:
            targets.append(nxt)
            i += 2
        elif tok.startswith(tuple(f"{opt}=" for opt in _GIT_DIR_OPTS)):
            targets.append(tok.split("=", 1)[1])
            i += 1
        elif tok in _GIT_VALUE_OPTS:
            i += 2
        elif tok.startswith("-"):
            i += 1
        else:
            dirs = [_resolve(directory, t) for t in targets] if targets else [directory]
            return dirs, tok, tokens[i + 1 :]
    return [], "", []


def _has_paths_after_dashdash(args: list[str]) -> bool:
    return "--" in args and args.index("--") + 1 < len(args)


def _safe_branch_change(sub: str, args: list[str]) -> bool:
    """True for the only branch changes allowed in the primary checkout: putting
    it back on `main` (`switch main`, `checkout main`, `checkout -B main
    origin/main`, `switch -C main origin/main`), and `checkout .` (path restore)."""
    if not args:
        return sub == "checkout"  # bare `git checkout` only lists status
    if sub == "checkout" and _has_paths_after_dashdash(args):
        return True  # path restore; HEAD does not move
    rest = args[:-1] if args[-1] == "--" else args
    # Exact shapes only. Every dash token that is not one of the listed ones is a
    # refusal: git accepts glued (`-bfeat`, `--create=feat`) and abbreviated
    # (`--det`) spellings that a looser parse would read as harmless flags.
    reset_flag = "-B" if sub == "checkout" else "-C"
    reset_name: str | None = None
    positionals: list[str] = []
    i = 0
    while i < len(rest):
        tok = rest[i]
        if tok in _QUIET_FLAGS:
            i += 1
        elif tok == reset_flag and reset_name is None and i + 1 < len(rest):
            reset_name = rest[i + 1]
            i += 2
        elif tok.startswith("-"):
            return False
        else:
            positionals.append(tok)
            i += 1
    if reset_name is not None:
        # Resetting `main` is recovery only when it is reset to origin/main.
        return reset_name == _RECOVERY_BRANCH and positionals == [_RECOVERY_START]
    if sub == "checkout" and positionals == ["."]:
        return True
    return positionals == [_RECOVERY_BRANCH]


def _fast_forward_only(args: list[str]) -> bool:
    """True when the LAST of --ff-only / --ff / --no-ff is --ff-only, as in git."""
    last = [a for a in args if a in _FF_FLAGS]
    return bool(last) and last[-1] == "--ff-only"


def _denied_subcommand(sub: str, args: list[str]) -> bool:
    flags = set(args)
    positionals = [a for a in args if not a.startswith("-")]
    if sub in {"checkout", "switch"}:
        return not _safe_branch_change(sub, args)
    if sub in {"merge", "rebase", "cherry-pick", "revert"}:
        if sub == "merge" and _fast_forward_only(args):
            return False
        return not (flags & _RECOVERY)
    if sub == "pull":
        return not _fast_forward_only(args)
    if sub in {"commit", "am"}:
        return "--dry-run" not in flags
    if sub == "reset":
        if flags & _RESET_MODES:
            return True
        if _has_paths_after_dashdash(args):
            return False  # path-only reset; HEAD does not move
        # `reset`, `reset HEAD [paths]` do not move HEAD; any other first
        # positional is a commit (bare `reset <commit>`).
        return bool(positionals) and positionals[0] != "HEAD"
    return False


class _Classifier:
    """`classify()` with a per-directory cache and one overall time budget. When
    the budget is spent the answer is "unknown", which allows: a hung git must
    never add up to run_hook.py's kill, which would deny instead."""

    def __init__(self) -> None:
        self._cache: dict[str, str] = {}
        self._deadline = time.monotonic() + TOTAL_BUDGET_SECONDS

    def __call__(self, directory: str) -> str:
        if directory not in self._cache:
            remaining = self._deadline - time.monotonic()
            if remaining <= 0:
                return "unknown"
            self._cache[directory] = classify(directory, timeout=min(CLASSIFY_TIMEOUT_SECONDS, remaining))
        return self._cache[directory]


def _check_segment(tokens: list[str], current: str | None, env_targets: list[str], classifier: _Classifier) -> str | None:
    """Deny reason for a `git ...` segment, else None."""
    directories, sub, args = _parse_git(tokens, current, env_targets)
    if not sub or not _denied_subcommand(sub, args):
        return None
    for directory in directories:
        if directory is not None and classifier(directory) == "primary":
            return MESSAGE.format(sub=sub)
    return None


def _nested_command(head: str, tokens: list[str]) -> str | None:
    """The command string a shell / cmd / PowerShell invocation will run, if any."""
    if head in _SHELLS:
        for idx, tok in enumerate(tokens[1:-1], start=1):
            if _SHELL_C_FLAG.match(tok):  # -c, -lc, -ic ...
                return tokens[idx + 1]
    elif head in _POWERSHELLS:
        for idx, tok in enumerate(tokens[1:], start=1):
            if len(tok) >= 2 and "-command".startswith(tok.lower()):  # -c, -co, ... -Command
                return " ".join(tokens[idx + 1 :])
    elif head == "cmd":
        for idx, tok in enumerate(tokens[1:], start=1):
            if tok.lower() in {"/c", "/k"}:
                return " ".join(tokens[idx + 1 :])
    return None


def _verdict(command: str, cwd: str | None, depth: int, classifier: _Classifier) -> str | None:
    current: str | None = cwd
    stack: list[str | None] = []
    for raw in _split_segments(_strip_heredoc_bodies(command or "")):
        env_targets, tokens = _leading_call(raw)
        if not tokens:
            continue
        head = os.path.basename(tokens[0].replace("\\", "/")).lower()
        head = head[:-4] if head.endswith(".exe") else head
        if head in _CD_COMMANDS or head in _PUSH_COMMANDS:
            if head in _PUSH_COMMANDS:
                stack.append(current)
            current = _resolve(current, _dir_argument(tokens[1:]))
        elif head in _POP_COMMANDS:
            current = stack.pop() if stack else None
        elif head == "git":
            reason = _check_segment(tokens, current, env_targets, classifier)
            if reason:
                return reason
        elif depth < 2:
            nested = _nested_command(head, tokens)
            if nested:
                reason = _verdict(nested, current, depth + 1, classifier)
                if reason:
                    return reason
    return None


def verdict(command: str, cwd: str | None) -> str | None:
    """Return the reason to deny, or None to stay silent."""
    if classify is None:
        return None
    return _verdict(command, cwd, 0, _Classifier())


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
    "bash -lc 'git switch -c x'",
    "git reset origin/main --",
    "git checkout feat/x --",
    "git switch -C main",
    "git checkout -B main origin/feat/x",
    "command git switch -c x",
    "env GIT_PAGER=cat git commit -m x",
    "if true; then git switch -c x; fi",
    "cmd /c git switch -c x",
    "git checkout -bfeat main",
    "git switch --det main",
    "git merge --ff-only --no-ff feat",
    "cat <<'EOF' | bash\ngit switch -c x\nEOF",
    "cat <<< hi\ngit switch -c x",
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
    "git switch main",
    "git checkout main",
    "git checkout -B main origin/main",
    "git switch -C main origin/main",
    "git checkout .",
    "python - <<'EOF'\ngit commit -m x\nEOF",
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

    if _IMPORT_ERROR:
        print(f"block_primary_checkout_git: failing open, sibling import failed: {_IMPORT_ERROR}", file=sys.stderr)
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
