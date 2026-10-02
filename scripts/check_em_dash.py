"""scripts/check_em_dash.py -- no em dash in user-facing copy (Hard Rule 13).

    python scripts/check_em_dash.py
    python scripts/check_em_dash.py --self-test
    python scripts/check_em_dash.py --root <dir>

This is `ci-docs.yml`'s former inline `git grep` step, moved into a script so it can be
run locally (scripts/premerge.py runs it) and so it can carry a `--self-test` that plants
an em dash and watches the guard say no, like every other check_*.py (#425). An inline
one-liner that has never been seen to fail is the half-wired-guard shape this repo keeps
finding.

WATCHED PATHS (unchanged from the inline step; changing them is a separate decision)
    docs/user_guide/**   *.resw   .github/ISSUE_TEMPLATE/**   README.md
Only TRACKED files are read (the same set `git grep` searched), so build output such as
an `obj/` copy of Resources.resw can never trip it.

Exit codes: 0 clean, 1 an em dash found, 2 bad usage (not a git repository).
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

EM_DASH = chr(0x2014)  # spelled as a code point so this file itself never contains one
WATCHED_PATHSPECS = ("docs/user_guide/**", "*.resw", ".github/ISSUE_TEMPLATE/**", "README.md")


@dataclass(frozen=True)
class Finding:
    path: str
    line: int
    text: str

    def __str__(self) -> str:
        return f"{self.path}:{self.line}: {self.text.strip()}"


class NotAGitRepo(Exception):
    pass


def tracked_watched_files(root: Path) -> list[str]:
    proc = subprocess.run(
        ["git", "ls-files", "-z", "--", *WATCHED_PATHSPECS],
        cwd=root,
        capture_output=True,
        check=False,
    )
    if proc.returncode != 0:
        raise NotAGitRepo(proc.stderr.decode("utf-8", errors="replace").strip() or "git ls-files failed")
    return [name for name in proc.stdout.decode("utf-8", errors="replace").split("\0") if name]


def check(root: Path) -> list[Finding]:
    findings: list[Finding] = []
    for rel in tracked_watched_files(root):
        path = root / rel
        try:
            data = path.read_bytes()
        except OSError:
            continue  # tracked but deleted in the working tree; nothing to scan
        if b"\0" in data:
            continue  # binary, like git grep -I
        for number, line in enumerate(data.decode("utf-8", errors="replace").splitlines(), 1):
            if EM_DASH in line:
                findings.append(Finding(rel, number, line))
    return findings


def self_test() -> bool:
    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        subprocess.run(["git", "init", "-q"], cwd=root, check=True, capture_output=True)

        def plant(rel: str, text: str) -> None:
            target = root / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(text, encoding="utf-8", newline="\n")
            subprocess.run(["git", "add", "-A"], cwd=root, check=True, capture_output=True)

        plant("docs/user_guide/a.md", "clean line\n")
        if check(root):
            print("FAIL: a clean watched file must not be flagged")
            ok = False
        else:
            print("ok    a clean watched file passes")

        for rel in ("docs/user_guide/a.md", "src/Strings/Resources.resw", ".github/ISSUE_TEMPLATE/b.md", "README.md"):
            plant(rel, f"line one\nbad {EM_DASH} line\n")
            found = [f for f in check(root) if f.path == rel]
            if [f.line for f in found] != [2]:
                print(f"FAIL: planted em dash in {rel} should be named at line 2, got {found}")
                ok = False
            else:
                print(f"ok    planted em dash in {rel} is named with file and line")
            plant(rel, "clean again\n")

        plant("docs/hard_rules.md", f"unwatched {EM_DASH} path\n")
        if check(root):
            print("FAIL: an em dash outside the watched globs must not be flagged")
            ok = False
        else:
            print("ok    an unwatched path is left alone")
    print(f"\n{'PASS' if ok else 'FAIL'}: check_em_dash self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Fail if an em dash appears in user-facing copy.")
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: cwd)")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return 0 if self_test() else 1
    try:
        findings = check(args.root.resolve())
    except NotAGitRepo as exc:
        print(f"ERROR: {args.root} is not a git repository: {exc}", file=sys.stderr)
        return 2
    for finding in findings:
        print(f"ERROR: {finding}", file=sys.stderr)
    if findings:
        print(
            f"\n{len(findings)} em dash(es) in user-facing copy. Use a plain hyphen or rewrite the sentence.",
            file=sys.stderr,
        )
        return 1
    print("check_em_dash: no em dashes in user-facing copy.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
