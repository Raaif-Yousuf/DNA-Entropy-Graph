"""scripts/check_third_party_notices.py -- the shipped-dependency notices file is current.

    python scripts/check_third_party_notices.py
    python scripts/check_third_party_notices.py --self-test

Hard Rule 21 (`CLAUDE.md`, `docs/hard_rules.md`): MIT/Apache/BSD only in anything that
ships, and `THIRD-PARTY-NOTICES.md` must list every shipped dependency. Issue #34 built
the generator (`scripts/gen_third_party_notices.py`) this script calls; this script's own
job is narrower and mechanical: regenerate the notices into memory (via the generator's
`--stdout` mode) and fail if the committed file differs, byte for byte after normalising
line endings -- catching a dependency added (or a licence that changed) without
regenerating the file in the same commit (Hard Rule 16).

THE FALSE-PASS SHAPE THIS SCRIPT USED TO BE (issue #34, fixed this session)
------------------------------------------------------------------------------
Before this session, `THIRD-PARTY-NOTICES.md` and the generator did not exist, and this
script's own `check()` printed a notice and returned a clean pass regardless -- a
documented no-op that had been sitting in the guard set reporting "clean" every run
without ever performing a real check (the same false-pass shape `scripts/
check_guard_drift.py` exists to catch). Both files exist now; the notice-and-pass branches
below are KEPT, not deleted, because a future accidental deletion of either file must still
be caught as a real failure, not silently degrade back into the old no-op. Only a truly
absent notices file with no generator to build one still gets a soft notice; every other
combination is now a hard check.

THE GENERATOR ITSELF CAN ALSO FAIL, SEPARATELY FROM STALENESS
-----------------------------------------------------------------
`gen_third_party_notices.py` exits 1 when a SHIPPED dependency's licence is denied
(GPL/LGPL/AGPL with no recorded carve-out) or unrecognised, even if its own output is
byte-identical to the committed file -- an honest, up-to-date notices file can still
describe a genuine, unresolved Hard Rule 21 problem (see that script's own docstring: this
is exactly evo2's real state as of this session, no known-good licence citation to fall
back on). This script propagates that failure as its own distinct problem, never folded
into "stale", so the two causes are never confused in CI output.

Exit codes: 0 clean, 1 stale and/or an unresolved licence problem, 2 bad usage.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

NOTICES_RELATIVE = "THIRD-PARTY-NOTICES.md"
GENERATOR_RELATIVE = "scripts/gen_third_party_notices.py"
NOTICES_ISSUE = 34


def _normalize(text: str) -> str:
    return text.replace("\r\n", "\n").replace("\r", "\n")


def check(root: Path, *, app_root: Path | None = None, worker_root: Path | None = None) -> tuple[list[str], list[str]]:
    """Return (problems, notices)."""
    notices_path = root / NOTICES_RELATIVE
    generator_path = root / GENERATOR_RELATIVE
    app_root = app_root if app_root is not None else root / "app"
    worker_root = worker_root if worker_root is not None else root / "worker"

    if not notices_path.is_file():
        return ([], [
            f"{NOTICES_RELATIVE} does not exist yet (issue #{NOTICES_ISSUE}: packaging: "
            "THIRD-PARTY-NOTICES generator and staleness check) -- nothing to check "
            "freshness against yet. This will start enforcing once both that file and "
            f"{GENERATOR_RELATIVE} exist."
        ])

    if not generator_path.is_file():
        return ([], [
            f"{NOTICES_RELATIVE} exists but {GENERATOR_RELATIVE} does not (issue #{NOTICES_ISSUE}) -- "
            "cannot regenerate to compare against, so the committed file is trusted as-is for now."
        ])

    proc = subprocess.run(
        [sys.executable, str(generator_path), "--stdout",
         "--app-root", str(app_root), "--worker-root", str(worker_root)],
        capture_output=True, text=True, timeout=180,
    )
    # A generator failure (an unresolved licence problem, e.g. evo2's -- see the module
    # docstring) is reported EVEN IF the file below turns out to be byte-identical: an
    # honest, up-to-date notices file can still describe a real, open Hard Rule 21 problem.
    problems: list[str] = []
    if proc.returncode not in (0, 1):
        # 2+ means the generator itself could not even run (dotnet/venv missing, bad args)
        # -- a usage/environment problem, not a licence finding either way.
        return ([f"{GENERATOR_RELATIVE} could not run (exit {proc.returncode}): {proc.stdout}\n{proc.stderr}"], [])
    if proc.returncode == 1:
        problems.append(
            f"{GENERATOR_RELATIVE} reports an unresolved licence problem (Hard Rule 21) -- "
            f"see its stderr:\n{proc.stderr.strip()}"
        )

    committed = _normalize(notices_path.read_text(encoding="utf-8"))
    fresh = _normalize(proc.stdout)
    if committed != fresh:
        problems.append(
            f"{NOTICES_RELATIVE} is stale: it does not match a fresh run of {GENERATOR_RELATIVE}. "
            f"Regenerate with 'python {GENERATOR_RELATIVE} --out {NOTICES_RELATIVE}' and commit the "
            "result in the same commit as the dependency change (Hard Rule 16)."
        )

    return (problems, [])


# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------

_FAKE_GENERATOR_OK = (
    "import sys\n"
    "print('# Third-party notices\\n\\n(fake, matches)\\n', end='')\n"
    "sys.exit(0)\n"
)
_FAKE_GENERATOR_LICENSE_PROBLEM = (
    "import sys\n"
    "print('# Third-party notices\\n\\n(fake, matches, but a licence is unresolved)\\n', end='')\n"
    "print('ERROR: some-package 1.0 has an unresolved licence problem', file=sys.stderr)\n"
    "sys.exit(1)\n"
)
_FAKE_GENERATOR_CRASHES = (
    "import sys\n"
    "print('cannot even run: dotnet not found', file=sys.stderr)\n"
    "sys.exit(2)\n"
)


def _write_fake_generator(root: Path, source: str) -> None:
    (root / "scripts").mkdir(parents=True, exist_ok=True)
    (root / GENERATOR_RELATIVE).write_text(source, encoding="utf-8")


def self_test() -> bool:
    import tempfile

    ok = True

    def report(label: str, condition: bool, detail: str = "") -> None:
        nonlocal ok
        print(f"{'ok' if condition else 'FAIL'}    {label}{(': ' + detail) if detail and not condition else ''}")
        ok = ok and condition

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        problems, notices = check(root)
        report(
            f"passes with a notice naming #{NOTICES_ISSUE} when {NOTICES_RELATIVE} is absent",
            not problems and any(f"#{NOTICES_ISSUE}" in n for n in notices),
            f"problems={problems} notices={notices}",
        )

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / NOTICES_RELATIVE).write_text("# Third-party notices\n\n(none yet)\n", encoding="utf-8")
        problems, notices = check(root)
        report(
            f"passes with a notice when {NOTICES_RELATIVE} exists but the generator does not",
            not problems and bool(notices),
            f"problems={problems} notices={notices}",
        )

    # Both exist, fake generator's stdout matches the committed file byte-for-byte -> clean.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / NOTICES_RELATIVE).write_text("# Third-party notices\n\n(fake, matches)\n", encoding="utf-8")
        _write_fake_generator(root, _FAKE_GENERATOR_OK)
        problems, _notices = check(root)
        report("a notices file matching a fresh regeneration is clean (no problems)", not problems, f"problems={problems}")

    # Both exist, fake generator's stdout DIFFERS from the committed file -> stale, MANDATORY
    # arm: this is the actual regenerate-and-diff step issue #34 asked for; a generator that
    # silently agrees with whatever is committed would never catch a real staleness case.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / NOTICES_RELATIVE).write_text("# Third-party notices\n\n(this is stale)\n", encoding="utf-8")
        _write_fake_generator(root, _FAKE_GENERATOR_OK)
        problems, _notices = check(root)
        report(
            "a notices file that disagrees with a fresh regeneration is reported STALE",
            bool(problems) and any("stale" in p for p in problems),
            f"problems={problems}",
        )

    # Line-ending normalisation: CRLF-committed file must still match LF generator output.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / NOTICES_RELATIVE).write_bytes(b"# Third-party notices\r\n\r\n(fake, matches)\r\n")
        _write_fake_generator(root, _FAKE_GENERATOR_OK)
        problems, _notices = check(root)
        report("CRLF vs LF alone does not count as staleness", not problems, f"problems={problems}")

    # The generator itself reports an unresolved licence problem -> a distinct failure,
    # even though its stdout is otherwise byte-identical to the committed file.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / NOTICES_RELATIVE).write_text("# Third-party notices\n\n(fake, matches, but a licence is unresolved)\n", encoding="utf-8")
        _write_fake_generator(root, _FAKE_GENERATOR_LICENSE_PROBLEM)
        problems, _notices = check(root)
        report(
            "an unresolved licence problem fails even when the file is byte-identical (not folded into 'stale')",
            bool(problems) and any("unresolved licence problem" in p for p in problems) and not any("stale" in p for p in problems),
            f"problems={problems}",
        )

    # The generator cannot even run (e.g. dotnet missing) -> a usage/environment failure,
    # distinct from both staleness and a licence finding.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / NOTICES_RELATIVE).write_text("# anything\n", encoding="utf-8")
        _write_fake_generator(root, _FAKE_GENERATOR_CRASHES)
        problems, _notices = check(root)
        report(
            "a generator that cannot run at all is reported distinctly, not as staleness",
            bool(problems) and any("could not run" in p for p in problems),
            f"problems={problems}",
        )

    print(f"\n{'PASS' if ok else 'FAIL'}: check_third_party_notices self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Check that THIRD-PARTY-NOTICES.md is current (issue #34's staleness check).",
    )
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: current directory)")
    ap.add_argument("--app-root", type=Path, default=None, help="path to app/ (default: <root>/app)")
    ap.add_argument("--worker-root", type=Path, default=None, help="path to worker/ (default: <root>/worker)")
    ap.add_argument("--self-test", action="store_true", help="run against synthetic fixtures and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    problems, notices = check(args.root.resolve(), app_root=args.app_root, worker_root=args.worker_root)
    for n in notices:
        print(f"NOTICE: {n}")
    if problems:
        for p in problems:
            print(f"ERROR: {p}", file=sys.stderr)
        return 1
    print("check_third_party_notices: clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
