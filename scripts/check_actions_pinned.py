"""scripts/check_actions_pinned.py -- every GitHub Action `uses:` is pinned to a full commit SHA (#485).

    python scripts/check_actions_pinned.py
    python scripts/check_actions_pinned.py --self-test
    python scripts/check_actions_pinned.py --root <dir>

A moving tag (`@v7`, `@main`) lets whoever controls the action's repo run new code with this repo's
token, and with the signing secrets once release.yml exists (#178). Scans .github/workflows/*.yml and
.github/actions/**/action.yml. Allowed without a SHA: local actions (`./...`) and `docker://...`.
Dependabot's github-actions ecosystem updates the SHA and its trailing `# vX.Y.Z` comment.

Exit codes: 0 clean, 1 an unpinned `uses:` (or nothing to scan, which would be a vacuous pass).
"""

from __future__ import annotations

import argparse
import re
import sys
import tempfile
from pathlib import Path
from typing import NamedTuple

USES_RE = re.compile(r"^\s*(?:-\s+)?uses:\s*(?P<ref>[^\s#]+)")
SHA_RE = re.compile(r"^[^@\s]+@[0-9a-fA-F]{40}$")


class Finding(NamedTuple):
    path: str
    line: int
    uses: str

    def __str__(self) -> str:
        return f"{self.path}:{self.line}: `uses: {self.uses}` is not pinned to a 40-character commit SHA"


def _files(root: Path) -> list[Path]:
    wf = sorted((root / ".github" / "workflows").glob("*.yml"))
    wf += sorted((root / ".github" / "workflows").glob("*.yaml"))
    acts = sorted((root / ".github" / "actions").glob("**/action.y*ml"))
    return wf + acts


def _refs(root: Path):
    for path in _files(root):
        rel = path.relative_to(root).as_posix()
        for n, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            m = USES_RE.match(raw)
            if not m:
                continue
            ref = m.group("ref").strip("'\"")
            yield rel, n, ref


def _allowed(ref: str) -> bool:
    return ref.startswith("./") or ref.startswith("docker://") or bool(SHA_RE.match(ref))


def find_unpinned(root: Path) -> list[Finding]:
    return [Finding(rel, n, ref) for rel, n, ref in _refs(root) if not _allowed(ref)]


def count_uses(root: Path) -> int:
    return sum(1 for _ in _refs(root))


def self_test() -> bool:
    ok = True
    sha = "a" * 40
    cases = {
        f"      - uses: foo/bar@{sha} # v1.2.3\n": 0,
        "      - uses: ./local\n": 0,
        "      - uses: docker://alpine:3\n": 0,
        "      - uses: foo/bar@v1\n": 1,
        "      - uses: foo/bar@main\n": 1,
        f"      - uses: foo/bar@{sha[:7]}\n": 1,
    }
    for text, want in cases.items():
        with tempfile.TemporaryDirectory() as tmp:
            wf = Path(tmp) / ".github" / "workflows"
            wf.mkdir(parents=True)
            (wf / "t.yml").write_text("steps:\n" + text, encoding="utf-8", newline="\n")
            got = len(find_unpinned(Path(tmp)))
        good = got == want
        print(("ok    " if good else "FAIL: ") + text.strip() + f" -> {got} finding(s)")
        ok = ok and good
    print(f"\n{'PASS' if ok else 'FAIL'}: check_actions_pinned self-test")
    return ok


# Script-relative, never cwd-relative: running another tree's copy of this guard from
# elsewhere must scan the tree the script lives in (MEASURED 2026-10-02).
REPO_ROOT = Path(__file__).resolve().parents[1]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Fail on any GitHub Action `uses:` not pinned to a commit SHA.")
    ap.add_argument("--root", type=Path, default=REPO_ROOT, help="repo root (default: the repo this script lives in)")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return 0 if self_test() else 1
    root = args.root.resolve()
    total = count_uses(root)
    if total == 0:
        print(f"ERROR: no `uses:` found under {root}/.github; refusing to pass vacuously", file=sys.stderr)
        return 1
    findings = find_unpinned(root)
    for f in findings:
        print(f"ERROR: {f}", file=sys.stderr)
    if findings:
        print(
            f"\n{len(findings)} unpinned action(s). Replace the tag with the commit SHA and keep `# vX.Y.Z` after it.",
            file=sys.stderr,
        )
        return 1
    print(f"OK: check_actions_pinned: all {total} `uses:` are pinned to a commit SHA (or local/docker).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
