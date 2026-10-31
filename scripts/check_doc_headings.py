"""scripts/check_doc_headings.py -- numbered `## N.` doc sections never collide (#883).

    python scripts/check_doc_headings.py
    python scripts/check_doc_headings.py --root <dir>

Parallel branches each append "the next numbered section" to the same doc and all pick the
same number; git merges the duplicates silently (the headings sit on different lines) and every
`section N` cross-reference then points at the wrong section. MEASURED 2026-10-28: four
branches each appended `## 19.` to docs/cloud_design.md.

Rule, for every docs/**/*.md: the top-level numbered headings (`## 12. Title`) are STRICTLY
ascending, with no number used twice. Gaps are fine (a removed section). Only `##` headings
count: `###` and deeper numbering restarts under each parent and is ignored on purpose. Lines
inside a fenced code block (``` or ~~~, indented up to three spaces) are ignored, so the
template skeletons in docs/README.md do not count. Unnumbered headings are ignored.

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

_HEADING_RE = re.compile(r"^## (\d+)\.(?:\s|$)")
_FENCE_RE = re.compile(r"^ {0,3}(```|~~~)")

FIX_HINT = (
    "renumber your section to the next free number and fix every 'section N' reference to it (grep docs/, CLAUDE.md, app/ and worker/ comments)"
)


@dataclass(frozen=True)
class Problem:
    line_no: int
    kind: str  # "duplicate" | "descending"
    message: str
    first_line_no: int


def _numbered_headings(text: str) -> list[tuple[int, int, str]]:
    """(line_no, number, heading text) for each top-level numbered heading outside a fence."""
    found: list[tuple[int, int, str]] = []
    fence: str | None = None
    for line_no, raw in enumerate(text.splitlines(), start=1):
        fm = _FENCE_RE.match(raw)
        if fm:
            marker = fm.group(1)
            if fence is None:
                fence = marker
            elif fence == marker:
                fence = None
            continue
        if fence is not None:
            continue
        hm = _HEADING_RE.match(raw)
        if hm:
            found.append((line_no, int(hm.group(1)), raw.rstrip()))
    return found


def find_problems(text: str) -> list[Problem]:
    problems: list[Problem] = []
    first_seen: dict[int, tuple[int, str]] = {}
    previous: tuple[int, str] | None = None
    for line_no, number, heading in _numbered_headings(text):
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
            if previous is not None and number < previous[0]:
                problems.append(
                    Problem(
                        line_no,
                        "descending",
                        f"section numbers must ascend: '{heading}' follows '{previous[1]}'; {FIX_HINT}",
                        first_seen[previous[0]][0],
                    )
                )
        previous = (number, heading)
    return problems


def _doc_files(root: Path) -> list[Path]:
    return sorted((root / "docs").rglob("*.md"))


def numbered_heading_count(root: Path) -> int:
    return sum(len(_numbered_headings(p.read_text(encoding="utf-8", errors="replace"))) for p in _doc_files(root))


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
