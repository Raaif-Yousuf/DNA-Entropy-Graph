"""The ci-app / ci-worker `changes` gates (#31) must list every top-level directory their tests read.

A gate that skips the job on a PR that only edits a file the tests read reports "Skipped", which branch protection
counts as passing, so the edit lands untested. MEASURED 2026-10-03 (cold review): app tests read worker/, docs/contract/
and tests/contract-fixtures/, and worker tests read tests/contract-fixtures/ and docs/. This fails when a test starts
reading a top-level directory the gate does not list.
"""

from __future__ import annotations

import re
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TOP_DIRS = ("worker", "docs", "tests", "scripts", ".github")
GATE_RE = re.compile(r"grep -Eq '([^']+)'")


def _gate(workflow: str) -> str:
    text = (REPO / ".github" / "workflows" / workflow).read_text(encoding="utf-8")
    match = GATE_RE.search(text)
    assert match, f"{workflow} has no `grep -Eq '...'` changes gate"
    return match.group(1)


def _app_test_reads() -> set[str]:
    found: set[str] = set()
    files = [p for p in (REPO / "app" / "tests").rglob("*.cs") if "obj" not in p.parts and "bin" not in p.parts]
    assert len(files) > 20, "vacuity guard: scanned too few app test files"
    for path in files:
        for line in path.read_text(encoding="utf-8").splitlines():
            if "Path.Combine" in line or "Read(" in line or "File." in line:
                found.update(d for d in TOP_DIRS if re.search(rf'"{re.escape(d)}"', line))
    return found


def _worker_test_reads() -> set[str]:
    found: set[str] = set()
    files = list((REPO / "worker" / "tests").rglob("*.py"))
    assert len(files) > 20, "vacuity guard: scanned too few worker test files"
    for path in files:
        for line in path.read_text(encoding="utf-8").splitlines():
            if "parents[2]" in line or "parent.parent.parent" in line:
                found.update(m for m in re.findall(r'/ "([\w.-]+)"', line) if m in TOP_DIRS)
    return found


def _lists(gate: str, directory: str) -> bool:
    return re.search(rf"[(|^]{re.escape(directory)}/", gate) is not None or f"^{directory}/" in gate


def test_ci_app_gate_lists_every_top_level_dir_the_app_tests_read():
    reads = _app_test_reads()
    assert {"worker", "docs", "tests"} <= reads, f"scanner no longer sees the known reads: {reads}"
    gate = _gate("ci-app.yml")
    missing = sorted(d for d in reads if not _lists(gate, d))
    assert not missing, f"ci-app.yml changes gate does not list {missing}, so a PR editing only those is Skipped (passing)"


def test_ci_worker_gate_lists_every_top_level_dir_the_worker_tests_read():
    reads = _worker_test_reads()
    assert {"tests", "docs"} <= reads, f"scanner no longer sees the known reads: {reads}"
    gate = _gate("ci-worker.yml")
    missing = sorted(d for d in reads if not _lists(gate, d))
    assert not missing, f"ci-worker.yml changes gate does not list {missing}, so a PR editing only those is Skipped (passing)"
