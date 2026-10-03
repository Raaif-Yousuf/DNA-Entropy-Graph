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
  printf '{"schema":1,"jobId":"%s","stage":"%s","updatedAt":"%s","heartbeatSeq":0,"vm":{"name":"%s","zone":"%s"},"worker":{"version":"","image":""},"percent":0,"detail":{},"error":%s}\n' \
    "$JOB" "$1" "$(date -u +%FT%TZ)" "$NAME" "$ZONE" "${2:-null}" > /tmp/deg-status.json
  put_object /tmp/deg-status.json "jobs/${JOB}/status.json" || true  # never fatal: a
    # failed status write must not itself take down the job — the heartbeat/death-detection
    # timeout on the app side is exactly the backstop for "the VM is unreachable".
}

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

# Any uncaught error anywhere below this line reports WORKER_CRASH and applies the
# configured lifecycle rather than leaving the VM to bill silently forever.
trap 'status failed "{\"code\":\"WORKER_CRASH\",\"retriable\":false}"; cleanup "$LIFECYCLE"' ERR

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
if get_object "jobs/${JOB}/status.json" 2>/dev/null | grep -Eq '"stage":"(done|failed|cancelled)"'; then
  cleanup "$LIFECYCLE"
  exit 0
fi

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
    cleanup delete  # a box whose GPU never came up is not worth stopping-and-reusing
    exit 1
  fi
fi

status installing

# Pull BY DIGEST (IMAGE is a full ref@sha256:<digest> from the manifest, never a mutable
# tag) so every VM in a batch, and every retry, runs byte-identical bits.
if ! docker pull "$IMAGE"; then
  status failed '{"code":"IMAGE_PULL_FAILED","retriable":true}'
  cleanup "$LIFECYCLE"
  exit 1
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
# the old `/root/.cache/huggingface` mount was never read or written by that user.
mkdir -p /var/cache/deg-hf
chown 10001:10001 /var/cache/deg-hf
# `--gpus all` only when a GPU is expected: the CPU smoke VM boots the same image with no GPU (issue #56), and docker run fails on it with the flag.
GPU_FLAGS=()
if [ "$EXPECT_GPU" = "true" ]; then
  GPU_FLAGS=(--gpus all)
fi
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
  dna-entropy-worker run --manifest /work/manifest.json --store gcs
rc=$?

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
