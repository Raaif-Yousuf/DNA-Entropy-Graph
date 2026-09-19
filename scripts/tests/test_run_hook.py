"""Tests for scripts/hooks/run_hook.py -- the fail-closed PreToolUse dispatcher.

This is the file that decides whether the other four guards actually run, so
it gets the most suspicious test suite in scripts/hooks/, not the least: a
direct pytest arm for every failure mode `run()` itself defines, plus a
subprocess-level (real stdin/stdout, real process) test for each one too, so
the exact contract `.claude/settings.json` actually invokes is proven, not
just the importable function.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
HOOKS_DIR = SCRIPTS_DIR / "hooks"
sys.path.insert(0, str(HOOKS_DIR))

import run_hook as rh  # noqa: E402


def _write_guard(hooks_dir: Path, name: str, source: str) -> Path:
    p = hooks_dir / f"{name}.py"
    p.write_text(source, encoding="utf-8")
    return p


def _is_deny(out: str) -> bool:
    try:
        data = json.loads(out)
    except json.JSONDecodeError:
        return False
    return data.get("hookSpecificOutput", {}).get("permissionDecision") == "deny"


def _capture(guard_name: str, hooks_dir: Path, timeout: float = 3.0, stdin_payload: str = "{}") -> str:
    import io

    buf = io.StringIO()
    old_stdout = sys.stdout
    sys.stdout = buf
    try:
        rc = rh.run(guard_name, stdin_payload, timeout, hooks_dir=hooks_dir)
    finally:
        sys.stdout = old_stdout
    assert rc == 0
    return buf.getvalue()


# ---------------------------------------------------------------------------
# run_hook.py's own self-test, bridged into pytest
# ---------------------------------------------------------------------------

def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "run_hook.py"), "--self-test"],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "run_hook.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "--timeout" in proc.stdout
    assert "--self-test" in proc.stdout


# ---------------------------------------------------------------------------
# run(): every arm, in-process (fast, precise)
# ---------------------------------------------------------------------------

def test_missing_guard_file_denies(tmp_path):
    out = _capture("does-not-exist-at-all", tmp_path)
    assert _is_deny(out)
    assert "does-not-exist-at-all" in out
    assert "missing" in out.lower()


def test_guard_crash_denies_with_stderr_in_reason(tmp_path):
    _write_guard(tmp_path, "crashes", "import sys\nsys.stderr.write('boom\\n')\nsys.exit(3)\n")
    out = _capture("crashes", tmp_path)
    assert _is_deny(out)
    assert "boom" in out
    assert "3" in out  # the exit code


def test_guard_deny_passes_through_unchanged(tmp_path):
    _write_guard(
        tmp_path, "denies",
        "import json, sys\n"
        "json.dump({'hookSpecificOutput': {'hookEventName': 'PreToolUse', "
        "'permissionDecision': 'deny', 'permissionDecisionReason': 'no'}}, sys.stdout)\n",
    )
    out = _capture("denies", tmp_path)
    data = json.loads(out)
    assert data["hookSpecificOutput"]["permissionDecision"] == "deny"
    assert data["hookSpecificOutput"]["permissionDecisionReason"] == "no"


def test_guard_silent_produces_no_output_and_no_deny(tmp_path):
    _write_guard(tmp_path, "silent", "import sys\nsys.exit(0)\n")
    out = _capture("silent", tmp_path)
    assert out == ""


def test_guard_timeout_denies(tmp_path):
    _write_guard(tmp_path, "hangs", "import time\ntime.sleep(30)\n")
    out = _capture("hangs", tmp_path, timeout=0.5)
    assert _is_deny(out)
    assert "hangs" in out
    assert "timed out" in out.lower() or "did not finish" in out.lower()


def test_guard_nonjson_output_denies(tmp_path):
    _write_guard(tmp_path, "garbage", "import sys\nsys.stdout.write('not json {{{')\nsys.exit(0)\n")
    out = _capture("garbage", tmp_path)
    assert _is_deny(out)


def test_stdin_payload_genuinely_reaches_the_guard(tmp_path):
    _write_guard(
        tmp_path, "echo_and_fail",
        "import sys\nsys.stderr.write('saw:' + sys.stdin.read())\nsys.exit(1)\n",
    )
    out = _capture("echo_and_fail", tmp_path, stdin_payload='{"marker": "unique-12345"}')
    assert _is_deny(out)
    assert "unique-12345" in out


def test_guard_name_accepts_a_py_suffix(tmp_path):
    _write_guard(tmp_path, "silent", "import sys\nsys.exit(0)\n")
    out = _capture("silent.py", tmp_path)
    assert out == ""


def test_run_always_returns_zero_even_on_deny(tmp_path):
    """The dispatcher's own exit contract: 0 always, decision lives in the
    JSON -- never encoded as a process exit code, matching every guard's
    own contract."""
    import io

    buf = io.StringIO()
    old_stdout = sys.stdout
    sys.stdout = buf
    try:
        rc = rh.run("does-not-exist", "{}", 3.0, hooks_dir=tmp_path)
    finally:
        sys.stdout = old_stdout
    assert rc == 0


# ---------------------------------------------------------------------------
# main()'s own top-level catch-all: even a genuinely unexpected exception
# inside _main must still end in a deny, exit 0, never a bare traceback.
# ---------------------------------------------------------------------------

def test_main_catches_an_unexpected_exception_and_still_denies(monkeypatch, capsys):
    def _boom(argv=None):
        raise RuntimeError("something nobody anticipated")

    monkeypatch.setattr(rh, "_main", _boom)
    rc = rh.main([])
    assert rc == 0
    out = capsys.readouterr().out
    assert _is_deny(out)
    assert "unexpected" in out.lower()


# ---------------------------------------------------------------------------
# CLI surface: the real subprocess contract .claude/settings.json invokes
# ---------------------------------------------------------------------------

def _run_cli(args: list[str], stdin_payload: str = "{}", timeout: int = 15) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(HOOKS_DIR / "run_hook.py"), *args],
        input=stdin_payload, capture_output=True, text=True, timeout=timeout,
    )


def test_cli_no_guard_name_denies():
    proc = _run_cli([])
    assert proc.returncode == 0
    assert _is_deny(proc.stdout)
    assert "no guard name" in proc.stdout.lower()


def test_cli_unknown_guard_denies_not_silently_allows():
    """The exact scenario the coordinator verified by hand before wiring
    this into .claude/settings.json: an unknown guard name is denied,
    never silently ignored."""
    proc = _run_cli(["this-guard-does-not-exist"])
    assert proc.returncode == 0
    assert _is_deny(proc.stdout)


def test_cli_real_silent_guard_produces_no_stdout_and_exits_zero():
    """block_git_stash.py itself, run under the real dispatcher, against a
    harmless command: silent, exit 0 -- the everyday case."""
    proc = _run_cli(["block_git_stash"], stdin_payload=json.dumps(
        {"tool_name": "Bash", "tool_input": {"command": "git status"}}
    ))
    assert proc.returncode == 0
    assert proc.stdout.strip() == ""


def test_cli_real_guard_deny_passes_through():
    """block_git_stash.py itself, run under the real dispatcher, against a
    command it is known to deny."""
    proc = _run_cli(["block_git_stash"], stdin_payload=json.dumps(
        {"tool_name": "Bash", "tool_input": {"command": "git stash pop"}}
    ))
    assert proc.returncode == 0
    assert _is_deny(proc.stdout)


def test_cli_bad_timeout_value_is_a_usage_error_not_a_silent_allow():
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "run_hook.py"), "--timeout", "not-a-number", "block_git_stash"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 2  # argparse usage error, not a silent allow
