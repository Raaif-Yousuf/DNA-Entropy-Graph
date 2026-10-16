"""scripts/fix_eol.py -- make a stale CRLF working tree LF again (#462).

    worker\\.venv\\Scripts\\python.exe scripts\\fix_eol.py            # rewrite the stale files to LF
    worker\\.venv\\Scripts\\python.exe scripts\\fix_eol.py --check    # list them, exit 1 if any (changes nothing)

WHY: `.gitattributes` says `* text=auto eol=lf`, so a fresh checkout or worktree is LF even with
`core.autocrlf=true` (MEASURED 2026-10-15: `dotnet format DnaEntropyGraph.sln --verify-no-changes` exits 0 in
a fresh worktree). A checkout made before that file existed, or an editor/tool that wrote CRLF, leaves files that
git's index holds as LF but the tree holds as CRLF; `dotnet format` then reports ENDOFLINE on every line and a
real whitespace regression hides in the noise. This rewrites exactly those files (`git ls-files --eol` says
`i/lf` and the work tree is `w/crlf` or `w/mixed`) as bytes: CRLF -> LF, a lone CR is left alone. Files committed
as CRLF (`.ps1`, `.sln`) and binaries (`-text`) are never touched. Nothing is staged or deleted.

Exit codes: 0 nothing (left) to fix, 1 `--check` found stale files, 2 git failed. Console output is ASCII.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from collections.abc import Sequence
from pathlib import Path

_ENTRY = re.compile(r"^i/(?P<index>\S+)\s+w/(?P<tree>\S+)\s+attr/(?P<attr>[^\t]*)\t(?P<path>.+)$")


def stale_files(listing: str) -> list[str]:
    """Paths the index holds as LF whose working copy is CRLF or mixed, from `git ls-files --eol` output."""
    found: list[str] = []
    for line in re.split(r"[\0\n]", listing):
        match = _ENTRY.match(line.rstrip("\r"))
        # `eol=crlf` files (.ps1, .sln) are LF in the index and CRLF in the tree ON PURPOSE (MEASURED 2026-10-15)
        if match and match["index"] == "lf" and match["tree"] in {"crlf", "mixed"} and "eol=crlf" not in match["attr"]:
            found.append(match["path"])
    return found


def normalize(root: Path, paths: Sequence[str]) -> list[str]:
    """Rewrite each file's CRLF as LF (bytes in, bytes out). Returns the paths that actually changed."""
    changed: list[str] = []
    for rel in paths:
        target = root / rel
        data = target.read_bytes()
        fixed = data.replace(b"\r\n", b"\n")
        if fixed != data:
            target.write_bytes(fixed)
            changed.append(rel)
    return changed


def main(argv: Sequence[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Rewrite CRLF working-tree files that git holds as LF (#462).")
    ap.add_argument("--check", action="store_true", help="list stale files and exit 1 if any; change nothing")
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent, help="repository root")
    args = ap.parse_args(argv)
    proc = subprocess.run(["git", "ls-files", "--eol", "-z"], cwd=args.root, capture_output=True, check=False)
    if proc.returncode != 0:
        print(f"ERROR: git ls-files failed in {args.root}: {proc.stderr.decode('utf-8', errors='replace').strip()}")
        return 2
    stale = stale_files(proc.stdout.decode("utf-8", errors="replace"))
    if not stale:
        print("OK: every tracked text file is LF in the working tree")
        return 0
    if args.check:
        for rel in stale:
            print(f"- CRLF in the working tree, LF in git: {rel}")
        print(f"ERROR: {len(stale)} file(s) are CRLF here; run scripts/fix_eol.py to rewrite them as LF")
        return 1
    changed = normalize(args.root, stale)
    for rel in changed:
        print(f"- rewrote as LF: {rel}")
    print(f"OK: {len(changed)} file(s) rewritten as LF")
    return 0


if __name__ == "__main__":
    sys.exit(main())
