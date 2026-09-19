# The science and the output formats

This is the doc a biologist and a developer should both be able to read and then answer:
what does the entropy number mean, what exactly does the model see, what does each output
file contain, and which coordinate system does it use. If you are about to write a test
for `worker/src/dna_entropy/analysis/`, `writers/`, or `validation/`, read this first.

**Ported from:** `worker/docs-legacy/DESIGN.md`, the prototype's own design doc, which
remains untouched as the historical reference for the single-pass, no-windowing,
forward-only tool it described. This doc supersedes it for anything windowing- or
direction-related (spec section 5.6) and is the one that stays current. Everything below
that is a plain restatement of DESIGN.md is marked as such; everything from spec 5.6
onward is new.

**Source of truth for the numbers below:** `worker/src/dna_entropy/` as checked out in
this repo, read on 2026-09-19 (`predictors/base.py`, `analysis/entropy.py`,
`analysis/windowing.py`, `analysis/direction.py`, `validation/validators.py`,
`writers/*.py`, including `writers/tsv.py`). Windowing, direction, and the TSV writer,
each once tracked here as "not yet built," are all implemented and tested as of this
revision; where this doc still describes something not yet built, it says so explicitly
rather than by omission.

---

## 1. The model boundary: the `(L, 4)` contract

Every predictor - the deterministic `MockPredictor` used in every non-GPU test, the real
`EvoPredictor` (Evo 2, 7B by default), and any future trained model - implements the same
`Predictor` protocol:

```python
def predict(self, seq: str) -> np.ndarray:
    """seq: validated, uppercase A/C/G/T string of length L.
    Returns: (L, 4) float32 array, columns [A, C, G, T] (NUCLEOTIDES, fixed order),
    each row summing to 1.0."""
```

MEASURED 2026-09-19 (`worker/src/dna_entropy/predictors/base.py`): the column order
`NUCLEOTIDES = ("A", "C", "G", "T")` is fixed across the whole codebase and must never be
reordered; `check_probability_matrix` guards this boundary at runtime (shape, dtype
`float32`, values in `[0, 1]`, each row sums to `1.0` within `1e-4`) and raises
`ValueError` naming exactly what failed. This is the one place downstream code trusts
without re-checking, and the one place a real-model bug has actually shown up twice
(CLAUDE.md's Critical Pitfalls: evo2 returning a nested tuple, and the tokenizer's ids
coming back as `uint8` - both caught by this guard).

This is why the model is a swap point and nothing else is: entropy, windowing, direction
combination, and every writer consume only this array. None of them know or care whether
it came from a lookup table, Evo 2, or a future ANN.

### Position semantics (read this before touching `analysis/` or `predictors/`)

Evo 2 predicts the *next* token, autoregressively. MEASURED (prototype Sprint 3, on a GCP
L4; recorded in `CLAUDE.md`'s Critical Pitfalls - the exact calendar date was not
preserved in this repo's history): Evo 2's tokenizer adds **no BOS token**, so feeding it
`s_1 .. s_L` gives exactly `L` output rows, and row `i` (0-indexed) is the model's
predicted distribution for base `i` given bases `0 .. i-1`. Row `0` has seen nothing, so
in Forward-only mode it is exactly uniform - entropy `2.0` bits by design, not a bug. Do
not special-case it. `MockPredictor` reproduces the same `(L, 4)` shape directly, with no
BOS question to resolve, so mock-only tests cannot catch a BOS-handling regression - only
`test_evo_predictor.py` (`gpu`-marked) can.

---

## 2. Shannon entropy: what the number means

```python
def shannon_entropy(probs: np.ndarray) -> np.ndarray:
    """probs: (L, 4) -> entropy (L,) in bits, each value in [0.0, 2.0].
    H = -sum(p * log2(p)), with 0 * log2(0) := 0."""
```

MEASURED 2026-09-19 (`worker/src/dna_entropy/analysis/entropy.py`): for a 4-symbol
alphabet, maximum entropy is `log2(4) = 2.0` bits exactly (`MAX_ENTROPY_BITS`), reached
when the model is completely uncertain (uniform `0.25` across A/C/G/T - including row 0
above). Minimum is `0.0` bits, reached when the model is completely certain (one base has
probability `1.0`). A biologist reading a track should read **low entropy as "the model
is confident, this is a predictable/constrained region"** (e.g. inside a well-conserved
gene) and **high entropy as "the model is uncertain here"** (e.g. wobble positions,
intergenic sequence, or the un-contexted start of a Forward-only run). The `0 * log2(0) :=
0` convention is standard information theory (a zero-probability symbol contributes no
uncertainty) and is implemented by computing `log2` only where `p > 0`, per the source.
`EntropySummary` (`length, mean, minimum, maximum, argmin, argmax`) reports `argmin`/
`argmax` as **0-based array indices into the entropy track**, not 1-based genomic
positions - add the run's `start` coordinate (see section 5) before quoting one to a user
against a genome browser.

Entropy is a property of the *distribution*, not of any single base's identity. This
matters for direction (next section): the reverse-complement pass predicts a different
base at each position than the forward pass does, but if the model's confidence about
"what goes at this position" is the same either way, the entropy value is the same either
way too, even though the predicted identity is not - see section 3.

---

## 3. Long sequences: context length, windowing, and bidirectional prediction

**Status: implemented (worker issue #279, closed).** `analysis/windowing.py` and
`analysis/direction.py` satisfy the specification below; MEASURED 2026-09-19 against the
committed source, 381+ tests passing. This landed with one real bug along the way,
found by reading rather than by a failing test: `windowing.halved()`'s OOM-retry only
actually shrank the window when the ceiling, not the context length, was the binding
constraint, so the retry silently re-ran the identical window shape on any GPU tier with
headroom to spare (issue #313, fixed; the Pushback table's OOM row below now describes
the fixed behaviour). The single-pass, forward-only prototype behaviour described in
`worker/docs-legacy/DESIGN.md` is superseded for anything windowing- or direction-related.
See the [design spec, section
5.6](superpowers/specs/2026-09-18-dna-entropy-graph-design.md#56-context-window-and-bidirectional-prediction-owners-three-points-2026-09-18-confirmed-2026-09-19)
for the full owner rationale; this section restates only what a reader of `docs/` needs
without re-deriving it, plus one worked example the spec doesn't spell out numerically.

**Why this exists:** a single forward pass has two problems the prototype accepted and
the product does not. First, VRAM bounds how much sequence one pass can see (~8,192 nt on
an L4 for the 7B model), so anything longer needs tiling. Second, forward-only prediction
means the first `K` bases are always predicted with less than full context - down to zero
context at base 0 - which the owner explicitly asked to fix: predict the *tail* from a
forward read and the *head* from a reverse read, so accuracy does not degrade near either
end.

**Definitions:**
- `K` (context length, user-settable, default `4,096`): the amount of sequence the model
 must have seen before a prediction at a position is trusted.
- `W` (window) `= min(2K, GPU ceiling)`; `S` (stride) `= W - K`. Windows start at
 `0, S, 2S, ...`; the last window is right-aligned so it is never short.
- Passes per direction `= ceil((L - W) / S) + 1`, or `1` when `L <= W`.
- A base contributed by a window has between `K` and `W` bases of context *in that
 direction*. This is the cheap equivalent of a true per-base rolling window (one forward
 pass per base), which is explicitly rejected - the hard rule is one forward pass per
 window, never per position (CLAUDE.md rule 4).

**Worked example** (not in the spec body, added here because the formula alone under-
specifies the shape): `L = 30,000`, `K = 4,096`, GPU ceiling `8,192` (L4, 7B). Then
`W = min(8,192, 8,192) = 8,192`, `S = 8,192 - 4,096 = 4,096`. Passes per direction
`= ceil((30,000 - 8,192) / 4,096) + 1 = ceil(5.32) + 1 = 6 + 1 = 7`. That is 7 forward
passes and 7 reverse-complement passes, 14 total, each an 8,192 nt forward pass on the
GPU - matching the spec's own cost note ("a 30 kb sequence at K = 4,096 is about 12
forward passes of 8,192 nt" for the combined default, which counts overlap trimming
differently; treat both as THEORY (unverified) until measured on real hardware, since
neither number has run on a GPU yet).

**Directions:**
- **Forward**: base `i` predicted from bases `< i` (exactly the prototype's only mode).
- **Reverse**: run on the **reverse complement** of the sequence, then map index
 `i -> L-1-i` back onto the original coordinate frame. Base `i` is thus effectively
 predicted from bases `> i`. Feeding the model the sequence spelled backwards (not
 complemented) is not DNA and is a distinct bug from what this does - CLAUDE.md's
 Critical Pitfalls names this exact confusion. Entropy of the complement distribution
 equals entropy of the base distribution (complementation is a bijection on the four
 symbols, so it does not change how spread-out the distribution is), which is what makes
 a reverse-pass entropy value directly comparable to a forward-pass one at the same
 genomic position.

**Combining the two passes** (`RunOptions.Direction`, default **Both, combined**):
- **Both, combined**: base `i` takes the forward estimate when it has `>= K` bases of
 context before it, else the reverse estimate when it has `>= K` bases after it, else
 whichever direction has more context (only possible when `L < 2K`; recorded as "reduced
 context" in provenance). With `L >= 2K` this reduces exactly to the owner's recipe: the
 first `K` bases come from the reverse read, the rest from the forward read, with one
 **seam** at position `K` - drawn as a marker in the Results viewer and recorded in
 `provenance.json`.
- **Both, averaged**: mean of forward and reverse wherever both have `>= K` context, same
 fallback as above elsewhere.
- **Both, separate tracks**: emits `.entropy.fwd.*` and `.entropy.rev.*` alongside the
 combined track - three full sets of the position-indexed outputs in section 5.
- **Forward only** / **Reverse only**: single direction. Forward-only reproduces the
 prototype's output within a `1e-6` floating-point tolerance, including the uniform,
 2.0-bit first base — **not** exact bit-identity. MEASURED 2026-09-19 (issue #316): the
 comparison originally asserted exact equality against a real, recorded prototype fixture
 (the actual prototype, run for real, 201 values) and passed locally, then failed on CI's
 Linux runner because `log2` can differ in its last bit across numpy/libm builds on
 different platforms. "Bit-for-bit" was never a promise this pipeline could actually keep;
 the tolerance is three orders tighter than the project's own `1e-3` direction-parity bar,
 and the test name no longer overclaims.

**Pushback (validated locally in the app before any VM is created; the worker
re-validates the same rules, since the app's check is a convenience, not the boundary):**

| Condition | Result |
|---|---|
| `W` above the GPU ceiling for the selected model/GPU | Warn; offer **Clamp to `<ceiling>`** or **Use a bigger GPU** (never silently clamp) |
| `K < 1,024` | Warn: "predictions near a window edge are dominated by the model's prior; results may be noisy" |
| `K < 128` | Refuse, with the exact reason |
| `L < K` | Warn: "this sequence is shorter than the context length, so no base reaches full context; results are still produced" |
| `L < 10` nt | Refuse (prototype's existing minimum-length rule, section 4 below) |
| `K > L/2` on a long sequence | Allowed; it just means fewer windows |
| OOM at runtime | Halve `W` (keep `K` if possible, else halve both), log a notice, retry once; final values recorded in provenance |

**Cost:** bidirectional prediction doubles GPU time relative to Forward-only, since it is
two full passes over the sequence instead of one. The default is **Both, combined** - the
owner's explicit choice, in exchange for the accuracy gain at both ends of the sequence.

**Verify on the first real GPU session** (THEORY, unverified until then, per the spec):
Evo 2 was trained on both strands, so forward and reverse-complement entropy
distributions should be statistically similar on the same locus. The GPU acceptance
checklist (`docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md` section 9)
compares them and checks the seam at position `K` for a jump larger than typical
neighbour-to-neighbour variation - a large jump would indicate a direction-combination
bug, not a real biological feature.

---

## 4. Validation rules

MEASURED 2026-09-19 (`worker/src/dna_entropy/validation/validators.py`). Applied before
any predictor runs (CLAUDE.md rule 2) - the app re-applies the same rules locally (a C#
port) before it creates a single cloud resource, so a user never pays for a VM to learn
their file has a bad character in it.

**Normalize (in order):** strip a single leading FASTA-style `>` header line if present
(noticed, not an error); remove all whitespace; remove digits (handles pasted line
numbers); uppercase.

**Encoding (issue #294, MEASURED 2026-09-19):** all three input paths decode with
`errors="replace"` rather than raising `UnicodeDecodeError` on the first non-UTF-8 byte —
`readers/paste.py`'s file and stdin branches now match `readers/fasta.py` and
`readers/detect.py`'s existing behaviour, so a file with a stray non-UTF-8 byte (a
Windows-1252 export, a copy-paste artefact) reaches the normal validation-stage checks
above instead of crashing the process outright.

**Checks, in order, fail fast with the first offending position:**
1. **RNA**: if `U` is present and `--rna`/`Treat as RNA` is off, raise with the exact
 1-based position of the first `U` and a suggestion to turn the option on. With the
 option on, every `U` is converted to `T` and the count is recorded as a notice.
2. **Empty**: raise if nothing is left after cleaning.
3. **Alphabet**: only `A C G T` pass without any special handling. An IUPAC ambiguity code
 (`N R Y S W K M B D H V`) is handled per **`ambiguityPolicy`** (manifest
 `inputs[].ambiguityPolicy`; CLI `--ambiguity`; default **`keep`**) — MEASURED 2026-09-19,
 issue #249. A character that is neither `A/C/G/T` nor a recognized IUPAC code is
 **always** an error regardless of policy, reporting the first offending character, its
 1-based position, and the total count of bad characters; that is a genuinely invalid
 character, not an ambiguity question.

 **What does the entropy value mean at a position that was an N?** The raw sequence,
 ambiguity code and all, goes straight to Evo 2's tokenizer under `keep` — the `(L, 4)`
 output is still well-formed (section 1's contract holds regardless), but the *input
 token* at that position is one the model almost certainly saw far less often during
 training than a real base: routinely, for `N` (real assemblies are full of `N`-run gaps),
 and rarely to never for the other ten IUPAC codes (used for degenerate/heterozygous
 positions, e.g. primer design). So the entropy value at an ambiguous position is not "how
 sure is the model this position is a definite base" the way it is everywhere else on the
 track — it is "what did the model do with a token it may have rarely or never seen."
 Read an ambiguous stretch's entropy with that caveat, not as an ordinary base-confidence
 reading.

 The three policies:
 - **`keep`** (default) — the code is fed to the predictor exactly as written, per the
   paragraph above. Most information-preserving: the entropy at that position reflects
   the model's response to that *specific* token. This is also, undocumented until now,
   a real behaviour change from the prototype-parity default this doc previously
   described: the paste path used to have a stricter *implicit* default (ambiguity codes
   were never tolerated for a pasted/typed sequence at all, because the old
   `allow_ambiguity` boolean was simply never passed for that path) while GenBank/FASTA
   input silently allowed them; all three input paths now share the same explicit
   `keep` default.
 - **`mask`** — every ambiguity code is normalized to a single canonical `N` before the
   predictor sees it (a notice records which original codes were present and how many).
   Trades away which specific code was originally there for more consistent behaviour,
   since `N` is the one non-ACGT token a real genomic model is most likely to have a
   well-defined response to.
 - **`error`** — the run refuses outright if the input contains any ambiguity code,
   naming the first one, its position, and the total count. For a user who needs every
   position to be a genuine, unambiguous base call, or who wants to be forced to decide
   rather than have the tool decide quietly.

 **A real "wired to nothing" bug this replaces**: the old `allowAmbiguity` manifest field
 was parsed but never actually consulted downstream — `readers/input.py` hardcoded
 `True` for GenBank/FASTA regardless of what the manifest said, and never passed anything
 for the paste path. An app declaring `allowAmbiguity: false` had zero effect. `job_contract.md`
 §3 covers the wire-format side of this fix (the field rename and the old field's
 now-honest no-op tolerance); this section is the science-meaning side.
4. **Length vs. the whole-input cap**: MEASURED 2026-09-19 — every `validate_sequence(...)`
 call site passes `max_len=cfg.max_total_len` (default 10,000,000 nt; `config.py`'s own
 "outer sanity bound on total input length"), **not** `cfg.max_len` (default 8,192 nt,
 the GPU per-window ceiling from section 3). `cfg.max_len` never rejects a sequence on
 its own — windowing tiles anything longer into multiple `<= cfg.max_len` passes
 (Hard Rule 4), so treating it as an input-rejection cap would break the exact inputs
 windowing exists to serve. For a single sequence (the paste path) this one call already
 is a whole-input check. For a multi-record GenBank/FASTA file (issue #314), `readers/
 input.py` additionally sums every record's validated length as it reads them and raises
 the moment the running total exceeds `cfg.max_total_len` — a per-record-only check would
 let a file many times over the cap through, one small record at a time, since a record's
 own length was always checked against the same generous whole-input bound, never against
 the sum. Over the cap (either the single-sequence or the summed-across-records case):
 raise, naming the length reached and the configured cap, and by how much it is over.
5. **Minimum length**: below 10 nt, warn (not fail) that entropy near the start will be
 dominated by the model's prior.

**GenBank gene features: compound (spliced) locations** (issue #295, MEASURED
2026-09-19). `readers/genbank.py` maps each `gene` (or, failing that, `CDS`) feature to a
single `GeneFeature(begin, end, strand, ...)`. For a `join(...)` /
`complement(join(...))` (spliced/multi-exon) location, Biopython's own `CompoundLocation`
reports `.start`/`.end` as the outer bounding min/max across every exon segment — **not**
the union of the exon spans — so `begin..end` includes the intron sequence between exons.
`GeneFeature` has room for exactly one span (it is owned by `annotators/base.py`, outside
this reader's own files), so the reader still reports that bounding box, but it is never
silent about it any more: every compound-location feature raises a notice naming its
exact exon segments (sorted by genomic position, not transcript order), e.g. `GenBank
feature 'geneX' has a compound (spliced) location with 2 segments (100..200, 400..500)`.
Any consumer of a spliced gene's mean-entropy figure (`writers/genbank.py`'s `/note=` on
the output `.gb`) should read it as diluted by intron bases until a per-exon
representation ships — see the tracked `DECISION` issue on `GeneFeature.exons`.

Every one of these five checks has a dedicated, deterministic test in
`worker/tests/test_validation.py`; `docs/tests.md`'s neighbour-test set names the shapes a
new validation test should also sweep (empty contig, ambiguity codes, multi-record files,
`L < 2K`, `L > W`).

---

## 5. Output formats, one per row, with the coordinate system that matters

**The classic genomics bug this table exists to prevent:** bedGraph is **0-based,
half-open** (`[start, end)`, matching BED); everything else this tool writes - WIG
`fixedStep`, both GFF3 flavours, and the way a GenBank flat file *displays* its feature
locations - is **1-based, inclusive**. Mixing the two up by one position is the single
easiest coordinate bug to write and the hardest to notice, because it is off by exactly
one base at the edges and looks fine everywhere else. If you write a new format or a new
coordinate transform, write the test that would catch an off-by-one before you write the
transform.

| File | Format | Coordinates | What it contains | Source |
|---|---|---|---|---|
| `<name>.fasta` | FASTA | n/a (sequence only) | The full sequence as a single contig named `<name>` (or one record per input record for multi-record/GenBank input). This is the "genome" every other track lines up against in the viewer. | MEASURED, `writers/fasta.py` |
| `<name>.gb` | GenBank flat file | **1-based, inclusive** on the flat-file display; the writer's `GeneFeature` input and Biopython's internal `FeatureLocation` are both 0-based half-open, converted at write time (`begin0 = f.begin - 1`) | For GenBank input: the record's **own, authoritative** genes, each carrying a `/note="mean_entropy=<x> bits"` qualifier (the coarse per-gene summary). For FASTA/paste input with `--genes`: Prodigal-predicted genes, same qualifier. GenBank has no per-base numeric channel, so this file never carries the full-resolution track - that is always a companion file below. | MEASURED, `writers/genbank.py` |
| `<name>.entropy.bedgraph` | bedGraph | **0-based, half-open**: row `chrom start end value` where base `i` (0-based) covers `[start_coord - 1 + i, start_coord + i)` | Full-resolution per-position entropy, one row per base per direction requested. `chrom` matches the FASTA contig name so IGV lines the track up automatically. IGV's default track for this data. | MEASURED, `writers/bedgraph.py` |
| `<name>.entropy.wig` | WIG (`fixedStep`) | **1-based**: `fixedStep chrom=<name> start=<start> step=1 span=1`, then one value per line | The same full-resolution entropy track as bedGraph, in the alternate WIG format some tools prefer. | MEASURED, `writers/wig.py` |
| `<name>.entropy.geneious.gff3` | GFF3 | **1-based, inclusive**: one 1 bp feature per position, `pos = start - 1 + i + 1` | Full-resolution entropy as a GFF3 feature track, because **Geneious Prime imports GFF3 but not WIG or bedGraph as a graph track** (in Geneious those are export-only, from the Graphs tab). Each feature carries the entropy value in both the GFF3 score column and an `entropy` qualifier; shade the track with Geneious's *Color by / Heatmap* on either. | MEASURED (prototype team; recorded in `worker/docs-legacy/DESIGN.md` and the docstring of `writers/geneious.py`) |
| `<name>.genes.gff3` | GFF3 | **1-based, inclusive**: `f.begin + offset` .. `f.end + offset` where `offset = start - 1` | Gene features on the same contig and coordinate frame as the entropy track: the input's own genes for GenBank input (`source=genbank`, written only when the input actually carries genes), or Prodigal's predictions for FASTA/paste input with `--genes` on (`source=pyrodigal`). | MEASURED, `writers/gff.py` |
| `stats.txt` (GenBank input) / `<name>.summary.txt` (FASTA/paste input) | plain text | n/a | Per-contig `EntropySummary`: length, mean, min, max, and the 0-based array index of each extreme (see section 2 - add `start` to get a genomic position). | MEASURED, `writers/summary.py` |
| `<name>.entropy.tsv` | TSV | **1-based, inclusive**, matching WIG and both GFF3 flavours - see note below | Per-position entropy in a plain, spreadsheet-friendly table, one row per base. | MEASURED 2026-09-19, `writers/tsv.py` |

**A note on the Entropy TSV's shape (issue #281/#46, closed):** this doc proposed the
convention before `TsvWriter` existed, so the first implementation had a shape to land in
rather than inventing one at code-review time; the shipped writer confirms it exactly, no
changes. Columns: `position\tbase\tentropy_bits` for `Forward only`/`Reverse only`/
`Both, combined`/`Both, averaged` runs, or
`position\tbase\tentropy_fwd\tentropy_rev\tentropy_combined` for `Both, separate tracks`
(one sheet with three number columns to compare directly, rather than three separate
files - bedGraph/WIG do get separate `.fwd`/`.rev` files, since those serve a viewer, not
a human reading rows). **Coordinates: 1-based, inclusive**, matching WIG and both GFF3
flavours (the majority of this table) rather than bedGraph's half-open convention, on the
reasoning that a spreadsheet-opened TSV is read directly against 1-based GenBank/UniProt-
style positions by a biologist, not fed to a half-open-aware parser. A multi-record file
stays exactly 3 (or 5) columns rather than growing a `contig` column: each contig's block
is introduced by a `# contig: <name>` comment line, and position numbering restarts at
`start` for each contig, the same per-contig coordinate frame bedGraph/WIG already use.

**Multi-record input**: per spec decision D14, all FASTA records are processed (the
prototype processed only the first); GenBank multi-record input was already handled via
`write_multi` on every writer above, one block per record, correctly keyed by contig name
so the viewer and IGV keep every record's track aligned to its own sequence.

---

## 6. Viewer support matrix

| Viewer | Loads | Notes |
|---|---|---|
| IGV (igv.js, embedded; and desktop IGV via the batch port) | FASTA (as the loaded genome), bedGraph or WIG (bar graph track), gene GFF3 | This is the primary target; the file set and coordinate conventions above exist specifically to be IGV-loadable with no conversion step. MEASURED (prototype team; `worker/docs-legacy/DESIGN.md` section 7). |
| Geneious Prime | GenBank (sequence + per-gene mean-entropy note), Geneious entropy GFF3 (per-position, via *Color by / Heatmap*) | **Does not** load bedGraph/WIG as a graph track - this is exactly why the Geneious GFF3 exists as a separate file. MEASURED (prototype team; see the format table above). |
| SnapGene | GenBank (sequence + per-gene mean-entropy note) | THEORY (unverified): SnapGene's plasmid/sequence-editor view is expected to show the `/note` qualifier like any other GenBank annotation, but this has not been confirmed against a real SnapGene install in this repo's history. No per-base track is offered to SnapGene; only the coarse per-gene mean travels there. |
| Benchling | GenBank (sequence + per-gene mean-entropy note) | THEORY (unverified), same basis as SnapGene: GenBank import is Benchling's standard path, but this tool's specific output has not been confirmed against a live Benchling account. |

---

## 7. What is explicitly out of scope here (and where it is tracked)

- No reference-genome mapping: a pasted or FASTA sequence is treated as its own contig,
 not mapped onto an existing assembly. `--start` only offsets the emitted coordinates;
 it does not look anything up. Tracked for post-v1 (spec section 9).
- No eukaryotic gene prediction: Pyrodigal is prokaryotic-only; FASTA/paste `--genes`
 output should not be trusted for eukaryotic input.
- No bigWig output (post-v1; bedGraph/WIG cover the same viewers today).
- The ANN predictor that eventually replaces Evo 2 is a future swap behind the same
 `(L, 4)` contract in section 1 - nothing in this doc changes when that happens.

## Related

[`job_contract.md`](job_contract.md) (how `analysis.contextLength`/`window`/`stride`/
`direction` travel in `manifest.json`), [`cloud_design.md`](cloud_design.md) (where the
worker that runs this analysis executes), `worker/docs-legacy/DESIGN.md` (the prototype's
own account, single-pass and forward-only), CLAUDE.md rules 1, 3, 4, 5 (the science hard
rules this doc explains the rationale for).
