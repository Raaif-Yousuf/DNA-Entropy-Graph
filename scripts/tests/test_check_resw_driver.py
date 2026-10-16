"""Tests for scripts/check_resw_driver.py: the installed merge driver must be origin/main's resw_merge.py (issue #800)."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))
sys.path.insert(0, str(SCRIPTS_DIR / "tests"))

from test_setup_git_drivers import NEW_SCRIPT, _git, _real_repo  # noqa: E402

import check_resw_driver as crd  # noqa: E402
import setup_git_drivers as sgd  # noqa: E402


def test_unconfigured_is_a_skip_not_a_failure(tmp_path, capsys):
    repo = _real_repo(tmp_path, origin_script=NEW_SCRIPT)
    assert crd.main(["--repo", str(repo)]) == 0
    assert "skipped" in capsys.readouterr().out


def test_an_installed_copy_equal_to_origin_main_passes(tmp_path, capsys):
    repo = _real_repo(tmp_path, origin_script=NEW_SCRIPT)
    assert sgd.main(["--repo", str(repo)]) == 0
    capsys.readouterr()
    assert crd.main(["--repo", str(repo)]) == 0
    assert capsys.readouterr().out.startswith("OK:")


def test_a_drifted_copy_fails_and_names_the_fix_command(tmp_path, capsys):
    repo = _real_repo(tmp_path, origin_script=NEW_SCRIPT)
    assert sgd.main(["--repo", str(repo)]) == 0
    (repo / ".git" / "deg-drivers" / "resw_merge.py").write_bytes(b"# drifted\n")
    capsys.readouterr()
    assert crd.main(["--repo", str(repo)]) == 1
    out = capsys.readouterr().out
    assert out.startswith("ERROR:") and "setup_git_drivers.py" in out


def test_a_config_pointing_at_a_missing_script_fails(tmp_path, capsys):
    repo = _real_repo(tmp_path, origin_script=NEW_SCRIPT)
    _git(repo, "config", "merge.resw.driver", '"python" "' + (tmp_path / "gone" / "resw_merge.py").as_posix() + '" %O %A %B')
    assert crd.main(["--repo", str(repo)]) == 1
    assert "setup_git_drivers.py" in capsys.readouterr().out


def test_without_an_origin_main_ref_it_skips(tmp_path, capsys):
    repo = _real_repo(tmp_path)
    assert sgd.main(["--repo", str(repo)]) == 0
    capsys.readouterr()
    assert crd.main(["--repo", str(repo)]) == 0
    assert "skipped" in capsys.readouterr().out


def test_it_runs_as_a_script_in_this_checkout():
    done = subprocess.run([sys.executable, str(SCRIPTS_DIR / "check_resw_driver.py")], capture_output=True, text=True, check=False)
    assert done.returncode in (0, 1), done.stdout + done.stderr
    assert done.stdout.startswith(("OK:", "ERROR:"))
