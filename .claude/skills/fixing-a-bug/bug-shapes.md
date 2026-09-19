# Recurring bug shapes: the elaboration

Supporting file for the `fixing-a-bug` skill. `SKILL.md` carries a one-line
index of these shapes with the fact that makes each one land; this file
carries the detail. Read the shape here once the index line matches what you
are standing in front of.

## The predictor-boundary contract (the highest-value item here)

The Evo 2 predictor boundary is the single place this project has already
been burned by trusting a model's output shape at face value. All three are
**MEASURED on a GCP L4, prototype Sprint 3**, and the contract guard at
`check_probability_matrix` exists specifically to stop them recurring:

- **Nested-tuple unwrap.** `evo2`'s forward call does not return a bare
  logits tensor; it returns a nested tuple, and the prototype's
  `_extract_logits` exists only to unwrap it correctly. "Simplifying"
  `_extract_logits` because the nesting looks redundant has broken this
  before — the nesting is real, not defensive padding.
- **uint8-as-bool-mask.** The tokenizer returns token ids as `uint8`. torch
  silently reinterprets a `uint8` tensor as a boolean mask in several
  indexing contexts instead of raising, so an uncast `uint8` array produces
  wrong-but-plausible output with no error at all. Cast to `int` explicitly
  at the boundary; do not rely on an implicit numpy-to-torch conversion.
- **No-BOS row 0.** The tokenizer adds no beginning-of-sequence token, so the
  output array has exactly `L = len(seq)` rows, not `L + 1`. In Forward-only
  mode this makes row 0 uniform (2.0 bits of entropy) **by design**, not by
  bug — do not "fix" row 0 by inserting a synthetic BOS row, and do not read
  a uniform row 0 as evidence of a broken forward pass without checking
  whether combined (bidirectional) mode is in play, which resolves it from
  the reverse pass.

**Check for this class:** any change touching `predictors/evo.py` or the
`(L, 4)` contract boundary needs a test asserting the shape, dtype and
row-sum invariants from Hard Rule 3, run against a real small sequence, not
just a mocked tensor shaped by hand — a hand-shaped mock cannot reproduce a
dtype or nesting bug it was never told to have.

## Billing-vs-stockout misclassification

**MEASURED in the prototype's real misconfigured GCP project.** Billing
disabled, the Compute API disabled, and genuine zonal stockout produce
**visually identical** `Insert` failures from the caller's point of view —
all three read as "cannot create the resource right now." The prototype
initially treated all cloud creation failures as retryable, which meant a
billing-disabled project retried forever against an error that could never
resolve itself.

The fix is `CloudErrorClassifier`, which buckets every error into `billing |
api_disabled | quota | stockout | already_exists | permission | org_policy |
network | other`, and the app-side rule that `billing`, `api_disabled`,
`permission` and `org_policy` **abort immediately with a named user action**,
while only `quota` and `stockout` are worth a retry or a zone-ladder attempt.

**Check for this class:** any change to preflight or error handling needs a
test with a **recorded real payload** for each class (Critical Pitfalls),
not a synthetic error message invented to make the classifier pass. Confusing
quota (per-region, fixable by the user, pre-filterable) with stockout
(per-zone, transient, the only thing actually worth retrying) reproduces the
same shape one level down.

## A hand-maintained denominator

A guard that checks "every X is covered" cannot see the X nobody added to
its list, and a duplicated roster is that same defect written twice — a
label list, a `CloudError` class enum, and a `JobPhase` state table are all
candidates in this repo. **Fix by widening the predicate, never by narrowing
the scope.** Narrowing a guard to the sites it already sees moves the blind
spot rather than closing it, while turning the report green, which is why
this shape keeps recurring across projects. Prefer a whole-tree census over
a roster, and an AST-level check over a substring/regex one wherever the
guard lives in `Guards.Tests` or `scripts/check_*.py`.

**Fixing one member of a hard-coded list?** Grep for the list's *members*,
not the module, to find every sibling copy — two lists that "compute the
same thing" routinely drift by exactly one entry.

## The gate you ran vs. the gate that ships

Before trusting a green check, name what differs between the context you ran
it in and the context the code ships to. This project's two clearest
instances of the shape:

- A test run against `FakeGcp` (Hard Rule 7) proves the ViewModel logic is
  correct; it proves nothing about whether the real `Google.Cloud.*` client
  actually behaves the way `FakeGcp` was written to assume.
- A worker test run on the laptop (`pytest -m "not gpu"`) proves the
  pipeline's control flow; it proves nothing about GPU memory behaviour,
  which only a real run via `scripts/cloud_gpu_test.ps1` can show.

## A message that reports its branch's INTENT, not the OUTCOME

Any string asserting an outcome (a run summary, a diagnostics line, a
`.resw`-backed error) must be derived from the outcome that actually
occurred — count what was written, then describe *that* — never phrased from
the branch that decided to act, however certain that branch's condition
looked. A progress line that says "uploaded" because the upload branch was
entered is not evidence the upload finished; read the object back from the
bucket, or read the file the writer claims it wrote.

## Copied posture

Copying a sibling call site copies calibrations that were made for a
different consequence: a retry policy correct for `stockout` (transient,
worth retrying) is wrong for `billing` (permanent, must abort), and a
fire-and-forget write is right for a progress heartbeat and wrong where the
write **is** the run's own history record.
