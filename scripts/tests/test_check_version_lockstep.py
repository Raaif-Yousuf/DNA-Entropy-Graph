"""Tests for scripts/check_version_lockstep.py."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_version_lockstep as cvl  # noqa: E402


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_version_lockstep.py"), "--self-test"],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_version_lockstep.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0


def test_read_worker_version_parses_pep621_version(tmp_path):
    p = tmp_path / "pyproject.toml"
    p.write_text('[project]\nname = "x"\nversion = "1.2.3"\n', encoding="utf-8")
    assert cvl.read_worker_version(p) == "1.2.3"


def test_read_worker_version_none_when_missing(tmp_path):
    p = tmp_path / "pyproject.toml"
    p.write_text('[project]\nname = "x"\n', encoding="utf-8")
    assert cvl.read_worker_version(p) is None


def test_read_app_version_parses_msbuild_xml(tmp_path):
    p = tmp_path / "Directory.Build.props"
    p.write_text(
        "<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>\n",
        encoding="utf-8",
    )
    assert cvl.read_app_version(p) == "1.2.3"


def test_read_app_version_none_when_no_version_element(tmp_path):
    p = tmp_path / "Directory.Build.props"
    p.write_text("<Project><PropertyGroup></PropertyGroup></Project>\n", encoding="utf-8")
    assert cvl.read_app_version(p) is None


def test_read_app_version_none_on_malformed_xml(tmp_path):
    p = tmp_path / "Directory.Build.props"
    p.write_text("<Project><PropertyGroup>not closed\n", encoding="utf-8")
    assert cvl.read_app_version(p) is None


def test_check_against_this_repos_real_tree_notices_missing_app():
    """app/ genuinely does not exist yet (issue #61) as of this session --
    unlike the docs-index check, this is a stable fact for tonight (the app
    skeleton is a different agent's much larger, separate task), so this
    assertion is safe to fix."""
    repo_root = SCRIPTS_DIR.parent
    problems, notices = cvl.check(repo_root)
    assert problems == []
    assert any(f"#{cvl.APP_SKELETON_ISSUE}" in n for n in notices)
