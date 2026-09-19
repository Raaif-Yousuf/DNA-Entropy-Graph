# Handoff: after the 2026-09-18/19 planning and migration session

> Overwritten every session. Re-check `git log -1 main` and `gh issue list` before trusting anything here.
> Counts are deliberately not written down; run `gh issue list --repo Raaif-Yousuf/DNA-Entropy-Graph --limit 400 --json number,labels,milestone` for the current numbers.

## What this session was
Planning, then a verbatim migration. The owner approved the design; the repo, labels, milestones and
the full issue set were created from it in four waves; the prototype package was copied into
`worker/` unchanged; CLAIR's conventions were copied into a gitignored `legacy/clair/` for mining.
**No application code has been written or changed.**

## State on `main`
- `docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md` + appendices A (app), B (cloud/worker), C (repo conventions): the approved design.
- `FEATURES.md`: inventory of the two CLI prototypes (its test counts are stale; see the worker inventory).
- `worker/`: `dna_entropy` copied byte-for-byte from DNA-Entropy-GenBank @ `8026bf5`, plus `worker/legacy/dna-entropy-v1/`
  (the older prototype's delete-after-run orchestrator and cli). `worker/.venv` exists locally (uv, Python 3.12);
  `pytest -m "not gpu"` = 147 passed, 2 skipped. Nothing under `worker/` has been adapted yet.
- `docs/migration/`: two inventories with a KEEP / ADAPT / GUT / REFERENCE verdict per file, the exhaustive list of
  prototype health checks, permission checks, quota logic, error classes and constants the C# port must preserve,
  and the P0 issue lists.
- `legacy/clair/` (62 files from the private CLAIR repo) is **gitignored and must never be pushed** (owner decision
  2026-09-19). It exists on this PC only, for the P0 porting issues.

## Tracker shape
Epics (sub-issue parents): app, worker, cloud, viewer, local-engine, packaging, ux, docs, science, automation, aws, cost.
Milestones: v0.1 walking skeleton -> v0.2 real Evo on GPU -> v0.3 daily-use polish -> v1.0 lab release -> v1.1 multi-cloud (AWS) -> post-v1.
Labels that gate work: `P0` (migration cleanup; do first), `DECISION` (owner call), `owner` (only the owner can do it), `spike`, `needs-criteria`.

## Start here next session
1. Read the spec body once (section 2 lists every decision and why).
2. Work the `P0` issues in this order: CLAIR-derived docs and scripts (#267 to #276: CLAUDE.md, AGENTS.md stub,
   skills, hooks, settings.json, day-one docs), then the worker gutting (#277 to #287: drop cloudrun/keep-gpu, delete
   `cloud/`, reproduce its test cases as fixtures, port DESIGN.md). Every P0 names its inventory rows.
3. Keep `worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu"` green after every gut step.
4. Then the v0.1 issues, docs first, then worker, then app skeleton and cloud gateways. The two week-1 spikes
   (`packaging: spike: WinUI 3 unpackaged + Velopack ...`, `packaging: spike: NGC PyTorch base ...`) can run in parallel.

## Owner actions pending (issues labelled `owner` or `DECISION`)
OAuth client + consent screen; privacy policy + verification; Azure Trusted Signing; GPU quota on a dev project;
AWS sign-in model; the twelve design DECISION issues. None block the P0 or docs work.

## Traps this session paid for
- Evo 2 1B, 20B and 40B require FP8 on Hopper (H100). Only the 7B variants run on an L4 or A100.
- "Reverse" prediction must be reverse complement, not reversed text.
- A true per-base rolling window is one forward pass per base; overlapping windows of 2K with stride K are the cheap equivalent.
- `instanceTerminationAction` does not fire on guest `shutdown`; the VM must stop or delete itself via the Compute API.
- Two lab members may share one Google account; nothing cloud-side may be a fixed-name singleton.
- GitHub's sub-issues list endpoint pages at 30; read `sub_issues_summary.total` on the parent, not the list length.
- A transient network error killed an issue-creation run mid-way; scripts must retry non-4xx failures and be idempotent.
- The private conventions donor (CLAIR) must not be committed to this public repo; it is gitignored under `legacy/clair/`.
