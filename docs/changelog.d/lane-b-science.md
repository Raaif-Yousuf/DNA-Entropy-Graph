- Fixed #346 (DECISION recorded at #364, agent-made and reversible): `predictors/hardware.py`
  now fails CLOSED for an Evo model id not in `MODEL_REQUIREMENTS`, raising a new
  `UnknownModelError` (`code = "MODEL_UNKNOWN"`) naming the bad id and every supported id,
  before any Hopper/gpu-count check. `model_requirement()`'s own permissive lookup default
  is unchanged; none of the four real `MODEL_REQUIREMENTS` entries changed.
- Fixed #347: `check_probability_matrix`'s row-sum error did not name NaN when a NaN
  probability was the actual cause (it was already caught, just via a confusing "worst
  deviation nan" message). Now checks for NaN explicitly and names the affected row
  indices before falling through to the range/row-sum checks.
- Fixed `WindowPlan.context` (the field the lane-B round-1 report flagged and correctly
  did not fix): unlike `WindowPlan.ceiling` (which had a genuine gap -- nothing else
  recorded it, so it was threaded into `DirectionResult`/`SummaryWriter`), `context` (K)
  was already independently recorded on `DirectionResult.context_length`, sourced from
  the same parameter every caller already has in scope. Deleted rather than wired up, with
  the reasoning recorded in `WindowPlan`'s own docstring.
- Issue #160 (property-based tests), windowing and direction scope only (validation is
  Lane A's, deliberately not covered): `hypothesis` is not installed on this laptop and
  nothing was installed to get it, so this is the table-driven equivalent over a
  deliberately adversarial `(L, K, ceiling)` grid. New tests: window coverage for
  `L < K`/`L == K`/`L == 2K`/`L == 2K+1`/`L == 1` across K-bound and ceiling-bound windows
  (`test_windowing.py`); entropy invariance under the complement column permutation and
  entropy bounded in `[0.0, 2.0]` across a degenerate-row/softmax-temperature grid
  (`test_entropy.py`, `test_direction.py`); the seam position matching where the combiner
  actually switched, checked against an independent `BOTH_SEPARATE` run rather than
  asserted `== K` on faith (`test_direction.py`). All use a tolerance, never exact float
  equality. Each was mutation-checked against a planted bug (an off-by-one in
  `plan_windows`'s pass-count formula, an off-by-one in `_combine`'s context threshold, a
  column-asymmetric bug in `shannon_entropy`) and caught it. The real Hypothesis version is
  filed separately as #367.
