# Handoff: after the 2026-10-02/03 three-layer wave

> Overwritten every session. Re-check `git log -1 main` and `gh issue list` before trusting
> anything here.

## What this session was

Two waves in one shared checkout, with the top orchestrator running every git command.
- **Wave 1:** three Sonnet lanes.
- **Wave 2:** the owner's three-layer shape. Three Opus orchestrators each ran two Sonnets; the owner picked two per Opus over three, for RAM.
- **Results:** 14 PRs merged (#482, #483, #500-#509, plus this docs PR), and about 25 issues closed.
- **Landing gate:** each unit was verified in a clean worktree of its exact commit before merging. The full `premerge.py` ran on main afterwards.

## Start here next session

1. **Owner instruction (2026-10-02): work issues in ascending number order** with 2 Opus
   orchestrators x 2 Sonnets, skipping owner-only, DECISION, epic and GPU-VM issues. The split
   that keeps the C# lanes disjoint:
   - One orchestrator takes the real Google layer in order: #48, #50-#56, #59, #60.
   - The other takes everything else in order: #31, #32, #33, #35, #44, #63, #69, #79-#81, ...
2. **The real Google layer does not exist.** MEASURED: no `Google.*` package is referenced
   anywhere in app/. Production DI is `FakeGcp().WithCloudNotConnected()`, so every run fails
   fast with `cloud_not_connected` until #48/#50-#56 and #69 land.
3. **Split `CloudJobRunner.cs` before adding the real gateway.** It is about 1700 lines (provision,
   await, download, cancel), and every cold-review round of it found real defects.
4. Owner decisions are pending:
   - dismiss secret-scanning alert #1 as "used in tests" (it was a synthetic fixture, now built at runtime by #486)
   - enable Dependabot alerts
   - set the repo variable `DEPENDENCY_GRAPH_ENABLED=true` (#325)
   - rescope #44

## How landing works now (MEASURED this session)

- **Git hunks cannot separate units that share a file** (Resources.resw, ServiceRegistration.cs,
  NavigationRoutes.cs, dev_commands.md). Have each orchestrator write the exact committed
  content per unit (origin/main plus that unit only, or cumulative in landing order), then stage
  the blobs with `git hash-object -w --path=<p>` and `git update-index --cacheinfo`.
- **Verify the exact commit in a throwaway worktree, never in the shared tree.** This catches
  failures that the shared tree hides:
  - A guard read the shared tree.
  - An orphan resw key rode in a unit.
  - A test planted a user-home path.
- **Three requirements for the throwaway worktree:**
  - `worker\.venv` is a junction to the real venv, unlinked with `rmdir` before `git worktree remove`.
  - `PYTHONPATH=<wt>\worker\src`, so the editable install does not import the shared tree's code.
  - `--artifacts-path` sits inside the worktree, because some tests walk up from the test binary (#495).
- The orchestrator's scripts for this were in the session scratchpad. Making them a repo tool is
  worth an issue: `land_pr.py --content-dir` plus a worktree verify.
- **`dotnet test` must run from `app\`** (global.json selects Microsoft.Testing.Platform). Use
  `heavy.py --lane <name> --` for every agent build (#487).
- **Never run a git command while `land_pr.py` runs in the background.**

## Found this session, worth more than the fixes

- **Cold reviews keep paying.**
  - 13, 18, 10 and 7 findings on four rounds of the cloud runner.
  - 11 on the viewer, 8 on the validator and 10 on heavy.py.
  - 5 on the worker keep-alive change, including failed runs billed for an idle GPU.
- **CodeQL had never analysed C#.** Fixed (#484): the first C# analysis found 182 alerts, about 110 of them in generated code. Triage is in #491 (C#) and #488 (Python).
- **The app was launched and driven for the first time.** Build it with heavy.py and launch it
  maximized (owner instruction). UI Automation reaches the native controls, and
  `PrintWindow(hwnd, hdc, 2)` after `SetProcessDPIAware()` takes screenshots without stealing
  focus. WebView2 content is opaque to UI Automation.

## Still unproven

Nothing cloud-facing has run against real Google Cloud. `docs/ToTest.md` has a row for every
closed behaviour that needs a VM, an installer, or a real dev build.
