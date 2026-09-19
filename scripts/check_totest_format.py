"""scripts/check_totest_format.py -- docs/ToTest.md rows are well-formed and fresh.

    python scripts/check_totest_format.py
    python scripts/check_totest_format.py --max-age-days 45
    python scripts/check_totest_format.py --self-test

`docs/ToTest.md` is Hard Rule 17's one carve-out from "GitHub Issues is the
only tracker": a closed issue whose behaviour could not be proven on the
thing it actually ships on. Appendix C section 3 ("ToTest.md row format")
fixes the row shape:

    | # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |

and names this script by name: "`scripts/check_totest_format.py` validates
the sha with `git cat-file -e` and fails CI on a row older than 45 days."

WHERE "AGE" COMES FROM
-----------------------
The row format has no date column -- the sha in column 2 already IS a date,
by way of the commit it names. A row's age is the age of the commit that
closed it: how long the behaviour has gone unproven since the issue closed,
not since someone happened to type the row. This also means age falls
naturally out of validating the sha in the first place (one `git show` gets
both the "is this a real commit" answer and its date), rather than needing
a second, hand-maintained timestamp that could drift from the sha next to
it.

WHAT COUNTS AS A ROW
---------------------
The `## Queue` section's Markdown table. The header and separator lines are
skipped. A sentinel "empty queue" row -- first cell exactly `*(none yet)*`,
every other cell blank -- is recognised and skipped too: an empty queue is
the GOOD state (Rule: "Must drain"), not a malformed row.

Exit codes: 0 clean (including a genuinely empty queue), 1 a row is
malformed, names an unknown `Needs`, names a sha this repo does not have, or
is older than --max-age-days, 2 bad usage (docs/ToTest.md missing, or --root
is not a git repository).
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

TOTEST_RELATIVE = "docs/ToTest.md"
DEFAULT_MAX_AGE_DAYS = 45

COLUMNS = ("#", "Closed by (commit sha)", "Needs", "Do this", "Passing looks like", "False pass looks like")
VALID_NEEDS = frozenset({"app-dev", "installer", "gpu-vm", "cpu-vm", "two-accounts", "local-gpu"})

_ISSUE_CELL_RE = re.compile(r"^#\d+$")
_SHA_RE = re.compile(r"\b[0-9a-fA-F]{7,40}\b")
_EMPTY_QUEUE_FIRST_CELL = "*(none yet)*"


@dataclass
class Row:
    line_no: int
    cells: tuple[str, ...]


def _split_row(line: str) -> tuple[str, ...] | None:
    stripped = line.strip()
    if not stripped.startswith("|"):
        return None
    # Markdown table row: leading/trailing '|' are separators, not cells.
    inner = stripped.strip("|")
    return tuple(c.strip() for c in inner.split("|"))


def _is_separator_row(cells: tuple[str, ...]) -> bool:
    return all(re.fullmatch(r":?-{3,}:?", c) for c in cells)


def _is_empty_queue_sentinel(cells: tuple[str, ...]) -> bool:
    if not cells:
        return False
    return cells[0] == _EMPTY_QUEUE_FIRST_CELL and all(c == "" for c in cells[1:])


def parse_rows(text: str) -> list[Row]:
    """Every real data row of the `## Queue` table: header, separator, and
    the empty-queue sentinel row are all excluded. Only the FIRST table
    found after a `## Queue` heading is read -- the row-format example in
    the `## Row format` section above it is fenced code, not a live table,
    but this also protects against ever reading the wrong table if the file
    grows a second one."""
    lines = text.splitlines()
    try:
        queue_at = next(i for i, ln in enumerate(lines) if ln.strip().startswith("## Queue"))
    except StopIteration:
        return []

    rows: list[Row] = []
    header_seen = False
    separator_seen = False
    for i in range(queue_at + 1, len(lines)):
        line = lines[i]
        if line.strip().startswith("## ") and header_seen:
            break  # next section
        cells = _split_row(line)
        if cells is None:
            continue
        if not header_seen:
            header_seen = True
            continue  # the header row itself, e.g. "| # | Closed by ... |"
        if not separator_seen:
            separator_seen = True
            continue  # the "| --- | --- | ... |" row
        if _is_empty_queue_sentinel(cells):
            continue
        rows.append(Row(line_no=i + 1, cells=cells))
    return rows


def validate_row_shape(row: Row) -> list[str]:
    """Structural problems only: column count, empty cells, unknown `Needs`,
    malformed `#`. Never touches git."""
    problems = []
    if len(row.cells) != len(COLUMNS):
        problems.append(
            f"L{row.line_no}: {len(row.cells)} column(s), expected {len(COLUMNS)} ({', '.join(COLUMNS)})"
        )
        return problems  # further checks assume the right shape

    number, sha_cell, needs, do_this, passing, false_pass = row.cells
    if not _ISSUE_CELL_RE.match(number):
        problems.append(f"L{row.line_no}: '#' column {number!r} is not '#' followed by digits")
    if needs not in VALID_NEEDS:
        problems.append(
            f"L{row.line_no}: Needs {needs!r} is not one of {sorted(VALID_NEEDS)}"
        )
    for name, cell in (("Do this", do_this), ("Passing looks like", passing), ("False pass looks like", false_pass)):
        if not cell:
            problems.append(f"L{row.line_no}: '{name}' is empty")
    if not _SHA_RE.search(sha_cell):
        problems.append(f"L{row.line_no}: 'Closed by (commit sha)' {sha_cell!r} has no sha-shaped token")
    return problems


def extract_sha(sha_cell: str) -> str | None:
    m = _SHA_RE.search(sha_cell)
    return m.group(0) if m else None


def _run_git(root: Path, args: list[str]) -> subprocess.CompletedProcess:
    return subprocess.run(["git", *args], cwd=root, capture_output=True, text=True, timeout=15)


def commit_exists(root: Path, sha: str) -> bool:
    proc = _run_git(root, ["cat-file", "-e", f"{sha}^{{commit}}"])
    return proc.returncode == 0


def commit_date(root: Path, sha: str) -> datetime | None:
    proc = _run_git(root, ["show", "-s", "--format=%aI", sha])
    if proc.returncode != 0:
        return None
    out = proc.stdout.strip()
    if not out:
        return None
    try:
        return datetime.fromisoformat(out)
    except ValueError:
        return None


def row_age_days(commit_dt: datetime, now: datetime) -> int:
    """Whole days between `commit_dt` and `now`, both timezone-aware. A
    pure function so --self-test never needs a real git history to prove
    the arithmetic and the threshold comparison are right."""
    if commit_dt.tzinfo is None:
        commit_dt = commit_dt.replace(tzinfo=timezone.utc)
    if now.tzinfo is None:
        now = now.replace(tzinfo=timezone.utc)
    return (now - commit_dt).days


def is_stale(age_days: int, max_age_days: int) -> bool:
    return age_days > max_age_days


def check(root: Path, max_age_days: int, now: datetime | None = None) -> list[str]:
    now = now or datetime.now(timezone.utc)
    path = root / TOTEST_RELATIVE
    if not path.is_file():
        return [f"{TOTEST_RELATIVE} does not exist"]

    text = path.read_text(encoding="utf-8", errors="replace")
    rows = parse_rows(text)

    problems: list[str] = []
    for row in rows:
        shape_problems = validate_row_shape(row)
        problems.extend(shape_problems)
        if shape_problems:
            continue  # a malformed row cannot be sha/age-checked meaningfully

        sha_cell = row.cells[1]
        sha = extract_sha(sha_cell)
        if sha is None:
            continue  # already reported by validate_row_shape

        if not commit_exists(root, sha):
            problems.append(f"L{row.line_no}: sha '{sha}' is not a commit this repo has (git cat-file -e failed)")
            continue

        dt = commit_date(root, sha)
        if dt is None:
            problems.append(f"L{row.line_no}: could not read a date for commit '{sha}'")
            continue

        age = row_age_days(dt, now)
        if is_stale(age, max_age_days):
            problems.append(
                f"L{row.line_no}: row closed by {sha} is {age} day(s) old, over --max-age-days {max_age_days} "
                "-- drain it (verify and delete, or reopen the issue)"
            )

    return problems


# ---------------------------------------------------------------------------
# Self-test: the pure parsing/shape/age logic needs no git at all; the sha
# validity checks use a real, throwaway git repo (one commit) so both the
# "sha exists" and "sha does not exist" arms are proven against real `git
# cat-file`/`git show`, never mocked.
# ---------------------------------------------------------------------------

_GOOD_TABLE = """## Queue

| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |
| --- | --- | --- | --- | --- | --- |
| #12 | {sha} | installer | Install and run once | The tray icon appears | Process runs, no tray icon |
"""

_EMPTY_TABLE = """## Queue

Empty. No issue has been closed against unproven behaviour yet.

| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |
| --- | --- | --- | --- | --- | --- |
| *(none yet)* | | | | | |
"""

_MALFORMED_TABLE = """## Queue

| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |
| --- | --- | --- | --- | --- | --- |
| 12 | {sha} | not-a-real-need | | Something | |
"""

_BAD_SHA_TABLE = """## Queue

| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |
| --- | --- | --- | --- | --- | --- |
| #12 | 0000000deadbeef0000000deadbeef00000000 | installer | Install and run once | Works | Doesn't |
"""


def _init_throwaway_repo(root: Path) -> str:
    """A real, tiny git repo with one commit; returns its sha."""
    def run(*args):
        subprocess.run(["git", *args], cwd=root, check=True, capture_output=True, text=True)

    run("init", "-q")
    run("config", "user.email", "test@example.com")
    run("config", "user.name", "Test")
    (root / "f.txt").write_text("x\n", encoding="utf-8")
    run("add", "f.txt")
    run("commit", "-q", "-m", "initial")
    sha = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=root, check=True, capture_output=True, text=True
    ).stdout.strip()
    return sha


def self_test() -> bool:
    import tempfile

    ok = True

    # --- pure logic: no git, no filesystem ---
    now = datetime(2026, 9, 19, tzinfo=timezone.utc)
    fresh = datetime(2026, 9, 1, tzinfo=timezone.utc)  # 18 days old
    old = datetime(2026, 6, 1, tzinfo=timezone.utc)    # well over 45 days old

    if row_age_days(fresh, now) != 18:
        print(f"FAIL row_age_days: expected 18, got {row_age_days(fresh, now)}")
        ok = False
    else:
        print("ok    row_age_days computes whole days between two timezone-aware dates")

    if is_stale(row_age_days(fresh, now), 45) or not is_stale(row_age_days(old, now), 45):
        print("FAIL is_stale: threshold comparison wrong")
        ok = False
    else:
        print("ok    is_stale fires only once age exceeds --max-age-days")

    parsed = parse_rows(_GOOD_TABLE.format(sha="abc1234"))
    if len(parsed) != 1 or parsed[0].cells[1] != "abc1234":
        print(f"FAIL parse_rows: expected 1 row with sha abc1234, got {parsed}")
        ok = False
    else:
        print("ok    parse_rows reads a well-formed data row and skips header/separator")

    if parse_rows(_EMPTY_TABLE):
        print(f"FAIL parse_rows: the '(none yet)' sentinel row must be skipped, got {parse_rows(_EMPTY_TABLE)}")
        ok = False
    else:
        print("ok    parse_rows treats the empty-queue sentinel row as zero real rows")

    malformed = parse_rows(_MALFORMED_TABLE.format(sha="abc1234"))
    shape_problems = validate_row_shape(malformed[0]) if malformed else []
    if not shape_problems:
        print("FAIL validate_row_shape: expected problems for a bad '#' cell, bad Needs, and empty cells")
        ok = False
    else:
        print(f"ok    validate_row_shape flags a malformed row ({len(shape_problems)} problem(s))")

    # --- git-backed: a real throwaway repo, one real commit ---
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        sha = _init_throwaway_repo(root)

        if not commit_exists(root, sha):
            print(f"FAIL commit_exists: {sha} should exist in its own repo")
            ok = False
        else:
            print("ok    commit_exists confirms a real commit via git cat-file -e")

        if commit_exists(root, "0000000deadbeef0000000deadbeef00000000"):
            print("FAIL commit_exists: a bogus sha should not exist")
            ok = False
        else:
            print("ok    commit_exists refuses a sha that is not a real commit")

        dt = commit_date(root, sha)
        if dt is None:
            print("FAIL commit_date: should read the real commit's date")
            ok = False
        else:
            print("ok    commit_date reads a real commit's author date")

        (root / "docs").mkdir()
        (root / "docs" / "ToTest.md").write_text(_GOOD_TABLE.format(sha=sha), encoding="utf-8")
        # -1: no commit's age (a non-negative number of days) can ever satisfy
        # a -1-day budget, so this is stale regardless of how many
        # sub-second this self-test's own commit is old by the time it runs.
        problems = check(root, max_age_days=-1)
        if not any("day(s) old" in p for p in problems):
            print(f"FAIL check: expected a staleness problem with --max-age-days -1, got {problems}")
            ok = False
        else:
            print("ok    check() flags a row as stale once its commit is older than --max-age-days")

        problems = check(root, max_age_days=36500)  # 100 years: never stale
        if problems:
            print(f"FAIL check: a fresh, well-formed row should be clean at a huge max-age, got {problems}")
            ok = False
        else:
            print("ok    check() is clean for a well-formed, fresh row")

        (root / "docs" / "ToTest.md").write_text(_BAD_SHA_TABLE, encoding="utf-8")
        problems = check(root, max_age_days=DEFAULT_MAX_AGE_DAYS)
        if not any("is not a commit this repo has" in p for p in problems):
            print(f"FAIL check: expected an unknown-sha problem, got {problems}")
            ok = False
        else:
            print("ok    check() refuses a sha this repo does not have")

    print(f"\n{'PASS' if ok else 'FAIL'}: check_totest_format self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Validate docs/ToTest.md's row shape, Needs values, commit shas, and row age.",
    )
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: current directory)")
    ap.add_argument("--max-age-days", type=int, default=DEFAULT_MAX_AGE_DAYS,
                     help=f"fail a row whose closing commit is older than this many days (default {DEFAULT_MAX_AGE_DAYS})")
    ap.add_argument("--self-test", action="store_true", help="run against synthetic fixtures and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    root = args.root.resolve()
    if not (root / ".git").exists():
        print(f"error: {root} does not look like a git repository (no .git)", file=sys.stderr)
        return 2

    problems = check(root, args.max_age_days)
    if problems:
        for p in problems:
            print(f"ERROR: {p}", file=sys.stderr)
        print(f"\n{len(problems)} problem(s) in {TOTEST_RELATIVE}.", file=sys.stderr)
        return 1
    print(f"check_totest_format: clean (--max-age-days {args.max_age_days}).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
