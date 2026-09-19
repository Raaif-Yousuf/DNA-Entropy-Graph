"""Fail if a tracked file contains a real absolute user-home path.

WHY THIS IS A GUARD AND NOT A CONVENTION
----------------------------------------
This repository is public. A path like `C:\\Users\\<a real account name>\\...`
publishes the name of the person who wrote the line, and it is also simply
wrong for everyone else who clones the repo: nobody else has that directory.

MEASURED 2026-09-19: three files carried one on the day the repo was populated
(`FEATURES.md`, the appendix C spec, and the freshly written `CLAUDE.md`), all
three written by different passes that each knew the rule. It is the kind of
detail that survives a careful review because it reads as a normal path.

WHAT IS ALLOWED
---------------
An obviously fictional account name in an example (`C:/Users/you/...`) is fine
and is usually clearer than a variable, so the placeholder names below pass.
`%USERPROFILE%`, `$HOME` and `~` are the preferred spellings in real guidance.
`docs/migration/` is exempt: it is the record of the redaction that already
happened and necessarily quotes the shape it replaced.

USAGE
-----
    python scripts/check_user_home_paths.py            # check the tracked tree
    python scripts/check_user_home_paths.py --self-test

Exit code 0 when clean, 1 when a real home path is found. Run from the repo root.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys

# Account names that are self-evidently stand-ins. Compared lower-case.
PLACEHOLDERS = {
    "you",
    "user",
    "username",
    "me",
    "someone",
    "yourname",
    "youruser",
    "example",
    "<user>",
    "<user-home>",
    "<username>",
}

# The leading separator run is deliberately loose: a Windows path appears in
# this repo both as `C:\Users\x` and, inside JSON and shell examples, as
# `C:/Users/x`. Both leak the same name.
PATTERN = re.compile(r"(?:C:[\\/]+Users[\\/]+|/home/|/Users/)([A-Za-z0-9._<>-]+)")

# Paths whose whole job is to document the redaction, plus the guard itself.
EXEMPT_PREFIXES = (
    "docs/migration/",
    "scripts/check_user_home_paths.py",
    "scripts/tests/test_check_user_home_paths.py",
)

# Every absolute path in a Dockerfile is a path inside the image, not on anyone's
# machine. MEASURED 2026-09-19: `WORKDIR /home/worker` in both worker Dockerfiles was
# flagged as a leaked home directory, which it is not; "worker" is the container's
# service account. Exempting the file type is the honest rule, because there is no way to
# tell a real account name from a container user by looking at the name, and a guard that
# cries wolf on correct code is a guard someone deletes.
EXEMPT_BASENAME_PREFIXES = ("Dockerfile",)


def scan_text(text: str) -> list[tuple[int, str]]:
    """Return (line number, line) for every line holding a real home path."""
    hits: list[tuple[int, str]] = []
    for number, line in enumerate(text.splitlines(), 1):
        for match in PATTERN.finditer(line):
            if match.group(1).lower() not in PLACEHOLDERS:
                hits.append((number, line.strip()))
                break
    return hits


def tracked_files() -> list[str]:
    out = subprocess.run(
        ["git", "ls-files"], check=True, capture_output=True, text=True
    ).stdout
    return [p for p in out.split("\n") if p]


def check() -> int:
    files = tracked_files()
    hits: list[str] = []
    for path in files:
        if path.startswith(EXEMPT_PREFIXES) or path.rsplit("/", 1)[-1].startswith(
            EXEMPT_BASENAME_PREFIXES
        ):
            continue
        try:
            with open(path, encoding="utf-8") as handle:
                text = handle.read()
        except (UnicodeDecodeError, IsADirectoryError, FileNotFoundError):
            # Binary, a submodule, or deleted since `git ls-files` ran. Nothing
            # readable here, so nothing this guard can speak to.
            continue
        for number, line in scan_text(text):
            hits.append(f"{path}:{number}: {line[:160]}")

    if hits:
        print(
            "::error title=user-home path committed::Replace it with "
            "%USERPROFILE%, $HOME, or a placeholder account name.",
            file=sys.stderr,
        )
        print("\n".join(hits), file=sys.stderr)
        return 1
    print(f"check_user_home_paths: clean across {len(files)} tracked files.")
    return 0


def self_test() -> int:
    """Both arms. A guard that has only ever been run on a clean tree is not a
    guard; it is a line that has never said no."""
    real = "see C:" + chr(92) + "Users" + chr(92) + "raaif" + chr(92) + "repo"
    cases: list[tuple[str, bool]] = [
        (real, True),
        ("C:/Users/raaif/repo", True),
        ("/home/raaif/repo", True),
        ("/Users/raaif/repo", True),
        ("C:/Users/you/repo", False),
        ("/home/user/repo", False),
        ("%USERPROFILE%" + chr(92) + "repo", False),
        ("$HOME/repo", False),
        ("~/repo", False),
        ("no path here at all", False),
    ]
    failures = 0
    for text, should_flag in cases:
        flagged = bool(scan_text(text))
        if flagged != should_flag:
            failures += 1
            print(
                f"self-test FAILED: {text!r} -> flagged={flagged}, "
                f"expected {should_flag}",
                file=sys.stderr,
            )
    if failures:
        return 1
    print(f"check_user_home_paths: self-test passed ({len(cases)} cases).")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="exercise both arms of the pattern and exit",
    )
    args = parser.parse_args(argv)
    if args.self_test:
        return self_test()
    return check()


if __name__ == "__main__":
    sys.exit(main())
