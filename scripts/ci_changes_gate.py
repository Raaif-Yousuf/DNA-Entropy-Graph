"""scripts/ci_changes_gate.py -- decide whether ci-app / ci-worker must run on a pull request (#31). Fail-CLOSED.

    python scripts/ci_changes_gate.py --workflow ci-app --event pull_request --base <sha>

Writes `run=true` or `run=false` to $GITHUB_OUTPUT (or --output) and prints it. A skipped job counts as PASSING for
branch protection, so the gate skips only when it is certain: the diff command succeeded AND every changed path matches
a short known-irrelevant rule (and none of the relevant exceptions). Any git error, a missing base, or a non-PR event
runs the jobs (a ::warning says why). The diff uses --no-renames so a move OUT of app/ lists the old path too.

Keep the lists short. Adding to IRRELEVANT is the risky direction: scripts/tests/test_ci_changes_gate.py fails when a
path a test or a workflow step reads (including .editorconfig, global.json and the scripts the jobs run) would be skipped.
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

# Paths under these prefixes are irrelevant to the app/worker jobs, unless a RELEVANT_EXCEPTIONS entry claims them.
IRRELEVANT_PREFIXES = ("docs/", ".claude/", ".agents/", ".codex/", ".serena/")
# Exact root files with no effect on any job (root Markdown is handled by is_root_markdown).
IRRELEVANT_FILES = frozenset({"LICENSE", ".gitignore", ".ignore"})
# Read by tests or by a job step although they live under an irrelevant prefix. Matched exactly (prefix match only where
# the entry ends with '/'), never by substring.
RELEVANT_EXCEPTIONS = ("docs/contract/", "docs/copy_catalog.md")
# Root files that LOOK like docs but are read: THIRD-PARTY-NOTICES.md is regenerated and checked against dotnet/pip state.
RELEVANT_ROOT_MARKDOWN = frozenset({"THIRD-PARTY-NOTICES.md"})
WORKFLOWS = ("ci-app", "ci-worker")


def is_root_markdown(path: str) -> bool:
    return "/" not in path and path.endswith(".md") and path not in RELEVANT_ROOT_MARKDOWN


def is_relevant(workflow: str, path: str) -> bool:
    """True unless `path` is known to be irrelevant to `workflow` (so unknown paths run the jobs)."""
    if path.startswith(".github/workflows/") and path.endswith(".yml"):
        return path == f".github/workflows/{workflow}.yml"
    for exception in RELEVANT_EXCEPTIONS:
        if path == exception or (exception.endswith("/") and path.startswith(exception)):
            return True
    if path in IRRELEVANT_FILES or is_root_markdown(path):
        return False
    return not path.startswith(IRRELEVANT_PREFIXES)


def decide(workflow: str, paths: list[str]) -> bool:
    return any(is_relevant(workflow, p) for p in paths)


def changed_paths(root: Path, base: str) -> list[str]:
    proc = subprocess.run(
        ["git", "diff", "--name-only", "--no-renames", base, "HEAD"],
        cwd=root, capture_output=True, text=True, check=False,
    )
    if proc.returncode != 0:
        raise RuntimeError(f"git diff exited {proc.returncode}: {proc.stderr.strip()[:200]}")
    return [line for line in proc.stdout.splitlines() if line]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--workflow", required=True, choices=WORKFLOWS)
    ap.add_argument("--event", default="pull_request")
    ap.add_argument("--base", default="")
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    ap.add_argument("--output", type=Path, default=None, help="default: $GITHUB_OUTPUT, else stdout only")
    args = ap.parse_args(argv)

    run = True
    if args.event != "pull_request":
        print(f"{args.event}: always runs")
    elif not args.base:
        print("::warning title=changes gate::no base sha, running every job (fail-closed)")
    else:
        try:
            paths = changed_paths(args.root, args.base)
            run = decide(args.workflow, paths)
            relevant = [p for p in paths if is_relevant(args.workflow, p)]
            print(f"{len(paths)} changed path(s), {len(relevant)} relevant to {args.workflow}: {relevant[:10]}")
            if not run:
                print(f"::notice title=skipped::every changed path is irrelevant to {args.workflow}; its jobs report Skipped (passing).")
        except (RuntimeError, OSError) as err:
            print(f"::warning title=changes gate::{err}; running every job (fail-closed)")
            run = True

    line = f"run={'true' if run else 'false'}"
    print(line)
    target = args.output or (Path(os.environ["GITHUB_OUTPUT"]) if os.environ.get("GITHUB_OUTPUT") else None)
    if target is not None:
        with target.open("a", encoding="utf-8", newline="\n") as fh:
            fh.write(line + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
