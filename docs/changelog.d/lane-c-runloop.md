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
