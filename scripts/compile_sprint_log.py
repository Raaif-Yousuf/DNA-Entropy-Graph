"""Fold docs/changelog.d/ fragments into docs/sprint_log.md.

Why: with multiple agent sessions merging branches the same day, every branch
editing the top of sprint_log.md's "Recent changes" section collides with
every other one editing the same few lines. Instead, a branch writes its
bullet to its own file, docs/changelog.d/<branch>.md, which never conflicts,
and the merger runs this script once at merge time:

    python scripts/compile_sprint_log.py [--dry-run]
    python scripts/compile_sprint_log.py --check   # shape only, never folds; what CI runs

Each fragment is the exact sprint-log bullet (starts with "- ", may span
multiple lines / contain several bullets). Fragments are prepended to the
"## Recent changes" section newest-first (git last-commit time, falling back
to file mtime) and deleted; the merger commits the result. Every session
still logs its own work; only the write path differs (see
docs/changelog.d/README.md).

Stdlib only. Exits non-zero (touching nothing) on a malformed fragment or a
sprint log missing the "## Recent changes" heading. `--check` (run by
`ci-docs.yml` on every PR) validates fragment shape only and never touches
`sprint_log.md`, so it does not need the heading to exist yet either.
"""

import argparse
import subprocess
import sys
from pathlib import Path

HEADING = "## Recent changes"
IGNORED = {"README.md"}

# The one directory compile_fragments() reads fragments from. Kept as a
# constant (rather than inlined in compile_fragments()) so
# find_stray_changelog_dirs() below can be tied to it structurally: its own
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


def _bulk_commit_times(root: Path, fragments_dir: Path) -> dict[str, float]:
    """Every currently-tracked file under `fragments_dir`, mapped to its
    newest commit's Unix timestamp -- ONE `git log` pass over the whole
    directory, not one `git log -1 -- <file>` subprocess per fragment.

    Issue #307 found this exact shape (`git log` once per item, serially)
    in `issue_precheck.py` at backlog scale; the same anti-pattern already
    existed here in miniature. It costs nothing observable with the handful
    of fragments a normal merge folds, which is why nobody noticed, but "the
    backlog only grows" applies just as much to a busy multi-agent merge
    day's fragment count, so it is fixed here too rather than left for the
    day it is not a handful.

    `git log`'s own default order is newest-commit-first, so the FIRST time
    a given filename appears in the `--name-only` stream is that file's most
    recent commit; `dict.setdefault` below relies on exactly that to take
    only the first (newest) hit per file.
    """
    try:
        out = subprocess.run(
            ["git", "log", "--format=\x01%ct", "--name-only", "--", str(fragments_dir)],
            cwd=root, capture_output=True, text=True, timeout=30,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return {}

    times: dict[str, float] = {}
    current_ts: float | None = None
    for line in out.splitlines():
        if line.startswith("\x01"):
            try:
                current_ts = float(line[1:])
            except ValueError:
                current_ts = None
            continue
        name = line.strip()
        if not name or current_ts is None:
            continue
        times.setdefault(name, current_ts)
    return times


def _sort_key(path: Path, root: Path, bulk_times: dict[str, float] | None = None) -> float:
    """Newest-first ordering: git last-commit time (from the bulk pass when
    given one), else file mtime -- the fallback a fragment that was just
    written and never committed always needs, bulk pass or not."""
    if bulk_times is not None:
        rel = path.resolve().relative_to(root.resolve()).as_posix()
        ts = bulk_times.get(rel)
        if ts is not None:
            return ts
        return path.stat().st_mtime

    # No bulk pass supplied: the single-file fallback, kept for any direct
    # caller (this module's own tests exercise both paths deliberately).
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
    days, on the project this script came from: four fragments written as
    Markdown headings ("## ..." / "### ...") instead of "- " bullets made
    every one of them fail this check, so `compile_sprint_log.py` correctly
    exited non-zero and folded nothing — but the only way anyone found out
    was a human running the script by hand and reading stderr, which nobody
    did for two days.
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

    bulk_times = _bulk_commit_times(root, fragments_dir)
    fragments.sort(key=lambda item: _sort_key(item[0], root, bulk_times), reverse=True)

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


def check_fragments(root: Path) -> int:
    """Validate every fragment's shape (the same rule `_validate` already
    enforces at fold time) without touching `sprint_log.md` at all -- no
    fold, no delete, no requirement that the "## Recent changes" heading
    even exist yet. This is what CI runs on every PR (`ci-docs.yml`'s
    "Changelog fragment shape" step, `python scripts/compile_sprint_log.py
    --check`): a branch's own malformed fragment is caught on that PR,
    before it ever reaches the merger who runs the real fold.
    `docs/changelog.d/README.md` documents this flag by name; keep them in
    sync if the flag is ever renamed.
    """
    fragments_dir = root / Path(*FRAGMENTS_RELATIVE)
    files = _fragment_files(fragments_dir)
    if not files:
        print("No changelog fragments to check.")
        return 0

    errors = []
    for path in files:
        try:
            _validate(path)
        except ValueError as exc:
            errors.append(str(exc))

    if errors:
        for err in errors:
            print(f"ERROR: {err}", file=sys.stderr)
        print(f"{len(errors)} malformed fragment(s) in {fragments_dir}.", file=sys.stderr)
        return 1
    print(f"compile_sprint_log --check: {len(files)} fragment(s), all well-formed.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=_repo_root(),
                        help="repo root (default: this script's parent repo)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--dry-run", action="store_true",
                       help="report what folding would do without writing")
    mode.add_argument("--check", action="store_true",
                       help="validate every fragment's shape only; never folds, never touches "
                            "sprint_log.md (what CI runs on every PR)")
    args = parser.parse_args()
    root = args.root.resolve()
    if args.check:
        return check_fragments(root)
    return compile_fragments(root, dry_run=args.dry_run)


if __name__ == "__main__":
    sys.exit(main())
