"""#958: premerge_wt.py runs premerge --fast in an agent worktree and never leaves a venv junction behind.

A fake runner is injected, so these tests never run the real gate. NTFS junction arms are Windows only.
"""

from __future__ import annotations

import os
import subprocess
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
        self.timeouts: list[float | None] = []

    def __call__(self, argv: list[str], env: dict[str, str], cwd: Path, timeout: float | None = None) -> int:
        self.calls.append((argv, env))
        self.timeouts.append(timeout)
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
    monkeypatch.setattr(wt_venv, "create_kill_job", lambda: None)  # the taskkill /T fallback path
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

    monkeypatch.setattr(wt_venv, "spawn_in_job", lambda argv, env, cwd, job: (FakeProc(), False))
    monkeypatch.setattr(wt_venv, "create_kill_job", lambda: None)
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


def test_land_wt_and_premerge_wt_build_the_same_premerge_argv(world, monkeypatch):
    wt, venv = world
    import land_wt as lw

    py = venv / _py_rel()
    fast = wt_venv.premerge_argv(wt, py, full=False, lane_prefix="premerge")
    assert fast == [str(py), str(wt / "scripts" / "premerge.py"), "--root", str(wt), "--python", str(py), "--fast"]
    assert "--fast" not in wt_venv.premerge_argv(wt, py, full=True, lane_prefix="land")
    # land_wt's own builder, called for real with run_bounded faked: same argv, "land" lane
    land_py = wt / "worker" / ".venv" / _py_rel()
    land_py.parent.mkdir(parents=True)
    land_py.write_bytes(b"fake\n")
    (wt / "scripts" / "heavy.py").write_text("# stub\n", encoding="utf-8")
    seen: list[list[str]] = []

    def fake_run_bounded(argv, **kwargs):  # noqa: ANN001, ANN003, ANN202
        seen.append(list(argv))
        return subprocess.CompletedProcess(argv, 0, b"", b"")

    monkeypatch.setattr(lw, "run_bounded", fake_run_bounded)
    for full in (False, True):
        lw.run_premerge(wt, venv, full)
        assert seen[-1] == wt_venv.premerge_argv(wt, land_py, full=full, lane_prefix="land")
        assert seen[-1][:5] == [str(land_py), str(wt / "scripts" / "heavy.py"), "--lane", f"land-{wt.name}", "--"]
        assert ("--fast" in seen[-1]) is (not full)


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


# ---- fix round 2 ------------------------------------------------------------


def _alive(pid: int) -> bool:
    import ctypes
    from ctypes import wintypes

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    k32.OpenProcess.restype = wintypes.HANDLE
    k32.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    k32.GetExitCodeProcess.restype = wintypes.BOOL
    k32.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = k32.OpenProcess(0x1000, False, pid)
    if not handle:
        return False
    try:
        code = wintypes.DWORD()
        return bool(k32.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == 259
    finally:
        k32.CloseHandle(handle)


GRANDCHILD = (
    "import os, subprocess, sys, time\n"
    "gc = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)'])\n"
    "open(sys.argv[1], 'w').write(str(os.getpid()) + ' ' + str(gc.pid))\n"
    "time.sleep(120)\n"
)


@needs_windows
def test_a_timeout_kills_the_child_and_its_grandchild(tmp_path: Path):
    import time

    pids = tmp_path / "pids.txt"
    code = pmw.default_runner([sys.executable, "-c", GRANDCHILD, str(pids)], dict(os.environ), Path.cwd(), 12.0)
    assert code == pmw.EXIT_TIMEOUT
    wait_for = time.monotonic() + 15
    while time.monotonic() < wait_for and not (pids.is_file() and len(pids.read_text(encoding="utf-8").split()) == 2):
        time.sleep(0.1)
    child, grandchild = (int(x) for x in pids.read_text(encoding="utf-8").split())
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline and (_alive(child) or _alive(grandchild)):
        time.sleep(0.2)
    assert not _alive(child)
    assert not _alive(grandchild)


@needs_windows
def test_a_job_object_kill_on_close_ends_an_assigned_process():
    import subprocess as sp

    job = wt_venv.create_kill_job()
    assert job is not None
    proc = sp.Popen([sys.executable, "-c", "import time; time.sleep(120)"])
    try:
        job.assign(proc)
        job.close()  # KILL_ON_JOB_CLOSE
        proc.wait(timeout=15)
        assert not _alive(proc.pid)
    finally:
        if proc.poll() is None:
            proc.kill()


@needs_windows
def test_a_failed_job_creation_falls_back_to_taskkill_and_says_so(monkeypatch, capsys):
    monkeypatch.setattr(wt_venv, "create_kill_job", lambda: None)
    code = pmw.default_runner([sys.executable, "-c", "import sys; sys.exit(0)"], dict(os.environ), Path.cwd(), 30.0)
    assert code == 0
    assert "WARNING" in capsys.readouterr().out


def test_run_forwards_the_default_timeout_and_main_forwards_the_cli_value(monkeypatch, world):
    wt, venv = _main_world(monkeypatch, world)
    r = Runner(wt)
    pmw.run(wt, venv, runner=r)
    assert r.timeouts == [pmw.TIMEOUT_SECONDS] and pmw.TIMEOUT_SECONDS == 7200.0
    r2 = Runner(wt)
    pmw.main(["--venv", str(venv)], runner=r2)
    assert r2.timeouts == [7200.0]
    r3 = Runner(wt)
    pmw.main(["--venv", str(venv), "--timeout", "99"], runner=r3)
    assert r3.timeouts == [99.0]


def test_default_mode_forwards_premerges_code_when_a_real_dir_appears_during_the_run(world, capsys):
    wt, venv = world
    link = wt / "worker" / ".venv"

    def plant(argv: list[str], env: dict[str, str], cwd: Path, timeout: float | None = None) -> int:
        link.mkdir()
        (link / "keep.txt").write_bytes(b"mine\n")
        return 3

    assert pmw.run(wt, venv, runner=plant) == 3
    assert (link / "keep.txt").is_file()
    assert "WARNING" in capsys.readouterr().out


def test_venv_pointing_at_the_worktrees_own_link_path_is_refused(world):
    wt, venv = world
    r = Runner(wt)
    assert pmw.run(wt, wt / "worker" / ".venv", runner=r) == pmw.EXIT_REFUSED
    assert pmw.run(wt, wt / "worker" / ".venv" / "Scripts", runner=r) == pmw.EXIT_REFUSED
    assert r.calls == []


@needs_windows
def test_venv_resolving_through_a_stale_worktree_junction_is_refused_and_the_target_survives(world):
    wt, venv = world
    link = wt / "worker" / ".venv"
    wt_venv.make_dir_link(link, venv)
    r = Runner(wt)
    assert pmw.run(wt, link, runner=r) == pmw.EXIT_REFUSED
    assert r.calls == [] and _survives(venv)
    wt_venv.unlink_dir_link(link)


# ---- fix round 3 ------------------------------------------------------------

PID_SCRIPT = "import os, sys, time\nopen(sys.argv[1], 'w').write(str(os.getpid()))\ntime.sleep(120)\n"


def _wait_for_pid_file(f: Path) -> int:
    import time

    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        if f.is_file() and f.read_text(encoding="utf-8").strip():
            return int(f.read_text(encoding="utf-8"))
        time.sleep(0.05)
    raise AssertionError(f"{f} never appeared")


def _wait_dead(pid: int, seconds: float = 10.0) -> bool:
    import time

    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if not _alive(pid):
            return True
        time.sleep(0.1)
    return not _alive(pid)


@needs_windows
def test_a_hard_kill_of_the_owner_leaves_no_real_interpreter_behind_n_runs(tmp_path: Path):
    """The owner dies (job handle closed, no taskkill, no kill_all): the REAL interpreter behind the venv launcher must die.
    MEASURED 2026-10-30 against the old assign-after-start code: 0 of 20 escaped. Kept as a pin of the guarantee."""
    escaped: list[int] = []
    for i in range(20):
        f = tmp_path / f"pid{i}.txt"
        job = wt_venv.create_kill_job()
        assert job is not None
        proc, assigned = wt_venv.spawn_in_job([sys.executable, "-c", PID_SCRIPT, str(f)], dict(os.environ), Path.cwd(), job)
        try:
            assert assigned
            real = _wait_for_pid_file(f)
            job.close()
            if not _wait_dead(real):
                escaped.append(real)
        finally:
            job.close()
            if proc.poll() is None:
                proc.kill()
            for pid in escaped:
                subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True, check=False)
    assert escaped == []


@needs_windows
def test_spawn_in_job_resumes_the_child_even_when_assignment_fails(tmp_path: Path):
    class RefusingJob:
        def assign(self, proc: object) -> bool:
            return False

    f = tmp_path / "pid.txt"
    proc, assigned = wt_venv.spawn_in_job([sys.executable, "-c", PID_SCRIPT, str(f)], dict(os.environ), Path.cwd(), RefusingJob())  # type: ignore[arg-type]
    try:
        assert assigned is False
        real = _wait_for_pid_file(f)  # it ran: it was resumed
        assert _alive(real)
    finally:
        wt_venv.kill_tree(proc)


def test_the_job_handle_is_closed_when_popen_raises(monkeypatch):
    closed: list[bool] = []

    class FakeJob:
        def assign(self, proc: object) -> bool:
            return True

        def terminate(self) -> None:
            pass

        def close(self) -> None:
            closed.append(True)

    def boom(*a: object, **k: object) -> object:
        raise FileNotFoundError("no such exe")

    monkeypatch.setattr(wt_venv, "create_kill_job", lambda: FakeJob())
    monkeypatch.setattr(pmw.subprocess, "Popen", boom)
    monkeypatch.setattr(wt_venv.subprocess, "Popen", boom)
    with pytest.raises(FileNotFoundError):
        pmw.default_runner(["nope"], {}, Path.cwd(), 5.0)
    assert closed


@pytest.mark.parametrize("bad", ["0", "-5"])
def test_a_non_positive_timeout_is_refused(monkeypatch, world, bad, capsys):
    wt, venv = _main_world(monkeypatch, world)
    r = Runner(wt)
    assert pmw.main(["--venv", str(venv), "--timeout", bad], runner=r) == pmw.EXIT_REFUSED
    assert r.calls == [] and "--timeout" in capsys.readouterr().out
