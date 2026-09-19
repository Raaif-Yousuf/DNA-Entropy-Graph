# docs/ — index

`CLAUDE.md` (repo root) points; this directory explains. If you are looking
for a fact and `CLAUDE.md` names a doc for it, that doc is authoritative —
`CLAUDE.md` stays small on purpose and is not the place to re-derive detail
that already lives here.

Cold start on a narrow task? Read [`entry_points.md`](entry_points.md)
first, not this file top to bottom.

## Reading order for a new session

1. [`CLAUDE.md`](../CLAUDE.md) (repo root) — the whole Hard Rules set, the
   stack, the Critical Pitfalls. Small on purpose; read it in full.
2. [`entry_points.md`](entry_points.md) — task-shaped index and the
   disproven-diagnoses table, so you do not re-derive a cause this project
   already ruled out.
3. Whatever the task actually touches, from the topic map below.
4. [`NEXT_SESSION.md`](../NEXT_SESSION.md) (repo root) — where the last
   session stopped. Overwritten every session; trust `git log -1` and
   `gh issue list` over its prose if they disagree.

## Skills (`.claude/skills/`)

Invoked with the `Skill` tool, not read directly as documentation — but
knowing when each applies is itself a fact worth indexing here.

| Skill | Use before |
| --- | --- |
| `tests-first` | Writing any feature, fix, or behaviour change |
| `wired-to-nothing` | Reporting any feature, fix, or wiring change as done |
| `working-an-issue` | Starting or closing any GitHub issue |
| `fixing-a-bug` (+ `bug-shapes.md`) | Writing any fix code for a bug, regression, or defect |
| `orchestrating-agents` | Dispatching subagents, writing a brief, or merging an agent's branch |
| `working-on-gcp` | Creating, inspecting, or debugging any Google Cloud resource |
| `winui-dev` | Building, running, testing or debugging the WinUI 3 app in `app/` |

Full description of what each carries: [`.claude/README.md`](../.claude/README.md).

## Topic map

| Topic | Doc |
| --- | --- |
| Hard-rule rationale, carve-outs, which rules a machine checks | [`hard_rules.md`](hard_rules.md) |
| Task-shaped index, disproven diagnoses | [`entry_points.md`](entry_points.md) |
| First hour on a fresh Windows 11 box | [`onboarding.md`](onboarding.md) |
| Every dev command, PowerShell equivalents, GPU-test recipe | [`dev_commands.md`](dev_commands.md) |
| What runs where (laptop / CI / GPU VM), guards, fixtures, `gpu` marker | [`tests.md`](tests.md) |
| Annotated repo tree, generated vs. hand-written, never-commit list | [`project_structure.md`](project_structure.md) |
| Environment variables and dev-override settings | [`environment.md`](environment.md) |
| Every dependency, version, licence | [`tech_stack.md`](tech_stack.md) |
| App/worker split, job lifecycle, state machine | [`architecture.md`](architecture.md) |
| The app-worker contract: manifest, bucket layout, progress, result | [`job_contract.md`](job_contract.md) |
| The science: `(L,4)`, entropy, windowing, direction, writers | [`science_and_formats.md`](science_and_formats.md) |
| Cloud: preflight, error classes, labels, cost, termination | [`cloud_design.md`](cloud_design.md) · [`gcp_setup_manual.md`](gcp_setup_manual.md) |
| WinUI patterns, copy rules, theme, state-to-UI table | [`ui_conventions.md`](ui_conventions.md) |
| Narration, phase titles, and the error catalog (the .resw source table) | [`copy_catalog.md`](copy_catalog.md) |
| Trust boundaries: account, container, VM, bucket, laptop | [`threat_model.md`](threat_model.md) |
| Packaging, signing, update channel, version lockstep | [`packaging_design.md`](packaging_design.md) |
| Cutting a release, step by step | [`release_runbook.md`](release_runbook.md) |
| The verification queue: closed-but-unproven-on-a-real-build | [`ToTest.md`](ToTest.md) |
| What shipped, newest first | [`sprint_log.md`](sprint_log.md) |
| How work lands: branches, pull requests, merging, local guards | [`branching_and_prs.md`](branching_and_prs.md) |
| The branch-to-sprint-log write path | [`changelog.d/README.md`](changelog.d/README.md) |
| Dated design specs | [`superpowers/specs/`](superpowers/specs/) |
| Dated research notes | [`research/`](research/) — one file per note, `YYYY-MM-DD-<slug>.md`, per the template skeleton below |
| For the lab user (separate voice, no jargon) | [`user_guide/`](user_guide/) — start at `README.md` inside it |

## House style: who owns which fact

`CLAUDE.md` points, `docs/` explains. Five status surfaces exist and each
owns a different kind of fact. Do not merge two of them.

| Surface | Owns | Lifecycle |
| --- | --- | --- |
| GitHub Issues | Every bug, idea and roadmap item: what is wrong or wanted, with labels and milestones | Open -> closed. Nothing duplicates it in markdown |
| ToTest.md | Closed issues whose behaviour is unproven on a real installer build or a real GPU VM | Row added on close, deleted on verification, issue reopened on failure. Must drain |
| sprint_log.md | What shipped, one entry per merge, newest first | Append-only, never edited after the fact |
| changelog.d/ | The write path into sprint_log from a branch, one fragment per branch | Written on the branch, folded and deleted at merge |
| NEXT_SESSION.md | Where the last session stopped and what the next one does first | Overwritten every session; no history |

Issues is *what*, ToTest is *is it really fixed*, sprint_log is *what already
shipped*, NEXT_SESSION is *where we left off*. The user guide is a sixth
surface with a different reader: it owns *how a biologist does a task* and
nothing about how the code works.

Two more rules of this house:
- A number written in prose (test counts, guard counts, instance counts)
  goes stale. Prefer "run X to get the current number" over the number.
- A claim about a cause carries `MEASURED <date>:` or `THEORY (unverified):`
  (Hard Rule 18).

## Editing rules for this directory

- A behaviour change lands its `docs/` update in the **same commit**, not a
  follow-up (Hard Rule 16). A branch in flight writes
  [`docs/changelog.d/<branch-name>.md`](changelog.d/README.md) instead of
  editing `sprint_log.md` directly.
- Template skeletons for a new spec, decision record, runbook, or research
  note live at the bottom of this section so a new doc starts from a known
  shape rather than a blank page (also in Appendix C §3 of the design spec,
  which this section is ported from).
- Every doc that states a version number, a quota, a price, or a fact this
  session did not itself check should say so — `MEASURED <date>:` if
  checked, `THEORY (unverified):` if carried forward from a design doc or
  general knowledge and not yet confirmed against this project. Do not
  invent a number to fill a table cell.

### Template heading skeletons

**Spec** (`docs/superpowers/specs/YYYY-MM-DD-<slug>.md`):
```
# <Title>
**Date:** | **Status:** Draft / Approved (owner, date) / Implemented (#issue) / Superseded by
**Parent epic:** #N | **Supersedes:**
## 1. Why this exists (the observed problem, with measurements)
## 2. The model (entities, glossary of load-bearing words: means / does not mean)
## 3. Behaviour (state machine or sequence; what the user sees per state)
## 4. Contracts touched (job_contract, interfaces, schema; versioning)
## 5. What is explicitly out of scope
## 6. Work packages (each maps to one sub-issue; each names its test and its wired-to-nothing observable)
## 7. Open questions for the owner
```

**Decision record** (`docs/<topic>_decision.md` or a section in a spec):
```
# <Topic>: decision record (issue #N)
> Decision-support document, not a build task. Rule 18 applies: every claim is MEASURED or THEORY.
**Status:** open, needs an owner call / decided <date> by owner
## 1. What is actually true today (verified, with evidence per row)
## 2. What already exists and whether it is wired to anything
## 3. Options (table: option, cost, what it fixes, what it breaks)
## 4. Recommendation (one option, and the smallest first step)
## 5. What shipped without waiting for the call, and what is still open
## 6. Questions only the owner can answer
```

**Runbook** (`docs/<name>_runbook.md`):
```
# <Name> runbook: <version or target>
> Written <date>, before the thing it prepares for was done. Left as written per Rule 18.
## 0. Preconditions (what must be true; the command that proves each)
## 1. What changed since last time
## 2. Version line (what it must be and why)
## 3. Pre-flight gates (commands and their real outputs)
## 4. The exact command sequence
## 5. Verification on a clean machine (the false pass named, the real proof named)
## 6. Rollback
## 7. DONE notes (appended after, dated)
```

**Research note** (`docs/research/YYYY-MM-DD-<slug>.md`):
```
# <Question>, researched <date>
> Snapshot; goes stale by design. Issues are the tracker (Rule 17).
## 1. The question and why now
## 2. What was checked (sources, versions, licences, with dates)
## 3. Findings (table)
## 4. What this changes for us (issues filed: #...)
## 5. What was deliberately not pursued, and why
```
