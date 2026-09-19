- Corrected #345: an earlier pass this session concluded no cross-check was needed for
  `analysis.stride`, reasoning that `S = W - K` is a pure function of `context_length` and
  `ceiling` with no independent degree of freedom. That reasoning was right about the
  WORKER's own windowing math (`analysis/windowing.py::compute_window`/`plan_windows` have
  no parameter to accept an externally supplied stride) but wrong about the manifest
  contract: `worker/manifest.py::AnalysisSpec` parses its own `stride` field independently
  of `window`/`contextLength`, and `build_run_config` reads `analysis.window` (as
  `RunConfig.max_len`) but never `analysis.stride` — a manifest declaring a `stride` that
  disagreed with `window - contextLength` was silently ignored rather than refused.
- Fixed: `AnalysisSpec.from_dict` now cross-checks `stride == window - contextLength` at
  parse time and raises `ManifestError` naming both numbers on a mismatch — the same
  validate-and-refuse pattern already used for `predictor.precision` (issue #343),
  `analysis.direction`, `analysis.format`, and `inputs[].ambiguityPolicy`/`fastaRecords` in
  the same file.
- `docs/job_contract.md`'s `analysis.contextLength`/`window`/`stride` row is corrected to
  state plainly that `window` is consumed and `stride` was not, rather than repeating the
  earlier "settled, no gap" claim.
- Kept `worker/tests/test_windowing.py`'s two invariant-locking tests (they document a
  true, narrower claim about the windowing module itself, not the manifest contract) but
  corrected the surrounding comment block, which had drawn the wrong conclusion from a
  true premise.
- Tests: `worker/tests/test_worker_manifest.py` (3 new: a matching stride parses, a
  disagreeing stride is rejected naming both numbers, and the defaults are internally
  consistent so a manifest omitting `analysis` entirely still parses); one pre-existing
  fixture (`test_unknown_nested_fields_are_tolerated`) needed its `analysis.window`/
  `stride` values updated to stay consistent with its `contextLength` override, since it
  had been incidentally relying on nothing checking that before now. Mutation-checked:
  disabled the cross-check condition by hand, watched the new rejection test go red,
  restored byte-exactly.
