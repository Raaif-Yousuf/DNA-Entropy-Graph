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

Blocks are handled textually so untouched bytes survive. A comment (single or multi-line) right before
a `<data>` travels with that block; comments are never searched for tags. Line endings follow ours
(all three sides are compared with LF and rendered back in ours' style, taken from ours' first line
ending). No imports beyond the standard library.

Known limit: only the key set and the content are merged, not the order. If theirs reorders existing
keys, that reorder is ignored and ours' order stays (new keys from theirs still land after ours' last
block).

Every failure exit (real conflict, invalid XML, duplicate name, unterminated block, undecodable bytes)
leaves conflict markers in %A: around the one block for a real conflict, around the whole file when
the problem is not one block. Git then sees the markers, so a later `git add` cannot silently drop
theirs.
"""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NAME_RE = re.compile(r"""<data\s[^>]*?\bname=(["'])(.*?)\1""")
COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)


class Parsed:
    def __init__(self, header: str, blocks: list[tuple[str, str]], footer: str) -> None:
        self.header = header
        self.blocks = blocks  # (name, text) in file order
        self.footer = footer


def _scan(line: str, in_comment: bool) -> tuple[str, bool, bool]:
    """(the line with comment text removed, still inside a comment afterwards, the line touched a comment)."""
    touched = in_comment
    if in_comment:
        end = line.find("-->")
        if end < 0:
            return "", True, True
        line = line[end + 3 :]
    stripped = COMMENT_RE.sub("", line)
    if "<!--" in stripped:
        return stripped[: stripped.index("<!--")], True, True
    return stripped, False, touched or stripped != line


def parse(text: str) -> Parsed:
    """Split LF-normalised text into header, keyed `<data>` blocks and footer.

    A comment that sits before a `<data>` becomes the leading part of that block, so it moves, stays and
    is deleted with it. A comment before `</root>` stays in the footer.
    """
    lines = text.splitlines(keepends=True)
    header: list[str] = []
    blocks: list[tuple[str, list[str]]] = []
    pending: list[str] = []  # comment lines waiting for the block they precede
    in_comment = False
    i = 0
    while i < len(lines):
        line = lines[i]
        stripped, in_comment, touched = _scan(line, in_comment)
        match = NAME_RE.search(stripped)
        if match:
            name = match.group(2)
            chunk = [*pending, line]
            pending = []
            while "</data>" not in stripped:
                i += 1
                if i >= len(lines):
                    raise ValueError(f"unterminated <data name={name!r}>")
                stripped, in_comment, _ = _scan(lines[i], in_comment)
                chunk.append(lines[i])
            blocks.append((name, chunk))
        elif "</root>" in stripped:
            tail = "".join([*pending, *lines[i:]])
            return Parsed("".join(header), [(n, "".join(c)) for n, c in blocks], tail)
        elif touched and not stripped.strip():
            pending.append(line)
        else:
            (blocks[-1][1] if blocks else header).extend([*pending, line])
            pending = []
        i += 1
    (blocks[-1][1] if blocks else header).extend(pending)
    return Parsed("".join(header), [(n, "".join(c)) for n, c in blocks], "")


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
    first_nl = ours.find("\n")
    eol = "\r\n" if first_nl > 0 and ours[first_nl - 1] == "\r" else "\n"
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


def _marked(ours: bytes, theirs: bytes) -> bytes:
    """Whole-file conflict wrapper: git must see markers so a later `git add` cannot silently drop theirs."""
    ours_nl = b"" if ours.endswith(b"\n") else b"\n"
    theirs_nl = b"" if theirs.endswith(b"\n") else b"\n"
    return b"<<<<<<< ours\n" + ours + ours_nl + b"=======\n" + theirs + theirs_nl + b">>>>>>> theirs\n"


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if len(args) < 3:
        print("usage: resw_merge.py BASE OURS THEIRS", file=sys.stderr)
        return 2
    base_p, ours_p, theirs_p = (Path(a) for a in args[:3])
    base_b, ours_b, theirs_b = (p.read_bytes() for p in (base_p, ours_p, theirs_p))

    def fail(reason: str) -> int:
        ours_p.write_bytes(_marked(ours_b, theirs_b))
        print(f"ERROR: resw_merge: {reason}; resolve the markers in Resources.resw by hand", file=sys.stderr)
        return 1

    try:
        merged, conflicts = merge_text(*(b.decode("utf-8") for b in (base_b, ours_b, theirs_b)))
    except (ValueError, UnicodeDecodeError) as err:
        return fail(str(err))
    if conflicts == 0:
        problem = validate(merged)
        if problem:
            return fail(problem)
    ours_p.write_bytes(merged.encode("utf-8"))
    if conflicts:
        print(f"ERROR: resw_merge: {conflicts} conflicting resource block(s); resolve the markers by hand", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
