# Sprint log

Append-only. One entry per merge, newest first, under "Recent changes"
below. Never edited after the fact (`docs/README.md`'s house-style table).
Do not hand-write an entry here directly — write
`docs/changelog.d/<branch-name>.md` on your branch instead and let
`python scripts/compile_sprint_log.py` fold it in at merge time. See
[`changelog.d/README.md`](changelog.d/README.md) for the fragment
convention.

## Recent changes

- Added `scripts/check_unused_fields.py`, an eighth guard: it parses every `@dataclass` under
  `worker/src/dna_entropy/` and fails on any field that is set and never read, counting an
  attribute load or `getattr` as a read and a plain assignment or a construction keyword
  argument as not one. Reads that happen only inside a serialization method are reported
  separately, because a field that only travels back out to JSON still does nothing. Reads are
  also collected from `scripts/`, since a generator there can legitimately be a field's only
  consumer, as `ErrorCodeSpec.raised_by` is.
- MEASURED 2026-09-19: its first run found nine fields that were parsed, validated,
  schema-checked and ignored, including `Lifecycle.afterTask="keep"` leaving a VM running with
  no expiry at all (a Hard Rule 11 violation worth about twenty dollars a day), and
  `ModelRequirement.min_gpu_count` letting a single-GPU machine past the gate for a model that
  needs two cards. All nine are fixed (#292, #293, #296, #304, #306, #338, #340) or tracked
  (#343, #344, #345, #361).
- The guard documents its own blind spot rather than hiding it: it matches attribute reads by
  name, not by type, so a field whose name collides with a read attribute on another class is
  silently counted as read. `WindowPlan.context` and `StoreSpec.bucket/prefix/root` are masked
  that way today. A clean run means no field with a name nothing reads, not no unread field.
- `scripts/unused_fields_allowlist.json` carries the exceptions, each with a written reason, and
  supports `Class.*` for a document serialized wholesale with `dataclasses.asdict()`. A stale
  entry fails the guard, so the file cannot rot into a list of things that used to be true.

- Fixed issue #304: `manifest.json`'s `outputs` array is now honored literally instead of
  being silently ignored beyond three of eight writers. `RunConfig` (`worker/src/dna_entropy/
  config.py`) gained `include_fasta`/`include_track`/`include_geneious`/`include_stats`/
  `include_genbank`/`include_genes_gff3`, each defaulting to `True` so every existing caller
  (the CLI, every bare `RunConfig()` in tests) keeps writing the full output set unchanged.
  `pipeline.py`'s `_write_genbank_outputs`/`_write_standard_outputs` gate every writer on its
  matching flag. `JobManifest.build_run_config` (`worker/src/dna_entropy/worker/manifest.py`)
  maps `outputs` onto all six flags: an empty/omitted array still means "everything" (no
  behaviour change for a manifest that never mentions `outputs`), a non-empty array now
  actually suppresses anything not named. The genes-computation trigger (`cfg.genes`, which
  gates a real Prodigal compute cost) is kept independent of this "unspecified means
  everything" fallback on purpose - only an explicit `input.genes` or an explicit
  `genes_gff3` entry in a non-empty `outputs` turns Prodigal on, exactly as before.
- Found and fixed while auditing #304: `analysis.format` (`AnalysisSpec.track_format`) was
  accepted as any unvalidated string and, separately, never actually consulted by
  `build_run_config` - the track format was derived purely from `outputs` membership and
  always fell back to `bedgraph` regardless, so a manifest declaring `analysis.format:
  "wig"` with no `outputs` key silently got bedgraph. `AnalysisSpec.track_format` is now
  typed `TrackFormat` (validated at parse time like `analysis.direction`) and used as the
  fallback whenever `outputs` is empty/omitted; a non-empty `outputs` array remains
  authoritative (issue #304's own scope, unchanged). Regenerated `docs/contract/
  manifest.schema.json` (`python scripts/gen_manifest_schema.py`) - `analysis.format` now
  carries a real `enum: [bedgraph, wig]` constraint instead of a bare `"type": "string"`;
  `--check` passes.
- Fixed issue #306: `manifest.json`'s per-input `fastaRecords: "first"` is now honored.
  `RunConfig.fasta_records` (default `"all"`) is consulted in `pipeline.run()` right after
  `load_input()` returns, truncating to the first contig when `fasta_records == "first"` and
  the input is FASTA (GenBank multi-record input is untouched - the field is documented as
  FASTA-specific in `docs/job_contract.md` section 3), and records a notice naming how many
  records were dropped. `readers/input.py` (Lane A's file) was not touched: the truncation
  happens entirely in `pipeline.py`, which this lane owns. `worker.manifest.InputSpec.
  fasta_records` now validates against `{"all", "first"}` at parse time (previously any
  string was accepted silently and treated as `"all"`).
- Fixed issue #291: deleted `worker/packaging/launcher.py` (the double-click PyInstaller
  wizard, which shelled out to the retired `cloudrun` CLI command removed in #277) and
  `worker/packaging/build_exe.ps1` (which existed only to build `launcher.py` into an exe)
  outright, rather than rewriting the dead call. The migration inventory already recorded
  both as GUT/REFERENCE-only, not shipped, with no C# equivalent needed - the WinUI app's
  own drop-zone/paste dialog (design section 4.2) replaces the wizard. Design call recorded
  at issue #332 (DECISION).
- Verified issue #299 is already satisfied: `scripts/gen_manifest_schema.py`, `worker/src/
  dna_entropy/worker/schema_gen.py`, `docs/contract/*.schema.json`, and the CI-wired
  `--check` drift guard (`.github/workflows/ci-worker.yml`) all exist and pass. Recommended
  closure in a comment on the issue; not closed here (only the orchestrator closes issues).
- Fixed (mechanical unused-field guard, `scripts/check_unused_fields.py`, orchestrator-
  owned): `limits.heartbeatSeconds` was parsed but `run_job` built `StatusWriter` with no
  `interval_seconds` at all, so the worker always ticked at `status.py`'s own hardcoded
  10 s regardless of the manifest. `run_job` now passes
  `interval_seconds=min(heartbeatSeconds, 10)` - never slower than declared, never above
  the pre-existing 10 s ceiling (`progress.jsonl`'s own separate 5-10 s cadence is
  unaffected). Filed as #340 before the fix; comment added noting it is now resolved.
- Fixed (same guard): `limits.cancelPollSeconds` was parsed but `CancelWatcher.poll()`
  (`worker/cancel.py`) did a real store `exists()` round-trip on *every* cooperative-
  cancellation checkpoint (once per window, once per contig) with no throttling - a long
  batch tiled into many windows meant a real store query per window. `CancelWatcher` now
  takes `poll_interval_seconds`/`time_source` and throttles its real round-trip to at
  most once per interval, returning the last-known (non-cancelled) result in between; the
  checkpoint is still called every window/contig, only the underlying store query is
  throttled, so job_contract.md section 6's latency-to-effect budget is unchanged.
  `run_job` wires `poll_interval_seconds=manifest.limits.cancel_poll_seconds`. Updated two
  existing tests (`test_cancellation_mid_job_keeps_partial_results_for_completed_inputs`,
  `test_cancellation_partway_through_one_inputs_records_uploads_completed_contigs_first`)
  to set `cancelPollSeconds: 0` ("always due"), since they were relying on the pre-fix
  unthrottled behavior to observe a cancellation within the same instant, not testing the
  throttle itself. Filed as #338 before the fix; comment added noting it is now resolved.
- Fixed (same guard; Hard Rule 11 violation, flagged P1 by the orchestrator):
  `lifecycle.afterTask == "keep"` used to skip `run_job`'s entire lifecycle block, and
  `lifecycle.keepAliveMinutes`/`lifecycle.afterKeepAlive` were parsed but never consumed
  anywhere - a manifest requesting `"keep"` left the VM running with **zero** worker-side
  expiry, ever ("keep alive always has an expiry, never indefinitely"). The real
  keep-alive queue/idle-timer feature (waiting for a follow-up job) is issue #93 and is
  NOT built here tonight - building it correctly (queue polling, `lease.json`, an `idle`
  stage) needs its own deliberate design, not a rushed partial version. Until #93 lands,
  `run_job` now safely degrades `"keep"` to `lifecycle.afterKeepAlive` (default `"stop"`)
  with a notice explaining why, and forces `"stop"` if `afterKeepAlive` is itself `"keep"`
  rather than propagate a second unbounded keep. This is a worker-side safety net only -
  the VM's own `maxRunDuration`/`instanceTerminationAction=DELETE` (Hard Rule 10) remains
  the real backstop, and is itself unverified against a real GCP project tonight
  (`docs/ToTest.md`).
- `docs/job_contract.md` section 3's field-notes table updated in the same change: a new
  `outputs` row documents the suppression fix and the pre-existing (unresolved)
  bedgraph+wig-always-both asymmetry between GenBank and FASTA/paste input on the GenBank
  path; the `fastaRecords` row now says the field is honored instead of "if ever needed";
  `heartbeatSeconds`, `cancelPollSeconds`, and `afterTask` rows document the three fixes
  immediately above.
- Added tests: `worker/tests/test_pipeline.py` (11 new tests: per-`include_*`-flag
  suppression asserted against real files on `tmp_path`, not just the returned `RunConfig`
  or `RunResult.outputs` list; `fastaRecords` truncation, its default, its notice text, and
  its GenBank non-interference) and `worker/tests/test_worker_manifest.py` (9 new tests:
  `build_run_config`'s outputs-to-flags mapping, including a regression guard that the
  "unspecified outputs means everything" fallback does not also turn on Prodigal by
  itself). Updated `worker/tests/test_worker_runner.py::test_genbank_input_end_to_end`,
  which previously relied on the outputs-are-ignored bug (its manifest fixture's outputs
  list excludes `"genbank"`) to get a `.gb` file; it now asks for `"genbank"` explicitly.

- Fixed #293: `EvoPredictor._extract_logits` could leak a bare `AttributeError` instead
  of a `PredictorError` when the model's unwrapped output had no `.ndim`. The unwrap
  steps (nested tuple/list -> `.logits` -> drop batch dim) moved, unchanged, into a new
  torch-free `predictors/logits.py::normalize_model_output`, so the failure mode is now
  unit-tested on any machine (`test_evo_predictor.py`, using the same fake-`torch`/`evo2`
  module technique `test_model_gating.py` already used) instead of being reachable only
  on the GPU box. `evo.py`'s `_extract_logits` is unchanged in behavior, only in what it
  raises on an unrecognized shape.
- Fixed #292: `aligned_acgt_probs` crashed with an `IndexError` on a zero-length input
  (`aligned[0] = ...` on a `(0, 4)` array). Now returns an empty `(0, 4)` float32 array,
  which satisfies the `(L, 4)` contract vacuously and `check_probability_matrix` agrees
  (row-sum-to-1.0 is vacuous for zero rows).
- Fixed #296: `GeneiousWriter`'s per-base GFF3 track had unmeasured cost at large context
  lengths. MEASURED 2026-09-19: an unbinned 1,000,000-position track is 87.78 MB / 1.587s
  to write; a 10,000-position track is 0.82 MB / 0.012s. `GeneiousWriter` now switches to
  fixed-size mean-entropy bins above `DEFAULT_MAX_PER_BASE_FEATURES` (200,000) combined
  positions, recording a `# NOTE` line in the file; after the fix, both a 1,000,000- and a
  10,000,000-position run land at ~22-23 MB. `max_per_base_features=None` forces unbinned
  output. Design call recorded at #353 (`DECISION`).
- Fixed #339 (found during this audit): `GenBankWriter` wrote CRLF line endings on
  Windows (Hard Rule 5) because it handed `Bio.SeqIO.write` a bare path instead of an
  already-open UTF-8/LF text handle. Now opens the file itself first.
- Fixed #342 (found during this audit): `GffWriter`'s GFF3 column-9 percent-encoding
  omitted `%` itself, which the GFF3 spec requires escaping (it is the escape character).
  `%` is now escaped first, before the delimiter characters, so a literal `%` in a gene
  id can never be misread as part of another escape sequence.
- Fixed (orchestrator-reported unused-field findings, `scripts/check_unused_fields.py`):
  `predictors/hardware.py`'s `ModelRequirement.min_gpu_count` was set (evo2_40b needs
  two H100s) and never read by `require_hardware` -- a single-GPU H100 box would have
  passed the gate and failed later, deep inside multi-GPU model loading. `require_hardware`
  now takes an optional `gpu_count` and refuses with a named error when it is below
  `min_gpu_count`; `EvoPredictor.__init__` passes the real `torch.cuda.device_count()`.
  `ModelRequirement.precision` was also set and never read; it now appears directly in
  every `ModelNeedsHopperError` message. `analysis/windowing.py`'s `WindowPlan.ceiling`
  was set and never read; `DirectionResult` now carries the same `ceiling` value
  `analyze_direction` was called with, and `SummaryWriter`'s provenance section prints it,
  so a report can show why `window < 2*context_length` without reading notice text.
- Filed #346 (P3, `needs-criteria`): `predictors/hardware.py` fails OPEN (treats as
  bf16/no-Hopper) for an Evo model id not yet in `MODEL_REQUIREMENTS`; a future
  Hopper-only model added to the evo2 library before this table catches up would sail
  through the gate. Not fixed this wave -- needs an explicit policy call.
- Filed #347 (P3, `good-first-issue`): `check_probability_matrix`'s row-sum error message
  does not name NaN when a NaN probability is the actual cause (it is still caught, just
  via the row-sum-not-1.0 path, with a confusing "worst deviation nan" message).

- Fixed #330: a UTF-8 or UTF-16 byte-order mark broke FASTA and GenBank reading outright
  (rejected as "no records found") and a UTF-16 file decoded as UTF-8 turned into a wall
  of `�` replacement characters. Added the single shared
  `worker/src/dna_entropy/readers/encoding.py` that every reader (`paste.py`,
  `fasta.py`, `genbank.py`, `detect.py`) now decodes through: a UTF-8 BOM is stripped, a
  UTF-16 BOM (either byte order) is decoded in full, and everything else falls back to
  UTF-8 with `errors="replace"` (#294's fix, now centralized in one place instead of
  three, so it cannot drift into a fourth, fifth answer).
- Routing GenBank through this same decoder also fixed an unrelated inconsistency:
  Biopython's own file-opening used the OS locale encoding (cp1252 on this Windows box,
  MEASURED 2026-09-19), silently different from FASTA/paste's forced UTF-8, and would
  have decoded differently again on a UTF-8-locale Linux box.
- Docs: `docs/science_and_formats.md` section 4 now describes the shared decoder and
  the GenBank-locale-encoding fix.

- Fixed #331: a GenBank gene feature whose coordinates fell outside its own record's
  sequence length (a feature-table/ORIGIN mismatch -- truncation, hand-editing,
  corruption) was accepted silently, and would have let `writers/genbank.py` slice a
  wrong, out-of-range span for its mean-entropy note. `readers/genbank.py` now drops
  such a feature with a loud notice naming the gene id, its coordinates, and the
  record's real length -- rather than a hard failure for the whole record, so the
  record's other genes are still reported.
- Two deliberate exceptions, reasoned in the code: a feature carrying GenBank's own `>`
  partial-end marker is allowed to report an end past the record's length (GenBank's own
  way of saying "known to continue beyond what was given"; THEORY (unverified),
  confirmed only against this reader's own synthetic fixture); a compound
  (spliced/origin-wrapping) feature's segments must each individually fit in bounds
  regardless of a partial marker, which a legitimate origin-wrapping feature on a
  circular plasmid (issue #128's intended shape) already satisfies per-segment, so this
  check does not block it.
- New fixture: `worker/tests/data/out_of_range.gb` (a good gene, an out-of-range gene,
  and a legitimately-partial gene on one record).
- Docs: `docs/science_and_formats.md` section 4 documents the check and both exceptions.

- Round 2 audit: caught and fixed a Hard Rule 5 (ASCII-safe console output) violation in
  my own #331 notice string before reporting it done -- an em dash that a Windows
  console codepage mangled into `?` at runtime, found by actually running the real CLI
  end to end against the new fixture, not by reading the source.
- Round 2 audit, filed as issues rather than fixed (deeper pass over readers/ and
  validation/, per the shapes named in the round-2 brief): a U+FFFD-from-bad-encoding
  "Invalid character" error that never says it hit an encoding problem (#348); the
  GenBank reader letting Biopython's own parser exceptions escape as raw tracebacks on a
  missing ORIGIN block or lone-CR line endings (#349); `_safe_contig_name` accepting a
  Windows-reserved device name (CON, NUL, PRN, COM1, ...) or an unbounded-length id
  (#350); FASTA duplicate-header detection comparing the full header line instead of
  just the record ID, missing a real ID collision with a different description (#351);
  a grammatically broken "0 no gene feature(s)" summary notice (#352).

- Fixed #314: `readers/input.py::load_input()` validated `cfg.max_total_len` against each
  GenBank/FASTA record individually, never against the sum, so a multi-record file many
  times over the documented whole-input cap could pass cleanly one small record at a time.
  Both the GenBank and FASTA branches now track a running total across records and raise
  `ValidationError` (naming the length reached, the cap, and by how much it is over) the
  moment the sum exceeds the cap. Acceptance criteria were written into #314 as a comment
  before implementing (it carried `needs-criteria`); the issue's own per-record/whole-input
  framing was corrected in that comment against the real mechanism (`cfg.max_len` is a
  GPU per-window ceiling only, never a per-record validation bound — see
  `docs/science_and_formats.md` section 4).
- Filed #330 (P2): a UTF-8 BOM breaks FASTA and GenBank reading outright and gives a
  confusing "invalid character" error on the paste path; not fixed this wave.
- Docs: `docs/science_and_formats.md` section 4 (validation rules) now describes the
  encoding-robustness fix, the corrected whole-input-cap mechanism, and the
  compound-location notice behaviour.

- Fixed #295: a GenBank `join(...)`/`complement(join(...))` (spliced) gene location was
  silently collapsed to its outer bounding box, with no indication the reported
  begin/end (and any downstream mean-entropy figure) included intron sequence.
  `readers/genbank.py` now detects a Biopython `CompoundLocation` and raises an explicit
  notice per compound feature naming its exact exon segments; the bounding box itself is
  unchanged (a `GeneFeature` change would touch `annotators/base.py`, outside this lane) —
  see the tracked `DECISION` issue on adding per-exon `GeneFeature.exons`.
- New GenBank fixture `worker/tests/data/spliced.gb`: one plain gene plus two compound
  (spliced) genes, one on each strand, for #295's tests.
- Filed #331 (P2): the GenBank reader accepts a gene feature whose coordinates fall
  outside its own record's sequence length with no validation or notice; not fixed this
  wave.
- Filed #333 (DECISION): whether `GeneFeature` should carry per-exon segments so a spliced
  gene's mean-entropy figure can be computed from exon bases only; needs changes in
  `annotators/base.py` and `writers/genbank.py`, outside this lane's ownership.

- Fixed #294: `PasteReader.read()` crashed with `UnicodeDecodeError` on any non-UTF-8 byte
  in a pasted file or on stdin. Both branches now decode with `errors="replace"`, matching
  `readers/fasta.py` and `readers/detect.py`'s existing behaviour, so a bad byte reaches the
  normal validation-stage checks instead of crashing the process.

- Added `docs/branching_and_prs.md` and indexed it: nothing lands on `main` by a direct push any
  more, every unit of work is a branch, a pull request and a merge, one issue per PR. It records
  why `--merge` and never `--squash` (local `main` already holds the branch commit, so a squash
  makes the next `git merge --ff-only` fail), that CI here fires on demand only so a PR that looks
  green has been checked by nothing, and the local guard list to run before every merge.

- The .NET 10 SDK is now installed machine-wide at `C:\Program Files\dotnet\sdk` (10.0.401), by
  the owner from an elevated prompt, so `docs/onboarding.md` leads with that and keeps the
  per-user `dotnet-install.ps1` route for the unattended case only. The `PATH` and `DOTNET_ROOT`
  overrides that pointed at `%USERPROFILE%\.dotnet` have been removed, so the box has exactly one
  SDK and no ambiguity about which one a build used (#35).

- Documented the .NET 10 SDK install route that actually works unattended on the dev laptop.
  `winget install Microsoft.DotNet.SDK.10` fails with exit code 1602 because the machine-wide
  installer wants elevation and a non-interactive session cannot answer the UAC prompt;
  `dotnet-install.ps1 -InstallDir "$env:USERPROFILE\.dotnet"` needs no administrator. The
  shared host on `PATH` only finds SDKs beside itself, so a per-user SDK stays invisible until
  `PATH` and `DOTNET_ROOT` prefer it, which reads exactly like a failed install (#35).
- MEASURED 2026-09-19: an unpackaged, self-contained WinUI 3 app on .NET 10.0.401 with
  Microsoft.WindowsAppSDK 1.8.250916003 restores, compiles its XAML and builds with zero
  warnings using only the `dotnet` CLI, with no Visual Studio workload installed. The evidence
  and the four things still unverified are recorded on #35.

**2026-09-19: the first night.** The five entries below are this repository's entire
history to date - an empty scaffold to a documented, guarded, worker-gutted starting
point, in one overnight session across several branches merged in parallel. Read newest
first, as always (oldest to newest below: conventions/skills/hooks foundation, GitHub
labels/issue-forms/CI guards, the prototype's `dna_entropy.cloud` package gutted down to
what the C# port has to reproduce, the repo-safety hooks and diagnostics scripts ported,
and the design/science/job-contract reference docs, most recent), but the shape of the
night is the same either way: get the guardrails and conventions in place first, then
build on top of them, rather than writing product code before anything could check it.

- docs: add the design and science reference set: `architecture.md`, `job_contract.md`, `science_and_formats.md`, `cloud_design.md`, `gcp_setup_manual.md`, `ui_conventions.md`, `packaging_design.md`, `release_runbook.md`, `threat_model.md`. `science_and_formats.md` carries the coordinate systems each output format uses, since bedGraph is 0-based half-open and GenBank is 1-based inclusive and mixing them shifts every feature by one base in a way that looks plausible in a viewer. `job_contract.md` specifies `manifest.json`, `status.json`, `progress.jsonl`, `result.json` and `control/cancel` field by field as the contract #278 has to satisfy. `threat_model.md` is explicit about the size of the `cloud-platform` scope this app asks a lab user to grant. Closes #286, #27; progresses #26 and #287. For #287, no new issues were needed: every behaviour in the retired cloud package was already tracked, and the prototype's exact behaviour and line references went as comments onto the twelve issues that own them, with the mapping recorded in `docs/migration/2026-09-19-worker-migration-inventory.md`.

- chore(scripts): port the four repo-safety PreToolUse hooks (`block_git_stash`, `block_recursive_delete`, `block_agent_dispatch_in_worktree`, and the new `block_unlabelled_vm_create`), rebased onto Windows and PowerShell and covered by tests that exercise both arms of every guard; port `issue_precheck.py` and `compile_sprint_log.py` with this repo's real label set, milestones and repo name; port `sync_memory.py` with the secret scan that is the point of it, since this repo is public; and port `triage_diagnostics.py` against the documented manifest and status schema, with every field path in one `SCHEMA_FIELDS` block. Two real defects were fixed in the port: a hard-coded `post-alpha` milestone check that could never fire in this repo, and `find_stray_changelog_dirs()`, which was defined and never called. Closes #271, #272, #273, #274. Files #299 and #300 for the two pieces of the donor's automation that have no home here yet.

- refactor(worker): gut the prototype's `dna_entropy.cloud` package and its CLI verbs, extracting first what the C# port has to reproduce. The quota regexes, the error taxonomy and its evaluation order are now shared JSON vectors at `tests/contract-fixtures/`, in the repo root where the future xUnit suite reads the same files rather than a second copy. The package itself moves to `worker/legacy/cloud/` with a provenance note, because `keeper.py`'s setup diagnosis is still unported (#213) and a deleted file is a bad place to read it from. `cloudrun` and `keep-gpu` leave the CLI, `keep_gpu.py` and `keep-gpu.spec` go, and `pyproject.toml` drops the PyInstaller extras. Suite went from 147 passed / 2 skipped to 92 passed / 2 skipped: 66 tests left with the module they covered, 11 new ones replaced what still applies. Closes #284, #280, #277, #285. Six bugs found while reading the prototype are filed as #291 to #296.

- ci(packaging): add `.github/`: `labels.yml` as the source of truth for the 22 labels, issue forms that carry the Done-when and Observable contract so an issue filed through the UI cannot skip it, a decision form that demands a recommendation and a reversal cost, the PR template, `dependabot.yml` with the CUDA-pinned packages deliberately ignored, and the `ci-worker`, `ci-docs`, `ci-app` and `codeql` workflows. Every step that guards a file which does not exist yet prints a GitHub notice, passes, and starts enforcing the moment that file lands, so the pipeline tightens itself instead of needing an edit per issue. `ci-docs` adds two guards the repo did not have: the private conventions donor must never become a tracked file, and no absolute user-home path may appear in one. The second is `scripts/check_user_home_paths.py`, which allows a fictional account name in an example, rejects a real one, self-tests both arms, and found three more real paths on its first run. Closes #29, #297; progresses #31 and #197.

- docs: rewrite `CLAUDE.md` from the CLAIR template (21 Hard Rules, Stack table, Critical Pitfalls), add the `AGENTS.md` stub, port the 5 ADOPT skills plus `working-on-gcp` and `winui-dev`, seed `.claude/memory/` with 27 lesson files, add `.claude/agents/cold-diff-reviewer.md` and `.claude/README.md`, derive `.claude/settings.json` from the Appendix C template, author `.editorconfig`/`.ignore`/`OWNER_TODO.md`, and draft the developer half of the day-one `docs/` set (`README.md`, `hard_rules.md`, `entry_points.md`, `onboarding.md`, `dev_commands.md`, `tests.md`, `project_structure.md`, `environment.md`, `tech_stack.md`, `ToTest.md`, `sprint_log.md`, `changelog.d/README.md`). Closes #267, #268, #269, #270, #275, #289, #290 (partial: developer half of #25, #276, #30). Files a DECISION issue (#301) for a GPLv3 dependency (`pyrodigal`) found while writing `tech_stack.md`, and a P0 issue (#297) for two pre-existing literal user-home paths the new `ci-docs.yml` guard would have failed on.
