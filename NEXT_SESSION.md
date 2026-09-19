# Handoff: after the 2026-09-19 overnight agent wave

> Overwritten every session. Re-check `git log -1 main` and `gh issue list` before trusting anything here.
> Counts are deliberately not written down; run
> `gh issue list --repo Raaif-Yousuf/DNA-Entropy-Graph --limit 400 --json number,labels,milestone` for current numbers.

## What this session was

One orchestrator and three Sonnet subagents at a time, each on a disjoint set of file paths, with the
orchestrator holding every `git` command. Everything is committed and pushed to `main`, and `ci-worker`,
`ci-docs`, `ci-app` and `codeql` are green.

**Every `P0` is closed.** The migration is done and `app/` is now the entire remaining product.

## What exists and is green

- **`worker/`**: the whole `dna_entropy` package plus the `worker` subpackage (manifest, status, blobstore
  with a real retry policy, cancel, lifecycle, runner, weights, CLI), windowing and bidirectional
  direction, model gating, the TSV writer, batch limits, an ambiguity policy, partial results on failure,
  and a log-redaction guard. Ruff is live over the whole package.
- **Contract**: `docs/contract/*.schema.json` and `error-codes.json` are generated from the worker's own
  dataclasses, with a drift check CI runs. Golden vectors for the C# port live in
  `tests/contract-fixtures/`, produced by running the prototype rather than by reading its tests.
- **`vm/startup.sh`** and a **CPU container** that really builds and really runs a job.
- **`scripts/`**: about twenty, including seven self-testing guards, four safety hooks behind a fail-closed
  dispatcher, `agent_wave.ps1`, `new_issue.ps1`, `sync_labels.ps1` and `cloud_gpu_test.ps1`.
- **`docs/`**: the developer set, the design and science set, a ten-page user guide, and
  `copy_catalog.md`, which holds every phase, narration line and error message the app will show.

## Start here next session

1. **`OWNER_TODO.md`.** It is one prioritised list, roughly 90 minutes, with a one-line recommendation for
   each of the fifteen open `DECISION` issues so you can agree or overrule without opening them. The two
   that matter most are **#301** (pyrodigal is GPLv3 and already imported) and **#302** (memory sync
   published personal memory into the public repo; the fix is implemented and the hooks are off until you
   say yes).
2. **#266, five minutes with a browser.** Every GPU price in
   `docs/research/2026-09-19-gpu-pricing-and-instances.md` is `THEORY (unverified)`, because both vendors
   now render pricing only in JavaScript and the old public price list is a 404. The note names the exact
   pages to read. **#303** is sharper: the spec's A100 Spot range disagrees with every source by two to
   four times, and the cost estimator is built on it.
3. **Then `app/`**, starting with the two week-1 spikes (#35 Velopack and #37 the NGC base) and the
   solution skeleton (#61).

## What is true but not proven

**Nothing cloud-facing has ever run against real Google Cloud.** `lifecycle.py`, `GcsBlobstore`'s retry
path, `startup.sh`, `cloud_gpu_test.ps1 -Apply` and `Dockerfile.cuda` are implemented, tested against
fakes, and unproven. They are rows in `docs/ToTest.md`, each naming its false pass, not claims. See the
cost estimate below before booking a session to drain them.

## Roughly what draining the cloud ToTest queue costs

All figures trace to `docs/research/2026-09-19-gpu-pricing-and-instances.md` and are `THEORY` pending
#266. Rates used: L4 `g2-standard-8` ~$0.85/h on demand and ~$0.18/h Spot; A100 40 `a2-highgpu-1g`
~$3.67/h; `pd-balanced` ~$0.10/GB/month, so a 150 GB boot disk is ~$0.02/h and ~$15/month if forgotten.

| Session | What it covers | Rough cost |
|---|---|---|
| Smoke only | The CPU walking skeleton on `e2-small`, no GPU | under $0.10 |
| Minimum acceptance | Drain the four current ToTest rows: lifecycle stop/delete, `GcsBlobstore` against a real bucket, `startup.sh`, one real 7B run | $3 to $6 |
| Extensive | The above plus the zone ladder, an A100 fallback, forced stockout and quota failures, retention and leak sweeps, two accounts, repeated runs | $45 to $75 |
| Extensive on Spot | Same, L4 and A100 on Spot with preemption deliberately exercised | $15 to $25 |

The first run in any project adds roughly eight minutes while the 7B weights download, and that download
is once per project, not once per run.

**The number that actually matters is none of the above.** A forgotten running L4 is about $20/day and an
A100 about $88/day, so one VM left up over a weekend costs more than the entire test campaign. The guards
against that already exist and have never run for real: `maxRunDuration` with
`instanceTerminationAction=DELETE` on every VM, the worker stopping or deleting itself through the Compute
API, `cloud_gpu_test.ps1`'s end-of-run leak assertion, and the app's nothing-left-running sweep (#113).
Drain the lifecycle ToTest row first, because it is the one that proves the others can be trusted.

## How to run an overnight wave like this one

Three agents, disjoint paths, one shared brief, the orchestrator owning git and the full test suite, and
only one agent running pytest at a time. Give each agent its own `--basetemp`. `scripts/agent_wave.ps1`
does all of this and refuses a wave whose path assignments overlap.

## Traps this session paid for, beyond the ones in `CLAUDE.md`

- A guard that passes because the thing it checks does not exist, or because an optional dependency is
  missing, is not passing. Three separate shapes of this were found and fixed tonight, the last one a
  check that printed OK for validation it had just announced it was skipping.
- A subagent's report is a claim, not evidence. One claimed a retry policy that was not in the file, and
  it reached a pushed commit message before a different agent caught it by reading the file.
- Exact float equality is not portable. A parity fixture passed on Windows and failed on Linux CI because
  `log2` may differ in the last bit.
- Asserting on a CLI's rendered `--help` tests the terminal width, not the CLI.
- In PowerShell, `$script:x++` inside a closure resolves to file scope, which made every `.ps1` self-test
  report PASS regardless of failures.
- Both cloud vendors now serve pricing only to a browser; plan for an authenticated API call (#214).
