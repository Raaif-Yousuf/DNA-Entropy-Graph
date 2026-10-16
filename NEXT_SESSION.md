# Handoff: after the 2026-10-15/16 wave

> Overwritten every session. Re-check `git log -1 origin/main` and `gh pr list` before trusting anything here.

## What this session was

One orchestrator, at most 3 Sonnet lanes at a time, each in its own worktree. Every lane diff got a
framing-free cold review, and cloud/deletion diffs got 2 to 4 rounds, before it was queued.
- **Landing backlog:** about 40 finished branches had failed to land on gates or conflicts after main moved.
  Three repair lanes fixed them, and a scratchpad queue runner (`land_wt.py --merge-when-green` per item)
  landed them one at a time.
- **New work, in issue order:** #114, #619/#83, #759, #761/#653, #116, #113, #87, #91, #92, #109, #111,
  #100/#557, #117, #141, #112. Also the #740 fixes on the app12 chain.
- **Tools that landed:** `scripts/resw_merge.py`, a key-aware `Resources.resw` merge driver. `land_wt.py`
  uses it automatically. Run `worker\.venv\Scripts\python.exe scripts\setup_git_drivers.py` once per clone,
  so a plain `git merge` uses it too.

## Start here next session

1. **Finish the landing queue, in this order.** Skip any entry that is already an ancestor of origin/main
   (`git merge-base --is-ancestor <sha> origin/main`). Land with
   `scripts/land_wt.py --branch <b> --sha <s> --title ... --body-file ... --merge-when-green`, one at a time.
   The PR body is the branch's own `docs/changelog.d/` fragment plus `Closes #n`.
   - **Before this session:** fix/694-origin-wrap-writers @ d3f9ea34, feat/129-130-192-analysis-commands @ 62227c12
   - **GPU test path:** feat/619-83-gpu-test-apply @ 5d982249
   - **Cloud stack (a linear chain):** feat/89-vm-termination-verified-rs @ 1e50eb7f, feat/90-heartbeat-stage-deadlines-rs @ 22ae7653,
     feat/94-reuse-stopped-vm-rs @ 19290227, feat/670-zone-ladder-rs @ bc1492d3, fix/678-reuse-labels-rs @ 4f42e2ef,
     fix/679-vmname-and-limit-rs @ 78fe2929, fix/691-clock-skew-rs @ e418d098, feat/105-cloud-page-gateway-rs @ e7b40b65,
     feat/99-gpu-quota-request-r2-rs @ ec5651c9, feat/675-slow-run-control-r2-rs @ 37289401,
     worker5/494-496-522-299-rs @ 1da65a24, cloud9/723-progress-stall-rs @ d35daf1e
   - **GPU test mode (contains worker5):** feat/759-gpu-test-mode @ 24ad77f7
   - **Manifest fields:** feat/704-manifest-fields-rs @ 7a3c2025
   - **app10 chain:** feat/app10-base-rs @ 399e6d83, feat/app10-688-gc-codons-rs @ a4c2596c, feat/app10-684-accession-rs @ be136437
   - **app12 chain (#740 fixed, two review rounds):** feat/app12-base-rs @ fe786cfb, feat/app12-99-gpu-ask-rs @ 7752a66e,
     feat/app12-105-cloud-page-rs @ 0f1f00c9, feat/app12-675-slow-run-rs @ 8f896ef6,
     feat/app12-717-redownload-partial-rs @ bcf3cced, feat/app12-677-settings-followups-rs @ 1e898ca3
   - **Independent branches:** fix/114-retention-setting-wired @ 2a7cac3d, feat/local-engine-119-120-173-174 @ 27ea5150,
     chore/scripts-ruff-format-r2 @ 56efdfc0
   - **Built on the app12 tip:** feat/111-import-runs @ 89f0486b, feat/113-nothing-left-running @ 252f28ba,
     feat/91-spot-provisioning @ e5d715ba, feat/92-flex-start @ 8ae970c6 (on 91), feat/87-gpu-planner @ 2506341e
   - **On 91 + 87:** feat/109-spend-ledger @ 25650ff7. It has migration 0003; 91 has 0002, so 91 must land first.
   - **On 109:** feat/100-557-run-cost @ 88dc3f66
   - **Last:** ci/761-653-codeql-src-only @ 637bec69. Its pinned `format_arity_sites.txt` will drift. Regenerate it
     with the command in `docs/hard_rules.md` Rule 13, then commit. Once it lands, CodeQL stops tracing test stubs.
2. **Held, not queued:**
   - feat/141-run-notes-tags @ 34f367b0: fix #782 first. Its High finding: a common tag makes Save diagnostics fail.
   - feat/112-per-account-runs @ ba82fa16: built and tested, never cold-reviewed. It adds migration 0004,
     so it lands after 109/100/141.
   - feat/land-queue-tool @ 18dbd629: re-run `uvx ruff format scripts` after chore/scripts-ruff-format-r2 lands, then land it.
     It gives you `scripts/land_queue.py` instead of a scratchpad runner.
3. **Then continue issues in ascending order.**
   - **Triage (2026-10-15):** #35/#37/#84/#85/#95/#96 are BLOCKED (owner, real GPU or a new account). #98 is
     DONE in its own scope; rescope it and close. #104 is an epic split into #639-#644.
   - **Next unbuilt:** #93 (keep-alive queue, L), #97 (wizard real GPU test step), #137/#138/#139 (watch
     folder, CLI, schedule), #142 (shared signed URL; check iam.signBlob first), #143 to #146, #148 to #152.
   - **Filed this session:** #759 (done), #761, #768 to #772, #780, #782, #784.

## Measured this session

- **CodeQL `cs/invalid-string-formatting` false positives stall landings.**
  - Cause: CodeQL traces a test stub's IStrings template into product `string.Format` calls.
  - Fix: #761 (queued last).
  - Until then: dismiss by hand after checking the resw template has the right placeholders (done for #760, #762, #767, #771, #779).
- **Format failures were the commonest landing failure.** dotnet format whitespace and final newlines.
  Run `dotnet format --verify-no-changes` on every lane tip before queuing.
- **Push rejection after a re-landing.** A re-landed branch whose remote already has the earlier landing's
  merge commit is rejected on push. Re-land under `-r2`.
- **Cold reviews kept finding real defects in every lane, often High.** Examples:
  - The exit dialog could close before Google received the stop request.
  - A Spot "preemption" was inferred from a user's own delete.
  - A FLEX_START queue wait counted against the run limit.
  - A Save diagnostics could fail on a common tag.

  Round 2+ still found Highs on cloud diffs.
- **Read-only gcloud only.** A hook blocks printing a token. Live checks of Compute API shapes
  (preempted operation filter, FLEX_START body) are THEORY rows in docs/ToTest.md.

## Owner asks

- **A real GPU run.** Run #83/#759 end to end and close the gpu-vm ToTest rows. This needs a billing-enabled
  project, L4 quota, the worker service account email, and a worker image digest built from the new
  `worker/Dockerfile.cuda` (see #759's ToTest row). Recommendation: set up the project in OWNER_TODO 1a/1b first;
  then one `cloud_gpu_test.ps1 -Apply -Image <digest>` run proves #37, #74, #83 and #759 together.
- **Still open from before:** #703, #672 (branch protection: CI is still not a merge gate), #721, #14 (which models to offer;
  `GpuCatalog` is now the one place to change), and the agent DECISIONs to review.
