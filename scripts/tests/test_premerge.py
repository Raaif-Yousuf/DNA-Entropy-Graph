"""Tests for scripts/premerge.py (#431). Only stub gates and tmp repos: never a real suite."""

from __future__ import annotations

import shutil
import subprocess
import sys
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

    proc = _run_cli(
        "--root", str(tmp_path), "--python", sys.executable, "--only", "check_user_home_paths", "--fast"
    )
    assert proc.returncode == 1, proc.stdout + proc.stderr
    assert "FAILED gates: check_user_home_paths" in proc.stdout

    leaked.write_text("see %USERPROFILE%\\Downloads\\x.fasta\n", encoding="utf-8", newline="\n")
    subprocess.run(["git", "add", "-A"], cwd=tmp_path, check=True, capture_output=True)
    clean = _run_cli(
        "--root", str(tmp_path), "--python", sys.executable, "--only", "check_user_home_paths", "--fast"
    )
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


def test_the_dotnet_format_gate_has_the_forgive_hook_and_other_gates_do_not():
    gates = {g.name: g for g in pm.build_gates(REPO_ROOT)}
    assert gates["dotnet-format"].forgive is not None
    assert all(g.forgive is None for n, g in gates.items() if n != "dotnet-format")


def test_a_known_issue_exits_zero_but_is_printed_with_its_issue():
    gate = pm.Gate("fmt", [sys.executable, "-c", f"import sys; sys.stdout.write({EOL!r}); sys.exit(2)"], Path.cwd(),
                   forgive=lambda out, rc, cwd: pm.forgive_endofline(out, rc, True))
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
