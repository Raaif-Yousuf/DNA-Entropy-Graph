"""scripts/check_citation_cff.py -- CITATION.cff has the keys GitHub needs (#168).

    python scripts/check_citation_cff.py
    python scripts/check_citation_cff.py --file <path>

GitHub shows the "Cite this repository" button only when CITATION.cff parses. A broken file
fails silently there, so this checks the shape in CI. It reads text only (no PyYAML) because
the ci-docs `checks` job runs every check_*.py with a bare Python.

Required: cff-version 1.2.0, message, title, type, authors with family-names, and a licence.
A missing file FAILS: a rule that cannot fail because its subject is absent is a vacuous pass.

Exit codes: 0 clean, 1 a rule broken.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REQUIRED_KEYS = ("cff-version", "message", "title", "type", "authors", "license")
ROOT = Path(__file__).resolve().parent.parent


def check(path: Path) -> list[str]:
    if not path.is_file():
        return [f"{path.name} is missing. Add it at the repo root so GitHub shows 'Cite this repository'"]
    text = path.read_text(encoding="utf-8")
    problems: list[str] = []
    if "\t" in text:
        problems.append("CITATION.cff contains a tab; YAML forbids tab indentation")
    for key in REQUIRED_KEYS:
        if not re.search(rf"^{re.escape(key)}:", text, re.MULTILINE):
            problems.append(f"required top-level key '{key}' is missing")
    m = re.search(r"^cff-version:\s*['\"]?([^'\"\s]+)", text, re.MULTILINE)
    if m and m.group(1) != "1.2.0":
        problems.append(f"cff-version is {m.group(1)}; this repo uses 1.2.0")
    if re.search(r"^authors:", text, re.MULTILINE) and not re.search(r"^\s+-\s+family-names:|^\s+family-names:", text, re.MULTILINE):
        problems.append("authors has no entry with family-names (use family-names and given-names)")
    return problems


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--file", type=Path, default=ROOT / "CITATION.cff")
    args = ap.parse_args(argv)
    problems = check(args.file)
    for p in problems:
        print(f"ERROR: {p}")
    if problems:
        return 1
    print("check_citation_cff: clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
