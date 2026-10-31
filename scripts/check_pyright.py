"""Fail if static type errors grow: the pyright ratchet for the Python side.

WHY A RATCHET (AND WHERE IT STANDS TODAY)
-----------------------------------------
History: the first run (MEASURED 2026-10-28, pyright 1.1.407, typeCheckingMode "basic") found
345 errors in 238 files. The shipped code (`worker/src`) was cleaned to zero in the commit that
added this gate (it found a few real defects, see docs/dev_commands.md); the test suites and
`scripts/tests` carried the rest as debt, recorded per file in `scripts/pyright_baseline.json`.
That debt is paid (#877): the baseline's `files` object is empty, so the gate is zero errors
everywhere. The ratchet machinery stays, and any entry in the baseline may only go DOWN:

- NEW    a file has more errors than its baseline (a file not listed is held to 0). Fix the
         error; do not raise the baseline.
- STALE  a file has FEWER errors than its baseline, or its entry names a file that is gone.
         Bank the progress with `--lower`, or the slack lets a new error in unseen (the
         "allowlist that expires itself" pattern: the gate fires on schedule, nobody watches).
- ZERO   `worker/src` may not appear in the baseline at all. The shipped worker is at 0.

`--lower` rewrites the baseline to min(current, baseline) per file and REFUSES when any count
grew or a new file appeared, so it can never be the way to bank a new error.

HOW PYRIGHT IS OBTAINED
-----------------------
`uvx pyright==<PYRIGHT_VERSION>` (the PyPI wrapper of Microsoft's MIT-licensed pyright), pinned
below. `uv` is already a hard dependency of this repo (Hard Rule 20, ci-worker.yml), a pinned
version means the count cannot move overnight on an upstream release, and it needs no Node on
the laptop. Bump the pin deliberately, in its own commit, lowering or raising nothing else.

Pyright is pointed at the interpreter running THIS script (`--pythonpath sys.executable`), so
a git worktree with no `worker\\.venv` of its own still resolves numpy: run it with the worker
venv's python (premerge does). `pyrightconfig.json` pins `pythonPlatform` to "All" so the
count is the same on the Windows laptop and the Linux CI runner (heavy.py uses fcntl AND
msvcrt; a pinned single platform reports one of them on each machine).

Usage:
    worker\\.venv\\Scripts\\python.exe scripts\\check_pyright.py            # the gate
    worker\\.venv\\Scripts\\python.exe scripts\\check_pyright.py --lower    # bank progress
    ... --report saved.json    # judge a saved `pyright --outputjson` instead of running it
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

PYRIGHT_VERSION = "1.1.407"

REPO_ROOT = Path(__file__).resolve().parents[1]
BASELINE_PATH = Path(__file__).resolve().parent / "pyright_baseline.json"

# Shipped code: held at zero, never baselined.
ZERO_PREFIXES = ("worker/src/",)

# pyright_baseline.json's own `include` roots hold ~240 files; a run that saw only a handful
# used the wrong config (or no config) and would report a clean tree for the wrong reason.
MIN_FILES_ANALYZED = 50

TIMEOUT_SECONDS = 600.0


class PyrightGateError(Exception):
    """The gate could not judge (bad baseline, bad report, pyright missing): exit 2, never a pass."""


def _rel(file: str, root: Path) -> str:
    path = Path(file)
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def errors_by_file(report: dict, root: Path) -> dict[str, int]:
    """``{repo-relative posix path: error count}`` from a ``pyright --outputjson`` report.

    Only ``severity == "error"`` counts: basic mode reports no warnings today, and a warning is
    not a failure."""
    if "generalDiagnostics" not in report:
        raise PyrightGateError("the pyright report has no 'generalDiagnostics' list; it is not a --outputjson report")
    counts: dict[str, int] = {}
    for diag in report["generalDiagnostics"]:
        if diag.get("severity") != "error":
            continue
        rel = _rel(diag["file"], root)
        counts[rel] = counts.get(rel, 0) + 1
    return counts


def load_baseline(path: Path) -> dict[str, int]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except OSError as exc:
        raise PyrightGateError(f"cannot read the pyright baseline {path} ({exc}). Restore it from git.") from exc
    except json.JSONDecodeError as exc:
        raise PyrightGateError(f"the pyright baseline {path} is not valid JSON ({exc}). Restore it from git.") from exc
    files = data.get("files") if isinstance(data, dict) else None
    if not isinstance(files, dict):
        raise PyrightGateError(f"the pyright baseline {path} has no 'files' object. Restore it from git.")
    for name, count in files.items():
        if isinstance(count, bool) or not isinstance(count, int) or count < 1:
            raise PyrightGateError(
                f"baseline entry {name} is {count!r}; a count must be a positive whole number "
                "(a file at zero has no entry). Remove or fix the entry."
            )
    return dict(files)


def check_baseline_version(path: Path) -> None:
    """The baseline records the pyright version that produced its counts; a different pin
    means the counts may not mean the same thing (new rules, changed inference)."""
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise PyrightGateError(f"cannot read the pyright baseline {path} ({exc}). Restore it from git.") from exc
    recorded = data.get("pyright") if isinstance(data, dict) else None
    if recorded != PYRIGHT_VERSION:
        raise PyrightGateError(
            f"the baseline {path.name} was made with pyright {recorded!r} but PYRIGHT_VERSION is {PYRIGHT_VERSION!r}. "
            "After bumping the pin, run the gate on the new version, confirm no count rose, then set the baseline's "
            '"pyright" field to the new version (and lower counts with --lower) in the same commit.'
        )


def compare(current: dict[str, int], baseline: dict[str, int], root: Path) -> list[str]:
    """Every way the current counts disagree with the baseline, one line each (empty = pass)."""
    problems: list[str] = []
    for name in sorted(baseline):
        if name.startswith(ZERO_PREFIXES):
            problems.append(
                f"ZERO: {name} is in the baseline, but {', '.join(ZERO_PREFIXES)} must stay at 0 errors. Remove the entry and fix the errors."
            )
    for name, count in sorted(current.items()):
        allowed = 0 if name.startswith(ZERO_PREFIXES) else baseline.get(name, 0)
        if count > allowed:
            where = "held to 0 (not in the baseline)" if allowed == 0 else f"baseline allows {allowed}"
            problems.append(
                f"NEW: {name} has {count} pyright errors, {where}. "
                f"Fix them (run: uvx pyright=={PYRIGHT_VERSION} {name}); do not raise the baseline."
            )
    for name, allowed in sorted(baseline.items()):
        if name.startswith(ZERO_PREFIXES):
            continue
        count = current.get(name, 0)
        if not (root / name).exists():
            problems.append(
                f"STALE: baseline entry {name} ({allowed}) names a file that no longer exists. Run: scripts/check_pyright.py --lower"
            )
        elif count < allowed:
            problems.append(
                f"STALE: {name} is down to {count} pyright errors but the baseline still allows {allowed}. "
                "Bank the progress: scripts/check_pyright.py --lower"
            )
    return problems


def lowered_baseline(current: dict[str, int], baseline: dict[str, int]) -> dict[str, int]:
    """The baseline with every count lowered to the current one and fixed files dropped.

    Refuses when anything grew or a file outside the baseline has errors: ``--lower`` is for
    banking progress, never for absorbing a new error."""
    grew = sorted(n for n, c in current.items() if c > baseline.get(n, 0))
    if grew:
        raise PyrightGateError("refusing to lower the baseline: errors grew or appeared in " + ", ".join(grew) + ". Fix those first.")
    return {n: min(c, current.get(n, 0)) for n, c in sorted(baseline.items()) if current.get(n, 0) > 0}


def check_report(report: dict, root: Path, baseline: dict[str, int]) -> tuple[dict[str, int], list[str]]:
    analysed = report.get("summary", {}).get("filesAnalyzed", 0)
    if analysed < MIN_FILES_ANALYZED:
        raise PyrightGateError(
            f"pyright analysed only {analysed} files (expected at least {MIN_FILES_ANALYZED}); "
            "pyrightconfig.json was not found or its `include` is wrong. Run from the repo root."
        )
    current = errors_by_file(report, root)
    return current, compare(current, baseline, root)


def run_pyright(root: Path, python: str) -> dict:
    """Run the pinned pyright over ``root`` (its own pyrightconfig.json) and return its JSON report."""
    uvx = shutil.which("uvx")
    if uvx is None:
        raise PyrightGateError("uvx is not on PATH. Install uv (https://docs.astral.sh/uv/) and rerun.")
    env = dict(os.environ, PYRIGHT_PYTHON_IGNORE_WARNINGS="1", PYTHONIOENCODING="utf-8")
    argv = [uvx, f"pyright=={PYRIGHT_VERSION}", "--outputjson", "--pythonpath", python]
    try:
        proc = subprocess.run(  # noqa: S603 - fixed argv, no shell
            argv, cwd=root, capture_output=True, text=True, encoding="utf-8", env=env, timeout=TIMEOUT_SECONDS
        )
    except subprocess.TimeoutExpired as exc:
        raise PyrightGateError(f"pyright did not finish in {TIMEOUT_SECONDS:.0f}s. Rerun; if it repeats, file an issue.") from exc
    try:
        return json.loads(proc.stdout)
    except json.JSONDecodeError as exc:
        tail = (proc.stderr or proc.stdout).strip().splitlines()[-5:]
        raise PyrightGateError(
            "pyright did not produce a JSON report (exit "
            f"{proc.returncode}): {' | '.join(tail)!r}. Check that `uvx pyright=={PYRIGHT_VERSION} --version` works."
        ) from exc


def _write_baseline(path: Path, files: dict[str, int]) -> None:
    doc = {
        "_about": (
            "Per-file pyright error debt. May only go down: scripts/check_pyright.py --lower. "
            "worker/src is at 0 and may not be listed. See scripts/check_pyright.py."
        ),
        "pyright": PYRIGHT_VERSION,
        "files": files,
    }
    path.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8", newline="\n")


def self_test() -> int:
    """Prove the gate can fail: plant each violation and demand the specific verdict."""
    import tempfile

    failures = 0

    def expect(label: str, condition: bool) -> None:
        nonlocal failures
        if not condition:
            failures += 1
            print(f"  FAIL: {label}")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        for rel in ("scripts/a.py", "scripts/b.py", "worker/src/dna_entropy/x.py"):
            (root / rel).parent.mkdir(parents=True, exist_ok=True)
            (root / rel).write_text("x = 1\n", encoding="utf-8", newline="\n")

        def kinds(current: dict[str, int], baseline: dict[str, int]) -> list[str]:
            return sorted(p.split(":")[0] for p in compare(current, baseline, root))

        expect("a count at its baseline passes", kinds({"scripts/a.py": 2}, {"scripts/a.py": 2}) == [])
        expect("a count over its baseline is NEW", kinds({"scripts/a.py": 3}, {"scripts/a.py": 2}) == ["NEW"])
        expect("an unlisted file with an error is NEW", kinds({"scripts/b.py": 1}, {}) == ["NEW"])
        expect("a lowered count with a stale baseline is STALE", kinds({"scripts/a.py": 1}, {"scripts/a.py": 2}) == ["STALE"])
        expect("an entry for a deleted file is STALE", kinds({}, {"scripts/gone.py": 2}) == ["STALE"])
        expect(
            "worker/src in the baseline is ZERO",
            "ZERO" in kinds({"worker/src/dna_entropy/x.py": 1}, {"worker/src/dna_entropy/x.py": 1}),
        )
        expect("a worker/src error is NEW", kinds({"worker/src/dna_entropy/x.py": 1}, {}) == ["NEW"])
        try:
            lowered_baseline({"scripts/a.py": 3}, {"scripts/a.py": 2})
            expect("--lower refuses growth", False)
        except PyrightGateError:
            pass
        expect(
            "--lower lowers and drops fixed files",
            lowered_baseline({"scripts/a.py": 1}, {"scripts/a.py": 2, "scripts/b.py": 1}) == {"scripts/a.py": 1},
        )
        try:
            check_report({"generalDiagnostics": [], "summary": {"filesAnalyzed": 1}}, root, {})
            expect("a run that analysed 1 file is refused", False)
        except PyrightGateError:
            pass
        bad = root / "baseline.json"
        bad.write_text(json.dumps({"pyright": "0.0.0", "files": {}}), encoding="utf-8", newline="\n")
        try:
            check_baseline_version(bad)
            expect("a baseline from another pyright version is refused", False)
        except PyrightGateError:
            pass

    if failures:
        print(f"check_pyright: self-test FAILED ({failures} case(s)).")
        return 1
    print("check_pyright: self-test passed (11 cases).")
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=(__doc__ or "").split("\n")[0])
    ap.add_argument("--root", type=Path, default=REPO_ROOT, help="repo root (default: the repo this script lives in)")
    ap.add_argument("--baseline", type=Path, default=BASELINE_PATH, help="baseline file")
    ap.add_argument("--report", type=Path, help="judge this saved `pyright --outputjson` file instead of running pyright")
    ap.add_argument("--lower", action="store_true", help="lower the baseline to the current counts (never raises it)")
    ap.add_argument("--self-test", action="store_true", help="prove the gate can fail (no pyright needed)")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()

    try:
        check_baseline_version(args.baseline)
        baseline = load_baseline(args.baseline)
        if args.report is not None:
            report = json.loads(args.report.read_text(encoding="utf-8"))
        else:
            try:
                import numpy  # noqa: F401 - probe only: the interpreter pyright reads must have the worker deps
            except ImportError:
                raise PyrightGateError(
                    "this interpreter has no numpy, so pyright would report hundreds of false errors. "
                    "Run with worker\\.venv\\Scripts\\python.exe (create it: uv venv --python 3.12; "
                    "uv pip install -e worker[dev])."
                ) from None
            report = run_pyright(args.root, sys.executable)
        current, problems = check_report(report, args.root, baseline)

        if args.lower:
            _write_baseline(args.baseline, lowered_baseline(current, baseline))
            print(f"OK: baseline lowered ({args.baseline.name}); {sum(current.values())} errors remain as debt.")
            return 0
    except PyrightGateError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    if problems:
        for problem in problems:
            print(problem, file=sys.stderr)
        print(f"FAIL: {len(problems)} pyright ratchet problem(s).", file=sys.stderr)
        return 1
    print(f"OK: pyright {PYRIGHT_VERSION}: {sum(current.values())} errors in {len(current)} files, all within the baseline; worker/src is at 0.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
