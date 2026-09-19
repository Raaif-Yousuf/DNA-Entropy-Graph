"""PreToolUse hook: refuse a cloud VM create that carries no labels or no
max-run-duration.

WHY THIS EXISTS AS CODE AND NOT AS A SENTENCE
---------------------------------------------
`docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md` section 4
requires every VM this project creates to carry labels (`app=dna-entropy-graph`,
`job-id`, `installation-id`, `model`, `app-version`, `lifecycle`, `purpose` --
section 3.1) and a `maxRunDuration` (section 3 lists the model-to-GPU cost
table this exists to bound). Both facts matter for the same reason: an
untagged, unbounded VM is invisible to `CloudCli resources list --install <id>`
(the leak detector this repo's own `working-on-gcp` skill uses as its
end-state check) and can run, and bill, indefinitely. A VM created by hand
while debugging -- "just this once, I'll clean it up after" -- is exactly how
a leak starts, and it has no owner-visible trace until someone notices a
billing alert.

This is new tooling: it has no CLAIR-side history to port, only the
description in Appendix C section 4 ("the third hook scans a command for
`instances create`/`CloudCli vm create` and refuses one without `--labels`
and `--max-run-duration`") and the shape of its two siblings
(`block_git_stash.py`, `block_recursive_delete.py`) to match.

WHAT IT BLOCKS
--------------
A command that, in COMMAND position, invokes either:

  * `gcloud compute instances create ...` (or `gcloud ... instances create`,
    covering `gcloud --project X compute instances create`, etc.)
  * `CloudCli vm create ...` (this repo's own Compute-API console host,
    Appendix B section "cli.py"/"CloudCli", however it is invoked --
    `CloudCli.exe`, `dotnet run --project ... CloudCli -- vm create`, or a
    published single-file executable)

and whose argument text (scanned as a flat string, not strictly tokenised --
see `_missing_required_flags`) is missing one or more of `REQUIRED_FLAGS`.

WHAT IT DOES NOT BLOCK
-----------------------
- `gcloud` itself is separately denied outright by `.claude/settings.json`'s
  permission deny-list; this hook exists for the day a `CloudCli` wrapper
  reaches the Compute API without going through a bare `gcloud` invocation at
  all, and as a second, independent layer in case that deny-list is ever
  loosened or bypassed by a nested shell.
- `instances list`, `instances describe`, `instances delete`, `instances
  stop` and every other `gcloud compute instances` subcommand: only `create`
  provisions a new billable resource, so only `create` is scanned.
- A command that merely mentions the trigger phrase in prose (a brief, a
  grep, a docs edit): the pattern requires the phrase in command position,
  and a `cat <<DELIM ... DELIM` heredoc body is blanked before scanning, the
  same way and for the same reason as in `block_git_stash.py`.

CONTRACT
--------
Reads the PreToolUse payload on stdin, writes a JSON decision on stdout.
Silence plus exit 0 means "no opinion", which is the correct response to
everything except an under-labelled or unbounded VM create. It never blocks
on its own failure: a malformed payload or an unexpected exception exits 0
quietly, because a hook that breaks the session when IT has a bug is worse
than the leaked VM it guards against.

    python scripts/hooks/block_unlabelled_vm_create.py --self-test

proves both arms plus the fails-open contract above with no pytest on PATH
(issue #310); exits 0 on pass, 1 on a self-test failure.
"""

from __future__ import annotations

import json
import re
import sys

# The flags this hook currently refuses to let a VM create proceed without.
# A human extending this list (e.g. once the app's CloudCli wrapper grows a
# flag for `instanceTerminationAction`, the third field Appendix C section 4
# names alongside labels and maxRunDuration) edits this tuple and nothing
# else -- `_missing_required_flags` and the message below both read it, so
# they cannot drift out of sync with each other.
REQUIRED_FLAGS: tuple[str, ...] = ("--labels", "--max-run-duration")

# Alternate spellings accepted for each required flag above, keyed by the
# canonical name. `gcloud` uses kebab-case; a future C# `CloudCli` may use
# PascalCase or camelCase flag names instead (`--MaxRunDuration`,
# `--maxRunDuration`) since .NET command-line parsers commonly do. Every
# alias is checked case-insensitively regardless, but keeping the alias list
# explicit (rather than only lower-casing) makes it obvious at a glance which
# spellings this hook already knows about.
_FLAG_ALIASES: dict[str, tuple[str, ...]] = {
    "--labels": ("--labels", "--label"),
    "--max-run-duration": ("--max-run-duration", "--maxrunduration", "--max-run-secs", "--maxrunseconds"),
}

# `cat <<DELIM ... DELIM` bodies are data, not shell. Identical reasoning to
# block_git_stash.py's own copy.
_CAT_HEREDOC_OPENER = re.compile(r"\bcat\b[^\n]*<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")

# Command position: start of line, or after a shell separator, optionally
# behind a run of inline environment assignments.
_CMD_POS = r"(?:^|[;&|\n(]|&&|\|\|)\s*(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*"

# `gcloud [...] compute instances create [...]` or `gcloud [...] instances
# create [...]` (both spellings are real gcloud usage; `compute` is the
# component name and is optional in a `gcloud compute` alias). The argument
# run after `create` is captured so the required-flags check can look at it
# in isolation, without also matching flags that belong to a DIFFERENT
# command chained on the same line.
_GCLOUD_CREATE = re.compile(
    _CMD_POS + r"gcloud\b(?:(?!;|&|\||\n).)*?\binstances\s+create\b(?P<args>[^;&|\n]*)",
    re.IGNORECASE,
)

# `CloudCli [...] vm create [...]`, however CloudCli itself is invoked
# (`CloudCli`, `CloudCli.exe`, `dotnet run --project app/.../CloudCli -- vm
# create`, a published self-contained exe under a version-specific path).
# Anchoring on the literal `CloudCli` token rather than on how it is launched
# is deliberate: this repo's packaging story (self-contained publish,
# Velopack) is still being decided, and a hook that only recognised one
# launch shape would silently stop working the day that shape changes.
_CLOUDCLI_CREATE = re.compile(
    _CMD_POS + r"CloudCli(?:\.exe)?\b(?:(?!;|&|\||\n).)*?\bvm\s+create\b(?P<args>[^;&|\n]*)",
    re.IGNORECASE,
)

MESSAGE_TEMPLATE = """A cloud VM create is missing required flag(s): {missing}.

Every VM this project creates must carry labels ({{app=dna-entropy-graph,
job-id, installation-id, model, app-version, lifecycle, purpose}} per
docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md section 3.1)
and a max-run-duration, so it stays visible to `CloudCli resources list
--install <id>` (the leak detector) and cannot run, and bill, indefinitely.

Add the missing flag(s) and retry:

    {example}

If you are not sure this VM should exist at all right now: it probably
should not. Read the `working-on-gcp` skill before creating any Google Cloud
resource for this project, and use `scripts/cloud_gpu_test.ps1` for a smoke
test rather than a hand-built `gcloud`/`CloudCli` invocation."""


def _strip_cat_heredoc_bodies(command: str) -> str:
    """Blank out the BODY lines of every `cat <<DELIM ... DELIM` heredoc,
    leaving the opener and terminator lines in place. Identical to the
    sibling hooks' own copy of this helper; kept duplicated rather than
    imported so each hook stays a single, independently-readable file that
    can be dropped into `scripts/hooks/` on its own."""
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
            out.append("")
            i += 1
        if i < n:
            out.append(lines[i])
            i += 1
    return "\n".join(out)


def _missing_required_flags(args: str) -> list[str]:
    """Which of REQUIRED_FLAGS has no recognised spelling present in `args`.

    Deliberately a flat substring scan, not a real tokeniser: `--labels` and
    `--max-run-duration` both take a value (`--labels=k=v,k2=v2`,
    `--max-run-duration=14400s`), and a value can itself legally contain
    `=`, so splitting on `=`/whitespace and comparing whole tokens is more
    ways to get it wrong than a plain `in` check for the flag's own spelling
    is to get it right. The cost of that leniency is a command that merely
    MENTIONS `--labels` in an unrelated argument value would be read as
    present; that failure direction is "occasionally too permissive", which
    matches every sibling hook's stated preference to fail toward not
    blocking the orchestrator's own dispatching on a parsing guess -- the
    real backstop is the human or CI reading `CloudCli resources list`
    afterward, not this hook alone.
    """
    lowered = args.lower()
    missing = []
    for canonical in REQUIRED_FLAGS:
        aliases = _FLAG_ALIASES.get(canonical, (canonical,))
        if not any(alias in lowered for alias in aliases):
            missing.append(canonical)
    return missing


def verdict(command: str) -> str | None:
    """Return the reason to deny, or None to stay silent."""
    scanned = _strip_cat_heredoc_bodies(command or "")

    for pattern in (_GCLOUD_CREATE, _CLOUDCLI_CREATE):
        for match in pattern.finditer(scanned):
            missing = _missing_required_flags(match.group("args"))
            if not missing:
                continue
            example = (
                "gcloud compute instances create <name> --labels=app=dna-entropy-graph,"
                "job-id=<id>,purpose=job --max-run-duration=14400s ..."
            )
            return MESSAGE_TEMPLATE.format(missing=", ".join(missing), example=example)
    return None


# ---------------------------------------------------------------------------
# Self-test: same deny/allow cases scripts/tests/test_hooks.py already
# proves via pytest, plus the fails-open contract every hook's own
# docstring promises, runnable with no pytest on PATH (issue #310).
# ---------------------------------------------------------------------------

_DENY_CASES = (
    "gcloud compute instances create deg-job-1 --zone us-central1-a",
    "gcloud compute instances create deg-job-1 --labels=app=dna-entropy-graph",  # missing max-run-duration
    "gcloud compute instances create deg-job-1 --max-run-duration=14400s",  # missing labels
    "CloudCli vm create --zone us-central1-a",
    "CloudCli.exe vm create --labels app=x",
)

_ALLOW_CASES = (
    "gcloud compute instances create deg-job-1 --labels=app=dna-entropy-graph,job-id=1 "
    "--max-run-duration=14400s",
    "CloudCli vm create --labels app=x --max-run-duration 14400s",
    "gcloud compute instances list",
    "gcloud compute instances delete deg-job-1",
    "echo 'gcloud compute instances create is blocked without labels'",  # prose, not command position
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

    total = len(_DENY_CASES) + len(_ALLOW_CASES) + 1
    if failures:
        print(f"\nFAIL: block_unlabelled_vm_create self-test ({failures}/{total} failure(s))", file=sys.stderr)
        return 1
    print(f"PASS: block_unlabelled_vm_create self-test ({total} cases)")
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
                "systemMessage": "Blocked a VM create missing required labels/max-run-duration.",
            },
            sys.stdout,
        )
    except Exception:
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
