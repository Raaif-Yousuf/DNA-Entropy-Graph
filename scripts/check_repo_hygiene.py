"""scripts/check_repo_hygiene.py -- two repo-shape rules that were inline shell in ci-docs.yml (#449).

    python scripts/check_repo_hygiene.py
    python scripts/check_repo_hygiene.py --self-test
    python scripts/check_repo_hygiene.py --root <dir>

Moved out of YAML for the same reason as check_em_dash.py (#425): an inline step that has never
been seen to fail is the half-wired-guard shape this repo keeps finding, and it could not be run
locally or by `scripts/premerge.py`.

1. THE PRIVATE DONOR COPY IS NOT COMMITTED. `legacy/clair` is a private repository copied onto the
   owner's PC only and gitignored. If any file under it is tracked, this public repo would publish
   it. Checked with `git ls-files`, so an ignored-but-present directory is fine.
2. AGENTS.md STAYS A STUB. It is a pointer to CLAUDE.md, not a second rulebook: fewer than
   MAX_AGENTS_LINES lines. The old inline step passed with a notice when AGENTS.md was missing
   ("not created yet, issue #268"). It exists now, so a missing file FAILS: a rule that cannot fail
   because its subject is absent is the vacuous pass this repo has been bitten by before.

Exit codes: 0 clean, 1 a rule broken, 2 bad usage (not a git repository).
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import tempfile
from pathlib import Path

DONOR_PATH = "legacy/clair"
AGENTS_FILE = "AGENTS.md"
MAX_AGENTS_LINES = 20  # the file must have strictly fewer lines than this


class NotAGitRepo(Exception):
    pass


def tracked_under(root: Path, prefix: str) -> list[str]:
    proc = subprocess.run(
        ["git", "ls-files", "-z", "--", prefix], cwd=root, capture_output=True, check=False
    )
    if proc.returncode != 0:
        raise NotAGitRepo(proc.stderr.decode("utf-8", errors="replace").strip() or "git ls-files failed")
    return [n for n in proc.stdout.decode("utf-8", errors="replace").split("\0") if n]


def check(root: Path) -> list[str]:
    problems: list[str] = []
    tracked = tracked_under(root, DONOR_PATH)
    if tracked:
        shown = ", ".join(tracked[:5]) + (f" (+{len(tracked) - 5} more)" if len(tracked) > 5 else "")
        problems.append(
            f"{DONOR_PATH} is tracked ({shown}). It is a private donor copy and must never be committed "
            f"to this public repo: `git rm -r --cached {DONOR_PATH}`"
        )
    agents = root / AGENTS_FILE
    if not agents.is_file():
        problems.append(f"{AGENTS_FILE} is missing. It must exist as a short pointer to CLAUDE.md (issue #268)")
    else:
        lines = len(agents.read_text(encoding="utf-8").splitlines())
        if lines >= MAX_AGENTS_LINES:
            problems.append(
                f"{AGENTS_FILE} is {lines} lines. It is a pointer to CLAUDE.md, not a second rulebook: "
                f"keep it under {MAX_AGENTS_LINES}"
            )
    return problems


def self_test() -> bool:
    ok = True

    def git(repo: Path, *args: str) -> None:
        subprocess.run(["git", *args], cwd=repo, check=True, capture_output=True)

    def expect(label: str, problems: list[str], needle: str | None) -> None:
        nonlocal ok
        good = (not problems) if needle is None else any(needle in p for p in problems)
        print(("ok    " if good else "FAIL: ") + label + ("" if good else f" -> {problems}"))
        ok = ok and good

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        git(root, "init", "-q")
        (root / AGENTS_FILE).write_text("pointer\n" * 5, encoding="utf-8", newline="\n")
        expect("a short AGENTS.md and no donor passes", check(root), None)

        (root / AGENTS_FILE).write_text("line\n" * 25, encoding="utf-8", newline="\n")
        expect("a 25-line AGENTS.md is flagged with its line count", check(root), "25 lines")
        (root / AGENTS_FILE).write_text("line\n" * 19, encoding="utf-8", newline="\n")
        expect("19 lines still passes (the limit is strict)", check(root), None)
        (root / AGENTS_FILE).write_text("line\n" * 20, encoding="utf-8", newline="\n")
        expect("exactly 20 lines is flagged", check(root), "20 lines")

        (root / AGENTS_FILE).unlink()
        expect("a missing AGENTS.md fails instead of passing with a notice", check(root), "missing")
        (root / AGENTS_FILE).write_text("pointer\n", encoding="utf-8", newline="\n")

        (root / "legacy" / "clair").mkdir(parents=True)
        (root / "legacy" / "clair" / "secret.md").write_text("private\n", encoding="utf-8", newline="\n")
        expect("an untracked (ignored-style) donor copy passes", check(root), None)
        git(root, "add", "-f", "legacy/clair/secret.md")
        expect("a tracked donor file is flagged by name", check(root), "legacy/clair/secret.md")
    print(f"\n{'PASS' if ok else 'FAIL'}: check_repo_hygiene self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Repo-shape rules: no tracked donor copy, AGENTS.md stays a stub.")
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: cwd)")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return 0 if self_test() else 1
    try:
        problems = check(args.root.resolve())
    except NotAGitRepo as exc:
        print(f"ERROR: {args.root} is not a git repository: {exc}", file=sys.stderr)
        return 2
    for problem in problems:
        print(f"ERROR: {problem}", file=sys.stderr)
    if problems:
        return 1
    print("check_repo_hygiene: clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
