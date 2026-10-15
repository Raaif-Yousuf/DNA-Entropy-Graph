# The local engine ("This PC")

Run the same worker on the user's own NVIDIA GPU instead of a rented VM. Issues #119 (spike),
#120 (detection), #172 (install), #173 (health and the picker), #174 (the runner). Hard Rule 6
holds: the worker is only ever a **child process** of the app, and nothing under `app/` imports torch.

## 1. What a This PC run is

The same contract as a cloud run, with the job folder in place of the bucket prefix
(`job_contract.md` section 1):

```
%LOCALAPPDATA%\DNAEntropyGraph\runs\<jobId>\
    input\<file>          the app's own copy (Hard Rule 14); the input store already put it here
    manifest.json         WorkerManifestBuilder.Build(..., localRoot: <job folder>): store = {kind: localdir, root}
    control\cancel        0 bytes, written on a user cancel
    status.json progress.jsonl output\... result.json     written by the worker, exactly as in the cloud
```

`LocalJobRunner` (`DnaEntropyGraph.LocalEngine`) writes the manifest, starts
`<engine>\.venv\Scripts\python.exe -m dna_entropy.worker.cli run --manifest <m> --store localdir --root <job folder>`,
reads `status.json` every 500 ms (on a `TimeProvider`, so every deadline below is tested on virtual time) into the run row (heartbeat, worker version), and when the process ends reads
`result.json` and copies every file it lists, verified against its sha256 and byte count, into a fresh folder in the
chosen output folder (default Downloads), the same as the cloud download. A run with no `result.json` is
`local_worker_crashed` (WORKER_CRASH). Phases: Draft, Preparing, Running, Finalizing, Downloading, then Completed or
PartiallyCompleted or Failed. There are no Uploading or Provisioning phases and no cloud resource of any kind.

**Guard rails (a This PC run always ends).** The app does not rely on the worker to stop itself. The runner kills the whole
process tree and records a terminal row with a named code when: no first heartbeat within 10 minutes
(`local_worker_no_heartbeat`), the heartbeat does not change for 10 minutes (`local_worker_stalled`, 20 beats of 30 s), or
the run passes its `MaxRunDurationMinutes` plus a 5 minute margin (`local_worker_timed_out`; the worker's own
`limits.maxRunSeconds` cap, enforced by #89, gets to fire first and say why). THEORY (unverified): ten minutes covers a cold
start of Python and torch on a PC with a virus scanner. A nonzero exit code after a finished `result.json` still completes the run
(the files are verified against it) with the note `local_worker_exit_nonzero` on the row and the exit code in the log; DECISION
(agent-made, reversible). Any other exception, and a start that is refused, ends the row `Failed` with `other`.
The worker starts with `CUDA_VISIBLE_DEVICES` set to the uuid of the card the readiness check judged, and with `PYTHONPATH`,
`PYTHONHOME` and `VIRTUAL_ENV` removed from its environment (a development `PYTHONPATH` set by `LocalEngineSetup` is kept).

**Killed app.** Every child is started inside one Windows Job Object with `KILL_ON_JOB_CLOSE` (`WindowsKillOnCloseJob`), so a
killed app takes its worker with it. At launch (`AppStartup.BeginAsync`) `LocalJobRunner.ReconcileInterruptedAsync` judges every local row
that is not terminal (#734): a `result.json` and an output folder finish the run from the result; otherwise it ends
`Failed` as `local_run_interrupted`; a Cancelling row ends Cancelled.

**Cancel.** `control/cancel` is written; the worker checks it between windows and writes a cancelled result. If the
process has not ended after 30 seconds its whole process tree is killed. Whatever the worker had finished is copied
out, then the row goes Cancelling then Cancelled.

DECISION (agent-made, reversible): `status.json` is polled, not also watched with a `FileSystemWatcher` (the issue lists
both). The file is replaced atomically on a timer, the read is a few hundred bytes, and a watcher would still need
the poll.

## 2. Where a run goes

`IRunTargetResolver` (`RunTargetResolver`): **Cloud** and **This PC** are never changed (a This PC that cannot run
fails visibly with `local_engine_not_ready`, `local_gpu_not_usable` or `model_needs_hopper`, each naming one action;
quietly switching to the cloud would spend the user's money). **Auto** picks This PC only when the engine is healthy
and, for a real model, the GPU is capable and torch sees CUDA, and the model is not one of the fp8-only models
(`evo2_1b_base`, `evo2_20b`, `evo2_40b`, which need Hopper). The mock predictor needs only a healthy engine, so a PC
with no NVIDIA card can run the whole path with the mock.

The choice is saved under the settings key `run.target` by `RunTargetPickerViewModel` (Settings > This PC) and read by
`NewRunViewModel` into `RunOptions.RunTarget`. The row's `Target` column is `local` or `cloud`.

## 3. Detection and health (#120, #173)

* GPU: `NvidiaSmiGpuDetector` runs `nvidia-smi --query-gpu=uuid,name,memory.total,driver_version --format=csv,noheader,nounits`
  by full path (System32, then `Program Files\NVIDIA Corporation\NVSMI`, then the absolute folders of PATH; never a bare name, so the current folder is never searched). Chosen over NVML P/Invoke because the worker reads the
  GPU the same way (`worker/gpu.py`) and there is no native binding to ship. No tool is `NoNvidia` (this laptop's Intel
  Arc); a tool that is there but fails (a driver that is not loaded, exit 9) is `ToolFailed` and has its own blocker, `GpuToolFailed`, whose action is to reinstall or update the NVIDIA driver. Neither is an error. With several cards the one with the most memory is judged, and the worker is pinned to it.
* Thresholds (`LocalCapabilityEvaluator`): GPU memory at least 22000 MiB, driver major at least 550, free disk at least
  30 GB where the engine lives. DECISION (agent-made, reversible): the floor is 22000 MiB and not 24576, because cards sold
  as 24 GB report 23034 MiB (L4, A10G) to 24564 MiB (RTX 4090). A 16 GB card is still refused.
* Engine health: `LocalEngineManager.CheckHealthAsync` runs `python -m dna_entropy.worker.cli health` in the engine's own
  Python. The worker prints one JSON line `{"worker": <version>, "evo": <bool>, "cuda": <bool>}`; the CUDA question is
  answered by `predictors/evo.py` (`probe_cuda`), the only module allowed to import torch (Hard Rule 1). Healthy means the
  worker's version equals the app's. States: NotInstalled, Broken, WorkerVersionMismatch, Healthy.

## 4. Install, repair, remove (#172)

`LocalEngineInstaller` drives the bundled `uv` (`<app>\engine\uv.exe`):
`uv venv --python 3.12 --managed-python <engine>\.venv`, then `uv pip sync --require-hashes requirements-win-cu12x.lock`, then
`uv pip install --no-deps <worker wheel>`, then the health check. `UV_PYTHON_INSTALL_DIR` and `UV_CACHE_DIR` point inside
the engine folder so **Remove is deleting that one folder** (refused unless it is strictly inside the app data folder).
Repair re-syncs (or does a full install when the venv is gone).

**Not built:** the files that must ship beside the app (`engine\uv.exe`, `engine\requirements-win-cu12x.lock` with hashes,
`engine\dna_entropy-<ver>-py3-none-any.whl`) and the WSL2 mode. Until they ship, Install fails with
`local_uv_missing` or `local_lock_missing` (a message that names the action), which is the honest state. The lock cannot be
generated without a measurement on a real NVIDIA Windows PC; see
[research/2026-10-15-native-windows-evo2.md](research/2026-10-15-native-windows-evo2.md).

## 5. Developing without a GPU

Environment variables read by `LocalEngineSetup`: `DEG_LOCAL_ENGINE_PYTHON` (use this python as the engine) and
`DEG_LOCAL_ENGINE_WORKER_SRC` (set as `PYTHONPATH`). With both set, This PC with the mock predictor runs end to end on any
PC. The real-worker test (`RealWorkerTests`) runs the real Python worker with the mock predictor when
`DEG_TEST_WORKER_PYTHON` (and optionally `DEG_TEST_WORKER_SRC`) are set, and is reported as skipped otherwise.

## 6. Known gaps

* The New run page does not show the picker yet (the choice is read from the one picker Settings and New run share); see the issue filed with
  this change.
* The Runs list does not show the target column (issue #225 covers the ledger and the column).
