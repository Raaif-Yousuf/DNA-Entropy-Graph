"""scripts/wave_findings.py -- turn pasted wave findings into DRAFT issues, flagging duplicates (#477).

    worker\\.venv\\Scripts\\python.exe scripts\\wave_findings.py findings.md      # or: ... | python scripts\\wave_findings.py
    ... wave_findings.py --gh "C:\\Program Files\\GitHub CLI\\gh.exe" findings.md  # (env WAVE_FINDINGS_GH) a different gh

Every wave ends with each agent's "Suspected bugs / improvements" pasted into the orchestrator's chat, and filing
them by hand (search for a duplicate, write the house body, pick labels) is where findings get dropped or filed
twice (THEORY, unverified). This only DRAFTS: it never creates, edits, comments on or closes anything. The only
gh call it makes is the read-only `gh issue list --search "<keywords>" --state all --limit 50 --json ...`.

INPUT: lines `- <text> (path:line)` (or `* `), the `(path:line)` optional; headings, blank lines and prose are
ignored. Markdown bold and backticks are stripped.

OUTPUT, per finding, in order:
  [n] DUPLICATE of #N (open|closed): <title>     when an existing issue's title shares at least 3 words and at
                                                 least 60% of the finding's keywords; no draft is printed
  [n] NEW: <text>                                followed by a PowerShell block: `$body = @' ... '@` and one
                                                 `gh issue create --title '...' --label 'P3' --label 'area:x'
                                                 --body $body` line you run yourself after reading it
A failed search prints `WARNING: ... unchecked` and still drafts. The P-label is always P3 and the area label is a
guess from the path (scripts/ -> area:packaging); both are for the owner to change. Console output is ASCII.

Exit codes: 0 done, 2 no findings in the input.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from typing import TextIO

EXIT_OK, EXIT_NONE = 0, 2
SEARCH_LIMIT = 50
MIN_SHARED_WORDS = 3
MIN_OVERLAP = 0.6
MAX_KEYWORDS = 8
STOPWORDS = frozenset(
    [
        "the",
        "and",
        "for",
        "with",
        "that",
        "this",
        "from",
        "when",
        "what",
        "into",
        "are",
        "was",
        "but",
        "not",
        "its",
        "has",
        "have",
        "had",
        "can",
        "may",
        "only",
        "also",
        "than",
        "then",
        "there",
        "their",
        "they",
        "them",
        "which",
        "while",
        "where",
        "does",
        "did",
        "done",
        "use",
        "used",
        "using",
        "via",
        "per",
        "any",
        "all",
        "one",
        "two",
        "out",
        "off",
        "over",
        "under",
        "still",
        "again",
        "very",
        "more",
        "most",
        "some",
        "each",
        "both",
        "just",
        "like",
        "such",
        "will",
        "would",
        "should",
        "could",
        "must",
    ]
)

_BULLET = re.compile(r"^\s*[-*]\s+(?P<body>.+?)\s*$")
_LOCATION = re.compile(r"\s*\((?P<loc>[\w./\\-]+:\d+(?:-\d+)?)\)\s*$")
_WORD = re.compile(r"[a-z0-9]+")


@dataclass(frozen=True)
class Finding:
    text: str
    location: str


def parse_findings(source: str) -> list[Finding]:
    """The `- <text> (path:line)` bullets of `source`, in order."""
    found: list[Finding] = []
    for line in source.splitlines():
        match = _BULLET.match(line)
        if not match:
            continue
        body = match["body"]
        loc = _LOCATION.search(body)
        location = loc["loc"].replace("\\", "/") if loc else ""
        if loc:
            body = body[: loc.start()]
        text = re.sub(r"\s+", " ", body.replace("**", "").replace("`", "")).strip()
        if text:
            found.append(Finding(text, location))
    return found


def guess_area(location: str) -> tuple[str, str]:
    """(area label, title prefix) guessed from the path of a finding. Always a guess: the owner can change it."""
    path = location.split(":", 1)[0].lower()
    if path.startswith("app/") and "cloud" in path:
        return "area:cloud", "cloud"
    if path.startswith("app/") and "viewer" in path:
        return "area:viewer", "viewer"
    if path.startswith("worker/"):
        return "area:worker", "worker"
    if path.startswith("docs/"):
        return "area:docs", "docs"
    if path.startswith(("scripts/", ".github/", ".claude/")):
        return "area:packaging", "tooling"
    return "area:app", "app"


def keywords(text: str) -> list[str]:
    """Distinct lower-case words of 3+ characters minus stopwords, first-seen order, capped."""
    seen: list[str] = []
    for word in _WORD.findall(text.lower()):
        if len(word) >= 3 and word not in STOPWORDS and word not in seen:
            seen.append(word)
    return seen[:MAX_KEYWORDS]


def _asciify(text: str) -> str:
    return text.encode("ascii", errors="replace").decode("ascii")


def _quote(text: str) -> str:
    """A PowerShell single-quoted string body: only the single quote needs doubling."""
    return _asciify(text).replace("'", "''").replace("\n", " ")


def find_duplicate(words: Sequence[str], issues: Sequence[dict]) -> dict | None:
    """The existing issue whose title best overlaps the finding's keywords, or None."""
    best: tuple[float, dict] | None = None
    for issue in issues:
        title_words = set(_WORD.findall(str(issue.get("title", "")).lower()))
        shared = len([w for w in words if w in title_words])
        if shared < MIN_SHARED_WORDS or not words:
            continue
        overlap = shared / len(words)
        if overlap >= MIN_OVERLAP and (best is None or overlap > best[0]):
            best = (overlap, issue)
    return best[1] if best else None


def _search(gh: Sequence[str], words: Sequence[str]) -> list[dict] | None:
    """Read-only `gh issue list --search ...`; None when gh failed or printed something that is not a JSON list."""
    argv = [
        *gh,
        "issue",
        "list",
        "--search",
        " ".join(words[:6]),
        "--state",
        "all",
        "--limit",
        str(SEARCH_LIMIT),
        "--json",
        "number,title,state",
    ]
    try:
        proc = subprocess.run(argv, capture_output=True, check=False)
    except OSError:
        return None
    if proc.returncode != 0:
        return None
    try:
        data = json.loads(proc.stdout.decode("utf-8", errors="replace"))
    except ValueError:
        return None
    return [i for i in data if isinstance(i, dict)] if isinstance(data, list) else None


def draft_body(finding: Finding) -> str:
    where = f"Found at `{finding.location}` by a wave agent." if finding.location else "Reported by a wave agent."
    return "\n".join(
        [
            "## Why",
            f"THEORY (unverified), from a wave findings report: {finding.text}",
            where,
            "",
            "## Done when",
            "- [ ] The finding is reproduced (or disproved and this issue closed with the measurement)",
            "- [ ] It is fixed with a test that fails before the fix",
            "",
            "## Observable that proves it is wired",
            "The one thing that differs after the fix, named and checked.",
            "",
            "## Docs touched",
            "- [ ] none, or name the file",
            "",
            "## Out of scope",
            "Anything not named above.",
            "",
            "## Cloud money",
            "none",
        ]
    )


def run(source: TextIO, gh: Sequence[str]) -> int:
    findings = parse_findings(source.read())
    if not findings:
        print("ERROR: no findings in the input; expected lines like `- <text> (path:line)`")
        return EXIT_NONE
    for number, finding in enumerate(findings, 1):
        words = keywords(finding.text)
        issues = _search(gh, words) if words else None
        if issues is None:
            print(f"[{number}] WARNING: the duplicate search failed, so this one is unchecked; drafting anyway")
        else:
            dup = find_duplicate(words, issues)
            if dup is not None:
                state = "closed" if str(dup.get("state", "")).upper() == "CLOSED" else "open"
                print(f"[{number}] DUPLICATE of #{dup.get('number')} ({state}): {_asciify(str(dup.get('title', '')))}")
                print(f"    finding: {_asciify(finding.text)}")
                continue
        area, prefix = guess_area(finding.location)
        title = f"{prefix}: {finding.text}"
        if len(title) > 100:
            title = title[:97].rstrip() + "..."
        print(f"[{number}] NEW: {_asciify(finding.text)}")
        print("$body = @'")
        print(_asciify(draft_body(finding)))
        print("'@")
        print(f"gh issue create --title '{_quote(title)}' --label 'P3' --label '{area}' --body $body")
        print()
    return EXIT_OK


def main(argv: Sequence[str] | None = None, *, gh: Sequence[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Draft issues from pasted wave findings; flag duplicates; never files anything.")
    ap.add_argument("file", nargs="?", help="findings file (default: stdin)")
    ap.add_argument("--gh", default=os.environ.get("WAVE_FINDINGS_GH", "gh"), help="path of the gh executable (one path, spaces allowed)")
    args = ap.parse_args(argv)
    gh_argv = list(gh) if gh is not None else [args.gh]
    if args.file:
        with open(args.file, encoding="utf-8") as fh:
            return run(fh, gh_argv)
    return run(sys.stdin, gh_argv)


if __name__ == "__main__":
    sys.exit(main())
