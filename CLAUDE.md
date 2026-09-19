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

**Repo:** `%USERPROFILE%\DNA-Entropy-Graph` on the owner's PC | **Tracker:** GitHub Issues on
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

### The process (inherited discipline, carried from the prototype work)

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
| Signing | Azure Trusted Signing in `release.yml` | The step **silently no-ops** if the secret is unset, a known Azure Trusted Signing footgun. The workflow asserts every secret exists before building |
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

**`instanceTerminationAction` does not fire on guest shutdown.** MEASURED against Compute Engine's own semantics: it fires when *Compute Engine* stops the VM (max-run-duration expiry), not when the
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

**"Wired to nothing" is the recurring bug class this project already knows to watch for, with new shapes here.** A XAML
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

*Last updated: 2026-09-19 | v0.1 not yet cut.*
