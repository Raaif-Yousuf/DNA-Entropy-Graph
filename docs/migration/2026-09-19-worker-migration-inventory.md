# Worker migration inventory

**Date:** 2026-09-19
**Source repos:**
- `C:\Users\raaif\DNA-Entropy-Genbank` (primary; current prototype, 127 tests) at commit `8026bf5c4afbe3021c1f7e79a17a93af4eaad84b`
- `C:\Users\raaif\DNA-Entropy` (older prototype; ephemeral-VM reference only) at commit `c0f2375314a7d96be89c84d775c99110579a0582`

**What was copied where** (verbatim, no source edits; see `worker/.gitignore`, carried over unchanged, for what stays untracked):

| From (`DNA-Entropy-Genbank`) | To (`DNA-Entropy-Graph`) |
|---|---|
| `src/` | `worker/src/` |
| `tests/` | `worker/tests/` |
| `scripts/` | `worker/scripts/` |
| `packaging/` | `worker/packaging/` |
| `pyproject.toml` | `worker/pyproject.toml` |
| `README.md` | `worker/README.legacy.md` |
| `LICENSE` | `worker/LICENSE.prototype` |
| `.gitignore` | `worker/.gitignore` |
| `keep_gpu.py` | `worker/keep_gpu.py` |
| `dna-entropy.spec` | `worker/dna-entropy.spec` |
| `keep-gpu.spec` | `worker/keep-gpu.spec` |
| `CLAUDE.md` | `worker/CLAUDE.legacy.md` |
| `docs/` | `worker/docs-legacy/` |

| From (`DNA-Entropy`, older) | To (`DNA-Entropy-Graph`) |
|---|---|
| `src/dna_entropy/cloud/orchestrator.py` | `worker/legacy/dna-entropy-v1/orchestrator.py` |
| `src/dna_entropy/cli.py` | `worker/legacy/dna-entropy-v1/cli.py` |
| (new) | `worker/legacy/dna-entropy-v1/README.md` — provenance note (repo URL + commit hash, why kept) |

**File counts copied:**

| Folder | Files |
|---|---|
| `worker/src/` (36 `.py` under `dna_entropy/`, in 8 subpackages + top level) | 37 (incl. `__init__.py` files; matches the `robocopy` "Files: 37" report before build artifacts were cleaned) |
| `worker/tests/` (14 test modules + `conftest.py` + 4 data fixtures) | 19 |
| `worker/scripts/` | 7 |
| `worker/packaging/` | 2 |
| `worker/docs-legacy/` | 4 |
| `worker/legacy/dna-entropy-v1/` | 3 (2 copied + 1 new README) |
| `worker/` top level (`.gitignore`, `CLAUDE.legacy.md`, `dna-entropy.spec`, `keep-gpu.spec`, `keep_gpu.py`, `LICENSE.prototype`, `pyproject.toml`, `README.legacy.md`) | 8 |

**What was deliberately not copied, and why:**
- `dna-entropy.exe`, `keep-gpu.exe` (both repos) — built binaries; excluded per the "never copy `*.exe`" rule and because the C# app replaces both executables (D2/D6 in the design spec).
- `build/`, `dist/`, `out/` (both repos) — PyInstaller/output scratch directories; transient, already gitignored in the prototype.
- `.venv/`, `.pytest_cache/`, `__pycache__/`, `*.pyc`, `dna_entropy.egg-info/` — interpreter/build caches; regenerated locally by `uv pip install -e` and `pytest` during this migration's own test run and removed again afterward. Never sourced from the source repos.
- `.git/`, `.claude/` (both repos) — the source repos' own VCS/agent state; irrelevant to the new repo and explicitly excluded by the task.
- `temp.md` (Genbank repo root) — scratch planning note, explicitly excluded.
- Everything else in the older `DNA-Entropy` repo besides `cloud/orchestrator.py` and `cli.py` — the Genbank prototype is a strict superset (per `FEATURES.md` §16) of everything else in the older repo (readers, validation, predictors, writers, tests), so only the one older-repo-only feature (the ephemeral per-run VM lifecycle in `orchestrator.py`/`cli.py`, later removed from Genbank in favor of the keeper/client split) was worth a separate copy.

---

## Copied source files

Verdict legend: **KEEP** = ships as-is in the worker package · **ADAPT** = kept but must change · **GUT** = delete, superseded by the new design · **REFERENCE** = kept only as legacy reference under `worker/legacy` or `worker/docs-legacy`, not shipped.

### `worker/src/dna_entropy/` — core package

| Path (in new repo) | Purpose | Verdict | Why | Target location or action |
|---|---|---|---|---|
| `worker/src/dna_entropy/__init__.py` | Package docstring + `__version__ = "0.0.1"`. | KEEP | Version marker; the design keeps app/worker/tag in lockstep (§7 Conventions). | Stays; version string bumped at release time. |
| `worker/src/dna_entropy/config.py` | `RunConfig` dataclass, `PredictorKind`/`TrackFormat` enums, `DEFAULT_MAX_LEN=8192`. | ADAPT | Needs new fields for context length `K`, direction, window/stride (derived), and a progress-callback hook per §5.5/§5.6; CLI-flag-shaped fields (`out_dir`, `informat`) become manifest fields instead. | Stays in `worker/src/dna_entropy/config.py`; extended, not replaced. |
| `worker/src/dna_entropy/pipeline.py` | Orchestrates input -> validate -> predict -> analyze -> export; the only module that knows stage order; builds output sets for GenBank vs FASTA/paste. | ADAPT | Needs a progress callback (stage list from §4.4: checking, analysing per contig/window, saving), and must call the new windowing/direction logic per contig instead of one bare `predictor.predict()` call. | Stays; extended by the new `worker/run` module that wraps it with manifest I/O. |
| `worker/src/dna_entropy/cli.py` | Typer CLI: `version`, `run`, `validate`, `cloudrun`, `keep-gpu`; `_try_local_evo` for `--prefer-local`. | ADAPT | `cloudrun`/`keep-gpu`/`--prefer-local` and their gcloud/SSH plumbing are gone under the new design (direct Google APIs from C#, no gcloud); `run`/`validate` stay as the basis for the worker's manifest-driven entrypoint. | Drop `cloudrun`, `keep-gpu`, `_try_local_evo`; keep `run`/`validate` shape as the seed for the new worker CLI entrypoint. |
| `worker/src/dna_entropy/analysis/__init__.py` | Re-exports entropy helpers. | KEEP | Trivial package init. | Stays; will also export the new `windowing`/`direction` modules. |
| `worker/src/dna_entropy/analysis/entropy.py` | `shannon_entropy()` (per-position bits, `0*log2(0)=0` convention, clipped to `[0,2]`) and `summarize()`. | KEEP | Scientific core; unchanged by the design (§5.6 builds windowing *around* this, not instead of it). | Stays. |
| `worker/src/dna_entropy/annotators/__init__.py` | Re-exports `Annotator`, `AnnotatorError`, `GeneFeature`, `ProdigalAnnotator`. | KEEP | Trivial package init. | Stays. |
| `worker/src/dna_entropy/annotators/base.py` | `Annotator` Protocol, `GeneFeature` dataclass (1-based inclusive), `AnnotatorError`. | KEEP | Contract unaffected by the C#/cloud rework. | Stays. |
| `worker/src/dna_entropy/annotators/prodigal.py` | `ProdigalAnnotator`: lazy Pyrodigal import, meta mode, sequential `gene_N` ids. | KEEP | Gene calling logic is unchanged; still optional/best-effort per design. | Stays. |
| `worker/src/dna_entropy/cloud/__init__.py` | Re-exports gcloud/orchestrator/ui symbols. | GUT | The whole `cloud/` package shells out to the user's `gcloud`/SSH, which the approved design forbids (§3: "Direct Google APIs from C#. No gcloud, no SSH."); the C# `DnaEntropyGraph.Cloud` project replaces it entirely. | Not shipped in the worker container; retained untouched for reference (see below). |
| `worker/src/dna_entropy/cloud/gcloud.py` | Thin `gcloud` CLI wrappers: account/project, VM create/find/start/stop/delete, SSH/SCP, error classification, billing/API/quota checks. | GUT | Superseded by C# Compute/Storage/Billing/Quotas API gateways under `DnaEntropyGraph.Cloud` (design §3.1, §5.2). | GUT from the shipped package; **REFERENCE** for its error-classification buckets, quota metric names, and health-check logic — see "Prototype behaviours" below. |
| `worker/src/dna_entropy/cloud/keeper.py` | Always-on GPU keeper: acquire/start/watchdog loop, GCP setup health report, region quota pre-filter, GPU health probe (`nvidia-smi`). | GUT | The always-on singleton VM (`dna-entropy-box`) is explicitly removed by the design (D1: "Per-job VM, found by label, never by fixed name... no shared singleton"). | GUT from the shipped package; **REFERENCE** for the setup health check, quota pre-filter, and watchdog backoff logic — see "Prototype behaviours" below. |
| `worker/src/dna_entropy/cloud/orchestrator.py` | Zone/offer escalation (`_create_box`, `_try_sequential`/`_try_parallel`), VM setup script, `run_in_cloud` (find/wake/upload/run/download, never creates/deletes), constants (`BOX_NAME`, `OFFERS`, `DEFAULT_ZONES`, `BASE_IMAGE_FAMILY`). | GUT | Zone-ladder + escalation logic is valuable but must be reimplemented in C# against the Compute API directly (§5.4); the box-reuse-by-fixed-name model is replaced by per-job VMs found by label. | GUT from the shipped package; **REFERENCE** for the zone ladder, GPU offer table, DLVM image constants, and VM setup script shape — see "Prototype behaviours" below. |
| `worker/src/dna_entropy/cloud/ui.py` | ASCII `Spinner` context manager for CLI progress. | GUT | Console-only UX; the WinUI app has its own run-progress screen (§4.4) and the worker reports progress via `status.json`/`progress.jsonl`, not stdout spinners. | Not needed; no reference value beyond illustrating "never crash on a UI helper" pattern. |
| `worker/src/dna_entropy/predictors/__init__.py` | Re-exports Predictor contract + `MockPredictor` (not `EvoPredictor`, kept lazy). | KEEP | Contract package init; the lazy-import discipline for `EvoPredictor` is exactly the "only evo.py imports torch" rule the design repeats (design body, "only predictors/evo.py may import torch"). | Stays. |
| `worker/src/dna_entropy/predictors/base.py` | `Predictor` Protocol, `NUCLEOTIDES`, `check_probability_matrix()` contract guard. | KEEP | The single swap point; untouched by the cloud/UI rework. | Stays. |
| `worker/src/dna_entropy/predictors/evo.py` | `EvoPredictor`: Evo 2 wrapper, tokenizer-derived nucleotide ids, robust logits extraction, bf16, only module allowed to import `torch`/`evo2`. | ADAPT | Needs a `max_context` that comes from the manifest's window size `W` (not a fixed 8192), and a `MODEL_NEEDS_HOPPER` gate for `evo2_40b`/`evo2_1b_base` (design D11/D16, §5.5). | Stays in place; extended, not replaced. |
| `worker/src/dna_entropy/predictors/logits.py` | `aligned_acgt_probs()`: torch-free softmax + autoregressive position-shift alignment. | KEEP | Pure NumPy, torch-free, unit-tested everywhere; feeds directly into the new windowing/direction combination without modification. | Stays. |
| `worker/src/dna_entropy/predictors/mock.py` | `MockPredictor`: deterministic `(L,4)` probabilities seeded by `seed + crc32(seq)`. | KEEP | GPU-free stand-in used throughout local/CI testing; unaffected by the cloud rework. | Stays. |
| `worker/src/dna_entropy/readers/__init__.py` | Re-exports `Reader`, `PasteReader`. | KEEP | Trivial package init. | Stays. |
| `worker/src/dna_entropy/readers/base.py` | `Reader` Protocol. | KEEP | Contract unaffected. | Stays. |
| `worker/src/dna_entropy/readers/detect.py` | `detect_kind()`: extension-first, content-sniff fallback for GenBank/FASTA/paste. | KEEP | Format detection logic is unchanged by the design. | Stays. |
| `worker/src/dna_entropy/readers/fasta.py` | `read_fasta()`: hand-rolled parser, **analyzes only the first record** of a multi-record FASTA. | ADAPT | Design D14: "Multi-record FASTA processes all records (prototype was first only)" — must return every record, not just the first. | Stays; rewritten to yield all records like `readers/genbank.py` already does. |
| `worker/src/dna_entropy/readers/genbank.py` | `read_genbank()`: Biopython-based, all records, existing `gene`/`CDS` features mapped to `GeneFeature`, never re-annotates. | KEEP | Already matches the "process all records" behavior FASTA needs to adopt; unaffected by the cloud rework. | Stays. |
| `worker/src/dna_entropy/readers/input.py` | `load_input()`/`LoadedInput`/`Contig`: unified routing + output-safe contig naming. | ADAPT | Needs to route every FASTA record into its own `Contig` once `readers/fasta.py` returns all records (companion change to D14). | Stays; minor extension. |
| `worker/src/dna_entropy/readers/paste.py` | `PasteReader`: reads a file path or stdin, no cleaning. | KEEP | Still useful for the worker's manifest-supplied plain-text input path. | Stays. |
| `worker/src/dna_entropy/validation/__init__.py` | Re-exports validation symbols. | KEEP | Trivial package init. | Stays. |
| `worker/src/dna_entropy/validation/validators.py` | `validate_sequence()`: normalization, RNA handling, IUPAC ambiguity, length caps, notices. | KEEP | Validation rules are unchanged; §5.6 pushback rules (`K` warn/refuse thresholds) are *additional* checks layered on top, not a replacement. | Stays; new context-length pushback checks are added alongside, not instead. |
| `worker/src/dna_entropy/writers/__init__.py` | Re-exports writer classes. | ADAPT | Must also export the new `TsvWriter` once it exists (§5.5: "New `TsvWriter`"). | Stays; add the new writer's export. |
| `worker/src/dna_entropy/writers/base.py` | `Writer` Protocol, `write_text_lf()` (UTF-8 + LF). | KEEP | Shared writer contract untouched. | Stays. |
| `worker/src/dna_entropy/writers/bedgraph.py` | `BedGraphWriter`: 0-based half-open bedGraph track, `write`/`write_multi`. | KEEP | Output format unchanged. | Stays. |
| `worker/src/dna_entropy/writers/fasta.py` | `FastaWriter`: 60-column wrapped FASTA, `write`/`write_multi`. | KEEP | Output format unchanged. | Stays. |
| `worker/src/dna_entropy/writers/genbank.py` | `GenBankWriter`: per-gene mean-entropy `/note`, multi-record support. | KEEP | Output format unchanged. | Stays. |
| `worker/src/dna_entropy/writers/geneious.py` | `GeneiousWriter`: per-position entropy as a GFF3 heatmap track. | KEEP | Output format unchanged. | Stays. |
| `worker/src/dna_entropy/writers/gff.py` | `GffWriter`: gene-boundary GFF3, percent-encoding of reserved characters. | KEEP | Output format unchanged. | Stays. |
| `worker/src/dna_entropy/writers/summary.py` | `SummaryWriter`: single- and multi-record text summaries. | KEEP | Output format unchanged; may gain a seam-position/provenance line for §5.6's bidirectional combination, but that is additive. | Stays. |
| `worker/src/dna_entropy/writers/wig.py` | `WigWriter`: fixedStep WIG track. | KEEP | Output format unchanged. | Stays. |

### `worker/tests/`

| Path (in new repo) | Purpose | Verdict | Why | Target location or action |
|---|---|---|---|---|
| `worker/tests/conftest.py` | Shared pytest fixtures (currently minimal). | KEEP | Generic test scaffolding. | Stays; will grow fixtures for manifest/blobstore tests. |
| `worker/tests/data/multi.gb`, `sample.fasta`, `sample.gb`, `prokaryotic_demo.fasta` | Fixture sequences for reader/writer/pipeline tests. | KEEP | Test data is format-agnostic and reusable by the worker's new tests (windowing, direction, TSV writer). | Stays. |
| `worker/tests/test_annotator.py` | Real Pyrodigal gene-calling tests. | KEEP | Annotator contract unchanged. | Stays. |
| `worker/tests/test_cloud.py` | 38 tests: `classify_create_error` buckets, `gpu_quota_metric`, `list_region_gpu_quota` CSV parsing, `_create_box` setup-error short-circuit/quota-filtering, `Spinner`, `preflight`, state roundtrip, `run_in_cloud` "never creates a box" guard. | GUT | Tests the gutted `cloud/gcloud.py`/`cloud/orchestrator.py` modules directly (gcloud subprocess wrappers, VM zone escalation) which are not shipped. | Delete from the default test run. **Re-express as C# fixtures**: every `test_classify_create_error` case (billing/api_disabled/quota/stockout/permission/network/other stderr samples) and every `test_list_region_gpu_quota_parses_available`/`_returns_none_on_failure`/`_empty_metrics_is_empty` case should become `tests/contract-fixtures/` JSON vectors for `DnaEntropyGraph.Core.ErrorCatalog`/`GpuPlanner` xUnit tests. |
| `worker/tests/test_entropy.py` | 7 tests: uniform -> 2.0 bits, one-hot -> 0.0, range, `0*log0` convention. | KEEP | Scientific core unchanged. | Stays. |
| `worker/tests/test_evo_logits.py` | 5 tests: softmax, position shift, uniform row 0, single-base edge case. | KEEP | Torch-free alignment math unchanged. | Stays. |
| `worker/tests/test_evo_predictor.py` | 3 GPU-marked tests: real Evo 2 contract on a CUDA box. | ADAPT | Needs updating once `EvoPredictor` takes a manifest-derived `max_context`/model-gating, but the GPU-only contract test itself is still wanted for `pytest -m gpu`. | Stays; extended alongside `predictors/evo.py`'s ADAPT. |
| `worker/tests/test_genbank.py` | 21 tests: GenBank read/write, multi-record round-trip, input routing. | KEEP | GenBank behavior unchanged. | Stays. |
| `worker/tests/test_gff.py` | 3 tests: GFF3 gene-feature output. | KEEP | Writer format unchanged. | Stays. |
| `worker/tests/test_keeper.py` | 26 tests: `_next_action` state machine, quota health narrowing, `_check_gpu`, `_diagnose_setup` (billing/API/quota/project checks), full `keep_alive` loop behavior (acquire-once, retry-forever, start-a-stopped-box, GPU-unhealthy backoff, evo-install toggle), and an explicit "keeper must never call `stop_vm`/`delete_vm`" guard. | GUT | Tests the gutted always-on-singleton `keeper.py`, which the design removes outright (D1). | Delete from the default test run. **Re-express as C# fixtures**: the `_diagnose_setup` billing/API/quota assertions (`test_diagnose_setup_flags_billing_off`, `_auto_enables_api_when_off`, `_reports_when_api_enable_fails`, `_flags_unreachable_project`, `_flags_no_quota`, `_unknown_billing_is_warning_not_blocking`) map directly onto the C# first-run wizard's setup checks (§5.2) and should become fixtures/tests there. The "never stop/delete" guard's *intent* (a keeper-shaped component must not silently tear down a VM) has no direct C# equivalent since there is no keeper, but the underlying principle (explicit user action before any Stop/Delete) is already covered by design §4.4's Cancel/Stop/Delete buttons. |
| `worker/tests/test_local_fallback.py` | 2 tests: `--prefer-local` returns `False` gracefully without a GPU; succeeds using a monkeypatched mock predictor and writes files. | GUT | Tests `cli._try_local_evo`, which is dropped along with `cloudrun`/`--prefer-local` (§3, §5.7 replaces this with the dedicated `DnaEntropyGraph.LocalEngine` project talking to the same worker package via `LocalBlobstore`). | Delete from the default test run. The *behavior* it protects (a local-GPU attempt must fail gracefully, never raise, so the caller can fall back) should be re-expressed as a test of the new `LocalEngineManager`/`LocalBlobstore` path once §5.7 is implemented. |
| `worker/tests/test_mock_predictor.py` | 12 tests: `(L,4)` shape, float32, row sums, determinism. | KEEP | Predictor contract unchanged. | Stays. |
| `worker/tests/test_pipeline.py` | 5 tests: end-to-end pipeline on the mock predictor, expected files produced. | ADAPT | Needs new assertions once windowing/direction and the worker run-loop wrap `pipeline.run()`, but the existing single-pass, single-contig assertions remain valid as a baseline (Forward-only mode must still match). | Stays; extended alongside `pipeline.py`'s ADAPT. |
| `worker/tests/test_readers.py` | 4 tests: stdin/file reading, read+validate wiring. | KEEP | Reader contract unchanged. | Stays. |
| `worker/tests/test_validation.py` | 16 tests: normalization, alphabet, RNA, ambiguity, length, empty-input. | KEEP | Validation rules unchanged (the new §5.6 pushback checks are additive, tested separately). | Stays. |
| `worker/tests/test_writers.py` | 8 tests: bedGraph/WIG/FASTA spec correctness. | ADAPT | Needs a companion `test_tsv.py` (or extension here) once `TsvWriter` exists (§5.5). | Stays; extended when `TsvWriter` lands. |

### `worker/scripts/`

| Path (in new repo) | Purpose | Verdict | Why | Target location or action |
|---|---|---|---|---|
| `worker/scripts/demo.ps1` | Windows mock-predictor demo (plain locus + `--genes` prokaryotic run) with IGV loading instructions. | REFERENCE | Assumes the old `.venv\Scripts\dna-entropy.exe` CLI shape and local file layout; the WinUI app replaces this manual demo flow (§4.2 New run screen). | `worker/scripts/` kept as REFERENCE for a future smoke-test script, not run as part of CI. |
| `worker/scripts/vm_setup.sh` | Idempotent Evo-stack install on a fresh DLVM (detects existing stack, `TORCH_CUDA_ARCH_LIST` auto-derived, `flash-attn==2.8.3` build). | REFERENCE | Install-on-boot is a fallback per D6 (the primary path bakes the worker into a container image); this script's idempotency check and arch-detection logic are the reference for both the container `Dockerfile` build step and the DLVM startup-script fallback (§5.4). | REFERENCE for `worker/vm/startup.sh` and the `-cuda` Dockerfile. |
| `worker/scripts/gpu_setup.sh` | Installs evo2 into the system Python on a DLVM box, verifying torch survives the install. | REFERENCE | Same install sequence as `vm_setup.sh` but without the idempotency guard; superseded by the container image build. | REFERENCE only. |
| `worker/scripts/gpu_flashattn.sh` | Builds flash-attn 2.8.3 from source for sm_89 (L4) against CUDA 12.9. | REFERENCE | D6 notes the container's NGC PyTorch base ships flash-attn prebuilt specifically to remove this "10-minute flash-attn source build"; kept only as a reference for what the container build step must reproduce for other GPU architectures (A100 sm_80, H100 sm_90). | REFERENCE for the container image build (`ghcr.io/raaif-yousuf/dna-entropy-worker`). |
| `worker/scripts/gpu_diag.sh` | Full GPU box diagnostic (nvidia-smi, torch/CUDA, pip packages, disk, RAM, build tools). | REFERENCE | Useful diagnostic shape for a future `dna-entropy-worker diag` container command, but not wired into anything today. | REFERENCE only. |
| `worker/scripts/gpu_probe.sh` | Probes an unknown box's python/conda/torch layout. | REFERENCE | One-off exploration script from bringing up the DLVM originally; no ongoing role once the container pins the exact environment. | REFERENCE only. |
| `worker/scripts/evo_run.sh` | Batch-runs every locus in a multi-record FASTA through real Evo, named by accession. | REFERENCE | Superseded by the worker's manifest-driven batch run loop (§5.5: "batch run loop with one predictor instance"), which the new `worker/` subpackage implements against the job contract instead of a hand-rolled shell loop. | REFERENCE for the run-loop's per-locus naming convention. |

### `worker/packaging/`

| Path (in new repo) | Purpose | Verdict | Why | Target location or action |
|---|---|---|---|---|
| `worker/packaging/launcher.py` | Double-click PyInstaller entry point: wizard for a dropped file/pasted path/pasted sequence, local-vs-cloud prompt, hands off to `cloudrun`. | GUT | The WinUI 3 app is the double-click entry point now (D2: Velopack installer); there is no more PyInstaller `.exe` wizard. | Not shipped; the "distinguish a path from a pasted sequence" and "never crash silently on a double-click" logic have no direct C# equivalent to port (the app's drop-zone/paste dialog in §4.2 already covers this UX). |
| `worker/packaging/build_exe.ps1` | Builds `dna-entropy.exe`/`keep-gpu.exe` via PyInstaller, excluding torch/evo2/flash-attn/pyrodigal, collecting Biopython submodules. | REFERENCE | PyInstaller packaging is replaced by the container image (D6) and the Velopack installer (D2); the exclude-list and lazy-import discipline it encodes (never bundle torch/evo2) is a useful reference for keeping the worker's *local engine* install (§5.7) similarly lean, but the script itself is not reused. | REFERENCE only. |

### `worker/` top level

| Path (in new repo) | Purpose | Verdict | Why | Target location or action |
|---|---|---|---|---|
| `worker/pyproject.toml` | Package metadata, dependencies (`numpy`, `typer`, `biopython`), extras (`dev`, `evo`, `genes`, `package`), `dna-entropy` console script, pytest `gpu` marker. | ADAPT | `readme` points at `README.md`, which no longer exists at that path (renamed to `README.legacy.md` during migration); the `package` extra (pyinstaller) is obsolete once the container replaces `.exe` builds; the console-script entry point must point at the new worker CLI once `cloudrun`/`keep-gpu` are dropped from `cli.py`. | Stays at `worker/pyproject.toml`; fields updated (see P0 issue below). |
| `worker/README.legacy.md` | Prototype's user-facing README (what it does, cloud setup, IGV/Geneious walkthroughs, cost warnings). | REFERENCE | Describes the gcloud/keeper-based workflow that no longer exists; superseded by `docs/user_guide/*` per §7. | REFERENCE; not linked from the new repo's top-level README. |
| `worker/LICENSE.prototype` | MIT license text from the prototype. | REFERENCE | The new monorepo has its own top-level `LICENSE` (already present at `DNA-Entropy-Graph/LICENSE`, MIT); this copy is redundant but harmless to keep as provenance. | REFERENCE; no action needed (same license text). |
| `worker/.gitignore` | Python/venv/build/output ignore rules for the worker subtree. | KEEP | Directly reusable; already excludes `.venv/`, `__pycache__/`, `*.bedgraph`/`*.wig`/`*.gff3` outputs, `dist/`/`build/`, `runs/`, `evo_results/`. | Stays as `worker/.gitignore`, governs the `worker/` subtree under the monorepo's root `.gitignore`. |
| `worker/keep_gpu.py` | Standalone entry point for the always-on keeper (`python keep_gpu.py`). | GUT | Thin wrapper around `cloud.keeper.main`, which is gutted (D1). | Not shipped. |
| `worker/dna-entropy.spec` | PyInstaller spec for the client `.exe` (excludes torch/evo2/flash_attn/pyrodigal, bundles Biopython). | GUT | PyInstaller build is replaced by the container + Velopack installer (D2/D6). | REFERENCE only for its exclude-list discipline; not run. |
| `worker/keep-gpu.spec` | PyInstaller spec for the keeper `.exe`. | GUT | Same reasoning as `keep_gpu.py`/`cloud/keeper.py`. | Not shipped. |
| `worker/CLAUDE.legacy.md` | Prototype's own project-rules card (11 hard rules, architecture summary, testing status). | REFERENCE | Superseded by the new monorepo's own `CLAUDE.md` (§7: ~21 numbered Hard Rules covering science + split + cloud + user + process rules), which is a superset drawing on this file. | REFERENCE; cross-check the new top-level `CLAUDE.md` against this file's 11 rules to confirm none were dropped by accident. |
| `worker/docs-legacy/DESIGN.md` | Architecture/contracts doc: goals, pipeline diagram, module layout, `Predictor`/`Writer`/`Annotator` contracts, position semantics, validation rules, CLI spec, output spec, IGV design, testing conventions. | REFERENCE | §7 of the design spec: "docs/science_and_formats.md (from prototype DESIGN.md)" — this file's content becomes that new doc, adjusted for windowing/bidirectional prediction and the dropped cloud CLI commands. | Source material for `docs/science_and_formats.md` (P0 issue below); this copy stays as the untouched historical reference. |
| `worker/docs-legacy/DISTRIBUTION.md` | The two-executable model, keeper/client split rationale, local-GPU path, one-time user setup. | REFERENCE | Describes a distribution model (two PyInstaller `.exe`s, always-on keeper) fully replaced by D2 (Velopack installer)/D6 (container)/D1 (per-job VM). | REFERENCE only; superseded by `docs/packaging_design.md` and `docs/cloud_design.md`. |
| `worker/docs-legacy/EVO_SETUP.md` | Evo 2 install/GPU requirements, manual VM provisioning reference, quota notes, debugging. | REFERENCE | Manual `gcloud`-based provisioning steps are gone; the hardware/VRAM rationale (7B/bf16 on a 24 GB card) and quota debugging notes remain useful background for `docs/gcp_setup_manual.md` and `GpuPlanner`. | REFERENCE for the hardware-matrix and quota-debugging sections of the new cloud docs. |
| `worker/docs-legacy/ROADMAP.md` | Sprint 0-5 checklists (all complete) plus a future backlog. | REFERENCE | §7/D12: "ROADMAP.md with checkboxes is not carried over; GitHub Issues + milestones are the only tracker." | REFERENCE only; its backlog items (windowing, reverse strand, reference mapping, bigWig, ANN, GUI) map onto the design spec's §8 milestones / §9 out-of-scope list and the P0 issues below, not a new ROADMAP.md. |

### `worker/legacy/dna-entropy-v1/` (from the older `DNA-Entropy` repo)

| Path (in new repo) | Purpose | Verdict | Why | Target location or action |
|---|---|---|---|---|
| `worker/legacy/dna-entropy-v1/orchestrator.py` | Older prototype's cloud orchestrator: `cloudrun` created a per-run VM and **deleted it by default** after the run, with a `--keep` flag to stop-and-preserve instead (double-confirmed, ~$10/month disk-cost warning) and a confirm-before-delete-on-reuse prompt. | REFERENCE | This is exactly the ephemeral, non-singleton VM lifecycle the new design brings back (D1, §4.3 "VM after task: Stop by default, Delete option, Keep-alive option"); kept purely as a historical reference for the C# `DnaEntropyGraph.Cloud` lifecycle implementation. | `worker/legacy/dna-entropy-v1/`; not imported or shipped by the worker package. |
| `worker/legacy/dna-entropy-v1/cli.py` | Older prototype's CLI, including the `--keep` flag surface on `cloudrun`. | REFERENCE | Shows the exact user-facing flag/prompt shape (`--keep`, confirmation prompts) for the C# app's after-task action picker (§4.3 "After task: Stop / Delete / Keep running"). | Same folder; reference only. |
| `worker/legacy/dna-entropy-v1/README.md` | New file: provenance note (source repo URL, commit hash, why kept). | REFERENCE | Required by the migration task so the two files above are traceable to their source. | New; documents provenance per the task instructions. |

---

## Prototype behaviours the new design must preserve

Everything below lives in the gutted `cloud/keeper.py`, `cloud/orchestrator.py`, and `cloud/gcloud.py` and must be re-expressed against the Compute/Billing/Service Usage/Cloud Quotas APIs in `DnaEntropyGraph.Cloud` (design §3.1, §5.2, §5.4, §5.9). Exhaustive by function; constants quoted verbatim.

### Constants worth carrying forward exactly
- `BOX_NAME = "dna-entropy-box"` (orchestrator.py) — the prototype's single stable box name; the new design explicitly replaces fixed-name discovery with per-job VMs named `deg-<jobId>` found by label (D1, D13), so this constant itself is **not** ported, but its role (a name gcloud/GCP recognizes) maps to the new naming scheme.
- `BASE_IMAGE_FAMILY = "pytorch-2-9-cu129-ubuntu-2404-nvidia-580"`, `BASE_IMAGE_PROJECT = "deeplearning-platform-release"` (orchestrator.py) — matches the design's DLVM family in §5.4 almost exactly (`pytorch-2-9-cu129-ubuntu-2404-nvidia-580`, "overridable; ships Docker, NVIDIA container toolkit and gcloud, to verify"). Carry the family string forward as the default.
- `OFFERS = [("g2-standard-8", "nvidia-l4", "L4"), ("a2-highgpu-1g", "nvidia-tesla-a100", "A100")]` (orchestrator.py) — the L4-then-A100 escalation ladder; the design's model/hardware matrix (§5.4) extends this with A100-80 (`a2-ultragpu-1g`) and H100 (`a3-highgpu-2g`) tiers for the 40B model.
- `DEFAULT_ZONES` (orchestrator.py) — ~90 zones across US/Europe/Asia-Pacific/Middle East/South America/Canada; the design's "quota-filtered regions" zone ladder (§5.4) should start from this same list, filtered by the region-quota pre-check below.
- Backoff bounds: `_BACKOFF_START = 30`, `_BACKOFF_CAP = 300` (keeper.py) — exponential backoff floor/ceiling for the acquire retry loop.
- `_POLL_DEFAULT = 60` (keeper.py) — watchdog heartbeat poll interval on a healthy box; the design's job contract (§5.3) specifies a 30 s worker heartbeat / 10 s app poll instead, so this constant informs but does not directly transfer.
- Timeouts (orchestrator.py, via `gcloud._run`/named calls): VM create `timeout=600`, start/stop `timeout=300`, SSH wait deadline `240` s (5 s poll interval), Evo install `timeout=1800`, run `timeout=1800`, download `timeout=300`. The design's per-stage deadlines (§5.3: provisioning 10 min, boot 8 min, image pull 15 min) are a redesign of these same budgets for a container-pull world instead of an SSH-install world.
- Quota metric names (`gcloud.gpu_quota_metric`): `NVIDIA_L4_GPUS`, `NVIDIA_A100_GPUS`, `NVIDIA_T4_GPUS`, `NVIDIA_V100_GPUS`, fallback `GPUS_ALL_REGIONS` — directly reusable as the Cloud Quotas API metric names in the setup wizard (§4.1 step 6, §5.2).
- Escalating parallelism shape (`_create_box`): 3 sequential -> 3 parallel -> 5 parallel -> then batches of 7, **per GPU tier**, only escalating to the next tier after all zones in the current tier are exhausted. Matches design §5.4 verbatim ("3 seq -> 3 par -> 5 par -> 7 par").
- `_VM_SETUP_SCRIPT` / `scripts/vm_setup.sh` — idempotency check (`import evo2, flash_attn, Bio` succeeds -> `EVO_STACK_PRESENT`, exit 0 fast), `TORCH_CUDA_ARCH_LIST` auto-derived from `torch.cuda.get_device_capability()`, `MAX_JOBS=4`. Reference for the container image build and the DLVM startup-script fallback in §5.4.

### `cloud/gcloud.py` — GCP wrapper surface
- `find_gcloud()` — locates `gcloud`/`gcloud.cmd` on PATH; not applicable once the app talks to APIs directly, but its existence proves the prototype needed exactly the install-check the new OAuth-based setup wizard replaces (§4.1 step 2 "Sign in with Google", no CLI install step at all).
- `active_account()`, `get_project()` — reads the active `gcloud` auth/project; replaced by OAuth `sub` + Resource Manager `projects.search` (D4, §5.1/§5.2).
- `create_vm()` — creates a GPU VM from a public image family with `--maintenance-policy=TERMINATE --restart-on-failure`, a `pd-balanced` boot disk. Maps to the Compute `instances.insert` call in §5.4, plus the design's additional `scheduling.maxRunDuration` + `instanceTerminationAction=DELETE` safety net (§3) that the prototype did not have.
- `find_instance()` — lists instances by name filter, tolerates blank/transitional status and duplicate results, **prefers a RUNNING result** when several exist. Directly reusable logic for the C# per-job VM lookup by label (`app=dna-entropy-graph`, `job-id=<jobId>`).
- `start_vm()` / `stop_vm()` / `delete_vm()` — lifecycle verbs; map 1:1 onto `instances.start`/`instances.stop`/`instances.delete` in the Compute gateway.
- `ssh()` / `scp()` — remote command execution and file transfer, auto-accepting the host-key prompt (`stdin_text="y\n"`), with `--recurse` support for directory downloads. **Not ported** — the design forbids SSH entirely (§3); the equivalent transport is the GCS bucket (`jobs/<jobId>/input/`, `output/`) plus the container's own `docker run` on boot.
- `classify_create_error(stderr)` — buckets a create failure into exactly `billing | api_disabled | quota | stockout | already_exists | permission | network | other`, checking billing before quota (some billing errors mention "account"), matching on: `"billing"` + (`"enable"`/`"disabled"`/`"not found"`/`"not active"`/`"account"`); `"has not been used in project"` / `"accessnotconfigured"` / `"serviceusage"` / compute+api+disabled / `"it is disabled"`; `"quota"`; `"stockout"` / `"zone_resource_pool_exhausted"` / `"does not have enough resources"` / `"resource_availability"`; `"already exists"` / `"resource already exists"`; `"permission"` / `"forbidden"` / `"not authorized"` / `"iam"`; `"could not reach"` / `"connection"` / `"network is unreachable"` / `"timed out"` / `"timeout"`. This is the direct blueprint for `DnaEntropyGraph.Core.ErrorCatalog`'s structured-operation-error-code classification in §5.4, though the C# version should key off Compute API structured error codes rather than stderr substring matching where possible.
- `project_state(project)` — returns the project's lifecycle state (`ACTIVE` etc.) via `projects.describe`; `None` means unreachable (wrong ID/no access/network), empty string means "reachable, state unknown". Maps to Resource Manager `projects.get` in the setup wizard (§5.2).
- `billing_enabled(project)` — returns `(enabled, detail)`; `None` means the check itself failed (Billing API off, missing permission, network) and must be treated as "unknown", not "false". Maps to Billing `projects.getBillingInfo` in §5.2 step 4.
- `api_enabled(service, project)` / `enable_api(service, project)` — checks and idempotently enables a service (e.g. `compute.googleapis.com`); enabling takes ~30-60s. Maps to Service Usage `services.list`/`services.batchEnable` in §5.2 step 5.
- `gpu_quota_metric(accelerator)` — accelerator-to-metric-name mapping (see Constants above).
- `list_region_gpu_quota(metrics, project)` — single `regions list --flatten=quotas[]` call returning `{region: {metric: available}}` with `available = limit - usage` (clamped at 0, `limit<=0` dropped); returns `None` on query failure (distinguishing "unknown" from "genuinely zero everywhere"). Maps to Compute `regions.list` quota reads in §5.2/§5.4, and the "unknown vs. zero" distinction should carry over exactly since it drives different user messaging (retry silently vs. show the quota-request flow).

### `cloud/orchestrator.py` — provisioning and run orchestration
- `preflight(cfg)` — checks gcloud installed -> authenticated -> project set, each with copy-paste-able remediation text (`_INSTALL_MSG`, `_AUTH_MSG`, `_PROJECT_MSG`). Maps to the first-run wizard's per-step OK/Fix rows (§4.1).
- `_quota_msg(label, accel)` — quota-request remediation text: console URL (`https://console.cloud.google.com/iam-admin/quotas`), exact metric name, "request a limit of 1", note that L4 is usually auto-approved. Direct source text for the `NO_GPU_QUOTA` error taxonomy entry (§5.9) and the setup wizard's quota step (§4.1 step 6).
- `_ordered_zones(cfg, state)` — moves the last-known-good zone to the front of the zone list; state persisted via `load_state()`/`save_state()` to `%APPDATA%\dna-entropy\config.json` (`last_zone`, `ssh_key_file`). Maps to the design's "Zone preference: Auto (last-good, then bucket region, then quota-filtered global list)" (§4.3 Cloud group).
- `_setup_remediation(kind, raw)` / `_raise_if_setup(errors)` — for `billing`/`api_disabled`/`permission` failures, abort immediately with the exact `gcloud` error plus a fix command, since these are project-wide and retrying other zones cannot help. Directly informs the `NO_BILLING`/`API_DISABLED`/`PERMISSION_ACTAS` entries in the §5.9 error taxonomy: same principle (abort with one action, don't spam retries across regions).
- `_format_errors(errors)` — final give-up message shows **one exact gcloud error per failure kind** rather than 90 lines of noise. The C# equivalent should likewise summarize by error-kind rather than by zone.
- `_attempt_create` / `_try_sequential` / `_try_parallel` / `_create_box` — the zone/offer escalation ladder itself (3 seq -> 3 par -> 5 par -> 7 par per tier; quota failures recorded into a `no_quota` set and skipped on retry; duplicate VMs from a parallel race are detected and deleted, except a pre-existing box someone else already had running). This whole state machine is the reference implementation for `DnaEntropyGraph.Core.GpuPlanner`'s zone-selection logic in §5.4.
- `_wait_for_ssh(box, zone, cfg, project)` — polls `ssh ... echo ready` every 5 s up to a 240 s deadline. Not ported directly (no SSH), but the "wait with a deadline and a friendly progress message" pattern maps to waiting for `status.json` to reach `booting`/`installing` (§5.3 stage deadlines: provisioning 10 min, boot 8 min).
- `_ensure_evo(box, zone, cfg, project)` — packs the package into a `tar.gz`, scps it plus a setup script, then SSHes `bash ~/vm_setup.sh` with a 1800 s timeout. Not ported (no SSH/self-upload); replaced entirely by `docker pull <digest>` + `docker run` from the startup script (§5.4), which is the whole point of D5/D6 (no GitHub/package dependency at job time).
- `_no_box_error()` — "the always-on keeper provisions VMs, not this app" message. Not applicable once VMs are per-job (D1), but the underlying principle — a clear, actionable error when the expected resource doesn't exist — should appear in the §5.9 taxonomy for whatever the closest analogous state is (e.g. a keep-alive VM's lease expired).
- `run_in_cloud(...)` — the full run flow: preflight -> find existing box (error if none) -> wake if stopped -> wait for SSH -> ensure Evo -> upload input (original file with extension preserved for GenBank/FASTA, else a plain locus file) -> run remote CLI -> echo last 8 lines -> recursively download the whole result folder -> **leave the box running**, printing the exact delete command. The "upload with extension preserved so the remote side auto-detects format" behavior and the "never tear down the VM without telling the user exactly how" behavior both carry forward conceptually into the bucket-based job contract (§5.3) and the After-task action picker (§4.3).

### `cloud/keeper.py` — always-on lifecycle and health checks
- `_next_action(existing)` — state-machine mapping instance status to `acquire` (no box) / `start` (`TERMINATED`/`STOPPED`/`SUSPENDED`) / `up` (`RUNNING`/`STAGING`/`PROVISIONING`/blank-transitional). This exact three-way state machine is the reference for the C# job reconciler's "what do I do with the VM I found" decision in §3 ("reconciler reattaches non-terminal runs from `status.json`/instance state").
- `_apply_quota_health(cfg, all_zones, project)` — reads region GPU quota once, narrows the zone list to regions with quota **only when quota data is available**; if the query itself fails, tries all zones (quota is "unknown", not "zero"); if quota is genuinely zero everywhere, still tries all zones (the quota API can under-report) while printing the fixable-case guidance. This exact "unknown vs. zero vs. narrowed" three-way logic should carry over to `GpuPlanner`'s region pre-filter in §5.4.
- `_diagnose_setup(cfg, account, project)` — the one-time GCP setup health report, printed once per keeper start, covering exactly four checks in order:
  1. Project reachable and `ACTIVE` (via `project_state`).
  2. Billing enabled (via `billing_enabled`; `None` -> "WARN: could not verify billing" not a hard failure, since the Billing API being off is a different, non-blocking problem).
  3. Compute Engine API enabled, **auto-enabled if off** (idempotent, ~30-60s), with the manual fix command (`gcloud services enable compute.googleapis.com`) shown if the auto-enable itself fails.
  4. GPU quota present in at least one region (reusing the same quota read as `_apply_quota_health`); prints the quota-request steps if zero everywhere.
  Every check prints `OK:`/`WARN:`/`ERROR:` plus the exact fixing command, and the function **never raises**. This is close to a line-by-line spec for the first-run wizard's step 5/6 OK/Fix rows (§4.1).
- `_check_gpu(zone, cfg, project)` — runs `nvidia-smi -L` over SSH and checks for the string `"GPU"` in stdout, because "SSH-reachable is not the same as GPU-healthy". The design's equivalent health signal is the container's own `docker run --gpus all` succeeding and the worker writing a `model-loading`/`running` status (§5.4 startup script: "wait for `nvidia-smi`"), so this exact check is reused almost verbatim inside the startup script rather than from the app side.
- `_preflight_forever(cfg, sleep)` — retries `preflight()` forever with `_BACKOFF_START` between attempts, printing "fix the above, then leave this running" rather than exiting. Not directly ported (the app is interactive, not a background daemon), but its "never give up, just keep surfacing the same actionable error" philosophy matches the design's `HEARTBEAT_LOST`/`VM_DIED` retry semantics.
- `keep_alive(cfg, install_evo, poll, max_cycles, sleep)` — the main loop: on `acquire`, first narrows zones by quota health then calls `_create_box`; on any `GcloudError` during acquire, prints every line of the remediation text and retries after a fixed 10 s (not the exponential backoff, deliberately fast since setup errors are usually fixed quickly by the user); on `start`, calls `start_vm` then falls through to the reachability check; after acquire/start, waits for SSH, verifies the GPU with `_check_gpu`, and **only then** calls `_ensure_evo` and marks the box `ready`; heartbeats at `poll` (default 60s) once healthy. **Never calls `stop_vm` or `delete_vm`** — enforced by a test (`test_keeper.py`'s `patched` fixture asserts this explicitly). The loop's differentiated backoff (10s for setup errors vs. exponential 30s-300s for transient capacity errors) is a useful pattern for the C# reconciler's retry policy, but the "never stop/delete" invariant itself is inverted by design D10 (the app *does* stop/delete VMs, just never a shared singleton and always per an explicit lifecycle setting).
- `main(cfg)` — entry point; on `KeyboardInterrupt`, prints a reminder that the VM is still running and billing, with the exact delete command. This "always tell the user exactly how to stop paying" discipline should carry over into every terminal/cancelled state in the §5.9 error taxonomy and the Cloud page's cost banners (§4.7).

---

## Test suite status

`uv` is on PATH (`uv 0.12.13`). Environment set up and tests run exactly as specified:

```
uv venv C:\Users\raaif\DNA-Entropy-Graph\worker\.venv --python 3.12
uv pip install -e "C:\Users\raaif\DNA-Entropy-Graph\worker[dev]"
C:\Users\raaif\DNA-Entropy-Graph\worker\.venv\Scripts\python.exe -m pytest C:\Users\raaif\DNA-Entropy-Graph\worker\tests -m "not gpu" -q
```

- The venv's interpreter is `C:\Users\raaif\AppData\Local\Programs\Python\Python312\python.exe` (confirmed via `uv venv` output) — **not** the MSYS2 python at `C:\msys64`.
- `uv pip install -e ".[dev]"` succeeded, installing `dna-entropy==0.0.1` (editable) plus `numpy==2.5.3`, `biopython==1.88`, `typer==0.27.2`, `pytest==9.1.1`, and their transitive deps (15 packages total).
- Result (verbatim summary line):

```
147 passed, 2 skipped in 1.86s
```

  147 test items were collected and run (12 test modules: `test_cloud.py` 38, `test_entropy.py` 7, `test_evo_logits.py` 5, `test_genbank.py` 21, `test_gff.py` 3, `test_keeper.py` 26, `test_local_fallback.py` 2, `test_mock_predictor.py` 12, `test_pipeline.py` 5, `test_readers.py` 4, `test_validation.py` 16, `test_writers.py` 8). The 2 "skipped" are whole-module skips at import time via `pytest.importorskip`: `test_annotator.py` (needs the optional `pyrodigal` package, not installed by `[dev]`) and `test_evo_predictor.py` (needs `torch`/`evo2`, correctly absent on a GPU-less dev machine and also marked `gpu`). `FEATURES.md`'s per-file test counts (e.g. "24" for `test_cloud.py`, "20" for `test_keeper.py`, "127 tests" overall) are noticeably lower than what this checkout actually contains (e.g. 38 and 26 respectively) — that document is stale relative to the checked-out commit and was not corrected as part of this migration (it documents the prototype, not this repo).
- No failures. No fixes were made to the code — this is exactly what was copied.
- Note: the first run (without an explicit `--basetemp`) hit a pytest-internal `PermissionError` during its own temp-directory cleanup (`cleanup_dead_symlinks` under `%TEMP%\pytest-of-raaif\pytest-current`), a pre-existing Windows/pytest interaction unrelated to the migrated code; all 147/2 results above were already reported before that cleanup step ran. A rerun with an explicit `--basetemp` completed cleanly with exit code 0 and no traceback, confirming the failure was cleanup-only.
- `dna_entropy.egg-info/` and every `__pycache__/` directory created by this install/test run were deleted afterward so the migration leaves no build artifacts behind (all such paths are also covered by `worker/.gitignore`).

---

## Proposed P0 cleanup issues

The same list is saved as JSON at
`C:\Users\raaif\AppData\Local\Temp\claude\C--Users-raaif-DNA-Entropy-Graph\dc1bc05e-4761-4567-8c38-d22e1fe0c611\scratchpad\p0_worker.json`.

1. **`worker: drop cloudrun and keep-gpu from the CLI`**
   Why: `cli.py` still exposes `cloudrun` and `keep-gpu`, which shell out to gcloud/SSH and manage an always-on singleton VM that the approved design explicitly removes (§3). Leaving them in place invites wiring the WinUI app to the wrong lifecycle.
   Done when: `cli.py` has no `cloudrun`/`keep-gpu`/`--prefer-local`; `cloud/` package is deleted from the shipped worker package; a worker entrypoint command exists in its place; `test_cloud.py`/`test_keeper.py`/`test_local_fallback.py` are deleted or quarantined.
   Observable: `dna-entropy --help` (or its replacement) lists no `cloudrun`/`keep-gpu`, and pytest collects none of the three gutted test files.

2. **`worker: add the worker subpackage (manifest, status heartbeat, blobstore, cancel, lifecycle)`**
   Why: §5.5 requires a new `dna_entropy.worker` subpackage so a per-job VM can read a manifest, write status/progress, upload results, and self-stop/delete via the Compute API; none of this exists yet.
   Done when: `dna_entropy/worker/` has `manifest.py`, `status.py`, `blobstore.py` (Protocol + `GcsBlobstore` + `LocalBlobstore`), `cancel.py`, `lifecycle.py`; a run loop calls `pipeline.run()` per manifest; unit tests use `LocalBlobstore` only.
   Observable: a manifest.json + `LocalBlobstore` fixture runs end to end in pytest and produces `status.json`/`result.json`.

3. **`worker: implement windowing and bidirectional direction analysis`**
   Why: §5.6 is the owner's top priority (accurate first bases via forward + reverse-complement) and §4.3 exposes Context length/Direction as user options, but `analysis/windowing.py`/`analysis/direction.py` don't exist.
   Done when: windowing computes `W`/`S` per §5.6; direction implements the four combination modes; tests cover the seam at `K` and the `L < 2K` fallback; Forward-only reproduces the prototype's output bit-for-bit.
   Observable: a passing test asserts Forward-only entropy matches the legacy `pipeline.run()` output on `tests/data/sample.fasta`.

4. **`worker: replace cloud/gcloud.py and cloud/orchestrator.py logic with a startup-script + Compute-API design doc reference, not shipped code`**
   Why: the valuable parts (error buckets, quota metrics, zone ladder, remediation text) must survive for the C# port, but the files themselves violate the "no gcloud, no SSH" rule (§3).
   Done when: `worker/legacy`/`docs-legacy` retain the files untouched; a cloud design doc transcribes the buckets/ladder/quota names; nothing shipped imports `cloud/gcloud.py` or `cloud/orchestrator.py`.
   Observable: grepping for `from .cloud`/`from dna_entropy.cloud` returns no hits outside `worker/legacy` and `docs-legacy`.

5. **`worker: add TsvWriter and wire it into the output file list`**
   Why: §4.3 lists "Entropy TSV (new)" as an output option that doesn't exist in the prototype `writers/` package.
   Done when: `writers/tsv.py` implements a Writer-compatible `TsvWriter`; the run loop can include it in the output set; a unit test asserts the TSV's header/formatting.
   Observable: pytest produces a `<name>.entropy.tsv` fixture file with the expected header row.

6. **`worker: add model gating for Hopper-only Evo variants`**
   Why: §5.5 calls for a `MODEL_NEEDS_HOPPER` gate and D11/D16 restrict `evo2_40b`/`evo2_1b_base` to H100; `predictors/evo.py` currently has no hardware check.
   Done when: a model-to-minimum-GPU-tier mapping exists; `EvoPredictor` (or its caller) raises `PredictorError` before loading a Hopper-only model on a non-Hopper device; a GPU-free unit test exercises the gate.
   Observable: requesting `evo2_40b` on L4/A100 device metadata raises `PredictorError` before any model-loading code runs.

7. **`worker: multi-record FASTA parity with GenBank (process all records)`**
   Why: D14 changes prototype behavior — FASTA must process all records like GenBank, not just the first.
   Done when: `readers/fasta.py` returns all records; `readers/input.py` routes every FASTA record into its own `Contig`; tests assert a multi-record FASTA fixture produces N contigs and N output blocks.
   Observable: a 3-record FASTA fixture produces 3 contigs in `RunResult.contigs` and 3 named output blocks.

8. **`worker: reproduce quota-parsing and error-classification test cases as fixtures`**
   Why: `test_cloud.py`'s `classify_create_error`/`list_region_gpu_quota` cases encode real GCP error text that must not be lost when the cloud module is gutted; the C# `ErrorCatalog` needs the same coverage.
   Done when: `tests/contract-fixtures/` has a JSON vector of every `test_classify_create_error` stderr sample and expected bucket, plus the quota CSV parsing cases; the fixture is referenced from an open C# issue.
   Observable: `tests/contract-fixtures` contains a committed JSON file enumerating every `classify_create_error` case, readable by both pytest and a future xUnit test.

9. **`worker: update pyproject.toml for the new package layout and drop packaging extras`**
   Why: `pyproject.toml` still points `readme` at the now-renamed `README.md` and declares the obsolete `[package]` (pyinstaller) extra; the worker ships as a container now (D6), not a `.exe`.
   Done when: `readme` points at the real README (or is removed); `[package]` extra is removed; the console-script entry point matches the new CLI shape (no `cloudrun`/`keep-gpu`).
   Observable: `pip install -e .` from `worker/` succeeds with no dangling README reference and no pyinstaller dependency pulled in.

10. **`docs: port DESIGN.md content into docs/science_and_formats.md`**
    Why: §7 calls for `docs/science_and_formats.md` sourced from the prototype's `DESIGN.md`, but that content currently only exists under `worker/docs-legacy/`, not in the shipped docs tree.
    Done when: `docs/science_and_formats.md` exists with the contracts/position-semantics/validation content carried over, updated for windowing/bidirectional prediction; `worker/docs-legacy/DESIGN.md` stays untouched and cross-linked.
    Observable: `docs/science_and_formats.md` exists and is linked from `docs/README.md`'s index.

11. **`docs: write the C# port issue list for cloud/keeper.py, orchestrator.py, gcloud.py behaviours`**
    Why: this inventory's "Prototype behaviours" section enumerates every health check, quota rule, and remediation text in the gutted cloud modules; without tracked issues this knowledge is lost once `worker/legacy` stops being read.
    Done when: one issue per major behaviour group (setup health check, zone/offer escalation ladder, quota pre-filter, GPU health probe, watchdog loop) exists, each citing this doc and naming its target C# project.
    Observable: this inventory's "Prototype behaviours" section has a filed-issue number next to each behaviour group.
