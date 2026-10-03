"""scripts/check_changelog_fragments.py -- every fragment is a real bullet.

    python scripts/check_changelog_fragments.py
    python scripts/check_changelog_fragments.py --self-test

`docs/changelog.d/<branch>.md` is the branch-to-sprint-log write path
(`docs/changelog.d/README.md`, `scripts/compile_sprint_log.py`): a branch in
flight writes its own fragment there instead of editing `docs/sprint_log.md`
directly, and `compile_sprint_log.py` folds every fragment in at merge time.
That fold already refuses a malformed fragment on its own (see
`compile_sprint_log.py`'s own `_validate`), but only when someone actually
runs it; this is the same rule as a standalone, CI-facing check, so a
malformed fragment is caught on the PR that wrote it, not discovered by
whoever merges next.

THE RULE
--------
Every file in `docs/changelog.d/` other than `README.md` must start with
`- ` (a literal hyphen and a space). A heading of any depth (`#`, `##`,
...) is rejected -- this is the exact mistake `compile_sprint_log.py`'s own
docstring records costing two days on the project this repo's tooling is
modelled on: a heading-shaped fragment silently failed to fold and nobody
noticed until a human ran the fold by hand and read stderr.

PRESENCE (--base REF, #429)
---------------------------
The shape rule above only validates fragments that exist, so a PR that changed
behaviour and wrote no fragment passed silently (Hard Rule 16 was enforced only
by a human brief). With `--base REF` the guard also lists what changed between
`merge-base(REF, HEAD)` and the WORKING TREE (committed, staged, unstaged and
untracked, so it works before and after the commit) and fails when a behaviour
file changed and no fragment came with it.

  Needs a fragment : worker/src/, worker/vm/, app/src/, scripts/, .github/workflows/,
                     worker/pyproject.toml, worker/Dockerfile*
  Never needs one  : docs/, any tests/ directory, *.md anywhere (checked first, so
                     scripts/tests/ and docs/ win over the scripts/ prefix)
  Counts as one    : an added or modified docs/changelog.d/<x>.md, OR a modified
                     docs/sprint_log.md (the fold already happened and deleted the
                     fragment, so a diff spanning the fold shows only the sprint log)
  Does not count  : a deleted fragment

Exit codes: 0 clean (including no fragments at all -- an empty
`changelog.d/` is the normal, fully-drained state), 1 at least one fragment
does not start with `- ` or (with --base) behaviour changed with no fragment,
2 bad usage (--base names a ref git cannot resolve, or --root is not a git
repository; a presence check that cannot run must never read as a pass).
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

CHANGELOG_DIR_RELATIVE = "docs/changelog.d"
IGNORED_FILENAMES = {"README.md"}

# Presence rule (#429). Exempt prefixes/parts are checked before the behaviour prefixes.
BEHAVIOUR_PREFIXES = ("worker/src/", "worker/vm/", "app/src/", "scripts/", ".github/workflows/")
BEHAVIOUR_FILES = ("worker/pyproject.toml",)
BEHAVIOUR_FILE_PREFIXES = ("worker/Dockerfile",)
EXEMPT_PREFIXES = ("docs/",)
FOLDED_LOG = "docs/sprint_log.md"


def find_fragments(changelog_dir: Path) -> list[Path]:
    if not changelog_dir.is_dir():
        return []
    return [p for p in sorted(changelog_dir.glob("*.md")) if p.name not in IGNORED_FILENAMES]


def validate_fragment(path: Path) -> str | None:
    """Return the problem string, or None if `path` is a well-formed
    fragment. Mirrors compile_sprint_log.py's own `_validate` exactly (same
    rule, same hint), so a fragment author sees the identical complaint
    whether it is caught here (before merge) or at fold time."""
    try:
        text = path.read_text(encoding="utf-8").strip()
    except OSError as exc:
        return f"{path.name}: could not read ({exc})"
    if not text:
        return f"{path.name}: empty fragment"
    if not text.startswith("- "):
        hint = ""
        if text.lstrip().startswith("#"):
            hint = (
                " (looks like a Markdown heading, not a bullet -- rewrite the "
                "leading '#'/'##'/'###' line as the bullet's bold lead-in, "
                "e.g. '- **Heading text**: details.', and indent the rest "
                "as continuation lines/sub-bullets)"
            )
        return f"{path.name}: must start with a '- ' bullet{hint}"
    return None


def _is_fragment_path(path: str) -> bool:
    return path.startswith(CHANGELOG_DIR_RELATIVE + "/") and path.endswith(".md") and path.rsplit("/", 1)[-1] not in IGNORED_FILENAMES


def needs_fragment(path: str) -> bool:
    """True when a change to `path` is a behaviour change that Hard Rule 16 wants a fragment for."""
    parts = path.split("/")
    if path.startswith(EXEMPT_PREFIXES) or "tests" in parts[:-1] or path.endswith(".md"):
        return False
    return (
        path.startswith(BEHAVIOUR_PREFIXES)
        or path in BEHAVIOUR_FILES
        or path.startswith(BEHAVIOUR_FILE_PREFIXES)
    )


def missing_fragment(changes: list[tuple[str, str]]) -> str | None:
    """`changes` is [(status letter, repo-relative posix path)]. Return the complaint, or None."""
    behaviour = sorted({path for status, path in changes if status != "D" and needs_fragment(path)})
    if not behaviour:
        return None
    for status, path in changes:
        if status != "D" and _is_fragment_path(path):
            return None
        if status != "D" and path == FOLDED_LOG:
            return None
    shown = ", ".join(behaviour[:5]) + (f" (+{len(behaviour) - 5} more)" if len(behaviour) > 5 else "")
    return (
        f"behaviour files changed ({shown}) but no changelog fragment was added: "
        f"write {CHANGELOG_DIR_RELATIVE}/<branch-name>.md starting with '- ' (Hard Rule 16)"
    )


class GitError(Exception):
    pass


def _git(root: Path, *args: str) -> str:
    proc = subprocess.run(["git", *args], cwd=root, capture_output=True, check=False)
    if proc.returncode != 0:
        raise GitError(proc.stderr.decode("utf-8", errors="replace").strip() or f"git {' '.join(args)} failed")
    return proc.stdout.decode("utf-8", errors="replace")


def changes_since(root: Path, base: str) -> list[tuple[str, str]]:
    """Everything that differs between merge-base(base, HEAD) and the working tree."""
    try:
        merge_base = _git(root, "merge-base", base, "HEAD").strip()
    except GitError:
        merge_base = _git(root, "rev-parse", "--verify", "--quiet", base + "^{commit}").strip()
    changes: list[tuple[str, str]] = []
    raw = _git(root, "diff", "--name-status", "--no-renames", "-z", merge_base).split("\0")
    index = 0
    while index + 1 < len(raw):
        status, path = raw[index], raw[index + 1]
        if status:
            changes.append((status[0], path))
        index += 2
    for path in _git(root, "ls-files", "-z", "--others", "--exclude-standard").split("\0"):
        if path:
            changes.append(("A", path))
    return changes


def check_against_base(root: Path, base: str) -> str | None:
    return missing_fragment(changes_since(root, base))


def check(root: Path) -> list[str]:
    changelog_dir = root / CHANGELOG_DIR_RELATIVE
    problems = []
    for fragment in find_fragments(changelog_dir):
        problem = validate_fragment(fragment)
        if problem:
            problems.append(problem)
    return problems


# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------

def self_test() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        changelog_dir = root / "docs" / "changelog.d"
        changelog_dir.mkdir(parents=True)

        (changelog_dir / "README.md").write_text("# the convention, not a fragment\n", encoding="utf-8")
        (changelog_dir / "good-branch.md").write_text("- fixed the thing\n", encoding="utf-8")
        (changelog_dir / "heading-branch.md").write_text("## fixed the thing\n", encoding="utf-8")
        (changelog_dir / "empty-branch.md").write_text("   \n", encoding="utf-8")

        problems = check(root)

        if any("README.md" in p for p in problems):
            print(f"FAIL: README.md must never be validated as a fragment, got: {problems}")
            ok = False
        else:
            print("ok    README.md is never treated as a fragment")

        if any(p.startswith("good-branch.md") for p in problems):
            print(f"FAIL: good-branch.md is well-formed and should not be flagged: {problems}")
            ok = False
        else:
            print("ok    a fragment starting with '- ' is accepted")

        if not any(p.startswith("heading-branch.md") and "heading" in p for p in problems):
            print(f"FAIL: heading-branch.md should be flagged with the heading hint, got: {problems}")
            ok = False
        else:
            print("ok    a Markdown-heading fragment is rejected with the heading hint")

        if not any(p.startswith("empty-branch.md") for p in problems):
            print(f"FAIL: empty-branch.md should be flagged as empty, got: {problems}")
            ok = False
        else:
            print("ok    a whitespace-only fragment is rejected as empty")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        # No docs/changelog.d/ at all: must be clean, not an error -- a repo
        # that has never had a branch fragment yet is a normal state.
        problems = check(root)
        if problems:
            print(f"FAIL: a missing docs/changelog.d/ should report zero problems, got: {problems}")
            ok = False
        else:
            print("ok    a missing docs/changelog.d/ directory is treated as clean, not an error")

    # Presence (#429): the guard must be able to FAIL, not only validate what exists.
    source_only = [("M", "worker/src/dna_entropy/cli.py")]
    if missing_fragment(source_only) is None:
        print("FAIL: a worker source change with no fragment must be reported missing")
        ok = False
    else:
        print("ok    a source change with no fragment is reported missing")
    if missing_fragment([*source_only, ("A", "docs/changelog.d/fix-1-x.md")]) is not None:
        print("FAIL: a source change WITH a fragment must pass")
        ok = False
    else:
        print("ok    a source change with a fragment passes")
    if missing_fragment([("M", "docs/dev_commands.md"), ("M", "worker/tests/test_x.py")]) is not None:
        print("FAIL: docs-only and test-only changes need no fragment")
        ok = False
    else:
        print("ok    a docs-only or test-only change needs no fragment")

    print(f"\n{'PASS' if ok else 'FAIL'}: check_changelog_fragments self-test")
    return ok


# Script-relative, never cwd-relative: running another tree's copy of this guard from
# elsewhere must scan the tree the script lives in (MEASURED 2026-10-02).
REPO_ROOT = Path(__file__).resolve().parents[1]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Check that every docs/changelog.d/ fragment (other than README.md) starts with '- '.",
    )
    ap.add_argument("--root", type=Path, default=REPO_ROOT, help="repo root (default: the repo this script lives in)")
    ap.add_argument("--base", metavar="REF", default=None,
                    help="also require a fragment when behaviour files changed since merge-base(REF, HEAD)")
    ap.add_argument("--self-test", action="store_true", help="run against synthetic fixtures and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    problems = check(args.root.resolve())
    if args.base:
        try:
            missing = check_against_base(args.root.resolve(), args.base)
        except GitError as exc:
            print(f"ERROR: cannot compare against {args.base!r}: {exc}", file=sys.stderr)
            return 2
        if missing:
            problems.append(missing)
    if problems:
        for p in problems:
            print(f"ERROR: {p}", file=sys.stderr)
        print(f"\n{len(problems)} malformed fragment(s) in {CHANGELOG_DIR_RELATIVE}/.", file=sys.stderr)
        return 1
    print("check_changelog_fragments: clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
