# Handoff: after the 2026-09-19 six-lane wave

> Overwritten every session. Re-check `git log -1 main` and `gh issue list` before trusting
> anything here. Counts are deliberately not written down; run
> `gh issue list --repo Raaif-Yousuf/DNA-Entropy-Graph --limit 500 --json number,labels,milestone`
> for current numbers.

## What this session was

One orchestrator and **six** Sonnet lanes (raised from three mid-run), in one shared checkout,
each owning a disjoint set of paths, with the orchestrator holding every `git` command. No lane
ran `git` at all. No path collision all night.

**Seventeen pull requests, one per issue wherever the files allowed.** Every test count in every
commit message was re-run by the orchestrator before the commit, not quoted from an agent's
report.

**The app suite went from 51 tests to 316.** The worker suite is at 814. `scripts/tests` is at
340.

## Start here next session

1. **Launch the app.** It has still never been run, and there is now a great deal more to see
   than there was: a `NavigationView` shell, a run progress page, Mica, a status pill, theme at
   startup, window placement persisted through a real SQLite-backed settings store.
   [`docs/ToTest.md`](docs/ToTest.md) has the row and names the two false passes precisely. The
   one to watch: a `.resw` that is not packed as a PRI resource gives a window whose every label
   is **blank**, which reads as an unfinished layout rather than a broken build. Check
   `ShellTitle`, `NavNewRun`, `NavRuns`, `NavCloud`, `NavSettings` and the `RunProgress*` set
   specifically, because not one of them has ever been seen rendered.

2. **Only five `v0.1 walking skeleton` issues remain open**: #204 (SetupHealthService), #205 (IAM
   permission preflight), #258 (token refresh and retry policy), #261 (startup-script templating)
   and #266 (the GPU pricing research note, owner-flagged). The milestone is within reach.

3. **`OWNER_TODO.md` has grown again.** Seven new `DECISION` issues were filed this wave, each
   marked agent-made and reversible, each carrying its reasoning: **#402** (does Hard Rule 21
   reach a dev-only MPL-2.0 test dependency), **#413** (does it reach the Windows App SDK's
   proprietary redistributables), **#404** (how `app.db` corruption is handled on launch),
   **#408** (`Notes`/`TagsJson` columns added ahead of #141), **#386** and **#387** (two cloud API
   shapes), **#394** (a RunOptions option set that two spec sections disagree about). **#301,
   #402 and #413 all point at the same unwritten section of `docs/hard_rules.md`** - rule 21
   still reads "Carve-out: None currently recorded" while the notices generator now documents
   the `pyrodigal` GPL-3.0 exception mechanically. That is one edit, not three.

## What exists now that did not this morning

- **A real app.** Shell, run progress page, navigation, theme, window placement, a string
  resource provider, and a SQLite run history with forward migrations under `PRAGMA
  user_version`, WAL, and four repositories.
- **A real cloud layer.** `FakeGcp` with scripted failures (billing off, API off, quota,
  stockout, 403, org policy, preempt, already-exists, network), `CloudErrorClassifier` driven by
  recorded fixtures with their evaluation order, `VmSpec` refusing a spec the real API would
  reject, an operation poller with backoff and deadlines, and `CloudJobRunner` end to end over
  the fake.
- **Surprisal is wired.** It had a module and thirteen passing tests and no caller. It now has a
  config flag, a CLI flag, three track files, a TSV column and stats, computed inside the
  existing forward/reverse pass with zero extra predictor calls.
- **`provenance.json`**, written unconditionally, including on partial and cancelled runs.
- **`THIRD-PARTY-NOTICES.md` exists**, generated, with a staleness check that is no longer a
  no-op passing because the file it checked did not exist.
- **Two new guards**: `scripts/check_app_wiring.py` (nine finding codes for the C# half of
  wired-to-nothing) and `Guards.Tests/DialogStringLiteralScanner`.
- **Property-based tests** (Hypothesis) for windowing, direction and the readers.

## What was found that is worth more than the fixes

**`_combine()` silently collapsed combined mode to a single direction.** After an OOM halving
shrank `K` in one direction, `_combine()` compared both directions against the same stale shared
threshold, so the halved one could never re-qualify. No error, no notice, a wrong number in the
user's output file. Found by asking the lane that filed #407 as a `THEORY` to resolve it rather
than leave its workaround standing; the theory itself turned out to be half wrong, and the real
bug was one layer below it.

**A raw `AssertionError` was escaping the GenBank reader.** Biopython's scanner has bare `assert`
statements, and a feature qualifier missing its leading `/` hits one. `except ValueError` did not
catch it. This **disproved a claim this repo had already landed** with `MEASURED` and a fixture
behind it (#349's "every malformed-content failure this scanner raises is a ValueError"). It was
true of every input anyone had thought to try. Found by property-based fuzzing; 38 example-based
GenBank tests were green and stayed green.

**`analysis.stride` was parsed, never read, and the conclusion that this was fine had already
been written into `docs/job_contract.md` as settled.** `analysis.window` *is* read (as
`max_len`); `stride` was not, so a manifest declaring a disagreeing stride was silently
discarded. `check_unused_fields.py` could not see it because it matches attribute reads by name
and `DirectionResult.stride` is read - a live instance of the false negative that script
documents about itself.

**A new copy guard found three real Hard Rule 13 violations on its first run**, and a false
positive in an existing guard (`AlwaysShowHeader="True"` read as an inline "Header" string, for
want of a word boundary). Both now have regression tests.

**A high-severity advisory rode in on a new dependency.** `Microsoft.Data.Sqlite` 9.0.9 pulled
`SQLitePCLRaw.lib.e_sqlite3` 2.1.10 (GHSA-2m69-gcr7-jv3q). It was caught only because
`TreatWarningsAsErrors` turns NU1903 into a build error, and then only because an unrelated lane
ran an unrelated cross-project build. Fixed by version bump, never suppressed. #197 now puts
CodeQL on the C# app so the next one is caught on purpose.

**Two allowlist ratchets fired on schedule with nobody watching.** See
`.claude/memory/an-allowlist-that-expires-itself.md`.

## Still true, still unproven

**Nothing cloud-facing has ever run against real Google Cloud**, and that is now a much larger
surface: `CloudJobRunner`, `OperationPoller`, `CloudErrorClassifier`, `VmSpec`'s label
validation, `FakeGcp`'s whole failure vocabulary, plus everything that was already there.
`docs/ToTest.md` has a row for each, and the sharpest false pass is stated plainly: **`FakeGcp`
and the classifier were written by the same agent in the same session against the same document**,
so a failure shape that document gets wrong is wrong in both and no test between them can see it.

**`check_third_party_notices.py` runs in no CI job** (#416). `ci-docs.yml`'s guard loop cannot
run it, because that job has neither `dotnet` nor a `worker/.venv`, and it is excluded with a
visible `::notice` rather than silently. Until #416, a GPL dependency added tomorrow is caught
only by a human running it.

**The cost ticker does not exist.** #66 shipped the progress page without it, deliberately and
stated: `CostEstimator` is not in Core and nothing produces per-window progress yet.

## One external contribution

**PR #362** (Voyagerroc-Lab) was reviewed and closed by the owner. Their NaN diagnosis was
correct and they were twenty minutes ahead of `8ce6653`, but `main` had a strict superset and
merging would have landed an unreachable branch. **#401** is the follow-up (`inf` has the
identical flaw), labelled `good-first-issue` and offered to them.
