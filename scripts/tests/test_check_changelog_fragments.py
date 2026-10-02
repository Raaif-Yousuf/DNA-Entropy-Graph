"""Tests for scripts/check_changelog_fragments.py."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_changelog_fragments as ccf  # noqa: E402


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_changelog_fragments.py"), "--self-test"],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_changelog_fragments.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0


def test_find_fragments_ignores_readme(tmp_path):
    changelog_dir = tmp_path / "changelog.d"
    changelog_dir.mkdir()
    (changelog_dir / "README.md").write_text("# convention\n", encoding="utf-8")
    (changelog_dir / "a.md").write_text("- x\n", encoding="utf-8")
    found = ccf.find_fragments(changelog_dir)
    assert [p.name for p in found] == ["a.md"]


def test_validate_fragment_accepts_bullet(tmp_path):
    p = tmp_path / "a.md"
    p.write_text("- did a thing\n", encoding="utf-8")
    assert ccf.validate_fragment(p) is None


def test_validate_fragment_rejects_heading_with_hint(tmp_path):
    p = tmp_path / "a.md"
    p.write_text("## did a thing\n", encoding="utf-8")
    problem = ccf.validate_fragment(p)
    assert problem is not None
    assert "heading" in problem


def test_validate_fragment_rejects_empty(tmp_path):
    p = tmp_path / "a.md"
    p.write_text("   \n", encoding="utf-8")
    problem = ccf.validate_fragment(p)
    assert problem is not None
    assert "empty" in problem


def test_check_against_this_repos_real_tree_runs_without_crashing():
    """Not an assertion that every fragment several concurrent agents write
    tonight is well-formed -- that is exactly the kind of real finding this
    check exists to surface, not something to bake into a fixed pytest
    assertion -- only that the check runs cleanly end to end and returns a
    list. See this script's own report for the real, current result."""
    repo_root = SCRIPTS_DIR.parent
    problems = ccf.check(repo_root)
    assert isinstance(problems, list)


# ---------------------------------------------------------------------------
# #429: a fragment must EXIST when behaviour changed
# ---------------------------------------------------------------------------

import shutil  # noqa: E402

import pytest  # noqa: E402

FRAG = "docs/changelog.d/fix-1-x.md"


def test_source_change_without_fragment_is_missing():
    problem = ccf.missing_fragment([("M", "worker/src/dna_entropy/cli.py")])
    assert problem is not None
    assert "worker/src/dna_entropy/cli.py" in problem
    assert "docs/changelog.d/" in problem


def test_source_change_with_a_new_or_edited_fragment_is_fine():
    assert ccf.missing_fragment([("M", "worker/src/dna_entropy/cli.py"), ("A", FRAG)]) is None
    assert ccf.missing_fragment([("M", "app/src/X/Y.cs"), ("M", FRAG)]) is None


def test_deleting_a_fragment_does_not_count_as_having_one():
    assert ccf.missing_fragment([("M", "worker/src/a.py"), ("D", FRAG)]) is not None


def test_a_folded_sprint_log_counts_as_the_fragment_having_been_folded():
    assert ccf.missing_fragment([("M", "worker/src/a.py"), ("M", "docs/sprint_log.md")]) is None


@pytest.mark.parametrize(
    "path",
    [
        "docs/dev_commands.md",
        "worker/tests/test_x.py",
        "scripts/tests/test_x.py",
        "app/tests/Foo/BarTests.cs",
        "README.md",
        "docs/changelog.d/README.md",
    ],
)
def test_docs_and_tests_only_changes_need_no_fragment(path):
    assert ccf.missing_fragment([("M", path)]) is None


@pytest.mark.parametrize(
    "path",
    ["worker/src/a.py", "worker/vm/startup.sh", "app/src/A/B.cs", "scripts/check_x.py", ".github/workflows/ci.yml",
     "worker/pyproject.toml", "scripts/app_wiring_allowlist.json"],
)
def test_behaviour_paths_require_a_fragment(path):
    assert ccf.missing_fragment([("A", path)]) is not None


def test_nothing_changed_needs_nothing():
    assert ccf.missing_fragment([]) is None


def _git(root, *args):
    subprocess.run(["git", *args], cwd=root, check=True, capture_output=True)


@pytest.mark.skipif(shutil.which("git") is None, reason="git not installed")
def test_base_mode_names_the_missing_fragment_end_to_end(tmp_path):
    """The observable from #429: delete the fragment from a branch that changes a worker
    source file, and the guard names the missing requirement."""
    _git(tmp_path, "init", "-q", "-b", "main")
    _git(tmp_path, "config", "user.email", "t@example.com")
    _git(tmp_path, "config", "user.name", "T")
    (tmp_path / "worker" / "src").mkdir(parents=True)
    (tmp_path / "worker" / "src" / "a.py").write_text("x = 1\n", encoding="utf-8", newline="\n")
    _git(tmp_path, "add", "-A")
    _git(tmp_path, "commit", "-q", "-m", "base")

    (tmp_path / "worker" / "src" / "a.py").write_text("x = 2\n", encoding="utf-8", newline="\n")
    (tmp_path / "docs" / "changelog.d").mkdir(parents=True)
    fragment = tmp_path / "docs" / "changelog.d" / "fix-1-x.md"
    fragment.write_text("- changed x\n", encoding="utf-8", newline="\n")  # untracked, uncommitted
    assert ccf.check_against_base(tmp_path, "main") is None

    fragment.unlink()
    problem = ccf.check_against_base(tmp_path, "main")
    assert problem is not None
    assert "worker/src/a.py" in problem

    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_changelog_fragments.py"), "--root", str(tmp_path), "--base", "main"],
        capture_output=True, text=True, timeout=30, check=False,
    )
    assert proc.returncode == 1
    assert "worker/src/a.py" in proc.stderr


@pytest.mark.skipif(shutil.which("git") is None, reason="git not installed")
def test_base_mode_with_an_unknown_ref_is_a_usage_error_not_a_pass(tmp_path):
    _git(tmp_path, "init", "-q", "-b", "main")
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_changelog_fragments.py"), "--root", str(tmp_path), "--base", "nope/ref"],
        capture_output=True, text=True, timeout=30, check=False,
    )
    assert proc.returncode == 2
