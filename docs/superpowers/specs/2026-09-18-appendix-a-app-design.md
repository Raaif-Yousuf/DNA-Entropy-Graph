# Appendix A: Windows app design (detailed draft)

**Status:** Design draft produced 2026-09-18 by the app-architecture planning pass and reconciled into the [main spec](2026-09-18-dna-entropy-graph-design.md). Where this appendix and the spec body disagree (naming, worker delivery via container, bidirectional prediction replacing the `Strand` option, VM self-stop via the Compute API rather than guest `shutdown`), **the spec body wins**. This appendix is kept because it holds the screen-by-screen detail, the SQLite DDL, the option tables and the viewer bridge design that the implementation issues will draw from.

---

## 0. Decisions in one screen

| Area | Recommendation |
|---|---|
| Stack | WinUI 3 on the current stable Windows App SDK, **unpackaged**, .NET 10 LTS, self-contained win-x64 |
| MVVM | **CommunityToolkit.Mvvm 8.x** (source-generated `[ObservableProperty]`/`[RelayCommand]`), `WeakReferenceMessenger` for cross-page events |
| DI | `Microsoft.Extensions.DependencyInjection` via `Ioc.Default`; ViewModels transient, services singleton |
| Logging | **Serilog** to rolling file `%LOCALAPPDATA%\DNAEntropyGraph\logs\app-.log` (14 days, 10 MB), bridged to `ILogger<T>` |
| Persistence | **Microsoft.Data.Sqlite + Dapper**, WAL mode, numbered embedded SQL migrations tracked with `PRAGMA user_version`. Not EF Core (tooling friction in a WinUI solution, tiny schema). |
| Settings | `settings.json` (System.Text.Json source-gen, atomic write). OAuth refresh token encrypted with **DPAPI** (`ProtectedData`, CurrentUser). |
| Cloud | `Google.Cloud.Compute.V1`, `Google.Cloud.Storage.V1`, `Google.Cloud.ResourceManager.V3`, `Google.Cloud.Billing.V1`, `Google.Cloud.ServiceUsage.V1`, `Google.Cloud.Iam.Admin.V1`, `Google.Cloud.CloudQuotas.V1`, `Google.Apis.Auth` (loopback PKCE). No gcloud, no SSH. |
| App-worker contract | Bucket `deg-<projectNumber>-<rand6>`, prefix `jobs/<jobId>/`; worker writes `status.json` (snapshot) + `progress.jsonl` (history); app polls every 5 to 10 s |
| Viewer | **igv.js in WebView2** (primary, bundled offline, virtual-host mapping) + **ScottPlot 5 native** overview chart and sparklines. Not JBrowse 2. |
| Local GPU | Opt-in **uv-managed venv** under `%LOCALAPPDATA%\DNAEntropyGraph\engine`, same worker + same manifest/status contract, `storage.kind = "localdir"`; WSL2 + container fallback (spec D15) |
| Distribution | **Velopack** (Setup.exe + delta updates from GitHub Releases), signed via Azure Trusted Signing. Not MSIX (unsigned MSIX will not install; cert trust wall for labs). Fallback: Inno Setup + Octokit update check. |
| Job identity | Job id `yyyymmdd-hhmmss-<6 base32>`; VM `deg-<jobId>`; labels `app=dna-entropy-graph`, `job-id`, `installation-id`, `model`, `app-version`, `lifecycle` |
| Cost safety | VM stops or deletes itself via the Compute API (spec 5.4), `maxRunDuration` + `instanceTerminationAction=DELETE` backstop, deadman `shutdown` as last resort; app reconciles on launch; spend-cap warning; 7-day stopped-VM sweep |

---

## 1. Solution / repo layout

```
DNA-Entropy-Graph/
├── README.md  LICENSE(MIT)  CLAUDE.md  .editorconfig  global.json
├── Directory.Build.props  Directory.Packages.props        # central package versions
├── app/
│   ├── DnaEntropyGraph.sln
│   ├── src/DnaEntropyGraph.App/            # WinUI 3 (net10.0-windows10.0.22621.0), WindowsPackageType=None
│   │   ├── App.xaml(.cs)  MainWindow.xaml(.cs)  app.manifest (PerMonitorV2 DPI, longPathAware)
│   │   ├── Views/   Wizard/ (WelcomePage, SignInPage, ProjectPage, BillingPage, ApisPage, StoragePage, QuotaPage, TestRunPage, DonePage)
│   │   │            NewRunPage, RunProgressPage, ResultsPage, HistoryPage, CloudResourcesPage, SettingsPage
│   │   ├── Controls/ DropZone, InputListItem, OptionsPanel, StageTimeline, CostTicker, EntropyOverviewChart, IgvViewer, FileRow
│   │   ├── Services/ NavigationService, DispatcherAdapter, ToastService, ExternalViewerLauncher, ThemeService, FileAssociationActivation
│   │   ├── Converters/  Assets/viewer/ (igv.min.js, igv.css, index.html, bridge.js, dark.css)  Assets/pricing.json  Strings/en-US/Resources.resw
│   ├── src/DnaEntropyGraph.Presentation/   # ViewModels, NO WinUI reference -> xUnit-testable
│   ├── src/DnaEntropyGraph.Core/           # RunOptions, JobManifest, JobStatus, JobStateMachine, GpuPlanner, ModelGpuLinker,
│   │                                       #   CostEstimator, SequenceSniffer, SequenceValidator (C# port of validators.py), RunNamer, ErrorCatalog
│   ├── src/DnaEntropyGraph.Cloud/          # GoogleAuthService, gateways (IComputeGateway, IStorageGateway, IProjectSetupGateway, IQuotaGateway, ...),
│   │                                       #   GcpProvisioner (zone ladder), VmLifecycleService, CloudJobRunner, CloudResourceInventory, StartupScriptBuilder, FakeGcp
│   ├── src/DnaEntropyGraph.Persistence/    # SqliteDatabase, Migrations/0001_initial.sql..., RunRepository, VmRepository,
│   │                                       #   CostLedgerRepository, SettingsStore, DpapiTokenStore
│   ├── src/DnaEntropyGraph.LocalEngine/    # LocalEngineManager, LocalJobRunner, RunTargetResolver
│   ├── tools/DnaEntropyGraph.CloudCli/     # console host used by scripts (resources list, bucket describe, vm create for tests)
│   ├── tests/DnaEntropyGraph.Core.Tests/  .Presentation.Tests/  .Cloud.Tests/  .Persistence.Tests/  .Guards.Tests/  .App.UiTests/
│   └── packaging/  velopack.json  icon.ico
├── worker/                                 # the Python package, ported from DNA-Entropy-Genbank
│   ├── pyproject.toml  src/dna_entropy/ (readers, validation, predictors, analysis, annotators, writers, pipeline.py, config.py, cli.py)
│   ├── src/dna_entropy/worker/  (runner.py, status.py, blobstore.py, manifest.py, cancel.py, lifecycle.py, weights.py, __main__.py)   # NEW
│   ├── src/dna_entropy/analysis/windowing.py  direction.py                                                                     # NEW
│   ├── vm/startup.sh  Dockerfile.cuda  Dockerfile.cpu                                                                            # NEW
│   ├── engine/requirements-win-cu12x.lock                                                                                        # NEW pinned local stack
│   └── tests/
├── docs/contract/  manifest.schema.json  status.schema.json  error-codes.json  gpu-catalog.json
├── tests/contract-fixtures/               # shared JSON vectors read by pytest AND xUnit
├── scripts/  docs/  .claude/  .github/
```

Project dependency direction: `App -> Presentation -> Core`; `App -> Cloud, Persistence, LocalEngine`; `Cloud -> Core`; `Persistence -> Core`; `LocalEngine -> Core`. Presentation depends on interfaces only (`IJobEngine`, `IRunRepository`, `ISettingsStore`, `IGcpAccount`, `IDispatcher`, `IFilePicker`, `IToastService`, `INavigator`, `IDialogService`).

Key classes:

| Layer | Class | Responsibility |
|---|---|---|
| Core | `RunOptions` (record) | Every user option (section 2.3) with defaults; serialised into `Runs.OptionsJson` and `manifest.options` |
| Core | `JobManifest`, `ProgressEvent`, `WorkerStatus`, `WorkerResult` | Contract DTOs, `System.Text.Json` source-generated, validated against `docs/contract/*.schema.json` |
| Core | `JobPhase` enum + `JobStateMachine` | Legal transitions, terminal detection |
| Core | `ErrorCatalog` / `UserFacingError` | Code -> title, plain-language body, actions |
| Core | `SequenceSniffer`, `FastaLite`, `GenBankLite`, `SequenceValidator` | Local pre-flight (port of `detect.py`/`validators.py`; light GenBank parse: LOCUS, ORIGIN, count gene/CDS) |
| Core | `GpuPlanner` / `ModelGpuLinker` | Model -> (machineType, accelerator, context ceiling) mapping; "cheapest available" ladder; linked pickers |
| Core | `CostEstimator` | hourly rates x estimated minutes; live ticker; monthly aggregate |
| Core | `RunNamer` | `{file}`, `{date}`, `{time}`, `{model}`, `{n}` template + `_sanitize_name` port |
| Cloud | `GoogleAuthService` | OAuth loopback flow, DPAPI token store, `GoogleCredential` factory |
| Cloud | `ProjectSetupService` | list/create project, billing check/link, enable APIs, create bucket + worker SA, quota read/request |
| Cloud | `VmAllocator` / `GcpProvisioner` | reuse idle keep-alive VM -> start stopped VM -> create (zone ladder) |
| Cloud | `CloudJobRunner` | one per active run: upload, allocate, poll, download, after-job VM action |
| Cloud | `JobReconciler` | on launch / hourly: reattach non-terminal runs, clean expired cloud results, delete idle stopped VMs |
| Cloud | `CloudResourceInventory` | list all `app=dna-entropy-graph` labelled VMs/disks/buckets with cost |
| Cloud | `FakeGcp` (Compute+Storage+Auth in-memory) + `FakeWorker` | scripted behaviours for tests and `--fake-cloud` dev mode |
| Persistence | `SqliteDatabase`, `RunRepository`, `CloudResourceRepository`, `ProjectRepository`, `Migrations/0001_initial.sql...` | |
| LocalEngine | `LocalEngineManager`, `LocalJobRunner`, `RunTargetResolver`, `EngineHealth` | install/repair/health, launch worker locally, Auto target |
| Presentation | `ShellViewModel`, `WizardViewModel`, `NewRunViewModel`, `RunProgressViewModel`, `ResultsViewModel`, `HistoryViewModel`, `CloudResourcesViewModel`, `SettingsViewModel` | |
| App | `JobEngine` (singleton, owns runners, survives navigation, publishes `RunProgressChanged`/`RunPhaseChanged` via messenger) | |

NuGet (in `Directory.Packages.props`): `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`, `CommunityToolkit.Mvvm`, `CommunityToolkit.WinUI.Controls.SettingsControls` (SettingsCard/SettingsExpander), `CommunityToolkit.WinUI.Controls.Segmented`, `CommunityToolkit.WinUI.Converters`, `WinUIEx` (window placement persistence, backdrop helpers), `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging`, `Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.File`, `Serilog.Sinks.Debug`, `Microsoft.Data.Sqlite`, `Dapper`, `Polly`, `Google.Apis.Auth`, `Google.Cloud.Compute.V1`, `Google.Cloud.Storage.V1`, `Google.Cloud.ResourceManager.V3`, `Google.Cloud.ServiceUsage.V1`, `Google.Cloud.Billing.V1`, `Google.Cloud.Iam.Admin.V1`, `Google.Cloud.CloudQuotas.V1`, `System.Security.Cryptography.ProtectedData`, `Velopack`, `ScottPlot.WinUI`, `Microsoft.Web.WebView2` (transitive via WASDK). Tests: `xunit.v3`, `Shouldly`, `NSubstitute`, `Verify.Xunit` (snapshot manifests/DDL), `JsonSchema.Net`, `FlaUI.UIA3` (smoke only).

---

## 2. Screen-by-screen UX

Shell: `NavigationView` (left, compact on narrow widths) with **New run**, **Runs**, **Cloud**, and footer **Settings**. Title bar extended (Mica backdrop). Global status pill in the top-right: "Signed in as x@y - project-id - $0.00 this month". Active runs show a badge on **Runs**. Language: plain English, no GCP jargon except inside "Details".

### 2.1 First-run wizard (`Wizard/*`, `WizardViewModel` with `WizardStep` state)

| Step | What the user sees | Behind the scenes | Failure UX |
|---|---|---|---|
| 1 Welcome | What the app does; "Runs on a GPU you rent by the minute in your own Google Cloud. Typical run: 5 to 20 min, about $0.10 to $0.50." | | |
| 2 Sign in | "Sign in with Google" button -> browser | `GoogleAuthService.SignInAsync()`: loopback, scopes `cloud-platform openid email`; refresh token -> DPAPI store; credential feeds every client builder | Timeout 3 min, "Try again"; org-blocked app -> "your organization blocks this app; use a personal Google account" |
| 3 Project | List of projects (`ProjectsClient.SearchProjects`) with "Create new" (`dna-entropy-<6 random>`) | `IProjectSetupGateway` | Create denied (Workspace org policy) -> deep link + advice |
| 4 Billing | Green/red card: "Billing: enabled" or pick a billing account to link | `GetProjectBillingInfo`, `ListBillingAccounts`, `UpdateProjectBillingInfo`; fallback deep link | "Check again" button. Blocking step. |
| 5 Turn on services | One button "Turn on" with a progress list | `ServiceUsageClient.BatchEnableServices`: compute, storage, cloudquotas; poll LRO | Permission error -> plain text + who to ask |
| 6 Storage and identity | Progress lines | Create bucket (uniform access, PAP enforced, lifecycle rule, labels), worker SA + custom role + bucket binding; write `app-config.json` | 409 -> adopt existing (same account, other PC); org policy -> default SA fallback |
| 7 GPU allowance | Card per tier: "L4: allowed in 3 regions" / "not yet"; **Request** via Cloud Quotas API when eligible, else deep link pre-filtered to `NVIDIA_L4_GPUS` and `GPUS_ALL_REGIONS` with copyable justification | `RegionsClient.List` quotas -> `QuotaSnapshot`; `QuotaInfos.Get` eligibility | "Check again"; "Skip for now" (pipeline test still works) |
| 8 Test run | **Pipeline test** (recommended, ~3 min, ~$0.01: `e2-small`, `predictor=mock`, `-cpu` image) and **GPU test** (~6 to 15 min, ~$0.30, real evo2_7b on `tests/data/sample.gb`) | Uses the normal `CloudJobRunner`; proves bucket, SA, startup script, polling, download, viewer | Errors use the same taxonomy as real runs |
| 9 Local engine (optional) | Shown only when an NVIDIA GPU with 24 GB+ is detected | `LocalEngineManager.DetectAsync` | |
| 10 Done | Defaults: output folder, toasts on, theme | Save `settings.json`, mark `onboarding_complete` | |

Wizard is re-enterable from Settings -> Account -> "Run setup check again" (runs steps 4 to 7 as a health report, matching the prototype's `_diagnose_setup`), and each step is also exposed as an individual "Fix" from run errors.

### 2.2 New run (`NewRunPage`, `NewRunViewModel`)

Layout: left 60% inputs + basic options, right 40% summary card (estimated cost, time, GPU, files that will be produced) and the big **Run** button (Ctrl+Enter).

- **Drop zone** (whole left column accepts drop; also `Add files...` (Ctrl+O), `Paste sequence...`, `Add folder...`). Accepted: `.gb .gbk .gbff .genbank .fa .fasta .fna .ffn .txt .seq`. Multiple files -> a pill list; each pill shows detected kind, records, total nt, genes found, and inline validation state (green check / yellow notices / red error with the exact message from the validator port, e.g. "Found 'U' at position 12: this looks like RNA. Turn on *Treat as RNA*"). Files longer than the window show "will be analysed in N overlapping windows, both directions".
- **Paste dialog**: multi-line text box, live counter (nt after cleaning), notices (digits removed, header ignored), name field.
- **Run name**: single input -> from template (default `{file}`); multi-input -> a *batch name* (default `batch_{date}_{time}`) with each input keeping `{file}`. Collision with an existing local folder appends `_2`.
- **Basic options** (always visible, `SettingsCard` rows): Model, Find genes, Output files (checkbox set incl. the new Entropy TSV), Save to, Where to run, Estimated cost / time line.
- **Advanced** (`SettingsExpander`s: Sequence, Model and GPU, Cloud VM, Output and notifications; collapsed; state remembered). All remembered as "my defaults" via "Save as defaults".
- Footer: "Runs on **project-id** in **us-central1 (auto)** - GPU: **L4** - [Change...]" and the current month spend "$3.20 of $50 warning cap".

### 2.3 Every option, its default, and where it lives

See spec section 4.3 for the authoritative table. Additional detail:

**Linked model/GPU pickers** (`ModelGpuLinker` over `docs/contract/gpu-catalog.json`):

| Model | Precision | Min GPU | Offered |
|---|---|---|---|
| `evo2_7b` | bf16 | L4 24 GB | yes, **default** |
| `evo2_7b_262k` | bf16 | L4 24 GB | yes |
| `evo2_40b` | FP8 | 2x H100 80 GB (`a3-highgpu-2g`, Spot/Flex only) | yes, labelled "experimental, expensive, quota rarely granted" |
| `evo2_1b_base` | FP8 | H100 80 GB | under "Show all models", labelled "H100 only, not a cheap option" |

| GPU tier | Machine | Accelerator | Quota metric | Est. $/h on-demand (spot) |
|---|---|---|---|---|
| L4 24 GB | `g2-standard-8` | `nvidia-l4` | `NVIDIA_L4_GPUS` | ~0.85 (~0.30), **default** |
| A100 40 GB | `a2-highgpu-1g` | `nvidia-tesla-a100` | `NVIDIA_A100_GPUS` | ~3.67 (~1.20) |
| A100 80 GB | `a2-ultragpu-1g` | `nvidia-a100-80gb` | `NVIDIA_A100_80GB_GPUS` | ~5.07 (~1.80) |
| 2x H100 80 GB | `a3-highgpu-2g` | `nvidia-h100-80gb` x2 | `NVIDIA_H100_GPUS` | Spot/Flex only, ~4 to 5 per GPU-hour |
| Cheapest available | tries L4 -> A100 40 -> A100 80 subject to model minimum; never escalates cost tier without a click | | | |

Linking rules: pick a bigger model -> GPU auto-raises to its minimum (toast "Switched GPU to H100 for evo2_40b"); pick a bigger GPU -> model auto-suggests largest that fits (only if user has not pinned the model); either can be pinned via a small lock icon; an infeasible pair shows an inline warning "evo2_40b will not fit on an L4; the run will fail" and Start requires confirming. The context-length ceiling (spec 5.6) is also read from the catalog per GPU tier.

**Advanced - Output and notifications**: naming template (`{file}`; tokens `{file} {date:yyyy-MM-dd} {time:HHmm} {model} {n}`; collisions get `_2`), open viewer when done (on), toast when done (on), open folder when done (off), sound (off), keep worker log in output folder (off; always kept in app data).

### 2.4 Run progress (`RunProgressPage`)

- Vertical `StageTimeline`: Checking your files -> Uploading -> Starting a GPU computer (zone ladder narrated: "Trying us-central1-a... no L4 capacity, trying us-central1-b") -> Preparing the computer (image pull / restoring cached weights / loading model; first run in a project shows "First run in this project takes longer (~8 min) while the model is cached") -> Analysing (per input, per contig, per window and direction: "SetTnpB - record 2 of 5 - window 3 of 7, forward") -> Saving results -> Downloading -> Cleaning up ("Stopping the GPU computer" / "Deleting" / "Keeping running for 30 min"). Each with elapsed time.
- `CostTicker`: elapsed x hourly rate + fixed costs; labelled "estimate".
- Buttons: **Cancel run** (soft: writes `control/cancel`; hard after 60 s: stop VM); overflow: **Stop VM now**, **Delete VM now** (confirm dialogs stating consequences).
- "Show details" expander: worker log tail (from `logs/worker.log` every 10 s) and serial-port output (`GetSerialPortOutput`) before the worker is up; "Copy diagnostics".
- Banner: "You can close the app. The run continues in the cloud and results download when you come back."

### 2.5 Results (`ResultsPage`)

Header (name, date, model, GPU, duration, est. cost, contigs, mean/min/max, direction and K, seam position) -> **native overview chart** (ScottPlot: entropy line per contig, tabs for multi-record, mean line, seam marker, click-drag selects a region and asks igv to jump) -> **igv.js viewer** (FASTA reference + bedGraph bar track + genes GFF3; collapsed by default to an "Open in viewer" button so the page is instant) -> file list (`FileRow`: kind, name, size, present-locally, Open, Show in folder) -> actions: Open folder, Open in IGV, Open in Geneious, Re-run with these settings, Re-download from cloud, Delete local files, Export PNG.

### 2.6 Runs / History (`HistoryPage`)

Cards with sparkline (ScottPlot), name, inputs, status chip, cost, date; filters (status, date, text); context menu: Open, Re-run, Re-download, Delete local, Delete cloud results, Remove. Toolbar: "Find runs from other computers" (scans `jobs/*/manifest.json` in the bucket and imports read-only entries, solving the two-PCs/same-account history gap).

### 2.7 Cloud (`CloudResourcesPage`)

Tabs **VMs** / **Storage** (and **Images** if the post-v1 bake ships). VM row: name, zone, GPU, state, created by (this PC / another PC / run link), running since, est. cost this month; actions Stop, Start, Delete; "Delete all stopped"; deep link to Console. Storage: bucket, size, retention rule, cached weights size, "Empty results older than...", "Clear cache". Footer: month-to-date estimate vs cap. Banner if any VM RUNNING with no active job on this PC: "A GPU computer is running without a job (maybe from another PC). Stop it?". **Delete everything this app created** (double confirm, typed project id) also removes the bucket and SA/role. Inventory = `InstancesClient.AggregatedList(filter: "labels.app=dna-entropy-graph")` + `DisksClient.AggregatedList` + bucket listing; refreshed on open and every 60 s.

### 2.8 Settings (`SettingsPage`, SettingsCards)

Account (signed in as, Sign out, Switch project, Cloud setup check) - Appearance: Theme **Light / Dark / Use system** (default system) - Defaults (opens the same OptionsPanel) - **Local engine**: status card (GPU, VRAM, driver, torch, evo2), **Install local engine (~10 GB)**, Download model weights now, Check health, Repair, Uninstall - External viewers: IGV path (auto-detect `%LOCALAPPDATA%\Programs\IGV*\igv.exe`; also push via IGV batch port 60151 when running), Geneious path - Notifications - Budget (monthly warning cap $50, hard stop off, delete stopped VMs idle > 7 days, cloud retention 90 d) - Updates (check on launch on; "Check now") - Diagnostics (Open logs, Export support bundle zip, developer toggles: mock predictor, verbose, `--fake-cloud`) - Data (DB location, Clear history, Reset app) - Advanced (worker image digest override, DLVM image family override).

### 2.9 Dialogs and empty states

- No sign-in -> New run shows a blocking card "Sign in to run" (files can still be added and validated).
- Budget cap exceeded -> yellow banner; hard stop -> Run button disabled with explanation.
- Every error dialog: title, plain body, exact technical text in an expander ("Details for support"), 1 to 3 action buttons, and "Copy details".

---

## 3. Local state model

Root `%LOCALAPPDATA%\DNAEntropyGraph\` (per Windows user by construction):

```
app.db  app.db-wal            SQLite
settings.json                  RunOptions defaults + UI prefs + theme
auth\<sub>.tok                 DPAPI (CurrentUser) encrypted OAuth token (custom IDataStore); accounts.json lists known accounts
install.json                   installationId, createdAt
logs\app-YYYYMMDD.log          Serilog, 14-day retention
cache\inputs\<jobId>\          copy of every input exactly as uploaded (so re-run works if the original moves)
runs\<jobId>\                  manifest copy, status history, worker.log (local runs write status here)
engine\                        local engine (uv-managed venv, hf-cache, engine.json, install.log)
```

DDL (migration `0001_initial.sql`, `PRAGMA user_version = 1`, `journal_mode=WAL`, `foreign_keys=ON`, `busy_timeout=5000`):

```sql
CREATE TABLE Accounts (
  Sub TEXT PRIMARY KEY, Email TEXT, DisplayName TEXT, LastSignedInUtc TEXT);

CREATE TABLE Projects (
  ProjectId TEXT PRIMARY KEY, ProjectNumber TEXT, DisplayName TEXT, AccountSub TEXT REFERENCES Accounts(Sub),
  HomeRegionGroup TEXT, Bucket TEXT, WorkerSaEmail TEXT, BillingEnabled INTEGER,
  ApisEnabledJson TEXT, QuotaJson TEXT, QuotaCheckedAt TEXT, LastGoodZone TEXT,
  SetupCompletedAt TEXT, SmokeTestPassedAt TEXT, IsActive INTEGER);

CREATE TABLE Runs (
  Id TEXT PRIMARY KEY,                     -- jobId yyyymmdd-hhmmss-xxxxxx
  Name TEXT NOT NULL, IsBatch INTEGER NOT NULL DEFAULT 0,
  Target TEXT NOT NULL,                    -- cloud | local
  Phase TEXT NOT NULL,                     -- see JobPhase
  ErrorCode TEXT, ErrorDetail TEXT,
  CreatedAt TEXT NOT NULL, StartedAt TEXT, VmReadyAt TEXT, FinishedAt TEXT,
  OptionsJson TEXT NOT NULL, ManifestJson TEXT,
  ProjectId TEXT REFERENCES Projects(ProjectId), Bucket TEXT, JobPrefix TEXT,   -- jobs/<Id>/
  VmName TEXT, Zone TEXT, MachineType TEXT, GpuType TEXT, IsSpot INTEGER, VmReused INTEGER,
  LastProgressSeq INTEGER NOT NULL DEFAULT 0, LastHeartbeatAt TEXT, StatusGeneration INTEGER,
  OutputDir TEXT, EstimatedCostUsd REAL, ActualCostUsd REAL, VmSeconds INTEGER,
  CloudResultsExpireAt TEXT, CloudResultsDeleted INTEGER NOT NULL DEFAULT 0,
  AppVersion TEXT, WorkerVersion TEXT, WorkerImageDigest TEXT, ContractVersion INTEGER,
  InstallationId TEXT, Imported INTEGER NOT NULL DEFAULT 0);

CREATE TABLE RunInputs (
  Id INTEGER PRIMARY KEY, RunId TEXT REFERENCES Runs(Id) ON DELETE CASCADE, Idx INTEGER,
  OriginalPath TEXT, OriginalName TEXT, LocalCopyPath TEXT, Kind TEXT, Sha256 TEXT, SizeBytes INTEGER,
  RecordCount INTEGER, TotalNt INTEGER, GeneCount INTEGER, RunName TEXT, CloudObject TEXT,
  Status TEXT, ErrorCode TEXT, ErrorDetail TEXT, StatsJson TEXT, NoticesJson TEXT);

CREATE TABLE RunOutputs (
  Id INTEGER PRIMARY KEY, RunId TEXT REFERENCES Runs(Id) ON DELETE CASCADE, InputIdx INTEGER,
  Kind TEXT, FileName TEXT, CloudObject TEXT, LocalPath TEXT, SizeBytes INTEGER, Sha256 TEXT,
  Downloaded INTEGER NOT NULL DEFAULT 0);

CREATE TABLE RunEvents (                    -- app-side + worker progress, for the log view and support bundle
  Id INTEGER PRIMARY KEY, RunId TEXT REFERENCES Runs(Id) ON DELETE CASCADE, Seq INTEGER,
  Ts TEXT, Source TEXT, Stage TEXT, Level TEXT, Message TEXT, DataJson TEXT);
CREATE INDEX IX_RunEvents_Run ON RunEvents(RunId, Seq);

CREATE TABLE CloudResources (
  Id INTEGER PRIMARY KEY, Kind TEXT,        -- vm | disk | bucket | serviceaccount | image
  Name TEXT, ProjectId TEXT, Location TEXT, CreatedAt TEXT, CreatedByInstall TEXT,
  LastKnownState TEXT, LastCheckedAt TEXT, MachineType TEXT, GpuType TEXT, IsSpot INTEGER, DiskGb INTEGER,
  HourlyRateUsd REAL, RunningSecondsAccum INTEGER DEFAULT 0, LastStartedAt TEXT, CurrentJobId TEXT,
  LifecyclePolicy TEXT, KeepUntilUtc TEXT, DeletedAt TEXT, LabelsJson TEXT, UNIQUE(Kind, ProjectId, Name));

CREATE TABLE CostLedger (
  Id INTEGER PRIMARY KEY, RunId TEXT NULL, ResourceId INTEGER NULL, Kind TEXT,  -- vm_runtime | disk | storage
  StartedUtc TEXT, EndedUtc TEXT, UsdEst REAL);

CREATE TABLE MonthlySpend (Month TEXT PRIMARY KEY, EstimatedUsd REAL);   -- denormalised for the cap banner

CREATE TABLE LocalEngine (
  Id INTEGER PRIMARY KEY CHECK (Id = 1), InstallPath TEXT, Mode TEXT,   -- native | wsl2
  PythonVersion TEXT, TorchVersion TEXT, Evo2Version TEXT, CudaVersion TEXT, GpuName TEXT, VramGb REAL,
  InstalledUtc TEXT, LastHealthUtc TEXT, HealthJson TEXT);
```

Crash-safe resumption rules:

1. **Write-ahead**: the `Runs` row (Phase=`Validating`) and the `JobPrefix` are inserted *before* any network call; the VM name is derived from the job id so it can be found even if the crash happened between `Insert` and recording it.
2. Every phase change and every consumed progress event is committed before the UI is told.
3. On launch `JobReconciler.ReattachAsync()` iterates runs with non-terminal phase (also runs if the app was killed via Task Manager):
   - GET `jobs/<id>/result.json` exists -> go to `Downloading`.
   - else GET `status.json`: heartbeat < 5 min old -> resume polling.
   - else `InstancesClient.Get(VmName)`: `RUNNING` but heartbeat stale > 10 min -> `Failed(HEARTBEAT_LOST)` with **Retry** / **Stop VM**; `TERMINATED`/`STOPPING` with no result -> `Failed(SPOT_PREEMPTED)` if `IsSpot` else `Failed(VM_DIED)`; 404 -> `Failed(VM_MISSING)`; `PROVISIONING`/`STAGING` -> `Provisioning`.
   - Phase `Uploading` with no manifest in bucket -> restart from `Uploading` (inputs are in `cache\inputs`).
   - For local runs: if the worker process (pid stored in `runs\<id>\worker.pid`) is gone and no `result.json` -> `Failed(WORKER_CRASH)`.
4. `AfterJob` action is also enforced by the reconciler (a VM that should have been deleted but was only stopped gets deleted now).
5. Apply lifecycle policies whose `KeepUntilUtc` passed; run the stopped-VM sweep.

---

## 4. Job state machine and the contract

### 4.1 Phases (`JobPhase`)

```
Draft -> Validating -> Uploading -> Provisioning -> Preparing -> Running -> Finalizing -> Downloading -> Completed
                                     |               |            |           |             |-> PartiallyCompleted (some inputs failed)
                                     +---------------+------------+-----------+-> Failed(code)
any non-terminal --(user)--> Cancelling -> Cancelled
Local target: Validating -> PreparingEngine -> Running -> Downloading(copy) -> Completed
```

- `Provisioning`: VM create/start request through first `booting` status. `Preparing`: `installing` (image pull), `restoring-cache`, `model-loading`. `Running`: per input `validating`, `running`, `writing`. `Finalizing`: worker `uploading` -> terminal status, then lifecycle action.
- Separate `VmLifecycle` (per `CloudResources` row): `Creating -> Running -> Stopping -> Stopped -> Deleting -> Deleted`, plus `KeepAlive(untilUtc)`.

### 4.2 Contract (authoritative schemas in `docs/contract/`)

See Appendix B section 3 for the manifest, status and progress schemas and the bucket layout; the spec body's names win. The app polls with `IfGenerationNotMatch` so unchanged objects cost one 304. GCS objects are immutable, so "append" is implemented by re-uploading the whole small `progress.jsonl` (cap 1 MB; older lines roll into `progress.1.jsonl`).

### 4.3 Timeouts (defaults; `JobTimeoutMinutes=Auto`)

| Wait | Limit | On expiry |
|---|---|---|
| Create/start operation | 10 min | classify error, next zone |
| `booting` after VM RUNNING | 8 min | `VM_BOOT_TIMEOUT` (offer Retry in another zone) |
| Image pull (`installing`) | 15 min | `IMAGE_PULL_FAILED` |
| Model load | 15 min | `MODEL_DOWNLOAD_FAILED` / `WORKER_CRASH` |
| Heartbeat gap | warn 3 min, fail 10 min | `HEARTBEAT_LOST` -> reconciler logic |
| `Running` | max(30 min, 3 min x total windows x directions) | `RUN_TIME_LIMIT` |
| Download | 10 min per 100 MB | `DOWNLOAD_FAILED` (retryable, results stay in bucket) |
| VM hard cap (`maxRunDuration`) | user setting, default 4 h | Compute Engine deletes the VM |

Cancellation: app writes `control/cancel`; after 30 s without `stage:cancelled`, the app stops the VM directly; `AfterJob` still honoured; local phase `Cancelled`, cost recorded.

### 4.4 Error taxonomy

The authoritative list is spec 5.9 and Appendix B section 7. The app's `ErrorCatalog` maps every code to a `.resw` title, plain body and 1 to 3 actions. Every error card has "Copy details for support" (raw API error + job id + timestamps) and the prototype's "paste this into an LLM" hint in Details.

---

## 5. Viewer integration

**Primary: igv.js (MIT) embedded in WebView2, vendored offline.** Reasons over JBrowse 2: ~1 MB single file vs a React bundle, no build pipeline, first-class bedGraph/WIG/GFF3/FASTA, `indexed:false` FASTA for small sequences, and igv.js already matches the files the tool emits and the users' desktop IGV mental model. **Native chart** (`ScottPlot.WinUI` `Signal` plot) is worthwhile as a *fast overview*: History sparklines, Results header strip, PNG export, seam marker, drag-select to jump the viewer. WebView2 costs ~0.5 to 1 s to initialise and cannot be instanced per list row.

Data feed (no server):
- `CoreWebView2.SetVirtualHostNameToFolderMapping("viewer.deg", "<App>\Assets\viewer", Deny)` for the bundle.
- On opening a run: `SetVirtualHostNameToFolderMapping("run.deg", <OutputDir>, DenyCors)`; `ClearVirtualHostNameToFolderMapping("run.deg")` when switching runs. igv config: `reference:{ fastaURL:"https://run.deg/<name>.fasta", indexed:false }`, tracks `{type:"wig", format:"bedgraph", url:".../<name>.entropy.bedgraph", name:"Entropy (bits)", min:0, max:2}`, `{type:"annotation", format:"gff3", url:".../<name>.genes.gff3"}`. In "Both, separate tracks" mode the fwd/rev tracks are added as additional wig tracks. For inputs above ~50 MB fall back to `features` arrays posted via `PostWebMessageAsJson` in chunks; for long sequences generate a `.fai` locally.
- Bridge: `bridge.js` handles `window.chrome.webview.addEventListener('message')` commands `{cmd:"load", config}`, `{cmd:"theme", dark:true}`, `{cmd:"goto", locus}`, `{cmd:"snapshot"}`; posts back `{evt:"ready"}`, `{evt:"locusChanged"}`, `{evt:"error"}`. C# side: `IgvViewerHost` wraps the `WebView2` control.
- Dark mode: `CoreWebView2.Profile.PreferredColorScheme` follows the app theme; `dark.css` overrides igv.js's light chrome (background, track labels, ruler, popover) toggled by a `body.dark` class; entropy track colour swapped for contrast. `ThemeService` broadcasts `ThemeChanged` -> host re-posts `theme`.
- Multi-record: contig dropdown outside the WebView (WinUI ComboBox) -> `goto`.
- Security: `Settings.AreDevToolsEnabled=false` (except `--fake-cloud`), `IsWebMessageEnabled=true`, `AreDefaultContextMenusEnabled=false`, `AreDefaultScriptDialogsEnabled=false`, navigation locked to `https://viewer.deg/`.
- WebView2 Runtime: Evergreen is present on Windows 11; if `CoreWebView2Environment.GetAvailableBrowserVersionString()` throws, hide the viewer, keep the native chart, offer the Evergreen bootstrapper link (Velopack pre-install hook can also run it).
- "Open in IGV desktop": if IGV is listening on 60151 send `new`, `genome <fasta>`, `load <bedgraph>`, `load <gff3>`; else launch `igv.bat <files>` (path autodetected, else Browse). "Open in Geneious": `Process.Start(geneious.exe, files)`.

---

## 6. Accessibility, DPI, keyboard, localization, toasts, file associations

- **DPI**: WinUI 3 is per-monitor v2 by default; only vector icons (`FontIcon`/`PathIcon`), no bitmaps except the app logo (`.svg` via `SvgImageSource`). WebView2 and ScottPlot inherit `RasterizationScale`.
- **Accessibility**: `AutomationProperties.Name` on every icon-only button and the drop zone; `LiveSetting=Polite` on the stage list so Narrator announces phase changes; all colours from theme resources so High Contrast works; min 40x40 targets; drag-drop always has an "Add files" button twin; focus visuals default; no fixed heights (text scaling).
- **Keyboard**: Ctrl+N new run, Ctrl+O add files, Ctrl+Shift+V paste sequence, Ctrl+Enter run, Esc cancel dialog, F5 refresh (Cloud/History), Ctrl+, Settings, Ctrl+L focus locus box in viewer, Alt+Left back. `AccessKey`s on nav items.
- **Localization**: all strings in `Strings/en-US/Resources.resw` via `x:Uid` and `ResourceLoader` (works unpackaged with the generated `.pri`); numbers/dates/currency via `CultureInfo.CurrentCulture`; en-US only shipped in v1; layout tolerant of 30% longer strings.
- **Toasts**: Windows App SDK `AppNotificationManager` (unpackaged registration at startup); `AppNotificationBuilder` with argument `action=openRun&id=<jobId>`; buttons "Open results", "Open folder"; failure toasts carry the error title; when the app is closed, activation launches it and `NavigationService` deep-links. Idle-VM warning toast every 15 min while a VM runs without a job; keep-alive expiry reminder.
- **File associations** (optional, on by default in installer, toggle in Settings): per-user `HKCU\Software\Classes` ProgIDs `DNAEntropyGraph.GenBank` (`.gb .gbk .gbff .genbank`) and `DNAEntropyGraph.Fasta` (`.fa .fasta .fna`) registered as *Open with* entries (not default handler, so Geneious/SnapGene defaults are untouched). `App.OnLaunched` args -> NewRun with files pre-added; single-instance via `AppInstance.FindOrRegisterForKey` redirection.

---

## 7. Testing, CI, distribution

**Unit (xUnit v3)**: `Core.Tests`: validator port against `tests/contract-fixtures/validation.json` (the same vectors pytest uses), `RunNamer`, `GpuPlanner`/`ModelGpuLinker` matrix, `CostEstimator`, `JobStateMachine` transitions (table-driven), `ErrorCatalog` classification (port of `test_cloud.py` cases), JSON round-trips vs schemas (`JsonSchema.Net`), Verify snapshots of manifests and the rendered startup script. `Presentation.Tests`: ViewModels with NSubstitute'd services and a synchronous `IDispatcher`, `FakeTimeProvider`.

**Integration (`Cloud.Tests`)**: `FakeGcp` in-memory Compute (scriptable per-zone outcomes: ok / stockout / quota / billing / api-off / 403 / preempt-after-N-seconds) + Storage (dictionary with generations, conditional GETs) and `FakeWorker` that replays a progress stream fixture into the fake bucket on a `FakeTimeProvider`. Scenarios: happy path, cold vs cached, stockout ladder then success, preemption -> on-demand retry, cancel mid-run, app "crash" (dispose runner, new `JobReconciler` reattaches), keep-alive follow-up job, `AfterJob=delete` when app offline, two installations on one account never adopt each other's VMs without the lease. `--fake-cloud` CLI flag boots the real app on these fakes for UI work with zero spend.

**Guards (`Guards.Tests`, ~5 s)**: csproj scan (no `Google.*` outside `Cloud`, no torch/onnx anywhere), code-behind scan (no branching in `*.xaml.cs`), `.resw` em-dash and inline-string scan, DI resolution of every registered ViewModel and service, `VmSpec` label/`maxRunDuration` precondition planted-violation + vacuity tests.

**Worker (pytest)**: existing 127 tests + `test_windowing.py`, `test_direction.py`, `test_worker.py` with a `LocalBlobstore` backend and mock predictor, contract schema validation with `jsonschema`.

**UI smoke (FlaUI, nightly + release)**: launch with `--fake-cloud --fresh-profile`, drive first-run wizard, drop `sample.gb`, run, wait for Completed, assert files exist and viewer reports `ready`. Manual `docs/ToTest.md` checklist for real-GCP paths (billing off, quota zero, real preemption).

**CI (`ci-app.yml`, windows-latest)**: `actions/setup-dotnet` from `global.json`, `dotnet restore`, `dotnet format --verify-no-changes`, `dotnet build -c Release -p:Platform=x64 -warnaserror`, `dotnet test` (all non-UI test projects, trx + coverage), `dotnet publish -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None`, `vpk pack` -> unsigned artifact (7-day retention). `release.yml` on tag `v*`: assert secrets exist, same build, `azure/trusted-signing-action`, `vpk pack` with signing, `vpk upload github` as draft, attach worker wheel and container digests, publish after every asset is attached (the in-app updater reads Releases and a half-uploaded release would be visible).

**Distribution decision: Velopack, not MSIX, not Inno**: MSIX sideloading needs a trusted cert chain *and* is frequently blocked by policy on managed lab PCs; App Installer + GitHub Releases redirects are flaky. Inno Setup would work but then updating is homemade. Velopack gives per-user install with no admin, delta updates, `UpdateManager(new GithubSource("https://github.com/Raaif-Yousuf/DNA-Entropy-Graph", null, false))` in-app check on launch (setting; never restarts during a run), and `VelopackApp.Build().Run()` as the first line of `Main`. Sign with Azure Trusted Signing (cheap, avoids SmartScreen warnings). Velopack's WinUI 3 unpackaged support and `AppNotification` unpackaged registration are verified in the week-1 spike.

---

## 8. Risks and open questions

1. **OAuth app verification**: `cloud-platform` is a sensitive scope; until Google verifies the OAuth consent screen the app is capped at 100 test users and shows an "unverified app" warning. Start the verification (privacy policy URL, homepage, demo video) at project start. Workspace/university admins may block third-party apps entirely; document "use a personal Google account with its own billing" as the fallback.
2. **evo2 on native Windows** (flash-attn wheel, vortex/triton) is the biggest local-engine risk. Week-1 spike; if native fails, WSL2 + the same container behind the same `LocalEngineManager` interface (spec D15).
3. **evo2_40b** needs 2x H100 and Spot/Flex-only capacity; flagged experimental.
4. **DLVM image family drift** (torch/CUDA/Docker) can break the container host. Mitigation: catalog pin, overridable family, nightly canary.
5. **GPU quota UX**: new projects need both `NVIDIA_L4_GPUS` and `GPUS_ALL_REGIONS` > 0; approval can take minutes to days. The wizard must make "skip and come back" painless.
6. **Cost accuracy**: static price table; no access to real billing without export. Label everything "estimate"; link to the Console billing page.
7. **Stopped-VM disk cost** (~$15/month per 150 GB) with "Stop by default"; the 7-day sweep and Cloud page surfacing are the mitigation.
8. **Duplicated validation logic** (Python + C#): shared vector fixtures keep them aligned; the worker remains authoritative and re-validates.
9. **Same account, two PCs**: isolation is by installation id in labels and the bucket lease; the bucket lifecycle rule is bucket-wide so the last PC to change retention wins (acceptable; surfaced in Settings).
10. **igv.js unindexed FASTA** is fine for short sequences; for long sequences generate `.fai` locally.
