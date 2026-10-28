# Handoff: after the 2026-10-16c wave

> Overwritten every session. Re-check `git log -1 origin/main`, `gh pr list` and the runner's `done.log` before trusting anything here.

## What this session was

One orchestrator, at most 3 Sonnet lanes in worktrees, every lane diff cold-reviewed (cloud, deletion and tooling diffs 2 to 7 rounds).
Issues worked in ascending order from the 2026-10-16b triage; about 45 branches were built and reviewed.

- **Landed this session:** #94, #670, #678, #679, #691, #99 (gateway + wizard), #675, #723, #759 (GPU test mode), #694 (+#543),
  #129/#130/#192, #800 (resw driver), #143, #144, #784 (a real product race: a cancel during VM reuse could leave a
  running VM), #825 (every DI container shared `WeakReferenceMessenger.Default`), app10 #684/#686/#688, app12 base, #105 app side.
- **Closed as already done (verified on main):** #44, #54, #299, #480, #583, #627, #712.
- **Cleanup:** worktrees 87 -> 3, local branches 160 -> 52 (all queued, held or active), remote branches -> main + the
  Dependabot PR + the branch landing. Every removed worktree's dirty state is archived (diff + untracked tar) in
  `%TEMP%\claude\C--Users-raaif-DNA-Entropy-Graph\9a565426-06c5-4d10-8b43-9ca87d758089\scratchpad\worktree-archive\`
  (index.txt maps each to its branch). The tracked "modifications" there are stale trees from branch refs moved by
  later lanes, not lost work.

## The landing runner (still running when this session ended)

`%TEMP%\claude\C--Users-raaif-DNA-Entropy-Graph\2fcad6ed-bcba-448f-9b07-bbd7f0ed5eff\scratchpad\land_queue.sh`, orphan, reads
`queue.txt` (`branch|sha|title`, `#` skipped), appends `MERGED`/`FAILED ... b @ sha` to `done.log`. Stop: `touch STOP` there.
A `b @ sha` already in done.log is never retried; requeue a fixed branch under a new name (`-r2`) or a new sha.
Order (landing now, top first): the `-ea3` cloud chain (675, 717, 677, 111, 113, 91, 92, 87, 109, 100), then 214, 148,
204 -> 205 -> 208 -> 209 (stacked), 239, tool-lq-land, 255, 242 -> 244 -> 243 (stacked), 259, 450, 303, 245, 479,
262/534, 488/491, 563, 613, 423, 499 (stacked on 243), 527, 451, 247 -> 497 -> 699 (stacked), 842, 567, 568 -> 601
(stacked), 652, 141, 611/689/796, and this handoff.

**Expect landing failures that need a small repair lane, by design:**
- Hot files (ServiceRegistration, Resources.resw, RunErrorCodes/Actions, SettingsPage, ToTest.md) will conflict as
  the chain lands. The resw merge driver is installed in this clone (`setup_git_drivers.py`).
- `feat/242-settings-export` adds a guard that fails when any settings key is neither exported nor excluded; keys
  added by the chain (spend cap, This PC, cloud results), budget (#148) and proxy (#239) must be added to
  `SettingsCatalog` at that landing.
- `docs/303` changes shipped Spot prices (L4 0.18 -> 0.51 MEASURED); `feat/214` tests that pin the shipped Spot
  value may need the new number.
- `tool-lq-land` replaces the scratchpad runner with `scripts/land_queue.py` (#813). Switch to it once it lands.

## Start here next session

1. **Check `done.log`.** For each FAILED: read `land-<branch>.log` and `%TEMP%\landwt\logs\<branch>-*.premerge.log`.
   CI-only flakes: rerun the failed job once (`gh run rerun <id> --failed`). CodeQL `cs/invalid-string-formatting`
   on resw templates is a known false positive (#761): check the template, dismiss, merge by hand.
2. **Then #830 (P1): thread the run's own project AND account through every gateway call.** Compute calls and the
   reconciler use the currently selected project/account, so a switch mid-run orphans the VM. Held behind it:
   - `feat/260-switch-project` @ 4e0f53dc (#260; DECISION comment on the issue: rebase on #830, drop the guard).
   - `feat/112-per-account-runs` @ 823a8c2d (#112, never landed; review findings on the issue; renumber its
     migration after 0003, #842 now enforces contiguity).
   - #519 (accounts section) belongs in the same lane.
3. **Still held from before:** #138 CLI (`feat/138-cli` @ 432104bf; findings in the 2026-10-16b handoff, git history of
   this file), `ci/761-653-codeql-src-only` (its `scripts/tests/test_codeql_workflow.py:65` still asserts the old
   build line, so it fails premerge as is).
4. **Built, NOT cold-reviewed, not queued (the session ended first):** `tooling/584-notices-regen` @ 0c5a4fc8
   (#584, built on tool-lq-land; needs a ToTest app-dev row citing 0c5a4fc8) and `tooling/812-prune-content-check`
   @ 599804b9 (#812, content-based prune with `--contained-in` and `--remote`). Cold-review both, then queue.
5. **Code-scanning dismissals:** after `quality/488-491-codescan` lands, dismiss the reviewed false positives listed in
   `...\9a565426-...\scratchpad\codescan-dismiss.tsv` (165 FALSE-POSITIVE rows; the NOISE-CONFIG rows close by config).
6. **Next buildable, ascending:** #514, #515, #522-follow-ups, #523, #552, #556 (after #109), #566, #571, #588, #591
   (DECISION), #598, #610, #620, #624, #630, #631, #668, #680, #697, #720, #722, #735, #741, #768, #769, #772, #803,
   #828, #840, #841, #846, #847, plus the cost family #215-#225 once #100/#109 land.

## Measured this session

- **A cancel during VM reuse could leave a running VM under a cancelled run (#784).** It looked like a flaky test.
  Treat a flaky cloud test as a product race until proven otherwise.
- **GCS lists objects alphabetically, so `manifest.json` is deleted early in a prefix sweep.** The delete now removes
  it last (#262/#534).
- **`gh` itself failed to start (0xC0000142) during a long wave and land_wt reported "no checks were reported"**
  (#811). land_wt now reports gh read failures as such and bounds every gh/git call.
- **Shipped L4 Spot price was about 3x low** (0.18 vs 0.51/h, cloud.google.com Spot pricing, #303).
- **Only `Window.Resources` converters crash XamlCompiler silently; `Page.Resources` builds** (#423).
- **A newer app.db was silently used by an older app, and a busy DB could be quarantined or deleted** (#842).
- **Two heavy slots (heavy.py) are the right limit with 3 lanes:** RAM stayed above 12 GB free; lanes queued for
  slots for up to an hour during 20x stress loops, which is acceptable.
- **Lanes left sleep monitors that kept waking finished agents.** Stop a finished agent with TaskStop once its
  result is in.

## Owner asks

- **GPU runs are blocked on #24.** The only configured gcloud project is `clair-corpus-52g9xx` (the CLAIR project). Create
  `deg-dev` per OWNER_TODO 1b, or say to use the CLAIR project, and `cloud_gpu_test.ps1 -Apply` can run #83/#84/#85.
- **DECISIONs:** #801, #802 (CLI), #14, #672, plus the agent-made ones recorded this session (#450 docs follow
  workflows; #260 hold).
