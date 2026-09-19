# Tests: what runs where

## The matrix

| Suite | Where | Command | Status |
| --- | --- | --- | --- |
| `worker/tests` (not gpu) | Laptop, `ci-worker.yml` (ubuntu-latest) | `worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu" -q` | Live today |
| `worker/tests` (gpu) | A labelled GCP VM only, via `scripts/cloud_gpu_test.ps1` | `pytest -m gpu` (run on the VM, not the laptop) | Pending — script does not exist yet; blocked on `OWNER_TODO.md` item 2 (#24) for a real project/quota |
| `app/tests/*.Tests` (unit) | Laptop, `ci-app.yml` (windows-latest) | `dotnet test app/DnaEntropyGraph.sln --filter "FullyQualifiedName!~UiTests"` | Pending #61 (`app/` does not exist yet) |
| `app/tests/DnaEntropyGraph.Guards.Tests` | Laptop, `ci-app.yml` | `dotnet test app/tests/DnaEntropyGraph.Guards.Tests` | Pending #61; fast (seconds) once it exists, per Appendix C |
| `app/tests/*.UiTests` | Not CI — needs an interactive session | Manual, or a `docs/ToTest.md` row (`Needs: app-dev`) | Pending #61 |
| Container smoke (`dna-entropy-worker selftest`) | `ci-worker.yml`'s `contract` job | `docker build -f worker/Dockerfile.cpu ... && docker run ... selftest` | Pending — `worker/Dockerfile.cpu` does not exist yet (issue #36) |
| `shellcheck worker/vm/startup.sh` | `ci-worker.yml`'s `contract` job | `shellcheck worker/vm/startup.sh` | Pending — `worker/vm/startup.sh` does not exist yet (issue #45) |
| Manifest schema drift | `ci-worker.yml`'s `contract` job | `python scripts/gen_manifest_schema.py --check` | Pending — script does not exist yet (issue #39) |
| `docs/` guards (repo hygiene, link check, em-dash scan) | `ci-docs.yml` | See `dev_commands.md`'s `scripts/check_*.py` section | Partially live: the hand-written guards in `ci-docs.yml` itself (no committed private-donor copy, no user-home path, `AGENTS.md` line count, no em dash in user-facing copy, markdown links) run today; the `scripts/check_*.py` set they will eventually delegate to does not exist yet |
| `cloud-canary.yml` (nightly CPU smoke against the real `CloudJobRunner`) | Owner's GCP project, ~$0.02/run | n/a | Pending — not yet built; needs `app/` and a real project first |

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

`app/tests/DnaEntropyGraph.Guards.Tests` (pending #61) is where Hard Rules
1, 3, 6, 7, 8, 9, 10, 12, 13, 20 and 21's mechanical checks will live —
see `docs/hard_rules.md`'s "which rules a machine checks" table for the
current status of each. No count is stated here on purpose (`docs/README.
md`'s house-style rule: a number in prose goes stale — run the guard project
itself to get the current count, once it exists).

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
