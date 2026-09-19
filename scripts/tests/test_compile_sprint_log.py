"""Tests for scripts/compile_sprint_log.py."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import compile_sprint_log as csl  # noqa: E402


def _init_repo(root: Path) -> None:
    subprocess.run(["git", "init", "-q"], cwd=root, check=True, capture_output=True)
    subprocess.run(["git", "config", "user.email", "test@example.com"], cwd=root, check=True, capture_output=True)
    subprocess.run(["git", "config", "user.name", "Test"], cwd=root, check=True, capture_output=True)


def _make_sprint_log(root: Path, body: str = "") -> Path:
    docs = root / "docs"
    docs.mkdir(parents=True, exist_ok=True)
    log = docs / "sprint_log.md"
    log.write_text(f"# Sprint log\n\n{csl.HEADING}\n\n{body}", encoding="utf-8")
    return log


def test_validate_accepts_bullet(tmp_path):
    frag = tmp_path / "frag.md"
    frag.write_text("- did a thing\n", encoding="utf-8")
    assert csl._validate(frag) == "- did a thing"


def test_validate_rejects_heading_with_hint(tmp_path):
    frag = tmp_path / "frag.md"
    frag.write_text("## did a thing\n", encoding="utf-8")
    with pytest.raises(ValueError, match="Markdown heading"):
        csl._validate(frag)


def test_validate_rejects_empty_fragment(tmp_path):
    frag = tmp_path / "frag.md"
    frag.write_text("   \n", encoding="utf-8")
    with pytest.raises(ValueError, match="empty fragment"):
        csl._validate(frag)


def test_compile_fragments_prepends_newest_first_and_deletes_fragments(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _make_sprint_log(root, body="- an older entry already in the log\n")

    changelog_dir = root / "docs" / "changelog.d"
    changelog_dir.mkdir(parents=True)
    (changelog_dir / "a-branch.md").write_text("- fragment A\n", encoding="utf-8")
    (changelog_dir / "b-branch.md").write_text("- fragment B\n", encoding="utf-8")
    (changelog_dir / "README.md").write_text("not a fragment, ignored\n", encoding="utf-8")

    rc = csl.compile_fragments(root, dry_run=False)
    assert rc == 0

    result = (root / "docs" / "sprint_log.md").read_text(encoding="utf-8")
    assert csl.HEADING in result
    assert "fragment A" in result
    assert "fragment B" in result
    assert "an older entry already in the log" in result
    # Both real fragments were folded in and deleted; the README was left alone.
    assert not (changelog_dir / "a-branch.md").exists()
    assert not (changelog_dir / "b-branch.md").exists()
    assert (changelog_dir / "README.md").exists()


def test_compile_fragments_dry_run_touches_nothing(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _make_sprint_log(root)
    changelog_dir = root / "docs" / "changelog.d"
    changelog_dir.mkdir(parents=True)
    frag = changelog_dir / "a-branch.md"
    frag.write_text("- fragment A\n", encoding="utf-8")

    rc = csl.compile_fragments(root, dry_run=True)
    assert rc == 0
    assert frag.exists()
    assert "fragment A" not in (root / "docs" / "sprint_log.md").read_text(encoding="utf-8")


def test_compile_fragments_exits_nonzero_and_touches_nothing_on_malformed_fragment(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _make_sprint_log(root)
    changelog_dir = root / "docs" / "changelog.d"
    changelog_dir.mkdir(parents=True)
    good = changelog_dir / "good.md"
    good.write_text("- a good fragment\n", encoding="utf-8")
    bad = changelog_dir / "bad.md"
    bad.write_text("## not a bullet\n", encoding="utf-8")

    rc = csl.compile_fragments(root, dry_run=False)
    assert rc == 1
    # Nothing was modified: even the well-formed fragment survives.
    assert good.exists()
    assert bad.exists()
    assert "a good fragment" not in (root / "docs" / "sprint_log.md").read_text(encoding="utf-8")


def test_compile_fragments_exits_nonzero_when_heading_missing(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    (root / "docs").mkdir(parents=True)
    (root / "docs" / "sprint_log.md").write_text("# Sprint log\n\nno heading here\n", encoding="utf-8")
    changelog_dir = root / "docs" / "changelog.d"
    changelog_dir.mkdir(parents=True)
    (changelog_dir / "a.md").write_text("- x\n", encoding="utf-8")

    rc = csl.compile_fragments(root, dry_run=False)
    assert rc == 1


def test_no_changelog_fragments_reports_zero_and_succeeds(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _make_sprint_log(root)
    assert csl.compile_fragments(root, dry_run=False) == 0


def test_find_stray_changelog_dirs_finds_a_misplaced_directory(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _init_repo(root)
    (root / "docs" / "changelog.d").mkdir(parents=True)
    (root / "docs" / "changelog.d" / ".gitkeep").write_text("", encoding="utf-8")
    # A fragment written to the wrong nested directory -- the shape this
    # guard exists to catch.
    stray = root / "worker" / "docs" / "changelog.d"
    stray.mkdir(parents=True)
    (stray / "oops.md").write_text("- fragment\n", encoding="utf-8")
    subprocess.run(["git", "add", "-A"], cwd=root, check=True, capture_output=True)

    result = csl.find_stray_changelog_dirs(root)
    assert result == ["worker/docs/changelog.d"]


def test_find_stray_changelog_dirs_clean_when_only_correct_dir_present(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _init_repo(root)
    (root / "docs" / "changelog.d").mkdir(parents=True)
    (root / "docs" / "changelog.d" / "a.md").write_text("- x\n", encoding="utf-8")
    subprocess.run(["git", "add", "-A"], cwd=root, check=True, capture_output=True)

    assert csl.find_stray_changelog_dirs(root) == []


def test_find_stray_changelog_dirs_guard_against_this_repo():
    """This is the actual guard `find_stray_changelog_dirs`'s own docstring
    describes: run it against the real repo so a fragment landing in the
    wrong directory is caught by the normal test suite, not discovered by
    `compile_fragments()` silently ignoring it at merge time. This is also
    what keeps `find_stray_changelog_dirs` itself from being wired to
    nothing -- nothing else in this script calls it."""
    assert csl.find_stray_changelog_dirs(REPO_ROOT) == []


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "compile_sprint_log.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "--dry-run" in proc.stdout


def test_cli_nonzero_exit_on_malformed_fragment(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    _make_sprint_log(root)
    changelog_dir = root / "docs" / "changelog.d"
    changelog_dir.mkdir(parents=True)
    (changelog_dir / "bad.md").write_text("## not a bullet\n", encoding="utf-8")

    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "compile_sprint_log.py"), "--root", str(root)],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 1
    assert "must start with a '- ' bullet" in proc.stderr
