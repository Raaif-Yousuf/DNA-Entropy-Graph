#!/usr/bin/env bash
# worker/vm/startup.sh — the per-job VM's Compute Engine `startup-script` metadata value.
#
# Reference implementation transcribed from
# docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md section 4.5, hardened and
# commented for issue #45. Runs on EVERY boot, including a restart after a STOP (Compute
# Engine re-runs `startup-script` on every boot, not just the first) — every step below is
# written to be a correct no-op (or a safe retry) if it has already happened once.
#
# Hard rules this script exists to satisfy (docs/cloud_design.md section 8, CLAUDE.md
# Critical Pitfalls, and the CLAIR #2908 incident this design was written to never repeat):
#   - `instanceTerminationAction=DELETE` fires ONLY when Compute Engine itself stops the
#     VM at its `maxRunDuration` deadline — NEVER on a guest `shutdown`. So cleanup() calls
#     the Compute API on itself (stop/delete) as the PRIMARY mechanism; `shutdown -h` is
#     armed once, as a deadman, purely as a last resort if the API call itself is what fails.
#   - No SSH anywhere (docs/cloud_design.md section 9): every network call here is either
#     the metadata server (instance-local, no auth) or an authenticated REST call using the
#     instance's own metadata-token credentials.
#   - `put_object` uses curl unconditionally (no gcloud, no Python SDK). The CPU smoke VM boots
#     the same DLVM image as the GPU VM (issue #56, DECISION #615; Container-Optimized OS has no
#     python3 and a read-only root), so one script serves both and `--gpus all` is added only
#     when a GPU is expected. See worker/Dockerfile.cpu / issue #36.
#
# Per-job values (bucket, job id, worker image, lifecycle, expected GPU, max run minutes)
# are read from THIS INSTANCE's own metadata attributes at boot time via meta(), never
# baked into the script text itself — the app sets those attributes once at VM creation
# (design section 5.4); this file is a fixed template, not a per-job render (the metadata
# VALUE size guard, issue #261, is the C# StartupScriptBuilder's concern for those
# per-instance attribute VALUES, not this script's own, fixed, size).

set -uo pipefail  # deliberately not -e: every fallible step below is checked explicitly,
                   # and a background relay/trap-driven script is easier to reason about
                   # without -e's surprising interaction with pipelines and subshells.
exec > >(tee -a /var/log/deg-startup.log) 2>&1

MD='http://metadata.google.internal/computeMetadata/v1'
H='Metadata-Flavor: Google'

meta() { curl -sf -H "$H" "$MD/$1"; }

BUCKET=$(meta instance/attributes/deg-bucket)
JOB=$(meta instance/attributes/deg-job-id)
IMAGE=$(meta instance/attributes/deg-worker-image)          # a full ref@sha256:<digest>
EXPECT_GPU=$(meta instance/attributes/deg-expect-gpu)        # "true" | "false"
LIFECYCLE=$(meta instance/attributes/deg-lifecycle)          # "stop" | "delete" | "keep"
MAX_RUN_MIN=$(meta instance/attributes/deg-max-run-min)
MODE=$(meta instance/attributes/deg-mode)                    # "" (a job) | "gpu-test" (issue #759: run `pytest -m gpu` in the image)
NAME=$(meta instance/name)
ZONE=$(basename "$(meta instance/zone)")
PROJECT=$(meta project/project-id)
JOBURI="gs://${BUCKET}/jobs/${JOB}"

token() {
  curl -sf -H "$H" "$MD/instance/service-accounts/default/token" \
    | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])'
}

put_object() {
  # $1 = local file, $2 = object name (relative to the bucket root, e.g. jobs/<job>/x).
  curl -sf -X POST -H "Authorization: Bearer $(token)" -T "$1" \
    "https://storage.googleapis.com/upload/storage/v1/b/${BUCKET}/o?uploadType=media&name=$2"
}

get_object() {
  # $1 = object name; prints its content to stdout, or nothing (and a non-zero exit) if
  # it does not exist yet — used for the already-finished short-circuit below.
  curl -sf -H "Authorization: Bearer $(token)" \
    "https://storage.googleapis.com/storage/v1/b/${BUCKET}/o/$(python3 -c "import urllib.parse,sys;print(urllib.parse.quote(sys.argv[1], safe=''))" "$1")?alt=media"
}

status() {
  # $1 = stage, $2 = JSON error object or omitted for null. Writes ONLY the infra-level
  # stages this script owns (booting/installing/GPU-not-visible/image-pull-failed) — the
  # container's own worker process (dna-entropy-worker) writes every stage from
  # restoring-cache through done/failed/cancelled and owns the real heartbeat cadence; this
  # function exists so the app sees SOMETHING before the container has even started.
  local ts
  ts="$(date -u +%FT%TZ)"
  printf '{"schema":1,"jobId":"%s","stage":"%s","stageStartedAt":"%s","updatedAt":"%s","heartbeatSeq":0,"vm":{"name":"%s","zone":"%s"},"worker":{"version":"","image":""},"percent":0,"detail":{},"error":%s}\n' \
    "$JOB" "$1" "$ts" "$ts" "$NAME" "$ZONE" "${2:-null}" > /tmp/deg-status.json
  put_object /tmp/deg-status.json "jobs/${JOB}/status.json" || true  # never fatal: a
    # failed status write must not itself take down the job — the heartbeat/death-detection
    # timeout on the app side is exactly the backstop for "the VM is unreachable".
}

foreign_zone_exit() {
  # Issue #670: a parallel zone ladder creates this job's VM in several zones at once. The app deletes every zone but the one it kept and
  # records that zone in jobs/<job>/control/zone; a VM that is not in it ends ITSELF here, before it writes status.json or the log under
  # the job prefix (the winner owns both). No object yet means no parallel batch (or the claim has not landed): carry on. This does NOT
  # go through cleanup(), which uploads the startup log into the job prefix.
  local claimed
  claimed=$(get_object "jobs/${JOB}/control/zone" 2>/dev/null || true)
  if [ -n "$claimed" ] && [ "$claimed" != "$ZONE" ]; then
    echo "zone ${ZONE} is not the zone this job kept (${claimed}): deleting this VM without touching the job prefix"
    # The relay (once it runs) would keep writing startup.log into the winner's prefix: stop it first.
    if [ -n "${RELAY_PID:-}" ]; then
      kill "$RELAY_PID" 2>/dev/null || true
    fi
    if ! curl -sf -X DELETE -H "Authorization: Bearer $(token)" \
      "https://compute.googleapis.com/compute/v1/projects/${PROJECT}/zones/${ZONE}/instances/${NAME}"; then
      # LAST RESORT, the one place a guest shutdown is right: the Compute API call failed, and this VM must not keep running a
      # job that belongs to another zone. The app also deletes every extra by zone, and maxRunDuration stays the backstop.
      echo "self-delete failed: shutting the guest down so this VM stops running"
      shutdown -h now
    fi
    exit 0
  fi
}
foreign_zone_exit
# Relay startup.log to the bucket every 30s in the background for the whole life of this
# script, so a human debugging a stuck VM can read it without a console session.
relay() {
  while true; do
    put_object /var/log/deg-startup.log "jobs/${JOB}/logs/startup.log" || true
    sleep 30
  done
}
relay &
RELAY_PID=$!

cleanup() {
  # $1 = "stop" | "delete". NEVER `shutdown -h` as the primary mechanism (CLAUDE.md
  # Critical Pitfalls / CLAIR #2908): instanceTerminationAction does not fire on a guest
  # shutdown, so this calls the Compute API on itself instead.
  kill "$RELAY_PID" 2>/dev/null || true
  put_object /var/log/deg-startup.log "jobs/${JOB}/logs/startup.log" || true
  local url="https://compute.googleapis.com/compute/v1/projects/${PROJECT}/zones/${ZONE}/instances/${NAME}"
  case "$1" in
    delete) curl -sf -X DELETE -H "Authorization: Bearer $(token)" "$url" || true ;;
    # "keep" reaching HERE (an error trap, a failed boot step, or the reboot short-circuit)
    # stops: no window is being honoured on those paths, and Hard Rule 11 forbids keeping a
    # VM with no expiry. The normal keep path goes through keep_hold, which waits out the
    # window and then calls cleanup with stop or delete. Any unknown value also stops: the
    # billing meter must never be left running.
    stop|keep|*) curl -sf -X POST -H "Authorization: Bearer $(token)" "${url}/stop" || true ;;
  esac
}

# Keep-alive (issue #464, Hard Rule 11): this script owns the window. `deg-lifecycle=keep`
# used to fall through cleanup() and stop the VM the moment the container exited.
#
# keep_plan MANIFEST MAX_RUN_MIN ELAPSED_S prints "<wait seconds> <stop|delete>". The wait
# is lifecycle.keepAliveMinutes from the manifest, capped so the VM is stopped or deleted
# at least 10 minutes before maxRunDuration (which, with instanceTerminationAction=DELETE,
# is the hard backstop; the shutdown deadman armed above is the last resort). Anything
# unreadable, missing, negative or non-numeric means "0 stop": nothing to hold the VM for,
# so it stops now. afterKeepAlive can only be stop or delete, never keep.
keep_plan() {
  python3 - "$1" "$2" "$3" <<'PY' 2>/dev/null || echo "0 stop"
import json, sys
path, max_run_min, elapsed = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
wait, after = 0, "stop"
try:
    with open(path, encoding="utf-8") as fh:
        life = json.load(fh).get("lifecycle") or {}
    minutes = life.get("keepAliveMinutes", 0)
    if isinstance(minutes, int) and not isinstance(minutes, bool) and minutes > 0:
        wait = max(0, min(minutes * 60, max_run_min * 60 - elapsed - 600))
    if life.get("afterKeepAlive") == "delete":
        after = "delete"
except Exception:
    wait, after = 0, "stop"
print(wait, after)
PY
}

# keep_hold RC: RC is the worker's exit code (0 done, 2 failed, 3 cancelled).
# DECISION (agent-made on the owner's behalf, reversible): only a SUCCESSFUL run (RC 0) is
# held for its window. A failed or cancelled run applies afterKeepAlive (default stop)
# immediately: holding it would bill idle GPU time for a job that produced nothing worth
# keeping warm for. Elapsed time is the VM's real uptime (/proc/uptime, which also counts
# the boot and the docker pull), falling back to $SECONDS only if that file is unreadable.
keep_hold() {
  local rc=$1 plan wait_s after end up
  up=$(cut -d. -f1 /proc/uptime 2>/dev/null)
  plan=$(keep_plan /work/manifest.json "$MAX_RUN_MIN" "${up:-$SECONDS}")
  wait_s=${plan%% *}
  after=${plan#* }
  if [ "$rc" != "0" ]; then
    echo "keep-alive: worker exit code ${rc} (not a successful run), so no hold: applying ${after} now"
    wait_s=0
  else
    echo "keep-alive: holding the VM for ${wait_s}s, then applying ${after} (maxRunDuration and the shutdown deadman remain the backstops)"
  fi
  end=$(( SECONDS + wait_s ))
  while [ "$SECONDS" -lt "$end" ]; do
    sleep 15
  done
  cleanup "$after"
}

# gpu_test_early_fail CODE MESSAGE (issue #759): in gpu-test mode, a failure BEFORE the tests can start (GPU not visible, image pull
# failed) still ends in gpu-test.log and, LAST, gpu-test-result.json, so scripts/cloud_gpu_test.ps1 stops polling at once instead of
# waiting out its whole deadline. Outside gpu-test mode it does nothing (a job reports through status.json and result.json). exitCode 7
# is the infrastructure failure; "error" carries the code. GPU_TEST_LOG / GPU_TEST_RESULT exist so a test can point the files elsewhere.
gpu_test_early_fail() {
  [ "$MODE" = "gpu-test" ] || return 0
  local log="${GPU_TEST_LOG:-/var/log/deg-gpu-test.log}" result="${GPU_TEST_RESULT:-/tmp/deg-gpu-test-result.json}"
  echo "ERROR: $1: $2" > "$log"
  put_object "$log" "jobs/${JOB}/logs/gpu-test.log" || true
  printf '{"schema":1,"jobId":"%s","mode":"gpu-test","exitCode":7,"timedOut":false,"error":"%s","image":"%s","vm":{"name":"%s","zone":"%s"},"finishedAt":"%s"}\n' \
    "$JOB" "$1" "$IMAGE" "$NAME" "$ZONE" "$(date -u +%FT%TZ)" > "$result"
  put_object "$result" "jobs/${JOB}/logs/gpu-test-result.json" || true
}

# gpu_test_run (issue #759, scripts/cloud_gpu_test.ps1): the whole of gpu-test mode. Runs `pytest -m gpu` inside the image (the
# image must carry pytest and worker/tests, see Dockerfile.cuda), uploads the log and then, LAST, gpu-test-result.json (the file
# the caller polls for, like result.json for a job), and applies the lifecycle. Returns pytest's exit code.
#   - The deadline is counted from BOOT (/proc/uptime, like keep_hold: the GPU wait and the image pull already used part of
#     maxRunDuration) and ends 10 minutes before maxRunDuration (at least 60 s), so a hung test is cut off before Compute Engine
#     kills the VM.
#   - The container runs under `--init` with a NAME. `timeout` signals the docker CLI, and with python as PID 1 (no SIGTERM handler)
#     the signal never reached the tests; tini forwards it. `timeout -k 30` escalates to KILL, and on a timeout `docker kill <name>`
#     stops the container itself. exit 124 is normalised for both timeout outcomes.
#   - The tests are selected by COLLECTION (`pytest --collect-only -m gpu` per file inside the image), not by grepping file text: a
#     file that only mentions the marker is not run and a marker set another way is not missed. A file that cannot be collected and
#     carries a gpu marker FAILS the run (exit 6, error GPU_TEST_COLLECTION_ERROR); one that needs a dev-only dependency and has no
#     marker is named in the log and left out.
# GPU_TEST_LOG / GPU_TEST_RESULT / GPU_TEST_UPTIME_FILE exist so a test can point the files somewhere writable.
gpu_test_run() {
  local log="${GPU_TEST_LOG:-/var/log/deg-gpu-test.log}" result="${GPU_TEST_RESULT:-/tmp/deg-gpu-test-result.json}"
  local cname="deg-gputest-${JOB}" up limit rc timed_out=false started=$SECONDS select_py err="" err_json=null tee_rc=0 rcs elapsed
  up=$(cut -d. -f1 "${GPU_TEST_UPTIME_FILE:-/proc/uptime}" 2>/dev/null)
  limit=$(( (MAX_RUN_MIN - 10) * 60 - ${up:-$SECONDS} ))
  if [ "$limit" -lt 60 ]; then
    limit=60
  fi
  select_py=$(cat <<'PY'
import glob, re, subprocess, sys
files, bad = [], []
for f in sorted(glob.glob("tests/test_*.py")):
    name = f.replace("\\", "/")
    r = subprocess.run([sys.executable, "-m", "pytest", "--collect-only", "-q", "-m", "gpu", "-p", "no:cacheprovider", f],
                       capture_output=True, text=True)
    if r.returncode == 0:
        files.append(f)
    elif r.returncode != 5:
        with open(f, encoding="utf-8", errors="replace") as fh:
            marked = re.search(r"mark\.gpu|pytestmark", fh.read())
        if marked:
            bad.append(name)
            print("ERROR: gpu test file could not be collected:", name, flush=True)
        else:
            print("not collected (needs a dev-only dependency, carries no gpu marker):", name, flush=True)
rc = 5
if files:
    rc = subprocess.call([sys.executable, "-m", "pytest", "-m", "gpu", "-p", "no:cacheprovider", "-q", "-ra", *files])
elif not bad:
    print("no gpu test files found", flush=True)
if bad and rc in (0, 5):
    rc = 6
sys.exit(rc)
PY
)
  status running
  docker rm -f "$cname" >/dev/null 2>&1 || true  # a leftover of an earlier boot of this job would block --name
  if timeout -k 30 "$limit" docker run --rm --init --name "$cname" \
    "${GPU_FLAGS[@]}" --shm-size=8g \
    -v /var/cache/deg-hf:/hf-cache \
    -e HF_HOME=/hf-cache \
    -w /opt/worker \
    "$IMAGE" \
    python -c "$select_py" 2>&1 | tee "$log"; then
    rc=0
  else
    rcs=("${PIPESTATUS[@]}")
    rc=${rcs[0]}
    tee_rc=${rcs[1]:-0}
  fi
  # A log that could not be written is not a pass: under pipefail a failing tee also lands in the else branch, but with docker's rc 0.
  if [ "$rc" = "0" ] && { [ "$tee_rc" != "0" ] || [ ! -s "$log" ]; }; then
    echo "ERROR: the test log could not be written (exit 8), so nothing can be shown to have passed."
    rc=8
    err=GPU_TEST_LOG_NOT_WRITTEN
  fi
  # 124: `timeout` ended it; 137 after the full limit: the -k KILL had to finish it. Either way stop the container by name.
  # GPU_TEST_ELAPSED lets a test stand in for the time the tests took.
  elapsed=${GPU_TEST_ELAPSED:-$(( SECONDS - started ))}
  if [ "$rc" = "124" ] || { [ "$rc" = "137" ] && [ "$elapsed" -ge "$limit" ]; }; then
    timed_out=true
    rc=124
    docker kill "$cname" || true
  fi
  # A skipped GPU test is not a pass: with torch or evo2 not importable every gpu test skips itself and pytest still exits 0.
  # Exit code 6 says "the GPU tests did not really run".
  if [ "$rc" = "0" ] && grep -Eq '[0-9]+ skipped|no tests ran' "$log"; then
    echo "ERROR: pytest -m gpu skipped tests or ran none, so nothing was proven (exit 6): is the GPU stack importable in the image?"
    rc=6
  fi
  # A gpu-marked file that could not be collected means a gpu test never ran: the run cannot pass on the other files.
  if grep -q '^ERROR: gpu test file could not be collected' "$log" 2>/dev/null; then
    err=GPU_TEST_COLLECTION_ERROR
    if [ "$rc" = "0" ]; then
      rc=6
    fi
  fi
  if [ -n "$err" ]; then
    err_json="\"$err\""
  fi
  put_object "$log" "jobs/${JOB}/logs/gpu-test.log" || true
  printf '{"schema":1,"jobId":"%s","mode":"gpu-test","exitCode":%s,"timedOut":%s,"error":%s,"image":"%s","vm":{"name":"%s","zone":"%s"},"finishedAt":"%s"}\n' \
    "$JOB" "$rc" "$timed_out" "$err_json" "$IMAGE" "$NAME" "$ZONE" "$(date -u +%FT%TZ)" > "$result"
  if [ "$rc" = "0" ]; then
    status done
  else
    status failed '{"code":"GPU_TESTS_FAILED","retriable":false}'
  fi
  put_object "$result" "jobs/${JOB}/logs/gpu-test-result.json" || true
  cleanup "$LIFECYCLE"
  return "$rc"
}

# Any uncaught error anywhere below this line reports WORKER_CRASH and applies the
# configured lifecycle rather than leaving the VM to bill silently forever.
# In gpu-test mode the early-failure result is written too, so the caller does not wait out its deadline (a no-op for a job).
trap 'status failed "{\"code\":\"WORKER_CRASH\",\"retriable\":false}"; gpu_test_early_fail WORKER_CRASH "an unexpected error stopped the boot steps"; cleanup "$LIFECYCLE"' ERR

# Deadman: if nothing else ever calls cleanup (e.g. this script itself hangs before
# reaching one), the guest shuts down MAX_RUN_MIN+15 minutes after boot. This is a LAST
# RESORT, not the primary mechanism — see the module docstring above — and specifically
# does NOT rely on instanceTerminationAction, since a guest `shutdown` never fires that;
# it exists only to stop the meter if every API-based cleanup path has already failed.
shutdown -h "+$(( MAX_RUN_MIN + 15 ))" &

# --- idempotent-on-reboot short-circuit ------------------------------------------------
# startup-script re-runs on every boot, including a restart after STOP. If a previous boot
# already reached a terminal stage, do not repeat the whole install/run cycle — just apply
# the lifecycle again (harmless if it already happened) and exit clean.
# gpu-test mode keys on its OWN result object: a reused job id can carry an old status.json=done from an earlier run, which must not
# make a fresh VM skip the tests (the caller also refuses a job id whose result object already exists).
if [ "$MODE" = "gpu-test" ]; then
  gpu_done=$(get_object "jobs/${JOB}/logs/gpu-test-result.json" 2>/dev/null || true)
  if [ -n "$gpu_done" ]; then
    cleanup "$LIFECYCLE"
    exit 0
  fi
elif get_object "jobs/${JOB}/status.json" 2>/dev/null | grep -Eq '"stage":"(done|failed|cancelled)"'; then
  cleanup "$LIFECYCLE"
  exit 0
fi

foreign_zone_exit  # before this VM writes anything under the job prefix (#670)
status booting
df -h / | tail -1

if [ "$EXPECT_GPU" = "true" ]; then
  gpu_up=false
  for _ in $(seq 1 30); do  # up to 5 minutes: never assume the driver is up immediately
    if nvidia-smi -L >/dev/null 2>&1; then
      gpu_up=true
      break
    fi
    sleep 10
  done
  if [ "$gpu_up" != true ]; then
    status failed '{"code":"GPU_NOT_VISIBLE","message":"GPU driver did not come up within 5 minutes","retriable":false}'
    gpu_test_early_fail GPU_NOT_VISIBLE "GPU driver did not come up within 5 minutes"
    cleanup delete  # a box whose GPU never came up is not worth stopping-and-reusing
    exit 1
  fi
fi

foreign_zone_exit  # the claim may have landed during the boot: before the next write (#670)
status installing

# Pull BY DIGEST (IMAGE is a full ref@sha256:<digest> from the manifest, never a mutable
# tag) so every VM in a batch, and every retry, runs byte-identical bits.
if ! docker pull "$IMAGE"; then
  status failed '{"code":"IMAGE_PULL_FAILED","retriable":true}'
  gpu_test_early_fail IMAGE_PULL_FAILED "docker pull of the worker image failed"
  cleanup "$LIFECYCLE"
  exit 1
fi

mkdir -p /var/cache/deg-hf
chown 10001:10001 /var/cache/deg-hf
# `--gpus all` only when a GPU is expected: the CPU smoke VM boots the same image with no GPU (issue #56), and docker run fails on it with the flag.
GPU_FLAGS=()
if [ "$EXPECT_GPU" = "true" ]; then
  GPU_FLAGS=(--gpus all)
fi

# gpu-test mode (issue #759): no manifest, no job; run the GPU tests in the image and finish. `|| exit 1` (not a bare call) keeps
# a failing pytest from tripping the ERR trap, which would overwrite the status and apply the lifecycle a second time.
if [ "$MODE" = "gpu-test" ]; then
  gpu_test_run || exit 1
  exit 0
fi

mkdir -p /work
if ! get_object "jobs/${JOB}/manifest.json" > /work/manifest.json; then
  status failed '{"code":"MANIFEST_INVALID","message":"could not download manifest.json","retriable":false}'
  cleanup "$LIFECYCLE"
  exit 1
fi

# From here on, the CONTAINER (dna-entropy-worker) owns every stage from restoring-cache
# through done/failed/cancelled, the real heartbeat/progress cadence, uploading outputs,
# and writing result.json LAST (docs/job_contract.md). This script's job is done once the
# container is handed off; it only interprets the container's own exit code below.
# Issue #75: the worker image runs as uid 10001 (Dockerfile.cuda `USER worker`), so the
# Hugging Face cache must be a folder that uid can write, and HF_HOME must point at it:
# the old `/root/.cache/huggingface` mount was never read or written by that user (the folder
# is made, and GPU_FLAGS set, above the gpu-test branch because both modes need them).
foreign_zone_exit  # last look before the worker container writes anything (#670)
docker run --rm \
  "${GPU_FLAGS[@]}" --shm-size=8g \
  -v /work:/work \
  -v /var/cache/deg-hf:/hf-cache \
  -e HF_HOME=/hf-cache \
  -e DEG_JOB_URI="$JOBURI" \
  -e DEG_BUCKET="$BUCKET" \
  -e DEG_VM_NAME="$NAME" \
  -e DEG_ZONE="$ZONE" \
  -e DEG_PROJECT="$PROJECT" \
  "$IMAGE" \
  dna-entropy-worker run --manifest /work/manifest.json --store gcs \
  && rc=0 || rc=$?
# `&& ... || rc=$?` makes the non-zero exit a tested condition, so the ERR trap does not fire
# on the worker's legitimate exit codes (2, 3, 10, 11): the trap used to overwrite the
# worker's own terminal status.json with WORKER_CRASH and run cleanup a second time (#494,
# MEASURED 2026-10-15 against the real trap line with a stub docker).

# worker/src/dna_entropy/worker/cli.py's exit-code contract:
#   0 done | 2 failed | 3 cancelled | 10 stopped-by-worker | 11 deleted-by-worker
# 10/11 mean the worker already stopped/deleted this VM itself through the Compute API
# (and its accepted call takes precedence over the outcome; result.json carries the real
# outcome). Calling the API a second time would repeat a finished action, so for 10/11
# this only uploads the startup log and exits. If the worker's own call failed it returns
# the ordinary outcome code instead, and 0/2/3 still run cleanup "$LIFECYCLE" as the
# backstop. With lifecycle=keep the 0/2/3 branch goes to keep_hold: DECISION (agent-made on
# the owner's behalf, reversible): only rc 0 is held for the keep-alive window; rc 2 and 3
# apply afterKeepAlive at once.
# docker itself failed (125-127) or the container was killed (137, OOM): the worker never wrote
# its own terminal state, so report the crash here, once.
if [ "$rc" != 0 ] && [ "$rc" != 2 ] && [ "$rc" != 3 ] && [ "$rc" != 10 ] && [ "$rc" != 11 ]; then
  status failed '{"code":"WORKER_CRASH","retriable":false}'
fi
case $rc in
  10|11)
    kill "$RELAY_PID" 2>/dev/null || true
    put_object /var/log/deg-startup.log "jobs/${JOB}/logs/startup.log" || true
    ;;
  *)
    if [ "$LIFECYCLE" = "keep" ]; then
      keep_hold "$rc"
    else
      cleanup "$LIFECYCLE"
    fi
    ;;
esac
