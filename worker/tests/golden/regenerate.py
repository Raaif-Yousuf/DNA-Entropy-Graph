"""Rewrite the golden files (issue #161). A golden change is a deliberate, reviewed event:

    worker\\.venv\\Scripts\\python.exe worker\\tests\\golden\\regenerate.py --justification "why"

``--justification`` is required (at least 15 characters). It is appended, with the case list, to
``JUSTIFICATIONS.txt`` next to the golden files, so ``git log -p`` shows WHY the bytes changed.
Run it from a clean checkout of the code you intend to ship, then read the diff of the golden
files before committing: a number-formatting change should touch every numeric file, a
coordinate change one writer's file.
"""

from __future__ import annotations

import argparse
import shutil
import sys
import tempfile
from datetime import date
from pathlib import Path

import cases

GOLDEN = Path(__file__).resolve().parent / "files"
MIN_JUSTIFICATION = 15


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--justification", default="", help="Why the golden output is changing (required).")
    args = parser.parse_args(argv)
    why = args.justification.strip()
    if len(why) < MIN_JUSTIFICATION:
        print(
            f"ERROR: --justification is required (at least {MIN_JUSTIFICATION} characters): say why "
            "the golden output is changing.",
            file=sys.stderr,
        )
        return 1

    GOLDEN.mkdir(exist_ok=True)
    for case in cases.CASES:
        with tempfile.TemporaryDirectory() as tmp:
            produced = cases.build(case, Path(tmp) / "out")
        target = GOLDEN / case
        if target.exists():
            shutil.rmtree(target)
        target.mkdir(parents=True)
        for name, data in produced.items():
            (target / name).write_bytes(data)
        print(f"OK: {case}: {len(produced)} file(s)")

    with open(GOLDEN.parent / "JUSTIFICATIONS.txt", "a", encoding="utf-8", newline="\n") as fh:
        fh.write(f"{date.today().isoformat()}: {why}\n")
    print("OK: golden files rewritten; review the diff before committing.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
