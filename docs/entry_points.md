# Entry points

Read this first for a narrow task or a cold start — before `docs/README.md`'s
full topic map, before re-deriving a cause `entry_points.md` already has an
answer for.

## Task-shaped index

| I need to... | Start here |
| --- | --- |
| Add or change a Hard Rule | [`CLAUDE.md`](../CLAUDE.md) + [`hard_rules.md`](hard_rules.md) in the same commit |
| Fix a bug | `fixing-a-bug` skill, **before** writing fix code |
| Report a change as done | `wired-to-nothing` skill, **before** the report |
| Start or close a GitHub issue | `working-an-issue` skill |
| Touch anything under `worker/src/dna_entropy/predictors/` | Read the three predictor-boundary memory seeds first (`evo2-returns-a-nested-tuple`, `tokenizer-ids-are-uint8`, `evo2-tokenizer-adds-no-bos`) |
| Touch anything under `worker/src/dna_entropy/analysis/` | Read `one-forward-pass-per-window` and `reverse-means-reverse-complement` memory seeds first |
| Create, inspect, or debug a Google Cloud resource | `working-on-gcp` skill, **before** the action |
| Build, run, or debug the WinUI app | `winui-dev` skill |
| Dispatch a subagent or write a brief | `orchestrating-agents` skill |
| Set up a fresh dev box | [`onboarding.md`](onboarding.md) |
| Find the right command | [`dev_commands.md`](dev_commands.md) |
| Understand what runs where in CI/tests | [`tests.md`](tests.md) |
| Check a licence before adding a dependency | [`tech_stack.md`](tech_stack.md) + Hard Rule 21 |
| Find an env var or a `DEG_*` dev override | [`environment.md`](environment.md) |
| Understand the repo tree | [`project_structure.md`](project_structure.md) |
| Cut a release | [`release_runbook.md`](release_runbook.md) |
| Check whether an issue already shipped | `python scripts/issue_precheck.py <n>...` |

## Invariants easy to miss

These are stated as Hard Rules or Critical Pitfalls elsewhere; repeated here
because they are the ones a narrow task most often runs into sideways,
without the task itself being about them:

- A cloud resource with no label is invisible to the app's own discovery
  (Hard Rule 9-10) — if something "isn't showing up," check labels before
  assuming a bigger bug.
- `instanceTerminationAction` does not fire on a guest `shutdown` call, only
  on `maxRunDuration` expiry (Critical Pitfalls; `termination-action-does-
  not-fire-on-guest-shutdown` memory seed).
- "Reverse" means reverse complement, never a reversed string
  (`reverse-means-reverse-complement` memory seed).
- The dev laptop cannot run a GPU test at all, not just slowly
  (`dev-laptop-has-no-cuda` memory seed) — do not "just try it locally to
  see."
- `RUNNING` instance status says nothing about job health; only the
  heartbeat in `status.json` does (Critical Pitfalls).

## §3. Disproven diagnoses

A theory that was stated, then measured and found wrong. Per Hard Rule 18,
disproving a theory **replaces** its entry here rather than appending a
correction elsewhere — read this table before re-deriving a cause it may
already cover.

| Theory (disproven) | What it looked like | What was actually true | MEASURED |
| --- | --- | --- | --- |
| "Cloud resource creation is failing everywhere because of a capacity shortage (stockout)" | Every `Insert` call fails, project-wide, no matter which zone or region is tried | The project had billing disabled (or the Compute API disabled) — `billing`, `api_disabled`, `quota`, and `stockout` all produce visually identical failures from the caller's side until `CloudErrorClassifier` separates them | In the prototype's own real misconfigured GCP project, carried forward as the seed example for this table (see the `billing-off-looks-like-stockout` memory seed); re-confirm with a real `MEASURED <date>:` entry the first time this project's own app hits it |

This table starts with one seeded row, carried from the prototype's own
recorded experience rather than invented fresh — see Appendix C §3 of the
design spec, which names this exact row as the table's seed. Add a new row
the first time a theory here is actually disproven on this project's own
code, not before.
