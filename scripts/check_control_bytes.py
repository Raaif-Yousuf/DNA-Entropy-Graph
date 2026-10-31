"""scripts/check_control_bytes.py -- no stray control bytes in text files (#1016).

    python scripts/check_control_bytes.py
    python scripts/check_control_bytes.py --self-test
    python scripts/check_control_bytes.py --root <dir>

MEASURED 2026-10-02: an agent's `scripts\\v...` path reached disk as a vertical tab (0x0b) because a
backslash escape was mangled on write. land_wt.py refused it at LAND time, which is too late: the lane
has already finished. This guard runs the same check in `premerge --fast` and CI, and land_wt.py imports
`CONTROL_BYTES`, `ALLOWLIST` and `find_control_bytes_in` from here (one implementation).

WHAT IS FLAGGED   NUL..BS, VT, FF and SO..US except ESC. TAB, LF, CR are text. ESC (0x1b) is allowed on
                  purpose: captured logs and test output carry ANSI colour.
WHAT IS SCANNED   Every tracked and untracked-but-not-ignored file in the working tree (MEASURED
                  2026-10-30: 1671 files in 0.17 s, so there is no reason to scan only the diff, and a
                  file that was already bad on main is found too). Untracked files (premerge may run
                  before a commit) above MAX_UNTRACKED_BYTES are skipped and their findings are
                  labelled "(untracked file)" so the cause is obvious.
BINARY RULE       A file is skipped when git would call it binary (a NUL in the first 8000 bytes, git's own
                  heuristic, the same verdict land_wt's `git diff --numstat` uses) or when .gitattributes
                  marks it `-text` / `binary` (the malformed-input fuzz corpus is bytes on purpose).

Exit codes: 0 clean, 1 a control byte found, 2 bad usage (not a git repository).
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass, replace
from pathlib import Path

CONTROL_BYTES = re.compile(rb"[\x00-\x08\x0b\x0c\x0e-\x1a\x1c-\x1f]")
BINARY_SNIFF_BYTES = 8000
MAX_PER_FILE = 5
MAX_UNTRACKED_BYTES = 2 * 1024 * 1024

# Vendored third-party files that legitimately hold a control byte. This is NOT derived from
# scripts/vendored_assets.json on purpose: that manifest lists every vendored asset, most of which hold no
# control byte, and the stale-entry rule below needs exactly the ones that do. A test keeps every entry
# here listed in the manifest. An entry that no longer has a control byte FAILS
# (test_repo_allowlist_entries_are_not_stale), so this list cannot rot.
ALLOWLIST: frozenset[str] = frozenset(
    {
        # MEASURED 2026-10-30: minified igv.js carries a literal 0x01 inside a string table.
        "app/src/DnaEntropyGraph.App/Assets/viewer/igv.min.js",
    }
)


@dataclass(frozen=True)
class Finding:
    path: str
    line: int
    column: int
    byte: int
    untracked: bool = False

    def __str__(self) -> str:
        suffix = " (untracked file)" if self.untracked else ""
        return f"{self.path}:{self.line}:{self.column}: control byte 0x{self.byte:02x}{suffix}"


class GitCommandFailed(Exception):
    """A git call exited non-zero. The message names the subcommand, the exit code and git's own words."""

    def __init__(self, args_: tuple[str, ...], returncode: int, detail: str) -> None:
        self.returncode = returncode
        shown = " ".join(args_[:3])
        super().__init__(f"git {shown} failed ({returncode}): {detail}")


class NotAGitRepo(GitCommandFailed):
    """The failed git call said the directory is not a repository (a subset of GitCommandFailed)."""


def is_binary(data: bytes) -> bool:
    return b"\0" in data[:BINARY_SNIFF_BYTES]


def scan_bytes(path: str, data: bytes) -> list[Finding]:
    """Findings (1-based line, 1-based byte column) for one file's bytes, at most MAX_PER_FILE."""
    found: list[Finding] = []
    for match in CONTROL_BYTES.finditer(data):
        start = match.start()
        line = data.count(b"\n", 0, start) + 1
        column = start - (data.rfind(b"\n", 0, start) + 1) + 1
        found.append(Finding(path, line, column, data[start]))
        if len(found) >= MAX_PER_FILE:
            break
    return found


def find_control_bytes_in(files: dict[str, bytes], allowlist: frozenset[str] | set[str] = ALLOWLIST) -> list[Finding]:
    """Scan already-read files. No binary rule here: callers (git numstat, scan_files) decide that."""
    out: list[Finding] = []
    for name in sorted(files):
        if name not in allowlist:
            out.extend(scan_bytes(name, files[name]))
    return out


def scan_files(files: dict[str, bytes], allowlist: frozenset[str] | set[str] = ALLOWLIST) -> list[Finding]:
    return find_control_bytes_in({n: d for n, d in files.items() if not is_binary(d)}, allowlist)


def _git(root: Path, *args: str, stdin: bytes | None = None) -> bytes:
    proc = subprocess.run(["git", *args], cwd=root, input=stdin, capture_output=True, check=False)
    if proc.returncode != 0:
        detail = proc.stderr.decode("utf-8", errors="replace").strip() or f"git {args[0]} failed"
        failure = NotAGitRepo if "not a git repository" in detail.lower() else GitCommandFailed
        raise failure(args, proc.returncode, detail)
    return proc.stdout


def text_attr_unset(root: Path, names: list[str], source: str | None = None) -> set[str]:
    """Paths whose `text` attribute is unset (`-text` or `binary`).

    `source` (a commit) reads .gitattributes from that tree instead of the working tree, so land_wt
    applies the same rule to the commit it is about to land that premerge applies to the working tree.
    """
    if not names:
        return set()
    argv = ["check-attr", *(["--source", source] if source else []), "-z", "--stdin", "text"]
    stdin = b"\0".join(n.encode("utf-8", errors="surrogateescape") for n in names) + b"\0"
    raw = _git(root, *argv, stdin=stdin)
    parts = raw.split(b"\0")
    unset: set[str] = set()
    for i in range(0, len(parts) - 2, 3):
        if parts[i + 2] == b"unset":
            unset.add(parts[i].decode("utf-8", errors="surrogateescape"))
    return unset


def check_repo(root: Path) -> list[Finding]:
    def listing(*flags: str) -> set[str]:
        raw = _git(root, "ls-files", "-z", *flags)
        return {n.decode("utf-8", errors="surrogateescape") for n in raw.split(b"\0") if n}

    tracked = listing("--cached")
    untracked = listing("--others", "--exclude-standard") - tracked
    names = sorted(tracked | untracked)
    skip = text_attr_unset(root, [n for n in names if n not in ALLOWLIST])
    files: dict[str, bytes] = {}
    for name in names:
        if name in skip:
            continue
        path = root / name
        try:
            # Untracked files belong to no commit (owner scratch, build output not in .gitignore): never slurp a big one.
            if name in untracked and path.stat().st_size > MAX_UNTRACKED_BYTES:
                continue
            files[name] = path.read_bytes()
        except OSError:
            continue  # tracked but deleted in the working tree
    return [replace(f, untracked=f.path in untracked) for f in scan_files(files)]


def self_test() -> bool:
    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        subprocess.run(["git", "init", "-q"], cwd=root, check=True, capture_output=True)
        (root / "a.txt").write_bytes(b"clean\ttext\r\n")
        if check_repo(root):
            print("FAIL: a clean file must not be flagged")
            ok = False
        else:
            print("ok    a clean file passes")
        (root / "a.txt").write_bytes(b"one\ntw" + bytes([0x08]) + b"o\n")
        got = [(f.path, f.line, f.column) for f in check_repo(root)]
        if got != [("a.txt", 2, 3)]:
            print(f"FAIL: planted 0x08 should be named a.txt:2:3, got {got}")
            ok = False
        else:
            print("ok    a planted 0x08 is named with file:line:column")
    print(f"\n{'PASS' if ok else 'FAIL'}: check_control_bytes self-test")
    return ok


REPO_ROOT = Path(__file__).resolve().parents[1]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Fail if a control byte appears in a text file.")
    ap.add_argument("--root", type=Path, default=REPO_ROOT, help="repo root (default: the repo this script lives in)")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return 0 if self_test() else 1
    try:
        findings = check_repo(args.root.resolve())
    except NotAGitRepo as exc:
        print(f"ERROR: {args.root} is not a git repository: {exc}", file=sys.stderr)
        return 2
    except GitCommandFailed as exc:
        print(f"ERROR: {exc}. Update git (2.40 or later) or run it from a healthy checkout", file=sys.stderr)
        return 2
    for finding in findings:
        print(f"ERROR: {finding}", file=sys.stderr)
    if findings:
        print(
            f"\n{len(findings)} control byte(s) in text files. A backslash escape was probably mangled "
            "on write; fix those files and run this again.",
            file=sys.stderr,
        )
        return 1
    print("check_control_bytes: no control bytes in text files.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
