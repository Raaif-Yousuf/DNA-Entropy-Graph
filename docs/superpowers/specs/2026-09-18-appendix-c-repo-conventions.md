# Appendix C: Repository conventions (detailed draft)

**Status:** Design draft produced 2026-09-18 by the repo-conventions planning pass, adapted from the CLAIR repository's conventions, and reconciled into the [main spec](2026-09-18-dna-entropy-graph-design.md). Where this appendix and the spec body disagree (for example .NET version, VM naming, worker delivery), **the spec body wins**. This appendix is the paste-ready source for the `docs:` issues in milestone v0.1: the CLAUDE.md draft, the docs set and templates, the `.claude/` set, the GitHub workflow design, and the issue body template.

**Reconciliation note (2026-09-19, this repo's own docs pass):** this appendix was "paste-ready source" for things that, as of this session, have all been pasted and built: `CLAUDE.md`, `docs/README.md` and the full `docs/` set, `.claude/settings.json`, `.claude/skills/*`, `.claude/agents/cold-diff-reviewer.md`, `.claude/memory/*`, `.github/labels.yml`, the issue and PR templates, and every CI workflow named below except `release.yml` (not written yet, `app/` does not exist) and `cloud-canary.yml` (not written yet). Section 2 (the CLAUDE.md draft) and section 4 (the `.claude/` set) are superseded by the real files; read those, not this draft, for the current exact text, since the real files were adapted rather than pasted verbatim in a few places (the real `.claude/settings.json`, for one, has more allowed commands than the draft below, and the memory-sync hooks are currently disabled pending `DECISION` issue #302, which this draft could not have anticipated). Sections 6 through 8 (issue title conventions, epic structure, the body template, what was deliberately not carried forward, the sequencing plan) remain the accurate, actively-used convention; no pointer added there, they are not stale.

---

## 1. Repo layout

```
DNA-Entropy-Graph/
├── CLAUDE.md                     HAND   quick-reference card (section 2 below)
├── AGENTS.md                     HAND   5-line stub: "Read CLAUDE.md. Rules and routing live there." (guard keeps it < 20 lines so it never forks)
├── README.md                     HAND   what it is, screenshots, install link, 3-step quickstart for lab users, dev quickstart, links to docs/
├── LICENSE                       HAND   MIT
├── FEATURES.md                   HAND   prototype capability inventory (frozen)
├── THIRD-PARTY-NOTICES.md        GEN+HAND  generated tables (scripts/gen_third_party_notices.py from `dotnet list package --include-transitive` + `uv pip list`), hand-written preamble; CI fails if stale
├── OWNER_TODO.md                 HAND   things only the owner can do (Azure Trusted Signing identity validation, OAuth consent screen, GPU quota on the owner's own project)
├── NEXT_SESSION.md               HAND   point-in-time handoff, fully overwritten each session
├── .gitignore  .gitattributes (`* text=auto eol=lf`, `*.ps1 eol=crlf`, `*.sln eol=crlf`)  .editorconfig  .ignore (ripgrep excludes)
│
├── app/                          C# side. One solution.
│   ├── DnaEntropyGraph.sln  Directory.Build.props  Directory.Packages.props (CPM)  global.json  nuget.config
│   ├── src/  DnaEntropyGraph.Core/  .Cloud/  .Presentation/  .App/  .Persistence/  .LocalEngine/
│   ├── tools/DnaEntropyGraph.CloudCli/
│   ├── tests/  .Core.Tests/  .Cloud.Tests/  .Presentation.Tests/  .Persistence.Tests/  .Guards.Tests/  .App.UiTests/
│   └── packaging/  velopack.json  icon.ico  README.md (how a release is cut; links docs/release_runbook.md)
│
├── worker/                       Python side. `dna_entropy` package.
│   ├── pyproject.toml            ported: core deps numpy/biopython/typer; extras dev/evo/genes/worker; ruff + pytest config; `gpu` marker
│   ├── uv.lock                   GEN
│   ├── README.md                 "you probably do not need to run this by hand" + the dev loop
│   ├── src/dna_entropy/          ported package + worker/ + analysis/windowing.py + analysis/direction.py; _version.py GEN (lockstep with app)
│   ├── vm/  startup.sh  README.md (what each step is for; which gce-brief lesson it answers)
│   ├── Dockerfile.cuda  Dockerfile.cpu
│   ├── engine/requirements-win-cu12x.lock
│   └── tests/                    mirrors src/; tests/data/ fixtures; test_worker_*.py with LocalBlobstore
│
├── docs/                         see section 3
├── docs/contract/                manifest.schema.json  status.schema.json  error-codes.json  gpu-catalog.json  (GEN from Python dataclasses, checked in, drift-guarded)
├── tests/contract-fixtures/      shared JSON vectors (validation cases, sample manifests, progress streams) read by pytest AND xUnit
├── .claude/                      see section 4
├── .github/                      see section 5
│   ├── workflows/ ci-app.yml  ci-worker.yml  ci-docs.yml  release.yml  cloud-canary.yml
│   ├── ISSUE_TEMPLATE/ feature.yml  bug.yml  decision.yml  config.yml
│   ├── PULL_REQUEST_TEMPLATE.md  labels.yml  dependabot.yml
└── scripts/
    ├── hooks/ block_recursive_delete.py  block_git_stash.py  block_unlabelled_vm_create.py  README.md
    ├── compile_sprint_log.py  check_docs_index.py  check_changelog_fragments.py  check_version_lockstep.py
    ├── check_third_party_notices.py  check_totest_format.py  gen_third_party_notices.py  gen_manifest_schema.py
    ├── issue_precheck.py  sync_labels.ps1  new_issue.ps1  sync_memory.py (with secret scan)
    ├── dev_app.ps1  dev_worker.ps1  gcp_burn.ps1  cloud_gpu_test.ps1  triage_diagnostics.py
```

`.gitignore` must cover: `worker/.venv/`, `app/**/bin/`, `app/**/obj/`, `app/**/*.user`, `.vs/`, `app/secrets/*.local.json`, `*.pt`, `*.safetensors`, `out/`, `runs/`, `Releases/` (Velopack output), `*.tok`. No `.scratch/` (use the session scratchpad).

---

## 2. CLAUDE.md draft (full text; adjust versions to the spec body)

> Superseded: the real `CLAUDE.md` at the repo root is the current text (rewritten via issue #267). Read that file, not this draft; the Hard Rule count and numbering match, but the real file has been adjusted since this was pasted.

```markdown
# DNA-Entropy-Graph : Quick-Reference Card

> Loaded into every session, so it stays small. It holds each rule in its shortest
> enforceable form plus the stack. The why, the history and the carve-outs live in
> [`docs/hard_rules.md`](docs/hard_rules.md); everything else lives in
> [`docs/`](docs/README.md). Read the linked file before concluding a rule does not
> apply to you.

**Name:** DNA-Entropy-Graph. User-facing name: **DNA Entropy Graph**.

**App:** A Windows desktop app for lab biologists. Drop a GenBank or FASTA file (or paste
a sequence), press Run, and get a per-base Shannon entropy track computed by the Evo 2
genomic language model on a GPU in the user's *own* Google Cloud project, then view
it in an embedded genome viewer or open it in IGV, Geneious, SnapGene or Benchling. We
host nothing but a public container image and the installer. No terminal, no gcloud,
no SSH for the user.

**Two halves, one repo:** `app/` (C#, WinUI 3) is a thin client that owns the UI, the
run history and every Google Cloud API call. `worker/` (Python, `dna_entropy`) is the
science: `input -> validate -> predict -> analyze -> export`. The worker runs on the VM
inside a container, or on a local NVIDIA GPU through the LocalEngine. The app never
imports torch.

**Repo:** `%USERPROFILE%\DNA-Entropy-Graph` | **Tracker:** GitHub Issues on
`Raaif-Yousuf/DNA-Entropy-Graph` | **Milestones:** v0.1 walking skeleton, v0.2 real Evo
on GPU, v0.3 daily-use polish, v1.0 lab release, post-v1.

---

## Hard Rules

Rationale and carve-outs: [`docs/hard_rules.md`](docs/hard_rules.md) (numbers match).
Rules marked (G) have a mechanical guard: `dotnet test app/tests/DnaEntropyGraph.Guards.Tests`
and `python scripts/check_*.py` (listed in [dev_commands.md](docs/dev_commands.md)).
Add a carve-out to the guard's allowlist AND `hard_rules.md`, or to neither.

### The science (carried from the prototype)

1. **The model is the only swappable detail that matters.** (G) All Evo-specific code
   lives in `worker/src/dna_entropy/predictors/evo.py`. Nothing else imports `torch`,
   `evo2` or `flash_attn`, or names an Evo token id. Heavy deps are `[evo]` extras, never
   core; the mock pipeline installs and runs with no GPU.
2. **Validate before predict, always.** The worker validates before the predictor sees a
   byte, and the *app* validates locally (same rules, C# port in `Core/Inputs/`) before it
   creates a single cloud resource. Nobody pays for a VM to learn their file has an `X` in it.
3. **The `(L, 4)` contract.** (G) `Predictor.predict(seq) -> np.ndarray` shape
   `(len(seq), 4)`, `float32`, columns `[A, C, G, T]`, rows sum to 1.0 (checked by
   `check_probability_matrix` at the boundary). Entropy is `(L,)` with every value in
   `[0.0, 2.0]`. Everything downstream depends on this array and nothing else.
4. **One forward pass per window, never per position.** Evo is autoregressive: one pass
   yields every position in the window. Long sequences are tiled into overlapping windows
   of `2K` with stride `K` (context length `K`), each run forward and on the reverse
   complement, and combined by `analysis/direction.py`. A per-base rolling window is
   forbidden.
5. **ASCII-safe console output in the worker; files are UTF-8 with LF.** `OK:` /
   `ERROR:` / `-`, never glyphs. Every file writer passes `encoding="utf-8", newline="\n"`.

### The split (new)

6. **The app never imports torch and never runs inference in-process.** (G) No Python in
   `app/` except the LocalEngine launching the worker as a child process; no
   `torch`/`onnx`/inference package in any `.csproj`. The MockPredictor exists for tests and
   for the v0.1 CPU walking skeleton, which still runs *on a VM*.
7. **Every Google Cloud call goes through an interface in `DnaEntropyGraph.Core/Cloud/`, and
   only `DnaEntropyGraph.Cloud` references `Google.*`.** (G) `FakeGcp` implements every
   interface with scripted failures (billing off, API off, quota, stockout, 403, preempt).
   ViewModel and runner tests run against the fake.
8. **MVVM: no logic in code-behind.** (G) A `*.xaml.cs` contains `InitializeComponent()`,
   constructor DI, and nothing else that branches. State and behaviour live in
   `DnaEntropyGraph.Presentation` (CommunityToolkit.Mvvm), which has no WinUI reference and
   is tested without a UI thread.

### The cloud (new)

9. **Never a shared singleton cloud resource.** (G) Two users may share one Google account.
   Every VM and bucket prefix carries the *job id*; every resource carries the
   *installation id* as a label. Discovery is by **label**, never by a fixed name. The
   prototype's `dna-entropy-box` is exactly what this forbids.
10. **Every cloud resource carries the standard labels, and every VM carries a
    `maxRunDuration` and `instanceTerminationAction=DELETE`.** (G) Labels:
    `app=dna-entropy-graph`, `installation-id`, `job-id`, `model`, `app-version`,
    `lifecycle`. `VmSpec` rejects a spec missing any of these before the request is built.
    An unlabelled resource is invisible to the Cloud page and therefore a leak.
11. **Every run ends in a recorded terminal state, and "keep alive" always has an expiry.**
    Default after a run: **Stop**. Options: Delete, or Keep alive until `<time>` (never
    indefinitely). The VM stops or deletes *itself* through the Compute API using the
    metadata token; guest `shutdown` is a last-resort deadman only, because the termination
    action does not fire on guest shutdown. The app verifies the terminal state after
    `result.json`.
12. **No secrets in the repo.** (G) No service-account keys, refresh tokens or API keys,
    ever. The OAuth *client id* is committed (public by design). The Desktop-app client
    "secret" is not confidential per Google, but is still injected at build time from a CI
    secret and read from `app/secrets/oauth_client.local.json` (gitignored) in dev. User
    tokens live in DPAPI-encrypted files, never in settings or SQLite.

### The user (new)

13. **User-facing copy rules.** (G) Every string a user can see lives in
    `app/src/DnaEntropyGraph.App/Strings/en-US/Resources.resw`, never inline in XAML or C#.
    No jargon without the plain phrase first ("a rented computer with a graphics card
    (a VM)"). **No em dashes** anywhere in `.resw`, error strings, exports, or release
    notes. **Every error names one action the user can take**, and the action is a button
    or a link when one exists. Comments, docstrings and `docs/` are exempt from the dash
    rule; nothing else is.
14. **The user's files are read-only to us.** Outputs go to the chosen output folder
    (default Downloads) and a copy of every input is kept under `%LOCALAPPDATA%`. We never
    write next to the input, never modify the input, never delete a run's outputs without
    the user asking. Re-download from the bucket is always offered while the object exists.

### The process (carried from CLAIR)

15. **Tests first.** A new class gets its test file before its code; a new worker module
    gets `worker/tests/test_<module>.py` first. Red output in the transcript, then code,
    then green. `dotnet test` and `pytest -m "not gpu"` are green on the laptop before any
    commit. **GPU tests run only via `scripts/cloud_gpu_test.ps1`** on a labelled VM in the
    owner's project; the dev laptop has an Intel Arc and cannot.
16. **Docs in the same commit.** Behaviour change -> the relevant `docs/` file changes in
    the same commit. On a branch write `docs/changelog.d/<branch-name>.md` (slashes to
    dashes, **starts with `- `**); the merger folds it with
    `python scripts/compile_sprint_log.py`. A user-visible change also updates
    `docs/user_guide/`.
17. **GitHub Issues is the only tracker.** Labels `P1/P2/P3`, `area:{app,worker,cloud,
    viewer,docs,packaging,ux,local-engine}`, `DECISION`, `post-v1`, `good-first-issue`;
    milestones as above. **One carve-out: [`docs/ToTest.md`](docs/ToTest.md)**, closed
    issues whose behaviour is unproven on a real build or a real VM. Never create
    `ROADMAP.md`, `TODO.md`, `known_issues.md`.
18. **Mark theories as theories.** A causal claim carries `MEASURED <date>:` plus the
    observation, or `THEORY (unverified):`. Disproving a theory replaces it. The disproven
    list is [entry_points.md](docs/entry_points.md); read it before re-deriving a cause.
19. **PowerShell, not Unix, on the dev box.** `tail` -> `Select-Object -Last`, `grep` ->
    `Select-String`, `rm -rf` -> refused by a hook inside the repo. Bash is for
    `worker/vm/*.sh` only, and those run on Ubuntu.
20. **The venv is `worker\.venv` and nothing else.** (G) Created with `uv venv --python 3.12`;
    `uv pip install`, never global pip. The `python` on PATH on this laptop is an MSYS2
    build with no wheels.
21. **Licences: MIT/Apache/BSD only in anything that ships.** (G) No GPL/LGPL/AGPL. Adding
    a dependency updates `THIRD-PARTY-NOTICES.md` in the same commit. Evo 2 weights are
    Apache-2.0, igv.js MIT, Velopack MIT, CommunityToolkit MIT, ScottPlot MIT, Google
    client libraries Apache-2.0.

---

## Stack

| Layer | Technology | The gotcha |
| --- | --- | --- |
| Desktop | WinUI 3 on Windows App SDK (current stable), .NET 10 LTS, x64, unpackaged self-contained | `x:Bind` defaults to `Mode=OneTime`; `ContentDialog` needs `XamlRoot`; UI updates from a task need `DispatcherQueue` |
| MVVM | CommunityToolkit.Mvvm 8.x | `[ObservableProperty]` needs a `partial class` and a field named `_camelCase` |
| DI | Microsoft.Extensions.DependencyInjection | A service never registered resolves to a runtime exception on first page open, not at build. The guards test resolves every ViewModel |
| Viewer | igv.js (MIT) in WebView2; ScottPlot.WinUI for the overview chart | Local files only via `SetVirtualHostNameToFolderMapping`; `file://` is blocked. Check `CoreWebView2Environment.GetAvailableBrowserVersionString()` and fail with a named action |
| Cloud | Google.Cloud.Compute.V1, Storage.V1, ResourceManager.V3, Billing.V1, ServiceUsage.V1, Iam.Admin.V1, CloudQuotas.V1, Google.Apis.Auth (Desktop-app OAuth, PKCE, loopback) | Compute is *operation*-based: `Insert` returns an operation; the real error (stockout, quota) is in the polled operation |
| Installer / updates | Velopack (MIT), GitHub Releases as the feed | The update check reads the *Releases* API, so a draft release is invisible and a pre-release is visible only if the channel says so |
| Signing | Azure Trusted Signing in `release.yml` | The step **silently no-ops** if the secret is unset (CLAIR #490). The workflow asserts every secret exists before building |
| History | SQLite via Microsoft.Data.Sqlite + Dapper | One file, migrations in `Persistence/Migrations/`, `PRAGMA user_version` |
| Logs | Serilog to `%LOCALAPPDATA%\DNAEntropyGraph\logs\`, diagnostics zip from Settings | Never log the sequence, the file name, or the email. Log ids, states, error classes |
| Worker | Python 3.12, numpy, biopython, typer; extras `[evo]` torch + evo2 + flash-attn, `[genes]` pyrodigal, `[worker]`; pytest, ruff, uv | `pytest -m "not gpu"` on the laptop; `[evo]` only inside the container |
| Container | `ghcr.io/raaif-yousuf/dna-entropy-worker:<ver>-cuda` (NGC PyTorch base) and `-cpu`, pinned by digest | The app refuses an image digest not in its allowlist unless developer mode |
| VM | Google DLVM image family `pytorch-2-9-cu129-ubuntu-2404-nvidia-580`, `g2-standard-8` (1x L4) first, A100 fallback by click; `pd-balanced` 150 GB; `maintenance=TERMINATE` | Ships Docker + NVIDIA container toolkit + gcloud (verify on family change) |
| Tests (C#) | xUnit v3, Shouldly, NSubstitute | Not FluentAssertions 8+ (commercial licence), not Moq (SponsorLink history) |

---

## Critical Pitfalls (read before editing)

**The contract guard at the predictor boundary has caught two real-model bugs and must
stay.** MEASURED on a GCP L4 (prototype Sprint 3): evo2 returns a **nested tuple**
unwrapped by `_extract_logits`; the tokenizer returns **uint8 ids** that torch reads as a
boolean mask unless cast to `int`; the tokenizer adds **no BOS** (L == len(seq)), so row 0
is uniform (2.0 bits) by design in Forward-only mode. Do not "simplify" `_extract_logits`
or the cast.

**Billing off and Compute API off look exactly like a stockout, everywhere.** MEASURED in
the prototype's real misconfigured project. The app therefore runs **preflight in this
order** before any `Insert`: project `ACTIVE` -> billing enabled -> Compute API enabled
(enable it ourselves, idempotent) -> GPU quota > 0 in at least one region -> bucket exists.
`CloudErrorClassifier` buckets every error into `billing | api_disabled | quota | stockout |
already_exists | permission | org_policy | network | other`; **billing/api_disabled/permission/
org_policy abort immediately** with a named action, while **quota and stockout** move on.

**Quota is not stockout.** Quota is per-region, fixable by the user, and pre-filterable.
Stockout is per-zone, transient, and the only thing worth retrying. `GPUS_ALL_REGIONS=0`
overrides a regional 1.

**RUNNING is not working.** Instance status says nothing about the job. The **only** health
signal is the worker's heartbeat in `status.json`. `startup.sh` writes `nvidia-smi`'s result
as its **first** progress line; the app fails the run and stops the VM if no heartbeat lands
within the boot deadline, and if the first line reports no GPU.

**`instanceTerminationAction` does not fire on guest shutdown.** MEASURED in CLAIR
(#2908): it fires when *Compute Engine* stops the VM (max-run-duration expiry), not when the
guest runs `shutdown`. `worker/vm/startup.sh` therefore ends by calling the Compute API on
itself (`stop` or `delete`) with the metadata token, and the app *also* verifies the terminal
state. `maxRunDuration` stays as the backstop; `shutdown -h +N` is a deadman only.

**The VM runs released bytes, not your working tree.** The app pins the worker container by
digest. A worker change is not on the VM until a tagged release builds the image (or, in
dev, `scripts/cloud_gpu_test.ps1 -Image <digest>` overrides it and says so in the log).

**"Reverse" means reverse complement.** Feeding the model a sequence spelled backwards is
not DNA. The reverse pass runs on the reverse complement and maps index `i -> L-1-i`;
entropy is invariant to the relabelling. Combined mode takes the first `K` bases from the
reverse pass; the seam at position `K` is recorded in provenance.

**"Wired to nothing" is the bug class we inherit from CLAIR, with new shapes here.** A XAML
binding to a property that does not exist compiles and shows blank; an `ICommand` nobody
binds; a service never registered in DI; a startup-script step that no-ops; a bucket
lifecycle rule never applied; a label missing so the Cloud page cannot find the VM; a
setting saved and never read. Before reporting anything done, **name the one observable
that would differ if it were wired to nothing, and check it.** The `/wired-to-nothing`
skill has the per-shape table.

**Long operations never block a tool call or the UI thread.** Create + boot + pull + run is
6 to 20 minutes. The runner is a polled state machine, progress is a stream of events, and an
agent driving `cloud_gpu_test.ps1` launches detached and polls the log.

---

## Where to look

| Topic | Doc |
| --- | --- |
| **Narrow task, cold start? Read this first** | **[entry_points.md](docs/entry_points.md)** |
| Which skill to reach for | [docs/README.md](docs/README.md) (`tests-first`, `wired-to-nothing`, `working-an-issue`, `fixing-a-bug`, `working-on-gcp`, `winui-dev`, `orchestrating-agents`) |
| Hard-rule rationale + carve-outs | [hard_rules.md](docs/hard_rules.md) |
| Architecture: app/worker split, job lifecycle, state machine | [architecture.md](docs/architecture.md) |
| The app-worker contract (manifest, bucket layout, progress, result) | [job_contract.md](docs/job_contract.md) |
| The science: `(L,4)`, entropy, alignment, windowing, direction, writers | [science_and_formats.md](docs/science_and_formats.md) |
| Cloud: preflight, error classes, labels, cost, termination | [cloud_design.md](docs/cloud_design.md) · [gcp_setup_manual.md](docs/gcp_setup_manual.md) |
| UI conventions (WinUI patterns, copy rules, theme) | [ui_conventions.md](docs/ui_conventions.md) |
| Environment, venv, dev commands, test commands | [onboarding.md](docs/onboarding.md) · [dev_commands.md](docs/dev_commands.md) |
| Tests: what runs where, GPU tests, guards | [tests.md](docs/tests.md) |
| Packaging, signing, update channel, release runbook | [packaging_design.md](docs/packaging_design.md) · [release_runbook.md](docs/release_runbook.md) |
| The approved design | [docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md](docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md) |
| **Issues, backlog, roadmap** | **[GitHub Issues](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues)** (Rule 17) |
| **Closed in code, unproven on a build or VM** | **[ToTest.md](docs/ToTest.md)** |
| Where the last session stopped | **[NEXT_SESSION.md](NEXT_SESSION.md)** |
| Has this issue already been done? | `python scripts/issue_precheck.py <n>...` |
| What shipped | [sprint_log.md](docs/sprint_log.md) (compiled from `changelog.d/`) |
| For the lab user (separate voice) | [docs/user_guide/](docs/user_guide/README.md) |
| Things only the owner can do | [OWNER_TODO.md](OWNER_TODO.md) |

*Last updated: <date> | v0.1 not yet cut.*
```

---

## 3. docs/ set for day one

> Superseded: `docs/README.md` is now the real index and the house-style section below was pasted into it near-verbatim, as planned. Read the real file for the current topic map; every doc named below now exists.

| File | Type | Purpose (one line) |
|---|---|---|
| `docs/README.md` | index | Topic map, house style (which surface owns which fact), skills table, reading order, editing rules |
| `docs/hard_rules.md` | reference | The why/history/carve-outs for each numbered rule; the "which rules a machine checks" table |
| `docs/entry_points.md` | reference | Task-shaped index, invariants easy to miss, disproven-diagnoses table (starts with the prototype's: "cloud creates fail everywhere = no capacity" -> billing off) |
| `docs/onboarding.md` | onboarding | First hour on a fresh Windows 11 box: VS 2022 workloads, WASDK, `uv venv`, the MSYS2-python trap, run app + `pytest -m "not gpu"`, 10-minute smoke |
| `docs/dev_commands.md` | reference | Every `dotnet`/`pytest`/`ruff`/`gh`/script command, PowerShell equivalents table, GPU test recipe |
| `docs/architecture.md` | spec (as-built) | App/worker split, projects and dependency arrows, `JobPhase` machine, sequence diagram of one run, where state lives |
| `docs/job_contract.md` | spec | manifest/status/progress/result schemas (versioned), bucket layout, compatibility rules |
| `docs/science_and_formats.md` | reference | Ported from prototype DESIGN.md: contracts, position semantics, windowing and direction, validation rules, output matrix by input kind, viewer support matrix, limitations |
| `docs/cloud_design.md` | spec + decision record | Preflight order, error classifier table with recorded payloads, labels, naming, cost table, termination semantics, per-job VM decision |
| `docs/gcp_setup_manual.md` | runbook (user-adjacent) | The fallback when the in-app wizard cannot: create project, link billing, enable APIs, request `NVIDIA_L4_GPUS`; plain voice |
| `docs/ui_conventions.md` | reference | WinUI patterns (NavigationView shell, InfoBar for errors, ContentDialog rules), theme tokens, copy rules with examples, state-to-UI table for `JobPhase` |
| `docs/tests.md` | reference | What runs where (laptop / CI windows / CI ubuntu / GPU VM), the guards list, fixtures, the `gpu` marker, flaky-test policy |
| `docs/packaging_design.md` | spec + decision record | Velopack vs MSIX, self-contained publish, signing, update channel, version lockstep, container images |
| `docs/release_runbook.md` | runbook | Cut a release: bump, tag, watch `release.yml`, verify signature, install on a clean VM, publish notes, ToTest rows |
| `docs/tech_stack.md` | reference | Every dependency, version, licence, purpose (both halves); source for THIRD-PARTY-NOTICES |
| `docs/environment.md` | reference | Env vars and settings keys (`DEG_*` for dev overrides), where each is read |
| `docs/project_structure.md` | reference | Annotated tree, generated vs hand-written, never-commit list |
| `docs/threat_model.md` | reference | Trust boundaries: user's Google account, our OAuth client, the container, the VM, the bucket, the laptop; what we never see |
| `docs/ToTest.md` | ToTest | The verification queue (rules in section 5) |
| `docs/sprint_log.md` | sprint_log | Append-only "Recent changes", compiled |
| `docs/changelog.d/README.md` | changelog.d | The fragment convention |
| `docs/superpowers/specs/` | spec dir | Dated design specs, `YYYY-MM-DD-<slug>.md` |
| `docs/research/` | research | Dated notes, e.g. `2026-09-XX-gpu-pricing.md` |
| `docs/user_guide/README.md` | user guide index | Written for the biologist. No jargon, no dashes, one task per page |
| `docs/user_guide/01-install.md` .. `07-when-something-goes-wrong.md`, `glossary.md` | user guide | install; connect Google Cloud; run a sequence; view results; history and re-download; cloud costs and cleanup; when something goes wrong; glossary |

### `docs/README.md` house-style section (paste-ready)

```markdown
## House style: who owns which fact

`CLAUDE.md` points, `docs/` explains. Five status surfaces exist and each owns a
different kind of fact. Do not merge two of them.

| Surface | Owns | Lifecycle |
| --- | --- | --- |
| GitHub Issues | Every bug, idea and roadmap item: what is wrong or wanted, with labels and milestones | Open -> closed. Nothing duplicates it in markdown |
| ToTest.md | Closed issues whose behaviour is unproven on a real installer build or a real GPU VM | Row added on close, deleted on verification, issue reopened on failure. Must drain |
| sprint_log.md | What shipped, one entry per merge, newest first | Append-only, never edited after the fact |
| changelog.d/ | The write path into sprint_log from a branch, one fragment per branch | Written on the branch, folded and deleted at merge |
| NEXT_SESSION.md | Where the last session stopped and what the next one does first | Overwritten every session; no history |

Issues is *what*, ToTest is *is it really fixed*, sprint_log is *what already shipped*,
NEXT_SESSION is *where we left off*. The user guide is a sixth surface with a different
reader: it owns *how a biologist does a task* and nothing about how the code works.

Two more rules of this house:
- A number written in prose (test counts, guard counts, instance counts) goes stale.
  Prefer "run X to get the current number" over the number.
- A claim about a cause carries `MEASURED <date>:` or `THEORY (unverified):` (Rule 18).
```

### Template heading skeletons

**Spec** (`docs/superpowers/specs/YYYY-MM-DD-<slug>.md`):
```
# <Title>
**Date:** | **Status:** Draft / Approved (owner, date) / Implemented (#issue) / Superseded by
**Parent epic:** #N | **Supersedes:**
## 1. Why this exists (the observed problem, with measurements)
## 2. The model (entities, glossary of load-bearing words: means / does not mean)
## 3. Behaviour (state machine or sequence; what the user sees per state)
## 4. Contracts touched (job_contract, interfaces, schema; versioning)
## 5. What is explicitly out of scope
## 6. Work packages (each maps to one sub-issue; each names its test and its wired-to-nothing observable)
## 7. Open questions for the owner
```

**Decision record** (`docs/<topic>_decision.md` or a section in a spec):
```
# <Topic>: decision record (issue #N)
> Decision-support document, not a build task. Rule 18 applies: every claim is MEASURED or THEORY.
**Status:** open, needs an owner call / decided <date> by owner
## 1. What is actually true today (verified, with evidence per row)
## 2. What already exists and whether it is wired to anything
## 3. Options (table: option, cost, what it fixes, what it breaks)
## 4. Recommendation (one option, and the smallest first step)
## 5. What shipped without waiting for the call, and what is still open
## 6. Questions only the owner can answer
```

**Runbook** (`docs/<name>_runbook.md`):
```
# <Name> runbook: <version or target>
> Written <date>, before the thing it prepares for was done. Left as written per Rule 18.
## 0. Preconditions (what must be true; the command that proves each)
## 1. What changed since last time
## 2. Version line (what it must be and why)
## 3. Pre-flight gates (commands and their real outputs)
## 4. The exact command sequence
## 5. Verification on a clean machine (the false pass named, the real proof named)
## 6. Rollback
## 7. DONE notes (appended after, dated)
```

**Research note** (`docs/research/YYYY-MM-DD-<slug>.md`):
```
# <Question>, researched <date>
> Snapshot; goes stale by design. Issues are the tracker (Rule 17).
## 1. The question and why now
## 2. What was checked (sources, versions, licences, with dates)
## 3. Findings (table)
## 4. What this changes for us (issues filed: #...)
## 5. What was deliberately not pursued, and why
```

### ToTest.md row format

```
| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |
```
`Needs` is one of `app-dev`, `installer`, `gpu-vm`, `cpu-vm`, `two-accounts`, `local-gpu`. Grouped by `Needs`, because the cost is setup. `scripts/check_totest_format.py` validates the sha with `git cat-file -e` and fails CI on a row older than 45 days.

---

## 4. `.claude/` set

> Superseded: the real `.claude/settings.json`, `.claude/skills/*`, `.claude/agents/cold-diff-reviewer.md`, and `.claude/memory/*` all exist. The real settings.json's allow-list has grown past what is pasted below, and the `SessionStart`/`Stop` memory-sync hooks are currently empty arrays pending `DECISION` issue #302 (a privacy finding this draft predates), documented in `.claude/README.md`. Read the real files for the current exact behaviour.

### `.claude/settings.json`

```json
{
  "$schema": "https://json.schemastore.org/claude-code-settings.json",
  "permissions": {
    "allow": [
      "Bash(dotnet build *)", "Bash(dotnet test *)", "Bash(dotnet restore *)", "Bash(dotnet list *)",
      "Bash(dotnet format --verify-no-changes *)",
      "Bash(worker/.venv/Scripts/python.exe -m pytest *)",
      "Bash(worker/.venv/Scripts/python.exe -m ruff check *)",
      "Bash(worker/.venv/Scripts/python.exe -m ruff format --check *)",
      "Bash(worker/.venv/Scripts/python.exe scripts/*)",
      "Bash(gh issue view *)", "Bash(gh issue list *)", "Bash(gh issue create *)", "Bash(gh issue comment *)", "Bash(gh issue edit *)",
      "Bash(gh pr view *)", "Bash(gh pr list *)", "Bash(gh pr checks *)", "Bash(gh run list *)", "Bash(gh run view *)",
      "Bash(gh api repos/Raaif-Yousuf/DNA-Entropy-Graph/*)",
      "Bash(git status *)", "Bash(git log *)", "Bash(git diff *)", "Bash(git show *)",
      "PowerShell(Get-Content *)", "PowerShell(Get-ChildItem *)", "PowerShell(Get-Item *)", "PowerShell(Select-String *)",
      "PowerShell(Select-Object *)", "PowerShell(Measure-Object *)", "PowerShell(Test-Path *)",
      "PowerShell(dotnet build *)", "PowerShell(dotnet test *)", "PowerShell(& worker\\.venv\\Scripts\\python.exe *)"
    ],
    "deny": [
      "Bash(gcloud *)", "PowerShell(gcloud *)", "Bash(gh release *)", "Bash(git push --force *)", "Bash(git stash *)"
    ]
  },
  "hooks": {
    "PreToolUse": [
      { "matcher": "Bash|PowerShell", "hooks": [
        { "type": "command", "command": "PY=$(command -v python3 || command -v python); [ -n \"$PY\" ] && [ -f scripts/hooks/block_git_stash.py ] && \"$PY\" scripts/hooks/block_git_stash.py || true", "shell": "bash", "timeout": 10, "statusMessage": "Checking for git stash..." },
        { "type": "command", "command": "PY=$(command -v python3 || command -v python); [ -n \"$PY\" ] && [ -f scripts/hooks/block_recursive_delete.py ] && \"$PY\" scripts/hooks/block_recursive_delete.py || true", "shell": "bash", "timeout": 10, "statusMessage": "Checking for a recursive delete inside the repo..." },
        { "type": "command", "command": "PY=$(command -v python3 || command -v python); [ -n \"$PY\" ] && [ -f scripts/hooks/block_unlabelled_vm_create.py ] && \"$PY\" scripts/hooks/block_unlabelled_vm_create.py || true", "shell": "bash", "timeout": 10, "statusMessage": "Checking a cloud create carries labels + max-run-duration..." }
      ] }
    ],
    "SessionStart": [ { "hooks": [ { "type": "command", "command": "PY=$(command -v python3 || command -v python); [ -n \"$PY\" ] && [ -f scripts/sync_memory.py ] && \"$PY\" scripts/sync_memory.py --pull || true", "shell": "bash", "timeout": 20, "statusMessage": "Pulling memory from .claude/memory..." } ] } ],
    "Stop": [ { "hooks": [ { "type": "command", "command": "PY=$(command -v python3 || command -v python); [ -n \"$PY\" ] && [ -f scripts/sync_memory.py ] && \"$PY\" scripts/sync_memory.py --push || true", "shell": "bash", "timeout": 20, "statusMessage": "Mirroring memory into the repo..." } ] } ]
  }
}
```

Notes: `gcloud` is denied on purpose (the app has no gcloud; an agent reaching for it is solving the wrong problem; `scripts/cloud_gpu_test.ps1` uses the app's own `DnaEntropyGraph.Cloud` via the `CloudCli` console host). `gh release` is denied because publishing is outward-facing and owner-only. The third hook scans a command for `instances create`/`CloudCli vm create` and refuses one without `--labels` and `--max-run-duration`. **Public repo caveat:** `sync_memory.py` must run a secret-pattern scan and refuse to push a file containing an email, a project number, a token-shaped string or a billing account id; `.claude/README.md` says so.

### Skills (frontmatter + body outline)

**`tests-first`**: description "Use before writing ANY feature, fix or behaviour change in DNA-Entropy-Graph, in C# or Python. Write the failing test first, prove it fails, add the neighbours, then the code, then prove green. Also when a fix 'should work' but nothing proves it, or when adding a guard." Body: name the observable (link wired-to-nothing); where the test goes (`app/tests/DnaEntropyGraph.<Proj>.Tests/<Class>Tests.cs` or `worker/tests/test_<module>.py`; ViewModel tests use `FakeGcp` + `FakeTimeProvider`, never a UI thread); run and paste red; neighbours (empty contig, `N`-bearing GenBank, multi-record, `L < 2K`, `L > W`, a cloud error of every class from the fake); code; green + the guards project; report both outputs verbatim. Non-negotiables: no code before red; a guard gets a planted violation + vacuity assertion; a mutation arm that stays green is a hole; never a GPU test on the laptop.

**`wired-to-nothing`**: description "Use before reporting ANY feature, fix or wiring change as done. Code that compiles, runs, passes tests and does nothing is the inherited house bug, and no test has ever caught one. Also when reviewing a change that adds a binding, command, DI registration, startup-script step, bucket rule, label or setting." Body: the one rule (name the observable, check it), then the shape table:

| You added/changed | The check that catches it |
|---|---|
| A XAML binding | `x:Bind` to a missing property is a *compile* error, `{Binding}` is not: `Select-String -Pattern '\{Binding ' app/src -Recurse` must return zero rows outside `DataTemplate`s that need it. Then run the page in Debug with `DebugSettings.BindingFailed` wired to throw, and open the page |
| An `ICommand` / `[RelayCommand]` | Grep the XAML for `Command="{x:Bind <Name>Command}"`. Then click it and watch the ViewModel state change |
| A DI service | `Guards.Tests/DiResolutionTests` builds the real `ServiceCollection` and resolves every `*ViewModel` and every `I*Service`. A service registered but never injected: grep constructors for the interface name |
| A startup-script step | `bash -n` and `shellcheck`, then on the CPU walking-skeleton VM read `progress.jsonl` for the step's own progress line. A step with no progress line is invisible and must get one |
| A bucket lifecycle rule / IAM binding | Read it back after `Apply`; on the real project once via `CloudCli bucket describe`. "Applied" is not "present" |
| A label | After creating anything, `CloudCli resources list --install <id>` must show it. If it does not, the leak-detector cannot see it either |
| A setting | Grep for the *read* side outside `SettingsViewModel`. Then change it and observe the behaviour differ |
| A `CloudError` class | The classifier test has a recorded payload *and* the ViewModel maps it to a `.resw` key *and* the key names an action. Three greps |
| A worker output file | Read the file back with a real reader (Biopython for `.gb`, a bedGraph parser, igv.js loading it in the viewer), never the writer's return value |
| A `JobPhase` transition | The state-machine test table has a row; the UI state table in `ui_conventions.md` has a row; the history row records it |
| A direction/windowing change | The combined track differs from Forward-only exactly in the first `K` bases on a synthetic input; the seam is in provenance |

**`working-an-issue`**: description "Use before starting OR closing any GitHub issue on Raaif-Yousuf/DNA-Entropy-Graph. Also when an issue lacks a 'Done when', when triaging, or when an issue is an epic rather than a task." Body: `python scripts/issue_precheck.py <n>` and `gh issue view <n> --comments`, newest comment first; does the body have the acceptance blocks (section 6)? If not, writing them is the first deliverable; epics use sub-issues, never a checklist in the body, close the epic only when every child is closed; branch `feat/<n>-<slug>` or `fix/<n>-<slug>`; commits `type(scope): subject (#n)`; before closing: commit sha, criteria, the observable, and the ToTest row if unproven; `gh issue close <n> --reason completed --comment "$(Get-Content body.md -Raw)"`.

**`fixing-a-bug`** (+ `bug-shapes.md`): description "Use when fixing any bug, regression or defect from an issue, a diagnostics zip or a field report, before writing fix code. Also when a fix 'should work' but the symptom persists, or when a green suite hides a wrong behaviour." Body: the eight steps from CLAIR (symptom test first, watch it fail, matrix, mutation-check, narrow, fix, revert-check, green + guards); "the diagnostics zip is the field report": `scripts/triage_diagnostics.py <zip>` prints app version, worker version, image digest, last `JobPhase`s, cloud error classes, the last 50 progress lines; read it before screenshots. `bug-shapes.md`: hand-maintained denominator (output catalog vs writers vs resw keys), the gate you ran vs the gate that ships (Debug vs self-contained publish; laptop vs VM), a proxy for the predicate (UI state from a timer instead of `JobPhase`), a message reporting intent not outcome, copied posture (a retry policy copied from the stockout path onto the billing path), a platform branch that returns a value indistinguishable from success (an empty label list read as "no resources" rather than "could not list").

**`orchestrating-agents`**: description "Use when dispatching work to subagents, writing a brief, deciding how many to run, or merging an agent's branch. Also when an agent reports done, goes quiet, or when choosing reuse vs spawn." Body: precheck every lane in one call; reuse over spawn; the brief names the skills BY NAME; "targeted tests only"; "no GPU tests, no cloud creates unless the brief says so and names the budget"; worktree path + branch; the changelog fragment path; the one observable; how to verify a report (read the red and green outputs, not the summary); the merge by the coordinator alone. Bans: `git stash`, recursive delete in-repo, closing issues, publishing releases, creating cloud resources outside `cloud_gpu_test.ps1`.

**`working-on-gcp`** (new; modelled on CLAIR's working-on-macos + gce_wave_brief): description "Use before creating, inspecting or debugging ANY Google Cloud resource for this project, before running scripts/cloud_gpu_test.ps1, and before writing a brief for an agent that will. Also when a cloud run looks stuck, when 'no capacity' appears, when a VM will not go away, or when deciding whether a finding is cloud-specific." Body: the owner's dev project id, default zones, the dev bucket, budget alert, GPU quota as MEASURED `<date>`; every VM carries labels + `maxRunDuration` + `instanceTerminationAction` (the hook enforces it) and stops/deletes itself via the API; budget arithmetic (L4 ~$0.85/h, A100 ~$3.70/h, 150 GB pd-balanced ~$15/month while it exists); preflight order and why "no capacity everywhere" means a misconfigured project; the measured ways a run wastes itself (disk-full reads as a crash; exit 0 is not a pass; a tool call caps at ~600 s so launch detached and poll; upload progress while running; a transient quota refusal gets one retry after the leak scan; the VM runs the released image digest); health = heartbeat, not status; end state: `CloudCli resources list --project <id>` is **empty** or you say exactly what you left and until when; what to bring home (research note with machine type, wall clock, exact command, headline numbers; one issue per distinct defect).

**`winui-dev`** (new): description "Use before building, running, testing or debugging the WinUI 3 app in app/, before adding a page, ViewModel, binding, dialog, setting or DI registration, and when a XAML change compiles but shows nothing." Body: commands (`dotnet build app/DnaEntropyGraph.sln -c Debug -p:Platform=x64`; `scripts/dev_app.ps1` sets `DEG_FAKE_CLOUD=1`; `dotnet test app/DnaEntropyGraph.sln`; guards ~5 s); where things go (view, ViewModel, registration in `App.xaml.cs::ConfigureServices`, strings via `x:Uid`, nothing in code-behind); `x:Bind` pitfalls (`Mode=OneTime` default, compile-time typed, `{Binding}` only inside `DataTemplate`s with a comment); `[ObservableProperty]` rules; threading via `IDispatcher`; dialogs via `IDialogService` with `XamlRoot`; WebView2 viewer (`EnsureCoreWebView2Async`, virtual host mapping, blank viewer = check DevTools console first); theme read at startup (the saved-never-read shape); packaging (unpackaged self-contained; `vpk pack` only in `release.yml`).

### Agent `.claude/agents/cold-diff-reviewer.md`
Frontmatter `name`, `description` ("Independent, framing-free review of a C#/WinUI/Python diff. Use when a change needs a second opinion not primed by the author's reasoning, the issue framing, or the orchestrator's theory of the fix."), `tools: Read, Grep, Glob`, `model: sonnet`. Body: only the diff and paths; check for this repo's recorded classes: wired to nothing (binding/command/DI/startup step/label/setting shapes); a timeout that cannot fire; a vacuous assertion; a duplicated roster; a raised baseline standing in for a fix; a cloud call outside the interface (`using Google` anywhere but `Cloud`); a resource created without labels/maxRunDuration; a user-facing string inline, with an em dash, or without an action; a reverse pass that is not a reverse complement. Report the one observable per finding.

### Memory seeds (`.claude/memory/`, one lesson per file)
`evo2-returns-a-nested-tuple`, `tokenizer-ids-are-uint8`, `evo2-tokenizer-adds-no-bos`, `one-forward-pass-per-window`, `reverse-means-reverse-complement`, `cp1252-console-crashes-on-glyphs`, `flash-attn-has-no-cu12-wheel-for-torch-2-9`, `billing-off-looks-like-stockout`, `quota-is-per-region-and-fixable-stockout-is-not`, `running-is-not-working`, `termination-action-does-not-fire-on-guest-shutdown`, `the-vm-runs-released-bytes`, `a-tool-call-caps-at-600s`, `exit-0-is-not-a-pass`, `wired-to-nothing-is-the-house-bug`, `a-check-that-cannot-fail`, `closing-on-the-route-not-the-observable`, `a-hand-maintained-denominator`, `a-ban-broken-five-times-needs-a-hook`, `msys2-python-on-path`, `dev-laptop-has-no-cuda`, `no-shared-singleton-cloud-resources`, `the-signing-step-silently-no-ops-without-the-secret`, `fluentassertions-8-is-commercial`, `geneious-imports-gff3-not-wig`, `igv-web-cannot-build-a-genome-from-genbank`, `evo2-1b-needs-hopper-too`.

---

## 5. GitHub workflow design

> Superseded for what has shipped: `.github/labels.yml`, the issue/PR templates, `ci-app.yml`, `ci-docs.yml`, and `ci-worker.yml` (now with a `scripts-tests` job this draft did not anticipate) all exist as real files. `release.yml` and `cloud-canary.yml` do not exist yet; this section remains the only source for those two until they are built. The `needs-criteria` label exists and is applied per this section's own rule (this session's own backlog audit checked it against every open issue).

### Labels (`.github/labels.yml`, applied by `scripts/sync_labels.ps1`)

| Label | Colour | Meaning |
|---|---|---|
| `P1` | `#B60205` | Blocks the current milestone or loses data/money |
| `P2` | `#D93F0B` | Should ship in the current milestone |
| `P3` | `#FBCA04` | Nice to have, any milestone |
| `area:app` | `#1D76DB` | C# app, ViewModels, Core, Persistence |
| `area:worker` | `#0E8A16` | Python package, science, writers, container |
| `area:cloud` | `#5319E7` | GCP calls, preflight, VM lifecycle, startup script |
| `area:viewer` | `#006B75` | igv.js / WebView2 / ScottPlot |
| `area:local-engine` | `#0052CC` | local NVIDIA GPU execution |
| `area:docs` | `#C5DEF5` | docs/, user guide, CLAUDE.md, .claude |
| `area:packaging` | `#BFD4F2` | installer, signing, updates, CI, release, images |
| `area:ux` | `#F9D0C4` | copy, flows, theme, accessibility |
| `DECISION` | `#000000` | Owner call required; the body is a decision record |
| `post-v1` | `#EEEEEE` | Explicitly not before v1.0 |
| `good-first-issue` | `#7057FF` | Bounded, no cloud money, has a test to copy |
| `epic` | `#3E4B9E` | A parent with sub-issues; never worked directly |
| `wired-to-nothing` | `#E99695` | Filed as this shape; feeds the skill's count |
| `needs-criteria` | `#FEF2C0` | Body lacks Done-when / Observable; not to be worked |

### Milestones
See spec section 8.

### Issue templates (`.github/ISSUE_TEMPLATE/`)
`config.yml`: `blank_issues_enabled: false`, contact link to the user guide "when something goes wrong" page.
`feature.yml` fields: Area dropdown; Milestone dropdown; Why; **Done when** (required); **Observable that proves it is wired** (required); Docs touched (checkboxes); Tests; Out of scope; Cloud money.
`bug.yml`: What happened / expected; App version; Worker image digest (from the run's details); Diagnostics zip ("Settings > Diagnostics > Save diagnostics, then drag the zip here. It contains no sequence data and no file names."); Screenshots; Which step (plain-word `JobPhase` names); Cloud error shown; Done when (pre-filled).
`decision.yml`: Question; What is true today (MEASURED); Options table; Recommendation; What is blocked on this; auto-label `DECISION`.

### PR template
```markdown
Closes #<n>

## What changed
## Done when (copied from the issue, ticked)
- [ ] ...
## The observable that proves it is wired, and what I saw
## Tests (red output, then green output, verbatim)
## Docs touched
- [ ] `docs/changelog.d/<branch>.md` written, starts with `- `
- [ ] relevant `docs/*.md` / `docs/user_guide/` updated
- [ ] `CLAUDE.md` (only if a rule, the stack or a pitfall changed)
## ToTest
- [ ] Not needed: proven on a real build / VM, evidence above
- [ ] Row added to `docs/ToTest.md` (Needs: app-dev | installer | gpu-vm | cpu-vm | two-accounts | local-gpu)
## Cloud money
- [ ] This PR created no cloud resources, or: <what, how long, cost>, and `CloudCli resources list` was empty at the end
```

### Branch and commit conventions
- `main` is protected. Required checks: `ci-app`, `ci-worker`, `ci-docs`. Squash merge, PR title becomes the commit subject.
- Branches: `feat/<issue>-<slug>`, `fix/<issue>-<slug>`, `docs/<issue>-<slug>`, `chore/<slug>`, `release/v<x.y.z>`.
- Commits: Conventional Commits, scope is an area label without the prefix, issue in the subject: `feat(cloud): classify billing-disabled before quota (#42)`. Types: `feat fix docs test chore refactor perf build ci release`.
- Version: `Directory.Build.props` `<Version>` and `worker/pyproject.toml` `version` are the same string; the tag is `v<version>`; `check_version_lockstep.py` fails CI otherwise. Pre-releases `0.3.0-t.1` sort below `0.3.0`.

### CI workflows
`ci-app.yml` (windows-latest, PR + push to main, paths `app/**`): setup-dotnet from `global.json`; restore; `dotnet format --verify-no-changes`; `dotnet build -c Release -p:Platform=x64 -warnaserror`; `dotnet test` (trx + coverage; no UI tests); `dotnet publish` self-contained; `vpk pack` -> unsigned artifact (7-day retention).
`ci-worker.yml` (ubuntu-latest, paths `worker/**`): `astral-sh/setup-uv`; `uv venv`; `uv pip install -e "worker[dev,worker]"`; `ruff check` and `ruff format --check`; `pytest -m "not gpu"`; `uv build`; `gen_manifest_schema.py --check`; `shellcheck worker/vm/startup.sh`; `docker build -f Dockerfile.cpu` smoke (`dna-entropy-worker selftest`).
`ci-docs.yml` (ubuntu-latest, every PR): `check_docs_index.py`, `check_changelog_fragments.py`, `check_totest_format.py`, `check_third_party_notices.py`, `check_version_lockstep.py`; em-dash scan over `docs/user_guide/**`, `*.resw`, `.github/ISSUE_TEMPLATE/**`, `README.md`; `AGENTS.md` < 20 lines; markdown link check (lychee, offline for local links).
`release.yml` (tag `v*`; `workflow_dispatch` with a tag input): **assert secrets exist** (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE`, `GOOGLE_OAUTH_CLIENT_SECRET`), fail loudly if any is empty; lockstep check; job `images` (ubuntu): build and push `-cuda` and `-cpu` to GHCR, output digests; job `worker` (ubuntu): sdist + wheel + `SHA256SUMS`; job `app` (windows): build with the digests baked into `gpu-catalog.json`, test, publish, `azure/trusted-signing-action` over `publish/**/*.exe|*.dll`, `vpk pack` signed, `vpk upload github ... --merge` as **draft**; job `notes`: `compile_sprint_log.py --dry-run --since-tag <prev>` renders release notes; attach wheel, `SHA256SUMS`, image digests; publish only after every asset is attached; post-release job opens a ToTest reminder issue "Verify v<version> on a clean VM".
`cloud-canary.yml` (nightly, owner's project, ~$0.02): the CPU smoke job through the real `CloudJobRunner` via `CloudCli`; asserts `done`, output hashes, and `instances.aggregatedList` empty afterwards.

### ToTest queue rules (paste into `docs/ToTest.md` header)
1. Add a row when you close an issue whose behaviour you could not prove on the thing it ships on (installer build, GPU VM, second account, local GPU). "Tests pass" is never that proof.
2. A row names: issue, closing commit sha (validated), what a human needs (`Needs`), the exact action, what passing looks like, what a **false** pass looks like.
3. Delete the row on verification. Reopen the issue with the measured evidence on failure, then delete the row.
4. Group by `Needs`; drain by group, because the cost is setup.
5. `release_runbook.md` drains every `installer` row as part of every release; a release with `installer` rows left is not a release.
6. `check_totest_format.py --max-age-days 45` fails CI on a row older than 45 days so the queue cannot become a backlog.

### changelog.d compile
`scripts/compile_sprint_log.py`: fragments `docs/changelog.d/<branch-with-dashes>.md`, must start with `- `, prepended newest-first under `## Recent changes` in `docs/sprint_log.md`, deleted, committed by the merger. `--since-tag v0.2.0` renders release notes without deleting; `--check` validates fragment shape only.

---

## 6. Issue title conventions, epic structure, body template

### Titles
`<area>: <imperative verb phrase>` with the area matching the label: `cloud: classify billing-disabled before quota`, `app: Cloud page lists every labelled VM with cost per hour`, `worker: run loop uploads progress while running`, `viewer: load the run folder through a virtual host`, `docs: user guide page for cloud costs`, `packaging: release.yml asserts signing secrets exist`. Epics: `EPIC <area>: <noun phrase>`. Decisions: `DECISION: <question>?`. Bugs: `<area>: <what happens> when <condition>` (a symptom, not a theory).

### Epic structure
One epic per area; children are GitHub sub-issues. The epic body is a one-paragraph "Why" plus the sub-issue list only. The spec's section 8 (milestones) and the issue list opened at project start define the initial children.

### The issue body template (exact markdown; `scripts/new_issue.ps1` renders it)

```markdown
## Why
<One paragraph. The user-visible or money-visible problem, or the capability. If a cause is
claimed, it carries `MEASURED <date>:` or `THEORY (unverified):`.>

## Done when
<Falsifiable. A person runs ONE command or opens ONE screen and says yes or no.>
- [ ] ...

## Observable that proves it is wired
<The single thing that differs between "this works" and "this compiles, passes tests and does
nothing". Name where to look: a screen, a bucket object, a history row, a log line, a
`CloudCli` output.>

## Docs touched
- [ ] `docs/<file>.md` section <n>
- [ ] `docs/user_guide/<page>.md` (if the user can see the change)
- [ ] `CLAUDE.md` (only if a rule, the stack or a pitfall changes)
- [ ] `docs/changelog.d/<branch>.md`

## Tests
- `app/tests/DnaEntropyGraph.<Proj>.Tests/<Class>Tests.cs`: <what it asserts>
- `worker/tests/test_<module>.py`: <what it asserts>
- ToTest row needed? <no, because ... | yes: Needs = app-dev | installer | gpu-vm | cpu-vm | two-accounts | local-gpu>

## Out of scope
<What this issue deliberately does not do, with the issue number that does.>

## Cloud money
<none | machine type, expected minutes, expected cost, and who pays (owner's dev project)>
```

Rules: an issue without a filled "Done when" and "Observable" block is labelled `needs-criteria` and is not worked; an epic body is a one-paragraph "Why" plus the sub-issue list only; a `DECISION` issue uses the decision-record skeleton instead.

---

## 7. What is deliberately not carried from the prototypes
- The keeper/client split, `keep_gpu.py`, `keep-gpu.exe`, `dna-entropy-box`, the forever-retry loop as a *user-facing* process. The zone ladder and error classifier survive as C# logic behind `IComputeGateway`.
- `cloud/gcloud.py`, `orchestrator.py`, `keeper.py`, `ui.py`, the SSH/scp path, `%APPDATA%\dna-entropy\config.json`.
- PyInstaller `.exe`s, `packaging/launcher.py`, the double-click wizard (the app is the wizard).
- `ROADMAP.md` checkboxes and "work sprint by sprint", replaced by milestones and Issues.
- `temp.md` and the machine-local `settings.local.json` allowlists.

## 8. Sequencing for the first sessions
1. Repo bootstrap: CLAUDE.md, AGENTS.md, `.gitignore`, `.editorconfig`, `docs/README.md`, `docs/hard_rules.md`, `docs/ToTest.md`, `docs/changelog.d/README.md`, `.claude/` (settings, skills, agent, memory seeds), `scripts/hooks/`, `scripts/compile_sprint_log.py`, `scripts/issue_precheck.py`, `.github/` (templates, labels, workflows).
2. `worker/` port with tests green on the laptop (`pytest -m "not gpu"`), then the `worker/` subpackage against `LocalBlobstore`, then `Dockerfile.cpu` and the container smoke.
3. `app/` skeleton with `FakeGcp` and the Guards project; `CloudJobRunner` tests; the walking skeleton against the fake.
4. `DnaEntropyGraph.Cloud` real implementations; the first real CPU VM run in the owner's project via `CloudCli`; `startup.sh` self-stop proven (`resources list` empty).
5. v0.1 tagged as `0.1.0-t.1` through `release.yml` with signing disabled by an explicit `-p:SkipSigning=true` that prints a red banner (never a silent no-op).
