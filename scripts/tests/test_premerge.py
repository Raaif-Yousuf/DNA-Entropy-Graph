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
    leaked.write_text("see C:\\Users\\realperson\\Downloads\\x.fasta\n", encoding="utf-8", newline="\n")
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
