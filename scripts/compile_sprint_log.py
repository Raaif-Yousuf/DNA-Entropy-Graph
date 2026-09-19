"""Fold docs/changelog.d/ fragments into docs/sprint_log.md.

Why: with multiple agent sessions merging branches the same day, every branch
editing the top of sprint_log.md's "Recent changes" section collides with
every other one editing the same few lines. Instead, a branch writes its
bullet to its own file, docs/changelog.d/<branch>.md, which never conflicts,
and the merger runs this script once at merge time:

    python scripts/compile_sprint_log.py [--dry-run]

Each fragment is the exact sprint-log bullet (starts with "- ", may span
multiple lines / contain several bullets). Fragments are prepended to the
"## Recent changes" section newest-first (git last-commit time, falling back
to file mtime) and deleted; the merger commits the result. Every session
still logs its own work; only the write path differs (see
docs/changelog.d/README.md).

Stdlib only. Exits non-zero (touching nothing) on a malformed fragment or a
sprint log missing the "## Recent changes" heading.
"""

import argparse
import subprocess
import sys
from pathlib import Path

HEADING = "## Recent changes"
IGNORED = {"README.md"}

# The one directory compile_fragments() reads fragments from. Kept as a
# constant (rather than inlined in compile_fragments()) so
# find_stray_changelog_dirs() below can be tied to it structurally: #903's
# regression test asserts compile_fragments()'s source actually references
# this name, so a future refactor that adds a second fragments source has to
# touch the stray-directory guard deliberately instead of leaving it blind.
FRAGMENTS_RELATIVE = ("docs", "changelog.d")


def _repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def _fragment_files(fragments_dir: Path) -> list[Path]:
    if not fragments_dir.is_dir():
        return []
    return [p for p in sorted(fragments_dir.glob("*.md")) if p.name not in IGNORED]


def _sort_key(path: Path, root: Path) -> float:
    """Newest-first ordering: git last-commit time, else file mtime."""
    try:
        out = subprocess.run(
            ["git", "log", "-1", "--format=%ct", "--", str(path)],
            cwd=root, capture_output=True, text=True, timeout=10,
        ).stdout.strip()
        if out:
            return float(out)
    except (OSError, subprocess.SubprocessError, ValueError):
        pass
    return path.stat().st_mtime


def _validate(path: Path) -> str:
    """Return the fragment's stripped text; raise ValueError if malformed.

    The heading-hint below exists because this exact mistake already cost two
    days: four fragments written as Markdown headings ("## ..." / "### ...")
    instead of "- " bullets made every one of them fail this check, so
    `compile_sprint_log.py` correctly exited non-zero and folded nothing — but
    the only way anyone found out was a human running the script by hand and
    reading stderr, which nobody did until 2026-08-01 (fixed in ec9fb70f).
    `test_live_changelog_fragments_are_well_formed` in
    scripts/tests/test_compile_sprint_log.py now catches this inside the
    normal test suite instead of relying on that; this hint just makes the
    fix obvious once it does.
    """
    text = path.read_text(encoding="utf-8").strip()
    if not text:
        raise ValueError(f"{path.name}: empty fragment")
    if not text.startswith("- "):
        hint = ""
        if text.lstrip().startswith("#"):
            hint = (
                " (looks like a Markdown heading, not a bullet — rewrite the "
                "leading '#'/'##'/'###' line as the bullet's bold lead-in, "
                "e.g. '- **Heading text** — details.', and indent the rest "
                "as continuation lines/sub-bullets)"
            )
        raise ValueError(f"{path.name}: must start with a '- ' bullet{hint}")
    return text


def _tracked_and_untracked_paths(root: Path) -> list[str]:
    """`git ls-files` for tracked + untracked-but-not-ignored paths, as
    forward-slash strings relative to `root`. This is how a human `rg` of
    the repo sees it: .gitignore'd trees (`worker/.venv`, `app/**/bin`,
    `app/**/obj`) are never walked, without hand-rolling ignore-file parsing.
    """
    result = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard"],
        cwd=root, capture_output=True, text=True, timeout=30,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"git ls-files failed under {root} (rc={result.returncode}): "
            f"{result.stderr.strip()}"
        )
    return [line for line in result.stdout.splitlines() if line]


def find_stray_changelog_dirs(root: Path) -> list[str]:
    """Any `changelog.d` directory under `root` other than the one this
    script actually reads (FRAGMENTS_RELATIVE), as sorted POSIX-style paths
    relative to `root`. Skips `docs/archive/` (frozen history, excluded from
    search by repo convention).

    A fragment written to the wrong directory (e.g. `worker/docs/changelog.d/`
    instead of `docs/changelog.d/`) would otherwise be silently dropped by
    compile_fragments(), because it only ever globs `docs/changelog.d/*.md`
    non-recursively, and no guard would notice on its own. This is that
    guard.
    """
    correct = "/".join(FRAGMENTS_RELATIVE)
    stray: set[str] = set()
    for rel in _tracked_and_untracked_paths(root):
        rel = rel.replace("\\", "/")
        if rel.startswith("docs/archive/"):
            continue
        parts = rel.split("/")
        for i in range(len(parts) - 1):  # directory components only, not the filename
            if parts[i] == "changelog.d":
                dir_path = "/".join(parts[: i + 1])
                if dir_path != correct:
                    stray.add(dir_path)
    return sorted(stray)


def compile_fragments(root: Path, dry_run: bool = False) -> int:
    fragments_dir = root / Path(*FRAGMENTS_RELATIVE)
    sprint_log = root / "docs" / "sprint_log.md"

    files = _fragment_files(fragments_dir)
    if not files:
        print("No changelog fragments to compile.")
        return 0

    errors = []
    fragments = []
    for path in files:
        try:
            fragments.append((path, _validate(path)))
        except ValueError as exc:
            errors.append(str(exc))
    if errors:
        for err in errors:
            print(f"ERROR: {err}", file=sys.stderr)
        print("Nothing was modified.", file=sys.stderr)
        return 1

    log_lines = sprint_log.read_text(encoding="utf-8").splitlines()
    try:
        insert_at = log_lines.index(HEADING) + 1
    except ValueError:
        print(f"ERROR: {sprint_log} has no '{HEADING}' heading.", file=sys.stderr)
        return 1
    # Skip the blank line that follows the heading so bullets land after it.
    while insert_at < len(log_lines) and not log_lines[insert_at].strip():
        insert_at += 1

    fragments.sort(key=lambda item: _sort_key(item[0], root), reverse=True)

    block: list[str] = []
    for path, text in fragments:
        block.extend(text.splitlines())
        block.append("")
        print(f"{'[dry-run] ' if dry_run else ''}folding {path.name}")

    if dry_run:
        print(f"[dry-run] would prepend {len(fragments)} fragment(s) and delete them.")
        return 0

    log_lines[insert_at:insert_at] = block
    sprint_log.write_text("\n".join(log_lines) + "\n", encoding="utf-8")
    for path, _ in fragments:
        path.unlink()
    print(f"Prepended {len(fragments)} fragment(s) to {sprint_log.name}; "
          "commit the result (fragments deleted).")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=_repo_root(),
                        help="repo root (default: this script's parent repo)")
    parser.add_argument("--dry-run", action="store_true",
                        help="report what would happen without writing")
    args = parser.parse_args()
    return compile_fragments(args.root.resolve(), dry_run=args.dry_run)


if __name__ == "__main__":
    sys.exit(main())
