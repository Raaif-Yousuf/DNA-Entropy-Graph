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
    installed = (repo / ".git" / "deg-drivers" / "resw_merge.py").resolve().as_posix()
    assert got.strip().splitlines() == [f'"{Path(sys.executable).resolve().as_posix()}" "{installed}" %O %A %B']
    assert got.startswith('"') and "resw_merge.py" in got and got.strip().endswith("%O %A %B")


def test_outside_a_repo_it_fails_with_a_message_not_a_traceback(tmp_path, capsys):
    assert sgd.main(["--repo", str(tmp_path)]) == 1
    assert "ERROR" in capsys.readouterr().out


def test_base_fixture_is_the_two_key_document():  # keeps the imported fixture honest
    assert names(BASE) == ["A", "B"]


def test_the_driver_still_runs_when_the_merged_tree_has_no_scripts_folder_and_no_attribute_line(tmp_path):
    """Issue #800: the config used to point at scripts/resw_merge.py inside the checkout that ran setup, so merging
    from a branch older than the driver (no such file in its tree, no merge=resw line either) conflicted. Uses the
    REAL nested resw path so the info/attributes route is what makes git pick the driver."""
    repo = _real_repo(tmp_path, local_script=(SCRIPTS_DIR / "resw_merge.py").read_bytes())
    done = _setup_from(repo)
    assert done.returncode == 0, done.stdout + done.stderr
    resw = repo / sgd.RESW_PATH
    for branch, key in (("old1", "X"), ("old2", "Y")):
        _git(repo, "switch", "-q", "-c", branch, "main")
        _git(repo, "rm", "-q", "-r", "scripts")
        resw.write_bytes(doc(blk("A", "a"), blk("B", "b"), blk(key, key.lower())).encode())
        _git(repo, "commit", "-q", "-am", branch)
    assert not (repo / "scripts").exists()
    merged = _git(repo, "merge", "-q", "--no-edit", "old1", check=False)
    assert merged.returncode == 0, merged.stdout + merged.stderr
    assert names(resw.read_text(encoding="utf-8")) == ["A", "B", "Y", "X"]


def test_rerunning_setup_refreshes_the_installed_copy(tmp_path):
    repo = _repo(tmp_path, configure=False)
    assert sgd.main(["--repo", str(repo)]) == 0
    installed = sgd.install_driver_script(repo)
    assert installed.read_bytes() == (SCRIPTS_DIR / "resw_merge.py").read_bytes()
    installed.write_bytes(b"stale")
    assert sgd.main(["--repo", str(repo)]) == 0
    assert installed.read_bytes() == (SCRIPTS_DIR / "resw_merge.py").read_bytes()
    assert (repo / ".git").resolve() in installed.resolve().parents


# ---- cold review of #800: stale worktrees, stable interpreter, attribute file hygiene -------------------------------

NEW_SCRIPT = b"# NEW origin/main resw_merge\n"
OLD_SCRIPT = b"# OLD stale-branch resw_merge\n"


def _venv_python(root: Path) -> Path:
    return root / "worker" / ".venv" / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")


def _real_repo(tmp_path: Path, *, origin_script: bytes | None = None, local_script: bytes = OLD_SCRIPT) -> Path:
    """A repo with the REAL nested resw path, a committed copy of the setup script and a (possibly stale) resw_merge.py;
    when origin_script is given, refs/remotes/origin/main holds a commit whose scripts/resw_merge.py is that."""
    repo = tmp_path / "primary"
    repo.mkdir()
    _git(repo, "init", "-q", "-b", "main")
    _git(repo, "config", "user.email", "t@example.invalid")
    _git(repo, "config", "user.name", "t")
    _git(repo, "config", "core.autocrlf", "false")
    (repo / "scripts").mkdir()
    shutil.copyfile(SCRIPTS_DIR / "setup_git_drivers.py", repo / "scripts" / "setup_git_drivers.py")
    (repo / "scripts" / "resw_merge.py").write_bytes(local_script)
    resw = repo / sgd.RESW_PATH
    resw.parent.mkdir(parents=True)
    resw.write_bytes(BASE.encode())
    _git(repo, "add", "-A")
    _git(repo, "commit", "-q", "-m", "base")
    if origin_script is not None:
        (repo / "scripts" / "resw_merge.py").write_bytes(origin_script)
        _git(repo, "commit", "-q", "-am", "origin")
        _git(repo, "update-ref", "refs/remotes/origin/main", "HEAD")
        _git(repo, "reset", "-q", "--hard", "HEAD~1")
    return repo


def _setup_from(repo: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(repo / "scripts" / "setup_git_drivers.py"), "--repo", str(repo)], capture_output=True, text=True, check=False
    )


def _attrs(repo: Path) -> bytes:
    return (repo / ".git" / "info" / "attributes").read_bytes()


def test_the_pattern_is_the_one_land_wt_uses():
    import land_wt

    assert sgd.RESW_PATH == land_wt.RESW_PATH


def test_a_stale_checkout_still_routes_the_file_and_the_check_attr_says_resw(tmp_path):
    repo = _real_repo(tmp_path)  # no .gitattributes anywhere: the old code wrote nothing yet printed OK
    done = _setup_from(repo)
    assert done.returncode == 0, done.stdout + done.stderr
    assert _attrs(repo) == f"{sgd.RESW_PATH} merge=resw\n".encode()
    assert _git(repo, "check-attr", "merge", "--", sgd.RESW_PATH).stdout.strip().endswith("merge: resw")


def test_the_installed_copy_is_origin_mains_blob_not_the_stale_checkouts(tmp_path):
    repo = _real_repo(tmp_path, origin_script=NEW_SCRIPT, local_script=OLD_SCRIPT)
    assert (repo / "scripts" / "resw_merge.py").read_bytes() == OLD_SCRIPT
    done = _setup_from(repo)
    assert done.returncode == 0, done.stdout + done.stderr
    assert (repo / ".git" / "deg-drivers" / "resw_merge.py").read_bytes() == NEW_SCRIPT


def test_a_newer_installed_copy_is_not_replaced_by_an_older_checkout(tmp_path):
    repo = _real_repo(tmp_path, origin_script=NEW_SCRIPT, local_script=OLD_SCRIPT)
    assert _setup_from(repo).returncode == 0
    assert _setup_from(repo).returncode == 0  # a second run from the same stale tree
    assert (repo / ".git" / "deg-drivers" / "resw_merge.py").read_bytes() == NEW_SCRIPT


def test_foreign_attribute_lines_survive_even_when_they_are_not_utf8_and_the_line_is_not_duplicated(tmp_path):
    repo = _real_repo(tmp_path)
    info = repo / ".git" / "info"
    info.mkdir(exist_ok=True)
    foreign = b"*.png binary\n# caf\xe9 comment\n*.bin -diff"  # latin-1 byte, and no trailing newline
    (info / "attributes").write_bytes(foreign)
    assert _setup_from(repo).returncode == 0
    assert _setup_from(repo).returncode == 0
    got = _attrs(repo)
    assert got == foreign + b"\n" + f"{sgd.RESW_PATH} merge=resw\n".encode()


def test_a_different_merge_attribute_for_the_same_path_is_warned_about_and_ours_wins(tmp_path):
    repo = _real_repo(tmp_path)
    info = repo / ".git" / "info"
    info.mkdir(exist_ok=True)
    (info / "attributes").write_bytes(f"{sgd.RESW_PATH} merge=union\n".encode())
    done = _setup_from(repo)
    assert done.returncode == 0, done.stdout + done.stderr
    assert "WARNING" in done.stdout and "merge=union" in done.stdout
    assert _git(repo, "check-attr", "merge", "--", sgd.RESW_PATH).stdout.strip().endswith("merge: resw")


def test_the_interpreter_is_the_primary_checkouts_venv_even_when_setup_runs_from_a_linked_worktree(tmp_path):
    repo = _real_repo(tmp_path)
    venv = _venv_python(repo)
    venv.parent.mkdir(parents=True)
    venv.write_bytes(b"")
    _git(repo, "add", "-A", "-f")
    _git(repo, "commit", "-q", "-m", "more")
    linked = tmp_path / "linked"
    _git(repo, "worktree", "add", "-q", "-b", "lane", str(linked))
    assert sgd.main(["--repo", str(linked)]) == 0
    value = _git(repo, "config", "--get", "merge.resw.driver").stdout.strip()
    expected = f'"{venv.resolve().as_posix()}" "{(repo / ".git" / "deg-drivers" / "resw_merge.py").resolve().as_posix()}" %O %A %B'
    assert value == expected


def test_without_a_primary_venv_the_running_interpreter_is_used(tmp_path):
    repo = _real_repo(tmp_path)
    assert sgd.main(["--repo", str(repo)]) == 0
    value = _git(repo, "config", "--get", "merge.resw.driver").stdout.strip()
    assert value.startswith(f'"{Path(sys.executable).resolve().as_posix()}" ')
