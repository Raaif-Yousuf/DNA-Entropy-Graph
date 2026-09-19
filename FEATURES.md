# DNA-Entropy — Complete Feature & Capability Inventory

**Scope of this document.** A comprehensive catalogue of everything the DNA-Entropy tool
does, compiled from three directories on this machine:

| Directory | Status | Contents |
|---|---|---|
| `C:\Users\raaif\DNA-Entropy-Genbank` | **Current / authoritative** — superset of everything | Full tool, Sprints 0–5, 127 tests, 2 built `.exe`s |
| `C:\Users\raaif\DNA-Entropy` | **Earlier snapshot** (Sprints 0–4) | Same core; no GenBank I/O, no keeper, 72 tests |
| `C:\Users\raaif\DNA-Entropy-Graph` | **Empty** | No files present |

Unless flagged with ⚠️ *(older repo only)*, every feature below is present in
`DNA-Entropy-Genbank`. A short section at the end lists what differs between the two
code-bearing repos.

---

## 1. What the tool is

A command-line (and double-click) tool that computes the **per-position Shannon
information entropy** of a DNA sequence using the **Evo 2 (7B) genomic language model**,
and exports it as a track viewable in IGV, Geneious Prime, SnapGene, Benchling, UCSC and
JBrowse.

```
input (GenBank / FASTA / pasted DNA)
   -> validate -> Evo 2 (one forward pass) -> Shannon entropy per base
   -> export (entropy track + GenBank + gene track + stats)
```

- Proof of concept for Prof. Meers, Vanderbilt University Medical Center.
- MIT licensed; Python 3.10–3.12; version `0.0.1`.
- Designed so Evo can later be swapped for a custom ANN with **zero downstream changes**.

---

## 2. Scientific core

### 2.1 Entropy computation
- **Per-position Shannon entropy in bits**: `H = -Σ pᵢ·log₂(pᵢ)` over `p = [P(A), P(C), P(G), P(T)]`.
- Uses the `0·log₂(0) := 0` convention, computed with a masked `np.log2` (no `-inf`/NaN).
- Guaranteed output range `[0.0, 2.0]` bits, clipped against floating-point excursions.
  - Uniform distribution (all bases 25%) → **2.0 bits** (maximum uncertainty).
  - One-hot distribution (one base at 100%) → **0.0 bits** (fully predictable).
- Interpretation: low entropy = highly predictable to the model (often conserved /
  structured); high entropy = uncertain.
- Output dtype is `float32`, shape `(L,)`.

### 2.2 Summary statistics
- `summarize()` produces length, mean, minimum, maximum, and the 0-based `argmin`/`argmax`
  positions of the entropy track.
- Positions in reports are converted to the user's genomic coordinate frame via `--start`.

---

## 3. The model layer (the single swap point)

### 3.1 `Predictor` contract
A `typing.Protocol` (runtime-checkable) that every backend must satisfy:

```python
Predictor.predict(seq: str) -> np.ndarray   # shape (len(seq), 4), float32, columns [A, C, G, T]
```

- Column order `[A, C, G, T]` is **fixed project-wide**.
- Each row must sum to `1.0` within a `1e-4` tolerance.
- `check_probability_matrix()` is a hard runtime guard invoked at the predictor boundary —
  it validates shape, dtype, `[0,1]` value range, and per-row sums, raising `ValueError`
  with the worst deviation on failure.
- Everything downstream (entropy, all writers) depends **only** on this `(L, 4)` array.

### 3.2 `EvoPredictor` — real Evo 2 7B
- Wraps the `evo2` package; **the only module in the codebase allowed to import
  `torch` or `evo2`** (enforced as a hard project rule).
- **One forward pass per contig** — never loops the model per position.
- **Tokenizer-derived nucleotide IDs**: asks the tokenizer for the exact token ids of
  `A`, `C`, `G`, `T` rather than hard-coding ASCII; fails loudly if `"ACGT"` does not
  tokenize to exactly 4 tokens.
- Casts `uint8` tokenizer ids to `int` so torch does not misread them as a boolean mask.
- **Robust return-shape handling** (`_extract_logits`): tolerates evo2 versions that
  return a bare tensor, a nested tuple, an object with `.logits`, with or without a batch
  dimension.
- Configurable model id (`--model`, default `evo2_7b`) and device (`--device`, `cuda`/`cpu`).
- Configurable single-pass context cap (`max_context`, default 8192 nt) with a clear
  error when exceeded.
- Runs in **bf16** — works on Ampere or Ada (no FP8/Hopper hardware required).
- Verified end-to-end on a real GCP L4 (`g2-standard-8`).

### 3.3 `logits.py` — torch-free alignment math
- Numerically stable row-wise softmax over the four nucleotide logits (equivalent to
  renormalizing over A/C/G/T).
- **Autoregressive position alignment**: Evo's logits at position `i` predict base `i+1`,
  so outputs are shifted so that row `i` is the distribution *for* base `i`.
- Row 0 (no preceding context) is set to the uniform distribution = 2.0 bits, by design
  and documented.
- Pure NumPy, so this trickiest part of the integration is unit-testable on any machine
  without a GPU.

### 3.4 `MockPredictor` — GPU-free stand-in
- Produces deterministic, contract-correct `(L, 4)` probabilities so the *entire* pipeline
  runs on a laptop with no GPU and no Evo install.
- **Reproducible across processes**: seeds the RNG from `seed + zlib.crc32(seq)` rather
  than Python's randomized `hash()`, so identical inputs give identical output every run,
  while different sequences still differ.
- Exposed via `--seed` for reproducibility control.
- A true drop-in swap for `EvoPredictor` (same contract, validated by the same guard).

### 3.5 Future backend
- An `AnnPredictor` (Evo embeddings + BLAST → trained ANN) can replace Evo behind the same
  contract with no changes to readers, validation, analysis, writers, or CLI.

---

## 4. Input handling

### 4.1 Supported input sources
| Source | How |
|---|---|
| **GenBank** | `.gb`, `.gbk`, `.genbank`, `.gbff` files |
| **FASTA** | `.fa`, `.fasta`, `.fna`, `.ffn` files |
| **Plain / pasted sequence** | A text file, stdin pipe, or text typed/pasted into the wizard |
| **Dropped file** | Drag-and-drop onto the `.exe` window |

### 4.2 Format auto-detection (`readers/detect.py`)
- Extension-first detection, falling back to a **content sniff** of the first non-blank
  line (`LOCUS` → GenBank, `>` → FASTA, otherwise paste).
- Manual override with `--informat genbank|fasta|paste`.
- Unreadable file → safely degrades to the paste path.

### 4.3 GenBank reader
- Parses **every record** in the file via Biopython (`SeqIO.parse`) — multi-record GenBank
  files are fully supported, each record analyzed separately.
- Extracts each record's sequence plus its **pre-existing gene annotations**, preferring
  `gene` features and falling back to `CDS` features.
- Feature labelling picks the first available of `gene`, `locus_tag`, `product` qualifiers,
  else the feature type.
- Converts Biopython's 0-based half-open coordinates to the project's 1-based inclusive
  `GeneFeature` frame, including strand (`+`/`-`) and partial-feature detection (`<`/`>`).
- Skips records with an empty or all-`N` ORIGIN block, with a notice.
- **Never re-annotates a GenBank** — the file's own curated genes are treated as
  authoritative and Prodigal is never run on this path.
- Reports how many records and features were read as a user-facing notice.

### 4.4 FASTA reader
- Hand-rolled, dependency-free parser (works even without Biopython).
- Multi-record FASTA → analyzes the first record and emits a notice naming it.
- Errors clearly when no `>` header/records are found.

### 4.5 Paste / stdin reader
- Reads raw text from a file path, or from stdin when no path is given.
- Performs no cleaning itself — raw text is handed to the validation stage (strict
  separation of reading and validating).

### 4.6 Unified loader (`readers/input.py`)
- Routes any input to the right reader and returns a `LoadedInput` carrying one or more
  `Contig` objects (name, sequence, features, original record id) plus notices and the
  detected `source_kind`.
- **Output-safe contig naming**: sanitizes names to `[A-Za-z0-9._-]`, and for multi-record
  inputs indexes off the run name (`name_1`, `name_2`, …) — so unsafe ids such as
  Geneious's `geneious|urn:local:...` never reach a filename, IGV contig, or GenBank LOCUS.
- Applies **lenient ambiguity handling for GenBank/FASTA** (real files contain `N`) while
  keeping the paste path strict A/C/G/T.

---

## 5. Validation (always runs before any model)

- **Normalization**: strips all whitespace/newlines/tabs, removes digits (pasted line
  numbers), uppercases the sequence.
- **Leading FASTA header strip**: a single leading `>` line in pasted text is removed with
  a notice quoting the header.
- **Digit removal notice**: reports exactly how many digit characters were removed.
- **RNA detection**: finds `U`, reports its 1-based position, and instructs the user to
  re-run with `--rna`; with `--rna` it converts `U→T` and reports the conversion count.
- **Strict alphabet check** reporting the **first offending character, its 1-based
  position, and the total count** of bad characters.
- **IUPAC ambiguity awareness**: recognizes `N R Y S W K M B D H V`; gives a specific hint
  when one is the blocker, and in `allow_ambiguity` mode (GenBank/FASTA) keeps them with a
  notice explaining that entropy at those positions reflects prediction, not a known base.
- **Empty-input rejection** after cleaning.
- **Length cap** against the single-pass context limit (`--max-len`, default **8192 nt**),
  with an explanatory error about windowing being future work and VRAM being the bound.
- **Short-sequence warning** (< 10 nt) noting entropy near the start is dominated by the
  model's prior.
- Non-fatal observations are returned as **notices** and surfaced in yellow by the CLI —
  they never abort a run.
- Available as a standalone command (`dna-entropy validate`) so input can be checked
  without spending GPU time, and the cloud path validates **locally first** before
  touching a VM.

---

## 6. Gene annotation (optional)

- `Annotator` Protocol + `GeneFeature` dataclass (1-based inclusive `begin`/`end`,
  `strand`, `partial`, `gene_id`) + `AnnotatorError`.
- **`ProdigalAnnotator`** using Pyrodigal in **meta mode** (Prodigal's pre-trained models),
  so no training pass is needed and short loci work.
- Lazily imported from the optional `[genes]` extra with an actionable install message.
- **Prokaryotic caveat documented** — not accurate for eukaryotic genomes.
- Emits sequential `gene_N` ids, strand, and partial-gene flags.
- **Never on the critical path**: with `--genes` it is an explicit opt-in that may raise;
  as a bonus for the GenBank output it is strictly best-effort and swallows all failures,
  so the entropy track is always produced.
- **Not run at all for GenBank input** — the file's own genes are used instead.

---

## 7. Outputs

### 7.1 Output set by input kind

**GenBank input** (written into `<out>/<name>/`):

| File | Contents |
|---|---|
| `<name>.gb` | One GenBank record per input record; original genes preserved, each carrying `/note="mean_entropy=… bits"` |
| `<name>.fasta` | All records as contigs — load this as the IGV genome |
| `<name>.entropy.bedgraph` | Full-resolution per-base entropy, one block per record |
| `<name>.entropy.wig` | Same track in fixedStep WIG |
| `<name>.entropy.geneious.gff3` | Per-position entropy track for Geneious Prime |
| `<name>.genes.gff3` | The file's **own** genes as an IGV feature track (`source=genbank`); written only if genes exist |
| `stats.txt` | Multi-record summary (overall block + one block per record) |

**FASTA / pasted input**:

| File | Contents |
|---|---|
| `<name>.fasta` | Sequence as a single named contig |
| `<name>.entropy.bedgraph` *or* `<name>.entropy.wig` | Entropy track in the chosen `--format` |
| `<name>.entropy.geneious.gff3` | Per-position entropy track for Geneious |
| `<name>.summary.txt` | Length + entropy statistics |
| `<name>.genes.gff3` | Prodigal-called genes (with `--genes`) |
| `<name>.gb` | **Bonus** GenBank (genes from Prodigal where available; never fails the run) |

### 7.2 Writer capabilities
- **`Writer` Protocol** — one uniform signature so the pipeline can call every writer the
  same way; each ignores what it doesn't need.
- All files written **UTF-8 with explicit LF newlines** for deterministic, IGV-friendly,
  cross-platform output; parent directories are created automatically.
- **bedGraph writer** — 0-based half-open `chrom start end value`, 4-decimal values, with a
  `track type=bedGraph … visibility=full` header so IGV renders a bar graph immediately.
- **WIG writer** — compact fixedStep, 1-based, one value per line, `track type=wiggle_0`
  header, one `fixedStep` block per contig.
- **FASTA writer** — 60-column wrapped records; contig names match the entropy track's
  `chrom` so coordinates line up in IGV without any reference genome.
- **GenBank writer** — builds Biopython `SeqRecord`s with `molecule_type=DNA`,
  `source=DNA-Entropy`, a descriptive header (including the original source id when it
  differs), and one `gene` feature per input gene carrying its **mean entropy note**, the
  `gene` qualifier, and a `partial` marker when applicable. LOCUS ids are truncated to 16
  characters to avoid Biopython warnings; out-of-range features are clipped or skipped.
- **Geneious GFF3 writer** — emits one 1 bp feature per position with the entropy in **both**
  the score column and an `entropy` qualifier (plus `Name=H=…`), on the same contig and
  coordinate frame as the FASTA/GenBank. This exists because **Geneious Prime imports GFF3
  but treats WIG/bedGraph as export-only**, so this is the Geneious-native equivalent; it
  can then be shaded via *Color by / Heatmap*.
- **Gene GFF3 writer** — `##gff-version 3` plus `##sequence-region` headers, one line per
  gene, start-offset aware, with **percent-encoding of GFF3-reserved characters**
  (`; = & , tab newline`) in attribute values; `source` is `pyrodigal` or `genbank`
  depending on provenance.
- **Summary writer** — single-record report (name, length, coordinate start, mean/min/max
  with extrema positions) and a multi-record variant (aggregate block over all contigs,
  then one block per contig); filename overridable (`stats.txt` for GenBank runs).
- **Multi-record support across every track writer** (`write_multi`) — one file containing
  a correctly-keyed block per contig, so a multi-record GenBank round-trips end to end.
- **Coordinate offsetting** — `--start` shifts every track and feature onto a real genomic
  coordinate frame consistently.

### 7.3 Viewer support
- **IGV (desktop and web)** — self-contained genome from the FASTA + bedGraph/WIG entropy
  bar graph + GFF3 gene track. Documentation explicitly warns to load the FASTA, not the
  `.gb`, because IGV-web cannot build a genome from GenBank.
- **Geneious Prime** — GenBank/FASTA sequence + the `entropy.geneious.gff3` heatmap track,
  with step-by-step import and *Color by / Heatmap* instructions.
- **SnapGene / Benchling / Geneious** — coarse per-gene view via the `mean_entropy` notes
  in the `.gb`.
- **UCSC / JBrowse** — via the standard WIG/bedGraph tracks.

---

## 8. Command-line interface

Built on Typer; the CLI only parses arguments and delegates.

| Command | Purpose |
|---|---|
| `dna-entropy run` | Full local pipeline: validate → predict → entropy → export |
| `dna-entropy validate` | Validate a sequence without running any model |
| `dna-entropy cloudrun` | Run Evo 2 on the always-on cloud GPU box (or local GPU first) |
| `dna-entropy keep-gpu` | Run the always-on GPU keeper |
| `dna-entropy version` | Print the version |

### 8.1 `run` options
`--input/-i`, `--name` (prompts if omitted), `--informat`, `--predictor mock|evo`,
`--model`, `--device`, `--out/-o`, `--format bedgraph|wig`, `--start`, `--max-len`,
`--rna`, `--genes/--no-genes`, `--seed`.

### 8.2 `cloudrun` options
`--input/-i`, `--name`, `--out/-o`, `--informat`, `--genes/--no-genes` (default **on**),
`--rna`, `--prefer-local`, `--project`, `--zone`, `--ssh-key-file`.

### 8.3 `keep-gpu` options
`--project`, `--zone`, `--ssh-key-file`, `--no-install`.

### 8.4 CLI behaviours
- **Name sanitization** — collapses whitespace to `_`, strips characters outside
  `[A-Za-z0-9._-]`, and rejects names with no usable characters, so the name is always safe
  as a folder, filename, and IGV contig id.
- **Default output location is the user's `Downloads` folder**, in a per-run subfolder
  `<out>/<name>/` — no `--out` needed for a lab user.
- **Result reporting**: notices in yellow, a green `OK:` headline with nucleotide count
  (and contig count for multi-record runs), entropy mean/min/max, gene count, the output
  folder, and a list of every file written.
- **Friendly error UX** — `ValidationError`, `PredictorError`, `AnnotatorError` are caught
  and printed as a red `ERROR: …` with exit code 1, never a raw traceback.
- **ASCII-safe console output throughout** (`OK:` / `ERROR:` / `-`), because lab users run
  a Windows cp1252 console that crashes on Unicode glyphs.
- **"Stuck? paste the error into an LLM" hint** printed on cloud failures.
- Invalid `--predictor`/`--format` values are reported as clean errors, not stack traces.

---

## 9. Cloud GPU capabilities

The tool drives the **user's own** `gcloud` CLI — no embedded credentials, nothing hosted
by the author, and the sequence never leaves the user's own Google Cloud project.

### 9.1 The keeper / client split
- **Keeper** (`keep_gpu.py`, `keep-gpu.exe`, `dna-entropy keep-gpu`) owns the VM lifecycle:
  secures a GPU VM and keeps it **RUNNING 24/7**. It never stops or deletes the VM.
- **Client** (`cloudrun`, `dna-entropy.exe`) never touches the VM lifecycle: it finds the
  keeper's box, wakes it if stopped, runs, downloads, and leaves it running. With no box
  present it errors with "start the keeper first" rather than creating one.

### 9.2 Provisioning (keeper)
- Single stable box name per project (`dna-entropy-box`) so an existing box is detected and
  reused.
- Boots from **Google's public Deep Learning VM image**
  (`pytorch-2-9-cu129-ubuntu-2404-nvidia-580`) — no private image is hosted.
- **GPU escalation ladder**: L4 (`g2-standard-8`) first, A100 (`a2-highgpu-1g`) as a
  stockout fallback from a different capacity pool.
- **~90 zones across US, Europe, Asia-Pacific, Middle East, South America and Canada**.
- **Escalating parallelism**: 3 zones sequential → 3 parallel → 5 parallel → batches of 7,
  per GPU tier, until a box is secured.
- **Duplicate cleanup** — if parallel creates race and several succeed, the extras are
  deleted automatically (never a pre-existing box).
- **Last-good-zone memory** persisted to `%APPDATA%\dna-entropy\config.json` and tried
  first on subsequent runs (also stores the SSH key file).
- `--maintenance-policy=TERMINATE` with `--restart-on-failure`, 100 GB pd-balanced boot disk.

### 9.3 Error classification & remediation
- `classify_create_error()` buckets every failure into **billing, api_disabled, quota,
  stockout, already_exists, permission, network, other**.
- **Project-wide setup failures (billing / API disabled / permission) abort immediately**
  with tailored remediation text *including the exact gcloud error*, instead of pointlessly
  retrying all 90 zones — this was the real-world symptom of a mis-configured project.
- **Quota failures do not abort** — other regions keep being tried, and regions known to
  lack quota are remembered and skipped.
- GPU-type-specific quota guidance: the exact console URL, the service, the precise quota
  metric (`NVIDIA_L4_GPUS` / `NVIDIA_A100_GPUS`), and the steps to request a limit of 1.
- Final give-up message summarizes **one exact gcloud error per failure kind**.
- Dedicated install / auth / project setup messages with the exact commands to run.

### 9.4 Preflight and health checks
- **Preflight**: gcloud on PATH → active account → project set, each with actionable text.
- **One-time GCP setup health report** printed by the keeper at startup:
  1. Project reachable and `ACTIVE`.
  2. **Billing enabled** (no billing → zero VMs will ever create).
  3. **Compute Engine API enabled — and auto-enabled if off** (idempotent, ~30–60 s), with
     the manual command if the automatic enable fails.
  4. **GPU quota present in at least one region**, otherwise the one-time request steps.
  Each check prints `OK:` or a precise `ERROR:` plus the fixing command. Never raises.
- **Region quota pre-filter**: reads per-region GPU quota in a single `regions list` call
  and narrows the zone list to regions that actually have quota — eliminating the flood of
  "no quota in this region" noise and making any remaining failure a genuine stockout. It
  distinguishes *"quota unknown"* (query failed → try all zones) from *"quota is zero
  everywhere"* (fixable → print the request steps).
- **GPU health probe**: `nvidia-smi -L` over SSH, because SSH-reachable ≠ GPU-healthy.
- **SSH readiness wait** with a 240 s deadline and 5 s polling.

### 9.5 The watchdog loop
- Runs **forever and never errors out**: stockout, quota, auth, and transient gcloud
  failures are all treated as "try again later".
- Exponential backoff from 30 s capped at 300 s, reset on success; 60 s heartbeat poll on a
  healthy box with timestamped status lines.
- **Restarts the VM** if host maintenance (TERMINATE policy) ever stops it.
- **Retries preflight forever** so the user can fix gcloud/auth/project while it runs and
  it picks up automatically.
- Re-reads quota each acquisition attempt, so a newly granted quota is picked up without a
  restart.
- Clear cost warnings on start and on Ctrl-C, including the exact `gcloud compute instances
  delete` command.

### 9.6 Remote execution
- **Idempotent remote Evo install**: detects an existing stack and exits fast; otherwise
  installs typer, biopython, pyrodigal, evo2, ninja, and builds `flash-attn==2.8.3`
  `--no-build-isolation`, with `TORCH_CUDA_ARCH_LIST` **auto-derived from the VM's actual
  GPU compute capability** (so it works on whichever GPU the stockout fallback landed on)
  and `MAX_JOBS=4`. Everything from public pip; weights from Hugging Face.
- **Self-upload**: packs its own `dna_entropy` package into a single `tar.gz` and scps it
  (single-file scp is reliable where recursive scp is not); works from a frozen `.exe` via
  the bundled `_pkgsrc` data directory.
- **Original-file upload**: for GenBank/FASTA the original file is uploaded *with its
  extension preserved*, so the remote pipeline auto-detects the format and GenBank genes
  survive to the VM; pasted sequences go up as a plain locus file.
- Runs the remote CLI with the right flags, echoes the last 8 lines of remote output, then
  **recursively downloads the whole result folder** to `Downloads\<name>\`.
- Auto-accepts the SSH host-key prompt; supports a custom `--ssh-key-file`.
- UTF-8 decoding with `errors="replace"` on all gcloud output (gcloud/pip output is not
  always cp1252-decodable on Windows).
- Per-operation timeouts (create 600 s, start/stop 300 s, install 1800 s, run 1800 s,
  download 300 s).

### 9.7 gcloud wrapper surface
`find_gcloud` (incl. `gcloud.cmd` on Windows), `active_account`, `get_project`,
`create_vm`, `find_instance` (handles blank/transitional status and duplicate results,
preferring RUNNING), `start_vm`, `stop_vm`, `delete_vm`, `ssh`, `scp` (with `--recurse`),
`project_state`, `billing_enabled`, `api_enabled`, `enable_api`, `gpu_quota_metric`
(L4/A100/T4/V100 + all-regions fallback), `list_region_gpu_quota`, `classify_create_error`.

### 9.8 Local GPU execution
- `--predictor evo` runs entirely on a local NVIDIA GPU (~24 GB) — no cloud involved.
- `cloudrun --prefer-local` **tries the local GPU first and falls back to the cloud on any
  failure** (no CUDA, evo/torch missing, OOM, anything) — the fallback never raises.
- The double-click wizard asks which to use; the frozen `.exe` bundles no torch, so it is
  cloud-only by design and skips the question.

---

## 10. Distribution & packaging

- **Two double-click Windows executables**, built by `packaging/build_exe.ps1` (PyInstaller,
  `--onefile --console`) and distributed via a GitHub Release:
  - **`keep-gpu.exe`** (~30 MB) — double-click once, leave open; brings up and holds the GPU.
  - **`dna-entropy.exe`** (~30 MB) — double-click, drop a file or paste a sequence.
- **Deliberately lightweight**: `torch`, `evo2`, `flash_attn`, and `pyrodigal` are excluded
  from the bundle (they run on the GPU box); **Biopython is bundled** with
  `--collect-submodules Bio` because GenBank/FASTA I/O runs locally and is lazily imported.
- The package source is bundled as `_pkgsrc` so the `.exe` can upload itself to the VM.
- Reproducible `.spec` files checked in for both executables.
- **Double-click wizard** (`packaging/launcher.py`):
  - With arguments → behaves exactly like the `dna-entropy` CLI.
  - Without arguments → friendly wizard accepting a **dropped file**, a **typed/pasted
    path**, or a **pasted DNA sequence** (written to a temp file); strips surrounding
    quotes; distinguishes "looks like a path but doesn't exist" from a pasted sequence and
    errors accordingly; asks local-vs-cloud when not frozen; **keeps the console window
    open** at the end and never crashes silently on a double-click.
- **Zero hosting cost to the author**: public Google DLVM image, public pip, Hugging Face
  weights; the user pays only for their own GPU time in their own account.
- `pip install` install paths: core (`numpy`, `typer`, `biopython`) plus optional extras
  `[dev]` (pytest), `[evo]` (torch, evo2), `[genes]` (pyrodigal), `[package]` (pyinstaller).
- Console-script entry point `dna-entropy = dna_entropy.cli:main`.

---

## 11. Architecture & engineering qualities

- **Strictly one-directional pipeline**, no back-edges:
  `readers → validation → predictors → analysis → writers`, with the optional annotator
  branching off to GFF3. `pipeline.py` is the only module that knows the stage order;
  `cli.py` only parses arguments.
- **Protocol-based interfaces** for `Reader`, `Predictor`, `Annotator`, `Writer` — every
  stage is independently swappable.
- **Model isolation enforced as a hard rule**: nothing outside `predictors/evo.py` may
  import `torch` or `evo2` or reference Evo token ids.
- **Lazy imports** of every heavy/optional dependency (evo2, torch, pyrodigal, Biopython)
  so the laptop/mock path stays light and fast.
- **Contract guard at the predictor boundary** on every run — it has already caught two
  real-model bugs (evo2's nested return tuple; uint8 tokenizer ids).
- **Stdlib dataclasses** for configuration (no extra dependency).
- **Type hints and docstrings on every public function and class**.
- **Explicit UTF-8 + LF** for all written files; ASCII-only console output.
- **Per-contig forward passes** with results concatenated for aggregate reporting.
- **Graceful degradation** — the bonus GenBank on the FASTA path, and best-effort Prodigal,
  never fail the core run.

---

## 12. Testing

- **127 tests** in the current repo (72 in the earlier snapshot), across 14 files mirroring
  the source layout.
- `pytest -m "not gpu"` runs everything on a GPU-less laptop; plain `pytest` adds the Evo
  GPU integration tests on a CUDA box.
- **`gpu` marker registered in `pyproject.toml`**; GPU tests skip themselves cleanly when
  no CUDA stack is present.
- Shared fixtures in `tests/conftest.py`; sample data in `tests/data/` (`sample.fasta`,
  `prokaryotic_demo.fasta`, `sample.gb`, `multi.gb`).
- **Deterministic by construction** — no network, no GPU, seeded mock predictor.
- Coverage by area:

| File | Tests | Area |
|---|---|---|
| `test_mock_predictor.py` | 12 | `(L,4)` shape, float32, row sums, determinism |
| `test_validation.py` | 16 | Normalization, alphabet, RNA, ambiguity, length, empty |
| `test_readers.py` | 4 | stdin/file reading |
| `test_entropy.py` | 7 | Uniform → 2.0, one-hot → 0.0, range, `0·log0` |
| `test_writers.py` | 8 | bedGraph/WIG/FASTA spec correctness |
| `test_pipeline.py` | 5 | End-to-end on mock; all expected files produced |
| `test_evo_logits.py` | 5 | Softmax, position shift, uniform row 0, single base |
| `test_evo_predictor.py` | 3 | *(gpu)* Real-model contract on a CUDA box |
| `test_gff.py` | 3 | GFF3 feature output |
| `test_annotator.py` | 3 | Real Pyrodigal gene calling |
| `test_genbank.py` | 15 | GenBank read/write, multi-record round-trip |
| `test_cloud.py` | 24 | gcloud wrappers, error classification, orchestration |
| `test_keeper.py` | 20 | Acquire/start/up state machine, backoff, health checks |
| `test_local_fallback.py` | 2 | `--prefer-local` success and fallback-to-cloud |

- Documented conventions: assert the **contract** (shapes, sums, ranges, file format), not
  the implementation; every behaviour change ships with a test in the same change.

---

## 13. Supporting scripts

| Script | Purpose |
|---|---|
| `scripts/demo.ps1` | Windows mock-predictor demo (plain locus + `--genes` prokaryotic ORF) with IGV loading instructions |
| `scripts/vm_setup.sh` | Idempotent Evo-stack install on a fresh DLVM, arch-auto-detected |
| `scripts/gpu_setup.sh` | Install evo2 into the system Python, verifying torch survives |
| `scripts/gpu_flashattn.sh` | Build flash-attn 2.8.3 from source for sm_89 (L4) against cu12.9 |
| `scripts/gpu_diag.sh` | Full GPU box diagnostic: nvidia-smi, python, torch/CUDA, pip packages, disk, RAM, build tools |
| `scripts/gpu_probe.sh` | Probe an unknown box's python/conda/torch layout |
| `scripts/evo_run.sh` | Batch-run every locus in a multi-record FASTA through real Evo, named by accession |
| `packaging/build_exe.ps1` | Build both Windows executables |

---

## 14. Documentation

| Document | Contents |
|---|---|
| `README.md` | What it does, input/output matrix, architecture, `.exe` quickstart, cloud setup, cost warning, local-GPU path, IGV and Geneious viewing walkthroughs, testing, layout |
| `CLAUDE.md` | Project rules: the 11 hard rules, architecture summary, environment notes, testing summary, status |
| `docs/DESIGN.md` | Goals/non-goals, pipeline diagram, module layout, the `Predictor`/`Writer`/`Annotator` contracts, position semantics, validation rules, full CLI spec, output specification, IGV output design, testing conventions, future swap points |
| `docs/EVO_SETUP.md` | Evo 2 install, GPU requirements and hardware rationale (7B/bf16 on a 24 GB card), manual VM provisioning reference, quota notes, debugging |
| `docs/DISTRIBUTION.md` | The two executables, the decentralized "we host nothing" model, keeper/client split rationale, local-GPU path, one-time user setup, component table |
| `docs/ROADMAP.md` | Sprint 0–5 checklists (all complete) plus the future backlog |
| `LICENSE` | MIT |

---

## 15. Explicit limitations and non-goals

- **Single-pass context cap of 8192 nt** — longer loci need windowing (not implemented).
- **Forward strand only** — no reverse-strand or both-strand entropy.
- **Prokaryotic gene calling only** — Prodigal is not accurate for eukaryotes.
- **No reference-genome mapping** — each sequence is treated as its own contig (`--start`
  exists to offset a track, but mapping onto an existing IGV reference is future work).
- **GenBank carries only coarse per-gene means** — it has no per-base numeric channel, which
  is why the full-resolution graph always ships as a companion track.
- **Multi-record FASTA analyzes the first record only** (multi-record **GenBank** is fully
  supported).
- **Ambiguity codes are kept, not modelled** — on the GenBank/FASTA path entropy at an `N`
  reflects the model's prediction, not a known base; the paste path rejects them.
- **Row 0 of every contig is uniform (2.0 bits)** — the first base has no preceding context.
- **No GUI, no persistence, no user accounts, no hosted inference API.**
- **The cloud VM bills continuously (~$0.74/h for an L4 ≈ $18/day)** by design, so runs are
  instant; the user must delete it manually when finished.
- **Not implemented (backlog)**: long-locus windowing, reverse strand, reference mapping,
  bigWig output, the ANN replacement for Evo, and a GUI.

---

## 16. Differences between the two code-bearing directories

`DNA-Entropy-Genbank` is a strict superset of `DNA-Entropy`. Present **only** in the
current repo:

- **All GenBank I/O** — `readers/genbank.py`, `readers/detect.py`, `readers/fasta.py`,
  `readers/input.py`, `writers/genbank.py`, multi-record support, `--informat`.
- **The Geneious writer** (`writers/geneious.py`) and the whole Geneious workflow.
- **The always-on GPU keeper** (`cloud/keeper.py`, `keep_gpu.py`, `keep-gpu.exe`,
  `dna-entropy keep-gpu`), the setup health check, the region quota pre-filter, the GPU
  health probe, and `enable_api` / `billing_enabled` / `project_state` /
  `list_region_gpu_quota` in the gcloud wrapper.
- **`--prefer-local`** local-GPU-first execution with automatic cloud fallback.
- **`allow_ambiguity`** lenient validation for real GenBank/FASTA files.
- **`write_multi` on every writer**, the `stats.txt` multi-record summary, and the
  `GffWriter` `source=genbank` path.
- **55 additional tests** (`test_genbank.py`, `test_keeper.py`, `test_local_fallback.py`).

Present **only** in the older `DNA-Entropy` ⚠️:

- **Ephemeral VM lifecycle in the client** — `cloudrun` created the VM itself and **deleted
  it by default** after the run, with a `--keep` flag to stop-and-preserve it instead
  (double-confirmed, with a ~$10/month disk-cost warning) and a confirmation prompt before
  deleting a reused box. This was deliberately removed in favour of the keeper/client split.

`DNA-Entropy-Graph` is an empty directory — it contains no code, documentation, or data.
