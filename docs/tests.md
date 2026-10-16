# Tests: what runs where

Last updated 2026-10-15, checked against `main`: every workflow, script and test project named below
exists, except the one row marked Pending.

## The matrix

**Before `gh pr create`, run the one command that runs every row below that a laptop
can run: `worker\.venv\Scripts\python.exe scripts\premerge.py` (`--fast` per lane).** See
[`dev_commands.md`](dev_commands.md#one-command-before-a-pr-scriptspremergepy). The rows are
what it runs, and where CI runs the same thing.

| Suite | Where | Command | Status |
| --- | --- | --- | --- |
| `worker/tests` (not gpu) | Laptop (`premerge.py`, full mode), `ci-worker.yml` | `worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu" -q` | Live |
| `worker/tests` (gpu) | A labelled GCP VM only, via `scripts/cloud_gpu_test.ps1` | `pytest -m gpu` (on the VM, never the laptop) | Dry-run and `-SelfTest` today; `-Apply` creates, deletes and leak-scans one VM but does not run `pytest -m gpu` yet (#83, see `dev_commands.md`) |
| `app/tests/*.Tests` (unit, five libraries) | Laptop (`premerge.py`, full mode; one gate per discovered project), `ci-app.yml` (per assembly, with `--hangdump`, TRX uploaded as the `test-results` artifact, and the step fails if fewer than five `.trx` files exist; #438). The App is built with Windows App SDK bootstrap option `None` in every configuration (no dialog, a missing runtime exits with its HRESULT) and the job installs then asserts Windows App Runtime 1.8, because the bootstrap cannot be disabled for these tests (`BootstrapNeverShowsUiGuardTests`) | `cd app; dotnet test tests/<Project>/<Project>.csproj` (cwd must be `app/`) | Live |
| `app/tests/DnaEntropyGraph.Guards.Tests` | Laptop (`premerge.py --fast`), `ci-app.yml` | `cd app; dotnet test tests/DnaEntropyGraph.Guards.Tests/DnaEntropyGraph.Guards.Tests.csproj` | Live; seconds |
| `app/tests/*.UiTests` | Not run by `premerge.py` or CI (WinUI-hosted; every test is a skip today) | Manual, or a `docs/ToTest.md` row (`Needs: app-dev`) | Exempt, named in `scripts/premerge.py` |
| `scripts/tests` (the guards' own tests) | Laptop (`premerge.py`, full mode), `ci-docs.yml`'s `scripts-tests` job | `uv run --with pytest --with pyyaml python -m pytest scripts/tests -q` | Live |
| `scripts/check_*.py` (every repo guard) | Laptop (`premerge.py`, discovered by glob), `ci-docs.yml`'s `checks` job (same glob) | `worker\.venv\Scripts\python.exe scripts\check_<name>.py` | Live; `check_third_party_notices.py` runs in `premerge.py` full mode only (needs dotnet) |
| `THIRD-PARTY-NOTICES.md` freshness | `ci-notices.yml` (windows-latest: dotnet SDK + `uv sync --frozen` worker venv), `premerge.py` full mode | `worker\.venv\Scripts\python.exe scripts\check_third_party_notices.py` | Written 2026-10-02 (#416), NOT yet run on a real runner: see `docs/ToTest.md` |
| Lint (`ruff check` worker and scripts) | Laptop (`premerge.py`), `ci-worker.yml`, `ci-docs.yml` | `uvx ruff check worker`, `uvx ruff check scripts` | Live; no format gate for `scripts/` yet (#430) |
| Manifest schema drift | Laptop (`premerge.py`), `ci-worker.yml`'s `contract` job | `python scripts/gen_manifest_schema.py --check` | Live |
| Container smoke (`dna-entropy-worker selftest`) | `ci-worker.yml`'s `contract` job only | `docker build -f worker/Dockerfile.cpu ... && docker run ... selftest` | Live in CI; not in `premerge.py` (needs Docker) |
| `shellcheck worker/vm/startup.sh` | `ci-worker.yml`'s `contract` job only | `shellcheck worker/vm/startup.sh` | Live in CI; not in `premerge.py` (no shellcheck on the laptop) |
| Markdown links | `ci-docs.yml` `links` job; `premerge.py` when `lychee` is installed | `lychee --offline ...` | Live; the gate is SKIPPED, and says so, without lychee |
| `cloud-canary.yml` (nightly CPU smoke against the real `CloudJobRunner`) | Owner's GCP project, ~$0.02/run | n/a | Pending, not yet built |

## CI checks on a pull request (#31)

An **empty** pull request (touching nothing the tests read: not `app/`, `worker/`, `tests/`, `docs/contract/`, say a one-line README edit)
must show these checks, all passing or Skipped:

| Workflow | Check names (these are the names branch protection lists) | On an empty PR |
| --- | --- | --- |
| `ci-docs.yml` | `repo guards`, `docs consistency checks`, `scripts tests`, `markdown links` | Run and pass (no path filter) |
| `ci-app.yml` | `changes (app)`, `build and test` | `changes (app)` passes; `build and test` is **Skipped** |
| `ci-worker.yml` | `changes (worker)`, `pytest (not gpu)`, `sdist and wheel`, `manifest schema and startup script` | `changes (worker)` passes; the other three are **Skipped** |

Why the `changes` job instead of a `paths:` filter on the trigger: a workflow that a path filter
stops from starting produces *no* check, and a required check that never reports sits at "Expected,
waiting for status" and blocks the merge button forever. A job skipped by its own `if:` reports
Skipped, which branch protection treats as passing. So `ci-app.yml` and `ci-worker.yml` always start,
and a tiny `changes` job runs `scripts/ci_changes_gate.py`, which gates the real jobs. The gate is **fail-closed**: it skips
only when `git diff --name-only --no-renames <base> HEAD` succeeded AND every changed path is on a short known-irrelevant
list (`docs/**` except `docs/contract/` and `docs/copy_catalog.md`, root `*.md` except `THIRD-PARTY-NOTICES.md`, `.claude/**` and
the other agent-tool folders, `LICENSE`, and other workflows' own files). Everything else runs the jobs, including
`.editorconfig`, `global.json`, `scripts/**` and any unknown path; a git error, a missing base or a non-PR event also runs them.
So a docs-only PR skips the Windows build, while a PR touching `worker/` runs ci-app too (app tests read worker files) and
ci-worker runs on app-only PRs (cheap, and not provably irrelevant).
**Fail-closed at job level too.** A job whose `needs` did not succeed is Skipped unless its `if:` has a status function, and a Skipped required check reads as green. So every job gated on `changes` uses `!cancelled() && (needs.changes.result != 'success' || needs.changes.outputs.run == 'true')`: a failed or broken gate RUNS the real jobs instead of skipping them, and a cancelled run stays cancelled. The test parses the workflow files and evaluates that expression for gate failed, run true and run false. Path matching in the script is case-insensitive.
`scripts/tests/test_ci_changes_gate.py` fails when a path an app or worker test reads, or a workflow step reads (including the
implicit `.editorconfig` and `app/global.json`), would be classified irrelevant; matching is exact per path, not prefix-of-a-directory.

Requiring them on `main` is an owner action in the repository settings (branch protection, MEASURED
2026-10-03: `main` is not protected). Require the job names above, **not** `changes (...)`.
`ci-notices.yml` and `codeql.yml` still use workflow-level path filters, so they must NOT be made
required until they get the same treatment.

## The `gpu` marker

Defined in `worker/pyproject.toml`:

```toml
[tool.pytest.ini_options]
markers = [
    "gpu: requires a CUDA GPU and the Evo stack (skipped without one)",
]
```

A `gpu`-marked test is meant to skip itself gracefully when no CUDA stack is
present, but the standing convention is still to exclude it explicitly with
`-m "not gpu"` on the laptop rather than relying on self-skip — an explicit
exclusion is a statement of intent, a self-skip is a fallback for when that
statement was forgotten.

## Property-based tests (Hypothesis)

`worker/tests/test_property_*.py` (issues #367/#368, parents #160/#162) generate inputs
rather than sample them, for properties a handful of hand-picked examples cannot cover:

- `test_property_windowing_direction.py`: window coverage and the closed-form pass-count
  formula over the whole valid `(L, K, ceiling)` space; the Hard Rule 3 `(L, 4)` contract
  (shape, dtype, row-sum, entropy bound) for generated sequences; entropy invariance under
  the reverse-complement column permutation; the seam recorded in provenance matching
  where the forward/reverse combiner actually switched, including after an OOM halving in
  either direction (#456); reverse complement as an involution over the full IUPAC
  alphabet (#78).
- `test_property_validation.py` (#160): `validate_sequence` over the full IUPAC alphabet,
  noise and mixed case, under each of the three ambiguity policies, and over arbitrary text.
- `test_property_fuzz_readers.py`: the GenBank/FASTA readers, fuzzed over arbitrary bytes
  and over mutated copies of the real `sample.fasta`/`sample.gb` fixtures (byte flips,
  deletions, insertions, truncations, encoding swaps). Property: every input parses to a
  genuine record set or raises one of the three registered clean exception types, never
  anything else.

Every property has an explicit `@settings(max_examples=..., deadline=None)` budget (never
the Hypothesis default) -- larger for pure Python/NumPy properties, smaller for properties
that drive `MockPredictor.predict` or a real Biopython parse, since a large budget is
itself a RAM/time cost when several agents run concurrently. `hypothesis` is a `dev`-only
extra in `worker/pyproject.toml`; it is MPL-2.0, not MIT (see
the test-367 entry in `docs/sprint_log.md` and the `DECISION` issue this
recorded under Hard Rule 21).

## Golden files (issue #161)

`worker/tests/test_golden.py` runs the pipeline with the MockPredictor (seed 0, default
settings) on `sample.gb`, `multi.gb` and `prokaryotic_demo.fasta` and compares EVERY output file
byte for byte with `worker/tests/golden/files/<case>/`. Changing a writer's number formatting, a
coordinate convention or the analysis fails it. The only normalization is removing
`generated_at`, `wall_time_seconds` and `worker_version` from `provenance.json`, so a release
bump is not a failure. The FASTA case turns the bonus `.gb` off because that file's genes come
from Prodigal only when the optional `[genes]` extra is installed (machine-dependent).

To change the golden files on purpose: `worker\.venv\Scripts\python.exe
worker\tests\golden\regenerate.py --justification "why"` (refuses without one), read the diff
(a formatting change should touch every numeric file, a coordinate change one writer's file), and
commit the files with the code. The justification is appended to
`worker/tests/golden/JUSTIFICATIONS.txt`.

## Reader parity fixture (issue #393)

`tests/contract-fixtures/readers_parity.json` pins what `read_fasta` and `read_genbank` produce
(records, notices, exact error text) for multi-record, duplicate-id, empty-after-header, all-empty,
no-header, multi-record GenBank, skipped and truncated/malformed inputs. `worker/tests/
test_readers_parity.py` replays it against the Python readers; the C# `FastaLiteTests` and
`GenBankLiteTests` replay the same file against `FastaLite`/`GenBankLite`. Changing a notice's
wording in `readers/fasta.py` fails the Python test; changing it in `FastaLite.cs` fails the C#
test; the fixture is never edited to make a case pass. `GenBankLite` is a partial port, so a case
may carry a `csharp_divergence` block (the port's own expected text, a reason, a tracking issue);
those blocks are the list of known wording differences.

## Fixtures

`worker/tests/conftest.py` carries shared fixtures (currently: `sample_seq`,
a short valid A/C/G/T sequence). `worker/tests/data/` carries file fixtures:
`sample.fasta`, `sample.gb`, `multi.gb` (multi-record), `prokaryotic_demo.
fasta`. `worker/tests/contract-fixtures/` carries JSON vectors shared with
the (future) C# side: `cloud_error_classification.json`, `cloud_quota_
parsing.json` — see `tests/test_contract_fixtures.py` for how they are
consumed today, and `docs/job_contract.md` for the schema they encode once
that doc lands.

**`tests-first`'s standing neighbour-test set** (the shapes a new worker
test should sweep, not just the happy path): an empty contig / zero-length
sequence, a GenBank record with ambiguity codes, a multi-record file
(`multi.gb` above covers this), `L < 2K` (shorter than one window), `L > W`
(many windows), and — once the cloud classifier exists in Python or C# — one
case per `CloudError` class.

**Finding the repo from a C# test (#495).** A test that reads a source-controlled file (a fixture, `Resources.resw`,
`worker/vm/startup.sh`) must find the repo root by walking up from `AppContext.BaseDirectory` first and then from its own
source file (`[CallerFilePath]`), and must throw loudly when neither finds it. A lane builds with a private
`--artifacts-path` (`$env:TEMP\deg-art\<lane>`), where the binary has no repo ancestor, so a binary-only walk throws there.
The helpers: `Guards.Tests/RepoPaths`, `Cloud.Tests/FixturePaths`, `Core.Tests/Inputs/ContractFixtures.RepoRoot`,
`Presentation.Tests/TestPaths`; each project has one test asserting its root contains `app/DnaEntropyGraph.sln`.
MEASURED 2026-10-15: Guards (116), Cloud (70, filtered), Core (44, filtered) and Presentation (18, filtered) are green
under `dotnet test <project> --artifacts-path "$env:TEMP\deg-art\x"`. A new test project that needs the repo copies one of those helpers.

## Guards

`app/tests/DnaEntropyGraph.Guards.Tests` is where the C# half of Hard Rules
1, 3, 6, 7, 8, 9, 10, 12, 13, 20 and 21's mechanical checks lives; the Python half
is `scripts/check_*.py` (all run by `scripts/premerge.py`) —
see `docs/hard_rules.md`'s "which rules a machine checks" table for the
current status of each. No count is stated here on purpose (`docs/README.
md`'s house-style rule: a number in prose goes stale — run the guard project
itself to get the current count).

## Flaky-test policy

There is no flaky-test runner or retry policy, and none is wanted yet. Flakes have
happened (the two timing cases in the next section, and open issue #673: the SettingsStore
concurrency tests exceed their 300 ms mutex budget on CI under load); each was fixed at its
cause, never retried. The standing default,
carried forward as a working principle rather than a rule with its own
enforcement yet: **never diagnose a flake by hand from a single failure.**
Re-run the specific failing test in isolation before concluding it is real
or spurious, and if a test fails only under parallel execution, that is
itself a finding worth an issue (a shared-state bug), not something to
silence with a retry annotation. Revisit this section with a real policy
if flakes become frequent enough to need one — see the
`run_tests.py` REFERENCE-verdict row in
`docs/migration/2026-09-19-clair-conventions-inventory.md` for the shape a
future parallel-then-isolate-failures runner could take if this repo's
suite grows to need it.

## Runner tests run on test time

MEASURED 2026-10-03 (#525): `CloudJobRunnerOutageTests` failed about 9 runs in 100 when the machine was loaded (64 busy loops), never when idle. Cause: `GatewayCalls.CallAsync` cut each gateway call at `CallTimeout` (50 ms in those tests) on the wall clock, so a healthy call stretched past 50 ms by a starved thread became `TIMEOUT` / "request timed out"; `ResultWaiter` and `VmTerminator` also measured their waits with `Stopwatch`. The fix is not a longer timeout or a retry. Every deadline, elapsed check and poll sleep of a run now reads `CloudRunSettings.TimeProvider` (the runner's `TimeProvider` property, `TimeProvider.System` in production), and the sleep between polls is `PollDelay` when set.

For a runner test that scripts an outage or a timeout: use `RunnerClocks` (test project, from #525's `Clocks`): a `VirtualTimeProvider` for the runner that moves only when the runner sleeps between polls, one for the retry pipeline's `CloudRetryOptions.TimeProvider` that fires backoff at once. Time never passes while a healthy call is in flight, so load cannot cut a call. A call the test scripts to park (`WithHungCalls`, `WithCreateDelay`, `WithBlockedCreate`) is cut by `RunnerClocks.ParkPump`: `FakeGcp.OnCallParked` moves the clock once per parked call, by that call's own limit, after its caller has armed its deadline (the clock's created-timer count rose). It is not "advance while any call is parked", which would also cut a healthy slow call that runs after an abandoned parked one. A hung create gets one more move for the settle window. The Google gateway harness runs on a clock that stands still (`GoogleGatewayHarness.Clock`) and `ScriptedHttpHandler.OnHang` moves it by the deadline, so the delay-sum assertions are exact. Hazard: a deadline on a virtual clock that nothing moves never fires, so a test about a hung call keeps a real-time watchdog (`WithinAsync`, `WaitAsync(20 s)`) that only turns a hang into a failure.
Signals over polls (#567, #647): a test never waits for a state on a real clock. `FakeGcp.WhenHungAsync` / `WhenVmDeletedAsync` (reset when the VM is made again) / `WhenDelayedCreateLandedAsync` with `CreateLandingGate` (a late landing held until the caller has given up), `FakeGoogleOAuth.WhenWaitingOnBrowserAsync`, `InMemoryRunRepository.WhenRecordedAsync`, `RecordingLog.WhenLoggedAsync`, `RunSignals.WhenActiveAsync` (on `ActiveRuns.Started`), `VirtualTimeProvider.WhenTimerCreatedAsync` (also by a timer's due time, which `RunnerClocks.ParkPump` waits on and fails loudly without) and `ReconcileOnReconnect.RunGroupsChanged` are the signals; the only real-time bound left on a wait is a generous safety net that turns a lost signal into a failure. Remaining real time, each justified in a comment: negative-evidence windows (`CloudJobRunnerResumeAndSettleTests`, `VmReuseReviewTests`: too short can only hide a regression, never fail correct code), the real-file-lock tests in `AccountRegistryTests`, `TcpIgvBatchClientTests` and the `GenBankLite` stopwatch bound (#841).
Same class in SettingsStore (#625): MEASURED 2026-10-03: the persistent-lock test asserted exactly 5 read attempts while the retry loop is also bounded by a 300 ms wall-clock budget, and under load it saw 4 in a landing premerge. SettingsStore now reads its budget from an internal Clock (a TimeProvider) and waits through an internal Pause; a test sets both to a manual clock so no real sleep sits on the path and the count follows from the arithmetic (5 pauses of 40 ms fit 300 ms; 150 ms pauses stop at 3). Tests that need a real file lock held for a real interval (A_lock_that_clears_within_the_retry_budget_is_read_normally) assert only an invariant (more than one attempt).

## Related

[`dev_commands.md`](dev_commands.md) (exact commands),
[`hard_rules.md`](hard_rules.md) (rule 15: tests first),
`.claude/skills/tests-first/SKILL.md`.
