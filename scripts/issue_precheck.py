#!/usr/bin/env python
"""
scripts/issue_precheck.py -- has this issue already been done?

    python scripts/issue_precheck.py <n> [<n> ...]
    python scripts/issue_precheck.py --all-open          # sweep every open issue
    python scripts/issue_precheck.py --all-open --suspect-only

Run it BEFORE starting work on a GitHub issue, and before closing one.

Why this exists
---------------
On the project this script came from (a private repo, mined for shape only;
see docs/migration/2026-09-19-clair-conventions-inventory.md), the same cost
got paid repeatedly, in both directions: a sweep once closed nine issues, six
of which turned out to be already fixed and never marked; an agent once spent
a full run rebuilding a feature before finding it had shipped the previous
day; an issue was once fully implemented, tested, and named in its own commit
message, and sat open anyway, found only by accident. And the opposite
failure: issues were closed on "code evidence" with no commit actually doing
the work, both disproven the next day.

Both are the same missing step: nobody asked the repository what it already
knows about the issue number. The repository always knew.

What it decides on
------------------
The verdict rests on ONE fact, not on reading English:

    did any commit whose message names #N actually change source files?

That distinguishes the two cases that matter and that a text search cannot:

    "feat(#N): ..."       touching several source files  -> the work landed
    "docs: ... see #N"    touching only docs/            -> the issue was discussed

An earlier draft of this script tried to tell an implementation comment from a
forward reference (something like "peeling those primitives out is #N") with
a keyword regex. It was wrong on three of the first five real inputs, because both kinds
of comment are written in the same voice. The commit graph is not a heuristic
and does not need a word list kept up to date.

Tree mentions are still reported -- with the matching line -- because they are
the single most useful place to START the work. They just do not drive the
verdict.

What it CANNOT see -- read this before trusting a verdict
-------------------------------------------------------------------------
This script answers one narrow, mechanical question well: did a commit whose
MESSAGE names #N change a source file. Three recorded blind spots follow
directly from how narrow that is, and none of them are fixed by this script
being cleverer about English -- the fix each time was either "widen what
counts as the message" or "say plainly what was not checked":

    1. It cannot see a milestone, an EPIC's do-not-build list, or an owner
       hold recorded only in a comment. On the project this script came from,
       an issue once read `OK -- OPEN with no trace anywhere. Safe to start.`
       while its milestone was the deferred one and an owner comment said to
       hold the line. The verdict below now prints the milestone and any
       post-v1 label next to the state, and says DEFERRED when either says
       so -- but a hold recorded ONLY as prose in a comment, with no
       milestone or label set, is still invisible. Read the thread
       regardless of what this script says.
    2. The commit matcher must search the commit BODY as well as the
       SUBJECT: searching the subject alone misses a fix referenced
       mid-paragraph in the body (a commit whose body opens with the real
       issue reference while its own subject line names something else
       entirely), and a closed issue with only that commit reads as
       `SUSPECT -- no commit naming it ever changed a source file`, the
       closed-with-no-trace shape in reverse. Fixed by matching subject+body
       together; see `scan_commits`.
    3. It matches the literal issue number, so it cannot see a fix that
       landed under a DIFFERENT issue number entirely (one issue half-fixed
       by another issue's commits, which never mention the first). The
       `related` line below surfaces GitHub's own cross-reference graph (an
       issue's body/comments mentioning another, in either direction) for
       exactly the issues where this script itself found no implementing
       commit -- the cases where "nothing shipped" and "shipped under a
       different number" are otherwise indistinguishable. It is a candidate
       list, not a verdict: GitHub only knows about an explicit `#N`
       mention, so two issues fixed by the same commit with neither number
       ever written in the other's thread stay invisible. CONFIRMED the same
       night this fix landed, on the project this script came from: one
       issue's exact bug was fixed by a single batch commit auditing several
       unrelated guards, whose subject named a different issue entirely; the
       first issue's body never mentioned the second, and the second was
       itself a different, unrelated issue. No `#N` text connects them
       ANYWHERE -- not a matcher gap, not a cross-reference gap, just no
       trace to find. This is the residual case the standing footer exists
       for, not a fourth lookup to build.

Exit codes
----------
    0   nothing suspicious
    1   at least one issue looks mis-filed (open-but-done, or closed-with-no-trace)
    2   bad usage / a dependency (git) is missing

`gh` is only needed for `--all-open`, for open/closed state, and for the
milestone/label/related-issue lookups above. Without it the script still runs
and reports state as "unknown", with no milestone or related-issue data.

Why `--all-open` produced zero bytes at backlog scale (issue #307)
--------------------------------------------------------------------
MEASURED 2026-09-19, on this repo's own ~270-issue backlog: the root cause
was buffering, not the network. `stdout` redirected to a file (not a TTY,
which is exactly what `--all-open > out.txt` and a backgrounded run both
are) is fully block-buffered by Python's default -- nothing reaches the file
until either the internal buffer fills or the process exits. A 30-issue
timed run confirmed this directly: the output file measured 0 bytes at 48
of the run's ~64 total seconds, then jumped to its full ~28 KB essentially
at once. The run was never hung; it was working the entire time and saying
nothing. Fixed by putting `stdout` in line-buffered mode
(`sys.stdout.reconfigure(..., line_buffering=True)`, right where the
existing encoding reconfigure already runs) and by restructuring `main()`
to print and flush each issue's report as soon as that issue is evidence-
gathered, instead of gathering evidence for every issue first and only
printing at the end -- the second half matters on its own even with
line-buffering, because a version that still buffers ALL issues in memory
before the first `print()` call would still show nothing until the whole
sweep finished.

The SPEED problem was real but secondary, and came from the same shape
twice: `fill_reviewed_shas`, `fill_related_issues` and `fill_node_id_matches`
each independently re-fetched `gh issue view --repo <repo> --json
body,comments` (or just `body`) for the SAME issue when more than one of
them fired for it -- up to three redundant network round-trips per gated
issue. They now share one `get_issue_thread()` fetch per issue, and when
`--all-open`'s own bulk `gh issue list` call already carries `body` and
`comments` (it does, as of this fix: `gh issue list --json` supports both
fields directly, so the whole backlog's thread text arrives in the SAME one
call `gh_list` already made for state/milestone/labels), none of the three
needs a further per-issue call AT ALL. `--limit` and `--label` (see `--help`)
bound the normal case to a few dozen issues rather than the whole backlog,
which is what most invocations actually want.
"""

from __future__ import annotations

import argparse
import ast
import json
import re
import subprocess
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

# A piped stdout on Windows defaults to cp1252, and commit messages and issue
# titles are arbitrary user text (an arrow U+2192, an em dash, anything).
# `--all-open` must never die mid-sweep with UnicodeEncodeError over a title
# it does not control; the report must never be the thing that fails on them.
# `line_buffering=True` on stdout specifically is the fix for issue #307:
# redirected to a file or a pipe (not a TTY -- exactly `--all-open > out.txt`,
# or any backgrounded run), Python's default is fully block-buffered, so
# nothing reaches the file until the internal buffer fills or the process
# exits. See the module docstring's own "Why --all-open produced zero bytes"
# section for the measurement. stderr does not get the same treatment: it is
# progress/diagnostic chatter only (see --progress), never the report itself,
# and Python's own stderr is unbuffered/line-buffered by default already.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
except (AttributeError, ValueError):  # already-wrapped or non-reconfigurable
    pass
try:
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except (AttributeError, ValueError):
    pass

REPO = "Raaif-Yousuf/DNA-Entropy-Graph"

# How far back to read the log. The repo's issue numbers only reach the low
# hundreds, so a few thousand commits covers every referenceable issue while
# keeping a sweep of ~50 issues to one `git log` call.
LOG_DEPTH = "4000"

# Frozen history is excluded on purpose, exactly as `.ignore` excludes it from
# ripgrep: it is 44% of all markdown under docs/ and matches the same words as
# the docs describing how things work today. An issue number appearing only in
# a 2026-07 sprint entry is not evidence about the code that ships now.
EXCLUDED_PREFIXES = (
    "docs/archive/",
    "docs/old_sprint_log.md",
)

EXCLUDED_SUFFIXES = (
    ".png", ".jpg", ".jpeg", ".ico", ".svg", ".pdf", ".woff", ".woff2",
    ".lock", ".duckdb", ".parquet", ".onnx", ".zip", ".exe",
)

# Paths that count as "source" when deciding whether a commit did work. A
# commit touching only these is documentation, and documentation naming an
# issue is a citation, not a fix.
DOC_PREFIXES = ("docs/", "README", "CLAUDE.md", "NEXT_SESSION.md", "AGENTS.md",
                "FEATURES.md")


# A commit that names one issue while its diff is really about another is a
# real, recorded failure mode: on the project this script came from, a commit
# on a branch named for one issue cited that issue in its message while its
# entire diff was actually a different issue's subject (unrelated wall-clock
# test fixes), and it took three separate `git show` calls to learn that.
# True topic relevance is not this script's job (the whole reason it reads
# the commit graph instead of English is to avoid guessing at meaning), but a
# commit citing MORE than one issue number is a cheap, mechanical tell that
# the reader should check both before trusting the diff as evidence for
# either. Same trailing-lookahead requirement as the per-issue query patterns
# below: the digits of issue 52 must never be read out of issue 521's own,
# longer digit string.
_ISSUE_REF_RE = re.compile(r"#(\d+)(?![0-9])")


def _issue_refs(text: str) -> list[int]:
    """Every #NNNN reference in `text`, deduped and sorted."""
    return sorted({int(m) for m in _ISSUE_REF_RE.findall(text)})


def _is_doc(path: str) -> bool:
    p = path.replace("\\", "/")
    return p.startswith(DOC_PREFIXES) or p.endswith(".md")


# EXCLUDED_SUFFIXES is a hand-maintained list, and a hand-maintained
# denominator is a recorded bug shape in its own right: an issue can read as
# SUSPECT because its own digits happen to occur as a byte sequence inside a
# vendored binary with a suffix nobody had added to the tuple yet. Rather
# than keep extending it one extension at a time, sniff actual
# content the same way `git` and `diff` decide "binary": a NUL byte in the
# first few KB. Cheap (one small read, no full-file decode) and does not need
# a new entry every time a new binary type lands in the tree.
_BINARY_SNIFF_BYTES = 8000


def _looks_binary(sample: bytes) -> bool:
    return b"\x00" in sample


def _classify(path: str) -> str:
    """Which evidence bucket a repo-relative path falls into."""
    p = path.replace("\\", "/")
    if "/tests/" in p:
        return "tests"
    if _is_doc(p):
        return "docs"
    if p.startswith(("app/", "worker/", "scripts/")):
        return "code"
    return "docs"


@dataclass
class Commit:
    sha: str
    subject: str
    # First paragraph of the commit body, filled in lazily for matched commits
    # only. A repo that writes long, decision-bearing commit messages can have
    # a body that says outright "issue X was rescoped, issues Y/Z closed as
    # already-shipped" -- which is the exact verdict a reader would otherwise
    # go derive from diffs.
    body: str = ""
    source_files: list[str] = field(default_factory=list)
    doc_files: list[str] = field(default_factory=list)
    # Is this commit an ancestor of the current HEAD? `git log --all` reaches
    # every ref including unmerged branches, and a repo worked by several
    # agents in parallel will carry a few at any given time. Work sitting on
    # one of those is BUILT but not SHIPPED -- a completely different state
    # from done, and reporting it as done would recreate exactly the mistake
    # this script exists to prevent.
    merged: bool = True

    @property
    def touched_source(self) -> bool:
        return bool(self.source_files)


@dataclass
class IssueMeta:
    """What `gh` told us about one issue number, beyond open/closed.

    On the project this script came from, an orchestrator once dispatched
    several lanes onto issues milestoned for later, because the precheck
    output never showed a milestone at all -- "safe to start" (a
    commit-graph fact) got read as "worth doing now" (a priority decision
    this script has no opinion on). Kept as its own type
    rather than two more bare fields on `Evidence` because `state`/`title`
    were already threaded through `known` as a 2-tuple in half a dozen call
    sites; giving that tuple a name here is what let the milestone/labels
    ride along the same bulk `gh issue list` call instead of a second
    per-issue round trip.
    """

    state: str = "unknown"          # open | closed | unknown
    title: str = ""
    milestone: str = ""             # "" means none set
    labels: tuple[str, ...] = ()
    # Body + every comment body, concatenated -- populated ONLY when the
    # bulk `gh issue list` call that built this IssueMeta also asked for
    # `body,comments` (see `gh_list`). None means "not fetched in bulk, ask
    # for it per-issue if you need it"; "" is a real, fetched, empty thread.
    # This is issue #307's own redundancy fix: fill_reviewed_shas,
    # fill_related_issues and fill_node_id_matches each used to re-fetch
    # this same text per issue independently; see `get_issue_thread`.
    thread_text: str | None = None

    @property
    def deferred(self) -> bool:
        """The owner has already decided this waits: milestone or label says so.

        Deliberately narrow -- a `post-v1` MILESTONE or LABEL is a
        structured field `gh` can hand back directly. An owner hold written
        only as comment prose (the module docstring's own recorded case) is
        not caught here; see its blind-spot list.
        """
        if self.milestone.strip().lower() == "post-v1":
            return True
        return any(l.strip().lower() == "post-v1" for l in self.labels)


@dataclass
class Evidence:
    number: int
    state: str = "unknown"          # open | closed | unknown
    title: str = ""
    milestone: str = ""
    labels: tuple[str, ...] = ()
    # See IssueMeta.thread_text: None until either bulk-prefetched (copied
    # from IssueMeta in `assemble_evidence`) or fetched once, lazily, by
    # `get_issue_thread`.
    thread_text: str | None = None
    commits: list[Commit] = field(default_factory=list)
    # bucket -> list of (repo-relative path, first matching line)
    hits: dict[str, list[tuple[str, str]]] = field(default_factory=lambda: defaultdict(list))
    in_totest: bool = False
    # Short SHAs of implementing commits that the issue thread already cites.
    # An issue whose own comments discuss the commit has been looked at and
    # left open on purpose -- reporting it identically to a genuinely forgotten
    # one is how a checker trains people to skim past it.
    reviewed_shas: list[str] = field(default_factory=list)
    # Other issue numbers this issue's own body/comments mention, or that
    # mention THIS issue elsewhere (GitHub's own cross-reference graph).
    # Populated only when this issue has no implementing commit of its own --
    # exactly the case where "nothing shipped" and "shipped under another
    # number" are otherwise indistinguishable. Each entry is
    # (number, state, title, direction).
    related: list[tuple[int, str, str, str]] = field(default_factory=list)
    # A fix commit that names NO issue number at all -- not even
    # mid-paragraph, not under a different issue's number. No text
    # search over commit messages can ever see that, because there is no
    # text to find. When this issue's title/body literally names a test
    # function or symbol, `fill_node_id_matches` looks for a commit whose
    # DIFF actually defines a function of that exact name (confirmed by
    # parsing the commit's own post-image with `ast`, not by grepping for
    # the string) and reports it here as a CANDIDATE -- like `related`, it
    # never changes `verdict()` on its own; read it before trusting "no
    # trace"/"safe to start" above. Each entry is (symbol, sha, path).
    node_id_matches: list[tuple[str, str, str]] = field(default_factory=list)

    @property
    def deferred(self) -> bool:
        if self.milestone.strip().lower() == "post-v1":
            return True
        return any(l.strip().lower() == "post-v1" for l in self.labels)

    @property
    def implementing_commits(self) -> list[Commit]:
        """Source-changing commits that are actually on the current branch."""
        return [c for c in self.commits if c.touched_source and c.merged]

    @property
    def unmerged_commits(self) -> list[Commit]:
        return [c for c in self.commits if c.touched_source and not c.merged]

    @property
    def any_trace(self) -> bool:
        return bool(self.commits or self.hits)

    def verdict(self) -> tuple[str, str]:
        """(level, explanation). level is OK | SUSPECT | NOTE."""
        impl = self.implementing_commits

        if self.state == "open":
            if impl:
                n = len(impl)
                if self.reviewed_shas and all(c.sha in self.reviewed_shas for c in impl):
                    return (
                        "REVIEWED",
                        "OPEN with source-changing commits, but the issue thread "
                        "already cites every one of them -- somebody looked and left "
                        "it open deliberately. Read the thread, not the diff.",
                    )
                return (
                    "SUSPECT",
                    f"OPEN, but {n} commit(s) naming it changed source files. "
                    "This is the exact shape a fully-built-but-never-closed issue "
                    "has. Read the commit(s) below before writing anything.",
                )
            if self.unmerged_commits:
                return (
                    "SUSPECT",
                    "OPEN, and source-changing commits naming it exist on a branch "
                    "that is NOT merged into HEAD. The work may already be written. "
                    "Check the branch out before rewriting it.",
                )
            if self.commits:
                return (
                    "NOTE",
                    "OPEN. Commits name it but changed only docs -- it has been "
                    "discussed, not built. Safe to start.",
                )
            if self.hits.get("code") or self.hits.get("tests"):
                return (
                    "NOTE",
                    "OPEN, no commit has ever named it, but source files mention it. "
                    "Those lines are where the gap is documented -- start there.",
                )
            return ("OK", "OPEN with no trace anywhere. Safe to start.")

        if self.state == "closed":
            if not self.any_trace:
                return (
                    "SUSPECT",
                    "CLOSED with NO trace anywhere -- no commit, no code, no docs. "
                    "This is exactly how an issue gets wrongly closed. Verify or reopen.",
                )
            if not impl:
                return (
                    "SUSPECT",
                    "CLOSED, but no commit naming it ever changed a source file. "
                    "Either the fix landed under an unrelated message, or nothing "
                    "shipped. Confirm before trusting it.",
                )
            if self.in_totest:
                return ("OK", "CLOSED, a commit changed source, and a ToTest row is open. Correct state.")
            return ("OK", "CLOSED with a source-changing commit.")

        # state unknown (no gh)
        return ("NOTE", "State unknown (no `gh` available); evidence listed below.")


def _run(cmd: list[str], cwd: Path) -> str:
    try:
        out = subprocess.run(
            cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace"
        )
    except FileNotFoundError:
        return ""
    return out.stdout if out.returncode == 0 else ""


def repo_root(explicit: str | None = None) -> Path:
    """The checkout to scan.

    Resolved from the CURRENT DIRECTORY, not from where this file lives. Those
    differ in exactly the case that matters: an agent working in a git worktree
    (see scripts/setup_agent_worktree.ps1) invokes the primary checkout's copy
    of this script. Anchoring on __file__ would silently scan the primary
    checkout and report on code the agent cannot see -- the same class of
    mistake as Rule 24's Path(__file__).parent.parent anchoring.
    """
    start = Path(explicit).resolve() if explicit else Path.cwd()
    root = _run(["git", "rev-parse", "--show-toplevel"], start).strip()
    if not root:
        print(f"error: {start} is not inside a git repository", file=sys.stderr)
        sys.exit(2)
    return Path(root)


def tracked_files(root: Path) -> list[str]:
    files = []
    for line in _run(["git", "ls-files"], root).splitlines():
        line = line.strip()
        if not line or line.startswith(EXCLUDED_PREFIXES):
            continue
        if line.lower().endswith(EXCLUDED_SUFFIXES):
            continue
        files.append(line)
    return files


def _git_common_dir(root: Path) -> Path:
    """The shared `.git` directory, resolved the way that works for BOTH a
    plain checkout (where `.git` is a real directory) and a linked worktree
    (where `.git` is a FILE pointing elsewhere) -- `root / ".git"` is not a
    directory in the second case, so a cache write there would fail. The
    commit graph itself is shared across every worktree of the same repo, so
    a cache built from one worktree's scan is valid to read from any other
    (they can only disagree on which BRANCH is checked out, and the cache key
    below is the HEAD sha, not the worktree path)."""
    rel = _run(["git", "rev-parse", "--git-common-dir"], root).strip()
    if not rel:
        return root / ".git"
    p = Path(rel)
    return p if p.is_absolute() else (root / p)


def _commit_scan_cache_path(root: Path) -> Path:
    return _git_common_dir(root) / "issue_precheck_cache" / f"commits-n{LOG_DEPTH}.json"


def _parse_commit_records(root: Path) -> list[dict]:
    """One `git log` pass over every ref, with the file list AND body per
    commit, as plain JSON-able dicts -- independent of any issue's regex, so
    the result can be cached and re-matched against a DIFFERENT set of issue
    numbers without re-running `git log`.

    Matching only `%s` (the subject) misses a commit whose subject describes
    the change in its own words and whose BODY opens with the real issue
    reference. A closed issue whose only implementing commit references it
    that way used to read as
    `SUSPECT -- no commit naming it ever changed a source file`.

    The body can't just be appended to the per-commit format line the way the
    subject is: `%b` contains arbitrary text with its own blank lines, and
    `--name-only` already uses a blank line to separate the message from the
    file list, so the two would become indistinguishable. Instead each record
    is terminated with an explicit `\x02\x03` sentinel right after `%b`
    (neither byte is legal in text a person would type), so the body -- blank
    lines and all -- can be pulled out with one `str.partition` before the
    trailing file list is read line by line, in the same single `git log`
    pass this always ran as.
    """
    raw = _run(
        [
            "git", "log", "--all", "--no-decorate", "--name-only",
            "--format=\x01%H\x02%h\x02%s\x02%b\x02\x03", "-n", LOG_DEPTH,
        ],
        root,
    )

    # One pass to learn what is actually on the current branch, rather than a
    # `git merge-base --is-ancestor` subprocess per matched commit.
    on_head = set(_run(["git", "rev-list", "-n", LOG_DEPTH, "HEAD"], root).split())

    records: list[dict] = []
    for chunk in raw.split("\x01"):
        if not chunk:
            continue
        try:
            full, short, subject, rest = chunk.split("\x02", 3)
        except ValueError:
            continue  # malformed record -- skip it rather than crash the sweep
        body_raw, sep, tail = rest.partition("\x02\x03")
        if not sep:
            continue  # sentinel missing -- truncated output, same treatment
        records.append({
            "full": full, "short": short, "subject": subject, "body_raw": body_raw,
            "files": [p.strip() for p in tail.splitlines() if p.strip()],
            "merged": full in on_head,
        })
    return records


def _cached_commit_records(root: Path, use_cache: bool = True) -> list[dict]:
    """`_parse_commit_records`, cached on disk keyed by the repo's current
    HEAD sha plus `LOG_DEPTH`. MEASURED 2026-09-13, on the project this
    script came from: `--all-open` against 263 open issues took ~19 minutes,
    and the caller-side workaround of batching issue numbers into groups of
    40 made it WORSE, not better -- `scan_commits` re-ran this exact
    invariant `git log --all` pass once per batch regardless of how many
    numbers were in it. The fix is not smarter batching, it's recognising the
    pass doesn't depend on the issue numbers at all: cache it once per HEAD,
    and every subsequent invocation in the same session (a per-lane
    `issue_precheck.py <n>` before every dispatch) reads the cache instead of
    re-running `git log`.

    Invalidation is a straight HEAD-sha compare: a stale cache is a "check
    that cannot fail" risk, so a mismatch -- including "no cache file yet" or
    "the file is corrupt" -- always falls through to a real rescan rather
    than ever serving a guess. `--all` still scans every ref, not just HEAD,
    so a cache built on one HEAD could in principle miss a commit that only
    landed on some OTHER branch since; that gap is accepted deliberately
    rather than keying on a hash of every ref, which would need a full
    `git for-each-ref` walk just to compute the cache key -- most of the cost
    this cache exists to avoid.
    """
    head = _run(["git", "rev-parse", "HEAD"], root).strip()
    cache_path = _commit_scan_cache_path(root)
    if use_cache and head:
        try:
            data = json.loads(cache_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            data = None
        if isinstance(data, dict) and data.get("head") == head and data.get("log_depth") == LOG_DEPTH:
            records = data.get("commits")
            if isinstance(records, list):
                return records

    records = _parse_commit_records(root)
    if use_cache and head:
        try:
            cache_path.parent.mkdir(parents=True, exist_ok=True)
            cache_path.write_text(
                json.dumps({"head": head, "log_depth": LOG_DEPTH, "commits": records}),
                encoding="utf-8",
            )
        except OSError:
            pass  # best-effort: a cache write failure must never break a real scan
    return records


def scan_commits(
    root: Path, patterns: dict[int, re.Pattern], use_cache: bool = True,
) -> dict[int, list[Commit]]:
    """Match `patterns` against the (possibly cached) parsed commit
    log. Splitting the expensive, issue-independent `git log` pass
    (`_cached_commit_records`) from this cheap per-issue regex match is what
    makes the cache reusable across a DIFFERENT set of issue numbers, not
    just a repeated identical call."""
    found: dict[int, list[Commit]] = defaultdict(list)

    for rec in _cached_commit_records(root, use_cache=use_cache):
        subject, body_raw = rec["subject"], rec["body_raw"]
        # Subject AND body: see _parse_commit_records's docstring for why
        # body must count.
        matched = [n for n, pat in patterns.items() if pat.search(subject) or pat.search(body_raw)]
        if not matched:
            continue

        body = body_raw.split("\n\n")[0].strip() if body_raw else ""
        commit = Commit(sha=rec["short"], subject=subject, body=body, merged=rec["merged"])
        for path in rec["files"]:
            (commit.doc_files if _is_doc(path) else commit.source_files).append(path)
        for n in matched:
            found[n].append(commit)

    return found


def _issue_meta_from_json(data: dict, fallback_state: str = "") -> IssueMeta:
    milestone = data.get("milestone") or {}
    labels = data.get("labels") or []
    # `body`/`comments` are present only when the caller's `gh --json` field
    # list asked for them (gh_list's bulk call does; gh_state's per-issue
    # state-only call does not) -- absence (the field missing entirely from
    # `data`) means "not fetched", kept as None; PRESENT but empty is a real,
    # fetched, empty thread and must stay "" so `get_issue_thread` never
    # re-fetches it.
    thread_text = None
    if "body" in data or "comments" in data:
        thread = str(data.get("body") or "") + "\n"
        thread += "\n".join(str(c.get("body") or "") for c in data.get("comments") or [])
        thread_text = thread
    return IssueMeta(
        state=str(data.get("state", fallback_state)).lower(),
        title=data.get("title", ""),
        milestone=str(milestone.get("title", "") if isinstance(milestone, dict) else ""),
        labels=tuple(
            str(l.get("name", "")) for l in labels if isinstance(l, dict)
        ),
        thread_text=thread_text,
    )


def gh_state(numbers: list[int], root: Path,
             known: dict[int, IssueMeta] | None = None) -> dict[int, IssueMeta]:
    """issue number -> IssueMeta (state, title, milestone, labels).

    `known` carries anything a bulk `gh issue list` already told us. A sweep of
    50 issues would otherwise make 50 network round-trips to re-learn a fact the
    list call returned in one, which made `--all-open` take minutes.
    """
    states: dict[int, IssueMeta] = dict(known or {})
    for n in numbers:
        if n in states:
            continue
        raw = _run(
            ["gh", "issue", "view", str(n), "--repo", REPO,
             "--json", "state,title,milestone,labels"], root
        )
        if not raw:
            continue
        try:
            data = json.loads(raw)
        except json.JSONDecodeError:
            continue
        states[n] = _issue_meta_from_json(data)
    return states


def get_issue_thread(root: Path, n: int, e: Evidence) -> str:
    """Body + every comment body, concatenated -- the one fetch
    `fill_reviewed_shas_one`, `fill_related_issues_one` (its outbound half)
    and `fill_node_id_matches_one` all need, shared instead of each of them
    independently re-fetching it (issue #307). `e.thread_text` is already
    populated when the bulk `gh_list` sweep asked for `body,comments` (the
    `--all-open` and `--label` paths); this only calls `gh` when that is
    `None` (an explicit-numbers-only run whose state came from `gh_state`'s
    lighter per-issue call, which does not fetch the thread). The result is
    cached back onto `e.thread_text` so a second call this run, if any,
    never re-fetches either.
    """
    if e.thread_text is not None:
        return e.thread_text
    raw = _run(["gh", "issue", "view", str(n), "--repo", REPO, "--json", "body,comments"], root)
    thread = ""
    if raw:
        try:
            data = json.loads(raw)
            thread = str(data.get("body") or "") + "\n"
            thread += "\n".join(str(c.get("body") or "") for c in data.get("comments") or [])
        except json.JSONDecodeError:
            pass
    e.thread_text = thread
    return thread


def fill_reviewed_shas_one(root: Path, n: int, e: Evidence) -> None:
    """Note which implementing commits the issue's own thread already cites.

    Fetched ONLY for issues that would otherwise report SUSPECT. A sweep of the
    whole backlog would otherwise pay a comments round-trip per issue to learn
    something that changes nothing for the ~85% that are not suspect.
    """
    if e.state != "open" or not e.implementing_commits:
        return
    thread = get_issue_thread(root, n, e)
    e.reviewed_shas = [c.sha for c in e.implementing_commits if c.sha in thread]


def gh_list(root: Path, state: str, limit: int = 400, label: str | None = None) -> dict[int, IssueMeta]:
    """Every issue in `state`, as number -> IssueMeta, in one call.

    Milestone and labels ride along in the SAME bulk call: `gh issue
    list --json` accepts them exactly like `number,title,state`, so showing
    the milestone next to every verdict costs nothing extra over a sweep that
    was already making this one call.

    So do `body` and `comments` (issue #307): `gh issue list --json` accepts
    them exactly the same way, and doing so here means `fill_reviewed_shas`,
    `fill_related_issues` and `fill_node_id_matches` need NO further
    per-issue `gh issue view` call at all for anything this bulk sweep
    already covers -- see `get_issue_thread` and the module docstring's own
    "Why --all-open produced zero bytes" section for the redundancy this
    replaced (up to three separate re-fetches of the same text per issue).

    `label` filters server-side via `gh issue list --label`, which is how
    `--label` scopes a run to one area without this script doing its own
    filtering after the fact.
    """
    cmd = ["gh", "issue", "list", "--repo", REPO, "--state", state,
           "--limit", str(limit), "--json", "number,title,state,milestone,labels,body,comments"]
    if label:
        cmd += ["--label", label]
    raw = _run(cmd, root)
    if not raw:
        return {}
    return {
        int(i["number"]): _issue_meta_from_json(i, fallback_state=state)
        for i in json.loads(raw)
    }


def fill_related_issues_one(root: Path, n: int, e: Evidence, ev: dict[int, Evidence]) -> None:
    """Surface a fix that landed under a DIFFERENT issue number.

    This script's verdict rests entirely on the literal `#N` appearing in a
    commit message. Two shapes are both real and recorded, on the project
    this script came from: issue A was half-fixed by issue B's commits,
    which never once wrote "#A" -- the two were linked only because issue
    B's own ISSUE BODY said "supersedes #A" and GitHub tracks that as a
    cross-reference. The reverse also happened: a fix shipped under issue C
    whose own body named issue D, while D's own body never named C anywhere
    reachable except via GitHub's cross-reference graph.

    So two lookups, not one:
      * OUTBOUND -- numbers THIS issue's own body/comments mention. Free of
        any extra API surface: it is exactly the text `fill_reviewed_shas`
        already fetches for a different reason, just read differently. This
        catches the C -> D shape above.
      * INBOUND -- other issues/PRs whose body or comments mention THIS
        issue, from `gh api .../timeline`'s `cross-referenced` events. This
        catches the A -> B shape above, which no outbound scan of A's own
        body could ever see, because A never mentions B.

    Gated on `not e.implementing_commits`: once an issue already has its OWN
    implementing commit, whether some other issue also mentions it is not
    interesting -- the exact case this function exists for is exactly the one
    where this script would otherwise report "no trace" or "safe to start."
    That is also what keeps the cost bounded on `--all-open`: the ~85% of
    issues that already show a clean implementing commit make none of the
    extra round trips below.
    """
    if e.state == "unknown" or e.implementing_commits:
        return

    # number -> (direction, state, title). Populated with state/title
    # already in hand where possible: the timeline event carries both for
    # free, and `ev` already knows them for any sibling already processed
    # this run. Only an outbound-only number (found solely in this issue's
    # OWN body/comments, which give a number but nothing else) needs a
    # further lookup.
    candidates: dict[int, tuple[str, str, str]] = {}

    raw = _run(
        [
            "gh", "api", f"repos/{REPO}/issues/{n}/timeline", "--paginate",
            "-q", '.[] | select(.event=="cross-referenced") | '
                  '[(.source.issue.number|tostring), .source.issue.state, '
                  '.source.issue.title] | join(":::")',
        ],
        root,
    )
    for line in raw.splitlines():
        parts = line.split(":::", 2)
        if len(parts) != 3:
            continue
        try:
            m = int(parts[0])
        except ValueError:
            continue
        if m != n:
            candidates[m] = ("mentions this issue elsewhere (GitHub cross-reference)",
                              parts[1], parts[2])

    thread = get_issue_thread(root, n, e)
    for m in _issue_refs(thread):
        if m == n or m in candidates:
            continue
        candidates[m] = ("this issue's own body/comments mention it", "", "")

    for m, (direction, other_state, other_title) in candidates.items():
        if not other_state:
            if m in ev:
                other_state, other_title = ev[m].state, ev[m].title
            else:
                raw = _run(
                    ["gh", "issue", "view", str(m), "--repo", REPO,
                     "--json", "state,title"], root
                )
                other_state, other_title = "unknown", ""
                if raw:
                    try:
                        data = json.loads(raw)
                        other_state = str(data.get("state", "")).lower()
                        other_title = data.get("title", "")
                    except json.JSONDecodeError:
                        pass
        e.related.append((m, other_state, other_title, direction))


# A blind spot distinct from `fill_related_issues`'s own: `related` needs
# SOME `#N` text connecting the two issues, ANYWHERE. On the project this
# script came from, one issue had none -- its fixing commit named three
# other issue numbers, never its own, and no other issue's thread ever
# named it either. The one thing that DID survive is the test's own name,
# because issue titles there routinely quote a failing test function
# verbatim in the title. A symbol name is not an issue number, so it needs
# its own, narrower mechanism.
#
# Deliberately conservative, per the owner's own framing: a false "already
# done" here costs more than a false "safe to start" does, so this NEVER
# upgrades `verdict()` on its own -- it is reported the same way `related`
# is, as a candidate the reader must go look at.
_NODE_ID_RE = re.compile(r"\btest_[A-Za-z0-9_]{8,}\b")

# How many candidate commits the pickaxe search hands to the AST check, per
# symbol. Small on purpose: this only ever runs for the rare issue whose
# title/body literally contains a test_*-shaped identifier (gated further by
# `not e.implementing_commits`, same as `fill_related_issues`), so the cost
# is bounded by how often that happens, not by sweep size.
_NODE_ID_CANDIDATE_LIMIT = 5

# The real per-symbol cost is `find_node_id_matches`'s own `git log
# --all -S"def {symbol}("` pickaxe, MEASURED 2026-09-18 at ~7-10s PER CALL,
# on the project this script came from, against a history of several
# thousand commits -- not the file-tree AST scan
# `_find_defining_files` used to redo per symbol (that redundant rescan is
# fixed separately by `_cached_function_index`, but caching cannot help the
# pickaxe itself: each symbol searches genuinely different content, so there
# is nothing to reuse between them the way there is across repeated calls for
# the SAME symbol). A single real issue in a profiled 10-issue sample named
# 13 distinct test_*-shaped identifiers in its own body, worth ~90-130s of
# pickaxe time alone. This is a best-effort CANDIDATE list (see this module's
# own docstring above `fill_node_id_matches`: it never changes `verdict()`),
# so bounding how many distinct symbols one issue can spend pickaxe time on
# trades a rare, unbounded worst case for a small, deliberate gap -- an issue
# naming more than this many symbols still gets the first few checked.
_NODE_ID_MAX_SYMBOLS_PER_ISSUE = 8


def extract_node_id_candidates(title: str, body: str) -> list[str]:
    """Test-function-shaped identifiers named in an issue's own title/body,
    in the order they first appear, deduplicated, capped at
    `_NODE_ID_MAX_SYMBOLS_PER_ISSUE` (see that constant's own
    comment for why). `test_` is chosen over a bare `[A-Za-z_]+` symbol
    pattern on purpose: this repo's test names are long and specific (the
    8-char minimum after the prefix), so false positives from ordinary prose
    are rare, whereas a bare identifier pattern would match nearly every noun
    phrase in an issue body."""
    seen: list[str] = []
    for m in _NODE_ID_RE.finditer(f"{title}\n{body}"):
        if len(seen) >= _NODE_ID_MAX_SYMBOLS_PER_ISSUE:
            break
        if m.group(0) not in seen:
            seen.append(m.group(0))
    return seen


def _defines_function(source: str, name: str) -> bool:
    """True if `source` parses as Python and defines a top-level OR nested
    function/method named exactly `name`. Parsing (not `f"def {name}"` text
    matching) is what tells a real definition apart from the same string
    sitting in a comment, a docstring, or a call site -- the whole reason
    the coordinator asked for AST here instead of a second pickaxe."""
    try:
        tree = ast.parse(source)
    except SyntaxError:
        return False
    for node in ast.walk(tree):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == name:
            return True
    return False


_NODE_ID_SCAN_DIRS = ("worker/tests", "worker/src/dna_entropy")


def _function_index_cache_path(root: Path) -> Path:
    return _git_common_dir(root) / "issue_precheck_cache" / "function_index.json"


def _build_function_index(root: Path) -> dict[str, list[str]]:
    """Every top-level or nested function/method NAME defined anywhere under
    `_NODE_ID_SCAN_DIRS`'s CURRENT (working-tree) content, mapped to the
    file(s) that define it -- one `ast.parse` per FILE, not one per candidate
    SYMBOL.

    MEASURED 2026-09-18, on the project this script came from: profiling a
    real 10-issue `--all-open`-shaped sample found `fill_node_id_matches`
    responsible for 284.93s of a 305.73s total (scan_commits 0.76s, the tree
    scan 1.85s, gh_list+gh_state 1.86s, fill_reviewed_shas 0.47s,
    fill_related_issues 15.85s) -- NOT the `gh` network calls that had been
    suspected. The old `_find_defining_files`
    re-read and re-`ast.parse`d every `.py` file under both scan dirs from
    scratch for EVERY candidate symbol on EVERY issue that lacked its own
    implementing commit, with no memoisation across either axis. Building
    this index once (one pass over the files, extracting every def in one
    `ast.walk`) turns that O(files x symbols) cost into O(files) + O(1) per
    lookup.
    """
    index: dict[str, list[str]] = defaultdict(list)
    for scan_dir in _NODE_ID_SCAN_DIRS:
        base = root / scan_dir
        if not base.is_dir():
            continue
        for p in sorted(base.rglob("*.py")):
            try:
                content = p.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            try:
                tree = ast.parse(content)
            except SyntaxError:
                continue
            rel = str(p.relative_to(root)).replace("\\", "/")
            for node in ast.walk(tree):
                if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    index[node.name].append(rel)
    return dict(index)


def _cached_function_index(root: Path, use_cache: bool = True) -> dict[str, list[str]]:
    """`_build_function_index`, cached on disk keyed by HEAD sha, the same
    shape and the same caveat as `_cached_commit_records`: this indexes the
    WORKING TREE, not a git object, so an uncommitted edit to a tracked file
    between two calls on the SAME HEAD is invisible until the next commit
    moves HEAD. That mirrors `_find_defining_files`'s own pre-existing
    docstring ("CURRENT working-tree content") -- this was already true of
    the uncached version's relationship to `git log`-based lookups elsewhere
    in this script, just newly worth stating now that a cache makes staleness
    possible instead of merely a hypothetical."""
    head = _run(["git", "rev-parse", "HEAD"], root).strip()
    cache_path = _function_index_cache_path(root)
    if use_cache and head:
        try:
            data = json.loads(cache_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            data = None
        if isinstance(data, dict) and data.get("head") == head:
            index = data.get("index")
            if isinstance(index, dict):
                return index

    index = _build_function_index(root)
    if use_cache and head:
        try:
            cache_path.parent.mkdir(parents=True, exist_ok=True)
            cache_path.write_text(json.dumps({"head": head, "index": index}), encoding="utf-8")
        except OSError:
            pass  # best-effort: a cache write failure must never break a real scan
    return index


def _find_defining_files(root: Path, symbol: str, use_cache: bool = True) -> list[str]:
    """Every file under `_NODE_ID_SCAN_DIRS` whose CURRENT (working-tree)
    content defines a function named `symbol`, via `ast.parse` -- never
    grep. Scoped to those two directories because that is where an issue
    title's symbol overwhelmingly lives in this repo. A lookup into
    `_cached_function_index`; see that function's docstring for why
    this used to be the dominant cost of `--all-open`."""
    return list(_cached_function_index(root, use_cache=use_cache).get(symbol, []))


def find_node_id_matches(root: Path, symbol: str, use_cache: bool = True) -> list[tuple[str, str]]:
    """Commits (sha, path) whose POST-image genuinely defines a function
    named `symbol` -- confirmed by `ast.parse`, never by grepping for the
    string. Two candidate sources, unioned, because a real fixing commit on
    the project this script came from is the case that rules out relying on
    just one:

      1. `git log -S"def {symbol}("` -- git's native pickaxe, catches a
         commit that ADDED or REMOVED the def line itself. Cheap and
         precise, but MEASURED 2026-09-07 against that real history to find
         ONLY the commit that originally wrote the test -- the commit that
         actually fixed the bug the issue reported never touches the `def`
         line at all, only the function's BODY, so the pickaxe cannot see it.
      2. Recent commits touching whichever file(s) CURRENTLY define
         `symbol` (found by AST-scanning the working tree, not by name
         guessing). This is what actually catches a body-only fix commit:
         it edits a file under `_NODE_ID_SCAN_DIRS` that still defines the
         function today, so it is trivially in that file's recent history
         even though the `def` line itself was never touched.

    Both lists are then AST-verified against each candidate commit's own
    post-image before being reported -- the pickaxe/recent-history steps are
    only ever candidate GENERATORS, exactly as `-S` is described above; a
    commit that only mentions the name in a comment or docstring on one side
    of the diff is filtered out with certainty, not probability.
    """
    candidates: dict[str, set[str]] = defaultdict(set)  # sha -> {paths}

    needle = f"def {symbol}("
    raw = _run(
        ["git", "log", "--all", "-S", needle, "-n", str(_NODE_ID_CANDIDATE_LIMIT),
         "--name-only", "--format=%H"],
        root,
    )
    for block in raw.split("\n\n"):
        lines = [ln.strip() for ln in block.splitlines() if ln.strip()]
        if not lines:
            continue
        sha, paths = lines[0], lines[1:]
        for path in paths:
            if path.endswith(".py"):
                candidates[sha].add(path)

    for path in _find_defining_files(root, symbol, use_cache=use_cache):
        raw = _run(
            ["git", "log", "--all", "-n", str(_NODE_ID_CANDIDATE_LIMIT),
             "--format=%H", "--", path],
            root,
        )
        for sha in raw.splitlines():
            sha = sha.strip()
            if sha:
                candidates[sha].add(path)

    matches: list[tuple[str, str]] = []
    for sha, paths in candidates.items():
        for path in paths:
            content = _run(["git", "show", f"{sha}:{path}"], root)
            if content and _defines_function(content, symbol):
                matches.append((sha[:9], path))
                break  # one confirmed hit per commit is enough to report it
    return matches


def fill_node_id_matches_one(root: Path, n: int, e: Evidence, use_cache: bool = True) -> None:
    """See the module comment above `_NODE_ID_RE`: the shape of a fixing
    commit with no `#N` anywhere for `fill_related_issues_one` to find.
    Gated identically to it (only issues with no implementing commit of
    their own, only once state is known) so the ~85% of issues that already
    show a clean implementing commit pay nothing extra.

    Reads the symbol out of the SAME shared thread `get_issue_thread` (n, e)
    returns -- body AND comments, not body alone as the original per-issue
    fetch here used to ask for. A test function name quoted in a COMMENT
    (an owner or reviewer pointing at it after the fact) is exactly as valid
    a candidate as one in the issue body itself; scanning strictly more text
    than before only widens what this candidate generator can find, it does
    not change what `verdict()` decides (this list is a candidate list, per
    the module docstring above `fill_related_issues_one`, never the verdict
    itself)."""
    if e.state == "unknown" or e.implementing_commits:
        return
    thread = get_issue_thread(root, n, e)
    for symbol in extract_node_id_candidates(e.title, thread):
        for sha, path in find_node_id_matches(root, symbol, use_cache=use_cache):
            e.node_id_matches.append((symbol, sha, path))


def scan(root: Path, numbers: list[int],
         known: dict[int, IssueMeta] | None = None, use_cache: bool = True) -> dict[int, Evidence]:
    ev = {n: Evidence(number=n) for n in numbers}

    # The trailing lookahead is the whole reason this is not a substring search:
    # without it, issue 52's pattern matches inside issue 521's own digits and
    # every sweep reports phantom hits.
    patterns = {n: re.compile(rf"#{n}(?![0-9])") for n in numbers}

    for n, commits in scan_commits(root, patterns, use_cache=use_cache).items():
        ev[n].commits = commits

    totest = "docs/ToTest.md"
    for rel in tracked_files(root):
        fpath = root / rel
        try:
            with open(fpath, "rb") as fh:
                sample = fh.read(_BINARY_SNIFF_BYTES)
        except OSError:
            continue
        if _looks_binary(sample):
            continue
        try:
            text = fpath.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        if "#" not in text:
            continue
        bucket = _classify(rel)
        for n, pat in patterns.items():
            if not pat.search(text):
                continue
            if rel == totest:
                # ToTest is a verification queue, not evidence of code. Kept
                # separate so it cannot look like independent corroboration.
                ev[n].in_totest = True
                continue
            line = next((ln.strip() for ln in text.splitlines() if pat.search(ln)), "")
            ev[n].hits[bucket].append((rel, line[:150]))

    for n, meta in gh_state(numbers, root, known).items():
        if n in ev:
            ev[n].state = meta.state
            ev[n].title = meta.title
            ev[n].milestone = meta.milestone
            ev[n].labels = meta.labels
            ev[n].thread_text = meta.thread_text

    return ev


def report_one(n: int, e: Evidence, suspect_only: bool) -> str:
    """Render and print one issue's block immediately (never batched --
    issue #307's own fix), and return its verdict level so the streaming
    caller can count suspects and decide the exit code without this
    function needing to know about any OTHER issue. Printing is skipped
    (but the level is still returned) when `suspect_only` suppresses a
    non-SUSPECT verdict -- the caller still needs the level to keep an
    accurate running count even for an issue it never prints."""
    level, why = e.verdict()
    if level != "SUSPECT" and suspect_only:
        return level

    head = f"#{n}" + (f"  {e.title[:68]}" if e.title else "")
    print(f"\n{'=' * 78}\n{head}\n{'-' * 78}")
    print(f"  state    : {e.state}")
    # a milestone or post-v1 label is a PRIORITY decision, not a
    # gap this script found -- print it unconditionally so "safe to
    # start" (a commit-graph fact) is never read alone as "worth doing".
    if e.milestone or e.labels:
        label_note = f"  labels: {', '.join(e.labels)}" if e.labels else ""
        print(f"  milestone: {e.milestone or '(none)'}{label_note}")
    if e.deferred:
        print(
            f"  DEFERRED : milestone/label says {e.milestone or 'post-v1'!r} -- "
            "the owner already deferred this. Dispatching it now is a priority "
            "decision, not a gap. Read the thread before starting."
        )
    print(f"  verdict  : {level} -- {why}")

    if e.commits:
        print(f"  commits  : {len(e.commits)}")
        for c in e.commits[:6]:
            if not c.touched_source:
                mark = "docs"
            elif c.merged:
                mark = "SRC "
            else:
                mark = "UNMERGED"
            extra = f"  [{len(c.source_files)} source file(s)]" if c.touched_source else ""
            cited = "  (cited in the issue thread)" if c.sha in e.reviewed_shas else ""
            print(f"             [{mark}] {c.sha} {c.subject[:78]}{extra}{cited}")
            # Surface it, don't make the reader re-derive it. A
            # commit naming more than one issue is exactly the shape that
            # cost three `git show` calls to catch once already.
            other_refs = [r for r in _issue_refs(c.subject) if r != n]
            if other_refs:
                print(
                    "                    ALSO NAMES: "
                    + ", ".join(f"#{r}" for r in other_refs)
                    + " -- if that issue's own subject differs from #"
                    + str(n) + "'s, this diff may not really be about #"
                    + str(n) + ". Check the files below against both."
                )
            if c.touched_source:
                for p in c.source_files[:8]:
                    print(f"                    {p}")
                if len(c.source_files) > 8:
                    print(f"                    ... and {len(c.source_files) - 8} more file(s)")
            if c.body:
                for bl in c.body.splitlines()[:4]:
                    print(f"                    {bl[:100]}")
        if len(e.commits) > 6:
            print(f"             ... and {len(e.commits) - 6} more")

    # Source mentions carry their line: it is the fastest way to see whether
    # a module implements the issue or documents the gap it left, and it is
    # where the work starts either way.
    for bucket in ("code", "tests"):
        paths = e.hits.get(bucket) or []
        if not paths:
            continue
        print(f"  {bucket:<9}: {len(paths)}")
        for p, line in paths[:6]:
            print(f"             {p}")
            if line:
                print(f"               | {line}")
        if len(paths) > 6:
            print(f"             ... and {len(paths) - 6} more")

    docs = e.hits.get("docs") or []
    if docs:
        print(f"  docs     : {len(docs)}  ({', '.join(p for p, _ in docs[:4])}"
              f"{', ...' if len(docs) > 4 else ''})")

    if e.in_totest:
        print("  totest   : an open row in docs/ToTest.md")

    # This issue has no implementing commit of its OWN -- the exact
    # case where "nothing shipped" and "shipped under a different number"
    # look identical from the commit graph alone.
    if e.related:
        print(f"  related  : {len(e.related)} issue(s) this verdict does NOT account for")
        for m, other_state, other_title, direction in e.related[:6]:
            title_part = f"  {other_title[:60]}" if other_title else ""
            print(f"             #{m} ({other_state}){title_part}")
            print(f"               | {direction} -- check its own commits before trusting "
                  f"\"no trace\" above")
        if len(e.related) > 6:
            print(f"             ... and {len(e.related) - 6} more")

    # No `#N` text anywhere links this issue to its own fix, so `related`
    # above cannot find it either -- only a commit that genuinely
    # DEFINES the test/symbol this issue's title or body names,
    # confirmed by parsing it, not by grepping for it.
    if e.node_id_matches:
        print(
            f"  node-ids : {len(e.node_id_matches)} commit(s) AST-confirmed to "
            "define a symbol this issue's own title/body names, with no "
            "#-reference connecting them. NOT part of the "
            "verdict above; go read the commit."
        )
        for symbol, sha, path in e.node_id_matches[:6]:
            print(f"             {symbol}")
            print(f"               | {sha}  {path}")
        if len(e.node_id_matches) > 6:
            print(f"             ... and {len(e.node_id_matches) - 6} more")

    return level


def print_footer(total: int, suspects: int) -> int:
    """The closing summary + standing caveat, printed once after every
    issue has streamed. Returns the exit code (1 if any SUSPECT, else 0)."""
    print(f"\n{'=' * 78}")
    print(f"{total} issue(s) checked, {suspects} suspect.")
    if suspects:
        print("SUSPECT is a prompt to go read the commit -- not a conclusion.")
    print(
        "This verdict is commit-graph, milestone/label, and cross-reference "
        "based only. It cannot see an owner hold recorded only as comment "
        "prose, an EPIC's do-not-build list, or two issues fixed by the same "
        "commit that never named each other anywhere GitHub can see. Read "
        "the issue thread before dispatching or closing, always."
    )
    return 1 if suspects else 0


def run_stream(
    root: Path,
    numbers: list[int],
    known: dict[int, IssueMeta] | None,
    use_cache: bool,
    suspect_only: bool,
    show_progress: bool = True,
) -> int:
    """The streaming entry point issue #307 exists to add: gather the BULK,
    issue-independent evidence once (`scan()` -- cached commit log, one
    tree-scan pass, bulk `gh` state/thread fetch), then loop issue by
    issue, fetching only what THAT issue still needs (the per-issue
    enrichment calls, now gated exactly as before but no longer batched
    across the whole backlog first), printing and flushing its block
    immediately. A redirected or backgrounded run therefore shows real,
    growing output from the first issue onward, instead of the zero bytes
    a fully-buffered, gather-everything-then-print design produced at
    backlog scale -- see the module docstring's own root-cause section.

    `show_progress` prints one `[i/N] #n -> LEVEL` line per issue to
    STDERR only, never stdout: the real report (what a human or another
    tool reads back) is not touched by it either way, matching --help's own
    description of --no-progress.
    """
    ev = scan(root, numbers, known, use_cache=use_cache)
    ordered = sorted(ev)
    total = len(ordered)
    # These two passes key off `state`, and must not fire when --no-gh has
    # already ruled GitHub out for every issue (all states are "unknown").
    has_gh = any(e.state != "unknown" for e in ev.values())

    suspects = 0
    for i, n in enumerate(ordered, start=1):
        e = ev[n]
        if has_gh:
            fill_reviewed_shas_one(root, n, e)
            fill_related_issues_one(root, n, e, ev)
            fill_node_id_matches_one(root, n, e, use_cache=use_cache)

        level = report_one(n, e, suspect_only)
        if level == "SUSPECT":
            suspects += 1
        sys.stdout.flush()  # belt-and-suspenders alongside line_buffering=True (see module top)

        if show_progress:
            print(f"[{i}/{total}] #{n} -> {level}", file=sys.stderr, flush=True)

    return print_footer(total, suspects)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Check whether GitHub issues are already addressed in this repo.",
    )
    ap.add_argument("numbers", nargs="*", type=int, help="issue numbers to check")
    ap.add_argument("--all-open", action="store_true", help="check every open issue (needs gh)")
    ap.add_argument("--limit", type=int, default=None,
                    help="with --all-open: fetch at most this many issues (newest first). "
                         "Unset means the full backlog (up to 400). The recommended normal "
                         "use, per issue #307, is a few dozen: --all-open --limit 30")
    ap.add_argument("--label", help="with --all-open: scope to issues carrying this one label "
                                     "(e.g. area:worker), filtered server-side by `gh issue list --label`")
    ap.add_argument("--suspect-only", action="store_true",
                    help="print only issues with a SUSPECT verdict")
    ap.add_argument("--root", help="checkout to scan (default: the current directory's)")
    ap.add_argument("--no-gh", action="store_true",
                    help="skip GitHub entirely; report commit and tree evidence only")
    ap.add_argument("--no-scan-cache", action="store_true",
                    help="force a fresh `git log --all` pass instead of reading the "
                         "HEAD-keyed cache; use when debugging the cache itself")
    ap.add_argument("--no-progress", action="store_true",
                    help="do not print a [i/N] progress line to stderr as each issue completes "
                         "(the real report on stdout is unaffected either way)")
    args = ap.parse_args(argv)

    if args.all_open and args.no_gh:
        ap.error("--all-open needs GitHub; it cannot be combined with --no-gh")
    if args.label and not args.all_open:
        ap.error("--label only applies to --all-open")

    root = repo_root(args.root)

    numbers = list(args.numbers)
    known: dict[int, IssueMeta] = {}
    if args.no_gh:
        # A sentinel state, so verdict() reports evidence without guessing at
        # open/closed. Useful offline, and it keeps this script's own tests off
        # the network -- a test whose result depends on a live issue's state is
        # a test that fails for reasons unrelated to the code.
        known = {n: IssueMeta(state="unknown") for n in numbers}
    if args.all_open:
        known = gh_list(root, "open", limit=args.limit or 400, label=args.label)
        if not known:
            print("error: --all-open needs `gh` and a working GitHub auth", file=sys.stderr)
            return 2
        numbers = sorted(set(numbers) | set(known))
    if not numbers:
        ap.error("give at least one issue number, or --all-open")

    return run_stream(
        root, numbers, known,
        use_cache=not args.no_scan_cache,
        suspect_only=args.suspect_only,
        show_progress=not args.no_progress,
    )


if __name__ == "__main__":
    sys.exit(main())
