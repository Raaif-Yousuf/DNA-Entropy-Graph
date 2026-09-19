# Handoff: after the 2026-09-19 overnight agent wave

> Overwritten every session. Re-check `git log -1 main` and `gh issue list` before trusting anything here.
> Counts are deliberately not written down; run
> `gh issue list --repo Raaif-Yousuf/DNA-Entropy-Graph --limit 400 --json number,labels,milestone` for current numbers.

## What this session was

An unattended overnight run: one orchestrator and three Sonnet subagents at a time, each on a disjoint set
of file paths, with the orchestrator holding every `git` command so concurrent agents could not capture
each other's half-finished work. Everything is committed and pushed to `main`.

**Every `P0` is closed.** The migration cleanup that blocked all other work is done, and v0.1 is unblocked.

## What landed

- **`.github/`**: the 22-label source of truth, issue forms that carry the Done-when and Observable
  contract, a decision form that demands a recommendation and a reversal cost, the PR template,
  dependabot, and `ci-worker` / `ci-docs` / `ci-app` / `codeql`. Every CI step that guards a file which
  does not exist yet prints a notice, passes, and starts enforcing the moment that file lands.
- **`CLAUDE.md`**, `AGENTS.md`, seven skills, the `cold-diff-reviewer` agent, 27 memory seed files, and
  `.claude/settings.json`.
- **`scripts/`**: four repo-safety hooks behind a fail-closed dispatcher, `issue_precheck`,
  `compile_sprint_log`, `sync_memory`, `triage_diagnostics`, `sync_labels.ps1`, and six `check_*.py`
  guards that `ci-docs` runs automatically. **207 tests.**
- **`worker/`**: the prototype's `cloud` package gutted, with its quota regexes and error taxonomy
  preserved as shared vectors at `tests/contract-fixtures/`; then windowing, bidirectional direction,
  model gating, multi-record FASTA, the TSV writer, and the whole `dna_entropy.worker` subpackage
  (manifest, status, blobstore, cancel, lifecycle, runner, weights, entry point).
  **309 tests, up from 147.**
- **`docs/`**: the developer set, the design and science set, and a ten-page user guide, then audited
  against itself for rule numbering, stage names, error codes, coordinate systems and shared numbers.

## Start here next session

1. **Answer the two new `DECISION` issues.** Both are one decision each and both block real work:
   - **#301** `pyrodigal` is GPLv3 and is already imported by the worker. Recommendation and a
     compliance checklist are in the issue; the recommended answer is a scoped carve-out, not dropping
     gene calling.
   - **#302** memory sync published personal session memory into this public repo. The fix is
     implemented and the hooks are off until you say yes.
2. **#266, five minutes with a browser.** Every GPU price in
   `docs/research/2026-09-19-gpu-pricing-and-instances.md` is `THEORY (unverified)` because Google Cloud
   and AWS now render pricing only in JavaScript and the old public price list is a 404. The note names
   the exact pages to read. **#303** is the sharper one: the spec's A100 Spot range disagrees with every
   source by two to four times, and the cost estimator is built on it.
3. **Then v0.1**, in the order the spec's section 8 gives: the two week-1 spikes
   (`packaging: spike: WinUI 3 unpackaged + Velopack`, `packaging: spike: NGC PyTorch base`) can run in
   parallel with everything else, then `worker/vm/startup.sh` (#45) and the container images (#36), then
   the `app/` skeleton (#61) and the cloud gateways.

## What is true but not proven

`lifecycle.py` and `GcsBlobstore` are implemented and unit-tested against fakes, and have **never run
against real Google Cloud**, because nothing this session was allowed to spend money. The same goes for
every cloud path in the repo. Those are ToTest rows, not claims.

## How to run an overnight wave like this one

Three agents, disjoint paths, one shared brief, the orchestrator owning git and the full test suite, and
only one agent allowed to run pytest at a time (several concurrent runs exhaust this machine's RAM). Pass
each agent its own pytest `--basetemp`; the default Windows temp directory races between concurrent runs
and that cost real time to diagnose. #300 tracks turning this into a script.

## Traps this session paid for, on top of the ones already in `CLAUDE.md`

- A settings-file hook that mirrors memory into the repo will publish whatever it finds, including notes
  about a person, and will overwrite a curated file that happens to share a filename with its source.
- A guard whose shell wrapper ends in `|| true` stops guarding silently, and everyone keeps believing it
  works because the ban is written down in three places.
- Asserting on Typer's `--help` output tests Rich's line wrapping, not your CLI. It passes on a wide
  terminal and fails on CI's 80 columns.
- Dependabot fails the whole run when an ecosystem's manifest does not exist yet.
- Both cloud vendors now serve pricing only to a browser. Plan for an authenticated API call (#214), not
  for scraping.
- The donor scripts arrived carrying about thirty of the donor project's issue numbers in their comments.
  In this repo those numbers point at nothing, or at an unrelated issue.
