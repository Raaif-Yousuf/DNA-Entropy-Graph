"""Tests for scripts/heavy.py (#487): machine-wide slot lock for heavy commands."""

from __future__ import annotations

import contextlib
import csv
import os
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path

import pytest

SCRIPTS = Path(__file__).resolve().parents[1]
HEAVY = SCRIPTS / "heavy.py"
sys.path.insert(0, str(SCRIPTS))

import heavy  # noqa: E402

BASE = r"C:\t\deg-art"


def _spawn(lock_dir: Path, code: str, *extra: str, slots: int | None = 1) -> subprocess.Popen:
    slot_args = [] if slots is None else ["--slots", str(slots)]
    return subprocess.Popen(
        [sys.executable, str(HEAVY), *slot_args, "--lock-dir", str(lock_dir), *extra, "--", sys.executable, "-c", code],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )


class _Lines:
    """Reads a child's stdout on a thread so a test can wait for a line without depending on timing."""

    def __init__(self, proc: subprocess.Popen):
        self._q: queue.Queue[str | None] = queue.Queue()
        self.lines: list[str] = []
        threading.Thread(target=self._pump, args=(proc,), daemon=True).start()

    def _pump(self, proc: subprocess.Popen) -> None:
        assert proc.stdout is not None  # spawned with stdout=PIPE
        for line in proc.stdout:
            self._q.put(line.rstrip("\r\n"))
        self._q.put(None)

    def wait_for(self, prefix: str, timeout: float = 60.0) -> str:
        deadline = time.time() + timeout
        while True:
            left = deadline - time.time()
            if left <= 0:
                raise AssertionError(f"no line starting {prefix!r} within {timeout}s; saw {self.lines}")
            try:
                line = self._q.get(timeout=left)
            except queue.Empty:
                continue
            if line is None:
                raise AssertionError(f"stream ended before {prefix!r}; saw {self.lines}")
            self.lines.append(line)
            if line.startswith(prefix):
                return line

    def text(self) -> str:
        while True:
            try:
                line = self._q.get(timeout=5)
            except queue.Empty:
                break
            if line is None:
                break
            self.lines.append(line)
        return "\n".join(self.lines)


def _gated(gate: Path) -> str:
    """Child code: print S, hold until the test creates `gate`, print E. No sleeps decide the order."""
    return (
        "import os, time\n"
        "print('S', time.time(), flush=True)\n"
        "t = time.time()\n"
        f"while not os.path.exists({str(gate)!r}) and time.time() - t < 60: time.sleep(0.05)\n"
        "print('E', time.time(), flush=True)\n"
    )


def _pid_alive(pid: int) -> bool:
    if sys.platform == "win32":
        out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"], capture_output=True, text=True, check=False).stdout
        return any(len(row) > 1 and row[1] == str(pid) for row in csv.reader(out.splitlines()))
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    return True


def _kill_pid(pid: int, tree: bool = False) -> None:
    if sys.platform == "win32":
        subprocess.run(["taskkill", "/F", *(["/T"] if tree else []), "/PID", str(pid)], capture_output=True, check=False)
    else:
        import signal

        with contextlib.suppress(ProcessLookupError):
            os.kill(pid, signal.SIGKILL)


STAMP = "import time; print('S', time.time(), flush=True); time.sleep({d}); print('E', time.time(), flush=True)"


def _window(out: str) -> tuple[float, float]:
    vals = {line.split()[0]: float(line.split()[1]) for line in out.splitlines() if line[:2] in ("S ", "E ")}
    return vals["S"], vals["E"]


# --- injection (pure) ---------------------------------------------------------------------------


@pytest.mark.parametrize("verb", ["build", "test", "run", "pack", "publish"])
def test_injects_for_dotnet_verbs(verb):
    out = heavy.inject_artifacts_path(["dotnet", verb, "x.sln"], "laneA", BASE)
    assert out == ["dotnet", verb, "x.sln", "--artifacts-path", str(Path(BASE) / "laneA")]


def test_no_injection_when_given_separate_or_equals_form():
    a = ["dotnet", "build", "--artifacts-path", "X"]
    b = ["dotnet", "test", "--artifacts-path=X"]
    assert heavy.inject_artifacts_path(a, "l", BASE) == a
    assert heavy.inject_artifacts_path(b, "l", BASE) == b


def test_no_injection_for_format_or_non_dotnet():
    assert heavy.inject_artifacts_path(["dotnet", "format", "x"], "l", BASE) == ["dotnet", "format", "x"]
    assert heavy.inject_artifacts_path(["pytest", "build"], "l", BASE) == ["pytest", "build"]
    assert heavy.inject_artifacts_path(["dotnet"], "l", BASE) == ["dotnet"]


def test_injects_before_double_dash_args_for_run():
    out = heavy.inject_artifacts_path(["dotnet", "run", "--project", "p", "--", "a", "b"], "l", BASE)
    assert out == ["dotnet", "run", "--project", "p", "--artifacts-path", str(Path(BASE) / "l"), "--", "a", "b"]


def test_dotnet_exe_path_and_case_recognised():
    out = heavy.inject_artifacts_path([r"C:\Program Files\dotnet\dotnet.exe", "BUILD"], "l", BASE)
    assert out[-2] == "--artifacts-path"


# --- the lock -------------------------------------------------------------------------------------


def test_two_holders_with_one_slot_serialise(tmp_path):
    gate = tmp_path / "gate"
    p1 = _spawn(tmp_path, _gated(gate))
    r1 = _Lines(p1)
    r1.wait_for("S ")  # the child runs, so the wrapper holds the only slot
    p2 = _spawn(tmp_path, _gated(gate))
    r2 = _Lines(p2)
    r2.wait_for("heavy: waiting")  # p2 provably blocked on p1's slot
    gate.write_text("go", encoding="utf-8", newline="\n")
    p1.wait(timeout=30)
    p2.wait(timeout=30)
    _, e1 = _window(r1.text())
    s2, _ = _window(r2.text())
    assert s2 >= e1 - 0.05, f"second holder started at {s2} before first ended at {e1}"
    assert "waiting" not in r1.text().lower()


def test_two_slots_allow_two_holders_in_parallel(tmp_path):
    gate = tmp_path / "gate"
    p1 = _spawn(tmp_path, _gated(gate), slots=2)
    p2 = _spawn(tmp_path, _gated(gate), slots=2)
    r1, r2 = _Lines(p1), _Lines(p2)
    # Both print S while neither can finish (the gate is closed): they overlap, whatever the start-up time.
    r1.wait_for("S ")
    r2.wait_for("S ")
    gate.write_text("go", encoding="utf-8", newline="\n")
    p1.wait(timeout=30)
    p2.wait(timeout=30)
    assert "waiting" not in r1.text().lower() + r2.text().lower()


def test_waiting_line_printed_once(tmp_path):
    gate = tmp_path / "gate"
    p1 = _spawn(tmp_path, _gated(gate))
    _Lines(p1).wait_for("S ")
    p2 = _spawn(tmp_path, "print('x')", "--poll", "0.05")
    r2 = _Lines(p2)
    r2.wait_for("heavy: waiting")
    time.sleep(0.5)  # many more polls at 0.05 s; a per-poll line would show up
    gate.write_text("go", encoding="utf-8", newline="\n")
    p1.wait(timeout=30)
    p2.wait(timeout=30)
    out = r2.text()
    assert out.lower().count("waiting") == 1
    assert out.isascii()


def test_killed_holder_slot_is_reclaimed(tmp_path):
    holder = _spawn(tmp_path, "import os, time; print('up', os.getpid(), flush=True); time.sleep(20)")
    child_pid = int(_Lines(holder).wait_for("up ").split()[1])
    try:
        # While the holder lives the slot is really taken: a second wrapper must say it is waiting.
        # (Always-succeeding _try_lock makes this arm fail.)
        waiter = _spawn(tmp_path, "print('got')", "--poll", "0.05")
        rw = _Lines(waiter)
        rw.wait_for("heavy: waiting")
        # Kill the wrapper hard (no cleanup runs); the OS drops its lock.
        holder.kill()
        holder.wait(timeout=10)
        waiter.wait(timeout=20)
        assert "got" in rw.text()
    finally:
        _kill_pid(child_pid)  # safety net


@pytest.mark.skipif(sys.platform != "win32", reason="Windows job object")
def test_hard_killed_wrapper_takes_its_child_with_it(tmp_path):
    p = _spawn(tmp_path, "import os, time; print('up', os.getpid(), flush=True); time.sleep(20)")
    child_pid = int(_Lines(p).wait_for("up ").split()[1])
    try:
        assert _pid_alive(child_pid)
        subprocess.run(["taskkill", "/F", "/PID", str(p.pid)], capture_output=True, check=False)
        p.wait(timeout=10)
        deadline = time.time() + 5
        while _pid_alive(child_pid) and time.time() < deadline:
            time.sleep(0.1)
        assert not _pid_alive(child_pid), "child outlived its hard-killed wrapper (uncounted by the slot limit)"
    finally:
        _kill_pid(child_pid)  # safety net


def test_exit_code_preserved(tmp_path):
    p = _spawn(tmp_path, "import sys; sys.exit(7)")
    p.communicate(timeout=30)
    assert p.returncode == 7


def test_missing_command_is_usage_error(tmp_path):
    r = subprocess.run([sys.executable, str(HEAVY), "--lock-dir", str(tmp_path), "--"], capture_output=True, text=True)
    assert r.returncode == 2
    assert r.stdout.isascii() and r.stderr.isascii()


def test_env_heavy_slots_default_and_flag_wins(monkeypatch, tmp_path):
    monkeypatch.setenv("HEAVY_SLOTS", "5")
    assert heavy.resolve_slots(None, tmp_path) == 5
    assert heavy.resolve_slots(3, tmp_path) == 3
    monkeypatch.delenv("HEAVY_SLOTS")
    assert heavy.resolve_slots(None, tmp_path) == 2
    monkeypatch.setenv("HEAVY_SLOTS", "junk")
    assert heavy.resolve_slots(None, tmp_path) == 2


def test_acquire_slot_function_blocks_when_full(tmp_path):
    a = heavy.acquire_slot(tmp_path, 1, poll=0.05, on_wait=lambda holders: None)
    try:
        assert heavy.try_acquire(tmp_path, 1) is None
    finally:
        a.release()
    b = heavy.try_acquire(tmp_path, 1)
    assert b is not None
    b.release()


def test_relative_slash_path_executable_is_resolved(tmp_path):
    exe = Path(sys.executable).absolute()  # not resolve(): a venv python is a symlink on Linux and would leave the cwd
    if not exe.is_relative_to(Path.cwd()):
        pytest.skip("interpreter is not under the cwd")
    rel = exe.relative_to(Path.cwd()).as_posix()
    r = subprocess.run([sys.executable, str(HEAVY), "--lock-dir", str(tmp_path), "--", rel, "-c", "print('ok')"], capture_output=True, text=True)
    assert r.returncode == 0 and "ok" in r.stdout


def test_resolve_executable_keeps_unknown_command():
    assert heavy.resolve_executable(["no-such-binary-xyz", "a"]) == ["no-such-binary-xyz", "a"]


# --- dotnet env (pure) ----------------------------------------------------------------------------

_DOTNET_DEFAULTS = {"MSBUILDDISABLENODEREUSE": "1", "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "UseSharedCompilation": "false"}


def test_dotnet_env_sets_defaults_without_mutating_input():
    base = {"PATH": "x"}
    out = heavy.dotnet_env(["dotnet", "build"], base)
    assert out == {"PATH": "x", **_DOTNET_DEFAULTS}
    assert base == {"PATH": "x"}


def test_dotnet_env_caller_value_wins():
    out = heavy.dotnet_env([r"C:\dotnet\dotnet.exe", "test"], {"MSBUILDDISABLENODEREUSE": "0"})
    assert out["MSBUILDDISABLENODEREUSE"] == "0"
    assert out["DOTNET_CLI_USE_MSBUILD_SERVER"] == "0"
    assert out["UseSharedCompilation"] == "false"


def test_dotnet_env_untouched_for_non_dotnet():
    base = {"PATH": "x"}
    assert heavy.dotnet_env(["pytest", "-q"], base) == base


def test_dotnet_env_applies_to_any_dotnet_verb_including_format():
    assert heavy.dotnet_env(["dotnet", "format"], {})["UseSharedCompilation"] == "false"


@pytest.mark.skipif(sys.platform != "win32", reason="fake dotnet.cmd")
def test_child_of_a_dotnet_command_receives_the_env(tmp_path, monkeypatch):
    (tmp_path / "dotnet.cmd").write_text("@echo off\necho NR=%MSBUILDDISABLENODEREUSE%\n", encoding="utf-8", newline="\r\n")
    monkeypatch.setenv("PATH", str(tmp_path) + os.pathsep + os.environ["PATH"])
    monkeypatch.delenv("MSBUILDDISABLENODEREUSE", raising=False)
    r = subprocess.run(
        [sys.executable, str(HEAVY), "--lock-dir", str(tmp_path / "locks"), "--lane", "t", "--", "dotnet", "build"],
        capture_output=True,
        text=True,
    )
    assert "NR=1" in r.stdout, (r.stdout, r.stderr)


# --- review round: artifacts base, lane, machine-wide slots, holders, error paths -------------------


def _git(cwd, *args):
    subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@example.invalid", *args], cwd=str(cwd), check=True, capture_output=True)


def test_default_artifacts_base_follows_the_cwd_repo(tmp_path):
    # #874: the base is the git toplevel of the CWD (inside the repo, not %TEMP%), so a lane in a
    # worktree that runs the primary's heavy.py builds under ITS OWN app/.artifacts.
    repo = tmp_path / "repo"
    (repo / "app").mkdir(parents=True)
    (repo / "app" / "DnaEntropyGraph.sln").write_text("")
    _git(repo, "init", "-q")
    assert Path(heavy.default_artifacts_base(repo / "app")) == repo.resolve() / "app" / ".artifacts"


def test_default_artifacts_base_in_a_linked_worktree_is_the_worktree(tmp_path):
    primary = tmp_path / "primary"
    primary.mkdir()
    _git(primary, "init", "-q")
    _git(primary, "commit", "-q", "--allow-empty", "-m", "x")
    wt = tmp_path / "wt"
    _git(primary, "worktree", "add", "-q", str(wt), "-b", "lane")
    (wt / "app").mkdir()
    (wt / "app" / "DnaEntropyGraph.sln").write_text("")
    got = Path(heavy.default_artifacts_base(wt / "app"))
    assert got == wt.resolve() / "app" / ".artifacts"
    assert got != primary.resolve() / "app" / ".artifacts"


def test_repo_toplevel_is_none_outside_any_repo(tmp_path):
    assert heavy.repo_toplevel(tmp_path) is None


def test_default_artifacts_base_warns_when_the_cwd_repo_has_no_app_sln(tmp_path, capsys):
    other = tmp_path / "other"
    other.mkdir()
    _git(other, "init", "-q")
    got = Path(heavy.default_artifacts_base(other))
    err = capsys.readouterr().err
    assert got == heavy.REPO_ROOT / "app" / ".artifacts"
    assert not (other / "app").exists()
    assert "heavy: warning:" in err
    assert str(other.resolve()) in err.replace("/", "\\") or str(other.resolve()) in err
    assert str(heavy.REPO_ROOT) in err


def test_default_artifacts_base_warns_outside_any_repo(tmp_path, capsys):
    got = Path(heavy.default_artifacts_base(tmp_path))
    err = capsys.readouterr().err
    assert got == heavy.REPO_ROOT / "app" / ".artifacts"
    assert "heavy: warning:" in err
    assert "not inside a git repo" in err
    assert str(heavy.REPO_ROOT) in err


def test_main_injects_the_cwd_repo_artifacts_path(tmp_path, monkeypatch):
    # End to end through main(): the injected --artifacts-path comes from the CWD repo.
    repo = tmp_path / "repo"
    (repo / "app").mkdir(parents=True)
    (repo / "app" / "DnaEntropyGraph.sln").write_text("")
    _git(repo, "init", "-q")
    monkeypatch.chdir(repo / "app")
    captured = {}
    monkeypatch.setattr(heavy, "inject_artifacts_path", lambda argv, lane, base: captured.setdefault("base", base) and argv)
    monkeypatch.setattr(heavy, "acquire_slot", lambda *a, **k: (_ for _ in ()).throw(RuntimeError("stop")))
    with contextlib.suppress(RuntimeError):
        heavy.main(["--lock-dir", str(tmp_path / "l"), "--lane", "x", "--", "dotnet", "test"])
    assert Path(captured["base"]) == repo.resolve() / "app" / ".artifacts"


def test_needs_lane_only_for_dotnet_verbs_without_artifacts_path():
    assert heavy.needs_lane(["dotnet", "test", "x"])
    assert heavy.needs_lane(["dotnet.exe", "BUILD"])
    assert not heavy.needs_lane(["dotnet", "test", "--artifacts-path", "X"])
    assert not heavy.needs_lane(["dotnet", "test", "--artifacts-path=X"])
    assert not heavy.needs_lane(["dotnet", "format"])
    assert not heavy.needs_lane(["pytest", "build"])


def test_dotnet_without_lane_is_a_usage_error(tmp_path):
    r = subprocess.run(
        [sys.executable, str(HEAVY), "--lock-dir", str(tmp_path), "--", "dotnet", "test"], capture_output=True, text=True, check=False
    )
    assert r.returncode == 2
    assert r.stderr.startswith("ERROR:") and "--lane" in r.stderr and r.stderr.isascii()


def test_bad_lane_name_is_a_usage_error(tmp_path):
    r = subprocess.run(
        [sys.executable, str(HEAVY), "--lock-dir", str(tmp_path), "--lane", "..\\x", "--", "dotnet", "test"],
        capture_output=True,
        text=True,
        check=False,
    )
    assert r.returncode == 2 and "ERROR:" in r.stderr


def test_non_dotnet_runs_without_a_lane(tmp_path):
    p = _spawn(tmp_path, "print('fine')")
    assert "fine" in p.communicate(timeout=30)[0]
    assert p.returncode == 0


def test_slots_file_beats_env_and_flag_beats_file(monkeypatch, tmp_path):
    monkeypatch.setenv("HEAVY_SLOTS", "5")
    (tmp_path / "slots.txt").write_text("3\n", encoding="utf-8", newline="\n")
    assert heavy.resolve_slots(None, tmp_path) == 3
    assert heavy.resolve_slots(1, tmp_path) == 1
    (tmp_path / "slots.txt").write_text("junk", encoding="utf-8", newline="\n")
    assert heavy.resolve_slots(None, tmp_path) == 5
    (tmp_path / "slots.txt").write_text("0", encoding="utf-8", newline="\n")
    assert heavy.resolve_slots(None, tmp_path) == 5


def test_set_slots_writes_the_file_and_prints_ok(tmp_path):
    lock = tmp_path / "new" / "dir"
    r = subprocess.run([sys.executable, str(HEAVY), "--lock-dir", str(lock), "--set-slots", "4"], capture_output=True, text=True, check=False)
    assert r.returncode == 0 and r.stdout.startswith("OK:") and r.stdout.isascii()
    assert (lock / "slots.txt").read_text(encoding="utf-8").strip() == "4"
    bad = subprocess.run([sys.executable, str(HEAVY), "--lock-dir", str(lock), "--set-slots", "0"], capture_output=True, text=True, check=False)
    assert bad.returncode == 2 and "ERROR:" in bad.stderr


def test_callers_that_disagree_on_env_still_share_one_limit_via_the_file(monkeypatch, tmp_path):
    (tmp_path / "slots.txt").write_text("1", encoding="utf-8", newline="\n")
    monkeypatch.setenv("HEAVY_SLOTS", "5")  # would allow five; the file says one
    gate = tmp_path / "gate"
    p1 = _spawn(tmp_path, _gated(gate), slots=None)
    _Lines(p1).wait_for("S ")
    p2 = _spawn(tmp_path, "print('x')", slots=None)
    r2 = _Lines(p2)
    r2.wait_for("heavy: waiting")
    gate.write_text("go", encoding="utf-8", newline="\n")
    p1.wait(timeout=30)
    p2.wait(timeout=30)


def test_holders_reports_only_slots_whose_lock_is_held(tmp_path):
    # A stale .info (holder killed) next to a FREE slot must not be reported.
    (tmp_path / "slot-0.info").write_text("pid=999999\nlane=stale\nstart=x\ncmd=old\n", encoding="utf-8", newline="\n")
    assert heavy.holders(tmp_path, 1) == []
    held = heavy.try_acquire(tmp_path, 1)
    assert held is not None
    try:
        heavy.write_info(tmp_path, held.index, lane="mine", command=["echo", "hi"])
        got = heavy.holders(tmp_path, 1)
        assert [(h["pid"], h["lane"]) for h in got] == [(str(os.getpid()), "mine")]
    finally:
        held.release()
    assert heavy.holders(tmp_path, 1) == []


def test_held_slot_without_info_is_still_listed(tmp_path):
    held = heavy.try_acquire(tmp_path, 1)
    assert held is not None
    try:
        got = heavy.holders(tmp_path, 1)
        assert len(got) == 1 and got[0]["pid"] == "?"
    finally:
        held.release()


def test_info_file_is_written_while_holding_and_removed_after(tmp_path):
    gate = tmp_path / "gate"
    p = _spawn(tmp_path, _gated(gate), "--lane", "mylane")
    _Lines(p).wait_for("S ")
    info = (tmp_path / "slot-0.info").read_text(encoding="utf-8")
    assert info.isascii() and "lane=mylane" in info and "pid=" in info and "cmd=" in info
    assert all(len(line) <= 90 for line in info.splitlines())
    gate.write_text("go", encoding="utf-8", newline="\n")
    p.wait(timeout=30)
    assert not (tmp_path / "slot-0.info").exists()


def test_waiting_line_names_the_holder_and_the_action(tmp_path):
    gate = tmp_path / "gate"
    p1 = _spawn(tmp_path, _gated(gate), "--lane", "holderlane")
    _Lines(p1).wait_for("S ")
    p2 = _spawn(tmp_path, "print('x')")
    line = _Lines(p2).wait_for("heavy: waiting")
    gate.write_text("go", encoding="utf-8", newline="\n")
    p1.wait(timeout=30)
    p2.wait(timeout=30)
    assert "holderlane" in line and "pid" in line.lower()
    assert "taskkill" in line or "kill" in line
    assert line.isascii()


def test_unusable_lock_dir_is_a_usage_error(tmp_path):
    afile = tmp_path / "afile"
    afile.write_text("x", encoding="utf-8", newline="\n")
    r = subprocess.run(
        [sys.executable, str(HEAVY), "--lock-dir", str(afile), "--", sys.executable, "-c", "print(1)"],
        capture_output=True,
        text=True,
        check=False,
    )
    assert r.returncode == 2
    assert "ERROR:" in r.stderr and r.stderr.isascii()


def test_unstartable_command_message_is_ascii_and_exit_127(tmp_path):
    r = subprocess.run(
        [sys.executable, str(HEAVY), "--lock-dir", str(tmp_path), "--", "no-such-cmd-\u00e9"], capture_output=True, text=True, check=False
    )
    assert r.returncode == 127
    assert "ERROR:" in r.stderr and r.stderr.isascii()


# --- #996: --status and the periodic "still queued" line ---------------------------------------


def _status(lock_dir: Path, *extra: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(HEAVY), "--status", "--slots", "2", "--lock-dir", str(lock_dir), *extra],
        capture_output=True,
        text=True,
        check=False,
        timeout=60,
    )


def test_status_with_no_slots_held_exits_zero_and_says_every_slot_is_free(tmp_path):
    out = _status(tmp_path)
    assert out.returncode == 0, out.stderr
    assert out.stdout.isascii()
    assert out.stdout.lower().count("free") == 2


def test_status_lists_a_live_holder_with_pid_lane_age_alive_and_cpu(tmp_path):
    gate = tmp_path / "gate"
    p = _spawn(tmp_path, _gated(gate), "--lane", "statuslane", slots=2)
    try:
        _Lines(p).wait_for("S ")
        time.sleep(1.2)  # the age must be visibly nonzero
        out = _status(tmp_path)
        assert out.returncode == 0, out.stderr
        line = next(ln for ln in out.stdout.splitlines() if "statuslane" in ln)
        holder_pid = heavy._read_info(tmp_path / "slot-0.info")["pid"]  # not p.pid: a venv python.exe is a launcher that re-spawns
        assert f"pid {holder_pid}" in line and heavy.pid_alive(int(holder_pid))
        assert "alive yes" in line
        assert "cpu " in line and "cpu ?" not in line
        age = int(line.split("age ")[1].split("s")[0])
        assert age >= 1
        assert out.stdout.isascii()
    finally:
        gate.write_text("go", encoding="utf-8", newline="\n")
        p.wait(timeout=30)


def test_slot_status_fields_for_a_held_slot(tmp_path):
    held = heavy.try_acquire(tmp_path, 2)
    assert held is not None
    try:
        heavy.write_info(tmp_path, held.index, lane="mine", command=["echo"])
        rows = heavy.slot_status(tmp_path, 2)
    finally:
        held.release()
    by_slot = {r["slot"]: r for r in rows}
    assert by_slot["1"]["state"] == "free"
    row = by_slot["0"]
    assert (row["state"], row["pid"], row["lane"], row["alive"]) == ("held", os.getpid(), "mine", True)
    assert row["age_s"] is not None and 0 <= row["age_s"] < 60
    assert row["cpu_s"] is not None and row["cpu_s"] > 0  # this very test process has used CPU


def test_slot_status_marks_a_dead_pid_not_alive_and_without_cpu(tmp_path):
    held = heavy.try_acquire(tmp_path, 1)
    assert held is not None
    try:
        (tmp_path / "slot-0.info").write_text("pid=4194301\nlane=ghost\nstart=2020-01-01T00:00:00\ncmd=x\n", encoding="ascii", newline="\n")
        row = heavy.slot_status(tmp_path, 1)[0]
    finally:
        held.release()
    assert row["alive"] is False and row["cpu_s"] is None and row["age_s"] is not None and row["age_s"] > 3600


def test_tree_cpu_counts_a_grandchild_through_its_child(tmp_path):
    """child -> grandchild burner. `_descendants(child)` must contain the grandchild (a version returning only {root} fails here)
    and the tree's CPU must be at least what the grandchild alone has used."""
    pidfile = tmp_path / "gc.pid"
    burner = "x = 0\nfor i in range(4_000_000): x += i\nimport time; time.sleep(60)"
    child_code = (
        "import subprocess, sys, time\n"
        f"p = subprocess.Popen([sys.executable, '-c', {burner!r}])\n"
        f"open({str(pidfile)!r}, 'w').write(str(p.pid))\n"
        "time.sleep(60)\n"
    )
    child = subprocess.Popen([sys.executable, "-c", child_code])
    gc_pid = 0
    try:
        deadline = time.time() + 30
        while not pidfile.exists() and time.time() < deadline:
            time.sleep(0.1)
        while (not pidfile.exists() or not pidfile.read_text().strip()) and time.time() < deadline:
            time.sleep(0.1)  # the file exists before the child has written the pid into it
        gc_pid = int(pidfile.read_text().strip())
        time.sleep(2.0)  # let the burner run
        table = heavy._win_process_table() if heavy.IS_WINDOWS else {c: v[0] for c, v in heavy._posix_process_table().items()}
        assert gc_pid in heavy._descendants(child.pid, table)
        gc_cpu = heavy.tree_cpu_seconds(gc_pid)
        tree = heavy.tree_cpu_seconds(child.pid)
        assert gc_cpu is not None and gc_cpu > 0
        assert tree is not None and tree >= gc_cpu
    finally:
        _kill_pid(child.pid, tree=True)  # /T: the venv launcher's real child is under it
        if gc_pid:
            _kill_pid(gc_pid)


def test_descendants_walks_several_levels_whatever_the_table_order():
    parents = {5: 4, 4: 3, 3: 1, 9: 8}  # 5 -> 4 -> 3 -> 1, listed child-first; 9 is someone else's
    assert heavy._descendants(1, parents) == {1, 3, 4, 5}
    assert heavy._descendants(7, parents) == {7}


def test_waiting_line_says_how_long_the_holder_has_held_the_slot():
    start = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(time.time() - 125))
    held = [{"slot": "0", "pid": "123", "lane": "other", "cmd": "", "start": start}]
    line = heavy.describe_wait(1, held)
    assert "other" in line and "pid 123" in line and "for 2m" in line and line.isascii()


def test_acquire_slot_ticks_while_waiting_and_not_before_the_interval(tmp_path):
    held = heavy.try_acquire(tmp_path, 1)
    assert held is not None
    ticks: list[float] = []
    waits: list[object] = []

    def release_later():
        time.sleep(1.3)
        held.release()

    threading.Thread(target=release_later, daemon=True).start()
    slot = heavy.acquire_slot(tmp_path, 1, poll=0.05, on_wait=waits.append, on_tick=lambda h, waited: ticks.append(waited), tick_interval=0.5)
    slot.release()
    assert len(waits) == 1
    assert ticks, "no tick while queued past the interval"
    assert ticks == sorted(ticks) and ticks[0] >= 0.5  # never before the interval
    assert len(ticks) < ticks[-1] / 0.5 + 1  # never faster than one per interval (no upper bound on lateness: load makes it flaky)


def test_the_waiting_line_goes_to_stderr_not_stdout(tmp_path):
    gate = tmp_path / "gate"
    p1 = _spawn(tmp_path, _gated(gate), "--lane", "holder")
    _Lines(p1).wait_for("S ")
    p2 = subprocess.Popen(
        [sys.executable, str(HEAVY), "--slots", "1", "--lock-dir", str(tmp_path), "--poll", "0.05", "--", sys.executable, "-c", "print('x')"],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    err_q: queue.Queue[str | None] = queue.Queue()

    def pump_stderr() -> None:
        assert p2.stderr is not None
        for line in p2.stderr:
            err_q.put(line)
        err_q.put(None)

    threading.Thread(target=pump_stderr, daemon=True).start()
    try:
        first = None
        deadline = time.time() + 60
        while time.time() < deadline:  # the waiting line itself, not a sleep, says it is queued; other stderr lines may precede it
            line = err_q.get(timeout=max(0.1, deadline - time.time()))
            if line is None or "heavy: waiting" in line:
                first = line
                break
    finally:
        gate.write_text("go", encoding="utf-8", newline="\n")
    assert p2.stdout is not None
    out = p2.stdout.read()  # not communicate(): the stderr thread owns that pipe
    p2.wait(timeout=60)
    p1.wait(timeout=30)
    assert first is not None and "heavy: waiting" in first and "holder" in first
    assert "waiting" not in out


# --- #930: Ctrl-C ends the command's whole tree, not only the direct child -----------------------

ORPHANING_CHILD = (
    "import subprocess, sys\n"
    "kw = {'creationflags': 0x8 | 0x200} if sys.platform == 'win32' else {}\n"
    "p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], **kw)\n"
    "open(sys.argv[1], 'w').write(str(p.pid))\n"
)


def test_ctrl_c_ends_a_detached_grandchild_whose_parent_already_exited(tmp_path, monkeypatch):
    """The command starts a DETACHED grandchild and exits, so `proc.kill()` on the direct child (the old code) and taskkill /T by the
    child's pid both find nothing. Only the job (Windows) or the process group (POSIX) reaches it. In-process on purpose: the
    wrapper's own process-wide job is stubbed out, so this proves the per-command job, not the wrapper's exit. Limit: a process
    started with CREATE_BREAKAWAY_FROM_JOB escapes a job that allows breakaway."""
    pidfile = tmp_path / "gc.pid"
    monkeypatch.setattr(heavy, "kill_children_with_me", lambda: None)

    def interrupted_after_child_exits(proc):
        proc.wait(timeout=30)
        deadline = time.time() + 20
        while not pidfile.exists() and time.time() < deadline:
            time.sleep(0.05)
        raise KeyboardInterrupt

    monkeypatch.setattr(heavy, "_wait_child", interrupted_after_child_exits)
    rc = heavy.main(["--slots", "1", "--lock-dir", str(tmp_path / "locks"), "--", sys.executable, "-c", ORPHANING_CHILD, str(pidfile)])
    pid = int(pidfile.read_text())
    try:
        assert rc == 130
        deadline = time.time() + 5
        while _pid_alive(pid) and time.time() < deadline:
            time.sleep(0.1)
        assert not _pid_alive(pid), f"detached grandchild {pid} survived Ctrl-C"
    finally:
        _kill_pid(pid)


# --- review round: pid_alive access denied, kill ordering, diagnostics on stderr, errno None -----


@pytest.mark.skipif(not heavy.IS_WINDOWS, reason="OpenProcess is Windows only")
@pytest.mark.parametrize(("last_error", "alive"), [(5, True), (87, False)])
def test_pid_alive_treats_access_denied_as_alive_and_invalid_parameter_as_gone(monkeypatch, last_error, alive):
    import ctypes

    class K32:
        @staticmethod
        def OpenProcess(*_a):  # noqa: N802 (a ctypes name)
            return 0

    monkeypatch.setattr(heavy, "_win_k32", lambda: K32)
    monkeypatch.setattr(ctypes, "get_last_error", lambda: last_error)
    assert heavy.pid_alive(4242) is alive


class _FakeProc:
    def __init__(self):
        self.waited = False

    def wait(self, timeout=None):
        self.waited = True
        return 0


class _FakeJob:
    def __init__(self, result=True, raises=False):
        self.result, self.raises, self.terminated, self.closed = result, raises, False, False

    def close(self):
        self.closed = True

    def terminate(self):
        self.terminated = True
        if self.raises:
            raise OSError("boom")
        return self.result


def test_kill_child_tree_skips_taskkill_when_the_job_terminated(monkeypatch):
    calls: list[object] = []
    monkeypatch.setattr(heavy.wt_venv, "kill_tree", lambda proc: calls.append(proc))
    proc, job = _FakeProc(), _FakeJob(result=True)
    heavy._kill_child_tree(proc, job)  # type: ignore[arg-type]
    assert job.terminated and calls == [] and proc.waited


def test_kill_child_tree_falls_back_to_kill_tree_when_the_job_failed_or_raised(monkeypatch):
    for job in (_FakeJob(result=False), _FakeJob(raises=True), None):
        calls: list[object] = []
        monkeypatch.setattr(heavy.wt_venv, "kill_tree", lambda proc, calls=calls: calls.append(proc))
        proc = _FakeProc()
        heavy._kill_child_tree(proc, job)  # type: ignore[arg-type]
        assert calls == [proc], f"kill_tree not reached for {job!r}"
        assert proc.waited


def test_kill_child_tree_still_waits_when_kill_tree_raises(monkeypatch):
    def boom(_proc):
        raise OSError("taskkill missing")

    monkeypatch.setattr(heavy.wt_venv, "kill_tree", boom)
    proc = _FakeProc()
    heavy._kill_child_tree(proc, _FakeJob(result=False))  # type: ignore[arg-type]
    assert proc.waited


def test_ctrl_c_reaches_kill_tree_when_the_child_was_not_assigned_to_the_job(tmp_path, monkeypatch):
    """spawn_in_job says assigned=False: the job is EMPTY, and TerminateJobObject on an empty job still returns TRUE.
    Ctrl-C must not trust that TRUE; it has to fall back to kill_tree."""
    fake_job, proc, calls = _FakeJob(result=True), _FakeProc(), []
    monkeypatch.setattr(heavy, "kill_children_with_me", lambda: None)
    monkeypatch.setattr(heavy.wt_venv, "create_kill_job", lambda: fake_job)
    monkeypatch.setattr(heavy.wt_venv, "spawn_in_job", lambda *_a, **_k: (proc, False))
    monkeypatch.setattr(heavy.wt_venv, "needs_taskkill_fallback", lambda assigned: True)
    monkeypatch.setattr(heavy.wt_venv, "kill_tree", lambda p: calls.append(p))

    def interrupted(_proc):
        raise KeyboardInterrupt

    monkeypatch.setattr(heavy, "_wait_child", interrupted)
    rc = heavy.main(["--slots", "1", "--lock-dir", str(tmp_path), "--", sys.executable, "-c", "pass"])
    assert rc == 130
    assert calls == [proc], "an empty job's TRUE from terminate() suppressed the taskkill fallback"


def test_the_no_job_note_goes_to_stderr(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(heavy, "kill_children_with_me", lambda: "CreateJobObject error 5")
    rc = heavy.main(["--slots", "1", "--lock-dir", str(tmp_path), "--", sys.executable, "-c", "pass"])
    captured = capsys.readouterr()
    assert rc == 0
    assert "heavy: note: no kill-on-close job" in captured.err
    assert "heavy:" not in captured.out


def test_an_oserror_without_errno_prints_its_message_not_errno_none(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(heavy, "kill_children_with_me", lambda: None)

    def refuse(*_a, **_k):
        raise OSError("could not resume the suspended process 99")

    monkeypatch.setattr(heavy.wt_venv, "spawn_in_job", refuse)
    rc = heavy.main(["--slots", "1", "--lock-dir", str(tmp_path), "--", sys.executable, "-c", "pass"])
    err = capsys.readouterr().err
    assert rc == 127
    assert "errno None" not in err
    assert "could not resume the suspended process 99" in err
    assert err.isascii()


def test_an_oserror_with_an_errno_still_prints_it(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(heavy, "kill_children_with_me", lambda: None)
    rc = heavy.main(["--slots", "1", "--lock-dir", str(tmp_path), "--", str(tmp_path / "no-such-program-xyz")])
    err = capsys.readouterr().err
    assert rc == 127 and "errno 2" in err


def test_kill_children_with_me_names_the_windows_error_number(monkeypatch):
    monkeypatch.setattr(heavy, "IS_WINDOWS", True)
    monkeypatch.setattr(heavy.wt_venv, "create_kill_job", lambda: None)
    monkeypatch.setattr(heavy.wt_venv, "last_job_error", lambda: 5)
    reason = heavy.kill_children_with_me()
    assert reason is not None and "error 5" in reason and reason.isascii()


def test_kill_children_with_me_joins_the_shared_job_helper(monkeypatch):
    """No second ctypes job implementation: heavy asks wt_venv.create_kill_job and assigns its own process to it."""
    events: list[str] = []

    class Job:
        def assign_self(self):
            events.append("assign_self")
            return True

    monkeypatch.setattr(heavy.wt_venv, "create_kill_job", lambda: Job())
    monkeypatch.setattr(heavy, "_JOB_HANDLE", None)
    monkeypatch.setattr(heavy, "IS_WINDOWS", True)
    assert heavy.kill_children_with_me() is None
    assert events == ["assign_self"]


def test_kill_children_with_me_reports_why_it_could_not(monkeypatch):
    monkeypatch.setattr(heavy, "IS_WINDOWS", True)
    monkeypatch.setattr(heavy.wt_venv, "create_kill_job", lambda: None)
    assert "CreateJobObject" in (heavy.kill_children_with_me() or "")

    class Job:
        closed = False

        def assign_self(self):
            return False

        def close(self):
            Job.closed = True

    monkeypatch.setattr(heavy.wt_venv, "create_kill_job", lambda: Job())
    assert "AssignProcessToJobObject" in (heavy.kill_children_with_me() or "")
    assert Job.closed
