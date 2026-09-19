- Fixed #407 the same session it was filed: reproduced the theorized
  `DirectionResult.context_length` staleness by hand with an OOM-halving stub run all the
  way through `analyze_direction()` (not just `run_windowed()`), and found the theory was
  half right. Disproven part: `context_length` reporting the nominal, configured K after a
  halving retry is legitimate, not a bug. Real bug found one layer down:
  `analysis/direction.py::_combine` used that same stale, shared `context_length` as the
  qualification threshold (`fwd_context >= context_length`) for BOTH directions, even
  though an OOM-halving retry (issue #313) can shrink ONE direction's actually-achieved K
  independently (each `run_windowed` call only reacts to its OWN OOM). A halved direction's
  context is then capped below the un-halved, stale threshold, so it can never "qualify"
  again for the rest of the sequence — combined/averaged mode silently collapses to the
  other direction alone, with no error or notice.
- Fixed: `_combine` now takes `fwd_context_length`/`rev_context_length` separately,
  computed in `analyze_direction()` as each direction's own `window - stride` (always
  accurate regardless of a halving, same derivation `writers/provenance.py::contig_provenance`
  already used for `k_used`). Backward compatible: in the non-halved case,
  `window - stride == context_length` exactly, so every existing test was unaffected.
- Tests: `worker/tests/test_direction.py::test_both_combined_after_a_forward_only_oom_halving_lets_forward_requalify_at_its_own_k`
  reproduces the exact scenario (a shared predictor OOMs on its first call, landing during
  the forward pass) and asserts the combined track matches forward-only at a position that
  qualifies under forward's own post-halving K but not under the stale nominal K.
  Mutation-checked: reverted the per-direction thresholds to the shared `context_length`
  by hand, watched the test go red with the exact numbers hand-derived before the fix
  (1.0925992 vs 1.6316695), restored byte-exactly.
