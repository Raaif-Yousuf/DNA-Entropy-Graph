"""Tests for scripts/setup_git_drivers.py."""

from __future__ import annotations

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
    assert got.strip().splitlines() == [sgd.resw_driver_command()]
    assert got.startswith('"') and "resw_merge.py" in got and got.strip().endswith("%O %A %B")


def test_outside_a_repo_it_fails_with_a_message_not_a_traceback(tmp_path, capsys):
    assert sgd.main(["--repo", str(tmp_path)]) == 1
    assert "ERROR" in capsys.readouterr().out


def test_base_fixture_is_the_two_key_document():  # keeps the imported fixture honest
    assert names(BASE) == ["A", "B"]
