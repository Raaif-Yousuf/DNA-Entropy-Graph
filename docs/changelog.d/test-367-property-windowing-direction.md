- Issue #367 (parent #160, windowing and direction scope): added the real, Hypothesis-
  generated property tests `#160` itself filed as follow-up work once `hypothesis` could
  be installed. New file `worker/tests/test_property_windowing_direction.py`, generating
  rather than table-driving: window coverage (every position covered by exactly one
  winning window) and the closed-form pass-count formula over the whole valid `(L, K,
  ceiling)` space (`k` 1..5000, `length` 1..50000); the Hard Rule 3 `(L, 4)` contract
  (shape, dtype, row-sum, entropy bound) for generated A/C/G/T sequences and seeds;
  entropy invariance under the reverse-complement column permutation for generated
  probability distributions; and the seam recorded in provenance matching where the
  combiner actually switched, checked against an independently computed `BOTH_SEPARATE`
  run for generated `(K, L)` pairs. The existing table-driven tests in
  `test_windowing.py`/`test_direction.py`/`test_entropy.py` (landed under #160) are
  unchanged and stay as real regression coverage, not superseded.
- `hypothesis` added to `worker/pyproject.toml`'s `dev` extra. MEASURED 2026-09-19: it is
  MPL-2.0, not MIT as #368's own issue body assumed without checking (confirmed against
  the installed package's own `License-Expression: MPL-2.0` metadata) -- Hard Rule 21's
  text scopes the MIT/Apache/BSD requirement to "anything that ships", and this extra is
  explicitly dev/CI-only (never installed into the container image), but
  `docs/hard_rules.md`'s own carve-out section records no dev-only-dependency carve-out
  yet, so a `DECISION (agent-made, reversible):` issue was filed recording the call rather
  than adding it silently. `sortedcontainers` (MIT), hypothesis's own dependency, was
  pulled in transitively.
- Every property has an explicit `@settings` budget (`max_examples=200` for pure
  Python/NumPy properties, `40` for properties driving `MockPredictor.predict`), never the
  Hypothesis default, and `deadline=None` (a shared box running six concurrent agents
  makes wall-clock-based flakiness a false property failure). Every property was mutation-
  checked by patching the real function it exercises in memory (never editing
  `analysis/windowing.py` or `analysis/direction.py`, which are Lane E's) and watching the
  test go red on a planted bug, then restoring: a dropped `+1` in the window pass-count
  formula, a column-asymmetric `shannon_entropy`, and an off-by-one `>`/`>=` threshold in
  the forward/reverse combiner were each caught.
- `docs/tests.md` updated to name the new property test file and its budget.
