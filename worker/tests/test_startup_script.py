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
        ["bash", "-n", "-"], input=STARTUP_SH.read_bytes(), capture_output=True,
    )
    assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")


def _text() -> str:
    return STARTUP_SH.read_text(encoding="utf-8")


def _code_only() -> str:
    """``_text()`` with full-line comments stripped, so assertions about what the script
    actually DOES aren't fooled by its own explanatory prose (which necessarily mentions
    "shutdown -h"/"ssh"/"gcloud" while explaining why they are avoided)."""
    return "\n".join(
        ln for ln in _text().splitlines() if not ln.strip().startswith("#")
    )


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
    assert 'IMAGE=$(meta instance/attributes/deg-worker-image)' in text
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
    assert 'done|failed|cancelled' in text
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
    """Must match worker/src/dna_entropy/worker/cli.py's EXIT_* constants exactly."""
    text = _text()
    assert "10) cleanup stop" in text.replace(" ", "").replace("\n", " ") or "10) cleanup stop" in text
    assert "11) cleanup delete" in text.replace(" ", "").replace("\n", " ") or "11) cleanup delete" in text


def test_uses_curl_unconditionally_no_gcloud_dependency() -> None:
    """Must run identically on the DLVM (has gcloud) and Container-Optimized OS (does
    not) for the CPU smoke test."""
    code = _code_only()
    assert "gcloud" not in code
    assert "curl" in code
