"""PreToolUse hook: let a session READ Google Cloud state with gcloud, refuse everything else.

WHY THIS EXISTS AS CODE AND NOT AS A SETTINGS RULE
--------------------------------------------------
`.claude/settings.json` used to deny `gcloud *` outright, so no session could even confirm the
CLI was logged in, list this installation's labelled VMs, or audit a leak. A permission rule
cannot say "list yes, delete no": deny beats allow, and a wildcard cannot express a verb
position. The owner chose (2026-10-15) read-only access for sessions, with cloud spend still
going only through `scripts/cloud_gpu_test.ps1`, which runs gcloud inside the script where
this hook never sees it.

WHAT IT ALLOWS
--------------
A `gcloud`, `gsutil` or `bq` invocation in command position passes only when its verb (the
positional words, flags ignored) contains a READ verb and no WRITE verb. Anything unrecognised
is denied: the failure this guards is a VM that bills by the hour, so an unknown spelling
fails toward refusing. `auth print-access-token` and friends are denied too: a bearer token
in a transcript is a secret in a log.

Prose that merely names gcloud (grep, echo, a `cat <<EOF` commit message) is not a command
and is not matched. The scan is a guard against an accident, not a sandbox: a Python
subprocess call can reach gcloud without this hook seeing a `gcloud` word.

CONTRACT
--------
Same as every guard here: PreToolUse JSON on stdin, a deny decision on stdout or silence.
A malformed payload fails open (exit 0, silent); `run_hook.py` turns a crash into a deny.

    python scripts/hooks/gcloud_read_only.py --self-test
"""

from __future__ import annotations

import json
import re
import shlex
import sys

from block_git_stash import _strip_cat_heredoc_bodies  # one definition of the heredoc rule

# Command position: start, a shell separator, a subshell/substitution opener, or PowerShell's `&`,
# optionally behind inline env assignments. The tool word may carry a Windows shim suffix.
_INVOCATION = re.compile(
    r"(?:^|[;&|\n(`]|&&|\|\|)\s*"
    r"(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*"
    r"(?P<tool>gcloud|gsutil|bq)(?:\.cmd|\.ps1|\.exe)?(?=\s|$|[;&|)`])"
    r"(?P<rest>[^;&|\n)`]*)",
    re.IGNORECASE,
)

READ_VERBS = frozenset({
    "list", "describe", "get-iam-policy", "get-value", "info", "version", "ls", "du",
    "get-ancestors", "get-ancestors-iam-policy", "search-all-resources", "show",
})

WRITE_VERBS = frozenset({
    "create", "delete", "update", "set", "unset", "patch", "add-iam-policy-binding",
    "remove-iam-policy-binding", "set-iam-policy", "enable", "disable", "start", "stop", "reset",
    "resume", "suspend", "resize", "ssh", "scp", "deploy", "cp", "mv", "rm", "rsync", "mb", "rb",
    "login", "logout", "revoke", "activate-service-account", "print-access-token",
    "print-identity-token", "application-default", "import", "export", "attach-disk",
    "detach-disk", "add-labels", "remove-labels", "add-metadata", "remove-metadata", "move",
    "undelete", "link", "unlink", "mk", "load", "insert", "query", "cancel", "request",
})

MESSAGE = """This gcloud/gsutil/bq call is not read-only, so it is blocked here.

Sessions may READ Google Cloud state (list, describe, get-iam-policy, ls, config list, auth list).
Anything that creates, changes, starts, stops or deletes a resource, or prints a token, goes through
`scripts/cloud_gpu_test.ps1` (labelled, maxRunDuration, self-deleting) or the app itself, never a
bare CLI call (CLAUDE.md Hard Rules 9-11). Ask the owner if you really need a one-off mutation."""


def _positionals(rest: str) -> list[str]:
    try:
        words = shlex.split(rest, posix=True)
    except ValueError:
        words = rest.split()
    return [w.lower() for w in words if not w.startswith("-")]


def verdict(command: str) -> str | None:
    """Return the reason to deny, or None to stay silent."""
    scanned = _strip_cat_heredoc_bodies(command or "")
    for match in _INVOCATION.finditer(scanned):
        words = _positionals(match.group("rest"))
        if any(w in WRITE_VERBS for w in words):
            return MESSAGE
        if not any(w in READ_VERBS for w in words):
            return MESSAGE
    return None


_DENY_CASES = (
    "gcloud compute instances create vm",
    "gcloud compute instances delete vm",
    "gcloud config set project p",
    "gcloud auth print-access-token",
    "gsutil rm gs://b/o",
    "gcloud",
    "gcloud config list && gcloud compute instances delete vm",
    "x=$(gcloud compute instances create vm)",
)
_ALLOW_CASES = (
    "gcloud config list",
    "gcloud compute instances list --filter=labels.app=dna-entropy-graph",
    "& gcloud auth list",
    "gsutil ls gs://b",
    "grep gcloud docs/cloud_design.md",
    "",
)


def self_test() -> int:
    import subprocess

    failures = [c for c in _DENY_CASES if verdict(c) is None]
    failures += [c for c in _ALLOW_CASES if verdict(c) is not None]
    for c in failures:
        print(f"self-test FAILED: {c!r}", file=sys.stderr)
    proc = subprocess.run([sys.executable, __file__], input="not json", capture_output=True,
                          text=True, timeout=15)
    if proc.returncode != 0 or proc.stdout.strip():
        failures.append("<malformed stdin must fail open>")
        print("self-test FAILED: malformed stdin must fail open", file=sys.stderr)
    total = len(_DENY_CASES) + len(_ALLOW_CASES) + 1
    if failures:
        print(f"FAIL: gcloud_read_only self-test ({len(failures)}/{total} failure(s))", file=sys.stderr)
        return 1
    print(f"PASS: gcloud_read_only self-test ({total} cases)")
    return 0


def main() -> int:
    if len(sys.argv) >= 2 and sys.argv[1] == "--self-test":
        return self_test()
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0
    try:
        reason = verdict((payload.get("tool_input") or {}).get("command") or "")
        if reason is None:
            return 0
        json.dump(
            {
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": reason,
                },
                "systemMessage": "Blocked a non-read-only gcloud/gsutil/bq call.",
            },
            sys.stdout,
        )
    except Exception:
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
