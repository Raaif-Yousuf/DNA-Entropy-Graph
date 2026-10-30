# Handoff: after the 2026-10-28b/29 three-layer wave

> Overwritten every session. Re-check `git log -1 origin/main`, `gh pr list` and the lander's `land_results.txt` before trusting anything here.

## What this session was

Three layers: one top orchestrator (owns main, landing, the full suite) -> 3 Opus orchestrators at a time (7 in total over the
session, each retired near 300k context) -> 1 Sonnet each, in worktrees. Every lane diff cold-reviewed. A scratchpad lander
(`lander.sh`) landed serially with `land_wt.py --merge-when-green`.

- **Landed (about 40):** #242, #244, #813, #812, #243, #499/#701/#807, #892/#904, #652, #552, the RunOptions equality fix
  (PR #940), #925, #875/#910, #214, #239, #761/#653 (CodeQL product-only + format-arity guard), codeql-misc,
  codeql-path-combine, #920, #741 (process-wide `ClearAllPools` flake), #720, #841, #828, #846/#847, landed-prs record race,
  #959 (scripts tests under pytest-xdist: 1600 s -> ~600 s), #587, #255, #141, #514, #945/#927, #933, #909, dead-pid flake, #620/#624, #772.
- **CodeQL:** 90 triaged product false positives dismissed (path-combine, py ineffectual stubs); tables were in the session scratchpad.
- **Closed superseded PRs:** #876, #857, #900, #926, #928.

## Landing queue at hand-off (lander may still be running)

Lander: `%TEMP%\claude\C--Users-raaif-DNA-Entropy-Graph\f06a0c5f-11b5-4b76-b349-ebb248ff3a02\scratchpad\`
(`land_requests.txt` + `processed.count` pointer, results in `land_results.txt`, stop with `touch STOP`).
Queued after it: #918, #262/#534-r12, #923-r3, #133-r8, #109-r14 -> #100-r15, #844, #840 (timeout only), #588-r4, this handoff.

**Need a repair round (FAILED at landing):**
- `feat/245-cloud-audit-log-r13` @ cae874c9: Cloud.Tests red at landing premerge (log under `%TEMP%\landwt\logs\feat-245-*`).
  `feat/148-billing-budget-r13` @ 6e9cce11 is stacked on it (HELD).

## Held / not started (P1 first)

- **#938 (P1)** `fix/938-late-create-after-cancel-r3` @ 9a17aaa8: a create landing after a cancel is never deleted. Round-3 High:
  the real delete swallows 404, so a delete sent before the VM exists counts as accepted and is never re-sent.
- **#830 (P1)** not started; design is the DECISION comment on the issue. Then #476/#523, #260, #112, #519.
- **#958 (P1)** one safe command for `premerge --fast` in an agent worktree (junction + PYTHONPATH + guaranteed unlink).
- #921 needs a lane with Docker (CUDA image hash-lock). #929 after #588 lands.
- Unstarted app list: #515, #566, #571, #598, #610, #630, #631, #668, #680, P3s #914 #915 #916 #922.

## Measured this session

- **A land_wt refusal named only CodeQL while build-and-test was also red; a hand merge then turned main red** (#552 -> fixed by
  PR #940; #927 fixed the message). Never hand-merge without `gh pr checks` showing every non-CodeQL check green.
- **Fast premerge skips the C# suites**: lanes must run the affected C# projects; PR CI is the full gate.
- **scripts-tests exceeded 1800 s under 3-lane load** with zero failures; #959 parallelised it.
- **`SqliteConnection.ClearAllPools()` is process-wide**: disposing one database closed other tests' handles (#741; guard idea #951).
- **Windows reuses a reaped PID quickly**; a "dead pid" test helper must use an impossible pid.
- **The owner's CLAIR app loads a local model (5-8 GB)**; free RAM fell to 0.4 GB. Drop heavy.py slots to 1 when `llama-server` runs.

## Owner asks

- **GCP setup** (deferred): billing link, budget, GPU quota, make the ghcr package public; commands in the git history of this file (2026-10-28 handoff).
- **DECISIONs to confirm (agent-made, reversible; recommendation: keep all):** #109/#100 ledger is the only cost source and reads never
  write; cancel stops (never deletes) a reused VM; a VM end is never booked in the future; #933 stuck listing defers the run (safe
  only with #972); #133 key deleted by the app, not the VM; #620/#624 missing account says "sign in again"; drive-root output folders refused (#954 adds the user action).
- **#934:** older runs without an output-folder marker: refuse delete, or ask once with a dialog (recommend the dialog).
- **#840:** relax "under 3 minutes" to "a hang fails fast" (done)?
