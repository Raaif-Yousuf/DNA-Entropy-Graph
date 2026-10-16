"""PreToolUse hook: `gh pr create` and `gh pr merge` need a green `premerge.py` on this HEAD (#451).

WHY CODE AND NOT A SENTENCE
---------------------------
`scripts/premerge.py` (#431) is the one command that runs every gate, but a command that has
to be remembered is the procedure failure it was written to remove: MEASURED 2026-09-19, a
literal user-home path sat on main for several PRs because the guards ran before one lane
merged and not after. CI here is not a reliable backstop (docs/dev_commands.md, "CI
triggers"). This hook makes the one command a precondition without making CI automatic.

HOW IT KNOWS premerge RAN
-------------------------
`scripts/premerge_stamp.py` records `{head, mode, time}` inside `.git` after a green,
unfiltered premerge run. This hook allows the command only when that stamp matches the
current HEAD and is under 12 hours old. Committing changes HEAD, so the order is: commit, run
`premerge.py --fast`, then `gh pr create`.

LAND_WT IS THE SANCTIONED PATH
---------------------------
`scripts/land_wt.py` runs premerge in a throwaway worktree and runs `gh pr create` as a child process, which
this hook never sees. It then records the PR in `<common-git-dir>/landed-prs.json`, so a later
`gh pr merge <n>` from another checkout (whose HEAD has no stamp) is allowed for that PR only.

ESCAPE HATCH (logged, never silent)
-----------------------------------
`DEG_SKIP_PREMERGE=<reason> gh pr create ...` (an inline env assignment in the command itself,
so it is visible in the transcript) or the same variable in the hook's environment. Each use
appends a line to `<git-dir>/premerge-bypass.log`. The hook must never wedge a session: a
malformed payload, an unreadable repo or any exception exits 0 silently, like every guard here.

WHAT IS NOT MATCHED
-------------------
Only `gh` in command position followed by `pr create` or `pr merge`. `gh pr view`, `gh pr
checks`, prose that merely mentions the words, and `cat <<EOF` heredoc bodies are not commands.

    python scripts/hooks/require_premerge_before_pr.py --self-test
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HOOKS_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(HOOKS_DIR.parent))
sys.path.insert(0, str(HOOKS_DIR))

import block_git_stash as _stash  # noqa: E402  (reused: cat-heredoc stripping, one definition)

import premerge_stamp as stamp  # noqa: E402

BYPASS_VAR = "DEG_SKIP_PREMERGE"

_ENV_ASSIGN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")
_PS_BYPASS = re.compile(rf"\$env:{BYPASS_VAR}\s*=\s*(\"[^\"]*\"|'[^']*'|[^\s;]+)", re.IGNORECASE)
# `gh pr merge` flags that take a value in the next token (`--flag=value` carries its own).
_MERGE_VALUE_FLAGS = {"-R", "--repo", "--match-head-commit", "-b", "--body", "-F", "--body-file", "-t", "--subject", "-A", "--author-email"}

MESSAGE = """`gh pr {verb}` is blocked until scripts/premerge.py has passed on this HEAD.

Why: {reason}.

Run it now, from the repo root (about a minute; full mode before a merge that closes a wave):

    git fetch origin
    worker\\.venv\\Scripts\\python.exe scripts\\premerge.py --fast

Committing changes HEAD, so run it AFTER the last commit. If a gate fails, fix that first:
the summary names it. To override deliberately, with a reason that is logged:

    bash:        DEG_SKIP_PREMERGE="<reason>" gh pr {verb} ...
    PowerShell:  $env:DEG_SKIP_PREMERGE="<reason>"; gh pr {verb} ..."""

LANDED_HINT = """

For a PR that scripts/land_wt.py opened, `gh pr merge` is allowed without a stamp when it names the PR
number and pins the head land_wt verified:

    gh pr merge {pr} --merge --match-head-commit {sha}"""

BARE_MERGE_HINT = (
    "\n\nName the pull request number explicitly (`gh pr merge <pull request number> ...`); "
    "a bare `gh pr merge` merges whatever the current branch is."
)


def _segments(text: str) -> list[list[str]]:
    """Split a shell or PowerShell command into commands of tokens, honouring ' and " quoting.

    Separators (; & | ( ) and newlines) only count outside quotes, and quote characters are removed from
    the tokens, so `gh issue comment 5 --body "see gh pr merge"` is one command whose body is one token."""
    segments: list[list[str]] = []
    tokens: list[str] = []
    word: list[str] = []
    in_word = False
    quote = ""
    i = 0

    def end_word() -> None:
        nonlocal in_word
        if in_word:
            tokens.append("".join(word))
            word.clear()
            in_word = False

    def end_segment() -> None:
        nonlocal tokens
        end_word()
        if tokens:
            segments.append(tokens)
        tokens = []

    while i < len(text):
        ch = text[i]
        if quote:
            if quote == '"' and ch in "\\`" and i + 1 < len(text) and text[i + 1] == '"':
                word.append('"')
                i += 2
                continue
            if ch == quote:
                quote = ""
            else:
                word.append(ch)
        elif ch in "'\"":
            quote = ch
            in_word = True
        elif ch in ";&|()\n\r":
            end_segment()
        elif ch.isspace():
            end_word()
        else:
            word.append(ch)
            in_word = True
        i += 1
    end_segment()
    return segments


def _gh_pr_call(tokens: list[str]) -> tuple[str, list[str], str | None] | None:
    """(verb, args after the verb, bash-style inline bypass reason) when this command is `gh [flags] pr create|merge`."""
    i, bypass = 0, None
    while i < len(tokens) and _ENV_ASSIGN.match(tokens[i]):
        name, _, value = tokens[i].partition("=")
        if name == BYPASS_VAR:
            bypass = value or "(no reason given)"
        i += 1
    if i >= len(tokens) or Path(tokens[i].replace("\\", "/")).name.lower() not in ("gh", "gh.exe"):
        return None
    i += 1
    while i < len(tokens) and tokens[i].startswith("-"):  # gh's global flags before the subcommand
        i += 2 if tokens[i] in ("-R", "--repo") else 1
    if i + 1 < len(tokens) and tokens[i].lower() == "pr" and tokens[i + 1].lower() in ("create", "merge"):
        return tokens[i + 1].lower(), tokens[i + 2 :], bypass
    return None


def _merge_args(args: list[str]) -> tuple[str | None, str | None]:
    """(pull request number, --match-head-commit value) of `gh pr merge`'s arguments, skipping flag values."""
    target = match_head = None
    i = 0
    while i < len(args):
        tok = args[i]
        if tok.startswith("-"):
            name, eq, value = tok.partition("=")
            if not eq and tok in _MERGE_VALUE_FLAGS:
                value = args[i + 1] if i + 1 < len(args) else ""
                i += 1
            if name == "--match-head-commit":
                match_head = value
        elif target is None:
            tail = tok.rstrip("/").rsplit("/", 1)[-1]
            target = tail if tail.isdigit() else ""
        i += 1
    return (target or None), match_head


def _bypass_reason(inline: str | None, whole_command: str) -> str | None:
    if inline:
        return inline
    ps = _PS_BYPASS.search(whole_command)
    if ps:
        return ps.group(1).strip("'\"") or "(no reason given)"
    return os.environ.get(BYPASS_VAR) or None


def _log_bypass(repo: Path, verb: str, reason: str) -> None:
    path = stamp.stamp_path(repo)
    if path is None:
        return
    try:
        with open(path.with_name("premerge-bypass.log"), "a", encoding="utf-8", newline="\n") as handle:
            handle.write(f"{time.strftime('%Y-%m-%dT%H:%M:%S')} gh pr {verb}: {reason}\n")
    except OSError:
        # The bypass log is an audit aid only; a hook must never fail a command over it.
        pass


def verdict(command: str, cwd: str | Path | None) -> str | None:
    """The reason to deny, or None to stay silent."""
    scanned = _stash._strip_cat_heredoc_bodies(command or "")
    for tokens in _segments(scanned):
        call = _gh_pr_call(tokens)
        if call is None:
            continue
        verb, args, inline = call
        repo = Path(cwd) if cwd else Path.cwd()
        bypass = _bypass_reason(inline, scanned)
        ok, reason = stamp.check_stamp(repo)
        if ok:
            continue
        if bypass:
            _log_bypass(repo, verb, bypass)
            continue
        message = MESSAGE.format(verb=verb, reason=reason)
        if verb == "merge":
            target, match_head = _merge_args(args)
            if target is None:
                return message + BARE_MERGE_HINT
            landed = stamp.landed_head(repo, target)
            if landed is None:
                return message
            if match_head and len(match_head) >= 7 and (landed.startswith(match_head) or match_head.startswith(landed)):
                continue
            return message + LANDED_HINT.format(pr=target, sha=landed)
        return message
    return None


def self_test() -> int:
    failures = 0

    def git(repo: Path, *args: str) -> None:
        subprocess.run(["git", *args], cwd=repo, check=True, capture_output=True)

    def expect(label: str, got: object, want: bool) -> None:
        nonlocal failures
        if bool(got) != want:
            failures += 1
            print(f"self-test FAILED: {label} (denied={bool(got)}, wanted {want})", file=sys.stderr)

    saved = os.environ.pop(BYPASS_VAR, None)
    try:
        with tempfile.TemporaryDirectory() as tmp:
            repo = Path(tmp)
            git(repo, "init", "-q")
            git(repo, "config", "user.email", "t@example.com")
            git(repo, "config", "user.name", "T")
            (repo / "a.txt").write_text("1\n", encoding="utf-8", newline="\n")
            git(repo, "add", "-A")
            git(repo, "commit", "-q", "-m", "one")

            for command in (
                "gh pr create --title x",
                "gh pr merge 5 --merge",
                "cd x && gh pr create",
                "GH_TOKEN=a gh pr create",
                "gh --repo a/b pr create",
            ):
                expect(f"no stamp denies {command!r}", verdict(command, repo), True)
            for command in (
                "gh pr view 5",
                "gh pr checks",
                "gh issue create",
                "echo 'run gh pr create later'",
                "grep 'gh pr merge' README.md",
                "",
            ):
                expect(f"no stamp still allows {command!r}", verdict(command, repo), False)
            heredoc = "git commit -m \"$(cat <<'EOF'\nsay gh pr create here\nEOF\n)\""
            expect("a cat heredoc body is not a command", verdict(heredoc, repo), False)

            stamp.record_landed(repo, "77", "a1b2c3d4e5f6")
            expect("a landed merge without the pinned head is denied", verdict("gh pr merge 77 --merge", repo), True)
            expect("a landed merge with the pinned head is allowed", verdict("gh pr merge 77 --match-head-commit a1b2c3d4e5f6", repo), False)
            expect("merge of another PR is still denied", verdict("gh pr merge 78 --match-head-commit a1b2c3d4e5f6", repo), True)
            expect("create is not unlocked by a landed PR", verdict("gh pr create", repo), True)
            expect("a quoted mention is not a command", verdict('gh issue comment 1 --body "gh pr merge"', repo), False)
            stamp.write_stamp(repo, "fast")
            expect("a fresh stamp allows gh pr create", verdict("gh pr create", repo), False)
            (repo / "a.txt").write_text("2\n", encoding="utf-8", newline="\n")
            git(repo, "add", "-A")
            git(repo, "commit", "-q", "-m", "two")
            expect("a new commit denies again", verdict("gh pr merge 1", repo), True)
            expect("the inline bypass allows", verdict("DEG_SKIP_PREMERGE=hotfix gh pr create", repo), False)
            log = stamp.stamp_path(repo).with_name("premerge-bypass.log")
            if not log.is_file() or "hotfix" not in log.read_text(encoding="utf-8"):
                failures += 1
                print("self-test FAILED: a bypass must be logged", file=sys.stderr)
        with tempfile.TemporaryDirectory() as tmp:
            expect("a non-repo cwd fails open", verdict("gh pr create", tmp), False)
    finally:
        if saved is not None:
            os.environ[BYPASS_VAR] = saved

    proc = subprocess.run([sys.executable, __file__], input="not json at all", capture_output=True, text=True, timeout=15, check=False)
    if proc.returncode != 0 or proc.stdout.strip():
        failures += 1
        print("self-test FAILED: malformed stdin must fail open", file=sys.stderr)
    if failures:
        print(f"\nFAIL: require_premerge_before_pr self-test ({failures} failure(s))", file=sys.stderr)
        return 1
    print("PASS: require_premerge_before_pr self-test")
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
        reason = verdict(command, payload.get("cwd"))
        if reason is None:
            return 0
        json.dump(
            {
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": reason,
                },
                "systemMessage": "Blocked gh pr create/merge: run scripts/premerge.py on this HEAD first.",
            },
            sys.stdout,
        )
    except Exception:
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
