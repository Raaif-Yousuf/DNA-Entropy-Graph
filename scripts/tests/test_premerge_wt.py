"""#958: premerge_wt.py runs premerge --fast in an agent worktree and never leaves a venv junction behind.

A fake runner is injected, so these tests never run the real gate. NTFS junction arms are Windows only.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS_DIR))

import premerge_wt as pmw  # noqa: E402
import wt_venv  # noqa: E402

needs_windows = pytest.mark.skipif(os.name != "nt", reason="NTFS junctions are Windows only")


def _py_rel() -> Path:
    return Path("Scripts") / "python.exe" if os.name == "nt" else Path("bin") / "python"


@pytest.fixture
def world(tmp_path: Path):
    venv = tmp_path / "primary" / "worker" / ".venv"  # the shared venv (sentinel)
    (venv / _py_rel()).parent.mkdir(parents=True)
    (venv / _py_rel()).write_bytes(b"fake python\n")
    (venv / "marker.txt").write_bytes(b"the real venv\n")
    wt = tmp_path / "wt"
    (wt / "worker").mkdir(parents=True)
    (wt / "scripts").mkdir()
    return wt, venv


class Runner:
    """Records the call; `link_seen` says whether worker/.venv existed while the 'gate' ran."""

    def __init__(self, wt: Path, code: int = 0, raises: BaseException | None = None):
        self.wt, self.code, self.raises = wt, code, raises
        self.calls: list[tuple[list[str], dict[str, str]]] = []
        self.link_seen: bool | None = None

    def __call__(self, argv: list[str], env: dict[str, str], cwd: Path) -> int:
        self.calls.append((argv, env))
        self.link_seen = os.path.lexists(self.wt / "worker" / ".venv")
        if self.raises:
            raise self.raises
        return self.code


def _survives(venv: Path) -> bool:
    return (venv / "marker.txt").read_bytes() == b"the real venv\n" and (venv / _py_rel()).is_file()


# ---- junction-free default -------------------------------------------------


def test_default_makes_no_link_and_points_premerge_at_the_shared_python_and_worktree_sources(world):
    wt, venv = world
    r = Runner(wt)
    assert pmw.run(wt, venv, runner=r) == 0
    argv, env = r.calls[0]
    assert r.link_seen is False
    assert not os.path.lexists(wt / "worker" / ".venv")
    py = str(venv / _py_rel())
    assert argv[0] == py and "--fast" in argv
    assert argv[argv.index("--python") + 1] == py
    assert argv[argv.index("--root") + 1] == str(wt)
    assert env["PYTHONPATH"].split(os.pathsep)[0] == str(wt / "worker" / "src")
    assert _survives(venv)


def test_exit_code_is_forwarded(world):
    wt, venv = world
    assert pmw.run(wt, venv, runner=Runner(wt, code=3)) == 3


def test_a_real_directory_at_worker_venv_is_refused_and_untouched(world):
    wt, venv = world
    real = wt / "worker" / ".venv"
    real.mkdir()
    (real / "keep.txt").write_bytes(b"mine\n")
    r = Runner(wt)
    assert pmw.run(wt, venv, runner=r) == pmw.EXIT_REFUSED
    assert r.calls == []
    assert (real / "keep.txt").read_bytes() == b"mine\n"


def test_missing_shared_venv_is_refused(world):
    wt, venv = world
    (venv / _py_rel()).unlink()
    r = Runner(wt)
    assert pmw.run(wt, venv, runner=r) == pmw.EXIT_REFUSED
    assert r.calls == []


def test_the_primary_checkout_is_told_apart_from_a_worktree(tmp_path: Path):
    primary = tmp_path / "repo"
    (primary / ".git").mkdir(parents=True)
    assert pmw.is_primary_checkout(primary)
    wt = tmp_path / "wt"
    wt.mkdir()
    (wt / ".git").write_text("gitdir: elsewhere\n", encoding="utf-8")
    assert not pmw.is_primary_checkout(wt)


# ---- junction mode ----------------------------------------------------------


@needs_windows
@pytest.mark.parametrize("how", ["ok", "red", "raises", "keyboard"])
def test_junction_mode_unlinks_on_every_path_and_never_touches_the_target(world, how):
    wt, venv = world
    boom: dict[str, BaseException] = {"raises": RuntimeError("boom"), "keyboard": KeyboardInterrupt()}
    r = Runner(wt, code=1 if how == "red" else 0, raises=boom.get(how))
    if how == "raises":
        with pytest.raises(RuntimeError):
            pmw.run(wt, venv, runner=r, junction=True)
    elif how == "keyboard":
        assert pmw.run(wt, venv, runner=r, junction=True) == pmw.EXIT_INTERRUPTED
    else:
        assert pmw.run(wt, venv, runner=r, junction=True) == (1 if how == "red" else 0)
    assert r.link_seen is True  # the link existed while the gate ran
    assert not os.path.lexists(wt / "worker" / ".venv")
    assert _survives(venv)
    argv, env = r.calls[0]
    assert argv[0] == str(wt / "worker" / ".venv" / _py_rel())
    assert env["PYTHONPATH"].split(os.pathsep)[0] == str(wt / "worker" / "src")


@needs_windows
def test_a_stale_junction_from_a_killed_run_is_replaced(world):
    wt, venv = world
    wt_venv.make_dir_link(wt / "worker" / ".venv", venv)
    r = Runner(wt)
    assert pmw.run(wt, venv, runner=r, junction=True) == 0
    assert r.link_seen is True
    assert not os.path.lexists(wt / "worker" / ".venv")
    assert _survives(venv)


@needs_windows
def test_a_stale_junction_is_cleared_in_default_mode_too(world):
    wt, venv = world
    wt_venv.make_dir_link(wt / "worker" / ".venv", venv)
    r = Runner(wt)
    assert pmw.run(wt, venv, runner=r) == 0
    assert r.link_seen is False
    assert _survives(venv)


@needs_windows
def test_junction_mode_refuses_a_real_directory_and_leaves_it(world):
    wt, venv = world
    real = wt / "worker" / ".venv"
    real.mkdir()
    (real / "keep.txt").write_bytes(b"mine\n")
    assert pmw.run(wt, venv, runner=Runner(wt), junction=True) == pmw.EXIT_REFUSED
    assert (real / "keep.txt").is_file()


def test_a_missing_shared_python_after_the_run_is_reported_loudly(world, capsys):
    wt, venv = world

    def vandal(argv: list[str], env: dict[str, str], cwd: Path) -> int:
        (venv / _py_rel()).unlink()
        return 0

    assert pmw.run(wt, venv, runner=vandal) == pmw.EXIT_VENV_GONE
    assert "SHARED VENV" in capsys.readouterr().out


def test_wt_venv_unlink_refuses_a_real_directory(tmp_path: Path):
    d = tmp_path / "real"
    d.mkdir()
    with pytest.raises(wt_venv.LinkError):
        wt_venv.unlink_dir_link(d)
    assert d.is_dir()


def test_default_runner_returns_the_child_exit_code():
    code = pmw.default_runner([sys.executable, "-c", "import sys; sys.exit(7)"], dict(os.environ), Path.cwd())
    assert code == 7
