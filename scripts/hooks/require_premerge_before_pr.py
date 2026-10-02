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

_PR = re.compile(
    r"(?:^|[;&|\n]|&&|\|\|)\s*"
    r"(?P<env>(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*)"
    r"gh\b[^;&|\n]*?\bpr\s+(?P<verb>create|merge)\b",
    re.IGNORECASE,
)

MESSAGE = """`gh pr {verb}` is blocked until scripts/premerge.py has passed on this HEAD.

Why: {reason}.

Run it now, from the repo root (about a minute; full mode before a merge that closes a wave):

    git fetch origin
    worker\\.venv\\Scripts\\python.exe scripts\\premerge.py --fast

Committing changes HEAD, so run it AFTER the last commit. If a gate fails, fix that first:
the summary names it. To override deliberately, with a reason that is logged:

    DEG_SKIP_PREMERGE="<reason>" gh pr {verb} ..."""


def _bypass_reason(env_prefix: str) -> str | None:
    match = re.search(rf"\b{BYPASS_VAR}=(\S*)", env_prefix)
    if match:
        return match.group(1).strip("'\"") or "(no reason given)"
    return os.environ.get(BYPASS_VAR) or None


def _log_bypass(repo: Path, verb: str, reason: str) -> None:
    path = stamp.stamp_path(repo)
    if path is None:
        return
    try:
        with open(path.with_name("premerge-bypass.log"), "a", encoding="utf-8", newline="\n") as handle:
            handle.write(f"{time.strftime('%Y-%m-%dT%H:%M:%S')} gh pr {verb}: {reason}\n")
    except OSError:
        pass


def verdict(command: str, cwd: str | Path | None) -> str | None:
    """The reason to deny, or None to stay silent."""
    scanned = _stash._strip_cat_heredoc_bodies(command or "")
    for match in _PR.finditer(scanned):
        verb = match.group("verb").lower()
        repo = Path(cwd) if cwd else Path.cwd()
        bypass = _bypass_reason(match.group("env"))
        ok, reason = stamp.check_stamp(repo)
        if bypass:
            if not ok:
                _log_bypass(repo, verb, bypass)
            return None
        if not ok:
            return MESSAGE.format(verb=verb, reason=reason)
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

            for command in ("gh pr create --title x", "gh pr merge 5 --merge", "cd x && gh pr create",
                            "GH_TOKEN=a gh pr create", "gh --repo a/b pr create"):
                expect(f"no stamp denies {command!r}", verdict(command, repo), True)
            for command in ("gh pr view 5", "gh pr checks", "gh issue create", "echo 'run gh pr create later'" ,
                            "grep 'gh pr merge' README.md", ""):
                expect(f"no stamp still allows {command!r}", verdict(command, repo), False)
            heredoc = "git commit -m \"$(cat <<'EOF'\nsay gh pr create here\nEOF\n)\""
            expect("a cat heredoc body is not a command", verdict(heredoc, repo), False)

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

    proc = subprocess.run([sys.executable, __file__], input="not json at all", capture_output=True,
                          text=True, timeout=15, check=False)
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
