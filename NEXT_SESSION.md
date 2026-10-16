# Handoff: after the 2026-10-16b wave

> Overwritten every session. Re-check `git log -1 origin/main`, `gh pr list` and the runner's `done.log` before trusting anything here.

## What this session was

One orchestrator, at most 3 Sonnet lanes in worktrees. Every lane diff got a framing-free cold review before landing,
and deletion or tooling diffs got 2 rounds.
- **Unblocked the landing queue.**
  - #788: the C# GenBank validator ports Biopython's location verdict and shows the worker's dropped-feature notice.
  - #791: the cloud chain gained the missing `RunErrorActions` rows. It was rebuilt twice: as `-ea`, then as `-ea2` after
    local-engine landed its own rows.
- **Landed:**
  - Issues: #114 (retention), local-engine #120/#173/#174, #788, #89, #90.
  - Tools: #794 (`scripts/prune_worktrees.py`), #792 (ruff format of scripts/).
- **Built and reviewed, landing or held:** #138 CLI (held), #144 portable mode, #143 onboarding tour, #800 resw driver fix.
- **Cleanup:**
  - Worktrees went from 153 to 55, local branches from 383 to 73, and remote branches from about 100 to 4.
  - A worktree was kept if it had uncommitted changes or commits not on main.
  - A local branch was kept if its content is in no other branch (a `git merge-tree` check against main and every queued or held tip).

## The landing runner (still running when this session ended)

`%TEMP%\claude\C--Users-raaif-DNA-Entropy-Graph\2fcad6ed-bcba-448f-9b07-bbd7f0ed5eff\scratchpad\land_queue.sh`
is an orphan from an earlier session. It reads `queue.txt` (`branch|sha|title`; a line starting `#` is skipped), runs
`land_wt.py --merge-when-green` per line, and appends `MERGED`/`FAILED ... b @ sha` to `done.log`.
- **Skip rule:** a line whose `b @ sha` is already in `done.log`, FAILED or MERGED, is never retried. To retry the same sha,
  delete its FAILED line from `done.log`.
- **Stopping it:** `touch STOP` in that folder.
- **Queued, in order:**
  1. feat/144-portable-mode @ 513d1109 (retry; the last failure was flaky test #808)
  2. The `-ea2` cloud chain from 94 up to 759: 94, 670, 678, 679, 691, 105, 99, 675, worker5, 723, 759
  3. fix/694-origin-wrap-writers-r2-ea @ 6128545b, then feat/129-130-192-analysis-commands-r2-ea @ 3d2407e4
  4. fix/resw-merge-driver-conflicts @ cfd5a6c6 (#800)
  5. fix/prune-tools-build-output @ d8037b2d
  6. feat/143-onboarding-tour @ 3b69c020
- **If a chain item fails, comment out the items above it in the same chain at once.** They are stacked.
- **If it died:** re-run the remaining items one at a time with `scripts/land_wt.py`, or land `feat/land-queue-tool` first.

## Start here next session

1. **Check `done.log`.** Fix any FAILED item (read its `land-*.log` and the premerge log under `%TEMP%\landwt\logs`).
   CodeQL `cs/invalid-string-formatting` false positives still appear until #761 lands. Dismiss them by hand after
   checking the resw template.
2. **Held: cold review first.** These are the `-ea2` chain from #684 up. Their merges with local-engine were made by hand:
   - feat/app10-684-accession-rs-ea2 @ 771442c3
   - feat/app12-base-rs-ea2 @ d0087cbd, then 99 @ 8bb5ff55, 105 @ c4ea0cea, 675 @ cc23fff8, 717 @ a7d1d843, 677 @ 60919412
   - feat/111-import-runs-ea2 @ 58733319, feat/113-nothing-left-running-ea2 @ a763d303
   - feat/91-spot-provisioning-ea2 @ 48e2f2fb, feat/92-flex-start-ea2 @ 93fe7b31 (on 91), feat/87-gpu-planner-ea2 @ 431606c3
   - feat/109-spend-ledger-ea2 @ 0f5566bb (on 91 and 87), then feat/100-557-run-cost-ea2 @ 394615b2

   Review the merge resolutions: `NewRunViewModel.BuildRunOptions` (a This PC run carries the GPU planner's ModelId and
   tier), and 100-ea2 deleting `Core/RunTargets.cs` in favour of the local engine's `RunTargets`. Then queue them in that
   order. Migrations stay 0002 (#91) and 0003 (#109).
3. **#138 CLI, held:** feat/138-cli @ 432104bf. Its cold-review findings are below. Fix them on a fresh agent AFTER the
   chain lands, because they touch `JobReconciler` and `JobEngine`.
   - **Recommended fix (THEORY):** a driver lease in SQLite (`runs.driver_pid` plus a heartbeat).
     - The reconciler takes over any row whose lease expired, including CLI runs started after the app launched.
     - A second process never double-drives.
     - `deg` cancels the runner on Ctrl-C and waits for a terminal or durable state before exiting.
   - **DECISIONs to review:** #801 (deg is separate from CloudCli) and #802 (no `--wait` hands off to the app).
   - **Packaging:** #803 (deg.exe is not shipped yet).
4. **Still held from before:**
   - feat/141-run-notes-tags: fix #782 first.
   - feat/112-per-account-runs: never cold-reviewed; migration 0004.
   - feat/land-queue-tool: re-run ruff format.
   - ci/761-653-codeql-src-only: last; regenerate `format_arity_sites.txt`.
5. **Then continue issues in ascending order.** From triage on 2026-10-16:
   - **Built and closable:** #44 (rescope; move the keep-idle box to #93), and #168 and #182 (only an owner step is left).
   - **Next buildable:** #137/#139 (after #138), #142 (signed URL), #146 + #164 (crash reporting + correlation ids),
     #148 (budgets), #156/#159/#195 (viewer theming), #157/#158, #167, #198.
   - **After the chain:** #93 (keep-alive queue), #149, #150, #151, #152, #177.
   - **Blocked (owner or real GPU):** #84, #85, #95, #96, #163, #166, #170.

### #138 cold-review findings (round 1)
- **HIGH:** the CLI process is the only driver of its run. Ctrl-C or `--timeout` while provisioning can leave a VM and a
  row stuck at Provisioning, and the "continues in the cloud" copy is false before Preparing.
- **HIGH:** `ActiveRuns` is per process, and `JobReconciler.cs:613` only reattaches rows created before the app started.
  So an open app never collects a CLI run, an app launched mid-CLI-run double-drives it, and `deg cancel` from a second
  shell can overwrite Cancelled.
- **MED-HIGH:** `Start-DegRun .\file` resolves against the process cwd, not `$PWD`. `Invoke-Deg` swallows a non-zero exit
  that has no `ERROR:` line.
- **MEDIUM:**
  - `SqliteDatabase.IsReadable` treats a busy DB as corrupt, so it can quarantine the app's live DB.
  - Two migrators can race on one DB.
  - The OAuth client file is not found beside `deg.exe`.
  - `RedownloadAsync` writes back a stale row.
- **LOW:**
  - Exit codes for `cloud_not_connected` and an expired sign-in.
  - Tests that only cover the failure path.
  - `AppVersion` is now read from the Hosting assembly.
  - `ActiveRuns` is registered twice.

## Measured this session

- **Hand-merged chains go stale fast.** Any landing that appends to `RunErrorActions.cs`, `RunErrorCodes.cs`,
  `ServiceRegistration.cs` or `Resources.resw` conflicts every queued branch that also appends there.
  - Land independent branches that touch these files BEFORE re-merging a long chain.
  - Or re-merge the chain bottom-up once and land it without interleaving.
- **The resw merge driver failed on older branches (#800).** git ran the driver script from the merged tree, so
  branches older than the driver had no script and no attribute line. `setup_git_drivers.py` now installs `origin/main`'s
  copy under `.git/deg-drivers/` and writes `info/attributes`. Re-run it once per clone after #800 lands.
- **New files from lanes often lack a final newline.** That fails premerge's `dotnet format` gate. Two branches failed
  landing on it.
- **`RepoPaths.AllXamlFiles` included obj/bin copies.** It is fixed on the #143 branch. A guard test using `Single()` on a
  file name failed in in-tree builds.
- **Flaky under load:** `ReattachOnStartupTests` (#808) and `RunTargetPickerSharingTests` (once).
- **Monitors on `done.log` replay the whole file when it is rewritten in place.** Append only.

## Owner asks

- **DECISIONs to review:** #801 and #802 (CLI).
- **A real GPU run is still the biggest unproven area.** #83/#759 end to end needs OWNER_TODO 1a/1b.
- **Earlier asks, still open:** #672 (branch protection), #14 (which models to offer).
