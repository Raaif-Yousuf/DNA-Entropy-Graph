"""Tests for scripts/check_actions_pinned.py (#485)."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_actions_pinned as cap  # noqa: E402

SHA = "3d3c42e5aac5ba805825da76410c181273ba90b1"


def _tree(tmp_path: Path, files: dict[str, str]) -> Path:
    for rel, text in files.items():
        p = tmp_path / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8", newline="\n")
    return tmp_path


def _wf(*uses: str) -> str:
    steps = "".join(f"      - uses: {u}\n" for u in uses)
    return f"jobs:\n  a:\n    steps:\n{steps}"


@pytest.mark.parametrize(
    "ref",
    ["foo/bar@v1", "foo/bar@main", "foo/bar@3d3c42e", "foo/bar@" + SHA[:39], "foo/bar@" + SHA + "0", "foo/bar"],
)
def test_planted_unpinned_ref_is_named_with_file_and_line(tmp_path, ref):
    root = _tree(tmp_path, {".github/workflows/x.yml": _wf(f"foo/baz@{SHA}", ref)})
    found = cap.find_unpinned(root)
    assert [(f.path, f.line) for f in found] == [(".github/workflows/x.yml", 5)]


def test_allowed_forms_pass(tmp_path):
    text = (
        "jobs:\n  a:\n    steps:\n"
        f"      - uses: actions/checkout@{SHA} # v7.0.1\n"
        f"      - name: x\n        uses: 'actions/setup-uv@{SHA}'  # v7.6.0\n"
        f"      - uses: github/codeql-action/init@{SHA.upper()}\n"
        "      - uses: ./.github/actions/local\n"
        "      - uses: docker://alpine:3.20\n"
        "      # uses: commented/out@v1\n"
        "      - run: echo uses foo/bar@v1\n"
    )
    root = _tree(tmp_path, {".github/workflows/x.yml": text})
    assert cap.find_unpinned(root) == []
    assert cap.count_uses(root) == 5


def test_composite_action_files_are_scanned(tmp_path):
    root = _tree(tmp_path, {".github/actions/a/action.yml": _wf("foo/bar@v2")})
    assert [f.path for f in cap.find_unpinned(root)] == [".github/actions/a/action.yml"]


def test_cli_exits_one_naming_the_file_and_zero_when_clean(tmp_path):
    bad = _tree(tmp_path / "bad", {".github/workflows/x.yml": _wf("foo/bar@v1")})
    r = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_actions_pinned.py"), "--root", str(bad)],
        capture_output=True,
        text=True,
        check=False,
    )
    assert r.returncode == 1 and "ERROR:" in r.stderr and "x.yml:4" in r.stderr
    good = _tree(tmp_path / "good", {".github/workflows/x.yml": _wf(f"foo/bar@{SHA}")})
    r = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_actions_pinned.py"), "--root", str(good)],
        capture_output=True,
        text=True,
        check=False,
    )
    assert r.returncode == 0 and r.stdout.startswith("OK:")


def test_empty_tree_fails_rather_than_passing_vacuously(tmp_path):
    assert cap.main(["--root", str(tmp_path)]) == 1


def test_self_test_passes():
    assert cap.main(["--self-test"]) == 0


def test_real_tree_is_fully_pinned_and_scanner_sees_the_uses():
    assert cap.count_uses(REPO_ROOT) >= 20
    assert cap.find_unpinned(REPO_ROOT) == []
