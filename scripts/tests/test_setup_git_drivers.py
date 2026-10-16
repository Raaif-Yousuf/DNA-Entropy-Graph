"""Tests for scripts/setup_git_drivers.py."""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

from test_resw_merge import BASE, _branch, _git, _repo, blk, doc, names  # noqa: E402

import setup_git_drivers as sgd  # noqa: E402


def test_the_script_sets_a_driver_that_really_merges_two_appending_branches(tmp_path):
    repo = _repo(tmp_path, configure=False)
    assert sgd.main(["--repo", str(repo)]) == 0
    _branch(repo, "one", doc(blk("A", "a"), blk("B", "b"), blk("X", "x")))
    _branch(repo, "two", doc(blk("A", "a"), blk("B", "b"), blk("Y", "y")))
    _git(repo, "merge", "-q", "--no-edit", "one")
    assert names((repo / "Resources.resw").read_text(encoding="utf-8")) == ["A", "B", "Y", "X"]


def test_the_value_is_passed_without_shell_quoting_and_is_idempotent(tmp_path):
    repo = _repo(tmp_path, configure=False)
    assert sgd.main(["--repo", str(repo)]) == 0 and sgd.main(["--repo", str(repo)]) == 0
    got = subprocess.run(["git", "config", "--get-all", "merge.resw.driver"], cwd=repo, capture_output=True, text=True, check=False).stdout
    assert got.strip().splitlines() == [sgd.resw_driver_command(sgd.install_driver_script(repo))]
    assert got.startswith('"') and "resw_merge.py" in got and got.strip().endswith("%O %A %B")


def test_outside_a_repo_it_fails_with_a_message_not_a_traceback(tmp_path, capsys):
    assert sgd.main(["--repo", str(tmp_path)]) == 1
    assert "ERROR" in capsys.readouterr().out


def test_base_fixture_is_the_two_key_document():  # keeps the imported fixture honest
    assert names(BASE) == ["A", "B"]


def test_the_driver_still_runs_when_the_merged_tree_has_no_scripts_folder(tmp_path):
    """Issue #800: the config used to point at scripts/resw_merge.py inside the checkout that ran setup, so merging
    from a branch older than the driver (no such file in its tree) made git run a missing script and conflict."""
    repo = _repo(tmp_path, configure=False)
    shutil.copytree(SCRIPTS_DIR, repo / "scripts", ignore=shutil.ignore_patterns("tests", "__pycache__"))
    _git(repo, "add", "-A")
    _git(repo, "commit", "-q", "-m", "scripts")
    setup = subprocess.run(
        [sys.executable, str(repo / "scripts" / "setup_git_drivers.py"), "--repo", str(repo)], capture_output=True, text=True, check=False
    )
    assert setup.returncode == 0, setup.stdout + setup.stderr
    _git(repo, "switch", "-q", "-c", "old1", "main")
    _git(repo, "rm", "-q", "-r", "scripts", ".gitattributes")
    (repo / "Resources.resw").write_bytes(doc(blk("A", "a"), blk("B", "b"), blk("X", "x")).encode())
    _git(repo, "commit", "-q", "-am", "old1")
    _git(repo, "switch", "-q", "-c", "old2", "main")
    _git(repo, "rm", "-q", "-r", "scripts", ".gitattributes")
    (repo / "Resources.resw").write_bytes(doc(blk("A", "a"), blk("B", "b"), blk("Y", "y")).encode())
    _git(repo, "commit", "-q", "-am", "old2")
    assert not (repo / "scripts").exists()
    merged = _git(repo, "merge", "-q", "--no-edit", "old1", check=False)
    assert merged.returncode == 0, merged.stdout + merged.stderr
    assert names((repo / "Resources.resw").read_text(encoding="utf-8")) == ["A", "B", "Y", "X"]


def test_rerunning_setup_refreshes_the_installed_copy(tmp_path):
    repo = _repo(tmp_path, configure=False)
    assert sgd.main(["--repo", str(repo)]) == 0
    installed = sgd.install_driver_script(repo)
    assert installed.read_bytes() == (SCRIPTS_DIR / "resw_merge.py").read_bytes()
    installed.write_bytes(b"stale")
    assert sgd.main(["--repo", str(repo)]) == 0
    assert installed.read_bytes() == (SCRIPTS_DIR / "resw_merge.py").read_bytes()
    assert (repo / ".git") in installed.parents
