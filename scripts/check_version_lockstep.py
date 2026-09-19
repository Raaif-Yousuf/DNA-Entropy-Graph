"""scripts/check_version_lockstep.py -- worker and app ship the same version.

    python scripts/check_version_lockstep.py
    python scripts/check_version_lockstep.py --self-test

The worker image and the app that drives it are versioned together
(`docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md`'s
`release.yml` design: "lockstep check" before a release builds). This
script reads `worker/pyproject.toml`'s `[project].version` and
`app/Directory.Build.props`'s `<Version>` and fails when they differ.

`app/` does not exist yet -- issue #61 (app: solution skeleton) is still
open. Until it lands, there is nothing to be in lockstep WITH, so this
script prints a notice naming #61 and passes rather than failing a check
against a tree nobody has built yet. The day `app/Directory.Build.props`
exists, the same run starts enforcing automatically -- no flag, no second
migration step, because the check is "does the file exist", not "has
someone remembered to turn this on".

Exit codes: 0 clean (including the pre-#61 notice-and-pass state), 1 the
two versions differ (once app/ exists) or a version string could not be
read from a file that DOES exist, 2 bad usage (worker/pyproject.toml
itself missing, which is a broken checkout, not a pending-issue state).
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

WORKER_PYPROJECT_RELATIVE = "worker/pyproject.toml"
APP_DIRECTORY_BUILD_PROPS_RELATIVE = "app/Directory.Build.props"
APP_SKELETON_ISSUE = 61

_VERSION_LINE_RE = re.compile(r'^\s*version\s*=\s*["\']([^"\']+)["\']\s*$', re.MULTILINE)


def read_worker_version(path: Path) -> str | None:
    """`[project].version` from a pyproject.toml. A small regex, not
    `tomllib`, on purpose: this reads only ONE scalar out of a file this
    script does not otherwise need to understand, and staying regex-based
    means this script has no opinion on which TOML parser the rest of the
    worker's own tooling settles on. Matches the FIRST top-level
    `version = "..."` line, which is `[project]`'s own (the file's `\\[build-
    system\\]`/tool tables that might carry their own `version` keys, if any,
    come later in a conventional pyproject.toml)."""
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return None
    m = _VERSION_LINE_RE.search(text)
    return m.group(1) if m else None


def read_app_version(path: Path) -> str | None:
    """`<Version>` from Directory.Build.props (MSBuild XML). Returns the
    first `<Version>` element's text anywhere in the file, so it does not
    matter which `<PropertyGroup>` it lives in."""
    try:
        tree = ET.parse(path)
    except (ET.ParseError, OSError):
        return None
    for elem in tree.getroot().iter():
        tag = elem.tag.rsplit("}", 1)[-1]  # strip an XML namespace if present
        if tag == "Version" and elem.text and elem.text.strip():
            return elem.text.strip()
    return None


def check(root: Path) -> tuple[list[str], list[str]]:
    """Return (problems, notices). `notices` are informational
    (printed but never fail the check); `problems` do."""
    worker_path = root / WORKER_PYPROJECT_RELATIVE
    app_path = root / APP_DIRECTORY_BUILD_PROPS_RELATIVE

    if not worker_path.is_file():
        return ([f"{WORKER_PYPROJECT_RELATIVE} does not exist"], [])

    worker_version = read_worker_version(worker_path)
    if worker_version is None:
        return ([f"{WORKER_PYPROJECT_RELATIVE} has no readable [project].version"], [])

    if not app_path.is_file():
        return ([], [
            f"{APP_DIRECTORY_BUILD_PROPS_RELATIVE} does not exist yet (issue #{APP_SKELETON_ISSUE}: "
            "app: solution skeleton) -- nothing to check lockstep against; "
            f"{WORKER_PYPROJECT_RELATIVE}'s version is {worker_version!r}. "
            "This will start enforcing automatically once that file exists."
        ])

    app_version = read_app_version(app_path)
    if app_version is None:
        return ([f"{APP_DIRECTORY_BUILD_PROPS_RELATIVE} exists but has no readable <Version>"], [])

    if worker_version != app_version:
        return ([
            f"version mismatch: {WORKER_PYPROJECT_RELATIVE} says {worker_version!r}, "
            f"{APP_DIRECTORY_BUILD_PROPS_RELATIVE} says {app_version!r}"
        ], [])

    return ([], [])


# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------

def self_test() -> bool:
    import tempfile

    ok = True

    def make_worker_pyproject(root: Path, version: str) -> None:
        (root / "worker").mkdir(parents=True, exist_ok=True)
        (root / "worker" / "pyproject.toml").write_text(
            f'[project]\nname = "dna_entropy"\nversion = "{version}"\nrequires-python = ">=3.12"\n',
            encoding="utf-8",
        )

    def make_app_props(root: Path, version: str) -> None:
        (root / "app").mkdir(parents=True, exist_ok=True)
        (root / "app" / "Directory.Build.props").write_text(
            f'<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>\n',
            encoding="utf-8",
        )

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        make_worker_pyproject(root, "1.2.3")
        problems, notices = check(root)
        if problems or not any(f"#{APP_SKELETON_ISSUE}" in n for n in notices):
            print(f"FAIL: expected a clean pass with an issue #{APP_SKELETON_ISSUE} notice, "
                  f"got problems={problems} notices={notices}")
            ok = False
        else:
            print(f"ok    passes with a notice naming #{APP_SKELETON_ISSUE} when app/ does not exist")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        make_worker_pyproject(root, "1.2.3")
        make_app_props(root, "1.2.3")
        problems, notices = check(root)
        if problems:
            print(f"FAIL: matching versions should be clean, got {problems}")
            ok = False
        else:
            print("ok    matching worker/app versions pass once app/ exists")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        make_worker_pyproject(root, "1.2.3")
        make_app_props(root, "1.2.4")
        problems, _notices = check(root)
        if not any("mismatch" in p for p in problems):
            print(f"FAIL: mismatched versions should fail, got {problems}")
            ok = False
        else:
            print("ok    a real version mismatch is caught once app/ exists")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        problems, _notices = check(root)
        if not problems:
            print("FAIL: a missing worker/pyproject.toml should be a problem, not a notice")
            ok = False
        else:
            print("ok    a missing worker/pyproject.toml is a real problem (broken checkout), not a notice")

    print(f"\n{'PASS' if ok else 'FAIL'}: check_version_lockstep self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Check that worker/pyproject.toml and app/Directory.Build.props agree on version.",
    )
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: current directory)")
    ap.add_argument("--self-test", action="store_true", help="run against synthetic fixtures and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    root = args.root.resolve()
    if not (root / WORKER_PYPROJECT_RELATIVE).exists() and not (root / "worker").is_dir():
        print(f"error: {root} does not look like this repo (no worker/)", file=sys.stderr)
        return 2

    problems, notices = check(root)
    for n in notices:
        print(f"NOTICE: {n}")
    if problems:
        for p in problems:
            print(f"ERROR: {p}", file=sys.stderr)
        return 1
    print("check_version_lockstep: clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
