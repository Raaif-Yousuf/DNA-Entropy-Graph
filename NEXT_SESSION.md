# Handoff: after the 2026-10-30/11-01 three-layer wave

> Overwritten every session. Re-check `git log -1 origin/main`, `gh pr list` and the lander's `land_results.txt` before trusting anything here.

## What this session was

Three layers: top orchestrator (owns main, landing, the full suite) -> 3 Opus orchestrators at a time (9 in total: A to I, each
retired near 300k) -> 1 Sonnet each, in worktrees. Every lane diff cold-reviewed. Scratchpad lander (`lander.sh`, serial
`land_wt.py --merge-when-green`). Session scratchpad: `%TEMP%\claude\C--Users-raaif-DNA-Entropy-Graph\5db34e81-1e78-479a-b0d6-2d641653acb5\scratchpad\`
(`land_requests.txt`, `processed.count`, `land_results.txt`, `wave_brief.md`, `h_held_land_lines.txt`).

- **Landed (51):** #100/#557, #109 (rescoped, remainder #998), #112, #133 NOT yet (see below), #148, #177, #195, #245, #476, #566,
  #588, #735, #814, #830 (+#938, #999), #840, #877 (pyright debt now 0, baseline empty), #878, #883, #914/#915, #916/#922, #929,
  #930/#996 (`heavy.py --status`, Job-object tree kill), #934/#954 (run-folder marker), #937/#1001/#1049 (worktree imports), #948,
  #955, #957, #958 (`scripts/premerge_wt.py`), #970, #994, #1006, #1014, #1016 (control-byte gate), #1017, #1027 (ruff pinned
  0.17.3), #1028, #1029, #1037 (output-folder picker), #1043/#1059, #1044, #1046, #1047, #1055, #1070 (MSBuild node isolation),
  #1075/#1096, #1086 (run progress finally reaches the run page), #1092.
- **Closed on verification:** #791, #932, #947, #1088 (premise disproved).

## The lander was killed (low memory) at 01:00 on 11-01; nothing is queued now

`processed.count` = 106 of 106. These FAILED at their last landing and need a repair round, P order:
- **`fix/1022-cancel-vs-vm-unhealthy-r5` @ 4d683285** (closes #1022 P2 real product race, #1062): Cloud.Tests red in 44 s at landing.
  This is the fix for the cancel-recorded-as-Failed race that also flakes unrelated PRs (SpotProvisioningTests). Do first.
- **`feat/260-switch-project-r6` @ 78af00c2** (#260, migration 0005): premerge red (one gate failed in 1 s = environment/memory, plus a long gate). Re-land.
- **`feat/133-ncbi-api-key-r18` @ 57da84fa** (#133): stacked on 260-r6, migration renumbered to 0006; land right after 260. Worktree `.claude\worktrees\agent-aac57046bc99dcb4b`.
- **`ux/1074-spot-shipped-price-line-r2` @ 21d85047** (PR #1107 open): a REAL defect: `scripts/tests/test_gen_pricing_json.py::test_the_matching_rules_match_the_csharp_resolver_not_only_the_shapes` expects resolver version 4, the branch bumped C# to 5; bump the Python side too. Its Cloud.Tests red is the #1022 flake.
- **`feat/680-reused-vm-line-r2` @ e4eabc9c**, **`feat/523-topology-option-r6` @ 9c65af97**, **`feat/1065-keepalive-fence-notice` @ 82498df2**: premerge red while the box was out of memory (one failed in 0.3 s). Re-land first; investigate only if red again.
- **Held, append after base MERGES** (exact lines in `h_held_land_lines.txt`): `feat/519-accounts-section-r8` @ 6238d241 after 260;
  `fix/1051-1035-cancel-span-booking-r4` @ cc4de868 and `fix/610-cancel-requested-marker` @ 2f73fc16 after 1022 (they may conflict with each other).

## Main health

MEASURED 2026-11-01 01:30 on b0a1ed9b: full premerge 39 of 41 gates green (all C# suites, worker-pytest, every guard). The only red:
2 `test_heavy.py` tests that fail inside the DST fall-back repeated hour because heavy.py stores slot start times as naive local time (#1110, P3).

## Next work (P1 first)

- **#204 (P1):** do NOT rebuild. `feat/204-health-checks` @ e893f533 (2026-10-16, 1422 lines, reviewed twice) plus stacked `feat/205-iam-preflight`,
  `feat/208-health-page`, `feat/209-remediation-texts` never landed. Bring them up to main (touches `DnaEntropyGraph.Cloud/**`), cold-review, land. Unblocks #818, #167, #208.
- **#1057 (P1):** NCBI key ledger has no project/account (after #133 lands).
- P2: #1081 (scripts-tests load flakes), #1095 (month-to-date test fails first UTC hour of every month), #1099, #995, #998, #1087, #1093.
- Not started (cloud-dependent app): #598, #630, #631, #668, #515, #571 (migration numbering is crowded; check 0005/0006 first), #1052.

## Measured this session

- **Memory:** the box ran out of RAM around 00:30-01:00 on 11-01 with 3 lanes plus landing premerge; Claude Code reaped the lander and a watcher. 2 heavy slots plus a full landing premerge is the ceiling; drop to 2 Opus lanes or `heavy.py --set-slots 1` late in a wave.
- **Sub-agent notifications** usually reach only the top orchestrator, so it must relay Sonnet results to the owning Opus.
- **PowerShell double-quoted strings with backticks corrupt Markdown** (control bytes, swallowed `\r`-words); edit docs with the Edit tool. #1016 gates it.
- **A `|` in a LAND title shifts the lander's fields.** A stacked follower whose base fails stays HELD; re-append it.
- **Migration numbers collide across parallel branches** (0003, 0004, 0005 all claimed twice this wave). Idea: a migration-number reservation guard.
- **CI "markdown links" can hang pending forever;** `gh pr update-branch` re-triggers it.

## Owner asks

- **DECISIONs (agent-made, reversible; recommendation: keep all):** #1022 a cancel in progress beats vm_unhealthy; #476 a refused ending mark
  means no fence-less delete (VM runs to maxRunDuration, #1065 tells the user); #112 account-less runs list under every account; #260 no
  project filter or switcher pill, only the results bucket is per project; #938 blind delete by `deg-<job>` name after an unreadable look;
  #830 pre-upgrade runs adopt the account only when exactly one is on the PC; #934 one confirm dialog for marker-less folders, shell/profile/drive
  roots refused; #883 guard on section numbers rather than dropping them; #1006 only Save picture follows the output folder; #1037 one shared
  folder setting, not exported; #878 strict manifest ints; dismissed earlier-version runs share the dismissed-runs setting; over-cap notes saved shortened.
- **#970 raised to P1** on Opus B's recommendation (done; reversible).
- **Dev databases:** any local `app.db` that ran an older #133 build (NCBI table as 0003) should be deleted; nothing shipped.
- **Leftover dirs to delete by hand** (path too long / hook blocks recursive delete): `<scratchpad>\base_wt`, `<scratchpad>\s830d\base`.
- **GCP setup** still deferred (see the 2026-10-28 handoff in git history).
