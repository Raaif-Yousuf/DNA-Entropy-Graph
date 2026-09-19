# Architecture: the app/worker split, the job lifecycle, and where state lives

**Status: as-designed, not yet as-built.** MEASURED 2026-09-19: `app/` does not exist yet
(tracked by issue #61); `worker/src/dna_entropy/` exists and runs today, but nothing under
it yet reads a manifest or talks to Google Cloud (see `job_contract.md`'s own status
line). This doc describes the target shape from the approved design spec, marked
throughout where a piece is live versus planned, so a reader can tell "how it works" from
"how it will work" without cross-checking every claim against `git log`.

---

## 1. The one-sentence shape

A Windows desktop app that never runs machine-learning inference itself drives a Python
worker that always does, over a contract of small JSON files in a bucket - the worker
runs either on a per-job GPU VM in the user's own Google Cloud project, or on the user's
own NVIDIA GPU via the same contract pointed at a local directory instead of a bucket.

```
Windows PC (per Windows user)                     User's own GCP project
+------------------------------------+            +----------------------------------+
| DnaEntropyGraph.App (WinUI 3)      |  OAuth      | Bucket deg-<projnum>-<rand6>     |
|  Presentation (ViewModels)         | <--------> |   cache/  jobs/<jobId>/  vms/    |
|  Core (options, contract, errors)  |  Compute    |                                  |
|  Cloud (IGcp gateways + Google)    |  Storage    | VM deg-<jobId>                   |
|  Persistence (SQLite, settings,    |  APIs       |   startup.sh -> docker run worker |
|               DPAPI tokens)        |             |   reads manifest, writes status,  |
|  Viewer (igv.js in WebView2)       |             |   uploads outputs, self-stops     |
|  LocalEngine (optional NVIDIA GPU) |             +----------------------------------+
+------------------------------------+
```

## 2. The two halves, and why the boundary is there

**`app/` (C#, WinUI 3)** owns the UI, run history, every Google Cloud API call, and every
user-facing decision (which model, which GPU tier, what happens to the VM afterward). It
never imports `torch` and never runs inference in-process (CLAUDE.md rule 6) - even the
"Run on this PC" local-GPU path launches the worker as a **child process**
(`DnaEntropyGraph.LocalEngine`), not as an in-process call. This is a hard rule, not a
preference: keeping every heavy Python dependency (`torch`, `evo2`, `flash-attn`) entirely
out of the C# process is what lets the app stay a small, fast-launching WinUI executable
regardless of what the science stack needs, and what lets the worker's science logic be
tested identically whether it runs on a cloud VM, in the local engine, or in `pytest` on a
laptop with no GPU at all.

**`worker/` (Python, `dna_entropy`)** owns the science: input to validated sequence to
per-position probabilities to entropy to output files
(`science_and_formats.md`). It has no idea whether it is running inside a container on a
VM or as a local process launched by `LocalEngine` - the only thing that differs is which
`Blobstore` implementation it was constructed with (`job_contract.md` section 1). The
worker never calls a Google Cloud API to manage its own VM lifecycle beyond the narrow
metadata-token self-stop/self-delete calls documented in `cloud_design.md`; it does not
create, find, or list cloud resources - that is entirely the app's job.

The dependency direction the split enforces (CLAUDE.md rules 1, 6, 7):

```
App -> Presentation -> Core
App -> Cloud, Persistence, LocalEngine
Cloud -> Core
Persistence -> Core
LocalEngine -> Core
```

`Presentation` (ViewModels) depends on interfaces only (`IJobEngine`, `IRunRepository`,
`ISettingsStore`, `IGcpAccount`, `IDispatcher`, `IFilePicker`, `IToastService`,
`INavigator`, `IDialogService`), never on `Cloud` or `Persistence` directly - this is what
lets `Presentation.Tests` run every ViewModel test with no WinUI reference and no real
Google Cloud call.

## 3. Projects and their responsibility (planned; `app/` does not exist yet)

| Project | Owns |
|---|---|
| `DnaEntropyGraph.App` | The WinUI 3 executable: Views, Controls, WinUI-bound services (`NavigationService`, `ThemeService`, toasts, file-association activation), the vendored viewer assets, localized strings. |
| `DnaEntropyGraph.Presentation` | Every ViewModel (`ShellViewModel`, `WizardViewModel`, `NewRunViewModel`, `RunProgressViewModel`, `ResultsViewModel`, `HistoryViewModel`, `CloudResourcesViewModel`, `SettingsViewModel`). No WinUI reference (CLAUDE.md rule 8), so it is xUnit-testable with a synchronous `IDispatcher`. |
| `DnaEntropyGraph.Core` | `RunOptions`, the contract DTOs (`JobManifest`, `ProgressEvent`, `WorkerStatus`, `WorkerResult`), `JobPhase`/`JobStateMachine`, `ErrorCatalog`, `GpuPlanner`/`ModelGpuLinker`, `CostEstimator`, the C# port of the input sniffer/validator (`SequenceSniffer`, `SequenceValidator` - mirrors `worker/src/dna_entropy/validation/validators.py`, kept aligned via shared `tests/contract-fixtures/` vectors), `RunNamer`. |
| `DnaEntropyGraph.Cloud` | **The only project referencing `Google.*`** (CLAUDE.md rule 7). Every gateway interface (`IComputeGateway`, `IStorageGateway`, `IProjectSetupGateway`, `IQuotaGateway`, ...), the real Google-backed implementations, `GcpProvisioner` (the zone ladder), `VmLifecycleService`, `CloudJobRunner`, `CloudResourceInventory`, `StartupScriptBuilder`, and `FakeGcp` (the in-memory test double every other test project depends on instead of a real project). |
| `DnaEntropyGraph.Persistence` | `SqliteDatabase`, numbered migrations under `PRAGMA user_version`, `RunRepository`, `CloudResourceRepository`, `ProjectRepository`, `SettingsStore`, `DpapiTokenStore`. |
| `DnaEntropyGraph.LocalEngine` | `LocalEngineManager` (install/repair/health of the local `uv`-managed venv or WSL2 fallback per D15), `LocalJobRunner` (launches the worker as a child process), `RunTargetResolver` (Cloud / This PC / Auto). |
| `DnaEntropyGraph.CloudCli` (tool) | A console host for scripts: `resources list`, `bucket describe`, `vm create` - used by `scripts/cloud_gpu_test.ps1` and by nothing user-facing. |

`JobEngine` (in `App`, singleton) owns the active runners, survives navigation, and
publishes `RunProgressChanged`/`RunPhaseChanged` over `WeakReferenceMessenger` so any page
can subscribe without the runner needing a reference back to the UI.

## 4. The `JobPhase` state machine

```
Draft -> Validating -> Uploading -> Provisioning -> Preparing -> Running -> Finalizing -> Downloading -> Completed
                                     |               |            |           |             |-> PartiallyCompleted (some inputs failed)
                                     +---------------+------------+-----------+-> Failed(code)
any non-terminal --(user)--> Cancelling -> Cancelled
Local target: Validating -> PreparingEngine -> Running -> Downloading(copy) -> Completed
```

- **`Provisioning`** spans from the VM create/start request through the worker's first
 `booting` status write. **`Preparing`** spans the worker's `installing`
 (image pull), `restoring-cache`, and `model-loading` stages. **`Running`** spans the
 worker's own per-input `validating`/`running`/`writing`. **`Finalizing`** spans the
 worker's `uploading` stage through its terminal status write, then the app's
 verification of the after-task lifecycle action. See `job_contract.md` section 4 for
 the worker-side stage names these app-side phases wrap.
- A separate `VmLifecycle` state machine (one per `CloudResources` row) tracks the VM
 itself independently of any one job: `Creating -> Running -> Stopping -> Stopped ->
 Deleting -> Deleted`, plus `KeepAlive(untilUtc)` - this is what lets the Cloud page show
 a VM's real state even when no job on this PC currently owns it (e.g. a lab-mate's warm
 VM, or a VM from a crashed prior session).

## 5. One run, sequence of events

1. **Draft -> Validating**: the app validates every input locally (C# port of the
 worker's rules, `science_and_formats.md` section 4) before touching the network. A
 `Runs` row is inserted (`Phase = Validating`) **before any network call** - this is the
 write-ahead discipline in section 6.
2. **Uploading**: inputs are copied to `%LOCALAPPDATA%\...\cache\inputs\<jobId>\` (so a
 later re-run works even if the original file moved) and uploaded to
 `jobs/<jobId>/input/`; `manifest.json` is written last, once every input is confirmed
 uploaded.
3. **Provisioning**: `GcpProvisioner` either reuses an idle keep-alive VM, starts a
 stopped VM belonging to this installation, or runs the zone ladder to create a new one
 (`cloud_design.md` section 3). The VM's startup script begins executing on first boot.
4. **Preparing -> Running -> Finalizing**: the worker takes over entirely from here,
 driving its own stages exactly as documented in `job_contract.md`; the app is a pure
 observer, polling `status.json` and tailing `progress.jsonl`.
5. **Downloading**: once `result.json` exists, the app downloads every output object into
 the user's chosen output folder.
6. **Completed** (or **PartiallyCompleted**, or **Failed(code)**): the `Runs` row is
 updated with final timing and cost; the after-task lifecycle action (Stop/Delete/Keep)
 is verified against the VM's actual state, not merely assumed from the request having
 been sent.

Any non-terminal phase can move to **Cancelling -> Cancelled** at the user's request
(`job_contract.md` section 6).

## 6. Where state lives, and the crash-safe resumption rule

`%LOCALAPPDATA%\DNAEntropyGraph\` (per Windows user by construction - no cross-user
sharing is possible or attempted):

```
app.db  app.db-wal            SQLite (run history, cloud resource inventory, settings mirror)
settings.json                  RunOptions defaults + UI prefs + theme
auth\<sub>.tok                 DPAPI (CurrentUser) encrypted OAuth token; accounts.json lists known accounts
install.json                   installationId, createdAt
logs\app-YYYYMMDD.log          Serilog, 14-day retention
cache\inputs\<jobId>\          exact copy of every input as uploaded, so a re-run works even if the original moved
runs\<jobId>\                  local-run manifest copy, status history, worker.log
engine\                        local engine (uv-managed venv, hf-cache, engine.json, install.log)
```

The full SQLite DDL (`Accounts`, `Projects`, `Runs`, `RunInputs`, `RunOutputs`,
`RunEvents`, `CloudResources`, `CostLedger`, `MonthlySpend`, `LocalEngine`) is in
[Appendix A, section 3](superpowers/specs/2026-09-18-appendix-a-app-design.md#3-local-state-model);
this doc does not repeat the column list, since the appendix's `CREATE TABLE`
statements are the authoritative, copy-pasteable source and a second copy here would only
be another place for the two to drift apart.

**Crash-safe resumption**, because the app can be closed or killed (Task Manager, a
laptop lid, a crash) at any point in a run that takes minutes and continues in the cloud
regardless:

1. **Write-ahead**: the `Runs` row and its `JobPrefix` exist *before* any network call.
 The VM name is derived deterministically from the job id, so it can be found again even
 if the crash happened between inserting the row and anything else being recorded.
2. Every phase change and every consumed progress event is committed to SQLite before the
 UI is told about it - the UI is a read of committed state, never a cause of it.
3. On launch, `JobReconciler.ReattachAsync()` walks every run with a non-terminal phase:
 `result.json` exists -> go to `Downloading`; else `status.json` has a heartbeat under 5
 minutes old -> resume polling; else check `instances.get` directly and classify by
 instance state and heartbeat age (`HEARTBEAT_LOST`, `SPOT_PREEMPTED` / `VM_DIED`,
 `VM_MISSING`, or still `Provisioning`); a local run whose worker process is gone with no
 `result.json` -> `Failed(WORKER_CRASH)`.
4. The reconciler also **enforces** the after-task lifecycle policy retroactively - a VM
 that should have been deleted but was only stopped (because the app died before
 verifying) gets deleted now, not silently left as a stopped-disk cost leak.

## Related

[`job_contract.md`](job_contract.md) (the manifest/status/progress/result files this
lifecycle reads and writes), [`cloud_design.md`](cloud_design.md) (how the VM in step 3
above gets created, and the error taxonomy `Failed(code)` draws its codes from),
[`ui_conventions.md`](ui_conventions.md) (how `JobPhase` maps to what the user sees),
[Appendix A](superpowers/specs/2026-09-18-appendix-a-app-design.md) (full screen-by-screen
detail and the DDL this doc summarizes).
