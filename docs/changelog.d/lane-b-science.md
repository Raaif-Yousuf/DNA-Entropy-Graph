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
