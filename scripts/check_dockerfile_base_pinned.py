"""scripts/check_dockerfile_base_pinned.py -- every FROM in a Dockerfile is pinned by digest (#255, #178).

    python scripts/check_dockerfile_base_pinned.py worker/Dockerfile.cuda [more Dockerfiles]

Every `FROM` must carry a literal `@sha256:<64 lowercase hex>`, except `FROM scratch` and `FROM <alias of an EARLIER
stage>`. A multi-stage file with one pinned stage and one tag-only stage fails. A `${ARG}` in the image reference is not a
pin (the digest must be written in the file). A Dockerfile with no FROM at all fails rather than passing vacuously.

Exit codes: 0 all pinned, 1 something unpinned or unreadable.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

_PIN_RE = re.compile(r"@sha256:[0-9a-f]{64}(?![0-9a-zA-Z])")


def _instructions(text: str) -> list[str]:
    """Logical lines: comments dropped, backslash continuations joined."""
    out: list[str] = []
    buf = ""
    for raw in text.splitlines():
        line = raw.strip()
        if not buf and (not line or line.startswith("#")):
            continue
        if buf and line.startswith("#"):
            continue
        if line.endswith("\\"):
            buf += line[:-1] + " "
            continue
        out.append((buf + line).strip())
        buf = ""
    if buf.strip():
        out.append(buf.strip())
    return out


def find_unpinned(text: str) -> list[str]:
    problems: list[str] = []
    aliases: set[str] = set()
    froms = 0
    for ins in _instructions(text):
        parts = ins.split()
        if not parts or parts[0].upper() != "FROM":
            continue
        froms += 1
        rest = [p for p in parts[1:] if not p.startswith("--")]
        if not rest:
            problems.append(f"FROM with no image: {ins!r}")
            continue
        image = rest[0]
        alias = rest[2].lower() if len(rest) >= 3 and rest[1].upper() == "AS" else None
        if image.lower() != "scratch" and image.lower() not in aliases and not _PIN_RE.search(image):
            problems.append(f"FROM {image} is not pinned by @sha256:<64 hex>")
        if alias:
            aliases.add(alias)
    if froms == 0:
        problems.append("no FROM instruction found")
    return problems


def main(argv: list[str] | None = None) -> int:
    paths = [Path(a) for a in (sys.argv[1:] if argv is None else argv)]
    if not paths:
        print("ERROR: pass at least one Dockerfile path.")
        return 1
    bad = 0
    for path in paths:
        try:
            text = path.read_text(encoding="utf-8")
        except OSError as exc:
            print(f"ERROR: cannot read {path} ({type(exc).__name__}). Check the path.")
            bad += 1
            continue
        for problem in find_unpinned(text):
            print(f"ERROR: {path}: {problem} (issue 255). Resolve the digest and commit it.")
            bad += 1
    if bad:
        return 1
    print(f"OK: every FROM in {len(paths)} Dockerfile(s) is pinned by digest")
    return 0


if __name__ == "__main__":
    sys.exit(main())
