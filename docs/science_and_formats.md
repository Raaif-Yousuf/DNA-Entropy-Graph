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
`float32`, explicit NaN and inf checks with offending row indices, values in `[0, 1]`,
each row sums to `1.0` within `1e-4`) and raises `ValueError` naming exactly what failed
(distinguishing GPU predictor faults like NaN and inf from normalization or range errors).
This is the one place downstream code trusts without re-checking, and the one place a
real-model bug has actually shown up twice (CLAUDE.md's Critical Pitfalls: evo2 returning
a nested tuple, and the tokenizer's ids coming back as `uint8` - both caught by this guard).

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

## 2b. Surprisal: what the model thought of the base that is actually there

**Status: implemented (issue #123, closed).** `analysis/surprisal.py` computes it;
`analysis/direction.py` combines it by the identical forward/reverse selection rule as
entropy (section 3); every writer in section 5 emits it when `include_surprisal` is on
(the default). `--surprisal`/`--no-surprisal` on the CLI, `include_surprisal` on
`RunConfig`.

```python
def surprisal(probs: np.ndarray, seq: str) -> np.ndarray:
    """probs: (L, 4), seq: length-L actual sequence -> surprisal (L,) in bits.
    S[i] = -log2(P(actual base at i)), clamped to [0.0, MAX_SURPRISAL_BITS=20.0]."""
```

Entropy says how uncertain the model was **before** seeing the real base; surprisal says
how surprised it was **after**. They usually move together, but a position where they
diverge - **low entropy (the model was confident) and high surprisal (it was confident and
WRONG)** - is exactly the mutation-spotting signal this issue asks for: the model expected
a conserved base here and the real sequence has something else. A biologist scanning for
that pattern wants both tracks loaded side by side, not entropy alone.

Two decisions worth naming:

- **Unbounded above, unlike entropy's `[0.0, 2.0]`.** As `P -> 0`, `-log2(P) -> infinity`;
  `MAX_SURPRISAL_BITS = 20.0` (`P <= 2**-20`, far below any real Evo prediction) clamps
  this to a finite, documented ceiling rather than leaving a viewer axis to whatever a
  run's data happens to contain. A value pinned at the ceiling means "the model was
  confident and wrong beyond what this track bothers to distinguish further," not a
  literal probability.
- **An IUPAC ambiguity code (`N`, `R`, `Y`, ...) has no single "actual base" to index into
  the `(L, 4)` contract.** DECISION (agent-made, reversible): falls back to the row's
  Shannon entropy there, which is not an arbitrary choice - entropy IS the model's own
  EXPECTED surprisal (`entropy = E_p[-log2 p]` by definition), so it is the mathematically
  principled value to report when the true base is unknown, and it stays bounded in
  `[0.0, 2.0]` like the rest of the ambiguous-position handling in section 4, rather than
  requiring a new ambiguity-set decode table this issue never asked for.

Row 0 in Forward-only mode is uniform (`2.0` bits, section 3) by design, so surprisal
there is exactly `2.0` for ANY actual base - not a bug to special-case away.

`SurprisalSummary` (`length, mean, total_log_likelihood_bits, defined_count`) mirrors
`EntropySummary`'s per-contig role: `mean` is over the WHOLE track (ambiguity-fallback
positions included, matching `EntropySummary`'s own convention), while
`total_log_likelihood_bits = sum(log2 P(actual base))` and `defined_count` count only
positions with a real A/C/G/T (an ambiguity-fallback position is not a real observation of
a specific symbol, so it contributes no log-likelihood term). `stats.txt`/`<name>.summary.txt`
report both when `include_surprisal` is on (section 5).

Zero extra GPU cost: surprisal is computed from the SAME `(L, 4)` probability matrix and
the SAME predictor calls entropy already used - no second forward pass, no extra
windowing (Hard Rule 4 is unaffected either way).

**Known scope limit (not a bug, tracked separately):** the TSV's `Both, separate tracks`
5-column shape (section 5) does not yet grow forward/reverse surprisal columns - only the
plain (3/4-column) shape does. `DirectionResult` deliberately has no
`forward_surprisal`/`reverse_surprisal` pair (unlike `forward_values`/`reverse_values`):
an early draft of this wiring added one, `scripts/check_unused_fields.py` immediately
flagged both fields as genuinely unread (no writer consumed them), and they were removed
rather than left "for a future writer" - the exact bug class this repo hunts, caught by
its own guard within the same session. A future writer that wants per-direction surprisal
computes it locally from the already-available `fwd.probs`/`rev.probs`.

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
 Ambiguity codes are complemented too (`R<->Y`, `K<->M`, `B<->V`, `D<->H`; `N`, `S`, `W`
 are their own complement): under `ambiguityPolicy=keep` the code reaches the model, so
 the reverse pass must hand it the complemented code (**Fixed 2026-10-02, issue #78**: it
 used to be reversed but left uncomplemented). `mask` rewrites every code to `N` during
 validation, before any windowing, so the reverse pass only ever sees `N` there.

**Combining the two passes** (`RunOptions.Direction`, default **Both, combined**):
- **Both, combined**: base `i` takes the forward estimate when it has `>= K` bases of
 context before it, else the reverse estimate when it has `>= K` bases after it, else
 whichever direction has more context (only possible when `L < 2K`; recorded as "reduced
 context" in provenance: `reduced_context_count` plus `reduced_context_range`, the 0-based
 half-open `[start, end)` span of those positions, `null` when there are none; issue #79).
 With `L >= 2K` this reduces exactly to the owner's recipe: the
 first `K` bases come from the reverse read, the rest from the forward read, with one
 **seam** at position `K` - drawn as a marker in the Results viewer and recorded in
 `provenance.json`.
 **After an out-of-memory retry** (issue #456, fixed 2026-10-02) a pass may run with a
 smaller `K` than configured; each pass is judged against the `K` it actually ran with, so
 the seam is the **forward pass's actual `K`** (and exists only when
 `L >= forward K + reverse K`). The run notices say when a pass ran with a reduced `K`.
- **Both, averaged**: mean of forward and reverse wherever both have `>= K` context, same
 fallback as above elsewhere.
- **Both, separate tracks**: emits `.entropy.fwd.*` and `.entropy.rev.*` alongside the
 combined track - three full sets of the position-indexed outputs in section 5. The
 bedGraph/WIG and the Geneious GFF3 get separate `.fwd`/`.rev` files (the Geneious ones are
 `<name>.entropy.fwd.geneious.gff3` / `.rev.`, issue #79, gated by the `geneious_gff3`
 output); the TSV carries all three tracks in one file. The GenBank, FASTA and stats files
 carry the combined track only.
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
re-validates the same rules, since the app's check is a convenience, not the boundary;
the worker checks every record of a multi-record input before predicting the first, issue #80):**

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

**Circular molecules (plasmids; issue #128).** On a circular molecule base 1 follows the
last base, so a linear read throws away real context at both ends (Forward-only gives base
1 zero context, the uniform 2.0-bit row). `RunOptions.Topology` (`auto | linear | circular`,
default `auto`; CLI `--topology`, manifest `analysis.topology`) fixes that: a circular contig
is wrapped around by `K` bases on each side (its own last `K` bases in front, its own first
`K` behind; a molecule shorter than `K` wraps more than once) **before** the tiled passes, and
the outputs are trimmed back to `L` afterwards. Everything else is unchanged (one forward
pass per window of the `L + 2K` padded sequence, reverse = reverse complement, the same
combination rule), and because every kept base then has `>= K` context in both directions
there is **no seam and no reduced context**: `provenance.json` records `seam: null`,
`reduced_context_count: 0`, `reduced_context_range: null`. Cost: `2K` extra bases through
each pass. `auto` resolves per contig: a GenBank `LOCUS` line saying `circular` is circular,
everything else (`linear`, FASTA, pasted text) is linear; `linear`/`circular` override the
input. `provenance.json` records the requested option (`run.topology`) and what each contig
actually ran with (`contigs[].topology`: `linear | circular`). A circular molecule shorter than `K` is
allowed (it wraps several times) and gets a notice saying so instead of the linear "no base
reaches full context" one. The mock predictor seeds on
window content, so rotation invariance (rotating a circular input rotates the output) is
tested with a pure-local-context stub (`worker/tests/test_circular.py`), not the mock. The
output GenBank carries the topology each contig was analyzed with (issue #524): a circular contig
writes `circular` in its `LOCUS` line (so a plasmid stays a plasmid in SnapGene/Benchling and
`read_genbank` reads it back as circular); a linear one writes no topology word, exactly as before,
so every linear file is byte-identical. `--topology` decides, not only the input's word.

---

## 4. Validation rules

MEASURED 2026-09-19 (`worker/src/dna_entropy/validation/validators.py`). Applied before
any predictor runs (CLAUDE.md rule 2) - the app re-applies the same rules locally (a C#
port) before it creates a single cloud resource, so a user never pays for a VM to learn
their file has a bad character in it.

**Normalize (in order):** strip a single leading FASTA-style `>` header line if present
(noticed, not an error); remove all whitespace; remove digits (handles pasted line
numbers); uppercase.

**Encoding (issues #294, #330, MEASURED 2026-09-19):** every reader (`paste.py`,
`fasta.py`, `genbank.py`, `detect.py`'s content sniff) decodes through the single shared
`readers/encoding.py` rather than each calling `Path.read_text()` (or, for GenBank,
letting Biopython open the file itself) independently:
- A UTF-8 byte-order mark is stripped before anything else sees the text — Windows
  Notepad's default "UTF-8" save option adds one, and it used to decode into a literal
  `﻿` character that defeated every `>`/`LOCUS` check and validation's own alphabet
  check alike, rejecting an otherwise perfectly valid file.
- A UTF-16 byte-order mark (either byte order) is decoded in full as UTF-16, not
  refused — this is exactly what Notepad's "Unicode" save option produces, an ordinary
  way to save a plain-text file on Windows, and decoding it costs nothing over a clean
  refusal.
- Anything else decodes as UTF-8 with `errors="replace"`: a non-UTF-8 byte (a
  Windows-1252 export, a copy-paste artefact) becomes a single `�` replacement
  character and reaches the normal validation-stage checks above as an ordinary invalid
  character, rather than crashing the process with `UnicodeDecodeError`.

Routing GenBank through the same shared decoder (rather than handing Biopython a raw
path) also fixed a second, unrelated inconsistency: Biopython's own file-opening used
Python's default text-mode encoding, which is the OS locale encoding — cp1252 on this
Windows dev box, MEASURED 2026-09-19 — silently different from the other two readers'
forced UTF-8, and would have decoded differently again on a UTF-8-locale Linux box.
GenBank input now agrees with FASTA and paste on encoding, everywhere.

**A replacement character gets its own diagnosis (issue #348, MEASURED 2026-09-19):**
`validate_sequence`'s alphabet check special-cases the literal `�` character (Unicode's
own "could not decode" signal, and never something a biologist types) — instead of the
generic "Invalid character" wording it gets for a real typo, the error names the actual
cause ("could not be decoded as text... the file was not saved as UTF-8") and the fix
(re-save the file as UTF-8), naming the position and count the same way every other
alphabet error does.

**Malformed GenBank input never reaches the caller as a raw traceback (issue #349,
MEASURED 2026-09-19):** `read_genbank` wraps the call into Biopython's own parser in a
`try/except ValueError` (every failure mode Biopython's `Bio.GenBank.Scanner` raises for
malformed content, including its own `ParserFailureError`, is a `ValueError` or a
subclass of one) and re-raises as `GenBankReadError`, agreeing with `read_fasta`'s own
blanket guarantee that a malformed file is always a clean `*ReadError`, never a bare
traceback. Line endings are also normalized (`\r`/`\r\n`/`\n` all become `\n`) before
the text reaches Biopython's scanner, the same way `read_fasta`'s `text.splitlines()`
already handles them for free — a lone `\r` (classic Mac, and what some sequencing
instruments still emit) now parses successfully rather than merely failing cleanly.

**Biopython's escape types are `ValueError`, `AssertionError` and `IndexError`, and an
undefined sequence is a skipped record (issues #403, #536, MEASURED 2026-10-03):** the
parse `try` catches all three. `IndexError` comes from a feature line shorter than the
scanner's qualifier column (fixture `malformed/genbank_feature_line_shorter_than_qualifier_indent.gb`;
three seeds of a 6000-file mutation sweep of `sample.gb` found no other type). A truncated
file (a `LOCUS` length and an `ORIGIN` header, no sequence lines) parses into a record whose
sequence is undefined, and reading its bases raises Biopython's `UndefinedSequenceError`
(a `ValueError`) *after* the parse block; `_record_sequence` turns it into "no sequence", so
the record is skipped with a notice, and a file with no readable record is refused with
`GenBankReadError` (`INPUT_INVALID`), the same skip-then-refuse as the app's `GenBankLite`.

**A non-ASCII character in an ORIGIN block is refused before Biopython reads it (issue
#492, MEASURED 2026-10-02):** Biopython upper-cases the ORIGIN text with `str.upper()`,
which folds some non-ASCII letters into ASCII (long s U+017F becomes `S`, a valid IUPAC
code), so `validate_sequence` never saw the original and the run accepted a letter that is
not DNA. `readers/genbank.py` therefore scans the raw ORIGIN lines first and raises
`GenBankReadError` in `validate_sequence`'s own wording: `Invalid character U+017F at
position 9 of GenBank record 1's sequence`. The position is 1-based over the record's own
sequence characters (the line-start numbers and the spaces between 10-base groups are
skipped; a non-ASCII space or digit is refused, not skipped), and the record is named by
index, never by id. The app's `GenBankLite` already refuses the same characters at the same
position, so this closes the worker-versus-app divergence for GenBank (FASTA and paste were
already correct).

**The app refuses a bad GenBank feature table too (issue #548, MEASURED 2026-10-03):** the worker's Biopython scanner raises on a feature-table line that ends exactly at the qualifier column (column 21, an `IndexError`), on a continuation line with no leading `/` (a qualifier line missing its slash), and on a feature location or quoted qualifier that never closes. `GenBankFeatureTable` (`Core/Inputs`) ports exactly those `parse_features` / `parse_feature` refusals, so `InputFileValidator` refuses such a file before a VM is booted; a line shorter than column 21, which Biopython only warns about, still reads. The feature table is never parsed into data in the app. MEASURED 2026-10-03: a seeded 1500-file mutation sweep of the three sample GenBanks showed no file the worker reads that the port refuses, and 13 files the worker still refuses that the app accepts (a stray `)` line and an early `//`, from Biopython's consumer-level checks), tracked as #611 (one fixture per shape sits in the worker's malformed corpus and in the parity test's `FalseAccepts`). Line lengths and columns are counted in code points, as Python does, so an emoji in a feature line does not shift the qualifier column.

**Contig names are hardened against real filename hazards (issue #350, MEASURED
2026-09-19):** `_safe_contig_name` (`readers/input.py`) disambiguates a name that
sanitizes to a Windows-reserved device name (`CON`, `NUL`, `PRN`, `COM1`..`9`,
`LPT1`..`9`) and caps length for a realistic output path, truncating only the base
portion so the `_<n>` disambiguator that keeps records in one file from colliding always
survives intact. CORRECTED per Hard Rule 18: the original theory that "Windows refuses
to create `CON.fasta`" does not reproduce on this dev box (Windows 11 Home
10.0.26200) — Python, .NET and PowerShell all created it successfully when measured
directly — so this hardening is kept as free insurance (the sanitized name also becomes
a GenBank LOCUS field and an IGV/WIG `chrom=` label) rather than as a fix for a
confirmed crash. A separate, real gap this does **not** close — the actual output
folder/file name on disk comes from `cfg.name` directly in `pipeline.py`, never from
this sanitized form — is filed as its own issue against that file.

**FASTA duplicate detection keys on the record ID, not the full header (issue #351,
MEASURED 2026-09-19):** two records sharing an ID (the header up to its first
whitespace — the part BLAST/samtools/IGV actually treat as the identifier) but carrying
different free-text descriptions are now flagged as a genuine ID collision; comparing
the full header line missed exactly this shape.

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

**GenBank gene features: coordinates outside the record's own sequence** (issue #331,
MEASURED 2026-09-19). A feature's own coordinates must fit inside its record's actual
ORIGIN length, or the file's feature table disagrees with its own sequence data (a
truncated download, a hand-edited LOCUS/ORIGIN block, corruption). `readers/genbank.py`
now checks every feature's (or, for a compound location, every segment's) coordinates
against the record's length and **drops** the feature — never silently keeps it with a
wrong span — with a notice naming the gene id, its coordinates, and the record's real
length, when they disagree. One legitimate exception: a feature carrying GenBank's own
`>` partial-end marker is allowed to report an end past the record's length, since that
marker is GenBank's own way of saying "known to continue beyond what was given" (a
normal shape for a fragment at a contig edge) — this is a `THEORY (unverified)`,
confirmed only against this reader's own synthetic fixtures, not a corpus of real
partial GenBank records. A compound (spliced or origin-wrapping) feature's segments
must each individually fit inside the record regardless of a partial marker; a
legitimate origin-wrapping feature on a circular plasmid record (the intended shape for
issue #128) already satisfies this per-segment, so this check does not affect it.

**A feature whose location cannot be read is reported, not dropped (issue #543, MEASURED
2026-10-15, Biopython 1.88).** The bad-location shapes split in two:

- `LocationParserError`: Biopython warns and sets the feature's location to `None`. Covered
  shapes include `abc`, `0..42`, `-1`, `1..?`, `?..5`, `1..`, `<<1..5`, `gap(10)`, `1.5..9`, and
  `42..1` / `5..3` / `complement(42..1)` on a record that is **not** circular. On a circular record
  `42..1` is a valid join across the origin, so nothing is dropped.
- a plain `ValueError` (`join()`, `order()`, `complement(1..3)..5`, `99999999999999999999..5`, an
  unterminated `join(1..5,`) or a bare `AssertionError` (`1..5,9..12`, `1..3)`): Biopython raises out
  of the record and cannot resume, so the whole file is refused with the reader's own named action
  (`GenBankReadError`). The notice cannot cover these.

For the first kind, GenBank and EMBL (one shared mapper) add ONE notice per record: the count of
dropped features that would have become genes (a record's `gene` features, else its `CDS`
features; a CDS beside a readable `gene` set is not counted) and the key and location text of the
first three (the rest as "and N more"), with a good location shown as the action. The location text
is shown through `ascii()` and cut at 40 characters (Hard Rule 5). When the number of Biopython
warnings does not match the number of unreadable features, only the feature key is named. The
readable features are still used; the unreadable gene is not repaired. Biopython's other warnings
are re-emitted unchanged. SnapGene builds its locations itself and raises on a bad range, so a bad
SnapGene range is a refusal, not a notice. Test: `worker/tests/test_genbank_dropped_features.py`
(fixture `malformed/genbank_unparseable_location.gb`).

**Issue #694: CDS fallback per gene id, and two notices worded for what is wrong.** When a record
uses `gene` features and one of them is dropped (unreadable location, or coordinates outside the
record), the CDS feature with the same gene id stands in for it, per id, with one notice ("the CDS
feature with the same gene id was used instead"; its span is the coding sequence, not the gene's).
A gene whose id is only its feature key, or with no CDS of that id, is simply dropped as before. A
between-bases site (`1^2`, which Biopython reads as a zero-length span) is named as "a location
between two bases", and a position past 2**63-1 (`1..99999999999999999999`) as "too large to be a
real coordinate", instead of both being called "outside the sequence length". The whole-file
`ValueError` shapes above are unchanged.

**Issue #694: a gene that crosses the origin is written as a join.** `begin..end` of such a feature
is the outer span `1..L`; writing only that made the gene look like the whole molecule in SnapGene,
Benchling and IGV. When the contig is circular (`cfg.topology.resolve(contig.circular)`, so
`--topology` counts) and a feature has segments with one ending at base `L` and one starting at base
1 (the rule the gene table and the readers use), the writers emit its real parts:

- `<name>.gb`: `join(120..126,1..10)` (the segment ending at `L` first), `complement(join(...))` on
  the minus strand, and the LOCUS line says `circular`. The `mean_entropy` note covers the gene's own
  bases only. A spliced feature on a linear contig keeps its outer span (GenBank's own `gene`
  feature is the full span), but its `mean_entropy` note averages the joined exon bases only, the
  same value the gene table reports (#563).
- `<name>.genes.gff3`: one row per segment, all sharing one `ID`, plus a `region` row with
  `Is_circular=true` for every circular contig (the GFF3 convention for a discontinuous feature on a
  circular molecule). Positions carry the run's `start` offset like every other row.

Both read back as the same wrapping gene (`readers/genbank.py`, `readers/gff3.py` plus
`fit_to_sequence`). Test: `worker/tests/test_origin_wrap_writers.py`. Known gap: the GenBank reader
has no unstranded gene, so an unstranded gene (`.`) reads back from `.gb` as `+`.

Every one of these five checks has a dedicated, deterministic test in
`worker/tests/test_validation.py`; `docs/tests.md`'s neighbour-test set names the shapes a
new validation test should also sweep (empty contig, ambiguity codes, multi-record files,
`L < 2K`, `L > W`).

### 4b. Fetching a record by NCBI accession (issue #133)

`readers/entrez.py`, `dna-entropy run --accession NC_045512.2 [--accession-range 100-500]
[--ncbi-api-key K] [--cache-dir D]`. The worker asks NCBI E-utilities (`efetch`, `db=nuccore`,
`rettype=gb`) for the record, writes it under the cache directory
(`%LOCALAPPDATA%\DNAEntropyGraph\cache\inputs` by default) and then runs the ordinary
GenBank path on that file, so every rule in this section and the "use the file's own genes,
never re-annotate" rule apply unchanged. Mechanics:

- The accession is matched against a strict **ASCII-only** pattern before any request (`\d` alone
  accepts Arabic-Indic and full-width digits, and `str.upper()` folds long s and dotless i into
  ASCII letters, so both are refused first); a bad one is a `ValidationError` (`INPUT_INVALID`),
  never a request. The CLI also checks every other option (`--predictor`, `--region-threshold`,
  `--smoothing-window`, `--accession-range`, `--input`, a conflicting `--informat`) before the
  fetch, so a run that cannot start never costs a request. `--informat` other than `genbank` with
  `--accession` is refused, not overwritten.
- NCBI can answer a failed lookup with HTTP 200 and an error text. A body that does not start
  with `LOCUS`, does not end with `//` (a cut-off download), has no `VERSION` line, or names a
  different record than asked, is refused and not cached.
- **Resolved version.** The cache file is named, and provenance records, the versioned accession
  from the record's `VERSION` line (`NC_045512` asked, `NC_045512.2` stored). An unversioned request
  always asks NCBI (a cached unversioned answer could be a version NCBI has since replaced); a
  versioned request never changes and is served from the cache. Provenance has both
  `accession` (resolved) and `requested`.
- **Deadline and size.** The body is read as it streams: 120 s in total, however slowly it drips
  in (each single wait 30 s), and at most 64 MiB (the app's own input ceiling); over either, the
  fetch is refused and nothing is cached. A bigger record needs `--accession-range`.
- **Failures** each have their own message and one action: 429 (wait, or use an API key), 5xx (try
  again in a few minutes), 400 and 404 (check the accession and range), 401 and 403 (check the API
  key), a timeout, a too-large record, any other network failure. Only 429 and 5xx are retried,
  once, after a 2 s backoff (NCBI's `Retry-After` up to 30 s when it sends one). No message carries
  the URL or the key.
- Rate limit: at least 0.34 s between requests without a key, 0.1 s with one.
- The cache key is resolved accession plus range; a sidecar `.json` keeps the ORIGINAL retrieval
  date, so a later run served from the cache still reports when the record was downloaded.
- `provenance.json` gets `input_source: {type: "ncbi_entrez", accession, requested, range,
  retrieved, from_cache}`. The API key is never written anywhere.
- The test suite never touches the network (`worker/tests/test_entrez.py` injects the opener).
  Local validation in the app (rule 2) is not needed for the fetch itself: the fetched text
  goes through the same GenBank reader and validator in the worker before any prediction.

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
| `<name>.gb` | GenBank flat file | **1-based, inclusive** on the flat-file display; the writer's `GeneFeature` input and Biopython's internal `FeatureLocation` are both 0-based half-open, converted at write time (`begin0 = f.begin - 1`) | For GenBank input: the record's **own, authoritative** genes, each carrying a `/note="mean_entropy=<x> bits"` qualifier (the coarse per-gene summary). For FASTA/paste input with `--genes`: Prodigal-predicted genes, same qualifier. GenBank has no per-base numeric channel, so this file never carries the full-resolution track - that is always a companion file below. Written UTF-8/LF like every other writer here: the writer opens the file itself (`encoding="utf-8", newline="\n"`) and hands Biopython's `SeqIO.write` that handle, rather than a bare path, which on Windows would let Biopython open it in platform-default text mode (CRLF) - MEASURED 2026-09-19, found and fixed during the lane-B writers audit. | MEASURED, `writers/genbank.py` |
| `<name>.entropy.bedgraph` | bedGraph | **0-based, half-open**: row `chrom start end value` where base `i` (0-based) covers `[start_coord - 1 + i, start_coord + i)` | Full-resolution per-position entropy, one row per base per direction requested. `chrom` matches the FASTA contig name so IGV lines the track up automatically. IGV's default track for this data. | MEASURED, `writers/bedgraph.py` |
| `<name>.entropy.wig` | WIG (`fixedStep`) | **1-based**: `fixedStep chrom=<name> start=<start> step=1 span=1`, then one value per line | The same full-resolution entropy track as bedGraph, in the alternate WIG format some tools prefer. | MEASURED, `writers/wig.py` |
| `<name>.entropy.geneious.gff3` | GFF3 | **1-based, inclusive**: one 1 bp feature per position, `pos = start - 1 + i + 1`, **or**, above `DEFAULT_MAX_PER_BASE_FEATURES` (200,000) combined positions, one feature per fixed-size bin (mean entropy), `[start - 1 + bin_start + 1, start - 1 + bin_end]` | Full-resolution entropy as a GFF3 feature track, because **Geneious Prime imports GFF3 but not WIG or bedGraph as a graph track** (in Geneious those are export-only, from the Graphs tab). Each feature carries the entropy value in both the GFF3 score column and an `entropy`/`entropy_mean` qualifier; shade the track with Geneious's *Color by / Heatmap* on either. **Large-sequence binning (issue #296):** MEASURED 2026-09-19, an unbinned 1,000,000-position track is 87.78 MB / 1.587s to write; above the threshold the writer switches to mean-entropy bins instead (both a 1,000,000- and a 10,000,000-position run land at ~22-23 MB), and records a `# NOTE` line in the file saying so. `GeneiousWriter.write(..., max_per_base_features=None)` forces unbinned, full-resolution output regardless of length. | MEASURED (prototype team; recorded in `worker/docs-legacy/DESIGN.md` and the docstring of `writers/geneious.py`) |
| `<name>.entropy.smooth<W>.bedgraph` / `.wig` | same format and coordinates as the raw entropy track (bedGraph OR WIG, whichever the run selected), one file per window `W` in `analysis.smoothingWindows` (default `[51]`) | same as the raw track | The **centred rolling mean** of the combined entropy track over `W` bases (`W` odd; `h = (W - 1) / 2` bases each side). **Edges:** on a linear molecule the window **shrinks** at the ends (base `i` averages `[i - h, i + h]` clipped to the sequence; nothing is padded or wrapped), so the first and last `h` bases average fewer bases and are noisier; on a **circular** molecule (issue #128) the window **wraps** the origin so every base has a full window (a window longer than one turn is clamped to one turn). `W = 1` is the raw track bit for bit. The raw track is always written and stays primary: a smoothed track is a view of it, easy to over-read (a smoothed dip is not a gene boundary). Computed from the same `(L,)` entropy values (cumulative sum in float64, error far below the 4 written decimals), never from the `(L, 4)` matrix. Each contig is smoothed on its own. Only the entropy track is smoothed (no surprisal, fwd/rev or Geneious GFF3 variant yet). | MEASURED 2026-10-03, `analysis/smoothing.py`, `writers/bedgraph.py`/`wig.py` via `variant=smooth<W>` (issue #126) |
| `<name>.surprisal.bedgraph` / `.wig` / `.geneious.gff3` | same formats as the entropy tracks above, byte-identical layout; the track is bedGraph OR WIG (whichever the run selected, for every input kind including GenBank, issue #412), never both | Same, but the value at each position is surprisal (section 2b), not entropy - the `entropy`/`Shannon entropy` label and `H=` qualifier become `surprisal`/`Surprisal: -log2 P(actual base)` and `S=`. Written whenever `include_surprisal` is on (the default; `--no-surprisal` on the CLI omits all three plus the TSV column below). | MEASURED 2026-09-19, `writers/bedgraph.py`/`wig.py`/`geneious.py` (`metric="surprisal"`), issue #123 |
| `<name>.genes.gff3` | GFF3 | **1-based, inclusive**: `f.begin + offset` .. `f.end + offset` where `offset = start - 1` | Gene features on the same contig and coordinate frame as the entropy track: the input's own genes for GenBank input (`source=genbank`, written only when the input actually carries genes), or Prodigal's predictions for FASTA/paste input with `--genes` on (`source=pyrodigal`). Column-9 attribute values (gene ids/names) are percent-encoded per the GFF3 spec's reserved set - `;`, `=`, `&`, `,`, tab, newline, and `%` itself (escaped first, so it is never re-escaped and a literal `%` can never be misread as the start of another escape). | MEASURED, `writers/gff.py` |
| `<name>.regions.bed` / `<name>.regions.gff3` | BED (4 columns, no `track` line) and GFF3 | BED **0-based, half-open** with the run's `start` offset (`[begin + start - 1, end + start - 1)`, like the bedGraph); GFF3 **1-based, inclusive** | Stretches of **low** entropy (strictly below `regionThreshold`, default 0.5 bits) and **high** entropy (strictly above the mirror, `2 - regionThreshold` = 1.5) as a feature track that loads in IGV and the other viewers. Rule, in order: consecutive qualifying bases form a run; runs at most `regionMergeGap` (default 5) bases apart merge and the bridged bases belong to the region; a merged region shorter than `regionMinLength` (default 20) is dropped (merging comes first, so two short runs bridged into one long one survive). Names `low_entropy_<n>` / `high_entropy_<n>` (numbered per kind across the file); GFF3 type `region` (SO:0000001; the Sequence Ontology term, as `misc_feature` is a GenBank key; `kind` tells low from high), source `dna-entropy`, attributes `kind`, `length`, `mean_entropy`. Calls are made on the run's combined entropy track (the value `<name>.entropy.bedgraph` carries), never recomputed. On a **circular** molecule (issue #128) a stretch at the tail and one at the head are one region when the gap across the origin is within the merge gap; it is written as two BED lines and two GFF3 lines sharing one name/ID (a GFF3 discontinuous feature), the GFF3 pieces tagged `wraps_origin=true`. An empty file means "none found". | MEASURED 2026-10-03, `analysis/regions.py`, `writers/regions.py` (issue #125) |
| `<name>.genes.tsv` / `<name>.genes.csv` | TSV and CSV (the same table twice; UTF-8, LF; the CSV quotes a name containing a comma) | `begin`/`end` are **1-based, inclusive** with the run's `start` offset applied, exactly like `genes.gff3`; `begin > end` means the gene crosses the origin of a circular molecule | One row per gene/CDS (GenBank features, or Prodigal's calls with `--genes`; written only when the run has genes). Columns: `contig, gene_id, begin, end, strand, length_nt, partial, wraps_origin, segments, mean_entropy_bits, min_entropy_bits, max_entropy_bits, mean_surprisal_bits, fraction_low_entropy`. The statistics are taken over **the gene's own bases of the entropy track the run wrote** (never recomputed, Hard Rule 3): a spliced gene over its segments only (the intron is not averaged in), and on a **circular** molecule (issue #128) a gene whose segments span the origin (`join(4900..5000,1..100)`) over those real bases, reported with `wraps_origin=true` and `begin > end`; the same `join` on a linear molecule is just a spliced gene. `fraction_low_entropy` is the share of those bases strictly below 0.5 bits (`DEFAULT_LOW_ENTROPY_BITS`, the same default the region caller uses). `mean_surprisal_bits` is empty when surprisal is off. Floats to 4 decimals; a tab or line break inside a name becomes a space in the TSV. `length_nt` is the bases summarised, so for a spliced gene it is the exon total, not `end - begin + 1`. A partial feature running past the sequence end is clamped to it. | MEASURED 2026-10-03, `analysis/gene_summary.py`, `writers/gene_summary.py` (issue #124) |
| `stats.txt` (GenBank input) / `<name>.summary.txt` (FASTA/paste input) | plain text | n/a | Per-contig `EntropySummary`: length, mean, min, max, and the 0-based array index of each extreme (see section 2 - add `start` to get a genomic position). When `include_surprisal` is on, also reports `SurprisalSummary`'s mean and total log-likelihood (section 2b). When windowing/direction provenance is supplied, also records context length (K), window (W), stride (S), the GPU per-window ceiling the run used, direction, seam position, and any reduced-context count - the same derived numbers, not recomputed. **The app reads this file** (issue #102): the Results page's per-contig header is `Core/Runs/RunSummaryReader.cs` parsing the `write_multi` layout (an overall block, then one `[contig]` block each), pinned by `tests/contract-fixtures/run_summary_two_contigs.txt`, which `worker/tests/test_run_summary_fixture.py` regenerates from the mock pipeline. Change the layout and that test fails: update the fixture and the C# parser together. | MEASURED, `writers/summary.py` |
| `<name>.entropy.tsv` | TSV | **1-based, inclusive**, matching WIG and both GFF3 flavours - see note below | Per-position entropy (and surprisal, when `include_surprisal` is on) in a plain, spreadsheet-friendly table, one row per base. | MEASURED 2026-09-19, `writers/tsv.py` |
| `<name>.probs.tsv.gz` (and optionally `<name>.probs.npy`) | gzip-compressed TSV (UTF-8, LF; the gzip header carries no timestamp or file name, so equal matrices are equal bytes), and a float32 NumPy `.npy` | `position` is **1-based, inclusive** with the run's `start` offset, like `entropy.tsv`; a multi-contig file introduces each contig with a `# contig: <name>` line and restarts numbering (the `.npy` concatenates contigs in file order; lengths are in `provenance.json`) | **OFF by default** (size). Columns `position, base, pA, pC, pG, pT`: `base` is the actual base at that position and the four probabilities are the model's A/C/G/T distribution there, written with 9 significant digits (round-trips a float32 exactly). It is the very `(L, 4)` array the contract guard validated (Hard Rule 3), serialised and never recomputed, **in the original strand's coordinates and base order**: a reverse-complement pass is flipped back (`i -> L-1-i`) and its columns complemented (rc base b with probability p is original base comp(b) with probability p), then combined by the **same per-position rule as the entropy track**. So `shannon_entropy(matrix)` reproduces `<name>.entropy.*` within 1e-6 for forward-only, reverse-only, both-combined (and the combined track under both-separate), on linear and circular molecules; and `-log2(matrix[i, actual base])` reproduces the surprisal track. **Exception, `both-averaged`**: where both directions have full context the row is the *mean of the two probability vectors*, and entropy is concave, so its entropy is at least the averaged entropy track, not equal (the run says so in its notices). Not exported: the separate forward/reverse matrices under `both-separate`. | MEASURED 2026-10-03, `writers/probs.py`, `analysis/direction.py` `keep_probs` (issue #127) |

**Which of these files the app asks for (issue #103).** The New run page offers one tick box per name in the manifest's
`outputs` vocabulary (`job_contract.md` section 3), from one table, `OutputCatalog` in `DnaEntropyGraph.Core`: wire name,
label, the input kinds it is offered for, default on or off, and whether it needs genes. Every file is offered for both a
GenBank input and a FASTA/paste input (`pipeline.py` gates each writer on its own `include_*` flag on both paths). Every
file is ticked by default except `wig` and `probs`/`probs_npy`. The worker writes ONE track format per run (`wig` with `bedgraph` also named gives bedgraph, `manifest.py` `build_run_config`), so `wig` is the alternative to `bedgraph`: the page clears one when the other is ticked, `OutputCatalog.Normalize` keeps the bedgraph if a stored choice names both, and WIG is off by default. The worker treats `probs` and `probs_npy` as off unless named, even when everything else is on. `genes_gff3` and `gene_summary` need genes, so the manifest never names them when gene finding is
off. A non-empty `outputs` array is read literally by the worker, so a cleared box is a file that is not written; an empty
selection is refused in the app because the worker would read an empty array as "everything". `smoothed` is sent with no
`analysis.smoothingWindows`, so the worker's default window (51) applies. The choice is remembered per input kind
(`OutputSelectionStore`, settings keys `output_files_genbank` and `output_files_sequence`, stored as the wire names). A
stored run's `OptionsJson` that says `"All"` keeps meaning the eight files that existed before #103
(`OutputFileKinds.All` is frozen). `OutputCatalogTests` pins the catalog to the contract row and
`worker/tests/test_output_vocabulary.py` pins the worker to the same row.

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

**Issue #123's surprisal column:** the plain (non-`Both, separate tracks`) shape grows a
4th column, `position\tbase\tentropy_bits\tsurprisal_bits`, when `include_surprisal` is on
(the default). The `Both, separate tracks` 5-column shape does not yet grow surprisal
columns - see section 2b's "known scope limit."

**Multi-record input**: per spec decision D14, all FASTA records are processed (the
prototype processed only the first); GenBank multi-record input was already handled via
`write_multi` on every writer above, one block per record, correctly keyed by contig name
so the viewer and IGV keep every record's track aligned to its own sequence.

---

### 5c. A FASTA + GFF3 pair as an annotated input (issue #135)

`dna-entropy run -i seq.fasta --gff3 seq.gff3` (`RunConfig.annotation_path`, reader
`readers/gff3.py`). The GFF3's genes are the file's own, so **Prodigal never runs**, even with
`--genes`. Rules:

- Rows are matched to FASTA records by the GFF3's first column, **percent-decoded** (GFF3 escapes
  reserved characters, so `gnl%7Cx` is `gnl|x`), against the record id (the header text up to the
  first space). If no name matches, the run is refused; if some do not, one counted notice says
  so. A multi-record FASTA where only some records have GFF3 rows gives the others no genes, and
  Prodigal still never runs for them (MEASURED 2026-10-15: `test_multi_record_fasta_with_gff3_rows...`).
  A GFF3 with a non-FASTA input is refused.
- Per sequence name, `gene` features are used, else `CDS` (the same preference as the GenBank
  reader). Several rows (`gene` or `CDS`) sharing one `ID` are one feature: outer span in
  `begin..end`, real parts in `segments`, with a notice (like a GenBank `join`). The gene id is
  `Name`, else `gene`, `locus_tag`, `product`, `ID`.
- **Coordinates** are GFF3's 1-based inclusive positions counted over the sequence letters, so
  FASTA line wrapping and case do not shift them. The bounds check uses the validated sequence's
  length (the letters; whitespace is not part of it, an ambiguity code keeps its position and
  `--ambiguity-policy` never deletes a base). `--start` only relabels the outputs; it does not shift
  how GFF3 positions are read.
- **Strand** stays `+`, `-` or `.`: `.` and `?` are both unstranded, never turned into `+`. The
  GenBank output writes an unstranded gene as a plain `begin..end` (GenBank has no unstranded
  location), never as `complement(...)`.
- **Circular sequences.** `Is_circular=true` on any row of a sequence (normally its `region` row)
  is the input declaring a circular molecule, like a GenBank `LOCUS` line: the contig is analysed
  as circular, with one notice, unless `--topology linear` says otherwise (`--topology circular`
  forces it for the others). On a circular sequence a feature crosses the origin when either two
  rows share an `ID` and touch both ends, or one row's end is past the sequence length (and the
  wrapped part does not overlap the start). Both become the outer span `1..L` with `segments`, like
  a GenBank origin join, and the gene table reports `wraps_origin`. On a linear sequence a feature
  running past the end is dropped; the notice names both causes (an undeclared circular sequence, or
  a GFF3 for a different FASTA).
- A malformed line is skipped with a counted notice naming the first line number; the `##FASTA`
  section is ignored.
- Outputs: `<name>.genes.gff3` (source `gff3`), the gene summary tables, and the bonus `<name>.gb`
  carry these genes. Auto-pairing `<name>.fasta` with `<name>.gff3` in one drop, and a pairing UI
  when ambiguous, are app work (follow-up issue); the manifest field is `worker/manifest.py`'s.

### 5b. Input formats beyond GenBank and FASTA (issue #134)

`readers/embl.py` (EMBL flat file, `.embl`/`.emb`, or an `ID   ` first line) and
`readers/snapgene.py` (SnapGene `.dna`, detected by its magic bytes: a type-0x09 packet whose
data starts `SnapGene`, so a renamed file still works and a text file named `.dna` is not
claimed) are parsed by Biopython (BSD-3-Clause; the `snapgene` SeqIO parser needs no new
dependency) and mapped by the same `genbank.records_from_parsed` as GenBank: the file's own
`gene` (else `CDS`) features become `GeneFeature`s, a circular topology is kept, a record with no
sequence is skipped with a notice. The loaded input takes the GenBank output set (the genes
are the file's own, Prodigal never runs). Differences worth knowing:

- EMBL record ids are `accession.version`; a non-ASCII letter in the `SQ` block is refused (the
  GenBank #492 case-folding trap applies, so it is checked before Biopython upper-cases).
- SnapGene names features by `label`; the reader uses it as the gene id when there is no
  `gene`/`locus_tag`/`product` (this also applies to GenBank features that only have a `label`).
- SnapGene keeps one sequence per file and is capped at 64 MiB. The reader walks the packets
  itself before Biopython: a cut-off packet, any non-DNA packet over 8 MiB, any `DOCTYPE` or
  `ENTITY` declaration (SnapGene never writes one) and a non-ASCII DNA byte are each a named
  refusal, never a traceback. MEASURED 2026-10-15 (Python 3.12.10, expat 2.7.1): this build's
  expat already stops a billion-laughs document in 0.36 s, but that is a property of the Python
  build, so the refusal does not rely on it.
- A feature crossing the origin of a circular record (SnapGene range `120-10`, EMBL
  `join(120..126,1..10)`) is mapped exactly like GenBank's: outer span `1..L` plus its
  `segments`; the gene table reads the segments and reports `wraps_origin`. The notice says the
  feature wraps the origin (not "intron") only when the record is circular and one segment ends at
  the last base while another starts at base 1; otherwise it is a spliced join.
- `provenance.json` carries `run.input_format` (`genbank`, `embl`, `snapgene`, `fasta`, `paste`).
  `source_kind` stays `genbank` for EMBL and SnapGene so they take the GenBank output set.
- The app does not yet validate these two formats locally before creating a VM (rule 2); that
  port is tracked in a follow-up issue.

### 5e. Analysis commands on top of a run (issues #129, #130, #192)

**Batch outlier QC (#192).** A run of three or more inputs (a multi-record file) scores every input
in `stats.txt` / `<name>.summary.txt`. `analysis/batch_qc.py` reduces each input to its mean entropy
and computes a robust z-score over the batch, `0.6745 * (mean - median) / MAD`; `|z| > 3.5` is
flagged. The overall block gets `batch QC flagged:   N of M` and each `[contig]` block gets
`batch QC z-score`, `batch QC median` and `batch QC flag` (`ok` or `OUTLIER`); those keys are new, so
a reader that takes the first value of each key (the app's `RunSummaryReader`) is not shadowed. If
every other input is exactly alike (MAD = 0) any input that differs is flagged, with z capped at
99. Below three inputs nothing is flagged. Tests: `worker/tests/test_batch_qc.py` (the statistic and
the summary file; a planted outlier is flagged alone, MEASURED 2026-10-15 on synthetic tracks, not on
shuffled real plasmids, which is the ToTest row).

**Compare two runs (#129).** `dna-entropy compare A.entropy.tsv B.entropy.tsv [--offset N] [--contig C]`
writes `<name>.compare.bedgraph` (B minus A in bits, bedGraph coordinates like every other track,
one row per position) and `<name>.compare.deviations.tsv` (the `--top` largest `|difference|`,
1-based). B index `j` is compared with A index `j + offset`; with no offset the lengths must match,
otherwise the command refuses and names `--offset`. Positions are A's coordinates. A run compared with
itself gives an all-zero track. Stacking A, B and the difference in the viewer is app work. Tests:
`worker/tests/test_compare.py`.

**Variant effect scan (#130).** `dna-entropy variants -i seq.fasta (--variants v.tsv | --positions 12,40)`
scores substitutions with one forward pass per variant (`--max-variants`, default 50, refuses more
and names the option; `--dry-run` prints only the cost). The cost line ("N forward passes ... about N
seconds on an L4 GPU") is printed before any pass. Each variant is scored on the window `pos +- K/2`
(`--context-length`) of the reference and of the variant; `<name>.variants.tsv` has `position, ref,
alt, window_start, window_end, delta_surprisal_bits` (variant window total surprisal minus the
reference window's), `delta_mean_entropy_bits`, and `site_surprisal_ref_bits` / `site_surprisal_alt_bits`
(from the reference window's own prediction). Reference windows are predicted once per distinct
window. The variants file is `pos<TAB>ref<TAB>alt`; every ref must match the sequence. Tests:
`worker/tests/test_variants.py` (mock predictor: plumbing and arithmetic, not biology).
THEORY (unverified): a synonymous change in a conserved codon scores a smaller delta than a nonsense
change on the TnpB test locus (the issue's observable); it needs a real model (docs/ToTest.md).

### 5d. Model-free companion tracks: GC percent and codon usage (issue #191)

Both are computed from the validated sequence alone (`analysis/composition.py`), cost nothing on
the GPU, and are **off by default** (new files nobody asked for; see #411). `--gc` / `--gc-window`
and `--codons` on the CLI, `RunConfig.include_gc` / `gc_window` / `include_codons`.

| File | Format | Coordinates | What it contains |
|---|---|---|---|
| `<name>.gc.bedgraph` | bedGraph, always (not WIG) | **0-based, half-open**, like every bedGraph here | GC percent (0 to 100) at each base over a centred window (default 51, odd): `100 * (G+C) / (A+C+G+T)` in the window. Ambiguity codes are not counted in the denominator; a window with no unambiguous base is `0.0`. A linear molecule's window shrinks at the ends, a circular one wraps the origin (same rules as the smoothed entropy tracks). |
| `<name>.codons.tsv` | TSV with a header row | `begin`/`end` are 1-based inclusive, the gene's own | One row per codon that occurs, per CDS-shaped gene (the file's own genes, a paired GFF3's, or Prodigal's): `contig gene_id begin end strand codon amino_acid count frequency rscu deviation`. Codons are read on the gene's strand (minus strand = reverse complement; compound genes join their segments, not the intron) and translated with the standard code (table 1; sense assignments equal table 11). `frequency` = count / codons counted; `rscu` = count / (that amino acid's total / its number of synonymous codons), so 1.0 means an even split among synonyms; `deviation` = `rscu - 1`. A codon with an ambiguity code is not counted. A feature whose length is not a multiple of three is skipped and counted in one run notice. |

A poly-G sequence gives 100.0 across the whole GC track (the issue's observable, tested).

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
