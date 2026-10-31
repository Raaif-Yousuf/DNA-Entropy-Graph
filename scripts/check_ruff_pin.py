r"""scripts/check_ruff_pin.py -- one ruff version everywhere (#1027).

    python scripts/check_ruff_pin.py
    python scripts/check_ruff_pin.py --self-test
    python scripts/check_ruff_pin.py --root <dir>

ruff is not installed in worker\.venv; it runs through `uvx`, and an unpinned `uvx ruff` takes the
newest release, so a lane, premerge and CI could each format with a different ruff and a new release
could turn `ruff format --check` red at landing. The pin lives in `scripts/ruff.toml`
(`required-version = "==X.Y.Z"`, which ruff itself enforces: a different ruff refuses to run). This
guard fails when any other place disagrees or runs ruff unpinned:

- `worker/pyproject.toml` `[tool.ruff]` `required-version`
- `uvx ruff@X.Y.Z` in `scripts/premerge.py`, `.github/workflows/*.yml`, and the docs' command blocks

Bump the pin by changing every site in one commit (this guard lists the ones it missed).

Exit codes: 0 consistent, 1 a disagreement, an unpinned run, or fewer sites than expected.
"""

from __future__ import annotations

import argparse
import re
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]

CANONICAL = "scripts/ruff.toml"
CONFIGS = (CANONICAL, "worker/pyproject.toml")
SCAN_FILES = ("scripts/premerge.py", "docs/dev_commands.md", "docs/tests.md", "docs/branching_and_prs.md")
SCAN_GLOBS = (".github/workflows/*.yml",)
# A scanner that matches nothing must not report a clean tree: these are floors, not exact counts.
MIN_SITES = {"scripts/premerge.py": 4, ".github/workflows/ci-docs.yml": 2, ".github/workflows/ci-worker.yml": 2}

_REQUIRED = re.compile(r'^\s*required-version\s*=\s*["\']==\s*(\d+\.\d+\.\d+)["\']', re.MULTILINE)
_PINNED = re.compile(r"ruff@(\d+\.\d+\.\d+)")
# `uvx ruff ...` / `uv tool run ruff` / premerge's argv `"uvx", "ruff"`, with no `@version` on the ruff token.
_UNPINNED = re.compile(r"""(?:uvx|uv tool run)["',\s]+ruff(?![@\w.-])""")


def _read(root: Path, rel: str) -> str | None:
    try:
        return (root / rel).read_text(encoding="utf-8")
    except OSError:
        return None


def canonical_version(root: Path) -> str | None:
    text = _read(root, CANONICAL)
    match = _REQUIRED.search(text) if text else None
    return match.group(1) if match else None


def _scan_paths(root: Path) -> list[str]:
    found = [rel for rel in SCAN_FILES if (root / rel).is_file()]
    for pattern in SCAN_GLOBS:
        found.extend(sorted(p.relative_to(root).as_posix() for p in root.glob(pattern)))
    return found


def count_sites(root: Path) -> int:
    total = 0
    for rel in CONFIGS:
        total += 1 if _REQUIRED.search(_read(root, rel) or "") else 0
    for rel in _scan_paths(root):
        total += len(_PINNED.findall(_read(root, rel) or ""))
    return total


def check_repo(root: Path) -> list[str]:
    want = canonical_version(root)
    if want is None:
        return [f'{CANONICAL}: no `required-version = "==X.Y.Z"` line, so there is no ruff pin to compare against. Add it']
    problems: list[str] = []
    for rel in CONFIGS[1:]:
        match = _REQUIRED.search(_read(root, rel) or "")
        if match is None:
            problems.append(f'{rel}: no `required-version = "=={want}"` in its [tool.ruff] table. Add it')
        elif match.group(1) != want:
            problems.append(f"{rel}: required-version is {match.group(1)} but {CANONICAL} pins {want}. Make them equal")
    for rel in _scan_paths(root):
        text = _read(root, rel) or ""
        for lineno, line in enumerate(text.splitlines(), start=1):
            for found in _PINNED.findall(line):
                if found != want:
                    problems.append(f"{rel}:{lineno}: ruff@{found} but {CANONICAL} pins {want}. Make them equal")
            if _UNPINNED.search(line):
                problems.append(f"{rel}:{lineno}: unpinned ruff run; write ruff@{want}")
    for rel, floor in MIN_SITES.items():
        seen = len(_PINNED.findall(_read(root, rel) or ""))
        if seen < floor:
            problems.append(f"{rel}: expected at least {floor} `ruff@{want}` pins, found {seen}. Was a ruff gate removed or left unpinned?")
    return problems


def self_test() -> int:
    """Prove the gate can fail: plant each violation in a scratch tree and require it to be named."""
    good = {
        CANONICAL: 'required-version = "==1.2.3"\n',
        "worker/pyproject.toml": '[tool.ruff]\nrequired-version = "==1.2.3"\n',
        "scripts/premerge.py": 'a = ["uvx", "ruff@1.2.3"]\n' * 4,
        ".github/workflows/ci-docs.yml": "run: uvx ruff@1.2.3 check\n" * 2,
        ".github/workflows/ci-worker.yml": "run: uvx ruff@1.2.3 check\n" * 2,
    }
    cases = {
        "clean": ({}, 0),
        "worker drift": ({"worker/pyproject.toml": '[tool.ruff]\nrequired-version = "==9.9.9"\n'}, 1),
        "unpinned": ({".github/workflows/ci-worker.yml": "run: uvx ruff check\n" * 2}, 1),
        "no pins": ({"scripts/premerge.py": "x\n"}, 1),
    }
    ok = True
    for name, (change, expect_problems) in cases.items():
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for rel, text in {**good, **change}.items():
                path = root / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(text, encoding="utf-8", newline="\n")
            got = bool(check_repo(root))
            if got != bool(expect_problems):
                print(f"ERROR: self-test case {name!r}: expected problems={bool(expect_problems)}, got {got}", file=sys.stderr)
                ok = False
    if ok:
        print("OK: check_ruff_pin self-test (4 cases)")
    return 0 if ok else 1


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Fail if ruff is not pinned to one version everywhere.")
    ap.add_argument("--root", type=Path, default=REPO_ROOT, help="repo root (default: the repo this script lives in)")
    ap.add_argument("--self-test", action="store_true", help="prove the gate can fail")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    root = args.root.resolve()
    problems = check_repo(root)
    for problem in problems:
        print(f"ERROR: {problem}", file=sys.stderr)
    if problems:
        return 1
    print(f"OK: ruff is pinned to {canonical_version(root)} in {count_sites(root)} places")
    return 0


if __name__ == "__main__":
    sys.exit(main())
