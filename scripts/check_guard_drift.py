"""scripts/check_guard_drift.py -- a CI notice-and-pass gate that has started
mattering, and nobody has confirmed it, is a guard that cannot currently fail.

    python scripts/check_guard_drift.py
    python scripts/check_guard_drift.py --self-test

WHY THIS EXISTS (issue #309)
-----------------------------
Several steps in `.github/workflows/ci-worker.yml` and the whole of
`.github/workflows/ci-app.yml` are written as `if [ -f <guarded-file> ]; then
<real check>; else echo "::notice ..."; fi`. That shell-level `if` really does
flip to the enforcing branch the moment the guarded file is committed -- no
flag, no second migration step, exactly like `check_version_lockstep.py`'s own
`app/Directory.Build.props` gate. What it does NOT do is tell anyone it
flipped: the first PR (or push to `main`) that lands the guarded file gets a
real `ruff check`/`shellcheck`/`docker build`/`dotnet build` run for the first
time ever, on content nobody has run it against before, and if that run is
red, the only place that shows up is that one CI log. Nothing re-reads it, and
nothing distinguishes "the guard has been quietly passing for weeks" from
"the guard ran for the very first time in this commit and something is
already wrong under it" -- the exact shape this repo's own
`a-check-that-cannot-fail.md` and
`the-signing-step-silently-no-ops-without-the-secret.md` memory notes already
warn about, for other guards.

This script is a second, independent layer that watches the WORKFLOW YAML
itself rather than trusting the shell `if` to be noticed: for each entry in
GUARDS below, it checks whether the guarded file now exists in the committed
tree (`git cat-file -e HEAD:<path>`, not merely the working tree, because a
guard is only really "landed" once it is in the tree CI itself checks out)
while the workflow file that gates it still contains the exact notice-mode
`if [ -f ... ]` marker. Both true at once means: the guard has almost
certainly started enforcing for real, and nobody has come back to either (a)
confirm a real CI run exercised the enforcing branch cleanly, or (b) simplify
the workflow step so a FUTURE accidental deletion of the guarded file fails
loudly instead of quietly reverting to notice-and-pass. That combination is
what this script calls "drift", and it is the only thing it looks for.

WHAT IS DELIBERATELY NOT IN SCOPE
-----------------------------------
This script reads workflow YAML as text (a substring search for the marker),
never edits it -- wiring GUARDS's findings into an actual scheduled workflow,
or hardening `.github/workflows/*.yml` to drop a `if [ -f ... ]` wrapper once
its file is confirmed permanent, is `.github/workflows/**`, outside
`scripts/**`, and is left for whoever owns that path next.

The ruff gate in `ci-worker.yml` ("Is ruff configured?") is deliberately NOT
in GUARDS: it is not a one-time "does this file exist yet" gate but an
ongoing "is `[tool.ruff]` present" content check that stays conditional
forever by design, and as of this session `worker/pyproject.toml` carries a
real `[tool.ruff]` table with all findings already fixed, so lint is
genuinely live -- there is nothing left for a drift check to catch there.

Exit codes: 0 clean (every gated guard is either still legitimately unbuilt,
or already confirmed/hardened past its notice-mode marker), 1 one or more
guards have drifted, 2 bad usage (`--root` does not look like this repo).
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class GuardEntry:
    name: str
    issue: int
    guarded_file: str      # POSIX-style, relative to repo root
    workflow_file: str     # POSIX-style, relative to repo root
    notice_marker: str     # exact substring the workflow's soft-gate still needs to contain


# The pile as of issue #309 (2026-09-19), minus the ruff gate -- see the
# module docstring's "WHAT IS DELIBERATELY NOT IN SCOPE" section for why.
GUARDS: tuple[GuardEntry, ...] = (
    GuardEntry(
        name="manifest schema drift check",
        issue=39,
        guarded_file="scripts/gen_manifest_schema.py",
        workflow_file=".github/workflows/ci-worker.yml",
        notice_marker="if [ -f scripts/gen_manifest_schema.py ]",
    ),
    GuardEntry(
        name="VM startup script shellcheck",
        issue=45,
        guarded_file="worker/vm/startup.sh",
        workflow_file=".github/workflows/ci-worker.yml",
        notice_marker="if [ -f worker/vm/startup.sh ]",
    ),
    GuardEntry(
        name="CPU container smoke test",
        issue=36,
        guarded_file="worker/Dockerfile.cpu",
        workflow_file=".github/workflows/ci-worker.yml",
        notice_marker="if [ -f worker/Dockerfile.cpu ]",
    ),
    GuardEntry(
        name="ci-app.yml build job",
        issue=61,
        guarded_file="app/DnaEntropyGraph.sln",
        workflow_file=".github/workflows/ci-app.yml",
        notice_marker="if [ -f app/DnaEntropyGraph.sln ]",
    ),
)


def _file_is_committed(root: Path, rel: str) -> bool:
    """True only when `rel` exists in HEAD's own tree -- the tree CI actually
    checks out -- not merely the working tree. A file sitting uncommitted on
    someone's disk has not "landed" for this check's purposes."""
    result = subprocess.run(
        ["git", "cat-file", "-e", f"HEAD:{rel}"],
        cwd=root, capture_output=True, timeout=10,
    )
    return result.returncode == 0


def _workflow_still_soft_gates(root: Path, entry: GuardEntry) -> bool | None:
    """True if `entry.notice_marker` is still present in the workflow file's
    committed text, False if it is gone (already hardened past notice mode),
    None if the workflow file itself cannot be read at HEAD -- treated as
    "cannot tell" and excluded from findings rather than guessed at, matching
    every sibling check_*.py's stated preference to fail toward not blocking
    on a guess."""
    result = subprocess.run(
        ["git", "show", f"HEAD:{entry.workflow_file}"],
        cwd=root, capture_output=True, text=True, timeout=10,
    )
    if result.returncode != 0:
        return None
    return entry.notice_marker in result.stdout


def check_drift(root: Path, guards: tuple[GuardEntry, ...] = GUARDS) -> list[GuardEntry]:
    """Entries whose guarded file has landed in the committed tree while the
    workflow that gates it still reads, textually, as notice-and-pass."""
    drifted = []
    for entry in guards:
        if not _file_is_committed(root, entry.guarded_file):
            continue  # still legitimately not built yet -- notice mode is correct
        still_soft = _workflow_still_soft_gates(root, entry)
        if still_soft is None:
            continue  # workflow file unreadable at HEAD; cannot tell, do not guess
        if still_soft:
            drifted.append(entry)
    return drifted


def check(root: Path, guards: tuple[GuardEntry, ...] = GUARDS) -> int:
    drifted = check_drift(root, guards=guards)
    if not drifted:
        print(f"check_guard_drift: clean across {len(guards)} gated guard(s).")
        return 0
    for entry in drifted:
        print(
            f"::warning title=guard drift (issue #{entry.issue})::'{entry.name}': "
            f"{entry.guarded_file} now exists in the committed tree, but "
            f"{entry.workflow_file} still soft-gates on its absence. Confirm a real "
            f"CI run has actually executed the enforcing branch, then simplify the "
            f"workflow step so a future accidental deletion of the file fails loudly "
            f"instead of silently reverting to notice-and-pass.",
            file=sys.stderr,
        )
    print(
        f"{len(drifted)}/{len(guards)} guard(s) have drifted: their file landed and "
        "nothing has confirmed the workflow now enforces it for real.",
        file=sys.stderr,
    )
    return 1


# ---------------------------------------------------------------------------
# Self-test: a real throwaway git repo (tempfile.TemporaryDirectory, never
# this repo's own working tree), with real commits, so `git cat-file -e` and
# `git show` are exercised against real HEAD trees rather than mocked.
# ---------------------------------------------------------------------------

def self_test() -> bool:
    import tempfile

    ok = True

    def check_case(label: str, condition: bool) -> None:
        nonlocal ok
        if condition:
            print(f"ok    {label}")
        else:
            print(f"FAIL  {label}", file=sys.stderr)
            ok = False

    def run(*args, cwd):
        subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)

    def make_repo(case_dir: Path) -> Path:
        repo = case_dir / "repo"
        repo.mkdir(parents=True)
        run("init", "-q", cwd=repo)
        run("config", "user.email", "test@example.com", cwd=repo)
        run("config", "user.name", "Test", cwd=repo)
        return repo

    def commit_all(repo: Path, message: str) -> None:
        run("add", "-A", cwd=repo)
        run("commit", "-q", "-m", message, cwd=repo)

    workflow_soft = (
        "steps:\n"
        "  - name: guarded thing\n"
        "    run: |\n"
        "      if [ -f fixture/guarded.txt ]; then\n"
        "        echo real check\n"
        "      else\n"
        "        echo '::notice title=skipped::fixture/guarded.txt does not exist yet.'\n"
        "      fi\n"
    )
    workflow_hardened = (
        "steps:\n"
        "  - name: guarded thing\n"
        "    run: echo real check unconditionally\n"
    )

    fixture_guard = GuardEntry(
        name="fixture guard",
        issue=0,
        guarded_file="fixture/guarded.txt",
        workflow_file=".github/workflows/fixture.yml",
        notice_marker="if [ -f fixture/guarded.txt ]",
    )

    with tempfile.TemporaryDirectory() as tmp_str:
        tmp = Path(tmp_str)

        # Case 1: guarded file absent, workflow still soft-gated -- correct,
        # unbuilt state, no drift.
        repo = make_repo(tmp / "case1")
        (repo / ".github" / "workflows").mkdir(parents=True)
        (repo / ".github" / "workflows" / "fixture.yml").write_text(workflow_soft, encoding="utf-8")
        commit_all(repo, "workflow only, no guarded file yet")
        drifted = check_drift(repo, guards=(fixture_guard,))
        check_case("file absent + workflow soft-gated: no drift", drifted == [])

        # Case 2: guarded file landed in HEAD, workflow STILL soft-gated --
        # this is the drift this script exists to catch.
        repo = make_repo(tmp / "case2")
        (repo / ".github" / "workflows").mkdir(parents=True)
        (repo / ".github" / "workflows" / "fixture.yml").write_text(workflow_soft, encoding="utf-8")
        (repo / "fixture").mkdir()
        (repo / "fixture" / "guarded.txt").write_text("here now\n", encoding="utf-8")
        commit_all(repo, "guarded file lands, workflow not yet hardened")
        drifted = check_drift(repo, guards=(fixture_guard,))
        check_case("file present + workflow still soft-gated: DRIFT detected",
                   drifted == [fixture_guard])
        check_case("check() exits 1 on drift", check(repo, guards=(fixture_guard,)) == 1)

        # Case 3: guarded file landed AND the workflow has since been
        # hardened past the notice-mode marker -- confirmed, no drift.
        repo = make_repo(tmp / "case3")
        (repo / ".github" / "workflows").mkdir(parents=True)
        (repo / ".github" / "workflows" / "fixture.yml").write_text(workflow_hardened, encoding="utf-8")
        (repo / "fixture").mkdir()
        (repo / "fixture" / "guarded.txt").write_text("here now\n", encoding="utf-8")
        commit_all(repo, "guarded file lands and workflow already hardened")
        drifted = check_drift(repo, guards=(fixture_guard,))
        check_case("file present + workflow hardened: no drift", drifted == [])
        check_case("check() exits 0 once hardened", check(repo, guards=(fixture_guard,)) == 0)

        # Case 4: the workflow file itself does not exist at HEAD -- cannot
        # tell, must not guess, must not crash.
        repo = make_repo(tmp / "case4")
        (repo / "fixture").mkdir()
        (repo / "fixture" / "guarded.txt").write_text("here now\n", encoding="utf-8")
        commit_all(repo, "guarded file lands but no workflow file at all")
        drifted = check_drift(repo, guards=(fixture_guard,))
        check_case("file present + workflow file missing entirely: cannot tell, no crash",
                   drifted == [])

        # The real manifest against the real repo must not crash either,
        # whatever it currently finds -- a smoke check, not an assertion on
        # today's live findings (those change as issues close).
        check_case("the real GUARDS manifest runs clean against this repo without raising",
                   isinstance(check_drift(Path(__file__).resolve().parent.parent), list))

    if ok:
        print(f"\nPASS: check_guard_drift self-test ({len(GUARDS)}-entry live manifest also ran clean)")
    else:
        print("\nFAIL: check_guard_drift self-test", file=sys.stderr)
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: current directory)")
    ap.add_argument("--self-test", action="store_true", help="run against synthetic fixtures and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    root = args.root.resolve()
    if not (root / ".git").exists():
        print(f"error: {root} does not look like this repo (no .git)", file=sys.stderr)
        return 2

    return check(root)


if __name__ == "__main__":
    sys.exit(main())
