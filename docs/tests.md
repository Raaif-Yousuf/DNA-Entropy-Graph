# Tests: what runs where

## The matrix

**Before `gh pr create`, run the one command that runs every row below that a laptop
can run: `worker\.venv\Scripts\python.exe scripts\premerge.py` (`--fast` per lane).** See
[`dev_commands.md`](dev_commands.md#one-command-before-a-pr-scriptspremergepy). The rows are
what it runs, and where CI runs the same thing.

| Suite | Where | Command | Status |
| --- | --- | --- | --- |
| `worker/tests` (not gpu) | Laptop (`premerge.py`, full mode), `ci-worker.yml` | `worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu" -q` | Live |
| `worker/tests` (gpu) | A labelled GCP VM only, via `scripts/cloud_gpu_test.ps1` | `pytest -m gpu` (on the VM, never the laptop) | Dry-run only; `-Apply` waits on `CloudCli` (see `dev_commands.md`) |
| `app/tests/*.Tests` (unit, five libraries) | Laptop (`premerge.py`, full mode; one gate per discovered project), `ci-app.yml` (per assembly, with `--hangdump`, TRX uploaded as the `test-results` artifact; #438) | `cd app; dotnet test tests/<Project>/<Project>.csproj` (cwd must be `app/`) | Live |
| `app/tests/DnaEntropyGraph.Guards.Tests` | Laptop (`premerge.py --fast`), `ci-app.yml` | `cd app; dotnet test tests/DnaEntropyGraph.Guards.Tests/DnaEntropyGraph.Guards.Tests.csproj` | Live; seconds |
| `app/tests/*.UiTests` | Not run by `premerge.py` or CI (WinUI-hosted; hangs under CI) | Manual, or a `docs/ToTest.md` row (`Needs: app-dev`) | Exempt, named in `scripts/premerge.py` |
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
`docs/changelog.d/test-367-property-windowing-direction.md` and the `DECISION` issue this
recorded under Hard Rule 21).

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

## Guards

`app/tests/DnaEntropyGraph.Guards.Tests` is where the C# half of Hard Rules
1, 3, 6, 7, 8, 9, 10, 12, 13, 20 and 21's mechanical checks lives; the Python half
is `scripts/check_*.py` (all run by `scripts/premerge.py`) —
see `docs/hard_rules.md`'s "which rules a machine checks" table for the
current status of each. No count is stated here on purpose (`docs/README.
md`'s house-style rule: a number in prose goes stale — run the guard project
itself to get the current count).

## Flaky-test policy

Not yet needed at this repo's current size (`worker/tests` alone, no `app/`
suite yet, no evidence of flakiness recorded). The standing default,
carried forward as a working principle rather than a rule with its own
enforcement yet: **never diagnose a flake by hand from a single failure.**
Re-run the specific failing test in isolation before concluding it is real
or spurious, and if a test fails only under parallel execution, that is
itself a finding worth an issue (a shared-state bug), not something to
silence with a retry annotation. Revisit this section with a real policy
once `app/`'s test surface is large enough to need one — see the
`run_tests.py` REFERENCE-verdict row in
`docs/migration/2026-09-19-clair-conventions-inventory.md` for the shape a
future parallel-then-isolate-failures runner could take if this repo's
suite grows to need it.

## Related

[`dev_commands.md`](dev_commands.md) (exact commands),
[`hard_rules.md`](hard_rules.md) (rule 15: tests first),
`.claude/skills/tests-first/SKILL.md`.
