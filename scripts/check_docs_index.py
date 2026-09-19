"""scripts/check_docs_index.py -- every doc is findable, every link is real.

    python scripts/check_docs_index.py
    python scripts/check_docs_index.py --self-test

Two checks, both against `docs/README.md` (the topic-map index Appendix C
§3 calls the "House style: who owns which fact" surface):

  1. Every `.md` file under `docs/` -- excluding `docs/migration/` (a frozen
     migration record), `docs/superpowers/` (dated specs, their own
     directory), and `docs/changelog.d/` (branch fragments, deleted at
     merge, `check_changelog_fragments.py`'s job, not this one's) -- is
     linked from `docs/README.md` by at least one Markdown link. A doc
     nobody can reach from the index is the wired-to-nothing shape applied
     to documentation: it compiles (renders on GitHub), it "exists", and no
     session ever finds it because `docs/README.md`'s own "reading order"
     and "topic map" are how this repo's own house style says a doc gets
     found.
  2. Every relative link `docs/README.md` itself makes -- inside `docs/` or
     climbing out of it (`../CLAUDE.md`, `../.claude/README.md`) -- resolves
     to a real file or directory. A stale link is the opposite failure:
     the index promises something that is not there.

`docs/README.md` is exempt from rule 1 (a file does not need to link to
itself), and a link that is a directory (trailing slash, e.g.
`superpowers/specs/`) satisfies rule 2 by existing as a directory; it is not
required to be a file.

Exit codes: 0 clean, 1 at least one file is unlinked or one link is broken,
2 bad usage (docs/README.md itself missing, or --root is not a repo).
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

INDEX_RELATIVE = "docs/README.md"

# Directories under docs/ that do not need an inbound link from the index.
# Kept as a tuple of docs/-relative prefixes, not absolute paths, so the
# same list works against any --root (including a --self-test fixture).
EXCLUDED_DOC_DIR_PREFIXES = ("migration/", "superpowers/", "changelog.d/")

# A Markdown inline link: [text](target). Deliberately not a full CommonMark
# parser -- this repo's docs write links in the plain `[text](target)` shape
# throughout, and a false negative here (a link this regex fails to see)
# fails toward UNDER-reporting broken links, which is the direction a CI
# check should never fail in for "is this thing linked" -- so this pattern
# is intentionally permissive about what counts as a link target.
_LINK_RE = re.compile(r"\[[^\]]*\]\(([^)]+)\)")


def _is_external_or_anchor(target: str) -> bool:
    t = target.strip()
    if not t:
        return True
    if t.startswith("#"):
        return True
    if re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*://", t):  # scheme://... (http, https, mailto is caught below too)
        return True
    if t.startswith("mailto:"):
        return True
    return False


def _strip_link_target(target: str) -> str:
    """Drop a trailing `#anchor` or a Markdown title (`"..."`) from a link
    target, and any surrounding angle brackets (`<path with spaces>`)."""
    t = target.strip()
    if t.startswith("<") and ">" in t:
        # The angle-bracket form exists specifically to allow whitespace in
        # the URL, so the content between the brackets is the whole target
        # -- it must NOT also go through the whitespace-splits-off-a-title
        # step below, which would wrongly truncate it at its first space.
        t = t[1 : t.index(">")]
        if "#" in t:
            t = t.split("#", 1)[0]
        return t
    # A title after the URL is separated by whitespace: `path "Title"`.
    parts = t.split(None, 1)
    if parts:
        t = parts[0]
    if "#" in t:
        t = t.split("#", 1)[0]
    return t


def extract_links(markdown_text: str) -> list[str]:
    """Every non-external, non-anchor-only link target in `markdown_text`,
    in document order, exactly as written (not yet resolved to a path)."""
    out = []
    for m in _LINK_RE.finditer(markdown_text):
        target = _strip_link_target(m.group(1))
        if target and not _is_external_or_anchor(m.group(1)):
            out.append(target)
    return out


def find_doc_files(docs_dir: Path) -> list[Path]:
    """Every `.md` under `docs_dir`, excluding EXCLUDED_DOC_DIR_PREFIXES,
    as paths relative to `docs_dir`, sorted."""
    out = []
    for p in sorted(docs_dir.rglob("*.md")):
        rel = p.relative_to(docs_dir).as_posix()
        if any(rel.startswith(prefix) for prefix in EXCLUDED_DOC_DIR_PREFIXES):
            continue
        out.append(Path(rel))
    return out


def check(root: Path) -> list[str]:
    """Return a list of problem strings. Empty means clean."""
    problems: list[str] = []
    docs_dir = root / "docs"
    index_path = root / INDEX_RELATIVE

    if not index_path.is_file():
        return [f"{INDEX_RELATIVE} does not exist"]
    if not docs_dir.is_dir():
        return ["docs/ does not exist"]

    index_text = index_path.read_text(encoding="utf-8", errors="replace")
    raw_targets = extract_links(index_text)

    # Resolve every link target against docs/ (docs/README.md's own
    # location), and check it exists -- file OR directory.
    linked_doc_relpaths: set[str] = set()
    for target in raw_targets:
        resolved = (docs_dir / target).resolve()
        if not resolved.exists():
            problems.append(f"{INDEX_RELATIVE} links to '{target}', which does not exist ({resolved})")
            continue
        try:
            rel_to_docs = resolved.relative_to(docs_dir.resolve()).as_posix()
        except ValueError:
            continue  # outside docs/ (e.g. ../CLAUDE.md) -- existence already checked above
        linked_doc_relpaths.add(rel_to_docs)
        if resolved.is_dir():
            # A directory link (e.g. `superpowers/specs/`) covers every file
            # under it transitively -- the files inside are that
            # subdirectory's own business, not docs/README.md's, exactly
            # the same reasoning EXCLUDED_DOC_DIR_PREFIXES already applies
            # to migration/superpowers/changelog.d explicitly.
            for p in resolved.rglob("*"):
                if p.is_file():
                    linked_doc_relpaths.add(p.relative_to(docs_dir.resolve()).as_posix())

    for doc in find_doc_files(docs_dir):
        rel = doc.as_posix()
        if rel == "README.md":
            continue  # the index does not need to link to itself
        if rel not in linked_doc_relpaths:
            problems.append(f"docs/{rel} is not linked from {INDEX_RELATIVE}")

    return problems


# ---------------------------------------------------------------------------
# Self-test: a synthetic docs/ tree, no dependency on this repo's real
# content, proving both the "unlinked file" and "broken link" arms actually
# fire, plus the exclusions and the directory-link case.
# ---------------------------------------------------------------------------

def _build_fixture(root: Path) -> None:
    docs = root / "docs"
    (docs / "migration").mkdir(parents=True)
    (docs / "superpowers" / "specs").mkdir(parents=True)
    (docs / "changelog.d").mkdir(parents=True)
    (docs / "linked_dir").mkdir(parents=True)

    (docs / "README.md").write_text(
        "# index\n\n"
        "[linked](linked.md)\n\n"
        "[broken](does-not-exist.md)\n\n"
        "[outside](../CLAUDE.md)\n\n"
        "[a dir](linked_dir/)\n\n"
        "[an anchor only](#section)\n\n"
        "[external](https://example.com)\n",
        encoding="utf-8",
    )
    (docs / "linked.md").write_text("linked\n", encoding="utf-8")
    (docs / "unlinked.md").write_text("orphan\n", encoding="utf-8")
    (docs / "linked_dir" / "inner.md").write_text("inner\n", encoding="utf-8")
    (docs / "migration" / "record.md").write_text("excluded dir\n", encoding="utf-8")
    (docs / "superpowers" / "specs" / "spec.md").write_text("excluded dir\n", encoding="utf-8")
    (docs / "changelog.d" / "a-branch.md").write_text("- excluded dir\n", encoding="utf-8")
    (root / "CLAUDE.md").write_text("root file the index links to\n", encoding="utf-8")


def self_test() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        _build_fixture(root)
        problems = check(root)

        expect_unlinked = "docs/unlinked.md is not linked from docs/README.md"
        expect_broken = "does-not-exist.md"
        if not any(expect_unlinked in p for p in problems):
            print(f"FAIL: expected an 'unlinked' problem for docs/unlinked.md, got: {problems}")
            ok = False
        else:
            print("ok    detects a real, unexcluded doc that docs/README.md never links to")

        if not any(expect_broken in p for p in problems):
            print(f"FAIL: expected a 'broken link' problem for does-not-exist.md, got: {problems}")
            ok = False
        else:
            print("ok    detects a link in docs/README.md that resolves to nothing")

        excluded_ok = True
        for excluded in ("docs/migration/record.md", "docs/superpowers/specs/spec.md", "docs/changelog.d/a-branch.md"):
            if any(excluded in p for p in problems):
                print(f"FAIL: {excluded} should be excluded and was flagged: {problems}")
                ok = False
                excluded_ok = False
        if excluded_ok:
            print("ok    migration/, superpowers/, changelog.d/ are excluded from the inbound-link requirement")

        if any("linked_dir/inner.md" in p for p in problems):
            print("FAIL: a directory link should cover the files inside it")
            ok = False
        else:
            print("ok    a directory link (trailing slash) covers the files inside it")

        if any("README.md is not linked" in p for p in problems):
            print("FAIL: docs/README.md should not need to link to itself")
            ok = False
        else:
            print("ok    docs/README.md is not required to link to itself")

    # A clean tree must report zero problems.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        docs = root / "docs"
        docs.mkdir(parents=True)
        (docs / "README.md").write_text("# index\n\n[a](a.md)\n", encoding="utf-8")
        (docs / "a.md").write_text("a\n", encoding="utf-8")
        problems = check(root)
        if problems:
            print(f"FAIL: a fully-linked, link-clean tree should report zero problems, got: {problems}")
            ok = False
        else:
            print("ok    a fully-linked, link-clean tree reports zero problems")

    print(f"\n{'PASS' if ok else 'FAIL'}: check_docs_index self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Check that every docs/*.md is linked from docs/README.md and every link there resolves.",
    )
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: current directory)")
    ap.add_argument("--self-test", action="store_true", help="run against a synthetic fixture and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    problems = check(args.root.resolve())
    if problems:
        for p in problems:
            print(f"ERROR: {p}", file=sys.stderr)
        print(f"\n{len(problems)} problem(s). See docs/README.md's topic map and reading order.", file=sys.stderr)
        return 1
    print("check_docs_index: clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
