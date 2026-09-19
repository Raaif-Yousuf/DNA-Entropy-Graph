# DNA Entropy Graph: design spec

**Date:** 2026-09-18 (owner decisions on 2026-09-19 folded in)
**Status:** Approved by the owner on 2026-09-19. Not yet implemented.
**Repo:** `Raaif-Yousuf/DNA-Entropy-Graph`, public monorepo
**Sources:** `FEATURES.md` (prototype inventory), the `dna-entropy` and `DNA-Entropy-GenBank` prototypes, and the CLAIR repository as the conventions donor.
**Appendices:** [A. Windows app design](2026-09-18-appendix-a-app-design.md) · [B. Cloud and worker design](2026-09-18-appendix-b-cloud-design.md) · [C. Repository conventions](2026-09-18-appendix-c-repo-conventions.md). Where the body and an appendix disagree, the body wins; the appendices are the detailed drafts the body was reconciled from. **As of 2026-09-19, most of that detail has itself been reconciled a second time, into `docs/`**: architecture, the job contract, the science, cloud design, UI copy, packaging, release, and threat model each now have a `docs/` file written later than these appendices and checked against more of the real system, including the worker subpackage these appendices only speculated about. Each appendix's own header names which of its sections are superseded, which remain the only source (a real gap worth knowing about, not a stale one), and which a later decision or this session's own work found to be outright wrong. Read an appendix's header before its tables.

---

## 1. Context

Two Python prototypes proved the science: one Evo 2 (7B) forward pass per contig gives a per-base Shannon entropy track (0 to 2 bits) that IGV, Geneious, SnapGene and Benchling can display. They were command-line tools that shelled out to `gcloud` and SSH, needed the Cloud SDK on every PC, and kept one shared GPU VM (`dna-entropy-box`) running 24/7.

The project is now approved for daily use by non-technical lab biologists at more than one institution. The deliverable is a native Windows 11 app (WinUI 3) that:

- ingests GenBank, FASTA or pasted sequences (many at once),
- runs Evo 2 on a GPU VM inside each user's own Google Cloud project without gcloud, SSH or a terminal,
- returns entropy tracks and shows them in an embedded genome viewer,
- remembers every run, lets users re-download outputs, choose which files they get, and control what happens to the VM afterwards,
- and is the first contact with Google Cloud for most users, so first-run setup must be a guided wizard.

Owner decisions:

| Decision | Choice |
|---|---|
| GCP access | Each user has their own Google account and project. Two lab members may be signed into the same account at once; their sessions must never interact. |
| Cloud mechanism | Direct Google APIs from C#. No gcloud, no SSH. Bucket in, startup script, bucket out. |
| Repo | New public monorepo `Raaif-Yousuf/DNA-Entropy-Graph`. Prototype repos: recommend archive, not delete. |
| Distribution | Signed installer from GitHub Releases with in-app update check. |
| App name | DNA Entropy Graph (label `app=dna-entropy-graph`, folder `%LOCALAPPDATA%\DNAEntropyGraph`). |
| Models | 7B default with higher options. Model and GPU pickers linked, each independently overridable. |
| Local GPU | In v1 for NVIDIA 24 GB+. Intel Arc / CPU: spike only (evo2 is CUDA-only). |
| VM after task | Stop by default, Delete option, Keep-alive option, manual Stop/Delete, resources page. |
| Long sequences and context | User-settable context length with tiled windows, bidirectional (forward + reverse complement) prediction combined so the first bases are accurate, warn/refuse thresholds. Section 5.6. |
| Worker delivery | Author-published container on GitHub Container Registry (D6). |
| Local engine fallback | WSL2 + the same container if native Windows fails (D15). |

---

## 2. Decisions made in this spec (each open one is a `DECISION` issue so the owner can overrule)

| # | Decision | Why |
|---|---|---|
| D1 | Per-job VM, found by label, never by fixed name. | Two users on one account; "no shared singleton". |
| D2 | Velopack installer + delta updates from GitHub Releases, unpackaged self-contained WinUI 3, Azure Trusted Signing. Not MSIX, not Inno. | Per-user install without admin, in-app update in a few lines, no cert-trust dance on managed lab PCs. |
| D3 | .NET 10 LTS + current stable Windows App SDK (2.x as of Sept 2026). Verify in week-1 spike. | Longest support; WASDK 2.x is the current line. |
| D4 | Google OAuth Desktop-app client (PKCE + loopback), scopes `cloud-platform openid email`, tokens in DPAPI-encrypted files (Credential Manager blob limit is 2,560 bytes). | Narrowest scope set that covers Resource Manager, Billing, Service Usage, Compute, Storage, Quotas; one consent line. |
| D5 | The worker's pure-Python package is built at app-release time and baked into the container (D6); the app pins the image by digest. Not `pip install git+...` at boot. | VM runs exactly the bytes the app expects; no GitHub dependency at job time. |
| D6 | Worker software arrives as an **author-published container** `ghcr.io/raaif-yousuf/dna-entropy-worker:<ver>-cuda` (NGC PyTorch base with Transformer Engine and flash-attn prebuilt, plus evo2 and the worker) and `:<ver>-cpu` (mock only, for the smoke test), pinned by digest in the app. Model weights are cached in the user's bucket (`cache/models/<id>/`) after the first download. The VM boots Google's public DLVM image and runs `docker pull` + `docker run`. Install-on-boot (prototype) and a per-project image bake are fallbacks / later options. **Owner confirmed 2026-09-19.** | Removes the 10-minute flash-attn source build and gives the FP8 (40B) path for free; isolates users from DLVM torch drift; the pulled image and HF cache live on the VM disk, so a **stopped** VM restarts to `running` in about 2 minutes. Author-hosted surface stays free and public: OAuth client id, GHCR image, GitHub Releases. |
| D7 | igv.js (MIT) in WebView2 via `SetVirtualHostNameToFolderMapping`, vendored offline. Native ScottPlot overview chart alongside. | Matches the exact files we emit and the users' IGV mental model; 250 KB, no build pipeline. |
| D8 | SQLite (`Microsoft.Data.Sqlite` + Dapper) for run history; `settings.json` for settings. | Queryable, one file, hand-written migrations. |
| D9 | xUnit v3 + Shouldly + NSubstitute; Python: pytest + ruff + uv. | FluentAssertions 8 went commercial; Moq had SponsorLink. |
| D10 | Default after-task action stays **Stop** (owner). Reconciler deletes stopped VMs idle > 7 days; the app reuses its own stopped VM (same model/GPU) for the next job, which restarts in ~2 min because the container and weights are already on its disk. | Trade-off: a stopped 150 GB disk costs ~$15/month if forgotten, and a restart can hit a stockout. The 7-day sweep and the Cloud page mitigate. Owner may flip to Delete. |
| D11 | Evo 2 model menu: `evo2_7b` (default), `evo2_7b_262k`, `evo2_40b` (H100 only, marked experimental). `evo2_1b_base` hidden under Advanced with an H100-only warning. | Official README: 1B, 20B, 40B require FP8 on Hopper. Only the 7B variants run on L4/A100. |
| D12 | ROADMAP.md with checkboxes is not carried over; GitHub Issues + milestones are the only tracker. ToTest.md is the one carve-out. | CLAIR rule that worked. |
| D13 | Naming: job id `yyyymmdd-hhmmss-<6 base32>` (UTC, sortable); VM `deg-<jobId>`; bucket `deg-<projectNumber>-<rand6>`; installation id only in labels (`installation-id`). The `deg-` prefix is what the worker's IAM condition keys on. | Short, unique across PCs without coordination, sortable in bucket listings. |
| D14 | Multi-record FASTA processes **all** records (prototype did first only). | Parity with GenBank; both design drafts flagged it. Owner to confirm. |
| D15 | Local engine: week-1 spike on native Windows (uv venv, torch cu12x, flash-attn wheel, evo2/vortex). If native fails, ship the local engine as **WSL2 + the same `-cuda` container** behind the same `LocalEngineManager` interface. **Owner confirmed 2026-09-19.** | Native flash-attn/triton on Windows is the biggest local-engine risk; the container is already built for the VM. |
| D16 | Evo 2 40B tier is `a3-highgpu-2g` (2x H100 80 GB), Spot or Flex-start only, flagged experimental. | Arc README and NVIDIA NIM matrix both say one 80 GB H100 is not enough. |

---

## 3. Architecture

```
Windows PC (per Windows user)                     User's own GCP project
+------------------------------------+            +----------------------------------+
| DnaEntropyGraph.App (WinUI 3)      |  OAuth     | Bucket deg-<projnum>-<rand6>     |
|  Presentation (ViewModels)         | <--------> |   cache/  jobs/<jobId>/  vms/    |
|  Core (options, contract, errors)  |  Compute   |                                  |
|  Cloud (IGcp gateways + Google)    |  Storage   | VM deg-<jobId>                   |
|  Persistence (SQLite, settings,    |  APIs      |   startup.sh -> docker run worker |
|               DPAPI tokens)        |            |   reads manifest, writes status, |
|  Viewer (igv.js in WebView2)       |            |   uploads outputs, self-stops    |
|  LocalEngine (optional NVIDIA GPU) |            +----------------------------------+
+------------------------------------+
```

- **App never runs torch.** Inference runs on the VM, or on a local NVIDIA GPU through the same worker package (`LocalBlobstore` instead of `GcsBlobstore`, identical manifest).
- **Worker** = the prototype `dna_entropy` package (readers, validation, predictors, analysis, writers, annotators) plus a new `worker/` subpackage (manifest, status/heartbeat, blobstore, cancel, lifecycle, run loop, weights cache) and new `analysis/` modules (windowing, direction; section 5.6). Delivered inside a container (D6).
- **Contract** (`docs/contract/*.schema.json`, versioned): `manifest.json`, `status.json`, `progress.jsonl`, `result.json`, `control/cancel`. Transport-agnostic.
- **Isolation**: job id in every VM name and bucket prefix; installation id, job id, model, lifecycle, app version in labels on every VM, disk and object. Discovery by label `app=dna-entropy-graph`, never by fixed name. Keep-alive VMs are handed follow-up jobs only when the `installation-id` label matches, unless the user explicitly picks a lab-mate's warm VM on the Cloud page (adoption lock via `vms/<vm>/lease.json` with conditional write).
- **Safety net**: every VM carries `scheduling.maxRunDuration` (default 4 h, max 24 h) with `instanceTerminationAction=DELETE`; worker applies stop/delete via the Compute API using the metadata token (guest `shutdown` does not trigger the termination action).

### 3.1 Repo layout

```
DNA-Entropy-Graph/
  CLAUDE.md  AGENTS.md(stub)  README.md  LICENSE(MIT)  THIRD-PARTY-NOTICES.md  OWNER_TODO.md  NEXT_SESSION.md
  FEATURES.md                      # prototype inventory
  app/                             # C# solution DnaEntropyGraph.sln
    Directory.Build.props  Directory.Packages.props  global.json
    src/DnaEntropyGraph.App/       # WinUI 3 exe: Views, Controls, WinUI-bound services, Assets/viewer, Strings/en-US
    src/DnaEntropyGraph.Presentation/   # ViewModels (CommunityToolkit.Mvvm), no WinUI ref
    src/DnaEntropyGraph.Core/      # RunOptions, contract DTOs, JobStateMachine, ErrorCatalog, GpuPlanner, CostEstimator, input sniff/validate port
    src/DnaEntropyGraph.Cloud/     # ONLY project referencing Google.*: gateways + FakeGcp
    src/DnaEntropyGraph.Persistence/    # SQLite repos, migrations, SettingsStore, DpapiTokenStore
    src/DnaEntropyGraph.LocalEngine/    # uv venv provisioning, local worker launcher, health check
    tools/DnaEntropyGraph.CloudCli/     # console host for scripts (resources list, vm create for tests)
    tests/*.Tests/  tests/DnaEntropyGraph.Guards.Tests/  tests/DnaEntropyGraph.App.UiTests/
    packaging/  velopack.json  icon.ico
  worker/                          # Python package dna_entropy (ported) + worker/ subpackage + vm/startup.sh + Dockerfiles
  docs/                            # see section 7
  tests/contract-fixtures/         # shared JSON vectors read by pytest AND xUnit
  scripts/                         # hooks, compile_sprint_log, issue_precheck, check_*, cloud_gpu_test.ps1
  .claude/  .github/
```

---

## 4. Screens and options (the product surface)

Shell: `NavigationView` with **New run**, **Runs**, **Cloud**, footer **Settings**. Status pill: account, project, month-to-date estimate.

### 4.1 First-run wizard (re-enterable from Settings)
1. Welcome (what it costs, nothing runs on our servers)
2. Sign in with Google (system browser, loopback)
3. Choose or create project (Resource Manager v3)
4. Billing: check, link an existing account, or deep link to create one (blocking)
5. Enable services + create bucket + worker service account with least-privilege role (one click, OK/Fix rows)
6. GPU quota: read `NVIDIA_L4_GPUS`, `PREEMPTIBLE_NVIDIA_L4_GPUS`, `NVIDIA_A100_*`, `GPUS_ALL_REGIONS`; request via Cloud Quotas API when eligible, else deep link + copyable justification; explain free-trial limitation
7. Test run: **Quick pipeline test** (mock predictor on `e2-small`, ~$0.01) and **Real GPU test**
8. Optional: **Install local engine** if an NVIDIA 24 GB+ GPU is detected

### 4.2 New run
Drop zone (multi-file, folders, paste dialog), per-file validation pills (records, nt, genes, notices, errors with the validator's plain text), run/batch name template, **Basic**: model, find genes, output file checkboxes, save-to folder, live cost/time estimate. **Advanced** expander (state remembered).

### 4.3 Complete option list (`RunOptions`)

| Group | Option | Values | Default |
|---|---|---|---|
| Input | Format | Auto / GenBank / FASTA / Plain | Auto |
| Input | Treat as RNA | bool | Off |
| Input | Start coordinate | int >= 1 | 1 |
| Input | FASTA records | All / First only | All (prototype was first only) |
| Model | Model | evo2_7b, evo2_7b_262k, evo2_40b (H100), [adv] evo2_1b_base (H100) | evo2_7b |
| Model | GPU tier | Cheapest available / L4 / A100 40 / A100 80 / H100. **Linked**: bigger model raises GPU to minimum that fits; bigger GPU suggests largest model that fits; either overridable with an infeasibility warning | Cheapest available (= L4 for 7B) |
| Model | Predictor | Evo 2 / Test predictor (mock) | Evo 2 |
| Model | Seed | int (mock only) | 0 |
| Model | Context length K | 128..GPU ceiling (warn < 1,024, refuse < 128) | 4,096 |
| Model | Direction | Both combined / Both averaged / Both separate tracks / Forward only / Reverse only | Both combined |
| Model | Window and stride | derived: W = min(2K, GPU ceiling), S = W - K; shown read-only with pass count | derived |
| Genes | Find genes (prokaryotic, FASTA/paste only) | bool | On |
| Output | Files | GenBank, FASTA, bedGraph, WIG, Geneious GFF3, Gene GFF3, Stats, **Entropy TSV (new)** | all, remembered |
| Output | Folder, name template | path, `{file}` `{date}` `{time}` `{model}` `{n}` | Downloads, `{file}` |
| Run target | Where to run | Cloud / This PC / Auto (local if healthy) | Cloud (Auto once local engine installed) |
| Cloud | Zone preference | Auto (last-good, then bucket region, then quota-filtered global list) / explicit | Auto |
| Cloud | Spot VM | bool, auto-fallback to on-demand after one preemption | Off (owner may flip; DECISION) |
| Cloud | After task | Stop / Delete / Keep running | Stop |
| Cloud | Keep-alive minutes, then | 5..240; Stop / Delete | 30; Stop |
| Cloud | Max run duration (safety cap) | 15 min..24 h | 4 h |
| Cloud | Cloud results retention | 7/30/90/365 d | 90 d |
| Cloud | Boot disk GB | 100..500 | 150 (300 for 40B) |
| Local | Notify when done / open folder / open results | bool | On / Off / On |
| Budget (Settings) | Monthly warning cap, hard stop, delete idle stopped VMs after N days | $ / bool / days | $50 / Off / 7 |

### 4.4 Run progress
Stage list with plain words: Checking your files, Uploading, Starting a GPU computer (zone ladder narrated), Preparing the computer (first run in a project explains the cache fill), Analysing (input, contig, window), Saving results, Downloading, Cleaning up. Cost ticker, VM/zone (copyable), live log tail, **Cancel**, **Stop VM now**, **Delete VM now**. Survives navigation and app restart (reconciler reattaches via `status.json` + instance state).

### 4.5 Results
Header (model, GPU, duration, cost), stats per contig, **native overview chart** (ScottPlot: entropy line per contig, instant, drag-select jumps the viewer, seam marker at position K), **igv.js viewer** (FASTA genome + entropy bedGraph + gene GFF3, contig selector, dark-mode synced, collapsed behind "Open in viewer" so the page is instant), file list with Open / Show in folder / **Re-download from cloud**, Open in IGV (port 60151 or launch) / Geneious / folder, Re-run with same settings, Export PNG.

### 4.6 Runs (history)
Grouped by day with sparklines; status, name, inputs, model/GPU, duration, cost, cloud expiry. Actions: open, re-run, re-download, delete cloud results, delete local files, remove. **Find runs from other computers**: scans `jobs/*/manifest.json` in the bucket and imports read-only rows, so two PCs on one account can see each other's history on demand.

### 4.7 Cloud
Live inventory of everything labelled by this app across all zones (VMs with state/cost/created-by-this-PC-or-another, bucket size, cache size), Stop/Start/Delete per VM, "Empty expired", "Clear cache", banner when a VM runs with no job on this PC, **Delete everything this app created** (typed confirmation).

### 4.8 Settings
Account (sign out, switch project, re-run setup check), **Appearance: Light / Dark / Use system**, Run defaults (all of 4.3), Files, Notifications, Budget, External viewers (IGV/Geneious paths), Local engine (install/repair/remove, GPU detected, health), Updates (check on launch, check now), Diagnostics (logs, support bundle without sequences or file names, reset), Advanced (worker image override, DLVM image family override, fake-cloud dev mode).

---

## 5. Cloud and worker design

### 5.1 Auth and consent
- Author-owned GCP project hosts the OAuth Desktop client; enable Resource Manager, Service Usage, Billing, Quotas, IAM, Compute, Storage APIs there (quota project for client-based APIs).
- Consent screen External. Start in Testing (100 test users, **refresh tokens expire every 7 days**, app must treat `invalid_grant` as "sign in again"). Then publish + submit verification (privacy policy URL, homepage, demo video). Workspace admins can mark the client trusted.
- Multiple accounts per PC keyed by `sub`; same account on two PCs mints independent refresh tokens (no collision).

### 5.2 Setup wizard API mapping
Resource Manager `projects.search/create`; Billing `billingAccounts.list`, `projects.updateBillingInfo`; Service Usage `services.batchEnable`; Storage `buckets.insert` (uniform access, public access prevention, lifecycle rule on `jobs/`, labels); IAM `serviceAccounts.create` + custom role `dnaEntropyWorker` (`compute.instances.get/stop/delete`, `compute.zoneOperations.get`, IAM condition on the `deg-` name prefix) + `roles/storage.objectAdmin` on the bucket only; fallback to default compute SA when org policy blocks; Compute `regions.list` quotas + `projects.get` for `GPUS_ALL_REGIONS`; Cloud Quotas `quotaInfos.get` eligibility then `quotaPreferences.create`.
- Escape hatches to verify: GPU VMs with `maxRunDuration <= 7 d` may consume preemptible quota; `provisioningModel=FLEX_START` queues for capacity.

### 5.3 Job contract
Bucket: `cache/models/<id>/`, `jobs/<jobId>/{manifest.json, input/, control/cancel, status.json, progress.jsonl, logs/, output/<name>/, result.json}`, `vms/<vmName>/queue/` and `lease.json` for keep-alive follow-ups.
Stages: queued, provisioning, booting, installing, restoring-cache, model-loading, running, uploading, done | failed | cancelled, idle, finalizing. Heartbeat every 30 s; app polls every 10 s with `ifGenerationNotMatch`. Dead if heartbeat > 180 s and instance not RUNNING, or > 600 s regardless. Per-stage deadlines (provisioning 10 min, boot 8 min, image pull 15 min, run computed from nt count).

### 5.4 VM spec
DLVM family `pytorch-2-9-cu129-ubuntu-2404-nvidia-580` (overridable; ships Docker, NVIDIA container toolkit and gcloud, to verify), pd-balanced 150 GB (300 GB for 40B) autoDelete, `onHostMaintenance=TERMINATE`, `provisioningModel` STANDARD | SPOT | FLEX_START, `maxRunDuration` + `instanceTerminationAction=DELETE`, worker SA with `cloud-platform` scope, shielded vTPM (Secure Boot off for the NVIDIA module), `block-project-ssh-keys=TRUE`, metadata `startup-script` + job pointers, labels. Zone ladder: quota-filtered regions, accelerator availability filter, 3 seq -> 3 par -> 5 par -> 7 par, same name across zones so duplicates are trivially found and deleted. Error classification on structured operation error codes: stockout (next zone), quota (drop region), api_disabled / permission / billing / org_policy (abort with one action). Never auto-escalate GPU cost tier without a click.

Model/hardware matrix: `evo2_7b`, `evo2_7b_262k` bf16 ~14 GB on L4 (`g2-standard-8`, ~$0.85/h) -> A100 40 (`a2-highgpu-1g`, ~$3.67/h) -> A100 80 (`a2-ultragpu-1g`, ~$5/h); `evo2_40b` FP8 on 2x H100 (`a3-highgpu-2g`), Spot/Flex only, experimental; `evo2_1b_base` hidden (also needs H100). GPU ceilings for the context window (7B): 8,192 on L4, larger on A100 40/80 (measure in the GPU acceptance run and record in `GpuPlanner`).

Startup script (metadata, bash, idempotent on every boot): write `booting` status, `df -h`, wait for `nvidia-smi`, write `installing`, `docker pull <digest>`, fetch manifest, `docker run --gpus all` the worker with the bucket job URI; the worker owns every later stage. Cleanup never uses `shutdown -h`: it calls the Compute API on itself (stop or delete) with the metadata token and the app verifies the terminal state after `result.json`. A `shutdown -h +<maxRun+15>` deadman is armed as a last resort. For the CPU smoke test the same script runs on Container-Optimized OS with the `-cpu` image (curl-based status writes, no gcloud).

### 5.5 Worker changes
New `dna_entropy.worker` (blobstore Protocol with `GcsBlobstore` via metadata-token REST and `LocalBlobstore`; manifest; StatusWriter heartbeat; CancelWatcher; lifecycle apply via Compute REST; batch run loop with one predictor instance; weights cache; provenance.json). New `analysis/windowing.py` and `analysis/direction.py` (section 5.6). New `TsvWriter`. Model gating (`MODEL_NEEDS_HOPPER`). CLI loses `cloudrun`/`keep-gpu`.

### 5.6 Context window and bidirectional prediction (owner's three points, 2026-09-18; confirmed 2026-09-19)

Owner asked for: (1) a user-settable rolling context such as 3k nt; (2) pushback when the window is too big for the hardware or the input is too small, with a warn threshold and a refuse threshold; (3) most important: predict the tail from a forward read and the head from a reverse read so the first bases are as accurate as the rest.

**Definitions (worker `analysis/windowing.py`, pure NumPy, unit-testable with the mock):**
- `K` = **context length** (user option, default 4,096; the amount of sequence the model must have seen before a prediction is trusted).
- `W` = **window** = `min(2K, GPU ceiling)`; `S` = **stride** = `W - K`. Windows start at `0, S, 2S, ...`; the last window is right-aligned so it is never short. Passes per direction = `ceil((L - W) / S) + 1`, or 1 when `L <= W`.
- A base contributed by a window has between `K` and `W` bases of context in that direction. This is the cheap equivalent of a rolling window; a true per-base rolling window is one forward pass per base and is rejected (rule: one forward pass per contig).

**Directions:**
- Forward pass on the sequence: base `i` predicted from bases `< i`.
- Reverse pass on the **reverse complement**: base `i` predicted from bases `> i`. Entropy of the complement distribution equals entropy of the base distribution, so the value is directly comparable. Plain reversed text is not DNA and is not used.
- Both passes are tiled with the same `W`, `S`.

**Combination (option `Direction`):**
- `Both, combined` (**default**): base `i` takes the forward estimate when it has `>= K` bases before it, otherwise the reverse estimate when it has `>= K` bases after it, otherwise whichever direction has more context (only happens when `L < 2K`; recorded as "reduced context" in stats and provenance). With `L >= 2K` this is exactly the owner's recipe: the first `K` bases come from the reverse read, the rest from the forward read, one seam at position `K`. The seam position is written to provenance and drawn as a marker in the viewer.
- `Both, averaged`: mean of forward and reverse where both have `>= K` context, otherwise as above.
- `Both, separate tracks`: emits `.entropy.fwd.*`, `.entropy.rev.*`, and the combined track.
- `Forward only`, `Reverse only`: single direction (forward only reproduces the prototype's output bit-for-bit, including the 2.0-bit first base).

**Pushback rules (app validates locally before any VM is created; worker re-validates):**
- `W` above the GPU ceiling (7B: 8,192 on L4; larger on A100/H100, table in `GpuPlanner`): warn and offer **Clamp to <ceiling>** or **Use a bigger GPU** (the linked picker raises the tier). Never silently clamp.
- `K` below 1,024: warning "predictions near a window edge are dominated by the model's prior; results may be noisy".
- `K` below 128: refuse with the exact reason.
- Input shorter than `K`: warning "this sequence is shorter than the context length, so no base reaches full context; results are still produced". Input shorter than 10 nt: refuse (prototype rule).
- `K` larger than `L/2` on a long sequence is allowed; it simply means fewer windows.
- OOM at runtime: halve `W` (keep `K` if possible, else halve both), log a notice, retry once; final values in provenance.

**Cost:** two directions double GPU time; on an L4 a 30 kb sequence at `K = 4,096` is about 12 forward passes of 8,192 nt, roughly one minute against a 3 to 5 minute VM start. **Default is `Both, combined` on.**

**Verify on the first GPU session (THEORY, unverified until then):** Evo 2 was trained with both strands, so forward and reverse-complement entropy distributions should be statistically similar on the TnpB loci. The GPU acceptance checklist compares them and checks the seam at position `K` for a jump larger than typical neighbour variation.

Manifest fields: `analysis.contextLength`, `analysis.window` (derived, recorded), `analysis.stride` (derived), `analysis.direction`.

### 5.7 Local engine (v1)
`DnaEntropyGraph.LocalEngine`: detect NVIDIA GPU + VRAM + driver (nvidia-smi), one-click install into `%LOCALAPPDATA%\DNAEntropyGraph\engine\` using a bundled `uv` binary (managed Python 3.12, pinned lock `requirements-win-cu12x.lock`: torch, evo2, flash-attn wheel from a hash-pinned URL, biopython, pyrodigal; ~10 GB), optional "download model weights now" (~14 GB), health check (imports, CUDA, VRAM, worker version = app version), Repair, Uninstall. Launch `dna_entropy.worker run --manifest <path> --store localdir` with the same manifest; the app watches the same `status.json`. Run-target picker Cloud / This PC / Auto. **Week-1 spike decides native vs WSL2 + container (D15).** Intel Arc / CPU: post-v1 spike (evo2 is CUDA-only); a `device` field passes through the manifest untouched so the seam exists.

### 5.8 Cost and guards
Shipped `pricing.json` (refreshed best-effort from Billing Catalog API), pre-run estimate, live ticker, month-to-date from the app's own ledger + standing costs, thresholds, nothing-left-running scan on launch/exit/every 5 min via `instances.aggregatedList` by label.

### 5.9 Error taxonomy
Every error code maps to plain text and one action button: SIGNIN_EXPIRED, NOT_PROJECT_OWNER, NO_BILLING, FREE_TRIAL_NO_GPU, API_DISABLED, NO_GPU_QUOTA, QUOTA_NOT_ELIGIBLE, GPU_STOCKOUT, ORG_POLICY_BLOCK, NO_EXTERNAL_IP, PERMISSION_ACTAS, VM_DIED, SPOT_PREEMPTED, WORKER_CRASH, GPU_NOT_VISIBLE, IMAGE_PULL_FAILED, MODEL_OOM, MODEL_NEEDS_HOPPER, INPUT_INVALID, RETENTION_EXPIRED, RUN_TIME_LIMIT, HEARTBEAT_LOST, DOWNLOAD_FAILED, SPEND_CAP. Appendix B section 7 has the full table with user text and actions.

### 5.10 Security
Sequences only ever touch the user's own bucket and VM. Private bucket, least-privilege SA, no SSH keys, DPAPI tokens, no telemetry; crash reports open a pre-filled GitHub issue for the user to review.

---

## 6. Local state
`%LOCALAPPDATA%\DNAEntropyGraph\`: `app.db` (SQLite WAL; tables Projects, Runs, RunInputs, RunOutputs, RunEvents, CloudResources, MonthlySpend), `settings.json`, `auth\<sub>.tok` (DPAPI), `install.json`, `logs\`, `cache\inputs\<jobId>\` (exact copy of uploaded inputs so re-run works), `engine\` (local engine). Write-ahead: the Runs row exists before any network call; deterministic VM name; reconciler on launch reattaches non-terminal runs from `result.json` / `status.json` / instance state. Appendix A section 3 has the DDL.

---

## 7. Repository conventions (ported from CLAIR, re-authored; full drafts in Appendix C)

- **CLAUDE.md** quick-reference card: preamble, ~21 numbered Hard Rules (science: model is the only swap point, validate before predict, (L,4) contract, one forward pass, ASCII console + UTF-8/LF; split: app never runs torch or the worker in-process except via LocalEngine, all GCP behind interfaces with FakeGcp, MVVM no logic in code-behind; cloud: never a shared singleton, every resource labelled + max-run-duration, every run ends in a recorded terminal state, no secrets; user: copy rules with no em dashes and one action per error, user files read-only; process: tests first, docs same commit, Issues only tracker + ToTest carve-out, MEASURED/THEORY markers, PowerShell not Unix, venv `worker\.venv`, MIT/Apache/BSD only), Stack table with gotchas, Critical Pitfalls (evo2 nested tuple, uint8 ids, no BOS, billing-off looks like stockout, quota is not stockout, RUNNING is not working, termination action does not fire on guest shutdown, VM runs released bytes, flash-attn source build, wired-to-nothing shapes), Where-to-look table.
- **docs/**: README (index + five status surfaces), hard_rules, entry_points, onboarding, dev_commands, architecture, job_contract, science_and_formats (from prototype DESIGN.md), cloud_design, gcp_setup_manual, ui_conventions, tests, packaging_design, release_runbook, tech_stack, environment, project_structure, threat_model, ToTest, sprint_log, changelog.d/, superpowers/specs/, research/, **user_guide/** (01 install, 02 connect Google Cloud, 03 run, 04 view, 05 history, 06 costs and cleanup, 07 when something goes wrong, glossary). Templates: spec, decision record (issue number is the ADR number), runbook, research note.
- **.claude/**: settings.json (allowlist dotnet/pytest/gh/git read-only; deny gcloud, gh release, force-push, stash; hooks: block_git_stash, block_recursive_delete, block_unlabelled_vm_create; memory sync with secret scan because the repo is public), skills `tests-first`, `wired-to-nothing` (new shape table), `working-an-issue`, `fixing-a-bug` + bug-shapes, `orchestrating-agents`, new `working-on-gcp`, new `winui-dev`; agent `cold-diff-reviewer`; memory seeds.
- **.github/**: labels.yml, issue templates (feature, bug with diagnostics zip, decision), PR template, `ci-app.yml`, `ci-worker.yml`, `ci-docs.yml`, `release.yml`.
- **Conventions**: titles `<area>: <imperative>`, `EPIC <area>: ...`, `DECISION: ...?`; branches `feat|fix|docs|chore/<issue>-<slug>`; conventional commits with `(#n)`; version lockstep app/worker/tag; ToTest rows carry sha + observable + false-pass.

---

## 8. Milestones

| Milestone | Definition of done (one observable) |
|---|---|
| **v0.1 walking skeleton** | Fresh Windows 11 VM: install unsigned build, sign in, pick project, run `sample.gb` with the mock predictor on a CPU `e2-small`, progress lands, `sample.entropy.bedgraph` appears in the folder and in the embedded viewer, VM shows Stopped on the Cloud page. |
| **v0.2 real Evo on GPU** | Same flow with Evo 2 7B on an L4 (A100 fallback exercised once), `pytest -m gpu` green via `cloud_gpu_test.ps1`, TnpB entropy matches the prototype within 1e-3 in Forward-only mode, bidirectional combined default verified. |
| **v0.3 daily-use polish** | History + re-download, output selection, Settings (theme etc.), Cloud page with costs, diagnostics zip, every error has an action, two accounts on one PC see only their own runs, local engine on an NVIDIA PC. |
| **v1.0 lab release** | Signed installer via `release.yml`, in-app update verified on a clean VM, user guide read by a non-owner biologist, ToTest drained, OAuth verification submitted. |
| **post-v1** | Image bake for faster starts, Cloud NAT mode, reference-genome mapping, bigWig, ANN predictor, Intel Arc / CPU spike, macOS. |

---

## 9. What is deliberately not in scope for v1
Reference-genome mapping, bigWig output, the ANN replacement for Evo, Intel Arc / CPU inference, macOS, Cloud NAT / no-external-IP mode, per-project custom image bake, telemetry of any kind.

## 10. Open owner decisions
See the issues labelled `DECISION`. Decided on 2026-09-19 and recorded here: GHCR container delivery (D6); bidirectional combined default with K = 4,096, warn < 1,024, refuse < 128 (5.6); WSL2 + container fallback for the local engine (D15).
