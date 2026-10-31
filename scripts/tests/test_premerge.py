"""Tests for scripts/premerge.py (#431). Only stub gates and tmp repos: never a real suite."""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import premerge as pm  # noqa: E402


def _run_cli(*args: str, cwd: Path | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "premerge.py"), *args],
        capture_output=True,
        text=True,
        timeout=120,
        cwd=cwd,
        check=False,
    )


def test_self_test_passes():
    proc = _run_cli("--self-test")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS: premerge self-test" in proc.stdout


def test_failing_gate_is_reported_as_fail_not_error_and_exit_one():
    gate = pm.Gate("g", [sys.executable, "-c", "import sys; sys.exit(7)"], Path.cwd())
    result = pm.run_gate(gate)
    assert result.status == pm.FAIL
    summary, code = pm.render_summary([result])
    assert code == 1
    assert "FAILED gates: g" in summary


def test_missing_tool_is_error_never_pass():
    gate = pm.Gate("g", ["no-such-exe-premerge-test"], Path.cwd())
    assert pm.run_gate(gate).status == pm.ERROR


def test_summary_is_ascii_only():
    gate = pm.Gate("g", [sys.executable, "-c", "print('caf\\u00e9'); raise SystemExit(1)"], Path.cwd())
    summary, _ = pm.render_summary([pm.run_gate(gate)])
    summary.encode("ascii")


def test_real_gate_list_covers_every_check_script_and_audit_is_clean():
    gates = pm.build_gates(REPO_ROOT)
    names = {g.name for g in gates}
    for script in (REPO_ROOT / "scripts").glob("check_*.py"):
        assert script.stem in names
    assert pm.audit(REPO_ROOT, gates) == []


def test_every_dotnet_gate_uses_app_as_cwd_and_python_gates_use_the_venv():
    gates = pm.build_gates(REPO_ROOT)
    dotnet = [g for g in gates if g.argv[0] == "dotnet"]
    assert dotnet, "no dotnet gates registered"
    assert all(g.cwd == REPO_ROOT / "app" for g in dotnet)
    venv = str(pm.venv_python_or_marker(REPO_ROOT, None))
    assert all(g.argv[0] == venv for g in gates if g.name.startswith("check_"))


def test_fast_tier_excludes_the_big_suites():
    gates = pm.build_gates(REPO_ROOT)
    fast = {g.name for g in pm.select(gates, full=False, only=[], skip=[])}
    assert "worker-pytest" not in fast
    assert "scripts-tests" not in fast
    assert not any(n.startswith("dotnet-test-") and n != "dotnet-test-Guards.Tests" for n in fast)
    assert "check_user_home_paths" in fast


def test_unknown_only_name_exits_two():
    proc = _run_cli("--only", "check_user_home_pathz")
    assert proc.returncode == 2
    assert "unknown gate" in proc.stderr


@pytest.mark.skipif(shutil.which("git") is None, reason="git not installed")
def test_planted_home_path_is_named_by_the_one_command(tmp_path):
    """The observable from #431: a literal user-home path in a tracked file makes the one
    command fail and name check_user_home_paths."""
    (tmp_path / "scripts").mkdir()
    shutil.copy(SCRIPTS_DIR / "check_user_home_paths.py", tmp_path / "scripts" / "check_user_home_paths.py")
    leaked = tmp_path / "notes.md"
    leaked.write_text("see C:\\" + "Users\\realperson\\Downloads\\x.fasta\n", encoding="utf-8", newline="\n")
    subprocess.run(["git", "init", "-q"], cwd=tmp_path, check=True, capture_output=True)
    subprocess.run(["git", "add", "-A"], cwd=tmp_path, check=True, capture_output=True)

    proc = _run_cli("--root", str(tmp_path), "--python", sys.executable, "--only", "check_user_home_paths", "--fast")
    assert proc.returncode == 1, proc.stdout + proc.stderr
    assert "FAILED gates: check_user_home_paths" in proc.stdout

    leaked.write_text("see %USERPROFILE%\\Downloads\\x.fasta\n", encoding="utf-8", newline="\n")
    subprocess.run(["git", "add", "-A"], cwd=tmp_path, check=True, capture_output=True)
    clean = _run_cli("--root", str(tmp_path), "--python", sys.executable, "--only", "check_user_home_paths", "--fast")
    assert clean.returncode == 0, clean.stdout + clean.stderr
    assert "PREMERGE: PASS" in clean.stdout


# --- pytest gates and known issues (2026-10-02 first full run) -----------------------------------


@pytest.mark.parametrize(
    ("rc", "output", "status"),
    [
        (0, "398 passed in 12.3s", pm.PASS),
        (1, "FAILED tests/test_x.py::test_y - assert 1 == 2\n1 failed, 9 passed in 0.5s", pm.FAIL),
        (1, "2 errors in 0.3s", pm.FAIL),
        (1, "..........  [100%]\nTraceback (most recent call last):\nPermissionError: [WinError 5]", pm.ERROR),
        (1, "398 passed in 12.3s\nPermissionError: pytest-current", pm.ERROR),
        (3, "INTERNALERROR> boom", pm.ERROR),
        (5, "no tests ran in 0.01s", pm.ERROR),
    ],
)
def test_classify_pytest_follows_the_test_outcome(rc, output, status):
    assert pm.classify_pytest(rc, output)[0] == status


def test_a_crash_after_green_tests_is_never_a_pass_and_says_it_is_not_a_test_failure():
    status, detail = pm.classify_pytest(1, "....  [100%]\nPermissionError: x")
    assert status == pm.ERROR and "NOT a test failure" in detail


def test_the_real_pytest_gates_are_flagged_so_they_get_a_private_basetemp():
    gates = {g.name: g for g in pm.build_gates(REPO_ROOT)}
    assert gates["worker-pytest"].pytest and gates["scripts-tests"].pytest


def test_basetemp_stays_short_however_deep_the_log_dir_is(tmp_path):
    """#469 MEASURED 2026-10-02: a basetemp under a deep --log-dir (160+ chars) made `git push` into test_land_pr's temp
    bare remote fail ("unable to create temporary object directory"); the same suite passed from a ~100 char basetemp."""
    deep = tmp_path.joinpath(*(["d" * 30] * 3))  # past the 160 chars that failed in #469
    gate = pm.Gate("pt", [sys.executable, "-c", "import sys; print('ARGV', ' '.join(sys.argv[1:]))"], Path.cwd(), pytest=True)
    results, _, _ = pm.run_all([gate], log_dir=deep, emit=lambda _line: None)
    base = next(line for line in results[0].output.splitlines() if line.startswith("ARGV")).split("--basetemp ")[1].split(" -p")[0]
    assert len(base) <= 100, base
    assert not Path(base).is_relative_to(deep)
    assert (deep / "basetemp.txt").read_text(encoding="utf-8").strip() == str(Path(base).parent)


def test_a_passing_run_removes_its_basetemp_even_with_read_only_git_objects(tmp_path):
    code = (
        "import sys, os, stat; d = sys.argv[sys.argv.index('--basetemp') + 1]; os.makedirs(d); "
        "f = os.path.join(d, 'obj'); open(f, 'w').write('x'); os.chmod(f, stat.S_IREAD); print('1 passed in 0.01s')"
    )
    gate = pm.Gate("pt", [sys.executable, "-c", code], Path.cwd(), pytest=True)
    results, _, rc = pm.run_all([gate], log_dir=tmp_path, emit=lambda _line: None)
    assert rc == 0, results[0].output
    assert not Path((tmp_path / "basetemp.txt").read_text(encoding="utf-8").strip()).exists()


def _pt_gate(name="pt", code="print('1 passed in 0.01s')"):
    return pm.Gate(name, [sys.executable, "-c", code], Path.cwd(), pytest=True)


def test_a_failing_run_keeps_the_basetemp_and_says_where_it_is():
    lines: list[str] = []
    results, _, rc = pm.run_all([_pt_gate(code="import sys; print('1 failed in 0.01s'); sys.exit(1)")], emit=lines.append)
    kept = [x for x in lines if x.startswith("basetemp kept: ")]
    assert rc == 1 and len(kept) == 1
    path = Path(kept[0].split("basetemp kept: ")[1])
    try:
        assert path.exists()
    finally:
        pm._rmtree_force(path)


def test_a_passing_run_says_the_basetemp_was_removed():
    lines: list[str] = []
    pm.run_all([_pt_gate()], emit=lines.append)
    assert any(x.startswith("basetemp removed: ") for x in lines)


def test_an_undeletable_basetemp_does_not_crash_the_run(monkeypatch):
    def boom(_path):
        raise PermissionError("locked by antivirus")

    monkeypatch.setattr(pm, "_rmtree_force", boom)
    lines: list[str] = []
    results, summary, rc = pm.run_all([_pt_gate()], emit=lines.append)
    assert rc == 0 and "PASS" in summary
    kept = [x for x in lines if x.startswith("basetemp kept (could not remove it: PermissionError): ")]
    assert len(kept) == 1
    import shutil as _sh

    _sh.rmtree(kept[0].split("): ", 1)[1], ignore_errors=True)


def test_a_crashing_runner_surfaces_its_own_exception_not_an_unbound_local():
    def runner(_gate):
        raise RuntimeError("the real failure")

    with pytest.raises(RuntimeError, match="the real failure"):
        pm.run_all([_pt_gate()], runner=runner, emit=lambda _l: None)


def test_a_pytest_gate_is_started_with_its_own_basetemp_and_no_cache(tmp_path):
    gate = pm.Gate("pt", [sys.executable, "-c", "import sys; print(' '.join(sys.argv[1:]))"], Path.cwd(), pytest=True)
    result = pm.run_gate(gate, basetemp_root=tmp_path)
    assert str(tmp_path / "pt") in result.output and "no:cacheprovider" in result.output


EOL = "src/A.cs(1,1): error ENDOFLINE: Fix end of line marker. Replace 2 characters with '\n'. [x.csproj]\n"


def test_endofline_is_forgiven_only_on_autocrlf_and_only_when_it_is_the_whole_failure():
    assert "#462" in (pm.forgive_endofline(EOL * 3, 2, True) or "")
    assert pm.forgive_endofline(EOL, 2, False) is None
    assert pm.forgive_endofline(EOL + "src/B.cs(2,2): error IDE0055: Fix formatting.\n", 2, True) is None
    assert pm.forgive_endofline("Build failed", 1, True) is None
    assert pm.forgive_endofline(EOL, 0, True) is None


def _repo_with(tmp_path, files: dict[str, bytes]):
    def run(*args):
        assert subprocess.run(["git", *args], cwd=tmp_path, capture_output=True, check=False).returncode == 0

    run("init", "-q")
    (tmp_path / ".gitattributes").write_bytes(b"* text=auto eol=lf\n*.ps1 text eol=crlf\n")
    for name in files:
        (tmp_path / name).write_bytes(b"x\n")
    run("add", "-A")
    for name, data in files.items():
        (tmp_path / name).write_bytes(data)
    return tmp_path


def test_stale_crlf_detection_ignores_eol_crlf_files_and_sees_a_real_stale_file(tmp_path):
    (tmp_path / "a").mkdir()
    (tmp_path / "b").mkdir()
    only_ps1 = _repo_with(tmp_path / "a", {"x.ps1": b"x\r\n"})
    assert pm._tree_has_stale_crlf(only_ps1) is False  # a .ps1 is CRLF in the tree on purpose
    stale = _repo_with(tmp_path / "b", {"x.ps1": b"x\r\n", "y.cs": b"y\r\n"})
    assert pm._tree_has_stale_crlf(stale) is True


def test_endofline_in_an_lf_tree_is_a_real_failure_not_known_462():
    # the real fix for #462 is an LF tree (`.gitattributes` eol=lf); autocrlf=true alone is not an excuse.
    # ENDOFLINE with no CRLF file in the working tree is a genuine regression and must FAIL.
    assert pm.forgive_endofline(EOL * 3, 2, True, crlf_tree=False) is None
    note = pm.forgive_endofline(EOL * 3, 2, True, crlf_tree=True) or ""
    assert "fix_eol.py" in note  # and the KNOWN note names the one-command repair


def test_the_dotnet_format_gate_has_the_forgive_hook_and_other_gates_do_not():
    gates = {g.name: g for g in pm.build_gates(REPO_ROOT)}
    assert gates["dotnet-format"].forgive is not None
    assert all(g.forgive is None for n, g in gates.items() if n != "dotnet-format")


def test_a_known_issue_exits_zero_but_is_printed_with_its_issue():
    gate = pm.Gate(
        "fmt",
        [sys.executable, "-c", f"import sys; sys.stdout.write({EOL!r}); sys.exit(2)"],
        Path.cwd(),
        forgive=lambda out, rc, cwd: pm.forgive_endofline(out, rc, True),
    )
    summary, code = pm.render_summary([pm.run_gate(gate)])
    assert code == 0 and "KNOWN ISSUE" in summary and "#462" in summary


# --- the stamp the PR hook reads (#451) ------------------------------------------------------------


def _stub_world(tmp_path, monkeypatch, exit_code=0):
    subprocess.run(["git", "init", "-q"], cwd=tmp_path, check=True, capture_output=True)
    for key, value in (("user.name", "T"), ("user.email", "t@example.com")):
        subprocess.run(["git", "config", key, value], cwd=tmp_path, check=True, capture_output=True)
    (tmp_path / "scripts").mkdir()
    (tmp_path / "a.txt").write_text("1\n", encoding="utf-8", newline="\n")
    subprocess.run(["git", "add", "-A"], cwd=tmp_path, check=True, capture_output=True)
    subprocess.run(["git", "commit", "-q", "-m", "c"], cwd=tmp_path, check=True, capture_output=True)
    gate = pm.Gate("only-gate", [sys.executable, "-c", f"import sys; sys.exit({exit_code})"], tmp_path)
    monkeypatch.setattr(pm, "build_gates", lambda *a, **k: [gate])
    return tmp_path / ".git" / "premerge-stamp.json"


def test_a_green_unfiltered_run_writes_the_stamp(tmp_path, monkeypatch):
    stamp = _stub_world(tmp_path, monkeypatch)
    assert pm.main(["--root", str(tmp_path), "--fast"]) == 0
    assert stamp.is_file()


def test_a_red_or_filtered_run_writes_no_stamp(tmp_path, monkeypatch):
    stamp = _stub_world(tmp_path, monkeypatch, exit_code=1)
    assert pm.main(["--root", str(tmp_path), "--fast"]) == 1
    assert not stamp.exists()
    monkeypatch.setattr(pm, "build_gates", lambda *a, **k: [pm.Gate("only-gate", [sys.executable, "-c", "pass"], tmp_path)])
    assert pm.main(["--root", str(tmp_path), "--fast", "--only", "only-gate"]) == 0
    assert not stamp.exists()


def test_scripts_format_is_gated_in_premerge_and_in_ci_docs():
    """The bulk `ruff format scripts` (r2) is only worth keeping if a drifting file goes red in both places."""
    found = next(g for g in pm.build_gates(REPO_ROOT) if g.name == "ruff-scripts-format")
    assert list(found.argv) == ["uvx", "ruff", "format", "--check", "scripts"]
    ci = (REPO_ROOT / ".github" / "workflows" / "ci-docs.yml").read_text(encoding="utf-8")
    assert "uvx ruff format --check scripts" in ci


def test_scripts_are_ruff_formatted_now():
    """Vacuity guard: the tree the gate checks is clean today, via the same command."""
    proc = subprocess.run(["uvx", "ruff", "format", "--check", "scripts"], cwd=REPO_ROOT, capture_output=True, text=True, check=False)
    assert proc.returncode == 0, proc.stdout[-800:]


# --- a pytest gate that times out (issue #925) --------------------------------------------------


def test_classify_pytest_timeout_counts_progress_characters_and_names_failures():
    detail = pm.classify_pytest_timeout(900.0, "...s..  [ 10%]\n..F.E.  [ 20%]\n")
    assert detail == (
        "timed out after 900s with 9 passed and 2 failed so far (NOT a pass; the suite did not finish AND tests had already failed)"
    )


def test_classify_pytest_timeout_with_no_output_says_zero_passed_and_zero_failed():
    detail = pm.classify_pytest_timeout(5.0, "")
    assert "timed out after 5s with 0 passed and 0 failed so far" in detail


def test_a_timed_out_non_pytest_gate_keeps_the_plain_message():
    gate = pm.Gate("g", [sys.executable, "-c", "import time; time.sleep(60)"], Path.cwd(), timeout=2.0)
    result = pm.run_gate(gate)
    assert result.status == pm.ERROR and result.detail == "timed out after 2s"


# --- review follow-ups on the timeout path (issue #925) -----------------------------------------


def test_an_interrupted_gate_kills_its_tree_and_the_interrupt_propagates(monkeypatch):
    killed: list[int] = []
    real_popen = subprocess.Popen

    class Interrupted(real_popen):  # type: ignore[type-arg, misc]
        def communicate(self, input=None, timeout=None):
            raise KeyboardInterrupt

    def record_and_kill(proc):
        killed.append(proc.pid)
        proc.kill()
        return True

    monkeypatch.setattr(pm.subprocess, "Popen", Interrupted)
    monkeypatch.setattr(pm, "_kill_tree", record_and_kill)
    gate = pm.Gate("g", [sys.executable, "-c", "import time; time.sleep(30)"], Path.cwd())
    with pytest.raises(KeyboardInterrupt):
        pm.run_gate(gate)
    assert len(killed) == 1


def test_kill_tree_never_raises_and_reports_failure(monkeypatch):
    class Dummy:
        pid = 2**22 - 3

        def kill(self):
            raise OSError("gone")

    def boom_timeout(*_a, **_k):
        raise subprocess.TimeoutExpired("taskkill", 1)

    def boom_missing(*_a, **_k):
        raise FileNotFoundError("taskkill")

    for boom in (boom_timeout, boom_missing):
        monkeypatch.setattr(pm.subprocess, "run", boom)
        monkeypatch.setattr(pm.os, "killpg", boom, raising=False)
        assert pm._kill_tree(Dummy()) is False  # type: ignore[arg-type]


def test_a_kill_that_did_not_work_says_the_tree_may_have_survived_and_output_was_lost(monkeypatch):
    monkeypatch.setattr(pm, "_kill_tree", lambda _proc: False)
    monkeypatch.setattr(pm, "KILL_GRACE", 1.0)
    code = "import time\nprint('....', flush=True)\ntime.sleep(8)\n"
    gate = pm.Gate("pt", [sys.executable, "-c", code], Path.cwd(), timeout=2.0, pytest=True)
    result = pm.run_gate(gate)
    assert result.status == pm.ERROR
    assert "may have survived" in result.detail
    assert "unknown" in result.detail
    assert "0 passed and 0 failed" not in result.detail


def test_pid_alive_refuses_to_say_dead_when_tasklist_itself_fails(monkeypatch):
    if pm.os.name != "nt":
        pytest.skip("tasklist is Windows only")

    class Bad:
        returncode = 1
        stdout = b""

    monkeypatch.setattr(pm.subprocess, "run", lambda *_a, **_k: Bad())
    with pytest.raises(RuntimeError, match="tasklist"):
        pm._pid_alive(4)


# --- round 3 of the timeout path: load-proof stubs, signals, zombies, separate pipes (#925) ------

SLOW_START_TIMEOUT = 12.0  # a stub must write its ready file inside this; the stubs sleep forever so the timeout still fires


def _stub_code(ready: Path, progress: str = "", spawn_sleeper_pid_file: Path | None = None, sleeper_seconds: int = 600) -> str:
    """Python source for a gate stub: optionally spawns a sleeper holding the pipe, prints progress, writes a ready
    file, then sleeps. Nothing here depends on the interpreter starting quickly: the tests wait for the ready file."""
    lines = ["import subprocess, sys, time"]
    if spawn_sleeper_pid_file is not None:
        lines.append(f"p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep({sleeper_seconds})'])")
        lines.append(f"open({str(spawn_sleeper_pid_file)!r}, 'w').write(str(p.pid))")
    if progress:
        lines.append(f"sys.stdout.write({progress!r} + chr(10)); sys.stdout.flush()")
    lines.append(f"open({str(ready)!r}, 'w').write('ready')")
    lines.append("time.sleep(600)")
    return "\n".join(lines) + "\n"


def _wait_until(predicate, seconds: float = 5.0) -> bool:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.2)
    return predicate()


def test_a_timed_out_pytest_gate_is_error_with_partial_counts_exactly_once(tmp_path):
    ready = tmp_path / "ready"
    gate = pm.Gate("pt", [sys.executable, "-c", _stub_code(ready, "....F..  [ 50%]")], Path.cwd(), timeout=SLOW_START_TIMEOUT, pytest=True)
    result = pm.run_gate(gate)
    assert ready.exists(), "the stub did not start inside the timeout; raise SLOW_START_TIMEOUT"
    assert result.status == pm.ERROR
    assert "timed out after 12s with 6 passed and 1 failed so far" in result.detail
    assert result.output.count("....F..") == 1


def test_a_timeout_kills_the_grandchild_too_and_returns_promptly(tmp_path):
    ready, marker = tmp_path / "ready", tmp_path / "grandchild.pid"
    gate = pm.Gate("g", [sys.executable, "-c", _stub_code(ready, spawn_sleeper_pid_file=marker)], Path.cwd(), timeout=SLOW_START_TIMEOUT)
    started = time.monotonic()
    result = pm.run_gate(gate)
    assert time.monotonic() - started < 120
    assert ready.exists(), "the stub did not start inside the timeout; raise SLOW_START_TIMEOUT"
    assert result.status == pm.ERROR
    pid = int(marker.read_text())
    assert _wait_until(lambda: not pm._pid_alive(pid), seconds=5.0), f"grandchild {pid} survived the timeout"


def _kill_pid_quietly(pid: int) -> None:
    """Test cleanup only: stop exactly this PID (never by image name)."""
    cmd = ["taskkill", "/F", "/PID", str(pid)] if pm.os.name == "nt" else ["kill", "-9", str(pid)]
    subprocess.run(cmd, capture_output=True, check=False, timeout=30)


def test_a_timeout_kills_a_detached_grandchild_whose_parent_already_exited(tmp_path):
    """#930. The direct child exits 0 at once; its DETACHED grandchild keeps the output pipe open. taskkill /T cannot
    find it (its parent is gone), so only a job object (Windows) or the process group (POSIX) can end it.
    Limit: a job does not cover a process started with CREATE_BREAKAWAY_FROM_JOB (when the job allows breakaway)."""
    ready, marker = tmp_path / "ready", tmp_path / "grandchild.pid"
    code = (
        "import subprocess, sys\n"
        "kw = {'creationflags': 0x8 | 0x200} if sys.platform == 'win32' else {}\n"
        "p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], stdout=sys.stdout, stderr=sys.stderr, **kw)\n"
        f"open({str(marker)!r}, 'w').write(str(p.pid))\n"
        f"open({str(ready)!r}, 'w').write('ready')\n"
        "sys.exit(0)\n"
    )
    gate = pm.Gate("g", [sys.executable, "-c", code], Path.cwd(), timeout=SLOW_START_TIMEOUT)
    result = pm.run_gate(gate)
    assert ready.exists(), "the stub did not start inside the timeout; raise SLOW_START_TIMEOUT"
    pid = int(marker.read_text())
    try:
        assert result.status == pm.ERROR
        assert _wait_until(lambda: not pm._pid_alive(pid), seconds=5.0), f"detached grandchild {pid} survived the timeout"
    finally:
        _kill_pid_quietly(pid)


def test_a_child_that_exited_but_left_its_pipe_open_reports_its_exit_code(tmp_path, monkeypatch):
    # The job would kill the sleeper and close the pipe, so the output_lost / tree_may_survive path is only reachable when the job
    # kill does nothing (a breakaway process, a refused TerminateJobObject): stub it, and the job-close at the end cleans up.
    monkeypatch.setattr(pm.wt_venv.KillJob, "terminate", lambda self: False)
    monkeypatch.setattr(pm, "KILL_GRACE", 1.0)
    ready, marker = tmp_path / "ready", tmp_path / "sleeper.pid"
    code = (
        "import subprocess, sys\n"
        f"p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(25)'])\n"
        f"open({str(marker)!r}, 'w').write(str(p.pid))\n"
        f"open({str(ready)!r}, 'w').write('ready')\n"
        "sys.exit(0)\n"
    )
    gate = pm.Gate("g", [sys.executable, "-c", code], Path.cwd(), timeout=SLOW_START_TIMEOUT)
    result = pm.run_gate(gate)
    assert ready.exists(), "the stub did not start inside the timeout; raise SLOW_START_TIMEOUT"
    assert result.status == pm.ERROR
    assert "child exited 0 but its pipe stayed open" in result.detail
    if pm.os.name == "nt":  # POSIX has no job: killpg really kills the sleeper there
        assert "output after the kill was lost" in result.detail
        assert "may have survived" in result.detail


@pytest.mark.skipif(pm.os.name != "nt", reason="the job object is Windows only")
def test_kill_tree_reports_failure_when_neither_the_job_nor_taskkill_worked(monkeypatch):
    class Done:
        returncode = 1

    class Job:
        def terminate(self):
            return False

    class Dummy:
        pid = 2**22 - 7

        def kill(self):
            pass

    monkeypatch.setattr(pm.subprocess, "run", lambda *_a, **_k: Done())
    monkeypatch.setitem(pm._GATE_JOBS, Dummy.pid, Job())
    assert pm._kill_tree(Dummy()) is False  # type: ignore[arg-type]


@pytest.mark.skipif(pm.os.name != "nt", reason="the job object is Windows only")
def test_kill_tree_counts_a_terminated_job_as_success_even_when_taskkill_finds_nothing(monkeypatch):
    class Done:
        returncode = 128  # taskkill: no such process

    class Job:
        def terminate(self):
            return True

    class Dummy:
        pid = 2**22 - 9

        def kill(self):
            pass

    monkeypatch.setattr(pm.subprocess, "run", lambda *_a, **_k: Done())
    monkeypatch.setitem(pm._GATE_JOBS, Dummy.pid, Job())
    assert pm._kill_tree(Dummy()) is True  # type: ignore[arg-type]


def test_the_real_scripts_tests_gate_gets_the_long_timeout_not_the_default():
    gates = {g.name: g for g in pm.build_gates(REPO_ROOT)}
    assert gates["scripts-tests"].timeout == pm.LONG_TIMEOUT
    assert pm.LONG_TIMEOUT > pm.DEFAULT_TIMEOUT


def test_kill_tree_still_kills_the_direct_child_when_interrupted_during_taskkill(monkeypatch):
    killed: list[bool] = []

    class Dummy:
        pid = 2**22 - 5

        def kill(self):
            killed.append(True)

    def interrupt(*_a, **_k):
        raise KeyboardInterrupt

    monkeypatch.setattr(pm.subprocess, "run", interrupt)
    monkeypatch.setattr(pm.os, "killpg", interrupt, raising=False)
    with pytest.raises(KeyboardInterrupt):
        pm._kill_tree(Dummy())  # type: ignore[arg-type]
    assert killed == [True]


def test_a_sigterm_handler_turns_the_signal_into_the_interrupt_the_gate_runner_cleans_up_on():
    with pytest.raises(KeyboardInterrupt):
        pm._raise_keyboard_interrupt(15, None)


def test_install_termination_handlers_registers_sigterm_and_sighup_where_present(monkeypatch):
    import signal

    registered: dict[int, object] = {}
    monkeypatch.setattr(pm.signal, "signal", lambda sig, handler: registered.__setitem__(sig, handler))
    pm.install_termination_handlers()
    assert registered[signal.SIGTERM] is pm._raise_keyboard_interrupt
    if hasattr(signal, "SIGHUP"):
        assert registered[signal.SIGHUP] is pm._raise_keyboard_interrupt


def test_main_installs_the_termination_handlers(monkeypatch):
    calls: list[str] = []
    monkeypatch.setattr(pm, "install_termination_handlers", lambda: calls.append("installed"))
    pm.main(["--root", str(REPO_ROOT), "--list"])
    assert calls == ["installed"]


def test_a_lost_output_run_still_waits_for_the_child_so_it_is_not_left_a_zombie(monkeypatch):
    waits: list[object] = []
    real_popen = subprocess.Popen

    class Recording(real_popen):  # type: ignore[type-arg, misc]
        def wait(self, timeout=None):
            waits.append(timeout)
            return super().wait(timeout=timeout)

    monkeypatch.setattr(pm.subprocess, "Popen", Recording)
    monkeypatch.setattr(pm, "_kill_tree", lambda _proc: False)
    monkeypatch.setattr(pm, "KILL_GRACE", 1.0)
    gate = pm.Gate("g", [sys.executable, "-c", "import time; time.sleep(6)"], Path.cwd(), timeout=1.0)
    result = pm.run_gate(gate)
    assert "unknown" in result.detail
    assert any(isinstance(t, float) for t in waits)


def test_pid_alive_treats_a_zombie_as_dead_on_posix(monkeypatch, tmp_path):
    if pm.os.name == "nt":
        pytest.skip("the /proc zombie check is POSIX only")
    stat = "123 (my (weird) name) Z 1 123 123 0 -1 0"
    real_open = open

    def fake_open(path, *a, **k):
        if str(path) == "/proc/123/stat":
            f = tmp_path / "stat"
            f.write_text(stat)
            return real_open(f, *a, **k)
        return real_open(path, *a, **k)

    monkeypatch.setattr("builtins.open", fake_open)
    monkeypatch.setattr(pm.os, "kill", lambda *_a: None)
    assert pm._pid_alive(123) is False


def test_stdout_and_stderr_stay_separate_pipes_joined_stdout_then_stderr():
    code = "import sys; sys.stderr.write('ERRTEXT'); sys.stderr.flush(); sys.stdout.write('OUTTEXT'); sys.stdout.flush()"
    gate = pm.Gate("g", [sys.executable, "-c", code], Path.cwd())
    result = pm.run_gate(gate)
    assert result.output == "OUTTEXTERRTEXT"


def test_a_timeout_keeps_partial_output_from_both_streams(tmp_path):
    ready = tmp_path / "ready"
    code = (
        "import sys, time\n"
        "sys.stdout.write('FROMOUT\\n'); sys.stdout.flush()\n"
        "sys.stderr.write('FROMERR\\n'); sys.stderr.flush()\n"
        f"open({str(ready)!r}, 'w').write('ready')\n"
        "time.sleep(600)\n"
    )
    result = pm.run_gate(pm.Gate("g", [sys.executable, "-c", code], Path.cwd(), timeout=SLOW_START_TIMEOUT))
    assert ready.exists(), "the stub did not start inside the timeout; raise SLOW_START_TIMEOUT"
    assert "FROMOUT" in result.output and "FROMERR" in result.output


def _snapshot_termination_handlers():
    """Return a callable that puts SIGTERM/SIGHUP back as they are now."""
    import signal

    names = [n for n in ("SIGTERM", "SIGHUP") if hasattr(signal, n)]
    saved = {n: signal.getsignal(getattr(signal, n)) for n in names}

    def restore() -> None:
        for n, handler in saved.items():
            signal.signal(getattr(signal, n), handler)

    return restore


@pytest.fixture(autouse=True)
def _restore_termination_handlers():
    """pm.main() installs SIGTERM/SIGHUP handlers; never let them leak into the rest of the pytest session."""
    restore = _snapshot_termination_handlers()
    yield
    restore()


def test_a_main_call_installs_the_handlers_and_the_restore_puts_them_back():
    """One test, not an order-dependent pair: under xdist the second half could land on another worker and pass vacuously (#959)."""
    import signal

    restore = _snapshot_termination_handlers()
    pm.main(["--root", str(REPO_ROOT), "--list"])
    assert signal.getsignal(signal.SIGTERM) is pm._raise_keyboard_interrupt
    restore()
    assert signal.getsignal(signal.SIGTERM) is not pm._raise_keyboard_interrupt


# --- #959: the scripts-tests gate runs in parallel (pytest-xdist) --------------------------------------------


def test_scripts_test_workers_scale_with_cpus_and_stay_capped():
    assert pm.scripts_test_workers(cpus=16) == pm.SCRIPTS_TESTS_MAX_WORKERS
    assert pm.scripts_test_workers(cpus=4) == 2
    assert pm.scripts_test_workers(cpus=1) == 2  # never fewer than two, or xdist is only overhead
    assert pm.scripts_test_workers(cpus=None) == 2


def test_the_real_scripts_tests_gate_is_parallel_and_installs_xdist():
    gate = {g.name: g for g in pm.build_gates(REPO_ROOT)}["scripts-tests"]
    cmd = gate.argv
    assert "pytest-xdist" in cmd and cmd[cmd.index("-n") + 1] == str(pm.scripts_test_workers())
    assert cmd[cmd.index("--with") + 1] == "pytest"  # the non-xdist `--with`s are untouched


def test_the_scripts_tests_ci_job_uses_the_same_parallel_command_as_premerge():
    text = (REPO_ROOT / ".github" / "workflows" / "ci-docs.yml").read_text(encoding="utf-8")
    assert "--with pytest-xdist" in text and "pytest scripts/tests -q -n " in text


@pytest.mark.parametrize(
    ("rc", "output", "status"),
    [
        (0, "created: 8/8 workers\n8 workers [1874 items]\n1874 passed, 19 skipped in 190.02s (0:03:10)", pm.PASS),
        (1, "[gw3] FAILED scripts/tests/test_x.py::test_y\n1 failed, 1873 passed in 190.02s", pm.FAIL),
        (1, "worker 'gw2' crashed while running 'scripts/tests/test_x.py::test_y'\n....  [ 40%]", pm.ERROR),
    ],
)
def test_classify_pytest_reads_xdist_output(rc, output, status):
    assert pm.classify_pytest(rc, output)[0] == status


def test_a_timed_out_xdist_gate_still_counts_progress():
    out = "......F...  [ 12%]\n..........  [ 24%]\n"
    assert "timed out after 600s with 19 passed and 1 failed" in pm.classify_pytest_timeout(600, out)


# ---- #1001: a worktree with no worker/.venv of its own ----------------------------------------------------------------


def _git(cwd: Path, *args: str) -> None:
    subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@t", *args], cwd=cwd, check=True, capture_output=True)


@pytest.fixture
def fake_worktree(tmp_path: Path) -> tuple[Path, Path, Path]:
    """(primary checkout with a fake shared venv python, a real linked worktree of it with no venv, the fake python)."""
    primary = tmp_path / "primary"
    primary.mkdir()
    _git(primary, "init", "-q")
    _git(primary, "commit", "-q", "--allow-empty", "-m", "init")
    rel = Path("Scripts") / "python.exe" if sys.platform == "win32" else Path("bin") / "python"
    shared = primary / "worker" / ".venv" / rel
    shared.parent.mkdir(parents=True)
    shared.write_bytes(b"fake python\n")
    wt = tmp_path / "wt"
    _git(primary, "worktree", "add", "-q", str(wt), "-b", "lane")
    (wt / "worker" / "src").mkdir(parents=True, exist_ok=True)
    return primary, wt, shared


def test_find_venv_python_prefers_the_checkouts_own_venv(fake_worktree):
    primary, _, shared = fake_worktree
    assert pm.find_venv_python(primary) == shared


def test_find_venv_python_falls_back_to_the_primary_checkouts_venv_from_a_worktree(fake_worktree):
    _, wt, shared = fake_worktree
    assert not (wt / "worker" / ".venv").exists()
    found = pm.find_venv_python(wt)
    assert found is not None and found.resolve() == shared.resolve()


def test_find_venv_python_is_none_when_neither_checkout_has_a_venv(fake_worktree):
    _, wt, shared = fake_worktree
    shared.unlink()
    assert pm.find_venv_python(wt) is None


def test_the_fallback_is_announced_and_an_own_venv_or_override_is_not(fake_worktree):
    primary, wt, _ = fake_worktree
    note = pm.venv_fallback_note(wt, None)
    assert note is not None and "primary checkout" in note and "\n" not in note
    assert pm.venv_fallback_note(primary, None) is None
    assert pm.venv_fallback_note(wt, "C:/x/python.exe") is None


def test_gates_get_this_checkouts_worker_src_first_on_pythonpath(tmp_path):
    src = str((tmp_path / "worker" / "src").resolve())
    sep = os.pathsep
    assert pm.pythonpath_for(tmp_path, {}) == src
    assert pm.pythonpath_for(tmp_path, {"PYTHONPATH": "A"}) == src + sep + "A"
    # already there: moved to the front, not duplicated
    assert pm.pythonpath_for(tmp_path, {"PYTHONPATH": f"A{sep}{src}"}).split(sep) == [src, "A"]


def test_main_in_a_worktree_prints_the_fallback_and_runs_the_gates_with_the_worktree_src(fake_worktree, monkeypatch, capsys):
    _, wt, shared = fake_worktree
    seen: dict[str, str] = {}

    def fake_run_all(gates, log_dir=None, runner=None, emit=print, env=None):
        seen["PYTHONPATH"] = (env or {}).get("PYTHONPATH", "")
        return [], "SUMMARY", 0

    monkeypatch.setattr(pm, "run_all", fake_run_all)
    monkeypatch.setattr(pm, "_write_stamp", lambda *a, **k: None)
    monkeypatch.setenv("PYTHONPATH", "KEEP")
    before = dict(os.environ)
    (wt / "scripts").mkdir(exist_ok=True)
    (wt / "scripts" / "check_stub.py").write_text("print('ok')\n", encoding="utf-8", newline="\n")
    pm.main(["--root", str(wt), "--only", "check_stub"])
    captured = capsys.readouterr()
    out = captured.out
    assert "ERROR" not in captured.err, captured.err
    assert "primary checkout" in out
    assert seen["PYTHONPATH"].split(os.pathsep) == [str((wt / "worker" / "src").resolve()), "KEEP"]
    assert str(shared) in out  # the python it announced is the primary's
    assert dict(os.environ) == before  # main() builds the gates' env locally; this process is not mutated


def test_run_all_hands_the_given_env_to_every_gate_and_leaves_os_environ_alone(tmp_path, monkeypatch):
    monkeypatch.delenv("PYTHONPATH", raising=False)
    code = "import os; print(os.environ.get('PYTHONPATH', 'unset'))"
    gate = pm.Gate("echo-env", [sys.executable, "-c", code], tmp_path)
    results, _, _ = pm.run_all([gate], env={**os.environ, "PYTHONPATH": "FROM_ENV"}, emit=lambda *_: None)
    assert "FROM_ENV" in results[0].output
    assert "PYTHONPATH" not in os.environ


def test_git_is_asked_once_per_root_and_with_a_timeout(fake_worktree, monkeypatch):
    _, wt, _ = fake_worktree
    calls: list[dict] = []
    real = subprocess.run

    def spy(argv, *a, **k):
        if argv[:2] == ["git", "rev-parse"]:
            calls.append(k)
        return real(argv, *a, **k)

    monkeypatch.setattr(subprocess, "run", spy)
    pm._primary_venv_dir.cache_clear()
    pm.find_venv_python(wt)
    pm.find_venv_python(wt)
    pm.venv_fallback_note(wt, None)
    assert len(calls) == 1
    assert calls[0].get("timeout")


def test_pythonpath_dedupe_ignores_case_and_relative_spelling(tmp_path, monkeypatch):
    src = (tmp_path / "worker" / "src").resolve()
    monkeypatch.chdir(tmp_path)
    spelled = os.path.join("worker", "..", "worker", "src")  # relative, same directory
    out = pm.pythonpath_for(tmp_path, {"PYTHONPATH": os.pathsep.join([spelled, "A"])})
    assert out.split(os.pathsep) == [str(src), "A"]


# --- #840: a hung test in scripts/tests fails fast with a traceback (pytest-timeout) ------------------------


def test_the_scripts_tests_gate_installs_pytest_timeout_and_bounds_every_test():
    cmd = {g.name: g for g in pm.build_gates(REPO_ROOT)}["scripts-tests"].argv
    assert "pytest-timeout" in cmd
    assert f"--timeout={pm.SCRIPTS_TEST_TIMEOUT}" in cmd
    assert pm.SCRIPTS_TEST_TIMEOUT >= 120, "tighter than the slowest legitimate test under load (MEASURED 2026-10-29: 34 s)"
    assert pm.SCRIPTS_TEST_TIMEOUT < pm.LONG_TIMEOUT, "a per-test bound looser than the whole gate would never fire first"


def test_the_scripts_tests_ci_job_has_the_same_per_test_timeout_as_premerge():
    text = (REPO_ROOT / ".github" / "workflows" / "ci-docs.yml").read_text(encoding="utf-8")
    assert "--with pytest-timeout" in text and f"--timeout={pm.SCRIPTS_TEST_TIMEOUT}" in text
    assert "pytest scripts/tests -q -n " in text, "the xdist assertion above must keep matching"


@pytest.mark.skipif(shutil.which("uv") is None, reason="uv not installed")
def test_a_hung_test_under_the_gates_flags_fails_fast_and_names_the_test(tmp_path):
    """The behaviour the flags exist for, not just their spelling: a test that sleeps forever is killed with a traceback."""
    (tmp_path / "test_hang.py").write_text(
        "import time\n\n\ndef test_it_hangs_forever():\n    time.sleep(600)\n", encoding="utf-8", newline="\n"
    )
    started = time.monotonic()
    proc = subprocess.run(
        [
            "uv",
            "run",
            "--with",
            "pytest",
            "--with",
            "pytest-timeout",
            "python",
            "-m",
            "pytest",
            "test_hang.py",
            "-q",
            "-p",
            "no:cacheprovider",
            "--timeout=3",
        ],
        cwd=tmp_path,
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
    )
    elapsed = time.monotonic() - started
    out = proc.stdout + proc.stderr
    assert proc.returncode != 0
    assert elapsed < 100, f"did not fail fast: {elapsed:.0f}s"
    assert "Timeout" in out and "test_it_hangs_forever" in out and "time.sleep" in out, out[-1500:]


# --- review round (#930): terminate() reports the API result; the failure path closes pipes ------


def test_kill_job_terminate_returns_the_api_result():
    class K32:
        def __init__(self, result):
            self.result = result

        def TerminateJobObject(self, _handle, _code):  # noqa: N802
            return self.result

    assert pm.wt_venv.KillJob(1, K32(1)).terminate() is True
    assert pm.wt_venv.KillJob(1, K32(0)).terminate() is False
    closed = pm.wt_venv.KillJob(1, K32(1))
    closed._handle = None
    assert closed.terminate() is False  # nothing to terminate


def test_needs_taskkill_fallback_is_only_for_an_unassigned_windows_child():
    assert pm.wt_venv.needs_taskkill_fallback(False) is (pm.os.name == "nt")
    assert pm.wt_venv.needs_taskkill_fallback(True) is False


@pytest.mark.skipif(pm.os.name != "nt", reason="the suspended-spawn failure path is Windows only")
def test_spawn_in_job_closes_the_pipes_of_a_child_it_kills_on_failure(monkeypatch):
    made: list[subprocess.Popen] = []
    real = subprocess.Popen

    def recording(*a, **k):
        proc = real(*a, **k)
        made.append(proc)
        return proc

    class BrokenJob:
        def assign(self, _proc):
            raise RuntimeError("assign failed")

    monkeypatch.setattr(pm.wt_venv.subprocess, "Popen", recording)
    with pytest.raises(RuntimeError):
        pm.wt_venv.spawn_in_job(
            [sys.executable, "-c", "pass"],
            dict(pm.os.environ),
            Path.cwd(),
            BrokenJob(),  # type: ignore[arg-type]
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    (proc,) = made
    assert proc.stdout is not None and proc.stderr is not None
    assert proc.stdout.closed and proc.stderr.closed
    proc.wait(timeout=10)
