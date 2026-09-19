#!/usr/bin/env python3
"""Keep Claude Code's auto-memory in sync across dev machines, with a secret
scan on every push, because this repo is public.

WHY THIS EXISTS
---------------
Claude Code reads and writes its auto-memory in a machine-local directory:

    ~/.claude/projects/<sanitized-cwd>/memory/

That path is outside the repo and differs per platform (a `C--Users-...`
shaped slug on Windows, a POSIX-path-derived slug on macOS/Linux), so nothing
about it travels between two dev machines on its own.

A mirror of that directory is committed at `.claude/memory/`, and it is what
actually moves memories between machines: it rides this repo both machines
already pull. This script is the copy step, made runnable and hooked (see
`.claude/settings.json`):

    pull  mirror -> live   at SessionStart, so this machine picks up whatever
                           another one wrote and pushed
    push  live   -> mirror at Stop, so this session's memories land in the
                           working tree ready to commit

WHAT IT DELIBERATELY DOES NOT DO
--------------------------------
**It never runs git.** No pull, no add, no commit, no push. A hook that
commits on your behalf is a surprise, and a hook that pushes is a worse one.
The mirror is left dirty in the working tree and rides along with the normal
memory-update commit flow. `--push` prints what changed so it is visible.

**It never deletes.** A file present in the destination but absent from the
source is left alone and reported. Deleting a memory is a real decision (the
memory instructions say to delete ones that turn out wrong) and it should not
happen as a side effect of a machine that has not synced in a while. The cost
is that deletions have to be propagated by hand; that is the right trade.

THE SECRET SCAN (why `--push` is not just a copy)
---------------------------------------------------
DNA-Entropy-Graph is a **public** repository. `.claude/memory/` is committed
to it. A memory file is free-form prose an agent writes about itself, at the
end of a session, usually in a hurry -- exactly the conditions under which a
real credential, an internal path, or a project identifier ends up quoted
"for context" and then pushed straight into public history. `--push` refuses
to copy any file whose content trips `SECRET_PATTERNS` below (see that
constant's own comment for the categories and for how a human extends it),
prints which pattern matched and on which line, and by default aborts the
**whole** push rather than silently dropping just the flagged file -- a
partial push that "worked" is the shape most likely to be waved through
without anyone reading the warning. `--allow-flagged-skip` opts into the
narrower behaviour (copy the clean files, skip only the flagged ones) for the
rare case where a human has already looked at the flagged file and wants the
rest of the batch through immediately; see its own help text.

CONFLICTS
---------
Memories are one fact per file, so two machines rarely touch the same one and
`--pull` only overwrites a live file when the mirror's copy is genuinely
different. `MEMORY.md` is the exception: it is a shared index that both
machines append to, so it is the one file that will actually collide. When it
does, git surfaces it as a normal merge conflict on the mirror and the two
halves are almost always both wanted.

USAGE
-----
    python scripts/sync_memory.py --pull                 # mirror -> live
    python scripts/sync_memory.py --push                 # live -> mirror, secret-scanned
    python scripts/sync_memory.py --push --dry-run        # report only; write nothing
    python scripts/sync_memory.py --status                # report drift both ways, change nothing
    python scripts/sync_memory.py --self-test              # run the scanner against its own fixtures
    python scripts/sync_memory.py --list-patterns          # print the secret-pattern set

Exit code is 0 on success and on "nothing to do", 1 when `--push` finds a
secret and aborts, 3 when `--self-test` finds the scanner itself broken. A
missing live directory is not an error: a fresh machine that has never run a
session here simply has nothing to pull into yet, and failing a SessionStart
hook over that would be obnoxious.
"""

from __future__ import annotations

import argparse
import dataclasses
import filecmp
import json
import os
import pathlib
import re
import shutil
import sys

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
MIRROR_DIR = REPO_ROOT / ".claude" / "memory"


# ---------------------------------------------------------------------------
# The secret-pattern set. A plain list of dataclass instances, deliberately
# NOT hidden behind a factory or a config file: the whole point (per
# docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md section 4
# and issue #273) is that a human can open this file, read one example
# pattern, and add a sibling in under a minute without understanding the rest
# of the script. `find_secrets()` below is the only thing that reads this
# list, and it does not care how many entries there are or what order they
# come in.
# ---------------------------------------------------------------------------

@dataclasses.dataclass(frozen=True)
class SecretPattern:
    name: str            # short, stable id -- printed in the refusal message
    category: str        # one of the categories issue #273 / Appendix C section 4 names
    pattern: re.Pattern[str]
    why: str             # one line: what this is and why it must never be pushed


SECRET_PATTERNS: tuple[SecretPattern, ...] = (
    SecretPattern(
        "email",
        "credentials",
        re.compile(r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"),
        "an email address; this repo is public and a memory file is not the place for one",
    ),
    SecretPattern(
        "google-oauth-access-token",
        "access-tokens",
        re.compile(r"\bya29\.[A-Za-z0-9_-]{20,}\b"),
        "a live Google OAuth access token (ya29.* prefix)",
    ),
    SecretPattern(
        "google-api-key",
        "access-tokens",
        re.compile(r"\bAIza[0-9A-Za-z_-]{35}\b"),
        "a Google API key",
    ),
    SecretPattern(
        "google-oauth-client-secret",
        "oauth-client-secrets",
        re.compile(r"\bGOCSPX-[A-Za-z0-9_-]{20,}\b"),
        "a Google OAuth client secret (GOCSPX- prefix)",
    ),
    SecretPattern(
        "oauth-client-secret-field",
        "oauth-client-secrets",
        re.compile(r"(?i)\bclient[_-]?secret\b\s*[:=]\s*[\"']?[A-Za-z0-9_\-/+=]{12,}[\"']?"),
        "a `client_secret` key with what looks like a real value next to it",
    ),
    SecretPattern(
        "github-token",
        "access-tokens",
        re.compile(r"\bgh[pousr]_[A-Za-z0-9]{36,}\b"),
        "a GitHub personal-access/app/OAuth token",
    ),
    SecretPattern(
        "aws-access-key-id",
        "access-tokens",
        re.compile(r"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"),
        "an AWS access key id (relevant once the v1.1 multi-cloud AWS milestone lands)",
    ),
    SecretPattern(
        "jwt",
        "access-tokens",
        re.compile(r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b"),
        "a JWT-shaped token (header.payload.signature, base64url)",
    ),
    SecretPattern(
        "generic-secret-field",
        "credentials",
        re.compile(
            r"(?i)\b(api[_-]?key|access[_-]?token|refresh[_-]?token|password|secret)\b"
            r"\s*[:=]\s*[\"']?[A-Za-z0-9_\-/+=]{16,}[\"']?"
        ),
        "a key named like a secret (api_key, password, token, ...) with a long value next to it",
    ),
    SecretPattern(
        "private-key-material",
        "private-key-material",
        re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA |)PRIVATE KEY-----"),
        "a PEM private key block",
    ),
    SecretPattern(
        "gcp-project-number",
        "gcp-project-identifiers",
        re.compile(r"\b\d{10,12}\b"),
        "a GCP project NUMBER-shaped run of 10-12 digits",
    ),
    SecretPattern(
        "gcp-project-id-suffixed",
        "gcp-project-identifiers",
        re.compile(r"\b[a-z][a-z0-9-]{4,28}-\d{6}\b"),
        "a GCP auto-generated project ID shape (name-123456)",
    ),
    SecretPattern(
        "gcp-billing-account-id",
        "gcp-project-identifiers",
        re.compile(r"\b[0-9A-Fa-f]{6}-[0-9A-Fa-f]{6}-[0-9A-Fa-f]{6}\b"),
        "a GCP billing account id shape (XXXXXX-XXXXXX-XXXXXX)",
    ),
    SecretPattern(
        "windows-user-home-path",
        "user-home-paths",
        re.compile(r"[A-Za-z]:\\\\?Users\\\\?[A-Za-z0-9_.\-]+", re.IGNORECASE),
        "an absolute Windows path under a real user's home directory",
    ),
    SecretPattern(
        "posix-user-home-path",
        "user-home-paths",
        re.compile(r"/(?:home|Users)/[A-Za-z0-9_.\-]+"),
        "an absolute POSIX path under a real user's home directory",
    ),
)


@dataclasses.dataclass(frozen=True)
class SecretMatch:
    pattern_name: str
    category: str
    why: str
    line_no: int
    redacted: str  # the matched text, masked -- never the raw secret


def _redact(match_text: str) -> str:
    """Mask a matched secret for display: enough to identify the pattern
    that fired, never enough to be useful if leaked a second time by this
    script's own output."""
    if len(match_text) <= 6:
        return "*" * len(match_text)
    return f"{match_text[:3]}...{match_text[-2:]}  ({len(match_text)} chars)"


def find_secrets(text: str) -> list[SecretMatch]:
    """Every SECRET_PATTERNS hit in `text`, one entry per match. Pure and
    side-effect-free so it can be unit-tested and used by `--self-test`
    without touching any file."""
    matches: list[SecretMatch] = []
    lines = text.splitlines()
    for i, line in enumerate(lines, start=1):
        for spec in SECRET_PATTERNS:
            m = spec.pattern.search(line)
            if m:
                matches.append(
                    SecretMatch(spec.name, spec.category, spec.why, i, _redact(m.group(0)))
                )
    return matches


def scan_file_for_secrets(path: pathlib.Path) -> list[SecretMatch]:
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return []
    return find_secrets(text)


def _slugify_cwd(path: pathlib.Path) -> str:
    """Claude Code's own project-directory slug: every path separator and
    drive colon becomes a dash. `<user-home>/DNA-Entropy-Graph` ->
    `<project-slug>`."""
    return str(path).replace("\\", "-").replace("/", "-").replace(":", "-")


def find_live_dir() -> pathlib.Path | None:
    """Locate the machine-local auto-memory directory for THIS repo.

    Three strategies, most-specific first, because the slug format is Claude
    Code's business and not a contract we control:

    1. `DEG_MEMORY_DIR`, for anyone who has moved it or is testing.
    2. The derived slug, which is what a normal Claude Code install uses.
    3. A scan of `~/.claude/projects/*/memory` for a slug ending in this
       repo's directory name. This is the fallback that keeps working if the
       slug format changes, or on a machine whose checkout lives elsewhere.

    Returns None rather than raising: a machine with no session history here
    has no live directory yet, which is a normal state, not a failure.
    """
    override = os.environ.get("DEG_MEMORY_DIR")
    if override:
        p = pathlib.Path(override).expanduser()
        return p if p.is_dir() else None

    projects = pathlib.Path.home() / ".claude" / "projects"
    if not projects.is_dir():
        return None

    exact = projects / _slugify_cwd(REPO_ROOT) / "memory"
    if exact.is_dir():
        return exact

    # Fallback: match on the repo's own directory name, case-insensitively,
    # since macOS slugs preserve case and a Windows drive prefix will not match
    # a full-path comparison anyway.
    want = REPO_ROOT.name.lower()
    candidates = [
        d / "memory"
        for d in sorted(projects.iterdir())
        if d.is_dir() and (d / "memory").is_dir() and d.name.lower().endswith(want)
    ]
    return candidates[0] if len(candidates) == 1 else None


def _md_files(d: pathlib.Path) -> dict[str, pathlib.Path]:
    return {p.name: p for p in sorted(d.glob("*.md"))} if d.is_dir() else {}


def compare(src: pathlib.Path, dst: pathlib.Path) -> tuple[list[str], list[str], list[str]]:
    """Return (new, changed, only_in_dst) by filename.

    `only_in_dst` is reported and never acted on. See the module docstring on
    why deletion is not propagated.
    """
    s, d = _md_files(src), _md_files(dst)
    new = [n for n in s if n not in d]
    changed = [n for n in s if n in d and not filecmp.cmp(s[n], d[n], shallow=False)]
    only_dst = [n for n in d if n not in s]
    return sorted(new), sorted(changed), sorted(only_dst)


def copy_over(src: pathlib.Path, dst: pathlib.Path, names: list[str]) -> None:
    dst.mkdir(parents=True, exist_ok=True)
    for n in names:
        shutil.copy2(src / n, dst / n)


def _scan_candidates(src: pathlib.Path, names: list[str]) -> dict[str, list[SecretMatch]]:
    """Which of `names` (files under `src`) contain a secret, and what was
    found. Only files that actually matched are present in the result."""
    flagged: dict[str, list[SecretMatch]] = {}
    for n in names:
        hits = scan_file_for_secrets(src / n)
        if hits:
            flagged[n] = hits
    return flagged


def _print_flagged(flagged: dict[str, list[SecretMatch]]) -> None:
    for name, hits in flagged.items():
        print(f"  REFUSED  {name}")
        for h in hits:
            print(f"             L{h.line_no}  [{h.pattern_name}/{h.category}]  {h.redacted}")
            print(f"                    {h.why}")


def run(
    direction: str,
    apply_changes: bool,
    dry_run: bool = False,
    allow_flagged_skip: bool = False,
) -> int:
    live = find_live_dir()
    if live is None:
        print("sync_memory: no live auto-memory directory for this repo on this machine; nothing to do")
        return 0

    src, dst, label = (
        (MIRROR_DIR, live, "mirror -> live") if direction == "pull" else (live, MIRROR_DIR, "live -> mirror")
    )
    if not src.is_dir():
        print(f"sync_memory: source {src} does not exist; nothing to do")
        return 0

    new, changed, only_dst = compare(src, dst)
    candidates = new + changed

    # The secret scan only matters for the direction that can leak into the
    # public repo: live -> mirror. Pulling mirror -> live moves content that
    # is already committed (already scanned by whatever pushed it) onto this
    # one machine, which is not a new exposure.
    flagged: dict[str, list[SecretMatch]] = {}
    if direction == "push" and candidates:
        flagged = _scan_candidates(src, candidates)

    if not apply_changes or dry_run:
        verb = "[dry-run] would " if dry_run and apply_changes else ""
        print(f"sync_memory [{'dry-run' if dry_run and apply_changes else 'status'}] {label}")
        print(f"  live   : {live}")
        print(f"  mirror : {MIRROR_DIR}")
        print(f"  new={len(new)} changed={len(changed)} only-in-destination={len(only_dst)}"
              f"{f' flagged={len(flagged)}' if direction == 'push' else ''}")
        for n in new:
            marker = "  (WOULD BE REFUSED, see below)" if n in flagged else ""
            print(f"    new      {n}{marker}")
        for n in changed:
            marker = "  (WOULD BE REFUSED, see below)" if n in flagged else ""
            print(f"    changed  {n}{marker}")
        for n in only_dst:
            print(f"    dst-only {n}   (never deleted automatically)")
        if flagged:
            print(f"\n  {verb}refuse {len(flagged)} file(s) containing what looks like a secret:")
            _print_flagged(flagged)
        return 1 if (flagged and not allow_flagged_skip) else 0

    if flagged and not allow_flagged_skip:
        print(f"sync_memory: REFUSING to push -- {len(flagged)} file(s) look like they contain a secret:",
              file=sys.stderr)
        _print_flagged(flagged)
        print(
            "\nNothing was copied. Remove the flagged content (or move it out of "
            ".claude/memory/ entirely -- a memory file should record a LESSON, not "
            "paste the credential that taught it) and retry. Pass --allow-flagged-skip "
            "to push everything else while leaving only the flagged file(s) behind, "
            "if you have already reviewed them by hand.",
            file=sys.stderr,
        )
        return 1

    to_copy = [n for n in candidates if n not in flagged]
    skipped_note = ""
    if flagged and allow_flagged_skip:
        skipped_note = f" ({len(flagged)} flagged file(s) skipped, see below)"
        print(f"sync_memory: skipping {len(flagged)} flagged file(s):")
        _print_flagged(flagged)

    if not to_copy:
        return 0

    copy_over(src, dst, to_copy)
    summary = f"sync_memory: {label}, {len(new) - len([n for n in new if n in flagged])} new, " \
              f"{len(changed) - len([n for n in changed if n in flagged])} updated{skipped_note}"
    if only_dst:
        summary += f" ({len(only_dst)} present only in destination, left alone)"
    # A hook's stdout is only surfaced to the user via systemMessage, so emit
    # the JSON shape Claude Code reads rather than a bare line nobody sees.
    print(json.dumps({"systemMessage": summary}))
    return 0


# ---------------------------------------------------------------------------
# Self-test: proves the scanner itself still works, without touching any
# real memory file or the filesystem beyond nothing at all. This is what
# `--self-test` runs, and it is also imported directly by
# scripts/tests/test_sync_memory.py so the same fixtures back both the CLI
# affordance and the real pytest suite.
# ---------------------------------------------------------------------------

# (should_flag, sample_text, expected_pattern_name_substring_or_None)
_SELF_TEST_CASES: tuple[tuple[bool, str, str | None], ...] = (
    (True, "contact me at raaif.yousuf@vanderbilt.edu for details", "email"),
    (True, "ya29.a0AfH6SMBxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "google-oauth-access-token"),
    (True, "AIzaSyD1234567890abcdefghijklmnopqrstuv", "google-api-key"),
    (True, "GOCSPX-abcdefghijklmnopqrstuvwx1234", "google-oauth-client-secret"),
    (True, 'client_secret: "abcdefghijklmnop12345678"', "oauth-client-secret-field"),
    (True, "ghp_abcdefghijklmnopqrstuvwxyz0123456789", "github-token"),
    (True, "AKIAIOSFODNN7EXAMPLE", "aws-access-key-id"),
    (True, "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dQw4w9WgXcQ_abc123", "jwt"),
    (True, 'password = "hunter2hunter2hunter2"', "generic-secret-field"),
    (True, "-----BEGIN RSA PRIVATE KEY-----", "private-key-material"),
    (True, "the project number is 123456789012", "gcp-project-number"),
    (True, "billing account 015919-31E210-2E7E0B is linked", "gcp-billing-account-id"),
    (True, r"C:\Users\username\DNA-Entropy-Graph\worker", "windows-user-home-path"),
    (True, "/home/username/DNA-Entropy-Graph/worker", "posix-user-home-path"),
    (False, "the fix was in worker/src/dna_entropy/cli.py, see #123", None),
    (False, "evo2 returns a nested tuple, unwrap it before indexing", None),
    (False, r"<user-home>\DNA-Entropy-Graph is the redacted placeholder form", None),
    (False, "the L4 GPU costs about $0.85/h on demand", None),
)


def self_test() -> bool:
    ok = True
    for should_flag, text, expect_name in _SELF_TEST_CASES:
        hits = find_secrets(text)
        flagged = bool(hits)
        if flagged != should_flag:
            ok = False
            print(f"FAIL  expected flagged={should_flag} got={flagged}  text={text!r}")
            continue
        if should_flag and expect_name and not any(h.pattern_name == expect_name for h in hits):
            ok = False
            got = [h.pattern_name for h in hits]
            print(f"FAIL  expected pattern {expect_name!r} in {got} for text={text!r}")
            continue
        print(f"ok    flagged={flagged!s:<5} text={text[:60]!r}")

    names = [spec.name for spec in SECRET_PATTERNS]
    if len(names) != len(set(names)):
        ok = False
        print(f"FAIL  duplicate pattern name(s) in SECRET_PATTERNS: {names}")

    print(f"\n{'PASS' if ok else 'FAIL'}: {len(_SELF_TEST_CASES)} case(s), "
          f"{len(SECRET_PATTERNS)} pattern(s) registered")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--pull", action="store_true", help="mirror -> live (SessionStart)")
    g.add_argument("--push", action="store_true", help="live -> mirror, secret-scanned (Stop)")
    g.add_argument("--status", action="store_true", help="report drift both ways, change nothing")
    g.add_argument("--self-test", action="store_true",
                    help="run the secret scanner against its own fixtures and exit")
    g.add_argument("--list-patterns", action="store_true",
                    help="print every registered secret pattern and exit")
    ap.add_argument("--dry-run", action="store_true",
                     help="with --push or --pull: report what would happen (including which "
                          "files a push would refuse) without writing anything")
    ap.add_argument("--allow-flagged-skip", action="store_true",
                     help="with --push: copy every clean file and skip only the flagged one(s), "
                          "instead of the default of refusing the whole push")
    args = ap.parse_args(argv)

    if args.list_patterns:
        for spec in SECRET_PATTERNS:
            print(f"{spec.name:<28} [{spec.category}]  {spec.why}")
        return 0

    if args.self_test:
        return 0 if self_test() else 3

    if args.allow_flagged_skip and not args.push:
        ap.error("--allow-flagged-skip only applies to --push")

    if args.status:
        rc_pull = run("pull", apply_changes=False)
        print()
        rc_push = run("push", apply_changes=False)
        return rc_pull or rc_push

    direction = "pull" if args.pull else "push"
    return run(
        direction,
        apply_changes=True,
        dry_run=args.dry_run,
        allow_flagged_skip=args.allow_flagged_skip,
    )


if __name__ == "__main__":
    sys.exit(main())
