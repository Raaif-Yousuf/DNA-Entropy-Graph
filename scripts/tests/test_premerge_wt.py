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

    def __call__(self, argv: list[str], env: dict[str, str], cwd: Path, timeout: float | None = None) -> int:
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

    def vandal(argv: list[str], env: dict[str, str], cwd: Path, timeout: float | None = None) -> int:
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


# ---- fix round (cold review) ------------------------------------------------


@needs_windows
def test_unlink_failure_after_the_run_names_premerges_real_exit_code_and_returns_a_defined_code(world, capsys):
    wt, venv = world
    link = wt / "worker" / ".venv"

    def plant_real_dir(argv: list[str], env: dict[str, str], cwd: Path, timeout: float | None = None) -> int:
        wt_venv.unlink_dir_link(link)
        link.mkdir()
        (link / "keep.txt").write_bytes(b"mine\n")
        return 3

    code = pmw.run(wt, venv, runner=plant_real_dir, junction=True)
    assert code == pmw.EXIT_CLEANUP_FAILED
    out = capsys.readouterr().out
    assert "ERROR" in out and "exit code 3" in out
    assert (link / "keep.txt").is_file()
    assert _survives(venv)


def test_make_dir_link_failure_in_junction_mode_is_a_refusal_not_a_traceback(world, monkeypatch, capsys):
    wt, venv = world

    def boom(link: Path, target: Path) -> None:
        raise wt_venv.LinkError("no junctions here")

    monkeypatch.setattr(wt_venv, "make_dir_link", boom)
    r = Runner(wt)
    assert pmw.run(wt, venv, runner=r, junction=True) == pmw.EXIT_REFUSED
    assert r.calls == []
    assert "no junctions here" in capsys.readouterr().out


def test_unlink_wraps_an_os_error_into_link_error(tmp_path: Path, monkeypatch):
    target = tmp_path / "t"
    target.mkdir()
    link = tmp_path / "l"
    try:
        os.symlink(target, link, target_is_directory=True)
    except OSError:
        pytest.skip("no symlink privilege")

    def deny(path: object) -> None:
        raise PermissionError("denied")

    with monkeypatch.context() as m:
        m.setattr(os, "rmdir", deny)
        m.setattr(os, "unlink", deny)
        with pytest.raises(wt_venv.LinkError):
            wt_venv.unlink_dir_link(link)
    assert target.is_dir()


def test_unlink_removes_a_file_symlink_without_following_it(tmp_path: Path):
    target = tmp_path / "t.txt"
    target.write_bytes(b"keep\n")
    link = tmp_path / "l"
    try:
        os.symlink(target, link)
    except OSError:
        pytest.skip("no symlink privilege")
    wt_venv.unlink_dir_link(link)
    assert not os.path.lexists(link)
    assert target.read_bytes() == b"keep\n"


def test_the_runner_gets_a_timeout_and_a_timeout_result_is_forwarded(world):
    wt, venv = world
    r = Runner(wt, code=pmw.EXIT_TIMEOUT)
    assert pmw.run(wt, venv, runner=r, timeout=12.5) == pmw.EXIT_TIMEOUT
    assert pmw.EXIT_TIMEOUT == 124


def test_default_runner_kills_the_tree_it_started_on_timeout(monkeypatch):
    killed: list[int] = []

    def kill(proc: object) -> None:
        killed.append(proc.pid)  # type: ignore[attr-defined]
        proc.kill()  # type: ignore[attr-defined]

    monkeypatch.setattr(wt_venv, "kill_tree", kill)
    code = pmw.default_runner([sys.executable, "-c", "import time; time.sleep(60)"], dict(os.environ), Path.cwd(), 1.0)
    assert code == pmw.EXIT_TIMEOUT
    assert len(killed) == 1


def test_default_runner_kills_the_tree_on_ctrl_c_then_reraises(monkeypatch):
    killed: list[object] = []

    class FakeProc:
        pid = 1234

        def __init__(self, *a: object, **k: object) -> None:
            pass

        def wait(self, timeout: float | None = None) -> int:
            raise KeyboardInterrupt

    monkeypatch.setattr(pmw.subprocess, "Popen", FakeProc)
    monkeypatch.setattr(wt_venv, "kill_tree", lambda proc: killed.append(proc))
    with pytest.raises(KeyboardInterrupt):
        pmw.default_runner(["x"], {}, Path.cwd(), None)
    assert len(killed) == 1


def test_a_stub_heavy_script_wraps_premerge_in_the_documented_argv_order(world):
    wt, venv = world
    (wt / "scripts" / "heavy.py").write_text("# stub\n", encoding="utf-8")
    r = Runner(wt)
    pmw.run(wt, venv, runner=r, extra=["--only", "x"])
    argv = r.calls[0][0]
    py = str(venv / _py_rel())
    assert argv[:5] == [py, str(wt / "scripts" / "heavy.py"), "--lane", f"premerge-{wt.name}", "--"]
    assert argv[5:] == [py, str(wt / "scripts" / "premerge.py"), "--root", str(wt), "--python", py, "--fast", "--only", "x"]


def test_land_wt_and_premerge_wt_build_the_same_premerge_argv(world):
    wt, venv = world
    py = venv / _py_rel()
    fast = wt_venv.premerge_argv(wt, py, full=False, lane_prefix="premerge")
    assert fast == [str(py), str(wt / "scripts" / "premerge.py"), "--root", str(wt), "--python", str(py), "--fast"]
    assert "--fast" not in wt_venv.premerge_argv(wt, py, full=True, lane_prefix="land")


def _main_world(monkeypatch, world):
    wt, venv = world
    (wt / "scripts" / "premerge.py").write_text("# stub\n", encoding="utf-8")
    (wt / ".git").write_text("gitdir: x\n", encoding="utf-8")
    monkeypatch.chdir(wt)
    return wt, venv


def test_main_refuses_the_primary_checkout(monkeypatch, world, capsys):
    wt, venv = _main_world(monkeypatch, world)
    (wt / ".git").unlink()
    (wt / ".git").mkdir()
    r = Runner(wt)
    assert pmw.main(["--venv", str(venv)], runner=r) == pmw.EXIT_REFUSED
    assert r.calls == [] and "primary checkout" in capsys.readouterr().out


def test_main_refuses_when_premerge_py_is_missing(monkeypatch, world, capsys):
    wt, venv = _main_world(monkeypatch, world)
    (wt / "scripts" / "premerge.py").unlink()
    r = Runner(wt)
    assert pmw.main(["--venv", str(venv)], runner=r) == pmw.EXIT_REFUSED
    assert r.calls == [] and "worktree root" in capsys.readouterr().out


def test_main_full_drops_fast_and_extras_reach_argv(monkeypatch, world):
    wt, venv = _main_world(monkeypatch, world)
    r = Runner(wt)
    assert pmw.main(["--venv", str(venv), "--full", "--", "--only", "x"], runner=r) == 0
    argv = r.calls[0][0]
    assert "--fast" not in argv and argv[-2:] == ["--only", "x"]
    r2 = Runner(wt)
    pmw.main(["--venv", str(venv)], runner=r2)
    assert "--fast" in r2.calls[0][0]


def test_primary_venv_git_failure_is_a_named_refusal(monkeypatch, world, capsys):
    wt, _ = _main_world(monkeypatch, world)

    def no_git(*a: object, **k: object) -> object:
        raise FileNotFoundError("git")

    monkeypatch.setattr(pmw.subprocess, "run", no_git)
    r = Runner(wt)
    assert pmw.main([], runner=r) == pmw.EXIT_REFUSED
    assert r.calls == [] and "--venv" in capsys.readouterr().out
