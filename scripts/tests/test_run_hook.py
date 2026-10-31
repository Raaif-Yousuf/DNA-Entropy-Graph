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
    if not isinstance(data, dict):
        return False
    hso = data.get("hookSpecificOutput", {})
    return isinstance(hso, dict) and hso.get("permissionDecision") == "deny"


def _capture(guard_name: str, hooks_dir: Path, timeout: float = rh.SELF_TEST_BACKSTOP_SECONDS, stdin_payload: str = "{}") -> str:
    # The default is the backstop, never a tight deadline: see #1055. Only the
    # hang test passes a short timeout.
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


def _decide(guard_name: str, hooks_dir: Path, timeout: float = rh.SELF_TEST_BACKSTOP_SECONDS, stdin_payload: str = "{}") -> rh.Decision:
    """The structural result (kind + stdout), same default backstop as _capture."""
    d = rh.run_decision(guard_name, stdin_payload, timeout, hooks_dir=hooks_dir)
    assert d.rc == 0
    return d


# ---------------------------------------------------------------------------
# run_hook.py's own self-test, bridged into pytest
# ---------------------------------------------------------------------------


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "run_hook.py"), "--self-test"],
        capture_output=True,
        text=True,
        timeout=rh.SELF_TEST_BACKSTOP_SECONDS * 2,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "run_hook.py"), "--help"],
        capture_output=True,
        text=True,
        timeout=15,
    )
    assert proc.returncode == 0
    assert "--timeout" in proc.stdout
    assert "--self-test" in proc.stdout


# ---------------------------------------------------------------------------
# run(): every arm, in-process (fast, precise)
# ---------------------------------------------------------------------------


def test_missing_guard_file_denies(tmp_path):
    out = _capture("does-not-exist-at-all", tmp_path)
    assert _is_deny(out), f"captured: {out!r}"
    assert "does-not-exist-at-all" in out
    assert "missing" in out.lower()


def test_guard_crash_denies_with_stderr_in_reason(tmp_path):
    _write_guard(tmp_path, "crashes", "import sys\nsys.stderr.write('boom\\n')\nsys.exit(3)\n")
    out = _capture("crashes", tmp_path)
    assert _is_deny(out), f"captured: {out!r}"
    assert _decide("crashes", tmp_path).kind == rh.KIND_CRASHED
    assert "boom" in out
    assert "3" in out  # the exit code


def test_guard_deny_passes_through_unchanged(tmp_path):
    _write_guard(
        tmp_path,
        "denies",
        "import json, sys\n"
        "json.dump({'hookSpecificOutput': {'hookEventName': 'PreToolUse', "
        "'permissionDecision': 'deny', 'permissionDecisionReason': 'no'}}, sys.stdout)\n",
    )
    out = _capture("denies", tmp_path)
    data = json.loads(out)
    assert data["hookSpecificOutput"]["permissionDecision"] == "deny", f"captured: {out!r}"
    assert data["hookSpecificOutput"]["permissionDecisionReason"] == "no", f"captured: {out!r}"


def test_guard_silent_produces_no_output_and_no_deny(tmp_path):
    _write_guard(tmp_path, "silent", "import sys\nsys.exit(0)\n")
    out = _capture("silent", tmp_path)
    assert out == ""


def test_guard_timeout_denies(tmp_path):
    _write_guard(tmp_path, "hangs", "import time\ntime.sleep(30)\n")
    d = _decide("hangs", tmp_path, timeout=rh.SELF_TEST_HANG_TIMEOUT_SECONDS)
    # The timeout deny specifically: a crash deny must not satisfy this test.
    assert d.kind == rh.KIND_TIMEOUT, f"captured: {d!r}"
    assert "hangs" in d.stdout


def test_each_dispatcher_deny_has_its_own_kind(tmp_path):
    _write_guard(tmp_path, "crashes", "import sys\nsys.exit(3)\n")
    _write_guard(tmp_path, "garbage", "import sys\nsys.stdout.write('not json {{{')\n")
    _write_guard(tmp_path, "silent", "import sys\nsys.exit(0)\n")
    assert _decide("crashes", tmp_path).kind == rh.KIND_CRASHED
    assert _decide("missing-guard", tmp_path).kind == rh.KIND_MISSING
    assert _decide("garbage", tmp_path).kind == rh.KIND_NOT_JSON
    assert _decide("silent", tmp_path).kind == rh.KIND_PASSTHROUGH


def test_a_guard_cannot_pass_for_a_dispatcher_timeout_by_its_prose(tmp_path):
    """The timeout is detected by the dispatcher's structural kind, not by a
    phrase in the reason: a real guard writing that phrase is still its own."""
    _write_guard(
        tmp_path,
        "mimic",
        "import json, sys\n"
        "json.dump({'hookSpecificOutput': {'hookEventName': 'PreToolUse', 'permissionDecision': 'deny', "
        "'permissionDecisionReason': 'The mimic guard did not finish within 1s and was killed.'}}, sys.stdout)\n",
    )
    d = _decide("mimic", tmp_path)
    assert "did not finish within" in d.stdout
    assert d.kind == rh.KIND_PASSTHROUGH, f"captured: {d!r}"


def test_production_stdout_has_exactly_the_keys_main_always_emitted(tmp_path):
    """#1055: the kind is in-process only. Printed deny JSON must stay byte-for-byte
    the pre-#1055 shape (no extra key), because the hook output schema Claude
    Code accepts is not verified here."""
    _write_guard(tmp_path, "hangs", "import time\ntime.sleep(30)\n")
    out = _capture("hangs", tmp_path, timeout=rh.SELF_TEST_HANG_TIMEOUT_SECONDS)
    data = json.loads(out)
    assert set(data) == {"hookSpecificOutput", "systemMessage"}, f"captured: {out!r}"
    assert set(data["hookSpecificOutput"]) == {"hookEventName", "permissionDecision", "permissionDecisionReason"}
    assert out == _decide("hangs", tmp_path, timeout=rh.SELF_TEST_HANG_TIMEOUT_SECONDS).stdout


def test_deny_helpers_tolerate_valid_json_that_is_not_an_object():
    for out in ("[]", '"x"', "3", "null", '{"hookSpecificOutput": []}', '{"hookSpecificOutput": "x"}'):
        assert rh._is_deny(out) is False, out  # the dispatcher's own copy
        assert _is_deny(out) is False, out  # this file's copy


def test_backstop_is_a_backstop_not_a_tight_deadline():
    # The slow-start test only proves > 4 s; pin the intent: a cold interpreter
    # under heavy load must never hit it.
    assert rh.SELF_TEST_BACKSTOP_SECONDS >= 60


def test_slow_starting_guard_is_not_misread_as_a_timeout(tmp_path):
    """#1055: a guard that terminates on its own must never fail a check by being
    slow to start (a cold interpreter under machine load). This guard takes ~4 s,
    longer than the old 3.0 s default, then denies with its own reason. The
    default `_capture` timeout must be the backstop, not a tight deadline."""
    _write_guard(
        tmp_path,
        "slow",
        "import json, sys, time\n"
        "time.sleep(4)\n"
        "json.dump({'hookSpecificOutput': {'hookEventName': 'PreToolUse', "
        "'permissionDecision': 'deny', 'permissionDecisionReason': 'slow but mine'}}, sys.stdout)\n",
    )
    out = _capture("slow", tmp_path)
    assert "slow but mine" in out, f"captured: {out!r}"


def test_guard_nonjson_output_denies(tmp_path):
    _write_guard(tmp_path, "garbage", "import sys\nsys.stdout.write('not json {{{')\nsys.exit(0)\n")
    out = _capture("garbage", tmp_path)
    # The not-valid-JSON deny specifically: a crash/timeout/missing deny must not pass.
    assert _is_deny(out), f"captured: {out!r}"
    assert _decide("garbage", tmp_path).kind == rh.KIND_NOT_JSON
    assert "not valid JSON" in out, f"captured: {out!r}"


def test_stdin_payload_genuinely_reaches_the_guard(tmp_path):
    _write_guard(
        tmp_path,
        "echo_and_fail",
        "import sys\nsys.stderr.write('saw:' + sys.stdin.read())\nsys.exit(1)\n",
    )
    out = _capture("echo_and_fail", tmp_path, stdin_payload='{"marker": "unique-12345"}')
    assert _is_deny(out), f"captured: {out!r}"
    assert "unique-12345" in out, f"captured: {out!r}"


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
    assert _is_deny(out), f"captured: {out!r}"
    assert "unexpected" in out.lower()


# ---------------------------------------------------------------------------
# CLI surface: the real subprocess contract .claude/settings.json invokes
# ---------------------------------------------------------------------------


def _run_cli(args: list[str], stdin_payload: str = "{}", timeout: int = 15) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(HOOKS_DIR / "run_hook.py"), *args],
        input=stdin_payload,
        capture_output=True,
        text=True,
        timeout=timeout,
    )


def test_cli_no_guard_name_denies():
    proc = _run_cli([])
    assert proc.returncode == 0
    assert _is_deny(proc.stdout), f"captured: {proc.stdout!r}"
    assert "no guard name" in proc.stdout.lower()


def test_cli_unknown_guard_denies_not_silently_allows():
    """The exact scenario the coordinator verified by hand before wiring
    this into .claude/settings.json: an unknown guard name is denied,
    never silently ignored."""
    proc = _run_cli(["this-guard-does-not-exist"])
    assert proc.returncode == 0
    assert _is_deny(proc.stdout), f"captured: {proc.stdout!r}"


def test_cli_real_silent_guard_produces_no_stdout_and_exits_zero():
    """block_git_stash.py itself, run under the real dispatcher, against a
    harmless command: silent, exit 0 -- the everyday case."""
    proc = _run_cli(["block_git_stash"], stdin_payload=json.dumps({"tool_name": "Bash", "tool_input": {"command": "git status"}}))
    assert proc.returncode == 0
    assert proc.stdout.strip() == ""


def test_cli_real_guard_deny_passes_through():
    """block_git_stash.py itself, run under the real dispatcher, against a
    command it is known to deny."""
    proc = _run_cli(["block_git_stash"], stdin_payload=json.dumps({"tool_name": "Bash", "tool_input": {"command": "git stash pop"}}))
    assert proc.returncode == 0
    assert _is_deny(proc.stdout), f"captured: {proc.stdout!r}"


def test_cli_bad_timeout_value_is_a_usage_error_not_a_silent_allow():
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "run_hook.py"), "--timeout", "not-a-number", "block_git_stash"],
        capture_output=True,
        text=True,
        timeout=15,
    )
    assert proc.returncode == 2  # argparse usage error, not a silent allow
