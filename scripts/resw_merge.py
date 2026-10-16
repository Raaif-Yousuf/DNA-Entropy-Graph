"""scripts/resw_merge.py -- key-aware 3-way merge driver for Resources.resw.

Git merge-driver contract: `resw_merge.py %O %A %B` (base, ours, theirs). The result is written into
%A; exit 0 means clean, exit 1 means a real conflict (standard conflict markers around only the
conflicting `<data>` block) or a result that is not valid XML / has a duplicate name.

Why: nearly every branch appends keys at the end of the file, so every merge conflicts there. Git's
union merge is banned for this file (overlapping hunks break the XML), so the merge is done per
`<data name=...>` block instead:

- a key added on one side, or on both sides with identical content, is kept;
- the same key added or modified differently on both sides is a conflict;
- a key deleted on one side and unchanged on the other is deleted; deleted vs modified is a conflict;
- keys new in theirs are inserted after ours' last block, in theirs' order (deterministic).

Blocks are handled textually so untouched bytes survive. A single-line `<!-- ... -->` comment right
before a `<data>` travels with that block. Line endings follow ours (all three sides are compared
with LF and rendered back in ours' style). No imports beyond the standard library.
"""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NAME_RE = re.compile(r'<data\s[^>]*?\bname="([^"]*)"')
COMMENT_RE = re.compile(r"^\s*<!--.*-->\s*$")


class Parsed:
    def __init__(self, header: str, blocks: list[tuple[str, str]], footer: str) -> None:
        self.header = header
        self.blocks = blocks  # (name, text) in file order
        self.footer = footer


def parse(text: str) -> Parsed:
    """Split LF-normalised text into header, keyed `<data>` blocks and footer."""
    lines = text.splitlines(keepends=True)
    header: list[str] = []
    blocks: list[tuple[str, list[str]]] = []
    pending: list[str] = []  # comment lines waiting for the block they precede
    i = 0
    while i < len(lines):
        line = lines[i]
        if "<data" in line and NAME_RE.search(line):
            name = NAME_RE.search(line).group(1)  # type: ignore[union-attr]
            chunk = [*pending, line]
            pending = []
            while "</data>" not in lines[i]:
                i += 1
                if i >= len(lines):
                    raise ValueError(f"unterminated <data name={name!r}>")
                chunk.append(lines[i])
            blocks.append((name, chunk))
        elif COMMENT_RE.match(line):
            pending.append(line)
        else:
            (blocks[-1][1] if blocks else header).extend([*pending, line])
            pending = []
        i += 1
    footer: list[str] = list(pending)
    # `</root>` and anything after the last block belongs to the footer, not to that block.
    if blocks:
        tail = blocks[-1][1]
        end = next(k for k, ln in enumerate(tail) if "</data>" in ln) + 1
        footer = tail[end:] + footer
        del tail[end:]
    else:
        footer = header[:0] + footer
    return Parsed("".join(header), [(n, "".join(c)) for n, c in blocks], "".join(footer))


def _conflict(ours: str, theirs: str) -> str:
    return f"<<<<<<< ours\n{ours}=======\n{theirs}>>>>>>> theirs\n"


def _scalar(base: str, ours: str, theirs: str) -> tuple[str, bool]:
    if ours == theirs or theirs == base:
        return ours, False
    if ours == base:
        return theirs, False
    return _conflict(ours, theirs), True


def merge_text(base: str, ours: str, theirs: str) -> tuple[str, int]:
    """(merged text in ours' line-ending style, number of conflicts)."""
    eol = "\r\n" if "\r\n" in ours else "\n"
    b, o, t = (parse(s.replace("\r\n", "\n")) for s in (base, ours, theirs))
    bm, om, tm = dict(b.blocks), dict(o.blocks), dict(t.blocks)
    conflicts = 0

    header, bad = _scalar(b.header, o.header, t.header)
    conflicts += bad
    footer, bad = _scalar(b.footer, o.footer, t.footer)
    conflicts += bad

    out: list[str] = []
    for name, otext in o.blocks:
        if name in bm and name in tm:
            text, bad = _scalar(bm[name], otext, tm[name])
        elif name in bm:  # theirs deleted it
            text, bad = ("", False) if otext == bm[name] else (_conflict(otext, ""), True)
        elif name in tm:  # added on both sides
            text, bad = (otext, False) if otext == tm[name] else (_conflict(otext, tm[name]), True)
        else:
            text, bad = otext, False
        conflicts += bad
        out.append(text)
    for name, ttext in t.blocks:
        if name in om:
            continue
        if name in bm:  # ours deleted it
            if ttext != bm[name]:
                out.append(_conflict("", ttext))
                conflicts += 1
        else:
            out.append(ttext)
    merged = header + "".join(out) + footer
    return merged.replace("\n", eol), conflicts


def validate(text: str) -> str | None:
    """None when `text` is well-formed XML with unique data names, else the reason."""
    try:
        root = ET.fromstring(text.encode("utf-8"))
    except ET.ParseError as err:
        return f"result is not valid XML: {err}"
    seen: set[str] = set()
    for data in root.findall("data"):
        name = data.get("name", "")
        if name in seen:
            return f"duplicate resource name: {name}"
        seen.add(name)
    return None


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if len(args) < 3:
        print("usage: resw_merge.py BASE OURS THEIRS", file=sys.stderr)
        return 2
    base_p, ours_p, theirs_p = (Path(a) for a in args[:3])
    try:
        merged, conflicts = merge_text(*(p.read_bytes().decode("utf-8") for p in (base_p, ours_p, theirs_p)))
    except (ValueError, UnicodeDecodeError) as err:
        print(f"ERROR: resw_merge: {err}", file=sys.stderr)
        return 1
    if conflicts == 0:
        problem = validate(merged)
        if problem:
            print(f"ERROR: resw_merge: {problem}", file=sys.stderr)
            return 1
    ours_p.write_bytes(merged.encode("utf-8"))
    if conflicts:
        print(f"ERROR: resw_merge: {conflicts} conflicting resource block(s); resolve the markers by hand", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
