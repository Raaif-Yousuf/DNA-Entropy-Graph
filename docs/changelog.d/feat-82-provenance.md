- Added `writers/provenance.py` for #82: `provenance.json`, written unconditionally
  (never gated by an `include_*` flag) into `cfg.out_dir` at the end of every
  `pipeline.run()`, including a partial/cancelled run's best-effort partial write
  (`docs/job_contract.md` §6: "partial results are always kept, never discarded"). Records
  worker version, predictor kind/model/device/seed, run direction/ambiguity
  policy/RNA flag, per-contig windowing/direction (`context_length`, `window`, `stride`,
  `k_used`, `ceiling`, `direction`, `seam`, `reduced_context_count`), and wall time.
- `pipeline.run()` grew a `provenance_extra` keyword: the worker-layer-only fields this
  module cannot know on its own (GPU name/driver, torch/evo2/flash-attn versions,
  container image digest, input sha256 — none of which this package can compute without
  either importing `torch` outside `evo.py`, forbidden by Hard Rule 1, or seeing the raw
  input bytes the worker layer downloads before `pipeline.run()` is ever called) default
  to `None` and are overlaid additively when a caller supplies them. `worker/runner.py`
  (out of this lane) is the natural caller for that overlay; see this session's report for
  the exact call shape.
- `k_used` is deliberately `window - stride`, not the nominal `context_length`:
  `DirectionResult.context_length` is not updated after an OOM-halving retry (issue #313)
  shrinks K, so it can disagree with the K a halved pass actually ran with. Filed as #407
  (THEORY, then reproduced by hand and fixed the same session): the theory that
  `context_length` "going stale" was itself the bug was disproven — reporting the nominal
  K is legitimate — but reproducing it surfaced the REAL bug one layer down:
  `analysis/direction.py::_combine` used that same stale, shared value as the
  qualification threshold for BOTH directions, so a one-sided halving could permanently
  lock the halved direction out of ever "qualifying" again, silently collapsing
  combined/averaged mode to the other direction with no error. Fixed in `_combine` itself
  (see `fix-407-combine-per-direction-context.md`); `k_used`'s derivation here was already
  correct and needed no change.
- This also closes the remaining "provenance.json records the final W, S" half of #81's
  Done-when (its OOM catch/halve/retry half was already implemented); #81 itself is not
  closed here since closing an issue is the orchestrator's call, not this lane's.
- `docs/science_and_formats.md` was not touched for this specific issue (it already had
  no provenance.json section to correct); `docs/job_contract.md`'s `output/provenance.json`
  layout line (section 2) already named this file before this work started.
- Tests: `worker/tests/test_provenance.py` (10 new: shape, the issue's own named
  Observable — two builds of the same config differ only in `generated_at`/
  `wall_time_seconds` — the OOM-halving `k_used` derivation, worker-layer-field
  defaults/overlay, and `ProvenanceWriter`'s JSON/UTF-8/LF output), plus 5 new tests in
  `worker/tests/test_pipeline.py` (always written, records seam/reduced-context, the
  two-runs reproducibility Observable end to end, and a partial/cancelled run still gets
  one). Mutation-checked: removed the `_write_provenance` call and the `k_used` derivation
  by hand in turn, watched the matching tests go red, restored byte-exactly.
