---
name: fixing-a-bug
description: Use when fixing any bug, regression or defect from an issue, a diagnostics zip or a field report, before writing fix code. Also use when a fix "should work" but the symptom persists, or when a green suite hides a wrong behaviour.
---

# Fixing a bug

A green test suite is not evidence. This repo's own prototype history has
features that compiled, ran, passed their tests and did nothing (see
`wired-to-nothing`), and the Evo 2 predictor boundary alone hides three
distinct dtype/shape bugs that never raised an exception (see
`bug-shapes.md`). The order below exists because each step catches a
failure the next step cannot.

## The order (do not reorder)

1. **Write the failing test FIRST, at the symptom level.**
2. **Watch it fail**, and read the failure message.
3. **Broaden it into a matrix.**
4. **Mutation-check: break the code on purpose** and confirm the tests go red.
5. **Narrow the cause**: whole repo to a few files to roughly 100 lines.
6. **Fix it.**
7. **Revert-check**: undo the fix, confirm the new tests fail, restore.
8. **Full green**, plus the guard set.

## The diagnostics zip is the field report

Before screenshots, before asking the user to describe it again:

```powershell
worker\.venv\Scripts\python.exe scripts\triage_diagnostics.py <path-to-zip>
```

It prints the app version, worker version, container image digest, the last
several `JobPhase` transitions, any `CloudError` classes seen, and the last
50 progress lines. It reads no sequence data and no file names — those never
leave the user's machine (Hard Rule 14, `docs/threat_model.md`).

## 1. Failing test first, at the SYMPTOM level

Write the test from the **reported observable**, not from your theory of the
cause. You do not need to know the cause yet, and not knowing it is an
advantage: a test written from the symptom tests behaviour, and a test
written after you find the cause tends to test your fix instead.

State the symptom as an assertion someone would recognise:

- "a run against a billing-disabled project reports stockout"
- "the reverse track is not the reverse complement of the forward track"
- "the VM is still running an hour after `maxRunDuration` expired"

**If you cannot write a failing test from the symptom, that IS the finding.**
Say so in the issue, name what makes it unreachable (needs a packaged build,
needs a real GCP project, needs a real GPU), and add a `docs/ToTest.md` row.
Then narrow first and come back. Do not skip to fixing because the test was
awkward to write.

## 2. Watch it fail, and READ the failure

Not "it errored". The message must describe the real defect. A test that
fails with an `ImportError` or a `KeyError` on your own fixture is not yet a
test of anything.

**Doubt the harness before you doubt the product.** A console-encoding
"bug" can be the Windows terminal mangling a `print()` call while the stored
value round-trips exactly — check the value as written to a file (Hard Rule
5: ASCII-safe console, UTF-8/LF files), not as displayed in the terminal.

## 3. Broaden into a matrix

One reproduction is an anecdote. Sweep the dimensions the bug actually lives
in and assert against the **stated rule** (a Hard Rule, a contract in
`docs/job_contract.md`), not against current behaviour.

For this repo's shape, the standing matrix is usually some subset of: input
length relative to the window (`L < 2K`, `L == 2K`, `L > many windows`),
Forward-only vs combined direction, every `CloudError` class, and every
`JobPhase`. A fix that passes its one targeted test and the whole suite can
still fail two rows over — sweep before declaring victory.

## 4. Mutation-check: break the code on purpose

**Mandatory, not optional.** Before trusting any test or guard, damage the
production code and confirm the test goes red with a message that names the
problem. Then restore.

For a guard or ratchet, prove **every arm** fires. A three-arm guard needs
three demonstrations — and **an arm that stays GREEN is the finding, not a
pass.** A green arm means no test can see that part of the fix.

**Assert the VALUE, never mere presence.** `"quota" in classes` can pass with
the production classification branch deleted, if the fake's own default
happens to supply that string.

**Every scanner needs a vacuity assertion.** If your test walks files or
resources, assert it found a plausible number of them. A scanner that
silently matches nothing reports a clean tree.

## 5. Narrow the cause

Only now go looking. Repo to a few files to roughly 100 lines. Useful moves:

- `git log -S'<literal>'` to find when a value changed
- Compare the failing input against the nearest passing one
- Check the **pair**, not each half — a bug can live in the relationship
  between two individually-correct values (a label written one way, read
  another; a schema field named one way in `manifest.schema.json` and
  another in the C# DTO that deserializes it)
- `python scripts/issue_precheck.py <n>` decides from the commit graph
  whether it already shipped

**A link-by-link trace is not proof.** Every hop in a chain can look present
while a runtime precondition (a settings watermark, a gated flag) keeps the
whole thing from ever firing.

**Run it, do not read it.** For the worker, that means running the real
pipeline on a small real input, not reasoning about it from the source. Where
the path produces an artefact, read the artefact **back** with a real
reader — Biopython on the `.gb`, a bedGraph parser on the track, igv.js
actually loading it — never the writer's own return value.

## 6. Fix it

Fix the cause you narrowed to, not the symptom. If the issue body proposes a
fix, verify the diagnosis yourself first — issue bodies can carry a wrong
theory of the cause.

Hard Rule 18: every causal claim carries `MEASURED <date>:` plus the
observation, or `THEORY (unverified):`.

### If an existing test goes red against your fix, the TEST may be the bug

When a test's assertion **is** the reported symptom (it asserts the buggy
behaviour as intended), the test is the defect. Replace it in place — Rule 18
replaces a disproved claim rather than appending a correction elsewhere — and
say so in the commit, instead of shaping the fix to keep the old test green.

### Before you write a new helper, check these, in order

1. **Does this already exist in `worker/src/dna_entropy/` or
   `app/src/DnaEntropyGraph.Core/`?** Grep for the concept's vocabulary, not
   the function name you were about to type.
2. **Does an already-referenced dependency do it?** numpy, Biopython,
   `Google.Cloud.*`, CommunityToolkit.Mvvm each cover more than they get used
   for.
3. **Only then write it** — and if you deliberately wrote a second
   implementation, say why in a comment next to it, because the next reader
   will read the pair as drift, not intent.

**When you find the existing one, do not just call it: check the two
agree.** The failure is never "we wrote it twice", it is "we wrote it twice
and they disagree by one entry" (see `bug-shapes.md`, hand-maintained
denominator).

## 7. Revert-check

Undo the fix. Run the new tests. **They must fail.** Restore.

Report honestly which assertions failed and which passed anyway. An
assertion that passes both ways is a property invariant, not a regression
guard, and calling it a guard is how a suite drifts into decoration.

**If the test does not go red, the correct read is not "the fix was
unnecessary."** It is "find an assertion that can see the difference." A
self-healing writer (a schema migration that no-ops if already applied, a
cache that self-corrects on next read) can produce the identical end state
whether or not the fix is present — the fix there needs a **source-level**
assertion (the file no longer contains the old literal) beside the runtime
one.

## 8. Green, plus the guard set

The tests you touched, plus the fast structural set for whichever half you
changed:

```powershell
dotnet test app/tests/DnaEntropyGraph.Guards.Tests
worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu"
```

Structural gates go red at **merge** time, not in your worktree. Deleting
code trips the stale arm as surely as adding code trips the missing arm.
Document the new module; never raise a baseline just to make a gate pass.

**If you touched anything under `app/src/**`, `app/tests/**`, or
`worker/src/dna_entropy/**`, run the relevant targeted tests before you
commit** — the merge-time guard (`dotnet test .../Guards.Tests` plus the
named `scripts/check_*.py` scripts) is what finds an unregistered service or
an undocumented module before it costs a full merge cycle.

## Before you say it is done

Name the **one observable that would differ if your change were wired to
nothing**, and go check it. Not a trace. Not "the test passes". A thing you
can see: a rendered string, a log line, a labelled resource that shows up in
`CloudCli resources list`, a track that visibly differs on the seam.

## Red flags: stop and go back a step

- "The test passes, so it works" (a check you never saw go red is a claim about the harness, not the bug — did you break the code and watch it fail?)
- "I traced every call site" (that is not the check)
- "The suite is green" (a green suite has hidden every instance of the house bug before)
- "Every mutation arm passed, so the fix is solid" (a green arm is a coverage hole, not a pass)
- "An existing test contradicts my fix, so my fix must be wrong" (the test can be the bug — check which one)
- "I read the code carefully" (running a real input finds bugs that reading misses)
- "I will write the test after the fix" (you will test your fix, not the bug)
- "The issue says the cause is X" (verify it; issue bodies can carry a wrong theory)
- "It is a one-line change" (a one-line change can land inside a comment and every test stays green)
- "I regenerated the baseline" (a ratchet you regenerate to silence is not a ratchet)
- "I could not reproduce it, but the fix is obviously right"

## Recurring bug shapes worth grepping for

One line each, so you recognise a shape while standing in front of it. The
elaboration — the exact mechanism, what to grep for, what this repo has
already recorded — is in `bug-shapes.md` beside this file; open it when a
line below matches what you are looking at.

- **The predictor-boundary contract** (the highest-value item here). Evo 2's
  nested-tuple return, uint8-as-bool-mask, and no-BOS row 0 have each caused
  a silent, non-crashing wrong answer. Do not "simplify" `_extract_logits` or
  the cast.
- **Billing-vs-stockout misclassification.** Billing disabled, the Compute
  API disabled, and real zonal stockout look identical from the caller's
  side. Only `quota` and `stockout` are worth retrying.
- **A hand-maintained denominator.** A guard that checks "every X is
  covered" cannot see the X nobody added to its list. Widen the predicate,
  never narrow the scope.
- **The gate you ran vs. the gate that ships.** `FakeGcp` proves ViewModel
  logic; it proves nothing about the real Google client. A laptop pytest run
  proves control flow; it proves nothing about GPU memory behaviour.
- **A message that reports its branch's INTENT, not the OUTCOME.** Derive
  any string asserting an outcome from what actually happened, never from
  the branch that decided to act.
- **Copied posture.** A retry policy correct for `stockout` is wrong for
  `billing`; a fire-and-forget write is right for a heartbeat and wrong
  where the write is the run's own history record.

## Rationalizations

| Excuse | Reality |
| --- | --- |
| "I can see the bug, the test is ceremony" | You can see *a* bug. A matrix sweep routinely finds a second one nearby. |
| "Breaking the code to test the test is overkill" | It is the only way to catch a vacuous guard or a call landing inside a comment. |
| "The symptom is hard to reach from a test" | Then say so and add a ToTest row. That is a finding, not a reason to skip. |
| "My guard obviously works" | Assert the value it checks, then break the thing it checks, and watch. |
| "Regenerating the baseline records real progress" | Only if the code shrank. If you just grew it, that is banking a number. |
| "The agent report says it is done" | Check the diff, not the summary. |
