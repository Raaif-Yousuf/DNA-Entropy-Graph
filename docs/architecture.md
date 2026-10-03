# Architecture: the app/worker split, the job lifecycle, and where state lives

**Status: as-designed, partly as-built.** MEASURED 2026-09-19: `app/`'s solution skeleton
exists (issue #61) with all thirteen projects, central package management, and a DI
resolution test that resolves all eight ViewModels through the same registration
`App.xaml.cs` itself calls. Everything behind those interfaces is still a placeholder:
`FakeGcp` and no `Google.*` package at all, an in-memory `RunRepository` and
`SettingsStore`, no Views beyond an empty shell, and a `LocalJobRunner` that throws rather
than pretending. `worker/src/dna_entropy/` exists and runs today, but nothing under
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

## 3. Projects and their responsibility (the `app/` skeleton exists per issue #61; implementations mostly still planned)

| Project | Owns |
|---|---|
| `DnaEntropyGraph.App` | The WinUI 3 executable: Views, Controls, WinUI-bound services (`NavigationService`, `ThemeService`, toasts, file-association activation), the vendored viewer assets, localized strings. |
| `DnaEntropyGraph.Presentation` | Every ViewModel (`ShellViewModel`, `WizardViewModel`, `NewRunViewModel`, `RunProgressViewModel`, `ResultsViewModel`, `HistoryViewModel`, `CloudResourcesViewModel`, `SettingsViewModel`). No WinUI reference (CLAUDE.md rule 8), so it is xUnit-testable with a synchronous `IDispatcher`. |
| `DnaEntropyGraph.Core` | `RunOptions`, the contract DTOs (`JobManifest`, `ProgressEvent`, `WorkerStatus`, `WorkerResult`), `JobPhase`/`JobStateMachine`, `ErrorCatalog`, `GpuPlanner`/`ModelGpuLinker`, `CostEstimator`, the C# port of the input sniffer/validator (`SequenceSniffer`, `SequenceValidator` - mirrors `worker/src/dna_entropy/validation/validators.py`, kept aligned via shared `tests/contract-fixtures/` vectors), `RunNamer`. |
| `DnaEntropyGraph.Cloud` | **The only project referencing `Google.*`** (CLAUDE.md rule 7). Every gateway interface (`IComputeGateway`, `IStorageGateway`, `IProjectSetupGateway`, `IQuotaGateway`, ...), the real Google-backed implementations, `GcpProvisioner` (the zone ladder), `VmLifecycleService`, `CloudJobRunner`, `CloudResourceInventory`, `StartupMetadata`, and `FakeGcp` (the in-memory test double every other test project depends on instead of a real project). |
| `DnaEntropyGraph.Persistence` | `SqliteDatabase`, numbered migrations under `PRAGMA user_version`, `RunRepository`, `CloudResourceRepository`, `ProjectRepository`, `SettingsStore`, `DpapiTokenStore`. |
| `DnaEntropyGraph.LocalEngine` | `LocalEngineManager` (install/repair/health of the local `uv`-managed venv or WSL2 fallback per D15), `LocalJobRunner` (launches the worker as a child process), `RunTargetResolver` (Cloud / This PC / Auto). |
| `DnaEntropyGraph.CloudCli` (tool) | A console host for scripts: `resources list`, `bucket describe`, `vm create` - used by `scripts/cloud_gpu_test.ps1` and by nothing user-facing. |

`JobEngine` (in `App`, singleton) owns the active runs, survives navigation, and
publishes `RunProgressChanged`/`RunPhaseChanged` over `WeakReferenceMessenger` so any page
can subscribe without the runner needing a reference back to the UI.

**Implemented (issue #428)**: `JobEngine` delegates a cloud run to `CloudJobRunner`, which
`ServiceRegistration` registers as a singleton whose phase callback sends
`RunPhaseChangedMessage` (after the runner has written the phase to `IRunRepository`).
`StartRunAsync` writes the run row up front (write-ahead: options, project, installation
id, VM name, machine type), then runs the runner on a background task, so the call returns
a job id immediately. `CloudJobRequestFactory` turns `RunOptions` into the request (the
six labels, `maxRunDuration`, `DELETE` termination action, GPU tier to machine type, zone
preference first); `InstallationId` is generated once and kept in `settings.json`.
`CancelRunAsync` cancels the run's token, waits for the runner to stop writing phases,
then `CloudJobRunner.CancelAsync` deletes the job's VMs (found by label) and records
Cancelled. `IRunVmActions.StopVmAsync`/`DeleteVmAsync` call the runner's label-based
Stop/Delete. A run with no selected project, or with a target other than Cloud/Auto
(local runs are issue #174), is recorded as Failed with error code `no_project` or
`target_not_supported` instead of being silently dropped. Not yet wired from
`RunOptions`: input staging and result download (the request carries no object keys, so
the runner transfers nothing), and the gateways behind the interfaces are still `FakeGcp`.

**`CloudJobRunner` is a thin sequencer (issue #512).** It keeps the public API (`RunAsync`, `CancelAsync`,
`StopVmAsync`, `DeleteVmAsync`, `UploadInputsAsync`, `GetPhaseAsync` and the timeout properties) and delegates to
internal collaborators in `app/src/DnaEntropyGraph.Core/Cloud/`, each with a narrow constructor and no access to another's
private state. They share one `CloudRunSettings` object (the runner's `init` properties write through to it) and one
`GatewayCalls` (the per-call deadline, `TryReadTextAsync`).

| Collaborator | Owns |
|---|---|
| `PreflightChecks` | Preflight steps 1-4 (project active, billing, Compute API, GPU quota). |
| `RunTransfer` | Input upload and manifest, output download with checksum and the `SafeRelativeOutputPath` rule, the run's output folder (Hard Rule 14). |
| `VmProvisioner` | The zone ladder, adoption of an existing VM, and the **only** in-flight create state (`_inflightCreates`, `SettleInflightCreatesAsync`). |
| `ResultWaiter` | Boot wait, `result.json` poll with the worker-heartbeat watch (`HeartbeatWatch`, issue #498), VM-lost detection and the worker's `status.json` error code. |
| `VmTerminator` | Stop/delete/verify of a VM (`EnsureVmEndedAsync`, by label, `DeleteOwnedVmsAndVerifyAsync`), the run page's Stop/Delete. |
| `VmCanceller` | The cancel path: asks `VmProvisioner` to settle, deletes via `VmTerminator`, records exactly one terminal state. |
| `RunOutcomeRecorder` | `FailAsync` and `FinishAsync`: how a run ends in a recorded terminal state. |
| `RunRowStore` | The only writer of phases and row annotations (committed before the UI callback). |

## 4. The `JobPhase` state machine

```
Draft -> Validating -> Uploading -> Provisioning -> Preparing -> Running -> Finalizing -> Downloading -> Completed
                                     |               |            |           |             |-> PartiallyCompleted (some inputs failed)
                                     +---------------+------------+-----------+-> Failed(code)
any non-terminal --(user)--> Cancelling -> Cancelled
Local target: Validating -> PreparingEngine -> Running -> Downloading(copy) -> Completed
```

The model loads **once per job** (worker issues #41 and #44): the worker's `model-loading`
stage runs one time and every input in the job reuses the loaded model, so a multi-input job
pays the load cost once, not per input. After the last input the worker applies the
lifecycle action itself and the VM-side script treats exit `10`/`11` as "already stopped /
deleted" (docs/cloud_design.md section 8).

- **`Provisioning`** spans from the VM create/start request through the worker's first
 `booting` status write. **`Preparing`** spans the worker's `installing`
 (image pull), `restoring-cache`, and `model-loading` stages. **`Running`** spans the
 worker's own per-input `validating`/`running`/`writing`. **`Finalizing`** spans the
 worker's `uploading` stage through its terminal status write, i.e. it ends once
 `result.json` exists and every output object is confirmed uploaded to the bucket ("Saving
 results" in the plain-language stage list below). The app's verification of the
 after-task lifecycle action (stopping or deleting the VM, "Cleaning up") happens **after**
 `Downloading`, not before it, matching the order the user actually sees results appear
 first and the rented computer's fate confirmed last: `docs/superpowers/specs/2026-09-18-
 dna-entropy-graph-design.md` section 4.4's own plain-language stage list is "Checking your
 files, Uploading, Starting a GPU computer, Preparing the computer, Analysing, Saving
 results, Downloading, Cleaning up", in that order, and `docs/user_guide/03-run.md` follows
 it exactly. See `job_contract.md` section 4 for the worker-side stage names these
 app-side phases wrap.
- A separate `VmLifecycle` state machine (one per `CloudResources` row) tracks the VM
 itself independently of any one job: `Creating -> Running -> Stopping -> Stopped ->
 Deleting -> Deleted`, plus `KeepAlive(untilUtc)` - this is what lets the Cloud page show
 a VM's real state even when no job on this PC currently owns it (e.g. a lab-mate's warm
 VM, or a VM from a crashed prior session).

## 5. One run, sequence of events

1. **Draft -> Validating**: the app validates every input locally (C# port of the
 worker's rules, `science_and_formats.md` section 4) before touching the network. A
 `Runs` row is inserted (`Phase = Validating`) **before any network call** - this is the
 write-ahead discipline in section 6. The local check is `InputFileValidator.Validate`
   (`Core/Inputs/`, #479): it detects the format, reads every record, applies the run's
   ambiguity policy, RNA flag and size caps (`InputLimits`, defaults equal to the worker's
   10 Mnt whole-input cap and the manifest's 20 Mnt batch budget), and returns the FIRST
   problem as an `InputProblemCode` plus a record index and base position, never an
   exception. It is a pre-flight filter: a file it accepts can still be refused by the
   worker (it does not parse GenBank feature tables); known gaps are listed in
   `InputFileValidatorWorkerParityTests`.
   `JobEngine` runs it on the STAGED copy right after staging and before the runner is called; a problem
   records the run Failed through `InputProblemErrorCodes` (for example `input_invalid_character`) with only the
   code, record and position in `ErrorDetail` (never the problem text, which can hold sequence), and no bucket
   object or VM is created.
2. **Uploading**: `JobEngine` has already copied the input to
 `%LOCALAPPDATA%\DNAEntropyGraph\runs\<jobId>\input\` (so a later re-run works even if
 the original file moved; issue #460) and the runner uploads that copy to
 `jobs/<jobId>/input/`; `manifest.json` is written last, once every input is confirmed
 uploaded.
3. **Provisioning**: `GcpProvisioner` either reuses an idle keep-alive VM, starts a
 stopped VM belonging to this installation, or runs the zone ladder to create a new one
 (`cloud_design.md` section 3). The VM's startup script begins executing on first boot.
4. **Preparing -> Running -> Finalizing**: the worker takes over entirely from here,
 driving its own stages exactly as documented in `job_contract.md`; the app is a pure
 observer, polling `status.json` and tailing `progress.jsonl`.
5. **Downloading**: once `result.json` exists (the runner polls for it while `Running`),
 the app downloads every file it lists into a fresh folder under the user's chosen output
 folder, verifying each against the checksum `result.json` gives (`cloud_design.md` section 11).
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

`settings.json` is never destroyed by a read problem (#558). It is read as JSON of any
value type (`GetString` returns a number or bool as its invariant text) and every key a
`SetString` does not touch is rewritten with its original JSON type. A file that does not
parse is copied to `settings.json.unreadable-<yyyyMMdd-HHmmss>` before the first write,
scalar `"key": value` pairs are salvaged from it (so `installation_id` survives and no new
id is minted), and `SettingsStore.RecoveredFromUnreadableFile` is set for the recovery UX
(DECISION #404). A file that cannot be read at all (locked) makes writes throw rather than
replace it. Writes are temp file plus `File.Move(overwrite)`.

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

## The embedded viewer (igv.js in WebView2), issues #72 and #73

Page key `ViewerViewModel.PageKey` ("Viewer"), parameter = the run's output folder (a string).
`ViewerViewModel` (Presentation, no WinUI) decides what to show: `IWebViewRuntimeProbe` /
`WebViewRuntimeDecision` (runtime missing -> a named-action error), `IgvLoadPlanner` (which
files exist in the run folder -> the JSON `load` command: FASTA reference with `indexed:false`
because the worker writes no `.fai`, `<name>.entropy.bedgraph` or `.wig` or `.fwd/.rev` pair as
wig tracks fixed to 0..2, `<name>.genes.gff3` as an annotation track), and `ViewerUrls`
(navigation allowed only to `https://viewer.deg/`). `IgvViewerHost` (App) only wires the
`WebView2` to it: `viewer.deg` -> `Assets/viewer`, `run.deg` -> the run folder (remapped when
`MapRunFolder` changes), DevTools on in Debug builds only, web messages on, no
new windows or downloads. `Assets/viewer/bridge.js` takes `load/goto/theme/snapshot` commands
and posts `ready/locusChanged/error/snapshot` events; the load command is held until `ready`,
and a page reload re-sends it. MEASURED 2026-10-02: `run.deg` must be mapped with `Allow`, not
`DenyCors` (igv.js's cross-origin fetch of the FASTA fails with status 0 under `DenyCors`).
igv.js still tries to fetch its genome lists from igv.org and raw.githubusercontent.com on
start; the page's Content-Security-Policy `connect-src` (viewer.deg and run.deg only) blocks
both, so the viewer makes no network request.

Entry: the run page's **Open viewer** button (`RunProgressViewModel.OpenViewerCommand`, enabled only for
Completed/PartiallyCompleted with a known `RunRecord.OutputDir`) calls `INavigator.NavigateTo("Viewer", folder)`.
Planner outcomes: the sequence is the `*.fasta` whose stem has an entropy file (several candidates ->
`AmbiguousSequenceFile`, none -> `NoEntropyTrack`), larger than 50 MB -> `SequenceTooLarge` (a `.fai` is #497).
Bridge events: `ready`, `loaded` (drawn), `warning` (page noise, ignored) and `error` (fatal; the ViewModel keeps the
run so **Try again** or a reloaded page redraws it). A renderer crash shows an error and the host reloads the page
once. The host only accepts web messages whose source is `https://viewer.deg/`, keeps WebView2's profile under
`%LOCALAPPDATA%\DNAEntropyGraph\webview2`, disables browser accelerator keys, and enables DevTools in Debug builds
only (#69 will own a real developer-mode switch). MEASURED 2026-10-02: igv.js 3.8.9 draws its toolbar, ruler,
sequence, bar and gene tracks under `script-src 'self'` (no inline script, no eval) and `style-src 'self'`; popups
(markup built from FASTA headers and GFF3 attributes) were not exercised, so `style-src` keeps `'unsafe-inline'`.

## Related

[`job_contract.md`](job_contract.md) (the manifest/status/progress/result files this
lifecycle reads and writes), [`cloud_design.md`](cloud_design.md) (how the VM in step 3
above gets created, and the error taxonomy `Failed(code)` draws its codes from),
[`ui_conventions.md`](ui_conventions.md) (how `JobPhase` maps to what the user sees),
[Appendix A](superpowers/specs/2026-09-18-appendix-a-app-design.md) (full screen-by-screen
detail and the DDL this doc summarizes).
