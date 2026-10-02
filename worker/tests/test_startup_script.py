"""Tests for worker/vm/startup.sh (issue #45).

``ci-worker.yml``'s ``contract`` job runs real ``shellcheck`` on this file (installs it via
apt on ubuntu-latest) — that is the authoritative lint. These tests are a laptop-local
safety net: a ``bash -n`` syntax check (skipped, not failed, if no ``bash`` is on PATH) and
a handful of structural greps proving the hard rules from the module's own docstring are
actually present in the text (a docstring claim with no matching line is exactly the kind
of drift this test catches before shellcheck or a real VM ever would).
"""

from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

import pytest

STARTUP_SH = Path(__file__).parent.parent / "vm" / "startup.sh"


def test_startup_script_exists() -> None:
    assert STARTUP_SH.is_file()


def test_startup_script_is_executable_shape() -> None:
    text = STARTUP_SH.read_text(encoding="utf-8")
    assert text.startswith("#!/usr/bin/env bash")


@pytest.mark.skipif(shutil.which("bash") is None, reason="no bash on PATH")
def test_startup_script_has_valid_bash_syntax() -> None:
    # Piped via stdin (`bash -n -`) as raw BYTES, not a file-path argument and not
    # `text=True`: a Windows path handed to Git-for-Windows bash as an argv entry needs
    # MSYS path translation that is not reliably applied when bash.exe is spawned
    # directly by subprocess.run; and subprocess's `text=True` mode re-translates outgoing
    # `\n` to Windows `\r\n` on write, which bash then chokes on even though the file ON
    # DISK is plain LF (confirmed: `grep -c $'\r' worker/vm/startup.sh` = 0, and
    # .gitattributes pins `eol=lf` repo-wide) — both are subprocess/Windows quirks, not
    # anything wrong with the script.
    result = subprocess.run(
        ["bash", "-n", "-"],
        input=STARTUP_SH.read_bytes(),
        capture_output=True,
    )
    assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")


def _text() -> str:
    return STARTUP_SH.read_text(encoding="utf-8")


def _code_only() -> str:
    """``_text()`` with full-line comments stripped, so assertions about what the script
    actually DOES aren't fooled by its own explanatory prose (which necessarily mentions
    "shutdown -h"/"ssh"/"gcloud" while explaining why they are avoided)."""
    return "\n".join(ln for ln in _text().splitlines() if not ln.strip().startswith("#"))


# --- the hard rules this script exists to satisfy (see its own module docstring) ------


def test_never_uses_shutdown_h_as_the_primary_cleanup_mechanism() -> None:
    """CLAUDE.md Critical Pitfalls / CLAIR #2908: instanceTerminationAction does not fire
    on a guest shutdown. The ONLY `shutdown -h` may be the deadman, armed once, in the
    background (trailing `&`), never inside cleanup() itself."""
    code = _code_only()
    shutdown_lines = [ln for ln in code.splitlines() if "shutdown -h" in ln]
    assert len(shutdown_lines) == 1, shutdown_lines
    assert shutdown_lines[0].rstrip().endswith("&")  # backgrounded deadman, not a direct call


def test_cleanup_calls_the_compute_api_not_shutdown() -> None:
    code = _code_only()
    cleanup_body = code.split("cleanup() {")[1].split("\n}\n")[0]
    assert "shutdown" not in cleanup_body
    assert "compute.googleapis.com" in cleanup_body
    assert "/stop" in cleanup_body
    assert "DELETE" in cleanup_body


def test_never_uses_ssh() -> None:
    code = _code_only().lower()
    assert " ssh " not in code
    assert "scp " not in code


def test_waits_for_nvidia_smi_rather_than_assuming_the_driver_is_up() -> None:
    text = _text()
    assert "nvidia-smi" in text
    assert "for _ in $(seq" in text  # a retry loop, not a single unconditional check


def test_pulls_the_image_by_digest_variable_not_a_mutable_tag() -> None:
    text = _text()
    assert "IMAGE=$(meta instance/attributes/deg-worker-image)" in text
    assert 'docker pull "$IMAGE"' in text


def test_writes_a_booting_status_before_any_step_that_can_fail() -> None:
    text = _text()
    booting_idx = text.index("status booting")
    docker_pull_idx = text.index('docker pull "$IMAGE"')
    nvidia_idx = text.index("nvidia-smi -L")
    assert booting_idx < docker_pull_idx
    assert booting_idx < nvidia_idx


def test_short_circuits_when_already_finished_on_a_previous_boot() -> None:
    text = _text()
    assert "done|failed|cancelled" in text
    # The short-circuit must appear BEFORE the install/run work, not after.
    short_circuit_idx = text.index("done|failed|cancelled")
    docker_pull_idx = text.index('docker pull "$IMAGE"')
    assert short_circuit_idx < docker_pull_idx


def test_error_trap_reports_worker_crash_and_applies_lifecycle() -> None:
    text = _text()
    assert "trap " in text
    trap_line = next(ln for ln in text.splitlines() if ln.strip().startswith("trap "))
    assert "WORKER_CRASH" in trap_line
    assert "cleanup" in trap_line


def test_exit_code_dispatch_matches_the_cli_contract() -> None:
    """worker/src/dna_entropy/worker/cli.py (issue #44): exit 10/11 mean the worker already
    stopped/deleted the VM itself, so the script must NOT apply the lifecycle a second time;
    every other code (0 done, 2 failed, 3 cancelled) still runs cleanup "$LIFECYCLE" as the
    backstop."""
    import re

    from dna_entropy.worker import cli

    assert (cli.EXIT_REQUEST_STOP, cli.EXIT_REQUEST_DELETE) == (10, 11)
    code = _code_only()
    case = re.search(r"case \$rc in(.*?)\nesac", code, re.DOTALL)
    assert case, "no `case $rc in ... esac` dispatch found"
    body = case.group(1)
    already_applied, _, backstop = body.partition("*)")
    assert re.search(r"(^|\s)10\|11\)", already_applied), already_applied
    assert "cleanup" not in already_applied, "10/11 must not call cleanup (no second Compute API call)"
    assert 'cleanup "$LIFECYCLE"' in backstop


def test_uses_curl_unconditionally_no_gcloud_dependency() -> None:
    """Must run identically on the DLVM (has gcloud) and Container-Optimized OS (does
    not) for the CPU smoke test."""
    code = _code_only()
    assert "gcloud" not in code
    assert "curl" in code


# --- lifecycle=keep (issue #464): the script owns the keep-alive window ------------------


def _extract_function(name: str) -> str:
    import re

    m = re.search(rf"^{name}\(\) \{{\n.*?\n\}}\n", _text(), re.DOTALL | re.MULTILINE)
    assert m, f"no {name}() function found in startup.sh"
    return m.group(0)


def _keep_plan(manifest: str | None, max_run_min: int, elapsed_s: int) -> str:
    """Run the REAL keep_plan() text from startup.sh in bash against a manifest. The manifest
    is written by bash itself into its own temp dir: the `bash` on a Windows PATH may be Git
    Bash, MSYS or WSL, and each spells Windows paths differently, so no path crosses over."""
    lines = [_extract_function("keep_plan"), 'cd "$(mktemp -d)" || exit 1']
    if manifest is not None:
        lines += ["cat > manifest.json <<'MANIFEST'", manifest, "MANIFEST"]
    lines.append(f"keep_plan manifest.json {max_run_min} {elapsed_s}")
    script = "\n".join(lines) + "\n"
    result = subprocess.run(["bash", "-s"], input=script.encode("utf-8"), capture_output=True)
    assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")
    return result.stdout.decode("utf-8").strip()


def _bash_available() -> bool:
    return shutil.which("bash") is not None


needs_bash = pytest.mark.skipif(not _bash_available(), reason="no bash on PATH")


def _bash_has_python3() -> bool:
    if shutil.which("bash") is None:
        return False
    return subprocess.run(["bash", "-c", "command -v python3"], capture_output=True).returncode == 0


needs_bash_python3 = pytest.mark.skipif(not _bash_has_python3(), reason="no bash with python3 on PATH")


def _lifecycle(**kw) -> str:
    import json

    return json.dumps({"lifecycle": kw})


@needs_bash_python3
@pytest.mark.parametrize(
    ("manifest", "max_run_min", "elapsed_s", "expected"),
    [
        # the window is honoured and afterKeepAlive is the action applied when it ends
        (_lifecycle(afterTask="keep", keepAliveMinutes=30, afterKeepAlive="delete"), 240, 100, "1800 delete"),
        (_lifecycle(afterTask="keep", keepAliveMinutes=5), 240, 0, "300 stop"),
        # Hard Rule 11: the window can never outlive maxRunDuration (10 minute margin kept)
        (_lifecycle(afterTask="keep", keepAliveMinutes=600), 60, 0, "3000 stop"),
        (_lifecycle(afterTask="keep", keepAliveMinutes=30), 60, 3500, "0 stop"),
        # no window, a negative one, or a nonsense one: nothing to hold the VM for
        (_lifecycle(afterTask="keep"), 240, 0, "0 stop"),
        (_lifecycle(afterTask="keep", keepAliveMinutes=-5), 240, 0, "0 stop"),
        (_lifecycle(afterTask="keep", keepAliveMinutes="soon"), 240, 0, "0 stop"),
        # keep can never be the action AFTER the window; unknown spellings stop
        (_lifecycle(afterTask="keep", keepAliveMinutes=5, afterKeepAlive="keep"), 240, 0, "300 stop"),
        (_lifecycle(afterTask="keep", keepAliveMinutes=5, afterKeepAlive="Delete "), 240, 0, "300 stop"),
        # an unreadable manifest fails safe, to stop now
        ("{not json", 240, 0, "0 stop"),
        (None, 240, 0, "0 stop"),
    ],
)
def test_keep_plan_bounds_the_window_and_always_ends_in_stop_or_delete(
    manifest: str | None, max_run_min: int, elapsed_s: int, expected: str
) -> None:
    assert _keep_plan(manifest, max_run_min, elapsed_s) == expected


def test_keep_is_held_for_its_window_not_stopped_immediately() -> None:
    """Issue #464: `cleanup "$LIFECYCLE"` with keep used to stop the VM at once. The
    exit-code dispatch must route keep through keep_hold, and keep_hold must end in a
    cleanup of the planned (never `keep`) action."""
    import re

    code = _code_only()
    case = re.search(r"case \$rc in(.*?)\nesac", code, re.DOTALL)
    assert case
    _, _, backstop = case.group(1).partition("*)")
    assert re.search(r'"\$LIFECYCLE" = "?keep"?', backstop), backstop
    assert "keep_hold" in backstop
    assert 'cleanup "$LIFECYCLE"' in backstop  # the non-keep branch is unchanged
    hold = _extract_function("keep_hold")
    assert "keep_plan" in hold
    assert 'cleanup "$after"' in hold
    assert "sleep" in hold and "shutdown -h" not in hold


# --- keep_hold: only a SUCCESSFUL run is held (DECISION, agent-made, reversible) ---------


def _run_keep_hold(rc: int, uptime_line: str = "5.00 12.00") -> list[str]:
    """Source the REAL keep_hold() with stubs for everything it touches (keep_plan prints a
    30 minute delete plan, cleanup/sleep record their calls, sleep advances $SECONDS, cut
    stands in for /proc/uptime) and return the recorded calls."""
    lines = [
        _extract_function("keep_hold"),
        'MAX_RUN_MIN=240; LOG=""',
        'keep_plan() { echo "1800 delete"; }',
        'cleanup() { LOG="$LOG cleanup:$1"; }',
        'sleep() { LOG="$LOG sleep"; SECONDS=$((SECONDS + $1)); }',
        f'cut() {{ echo "{uptime_line}"; }}',
        f"keep_hold {rc} >/dev/null",
        'echo "$LOG"',
    ]
    result = subprocess.run(
        ["bash", "-s"], input=("\n".join(lines) + "\n").encode("utf-8"), capture_output=True, timeout=60
    )
    assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")
    return result.stdout.decode("utf-8").split()


@needs_bash
@pytest.mark.parametrize("rc", [2, 3])
def test_a_failed_or_cancelled_keep_run_applies_after_keep_alive_immediately(rc: int) -> None:
    """DECISION (agent-made on the owner's behalf, reversible): a failed (2) or cancelled
    (3) run is NOT held for its keep-alive window, because that bills idle GPU time for a
    job that produced nothing worth keeping warm for. afterKeepAlive applies at once."""
    assert _run_keep_hold(rc) == ["cleanup:delete"]  # no sleep at all


@needs_bash
def test_a_successful_keep_run_is_held_for_the_whole_window_then_cleaned_up() -> None:
    calls = _run_keep_hold(0)
    assert calls[-1] == "cleanup:delete"
    assert calls.count("sleep") == 1800 // 15


def test_keep_hold_measures_elapsed_time_from_vm_boot_not_script_start() -> None:
    hold = _extract_function("keep_hold")
    assert "/proc/uptime" in hold
    assert 'keep_plan /work/manifest.json "$MAX_RUN_MIN" "$SECONDS"' not in hold


def test_the_exit_code_dispatch_passes_the_worker_exit_code_to_keep_hold() -> None:
    assert 'keep_hold "$rc"' in _code_only()
