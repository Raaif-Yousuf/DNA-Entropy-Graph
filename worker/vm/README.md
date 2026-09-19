# `worker/vm/`

`startup.sh` is the Compute Engine `startup-script` metadata value the app sets on every
per-job VM (design §5.4; docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md
section 4.5, which this file transcribes and hardens). It is a **fixed template**, not
rendered per job in C# — every per-job value (bucket, job id, worker image digest,
lifecycle action, whether a GPU is expected, max run minutes) is read at boot time from
this instance's own metadata attributes via `meta()`, not baked into the script text.

## What it does, in order

1. Reads its own identity and the job's attributes from the instance metadata server.
2. **Idempotent-on-reboot short-circuit**: if a previous boot already reached
   `done`/`failed`/`cancelled` in `status.json`, it just re-applies the lifecycle action
   and exits — `startup-script` re-runs on every boot (including a restart after STOP),
   and this must never repeat a finished job.
3. Writes `booting` status, checks free disk.
4. If a GPU is expected, waits (up to 5 minutes) for `nvidia-smi` — never assumes the
   driver is up immediately after boot.
5. Writes `installing` status, `docker pull`s the worker image **by digest** (never a
   mutable tag).
6. Downloads `manifest.json` to `/work/manifest.json` and hands off to
   `dna-entropy-worker run` inside the container — see
   `worker/src/dna_entropy/worker/cli.py`. From here the container owns every stage from
   `restoring-cache` through the terminal state, the real heartbeat/progress cadence, and
   uploading outputs.
7. Interprets the container's exit code and calls `cleanup()`, which stops or deletes the
   VM **via the Compute API on itself**, using its own metadata-token credentials —
   **never** `shutdown -h` as the primary mechanism. `instanceTerminationAction=DELETE`
   only fires when Compute Engine itself stops the VM at its `maxRunDuration` deadline,
   not on a guest `shutdown` — this is the exact CLAIR #2908 lesson
   (docs/cloud_design.md section 8) this script exists to never repeat. A `shutdown -h`
   deadman is still armed once, at boot, purely as a last resort for when the API call
   itself is what fails.

## Verification (this session)

- `shellcheck worker/vm/startup.sh` — **0 findings** (verified via
  `uv run --with shellcheck-py shellcheck worker/vm/startup.sh`; this laptop has no
  system-installed `shellcheck`, so the ephemeral `uv run --with` invocation was used
  instead — the exact binary/version `ci-worker.yml`'s `contract` job also runs).
- `bash -n` — valid syntax (also exercised by `worker/tests/test_startup_script.py`,
  which additionally greps the (comment-stripped) script for the hard rules above: no
  `shutdown -h` outside the single backgrounded deadman, `cleanup()` only ever calls the
  Compute API, no `ssh`/`scp`/`gcloud` anywhere, `status booting` is written before any
  step that can fail, the already-finished short-circuit runs before any install/run
  work, and the exit-code dispatch matches `worker/src/dna_entropy/worker/cli.py`'s
  `EXIT_*` contract exactly).
- **Not verified**: this script has never run on a real DLVM or Container-Optimized OS
  instance (no cloud calls this session — see the overnight brief's hard rule). Whoever
  runs the first real GPU/CPU smoke VM should confirm `status.json` shows `booting` then
  `installing` before the worker's first line, and that `logs/startup.log` lands in the
  bucket, per issue #45's own "Observable that proves it is wired".
