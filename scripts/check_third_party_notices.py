"""scripts/check_third_party_notices.py -- the shipped-dependency notices file is current.

    python scripts/check_third_party_notices.py
    python scripts/check_third_party_notices.py --self-test

Hard Rule 21 (`CLAUDE.md`, `docs/hard_rules.md`): MIT/Apache/BSD only in
anything that ships, and `THIRD-PARTY-NOTICES.md` must list every shipped
dependency. Issue #34 (packaging: THIRD-PARTY-NOTICES generator and
staleness check) is the full shape of this: a generator
(`gen_third_party_notices.py`, not yet written) builds the file's tables
from `dotnet list package --include-transitive` and `uv pip list`, and this
script's real job -- once that generator exists -- is comparing the
committed file against a freshly generated one and failing when they
differ, catching a dependency added without regenerating it.

WHAT THIS SCRIPT DOES TODAY
-----------------------------
`THIRD-PARTY-NOTICES.md` does not exist yet, and neither does the generator
that would produce one to diff against. Failing CI over a staleness check
this repo cannot yet perform would be theater, not a guard, so this script
prints a notice naming issue #34 and passes. It becomes a real staleness
check the day both `THIRD-PARTY-NOTICES.md` and
`scripts/gen_third_party_notices.py` exist: at that point it regenerates
the notices into memory and fails if the committed file differs, byte for
byte after normalising line endings.

`worker/pyproject.toml`'s own optional `[genes]` extra pulling in a GPLv3
dependency (`pyrodigal`) is already a real, filed concern -- issue #301, a
DECISION, not resolved by this script. A generic license-family scan
belongs to that generator once it exists (it is the natural place to flag a
disallowed license per-dependency, per issue #34's own "A GPL/LGPL/AGPL
licence fails the check" acceptance line); this script does not attempt a
parallel, narrower version of that scan today, to avoid gating CI on an
open owner DECISION with a check nobody asked this script to be.

Exit codes: 0 clean (including the pre-#34 notice-and-pass state, and the
real staleness check once both files exist), 1 the notices file is stale
against a real regeneration, 2 bad usage.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

NOTICES_RELATIVE = "THIRD-PARTY-NOTICES.md"
GENERATOR_RELATIVE = "scripts/gen_third_party_notices.py"
NOTICES_ISSUE = 34


def check(root: Path) -> tuple[list[str], list[str]]:
    """Return (problems, notices)."""
    notices_path = root / NOTICES_RELATIVE
    generator_path = root / GENERATOR_RELATIVE

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

    # Both exist: the real check is "regenerate, diff". Deliberately not
    # implemented as a bare `import` + function call here: the generator's
    # own CLI contract (how it is invoked, whether it writes a file or
    # prints to stdout) is issue #34's to design, not this script's to
    # guess at ahead of time. Once #34 lands, this branch is where the
    # actual `subprocess.run([sys.executable, generator_path, "--stdout"])`
    # (or equivalent) and the byte-for-byte comparison belongs.
    return ([
        f"{NOTICES_RELATIVE} and {GENERATOR_RELATIVE} both exist, but this script's "
        "regenerate-and-diff step is not implemented yet -- update check_third_party_notices.py "
        f"to actually call the generator now that issue #{NOTICES_ISSUE} has landed enough "
        "of its own scope for that to be possible."
    ], [])


# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------

def self_test() -> bool:
    import tempfile

    ok = True

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        problems, notices = check(root)
        if problems or not any(f"#{NOTICES_ISSUE}" in n for n in notices):
            print(f"FAIL: expected a clean pass with an issue #{NOTICES_ISSUE} notice when the "
                  f"notices file is absent, got problems={problems} notices={notices}")
            ok = False
        else:
            print(f"ok    passes with a notice naming #{NOTICES_ISSUE} when {NOTICES_RELATIVE} is absent")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / NOTICES_RELATIVE).write_text("# Third-party notices\n\n(none yet)\n", encoding="utf-8")
        problems, notices = check(root)
        if problems or not any(f"#{NOTICES_ISSUE}" in n for n in notices):
            print(f"FAIL: expected a clean pass with a notice when the generator is missing, "
                  f"got problems={problems} notices={notices}")
            ok = False
        else:
            print(f"ok    passes with a notice when {NOTICES_RELATIVE} exists but the generator does not")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / NOTICES_RELATIVE).write_text("# Third-party notices\n\n(none yet)\n", encoding="utf-8")
        (root / "scripts").mkdir(parents=True, exist_ok=True)
        (root / GENERATOR_RELATIVE).write_text("# placeholder\n", encoding="utf-8")
        problems, _notices = check(root)
        if not problems:
            print("FAIL: expected this script to say its own diff step is unimplemented, "
                  "once both files exist, rather than silently pass")
            ok = False
        else:
            print("ok    once both files exist, this script says its own diff step still needs implementing "
                  "(never silently claims a real staleness check it cannot yet perform)")

    print(f"\n{'PASS' if ok else 'FAIL'}: check_third_party_notices self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Check that THIRD-PARTY-NOTICES.md is current (issue #34's staleness check).",
    )
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: current directory)")
    ap.add_argument("--self-test", action="store_true", help="run against synthetic fixtures and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    problems, notices = check(args.root.resolve())
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
