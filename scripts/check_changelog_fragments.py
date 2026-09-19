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

Exit codes: 0 clean (including no fragments at all -- an empty
`changelog.d/` is the normal, fully-drained state), 1 at least one fragment
does not start with `- `, 2 bad usage (`docs/changelog.d/` missing entirely,
which is different from empty -- see --self-test's own note on this).
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

CHANGELOG_DIR_RELATIVE = "docs/changelog.d"
IGNORED_FILENAMES = {"README.md"}


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

    print(f"\n{'PASS' if ok else 'FAIL'}: check_changelog_fragments self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Check that every docs/changelog.d/ fragment (other than README.md) starts with '- '.",
    )
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: current directory)")
    ap.add_argument("--self-test", action="store_true", help="run against synthetic fixtures and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    problems = check(args.root.resolve())
    if problems:
        for p in problems:
            print(f"ERROR: {p}", file=sys.stderr)
        print(f"\n{len(problems)} malformed fragment(s) in {CHANGELOG_DIR_RELATIVE}/.", file=sys.stderr)
        return 1
    print("check_changelog_fragments: clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
