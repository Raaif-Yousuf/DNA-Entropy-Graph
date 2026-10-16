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
    # The one other `shutdown -h` is foreign_zone_exit's last resort when its own self-delete failed
    # (#670 review): it is pinned by test_a_foreign_zone_vm_whose_self_delete_fails_shuts_the_guest_down.
    import re

    code = re.sub(r"foreign_zone_exit\(\) \{\n.*?\n\}\n", "", _code_only(), flags=re.DOTALL)
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
    """No gcloud dependency: the script talks to the metadata server and Google REST with curl, so it
    does not rely on the image shipping gcloud (the DLVM image does, for CPU and GPU alike, #615)."""
    code = _code_only()
    assert "gcloud" not in code
    assert "curl" in code


def test_docker_run_passes_gpus_all_only_when_a_gpu_is_expected() -> None:
    """The CPU smoke VM boots the same DLVM image with no GPU (issue #56): an unconditional
    `--gpus all` makes `docker run` fail there. The flag is appended only when EXPECT_GPU is true."""
    code = _code_only()
    assert code.count("--gpus all") == 1
    gate = code.index('if [ "$EXPECT_GPU" = "true" ]; then\n  GPU_FLAGS=(--gpus all)')
    # gpu_test_run() (issue #759) also has a `docker run --rm`, defined above the gate and only
    # CALLED after it: the worker's own run is the first one after the gate.
    assert '"${GPU_FLAGS[@]}"' in _extract_function("gpu_test_run")
    worker_run = code.index("docker run --rm", gate)
    run_block = code[worker_run:]
    assert '"${GPU_FLAGS[@]}"' in run_block.split("dna-entropy-worker run")[0]
    assert "--gpus all" not in run_block


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


# --- gpu-test mode (issue #759): `pytest -m gpu` in the image instead of a job ------------


def _run_gpu_test_run(
    docker_rc: int,
    max_run_min: int = 60,
    gpu_flags: str = "--gpus all",
    summary: str = "== 1 passed ==",
    uptime: str = "0.00",
    elapsed: int | None = None,
    bad_log: bool = False,
) -> list[str]:
    """Source the REAL gpu_test_run() with stubs for everything it touches and return the
    recorded calls, one per line. docker prints a pytest-looking line and exits docker_rc;
    put_object records the object name AND, for the result file, its content."""
    lines = [
        "set -uo pipefail",  # as in startup.sh: the pipeline's status is docker's, not tee's
        _extract_function("gpu_test_run"),
        f'MAX_RUN_MIN={max_run_min}; IMAGE="ghcr.io/x/y@sha256:abc"; JOB=job1',
        "NAME=deg-job1; ZONE=z1; LIFECYCLE=delete",
        f"GPU_FLAGS=({gpu_flags})",
        'cd "$(mktemp -d)" || exit 1',
        "GPU_TEST_LOG="
        + ("/nonexistent-deg-dir/gpu.log" if bad_log else "$PWD/gpu.log")
        + "; GPU_TEST_RESULT=$PWD/result.json",
        # how long the tests "took" (the harness cannot wait out a real 50 minute limit)
        *([f"GPU_TEST_ELAPSED={elapsed}"] if elapsed is not None else []),
        # a file standing in for /proc/uptime ("<seconds since boot> <idle>"); absent means "unreadable"
        *([f'echo "{uptime} 0.00" > "$PWD/uptime"', "GPU_TEST_UPTIME_FILE=$PWD/uptime"] if uptime else []),
        # records `timeout [-k N] LIMIT cmd...`: the kill-after and the limit, then runs the command
        'timeout() { if [ "$1" = "-k" ]; then echo "timeout-k:$2"; shift 2; fi; '
        'echo "timeout:$1"; shift; "$@"; }',
        # `docker kill`/`rm` succeed; `docker run` prints a pytest-looking line and exits docker_rc
        f'docker() {{ echo "docker:$*"; case "$1" in kill|rm) return 0;; esac; '
        f'echo "{summary}"; return {docker_rc}; }}',
        # the result file's content is echoed so the test can read it
        'put_object() { echo "put:$2"; '
        '[ "${2##*/}" = "gpu-test-result.json" ] && { cat "$1"; echo; }; true; }',
        'status() { echo "status:$1"; }',
        'cleanup() { echo "cleanup:$1"; }',
        'gpu_test_run; echo "rc:$?"',
    ]
    result = subprocess.run(
        ["bash", "-s"], input=("\n".join(lines) + "\n").encode("utf-8"), capture_output=True, timeout=60
    )
    assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")
    return result.stdout.decode("utf-8").splitlines()


@needs_bash
def test_gpu_test_run_runs_pytest_gpu_in_the_image_with_the_gpu_flags_and_a_deadline() -> None:
    out = _run_gpu_test_run(0)
    docker = next(ln for ln in out if ln.startswith("docker:run"))
    assert "--gpus all" in docker
    assert "ghcr.io/x/y@sha256:abc" in docker
    code = "\n".join(out)
    assert '"--collect-only", "-q", "-m", "gpu"' in code  # selection asks for the gpu marker
    assert '"pytest", "-m", "gpu"' in code  # and so does the real run
    assert "timeout:3000" in out  # (MAX_RUN_MIN 60 - 10 min margin) in seconds


@needs_bash
def test_gpu_test_run_has_a_floor_on_the_deadline() -> None:
    assert "timeout:60" in _run_gpu_test_run(0, max_run_min=5)


@needs_bash
def test_gpu_test_run_uploads_the_log_then_the_result_then_applies_the_lifecycle() -> None:
    out = _run_gpu_test_run(0)
    puts = [ln for ln in out if ln.startswith("put:")]
    assert puts == ["put:jobs/job1/logs/gpu-test.log", "put:jobs/job1/logs/gpu-test-result.json"]
    # the result file is the LAST object, and the lifecycle comes after both uploads
    assert out.index("cleanup:delete") > out.index("put:jobs/job1/logs/gpu-test-result.json")
    assert out[-1] == "rc:0"


@needs_bash
@pytest.mark.parametrize(
    ("rc", "status", "timed_out"),
    [
        (0, "status:done", False),
        (1, "status:failed", False),
        (5, "status:failed", False),
        (124, "status:failed", True),
    ],
)
def test_gpu_test_run_result_json_carries_the_pytest_exit_code(rc: int, status: str, timed_out: bool) -> None:
    import json

    out = _run_gpu_test_run(rc)
    parsed = json.loads(next(ln for ln in out if ln.startswith("{")))
    assert parsed["exitCode"] == rc
    assert parsed["timedOut"] is timed_out
    assert parsed["jobId"] == "job1"
    assert status in out
    assert out[-1] == f"rc:{rc}"
    assert "cleanup:delete" in out  # a failed or timed-out run still ends in the lifecycle


@needs_bash
def test_gpu_test_run_without_a_gpu_expectation_passes_no_gpu_flag() -> None:
    docker = next(ln for ln in _run_gpu_test_run(0, gpu_flags="") if ln.startswith("docker:run"))
    assert "--gpus" not in docker


def test_gpu_test_mode_is_chosen_by_the_deg_mode_attribute_before_the_manifest_is_fetched() -> None:
    text = _code_only()
    assert "MODE=$(meta instance/attributes/deg-mode)" in text
    mode_idx = text.index('if [ "$MODE" = "gpu-test" ]; then\n  gpu_test_run')
    assert (
        text.index('docker pull "$IMAGE"')
        < mode_idx
        < text.index('get_object "jobs/${JOB}/manifest.json" > /work/manifest.json')
    )
    # GPU_FLAGS must exist before the branch uses it
    assert text.index("GPU_FLAGS=()") < mode_idx
    branch = text[mode_idx : text.index("mkdir -p /work")]
    assert "gpu_test_run" in branch
    assert "exit" in branch
    assert "dna-entropy-worker run" not in branch


@needs_bash
@pytest.mark.parametrize(
    "summary", ["== 1 passed, 2 skipped ==", "== 2 skipped in 0.1s ==", "== no tests ran in 0.01s =="]
)
def test_gpu_test_run_never_reports_a_pass_when_gpu_tests_were_skipped_or_none_ran(summary: str) -> None:
    """A skipped GPU test is the false pass this mode exists to prevent: evo2/torch not
    importable makes every gpu test skip itself and pytest exit 0 (THEORY (unverified),
    from reading test_evo_predictor.py's importorskip fixtures). Exit code 6 says so."""
    import json

    out = _run_gpu_test_run(0, summary=summary)
    parsed = json.loads(next(ln for ln in out if ln.startswith("{")))
    assert parsed["exitCode"] == 6
    assert "status:failed" in out
    assert out[-1] == "rc:6"


def test_gpu_test_run_selects_tests_by_gpu_marker_collection_not_by_grepping_file_text() -> None:
    """A file that merely MENTIONS pytest.mark.gpu (a docstring, a startup-script test) must not be
    run, and a gpu test marked another way (pytestmark, conftest) must not be missed: the
    selection is `pytest --collect-only -m gpu` per file, run inside the image."""
    body = _extract_function("gpu_test_run")
    assert "grep -l" not in body
    assert "--collect-only" in body


@needs_bash
def test_gpu_test_run_passes_the_container_a_name_and_an_init_process() -> None:
    """Without --init the python process is PID 1 in the container and has no SIGTERM handler, so
    the signal `timeout` sends the docker CLI is ignored and a hung test is never stopped."""
    docker = next(ln for ln in _run_gpu_test_run(0) if ln.startswith("docker:run"))
    assert " --init " in docker
    assert " --name deg-gputest-job1 " in docker


@needs_bash
def test_gpu_test_run_escalates_to_a_kill_after_the_term_grace() -> None:
    assert "timeout-k:30" in _run_gpu_test_run(0)


@needs_bash
def test_gpu_test_run_kills_the_container_by_name_when_the_deadline_passes() -> None:
    import json

    out = _run_gpu_test_run(124)
    assert "docker:kill deg-gputest-job1" in out
    assert out.index("docker:kill deg-gputest-job1") < out.index("put:jobs/job1/logs/gpu-test-result.json")
    parsed = json.loads(next(ln for ln in out if ln.startswith("{")))
    assert parsed["timedOut"] is True
    assert parsed["exitCode"] == 124


@needs_bash
def test_gpu_test_run_does_not_kill_anything_when_the_tests_finish() -> None:
    assert not any(ln.startswith("docker:kill") for ln in _run_gpu_test_run(1))
    assert not any(ln.startswith("docker:kill") for ln in _run_gpu_test_run(0))


@needs_bash
def test_gpu_test_run_counts_the_deadline_from_boot_not_from_the_start_of_the_tests() -> None:
    """The VM's maxRunDuration runs from creation; the GPU wait and the image pull already used
    some of it. 60 min - 10 min margin - 600 s of uptime = 2400 s left."""
    assert "timeout:2400" in _run_gpu_test_run(0, uptime="600.50")


@needs_bash
def test_gpu_test_run_deadline_cannot_drop_below_the_floor_however_late_it_starts() -> None:
    assert "timeout:60" in _run_gpu_test_run(0, uptime="99999.00")


def _run_early_failure(code: str, mode: str = "gpu-test") -> list[str]:
    """Source the REAL gpu_test_early_fail() and return what it recorded."""
    lines = [
        "set -uo pipefail",
        _extract_function("gpu_test_early_fail"),
        f'MODE="{mode}"; IMAGE="ghcr.io/x/y@sha256:abc"; JOB=job1; NAME=deg-job1; ZONE=z1',
        'cd "$(mktemp -d)" || exit 1',
        "GPU_TEST_LOG=$PWD/gpu.log; GPU_TEST_RESULT=$PWD/result.json",
        'put_object() { echo "put:$2"; '
        '[ "${2##*/}" = "gpu-test-result.json" ] && { cat "$1"; echo; }; true; }',
        f'gpu_test_early_fail {code} "something broke"',
    ]
    result = subprocess.run(
        ["bash", "-s"], input=("\n".join(lines) + "\n").encode("utf-8"), capture_output=True, timeout=60
    )
    assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")
    return result.stdout.decode("utf-8").splitlines()


@needs_bash
def test_an_early_failure_in_gpu_test_mode_writes_the_result_object_last() -> None:
    import json

    out = _run_early_failure("GPU_NOT_VISIBLE")
    assert [ln for ln in out if ln.startswith("put:")] == [
        "put:jobs/job1/logs/gpu-test.log",
        "put:jobs/job1/logs/gpu-test-result.json",
    ]
    parsed = json.loads(next(ln for ln in out if ln.startswith("{")))
    assert parsed["mode"] == "gpu-test"
    assert parsed["exitCode"] != 0
    assert parsed["error"] == "GPU_NOT_VISIBLE"
    assert parsed["timedOut"] is False


@needs_bash
def test_an_early_failure_outside_gpu_test_mode_writes_nothing() -> None:
    assert _run_early_failure("GPU_NOT_VISIBLE", mode="") == []


def test_both_early_failure_paths_write_the_gpu_test_result_before_cleanup() -> None:
    text = _code_only()
    for code in ("GPU_NOT_VISIBLE", "IMAGE_PULL_FAILED"):
        lines = [ln.strip() for ln in text.splitlines()]
        idx = next(i for i, ln in enumerate(lines) if ln.startswith(f"gpu_test_early_fail {code}"))
        assert lines[idx + 1].startswith("cleanup "), (code, lines[idx + 1])
        at = text.index(f"gpu_test_early_fail {code}")
        assert text.rindex("status failed", 0, at) > text.rindex("foreign_zone_exit", 0, at), code


def test_the_reboot_short_circuit_in_gpu_test_mode_keys_on_the_result_object_not_a_stale_status() -> None:
    """A reused job id has an old status.json=done in the bucket; gpu-test mode must not treat it
    as "this run already finished" and skip the tests."""
    text = _code_only()
    marker = (
        'if [ "$MODE" = "gpu-test" ]; then\n  gpu_done=$(get_object "jobs/${JOB}/logs/gpu-test-result.json"'
    )
    assert marker in text, "gpu-test mode needs its own short-circuit on logs/gpu-test-result.json"
    at = text.index(marker)
    assert text.index('stage":"(done|failed|cancelled)"') > at


def test_infra_stage_snapshots_carry_stage_started_at() -> None:
    """The app reads a stage's age from status.json after a resume (#90 review), so the snapshots this
    script writes for booting/installing carry stageStartedAt too (the same instant as updatedAt)."""
    text = _code_only()
    assert '"stageStartedAt":"%s"' in text


def test_a_vm_in_a_zone_the_ladder_did_not_keep_refuses_before_writing_anything() -> None:
    """#670: a parallel zone ladder creates the same job's VM in several zones at once and the app records the
    kept one in control/zone. A VM booted in another zone must read it and delete itself BEFORE it writes
    status.json or the startup log under the job prefix, and must not do so through cleanup()."""
    text = _code_only()
    assert "control/zone" in text
    guard_at = text.index("control/zone")
    for first_write in ("relay &", "status booting", "status installing"):
        assert guard_at < text.index(first_write), f"the zone check must precede {first_write!r}"
    guard = text[text.index("foreign_zone_exit()") : text.index("relay()")]
    assert 'curl -sf -X DELETE -H "Authorization: Bearer $(token)"' in guard
    assert "cleanup" not in guard, "cleanup() uploads the startup log into the job prefix"
    assert "put_object" not in guard, "the refusal must write nothing under the job prefix"
    # Checked again just before the worker container starts, in case the claim landed during the boot.
    assert text.count("foreign_zone_exit") >= 4, (
        "defined once, called at the start, after the boot and before docker run"
    )


# --- foreign_zone_exit behaviour, run for real in bash with stubbed curl/kill/shutdown (#670 review) ----


def _run_foreign_zone_exit(*, claimed: str, delete_ok: bool, relay_pid: str | None) -> list[str]:
    """Source the REAL foreign_zone_exit() with stubs for everything it touches and return what it did.
    ``curl`` fails a DELETE when ``delete_ok`` is false; ``kill`` and ``shutdown`` only record their calls."""
    lines = [
        "set -uo pipefail",
        _extract_function("foreign_zone_exit"),
        'ZONE=us-central1-a; NAME=deg-vm; JOB=job1; PROJECT=proj; LOG=""',
        f'token() {{ echo tok; }}; get_object() {{ echo "{claimed}"; }}',
        'curl() { case "$*" in *DELETE*) LOG="$LOG delete"; return '
        + ("0" if delete_ok else "22")
        + " ;; esac; }",
        'kill() { LOG="$LOG kill:$1"; }',
        'shutdown() { LOG="$LOG shutdown:$*"; }',
        "exec 3>&1",  # the trap must write past the redirect the call below is under
        "trap 'echo \"$LOG\" >&3' EXIT",
    ]
    if relay_pid is not None:
        lines.append(f"RELAY_PID={relay_pid}")
    lines += ["foreign_zone_exit >/dev/null", 'LOG="$LOG survived"']
    result = subprocess.run(
        ["bash", "-s"], input=("\n".join(lines) + "\n").encode("utf-8"), capture_output=True, timeout=60
    )
    assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")
    return result.stdout.decode("utf-8").split()


@needs_bash
def test_a_foreign_zone_vm_stops_the_relay_and_deletes_itself() -> None:
    assert _run_foreign_zone_exit(claimed="us-east1-b", delete_ok=True, relay_pid="4242") == [
        "kill:4242",
        "delete",
    ]


@needs_bash
def test_a_foreign_zone_vm_whose_self_delete_fails_shuts_the_guest_down() -> None:
    calls = _run_foreign_zone_exit(claimed="us-east1-b", delete_ok=False, relay_pid="4242")
    assert calls == ["kill:4242", "delete", "shutdown:-h", "now"], calls


@needs_bash
def test_a_foreign_zone_vm_before_the_relay_exists_does_not_trip_on_the_unset_pid() -> None:
    assert _run_foreign_zone_exit(claimed="us-east1-b", delete_ok=True, relay_pid=None) == ["delete"]


@needs_bash
@pytest.mark.parametrize("claimed", ["", "us-central1-a"])
def test_the_kept_zone_or_no_claim_carries_on(claimed: str) -> None:
    assert _run_foreign_zone_exit(claimed=claimed, delete_ok=True, relay_pid="4242") == ["survived"]


def test_the_zone_check_is_the_line_right_before_each_status_write_that_precedes_the_worker() -> None:
    """The guard must sit immediately before `status booting` and `status installing`, not after them: a VM in
    the wrong zone that has already written booting/installing has written into the winner's prefix."""
    lines = [ln.strip() for ln in _code_only().splitlines() if ln.strip()]
    for stage in ("status booting", "status installing"):
        at = lines.index(stage)
        assert lines[at - 1].startswith("foreign_zone_exit"), (
            f"the zone check must immediately precede {stage!r}"
        )


# --- the ERR trap must not fire on the worker's legitimate exit codes (#494) ---------------


def _run_docker_tail(rc: int, lifecycle: str = "stop") -> list[str]:
    """Run the REAL ERR trap line and the real docker-run-to-end tail of startup.sh with a stub
    ``docker`` that exits ``rc``, and return what status/cleanup/put_object/kill recorded."""
    import re

    text = _text()
    trap = next(ln for ln in text.splitlines() if ln.strip().startswith("trap "))
    tail = text[text.index("\ndocker run --rm") :]
    assert re.search(r"^docker run --rm", tail, re.MULTILINE), "tail must contain the docker run"
    lines = [
        "set -uo pipefail",
        'LOG=""; EXPECT_GPU=false; JOBURI=gs://b/jobs/j; BUCKET=b; NAME=vm; ZONE=z; PROJECT=p',
        "IMAGE=img; JOB=j; MAX_RUN_MIN=60; RELAY_PID=999999; MODE=; GPU_FLAGS=()",
        f"LIFECYCLE={lifecycle}",
        'status() { LOG="$LOG status:$1:${2:-}"; }',
        'cleanup() { LOG="$LOG cleanup:$1"; }',
        'put_object() { LOG="$LOG put"; }',
        'kill() { LOG="$LOG kill"; }',
        "foreign_zone_exit() { :; }",
        "mkdir() { :; }; chown() { :; }",
        f"docker() {{ return {rc}; }}",
        trap,
        "exec 3>&1",
        tail,
        'echo "$LOG" >&3',
    ]
    result = subprocess.run(
        ["bash", "-s"], input=("\n".join(lines) + "\n").encode("utf-8"), capture_output=True, timeout=60
    )
    assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")
    return result.stdout.decode("utf-8").split()


@needs_bash
@pytest.mark.parametrize("rc", [2, 3, 10, 11])
def test_a_legitimate_worker_exit_code_does_not_write_worker_crash(rc: int) -> None:
    """A non-zero `docker run` used to fire the ERR trap, which wrote WORKER_CRASH over the
    worker's own terminal status.json and ran cleanup a second time (#494)."""
    calls = _run_docker_tail(rc)
    assert not any("WORKER_CRASH" in c for c in calls), calls
    assert not any(c.startswith("status:") for c in calls), calls
    cleanups = [c for c in calls if c.startswith("cleanup:")]
    assert cleanups == ([] if rc in (10, 11) else ["cleanup:stop"]), calls


@needs_bash
def test_a_clean_exit_runs_cleanup_exactly_once() -> None:
    assert [c for c in _run_docker_tail(0) if c.startswith("cleanup:")] == ["cleanup:stop"]


@needs_bash
@pytest.mark.parametrize("rc", [1, 125, 126, 127, 137])
def test_an_unknown_exit_code_still_reports_worker_crash_once(rc: int) -> None:
    """docker itself failing (125-127) or the container being killed (137) is a real crash: the
    status must still say WORKER_CRASH, and cleanup still runs exactly once."""
    calls = _run_docker_tail(rc)
    assert sum("WORKER_CRASH" in c for c in calls) == 1, calls
    assert [c for c in calls if c.startswith("cleanup:")] == ["cleanup:stop"], calls


# --- second cold review (#759) ---------------------------------------------------------------


def _selection_code() -> str:
    """The Python the VM runs inside the image (the heredoc in gpu_test_run), verbatim."""
    text = _text()
    start = text.index("<<'PY'\n") + len("<<'PY'\n")
    return text[start : text.index("\nPY\n", start)]


def _run_selection(files: dict[str, str]) -> tuple[int, str]:
    """Run the REAL selection code in a scratch tests/ folder with this venv's pytest."""
    import sys
    import tempfile
    from pathlib import Path

    with tempfile.TemporaryDirectory() as tmp:
        (Path(tmp) / "tests").mkdir()
        for name, body in files.items():
            (Path(tmp) / "tests" / name).write_text(body, encoding="utf-8", newline="\n")
        done = subprocess.run(
            [sys.executable, "-I", "-c", _selection_code()],
            cwd=tmp,
            capture_output=True,
            text=True,
            timeout=120,
        )
        return done.returncode, done.stdout + done.stderr


_GPU_OK = (
    "import pytest\n\n\n@pytest.mark.gpu\ndef test_on_gpu():\n    assert True\n\n\n"
    "def test_unmarked_would_fail():\n    assert False\n"
)
_GPU_BROKEN = "import pytest\nimport a_dev_only_dependency_not_installed\n\n\n@pytest.mark.gpu\ndef test_x():\n    pass\n"
_DEV_ONLY_BROKEN = "import another_dev_only_dependency_not_installed\n\n\ndef test_y():\n    pass\n"


def test_selection_runs_only_the_gpu_marked_tests_and_passes() -> None:
    code, out = _run_selection({"test_a.py": _GPU_OK})
    assert code == 0, out
    assert "1 passed" in out
    assert "1 deselected" in out  # `-m gpu` reached the real run: the unmarked failing test did not run


def test_selection_leaves_out_a_dev_only_file_that_cannot_import_and_mentions_no_gpu_marker() -> None:
    code, out = _run_selection({"test_a.py": _GPU_OK, "test_dev.py": _DEV_ONLY_BROKEN})
    assert code == 0, out


def test_selection_fails_the_run_for_a_gpu_marked_file_that_cannot_be_collected() -> None:
    """The run must not PASS on the other files while a gpu test silently never ran."""
    code, out = _run_selection({"test_a.py": _GPU_OK, "test_b.py": _GPU_BROKEN})
    assert code == 6, out
    assert "ERROR: gpu test file could not be collected: tests/test_b.py" in out
    assert "1 passed" in out  # the good file still ran, so the log shows both


def test_selection_with_no_gpu_tests_at_all_is_exit_5() -> None:
    code, out = _run_selection({"test_dev.py": _DEV_ONLY_BROKEN})
    assert code == 5, out


@needs_bash
def test_gpu_test_run_reports_a_collection_error_as_exit_6_with_an_error_code() -> None:
    import json

    summary = "ERROR: gpu test file could not be collected: tests/test_b.py\n== 1 passed in 0.1s =="
    out = _run_gpu_test_run(6, summary=summary)
    parsed = json.loads(next(ln for ln in out if ln.startswith("{")))
    assert parsed["exitCode"] == 6
    assert parsed["error"] == "GPU_TEST_COLLECTION_ERROR"
    assert "status:failed" in out


@needs_bash
def test_gpu_test_run_turns_a_collection_error_into_a_failure_even_if_the_selection_exited_0() -> None:
    import json

    out = _run_gpu_test_run(0, summary="ERROR: gpu test file could not be collected: tests/test_b.py")
    parsed = json.loads(next(ln for ln in out if ln.startswith("{")))
    assert (parsed["exitCode"], parsed["error"]) == (6, "GPU_TEST_COLLECTION_ERROR")


@needs_bash
def test_gpu_test_run_result_has_a_null_error_when_there_is_none() -> None:
    import json

    parsed = json.loads(next(ln for ln in _run_gpu_test_run(0) if ln.startswith("{")))
    assert parsed["error"] is None


@needs_bash
def test_exit_137_after_the_full_limit_is_a_timeout_and_kills_the_container() -> None:
    import json

    out = _run_gpu_test_run(137, elapsed=3000)  # limit is 3000 s
    parsed = json.loads(next(ln for ln in out if ln.startswith("{")))
    assert (parsed["exitCode"], parsed["timedOut"]) == (124, True)
    assert "docker:kill deg-gputest-job1" in out


@needs_bash
def test_exit_137_before_the_limit_is_an_oom_style_kill_not_a_timeout() -> None:
    import json

    out = _run_gpu_test_run(137, elapsed=2999)
    parsed = json.loads(next(ln for ln in out if ln.startswith("{")))
    assert (parsed["exitCode"], parsed["timedOut"]) == (137, False)
    assert not any(ln.startswith("docker:kill") for ln in out)


@needs_bash
def test_a_log_that_could_not_be_written_is_never_a_pass() -> None:
    """tee failing (disk full, unwritable path) used to read as rc 0 because only PIPESTATUS[0] was kept."""
    import json

    out = _run_gpu_test_run(0, bad_log=True)
    parsed = json.loads(next(ln for ln in out if ln.startswith("{")))
    assert parsed["exitCode"] == 8
    assert "status:failed" in out


def test_the_err_trap_in_gpu_test_mode_writes_the_early_failure_result_before_cleanup() -> None:
    trap = next(ln for ln in _text().splitlines() if ln.strip().startswith("trap "))
    assert "gpu_test_early_fail WORKER_CRASH" in trap
    assert trap.index("gpu_test_early_fail") < trap.index("cleanup")


@needs_bash
def test_a_failing_early_step_in_gpu_test_mode_writes_the_result_object_through_the_real_trap() -> None:
    import json

    trap = next(ln for ln in _text().splitlines() if ln.strip().startswith("trap "))
    lines = [
        "set -uo pipefail",
        _extract_function("gpu_test_early_fail"),
        'MODE=gpu-test; IMAGE="ghcr.io/x/y@sha256:abc"; JOB=job1; NAME=deg-job1; ZONE=z1; LIFECYCLE=delete',
        'cd "$(mktemp -d)" || exit 1',
        "GPU_TEST_LOG=$PWD/gpu.log; GPU_TEST_RESULT=$PWD/result.json",
        'put_object() { echo "put:$2"; '
        '[ "${2##*/}" = "gpu-test-result.json" ] && { cat "$1"; echo; }; true; }',
        'status() { echo "status:$1"; }',
        'cleanup() { echo "cleanup:$1"; }',
        trap,
        "false",  # the failing mkdir/chown/df of the boot steps
    ]
    done = subprocess.run(
        ["bash", "-s"], input=("\n".join(lines) + "\n").encode("utf-8"), capture_output=True, timeout=60
    )
    out = done.stdout.decode("utf-8").splitlines()
    assert "put:jobs/job1/logs/gpu-test-result.json" in out, out
    assert out.index("cleanup:delete") > out.index("put:jobs/job1/logs/gpu-test-result.json")
    parsed = json.loads(next(ln for ln in out if ln.startswith("{")))
    assert parsed["error"] == "WORKER_CRASH"
