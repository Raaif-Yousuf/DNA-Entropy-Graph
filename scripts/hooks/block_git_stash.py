"""PreToolUse hook: refuse the mutating `git stash` subcommands.

WHY THIS EXISTS AS CODE AND NOT AS A SENTENCE
---------------------------------------------
`git stash` is shared across every worktree of a repository, because they all
share one `.git`. It is also shared across every Claude session running against
that repository at the same time.

MEASURED 2026-08-22: two agents ran a revert-check stash inside the same window
and their working sets SWAPPED. A P1 fix landed in an unrelated agent's
worktree while that agent's work landed in the first. Both reported it as "my
edits silently vanished", and one nearly committed the other's changes.

The ban has been written into `CLAUDE.md`, into the
`.claude/skills/orchestrating-agents/SKILL.md` skill, and verbatim into individual
agent briefs. It has been broken FIVE recorded times anyway (on the project these
hooks came from, before this repo existed), most recently 2026-09-05, by agents who
had the ban in their own brief and reported the violation themselves afterwards.
Every one of those was a revert check, which is a thing orchestrators actively
ask for, and stash is the reflex answer.

A rule broken five times by people who read it is not a documentation problem.
This hook is the enforcement, and the message it prints is the replacement,
because a ban with no alternative loses to the reflex every time.

WHAT IT DOES NOT BLOCK
----------------------
`git stash list` and `git stash show` are read-only and are allowed. Prose that
merely mentions the string (a brief being written, a grep, a docs edit) is not a
command invocation and is not matched: the pattern requires `git` in command
position.

CONTRACT
--------
Reads the PreToolUse payload on stdin, writes a JSON decision on stdout.
Silence plus exit 0 means "no opinion", which is the correct response to
everything except a mutating stash. It never blocks on its own failure: a
malformed payload or an unexpected exception exits 0 quietly, because a hook
that breaks the session when IT has a bug is worse than the bug it guards.

    python scripts/hooks/block_git_stash.py --self-test

proves both arms plus the fails-open contract above with no pytest on PATH
(issue #310); exits 0 on pass, 1 on a self-test failure.
"""

from __future__ import annotations

import json
import re
import sys

# Subcommands that mutate the shared stash stack. `list` and `show` are
# deliberately absent: they are read-only and there is no reason to block them.
MUTATING = ("push", "save", "pop", "apply", "drop", "clear", "create", "store", "branch")

# `git` must be in COMMAND position: at the start, or after a shell separator,
# optionally behind a run of inline environment assignments. This is what keeps
# `grep "git stash"` and a heredoc full of prose from matching, while still
# catching `git -C <path> stash` and `git --no-pager stash`, whose option run is
# consumed by [^;&|\n]*? before `stash`.
#
# The env-assignment prefix is not hypothetical tidiness: the first version of
# this pattern omitted it, and `GIT_PAGER=cat git stash` walked straight
# through. scripts/tests/test_hooks.py catches that, which is the whole reason
# a guard gets a test with both arms.
_STASH = re.compile(
    r"(?:^|[;&|\n]|&&|\|\|)\s*"
    r"(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*"
    r"git\b[^;&|\n]*?\bstash\b(?P<rest>[^;&|\n]*)",
    re.IGNORECASE,
)

# A `cat <<'EOF' ... EOF` heredoc is how this session's own commit
# instructions build a multi-line `-m` message (`git commit -m "$(cat <<'EOF'
# ... EOF)"`). A commit message that DESCRIBES the ban -- quoting "never `git
# stash`", or narrating a fix to this very hook -- puts the words "git" and
# "stash" at the start of a heredoc BODY line, which is command position by
# _STASH's own rules (a line start, i.e. right after `\n`, counts). The
# message text is not a command; it is data `cat` echoes back out. Scoped to
# `cat` specifically, not every heredoc: a heredoc fed to an interpreter
# (`bash <<EOF`, `sh <<EOF`, `python <<EOF`) has its body actually EXECUTED,
# and stripping THAT body from the scan would let a real mutating stash
# through undetected. `cat` never executes what it echoes, so it is the one
# consumer this hook can safely treat as message payload rather than more
# shell to check.
_CAT_HEREDOC_OPENER = re.compile(r"\bcat\b[^\n]*<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")


def _strip_cat_heredoc_bodies(command: str) -> str:
    """Blank out the BODY lines of every `cat <<DELIM ... DELIM` heredoc,
    leaving the opener line and the terminator line untouched (so a real
    `git stash` sitting outside any heredoc, on the same command, is still
    seen in command position exactly as before). See the module-level
    comment above `_CAT_HEREDOC_OPENER` for why this is scoped to `cat` and
    no other heredoc consumer."""
    lines = command.split("\n")
    out: list[str] = []
    i = 0
    n = len(lines)
    while i < n:
        line = lines[i]
        opener = _CAT_HEREDOC_OPENER.search(line)
        out.append(line)
        i += 1
        if not opener:
            continue
        delim = opener.group(2)
        while i < n and lines[i].strip() != delim:
            out.append("")  # keep line numbers stable; content is not needed
            i += 1
        if i < n:
            out.append(lines[i])  # the terminator line itself is harmless
            i += 1
    return "\n".join(out)

MESSAGE = """`git stash` is blocked in this repository. Use the patch file instead.

The stash stack is SHARED across every worktree and every concurrent Claude
session, because they all share one .git. Two agents' working sets have already
swapped this way, and entries sitting in the stack right now may belong to
someone else.

For a revert check, which is almost always why this comes up:

    mkdir -p .scratch && git diff > .scratch/fix.patch   # keep the fix
    git checkout -- <the SOURCE files only>              # keep your new tests
    <run your targeted tests; they must go RED>
    git apply .scratch/fix.patch                         # restore
    git diff --stat                                      # prove byte-exact

To park work and switch context, `git checkout -b <branch>` preserves
uncommitted changes in place. To keep a copy, copy the files.

`git stash list` and `git stash show` are read-only and are not blocked."""


def verdict(command: str) -> str | None:
    """Return the reason to deny, or None to stay silent."""
    scanned = _strip_cat_heredoc_bodies(command or "")
    for match in _STASH.finditer(scanned):
        rest = match.group("rest").strip()
        # Bare `git stash` is `push`. An explicit read-only subcommand is fine.
        if not rest:
            return MESSAGE
        word = rest.split()[0].lstrip("-")
        if word in MUTATING:
            return MESSAGE
        if word in ("list", "show"):
            continue
        # An unrecognised word after `stash` (a flag, a pathspec, a stash ref)
        # is treated as a mutation. `git stash -u`, `git stash -- path` and
        # anything a future git adds all belong on the blocked side: this hook
        # exists because the failure is silent and expensive, so an unknown
        # spelling should fail toward refusing, not toward allowing.
        return MESSAGE
    return None


# ---------------------------------------------------------------------------
# Self-test: same deny/allow/heredoc cases scripts/tests/test_hooks.py
# already proves via pytest, plus the fails-open contract every hook's own
# docstring promises, runnable with no pytest on PATH (issue #310).
# ---------------------------------------------------------------------------

_DENY_CASES = (
    "git stash",
    "git stash push -m wip",
    "git stash pop",
    "git stash drop",
    "GIT_PAGER=cat git stash",
    "git -C worker stash",
    "cd worker && git stash",
)

_ALLOW_CASES = (
    "git stash list",
    "git stash show",
    "grep 'git stash' README.md",
    "echo 'never run git stash here'",
    "",
)

_HEREDOC_CASE = (
    "git commit -m \"$(cat <<'EOF'\n"
    "fix: document why we never git stash here\n"
    "EOF\n"
    ")\""
)


def self_test() -> int:
    """Both arms of verdict() plus the fails-open contract. A guard that
    has only ever been run on a clean tree is not a guard; it is a line
    that has never said no."""
    import subprocess

    failures = 0
    for command in _DENY_CASES:
        if verdict(command) is None:
            failures += 1
            print(f"self-test FAILED: expected deny for {command!r}", file=sys.stderr)
    for command in _ALLOW_CASES:
        if verdict(command) is not None:
            failures += 1
            print(f"self-test FAILED: expected allow for {command!r}", file=sys.stderr)
    if verdict(_HEREDOC_CASE) is not None:
        failures += 1
        print("self-test FAILED: a `cat <<EOF` heredoc body must not be scanned as a command",
              file=sys.stderr)

    proc = subprocess.run(
        [sys.executable, __file__],
        input="not json at all",
        capture_output=True,
        text=True,
        timeout=15,
    )
    if proc.returncode != 0 or proc.stdout.strip() != "":
        failures += 1
        print("self-test FAILED: malformed stdin must fail open (exit 0, silent stdout)",
              file=sys.stderr)

    total = len(_DENY_CASES) + len(_ALLOW_CASES) + 2
    if failures:
        print(f"\nFAIL: block_git_stash self-test ({failures}/{total} failure(s))", file=sys.stderr)
        return 1
    print(f"PASS: block_git_stash self-test ({total} cases)")
    return 0


def main() -> int:
    if len(sys.argv) >= 2 and sys.argv[1] == "--self-test":
        return self_test()

    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0

    try:
        command = (payload.get("tool_input") or {}).get("command") or ""
        reason = verdict(command)
        if reason is None:
            return 0
        json.dump(
            {
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": reason,
                },
                "systemMessage": "Blocked a `git stash`; the shared stash stack is not safe here.",
            },
            sys.stdout,
        )
    except Exception:
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
