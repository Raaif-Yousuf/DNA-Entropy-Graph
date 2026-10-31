"""scripts/check_doc_headings.py -- numbered `## N.` doc sections never collide (#883).

    python scripts/check_doc_headings.py
    python scripts/check_doc_headings.py --root <dir>

Parallel branches each append "the next numbered section" to the same doc and all pick the
same number; git merges the duplicates silently (the headings sit on different lines) and every
`section N` cross-reference then points at the wrong section. MEASURED 2026-10-28: four
branches each appended `## 19.` to docs/cloud_design.md.

Rule, for every docs/**/*.md: the top-level numbered headings (`## 12. Title`) are STRICTLY
ascending, with no number used twice. Gaps are fine (a removed section). Only `##` headings
count: `###` and deeper numbering restarts under each parent and is ignored on purpose. Heading
forms follow CommonMark (up to 3 leading spaces, spaces or a tab after `##`, `## 19.Title`
counts, `## 1.2 Sub` does not). Lines inside a fenced code block are ignored, so the template
skeletons in docs/README.md do not count; a fence closes only on the same character, at least
as long as the opener, with nothing but whitespace after it, and a fence that never closes is
itself an error (it would hide the rest of the file). A commented-out `## N.` inside an HTML
comment is still counted. Unnumbered headings are ignored.

A failure names `file:line`, both headings, and the fix: renumber your section to the next free
number AND fix every `section N` reference to it (grep the repo, code comments included).

Exit codes: 0 clean, 1 a duplicate or descending heading, 2 bad usage (no docs/ under --root).
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path

# CommonMark ATX heading: up to 3 leading spaces, then spaces or tabs. "## 19.Title" counts; "## 1.2 Sub" does not.
_HEADING_RE = re.compile(r"^ {0,3}##[ \t]+(\d+)\.(?!\d)")
_FENCE_OPEN_RE = re.compile(r"^ {0,3}(`{3,}|~{3,})(.*)$")
# A closer: same char, at least as long as the opener, only whitespace after it.
_FENCE_CLOSE_RE = re.compile(r"^ {0,3}(`{3,}|~{3,})[ \t]*$")

FIX_HINT = (
    "renumber your section to the next free number and fix every 'section N' reference to it (grep docs/, CLAUDE.md, app/ and worker/ comments)"
)


@dataclass(frozen=True)
class Problem:
    line_no: int
    kind: str  # "duplicate" | "descending"
    message: str
    first_line_no: int


def _scan(text: str) -> tuple[list[tuple[int, int, str]], int | None]:
    """Numbered headings outside fences as (line_no, number, heading), and the line of an unclosed fence."""
    found: list[tuple[int, int, str]] = []
    fence_char = ""
    fence_len = 0
    fence_line = 0
    # split("\n"), not splitlines(): splitlines also breaks on \x0b, \x0c, \x85, U+2028 and friends,
    # which would shift every reported line number away from what an editor shows.
    for line_no, raw in enumerate(text.split("\n"), start=1):
        raw = raw.rstrip("\r")
        if fence_char:
            cm = _FENCE_CLOSE_RE.match(raw)
            if cm and cm.group(1)[0] == fence_char and len(cm.group(1)) >= fence_len:
                fence_char = ""
            continue
        om = _FENCE_OPEN_RE.match(raw)
        if om and not (om.group(1)[0] == "`" and "`" in om.group(2)):
            fence_char, fence_len, fence_line = om.group(1)[0], len(om.group(1)), line_no
            continue
        hm = _HEADING_RE.match(raw)
        if hm:
            found.append((line_no, int(hm.group(1)), raw.rstrip()))
    return found, (fence_line if fence_char else None)


def _numbered_headings(text: str) -> list[tuple[int, int, str]]:
    return _scan(text)[0]


def find_problems(text: str) -> list[Problem]:
    problems: list[Problem] = []
    first_seen: dict[int, tuple[int, str]] = {}
    previous: tuple[int, int, str] | None = None
    headings, unclosed = _scan(text)
    for line_no, number, heading in headings:
        if number in first_seen:
            first_line, first_heading = first_seen[number]
            problems.append(
                Problem(
                    line_no,
                    "duplicate",
                    f"duplicate section number: '{heading}' repeats '{first_heading}' (line {first_line}); {FIX_HINT}",
                    first_line,
                )
            )
        else:
            first_seen[number] = (line_no, heading)
            if previous is not None and number < previous[1]:
                problems.append(
                    Problem(
                        line_no,
                        "descending",
                        f"section numbers must ascend: '{heading}' follows '{previous[2]}'; {FIX_HINT}",
                        previous[0],
                    )
                )
        previous = (line_no, number, heading)
    if unclosed is not None:
        problems.append(
            Problem(
                unclosed,
                "unclosed-fence",
                f"unclosed code fence opened at line {unclosed}: the rest of the file is not checked; close the fence",
                unclosed,
            )
        )
    return problems


def _doc_files(root: Path) -> list[Path]:
    return sorted((root / "docs").rglob("*.md"))


def numbered_heading_counts(root: Path) -> dict[str, int]:
    return {p.relative_to(root).as_posix(): len(_numbered_headings(p.read_text(encoding="utf-8", errors="replace"))) for p in _doc_files(root)}


def numbered_heading_count(root: Path) -> int:
    return sum(numbered_heading_counts(root).values())


def check_root(root: Path) -> list[str]:
    messages: list[str] = []
    for path in _doc_files(root):
        rel = path.relative_to(root).as_posix()
        text = path.read_text(encoding="utf-8", errors="replace")
        messages.extend(f"{rel}:{p.line_no}: {p.message}" for p in find_problems(text))
    return messages


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Fail on duplicate or descending numbered '## N.' doc headings.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    args = parser.parse_args(argv)
    if not (args.root / "docs").is_dir():
        print(f"ERROR: no docs/ directory under {args.root}")
        return 2
    messages = check_root(args.root)
    if messages:
        for m in messages:
            print(f"ERROR: {m}")
        return 1
    print(f"OK: {numbered_heading_count(args.root)} numbered doc headings, all unique and ascending")
    return 0


if __name__ == "__main__":
    sys.exit(main())
