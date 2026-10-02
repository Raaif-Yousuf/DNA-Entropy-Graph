# Sprint log

Append-only. One entry per merge, newest first, under "Recent changes"
below. Never edited after the fact (`docs/README.md`'s house-style table).
Do not hand-write an entry here directly — write
`docs/changelog.d/<branch-name>.md` on your branch instead and let
`python scripts/compile_sprint_log.py` fold it in at merge time. See
[`changelog.d/README.md`](changelog.d/README.md) for the fragment
convention.

## Recent changes

- Fixed #401: `check_probability_matrix` now names inf explicitly with offending row indices before the [0, 1] range check, mirroring the NaN diagnosis from #347. Extracted shared `_format_row_preview` helper for NaN and inf row previews. Updated `docs/science_and_formats.md`.

- `ci-app.yml`, `ci-worker.yml` and `ci-docs.yml` now run on every pull request touching
  their own area (plus `workflow_dispatch`, kept for a manual re-run), instead of
  `workflow_dispatch` only. No `push` and no `schedule` trigger on any of them: a PR's own
  check is the gate, and `main` only ever gets there through a PR one of these already ran.
  `codeql.yml` stays `workflow_dispatch` only, with a comment explaining why (its csharp
  lane is a full Windows dotnet build, too heavy to pay for on every PR).
- Fixed `test_genes_flag_reports_a_gene_count_in_the_summary` in
  `worker/tests/test_cli.py`, which needs the optional `[genes]` extra
  (Pyrodigal) and neither installed it nor skipped without it. It now skips
  with `pytest.importorskip("pyrodigal")`, matching how `test_annotator.py`
  already guards itself; `ci-worker.yml` installs the `genes` extra, so the
  test still runs for real there.
- Skipped, rather than failed, the three THIRD-PARTY-NOTICES tests that run against the
  real dependency tree when the machine cannot enumerate it. They need a `dotnet` that can
  restore `app/` (impossible on a Linux runner: the app is WinUI 3 and its packages are
  Windows-only) and a populated `worker/.venv` with the optional extras, which is
  gitignored. `ci-docs.yml` already excludes the script itself from its guard loop for
  exactly this reason (#416); its tests were not excluded, so turning on pull-request CI
  made them red for a reason unrelated to any change under test. The new
  `scripts/tests/conftest.py` probes both prerequisites once per session and skips with a
  reason naming the missing one. Confirmed both arms: with only the `dev` extra installed
  the three skip and the rest of the suite is 337 passed, and after
  `pip install -e "worker[dev,genes]"` all 17 tests in the two files run for real and pass.
- Fixed `ci-app.yml`'s vpk pack step, which turned the first pull-request run of that
  workflow red: `--packVersion 0.0.0-ci` is below the `0.0.1` floor vpk 1.2.0 enforces, so
  it refused to pack at all. It is now `0.0.1-ci.<run number>`. The artifact is a
  seven-day throwaway that is never published, so nothing about the version is meaningful
  beyond being one vpk accepts.
- Named the five library test projects explicitly in `ci-app.yml`'s test step instead of
  running the whole solution. MEASURED 2026-09-20: a solution-wide `dotnet test` hung at
  that step on windows-latest twice in a row (runs 35528323292 and 35529823866, both
  cancelled by hand after more than twenty minutes on a step that takes nine seconds when
  it works). The one assembly that is not a plain library is
  `DnaEntropyGraph.App.UiTests`, which is WinUI-app-hosted and so starts a real desktop app
  in a session with no interactive desktop; every test in it is `[Fact(Skip=...)]` today,
  so leaving it out costs no coverage. Locally the five projects are 309 tests, 0 failing,
  0 skipped.
- Left `ci-app.yml` on `workflow_dispatch` after all, with the measurement written into the
  file. Its `test` step hangs on windows-latest: three runs (35528323292, 35529823866,
  35530219025) all had to be cancelled by hand after six to twenty-five minutes on a step
  that takes nine seconds when it works and about 25 seconds locally. Dropping the only
  WinUI-app-hosted test assembly did not fix it. A check that never finishes in front of
  every pull request is worse than one run by hand, so `ci-worker` and `ci-docs` go to
  pull requests now and this one follows when the hang is understood. Filed as an issue.

- Rewrote `README.md` around what a reader actually needs in the first ten seconds: what the
  app does, the bring-your-own-GPU design and why it exists, and a measured table of what
  runs today against what has never run. The old README's three "if you are here to..."
  sections and its milestone list were re-derivable from `docs/` and the milestones page and
  went stale the moment either moved, so the README now links to those surfaces instead of
  restating them. Every number in it is dated and was measured on the day it was written.

- Fixed #424 and the status pill's own resource-key bug: `IStringResourceProvider.GetString`
  is called from code with keys like `StatusPillSignedIn.Text`, but a `.resw` entry named
  `Foo.Text` compiles into the PRI as the nested resource path `Foo/Text` -- the convention
  `x:Uid` relies on for a XAML binding, which a literal-string code lookup has no way to use.
  Every key reached only from code (`StatusPillSignedIn`, `StatusPillNotSignedIn`,
  `ConfirmDeleteResource_Title`/`_Body`, `NoRunsYet_Title`/`_Body`, `ThemeUpdated_Title`,
  `ConfirmStopVm_Title`/`_Body`, `ConfirmDeleteVm_Title`/`_Body`) is now the plain, non-dotted
  form the `Phase*_Title` keys already used; keys also reached via `x:Uid`
  (`ShellTitle.Text`, `NavNewRun.Content`, ...) keep the dotted form.
- Fixed #424's own bug: `FakeGcp` defaulted to signed in with `fake-project` selected, so a
  fresh profile's status pill never actually read "Not signed in" against the fake. The
  default is now signed out with no selected project; `WithSelectedProject` arms a
  signed-in fake explicitly (`WithSignedOut` restates the default for a test that depends
  on it). `SignInAsync` now actually flips the fake to signed in (to `fake-project`) instead
  of no-opping, per issue #424's own "Done when" -- `WizardViewModel.SignInAsync` reads
  `IGcpAccount.IsSignedIn` right after awaiting this call, so a no-op left that flow wired
  to nothing against this fake.
- Added `DnaEntropyGraph.Guards.Tests.StringResourceKeyGuardTests`: scans every C#
  `strings.GetString("...")` call site against the real `Resources.resw` and fails if a
  looked-up key is dotted or has no matching entry, so this bug class cannot recur silently.
- Moved the status pill out from under the window caption buttons. With a 16 DIP right
  margin it sat directly beneath the minimise, maximise and close buttons that the system
  draws over an `ExtendsContentIntoTitleBar` window, so "Not signed in" and the close
  button overlapped on screen. The three buttons are 46 DIP wide each, so a 154 DIP margin
  clears them with a 16 DIP gap and holds at any display scale because the margin is in
  DIPs.

- `NEXT_SESSION.md` rewritten for the 2026-09-19 six-lane wave: seventeen pull requests, the app
  suite from 51 tests to 316, and only five `v0.1 walking skeleton` issues left open.
- Four `docs/ToTest.md` rows added for #62, #66, #197 and #34, each naming its own false pass.
  The sharpest is #62's: a `.resw` not packed as a PRI resource gives a window whose every label
  is blank, which reads as an unfinished layout rather than a broken build, so the row names the
  six string ids to check by hand because not one has ever been seen rendered.
- Fixed a literal user-home path (a real lab account's `Downloads` folder, hardcoded instead
  of `%USERPROFILE%\Downloads`) committed in `SettingsStoreTests.cs`. `check_user_home_paths.py`
  caught it, but only on the end-of-wave sweep: the guard had been run before that lane merged
  and not after, which is the argument for running the full guard set per lane rather than per
  night.

- Replaced my real university email address with `a.researcher@university.example` in
  `scripts/sync_memory.py`'s self-test cases and in every fixture in
  `scripts/tests/test_sync_memory.py`. The scanner's whole job is to keep personal
  identifiers out of committed files, so using a live personal address as its own sample
  text was the one string in that file that should not have been literal. The pattern under
  test is unchanged and `.example` is a reserved TLD, so the fixtures exercise the same
  branch and can never match a real person. Verified with 48 passing tests in
  `scripts/tests/test_sync_memory.py` and a PASS from `sync_memory.py --self-test`.

- Added the real `NavigationView` shell (issue #62): `app/src/DnaEntropyGraph.App/MainWindow.xaml(.cs)` now hosts an extended, draggable title bar, a Mica backdrop, three top-level nav items (New run, Runs, Cloud) plus a footer Settings item, an active-run `InfoBadge` on Runs, and a status pill bound to `ShellViewModel.StatusPillText`.
- Expanded `ShellViewModel` (`app/src/DnaEntropyGraph.Presentation/ViewModels/ShellViewModel.cs`): `StatusPillText` (reads `"Not signed in"` or `"Signed in - {projectId}"` from `IGcpAccount` through `IStringResourceProvider`, never a literal) and `HasActiveRun` (a `HashSet<string>` of active job ids kept in sync by subscribing to `RunPhaseChangedMessage` over `CommunityToolkit.Mvvm.Messaging.IMessenger` - no direct reference to `JobEngine`).
- Added `app/src/DnaEntropyGraph.Presentation/Messaging/RunMessages.cs`: `RunPhaseChangedMessage` and `RunProgressChangedMessage`, the message types `docs/architecture.md` section 3 already named as the design (`JobEngine` publishes `RunProgressChanged`/`RunPhaseChanged` over `WeakReferenceMessenger`) but that did not exist as code until now.
- `app/src/DnaEntropyGraph.App/JobEngine.cs` now takes an `IMessenger` and actually sends `RunPhaseChangedMessage(jobId, JobPhase.Validating)` on `StartRunAsync` and `RunPhaseChangedMessage(jobId, JobPhase.Cancelled)` on a successful `CancelRunAsync` - the first real (if still skeletal) driver of the phase state a UI can observe.
- Added theme-at-startup and window-placement persistence: `app/src/DnaEntropyGraph.App/Services/ThemeApplier.cs` (a pure `string? -> ElementTheme` mapping plus a one-line `Apply`), `WindowPlacementService.cs`/`WindowPlacement` record (round-trips through `ISettingsStore`, falls back to a sane default on a missing/corrupt/zero-sized saved value), and `WindowPlacementApplier.cs` (applies on launch, saves on every `AppWindow.Changed`). All three are plain classes outside `MainWindow.xaml.cs` on purpose - Hard Rule 8 forbids branching in code-behind, and the branch each of these needs (string switch, null-coalescing, validation) has to live somewhere.
- **Found and fixed a real Hard Rule 13 violation** while building the `IStringResourceProvider` abstraction these needed: `CloudResourcesViewModel.ConfirmDeleteAsync`, `ResultsViewModel.LoadAsync` and `SettingsViewModel.SetTheme` all passed literal strings straight to `IDialogService.ConfirmAsync`/`IToastService.ShowToast`, invisible to every existing guard (see the #71 fragment). Fixed by adding `IStringResourceProvider` (`Presentation/Services/`, implemented by `App/Services/ReswStringResourceProvider.cs` over the Windows App SDK `ResourceLoader`) and moving those six literals into `Resources.resw`.
- **MEASURED 2026-09-19**: `ExtendsContentIntoTitleBar="True"` as a XAML attribute, and a `Window.Resources` entry instantiating a custom `IValueConverter`, each independently crash this repo's pinned Windows App SDK version's `XamlCompiler.exe` with no diagnostic output at all (confirmed by direct invocation, `-v:diag`, and reading its own `output.json`) - reproduced in isolation down to one attribute/one resource entry on an otherwise-empty `Window`. Both are set from code-behind instead (`MainWindow.xaml.cs`'s constructor; `App/Converters/VisibilityHelper.cs`'s `{x:Bind converters:VisibilityHelper.FromBool(...)}` in place of a resource-dictionary converter). Filed as an issue for whoever next touches this Windows App SDK version.
- Added `app/src/DnaEntropyGraph.App/Startup/NavigationRoutes.cs`: registers "RunProgress" only. New Run/Runs/Cloud/Settings are sibling issues' own Views (#63/#101/#105/#104 per `scripts/app_wiring_allowlist.json`) and are deliberately not registered yet.
- `ServiceRegistration.cs` gained `IStringResourceProvider`, `IMessenger`, `WindowPlacementService`, `ILogTailReader`, `IRunVmActions`, and the `SqliteDatabase`/real `RunRepository`/real `SettingsStore` registrations issue #67 (merged to `main` mid-session) needed; `DiResolutionTests` gained matching entries for every one of them.
- Docs: `docs/ui_conventions.md` gets a short addendum on the two `XamlCompiler.exe` traps above and the `x:Bind`-method-call pattern used in place of a converter resource.

- Built the real run progress page (issue #66): `app/src/DnaEntropyGraph.App/Views/RunProgressPage.xaml(.cs)`, registered as `NavigationRoutes`'s only route today, resolving its `RunProgressViewModel` from the app's `IServiceProvider` in its parameterless constructor (WinUI `Frame.Navigate` gives pages no other way in) and setting `ViewModel.JobId` from `OnNavigatedTo`'s `e.Parameter`.
- Expanded `RunProgressViewModel` (`app/src/DnaEntropyGraph.Presentation/ViewModels/RunProgressViewModel.cs`) from 36 lines to a real state machine driven entirely by `RunPhaseChangedMessage`/`RunProgressChangedMessage` over `IMessenger`, applied through `IDispatcher` (never a direct field write - CLAUDE.md's "long operations never block the UI thread"): `CurrentPhase`, `StageTitle` (resolved per-phase from `Resources.resw` via `IStringResourceProvider`, matching `docs/copy_catalog.md` section 1's key convention), `StatusDetailText`, `FractionComplete`, `LogTailText`, `IsVmActionable` and `CanCancel` (both computed from `JobPhase` alone, per `docs/ui_conventions.md` section 7's table - never from a VM's own RUNNING status, per CLAUDE.md's "RUNNING is not working"), and `IsStayOpenBannerVisible`.
- `CancelCommand`, `StopVmCommand`, `DeleteVmCommand`: all three ask `IDialogService.ConfirmAsync` first (copy from `Resources.resw`'s `ConfirmStopVm.*`/`ConfirmDeleteVm.*` keys) and only act on a confirmed answer - proved by a test that declines the dialog and asserts the gateway method was never called, not just that the dialog appeared.
- Added `Presentation/Services/IRunVmActions.cs` (deliberately separate from `IJobEngine`, which is owned elsewhere in this session's lane split) and `Presentation/Services/ILogTailReader.cs`, implemented by `App/JobEngine.cs` (now also `IRunVmActions`) and `App/Services/FileLogTailReader.cs` (reads `runs/<jobId>/logs/worker.log` under the local-run root, job_contract.md section 1's path convention; never throws on a missing file).
- **Documented placeholder, not a claim of real behaviour**: `JobEngine.StopVmAsync`/`DeleteVmAsync` degrade to the same effect as `CancelRunAsync` today, because no `CloudJobRunner` exists yet to hold a real VM reference (see that file's own doc comment). The real Compute API stop/delete belongs in `DnaEntropyGraph.Cloud` behind a `Core/Cloud` interface (Hard Rule 7) once that runner lands.
- Cost ticker and the full plain-word narration table (`docs/copy_catalog.md` section 2's zone-ladder/cache/lifecycle lines) are **not** built tonight: `CostEstimator` does not exist in Core yet (read-only to this lane) and no runner drives per-window progress yet. `StatusDetailText` is wired to whatever `RunProgressChangedMessage.Progress.Message` says, which is real end-to-end wiring with nothing yet sending real messages beyond the two phase transitions `JobEngine` itself drives (see the #62 fragment).

- Finished issue #71's remaining "Done when" line: added `DialogStringLiteralScanner`/`DialogStringLiteralGuardTests` (`app/tests/DnaEntropyGraph.Guards.Tests/`), a regex-based scan (matching `ReswScanner`/`XamlInlineStringScanner`'s own style rather than a full Roslyn parse) for a literal argument passed to `IDialogService.ConfirmAsync` or `IToastService.ShowToast` - the C# half of Hard Rule 13 that neither `ReswScanner` (only reads `.resw`) nor `XamlInlineStringScanner` (only reads `.xaml`) could ever see.
- **This guard is not decorative: its first real run found three genuine violations already in the tree** - `CloudResourcesViewModel.ConfirmDeleteAsync`, `ResultsViewModel.LoadAsync`, and `SettingsViewModel.SetTheme` all had literal titles/bodies. Fixed by adding `IStringResourceProvider` (see the #62 fragment) and moving the six literals into `Resources.resw` (`ConfirmDeleteResource.*`, `NoRunsYet.*`, `ThemeUpdated.Title`). Watched red (the real violations), then green.
- **Mutation-checked**: reintroduced a literal into `SettingsViewModel.SetTheme`, watched `DialogStringLiteralGuardTests` fail naming the exact file/line/value, restored the fix.
- **Found and fixed a second guard bug while exercising the new markup this session added**: `XamlInlineStringScanner`'s attribute regex had no word boundary before the attribute name, so `NavigationView.AlwaysShowHeader="True"` was flagged as an inline `"Header"` violation - a false positive on legitimate markup, not a Hard Rule 13 violation. Fixed with a `\b` in the regex; added `An_attribute_whose_name_merely_ends_in_a_watched_word_is_not_flagged` as a permanent regression test; mutation-checked (reverted the `\b`, watched the real MainWindow.xaml false-positive reappear, restored).
- **Finding, not new work**: issue #71's other "Done when" line - "ci-docs scans user_guide, README, issue templates" - was already implemented before this session, in `.github/workflows/ci-docs.yml`'s "No em dashes in user-facing copy" step (`git grep -nI $'—' -- 'docs/user_guide/**' '*.resw' '.github/ISSUE_TEMPLATE/**' 'README.md'`). No code changed for that line; it is recorded here so nobody rebuilds it.
- **THEORY (unverified), filed as a suggestion, not fixed**: that em-dash `git grep` step has no dedicated unit/self-test the way `scripts/check_user_home_paths.py --self-test` does for its own guard - nobody has proven it can fail on a planted em dash the way every guard in this fragment was proven to. Worth a small `scripts/check_*.py` port of the same one-liner with a `--self-test` arm, so it stops being the one em-dash guard in this repo that has never been watched go red.

- Added `csharp` to `.github/workflows/codeql.yml`'s CodeQL matrix (issue #197): `app/` and its solution now exist (issue #61), so the placeholder "added by issue #61, when app/ exists" is resolved. Added an explicit `dotnet restore`/`build -c Release -p:Platform=x64` step for the C# language, mirroring `ci-app.yml`'s already-proven build command, rather than relying on CodeQL's `autobuild` heuristic against an unpackaged, central-package-managed WinUI 3 solution.
- Audited the rest of #197's scope: `.github/dependabot.yml` already covers `github-actions`, `pip` (`worker/`), and `nuget` (`app/`) on a weekly schedule (the `docker` ecosystem is deliberately deferred until issue #36's Dockerfiles - which now exist - get their own dependency-update cadence, a separate not-yet-filed follow-up); `.github/workflows/codeql.yml`'s `dependency-review` job already has `deny-licenses`/`fail-on-severity` configured.
- Confirmed, per this lane's own check rather than assumed: the `dependency-review` job's `if: github.event_name == 'pull_request'` condition is currently never true (every workflow in this repo is `workflow_dispatch`-only, the owner's on-demand-CI decision - there is no `pull_request` trigger at all right now), and even a restored trigger would fail immediately because the repository's Dependency graph is off (issue #325, an owner-only action, already filed and already correctly documented in the workflow's own comment). Not duplicated.
- Dependabot's version-update PRs are unaffected by the on-demand-CI decision (a separate GitHub-hosted scheduled service, not a workflow this repo's `workflow_dispatch`-only setting touches) and already opened #305 before this session.
- This session's own edit to `codeql.yml` is uncommitted and therefore cannot be exercised by a real `gh workflow run codeql.yml` (GitHub Actions only sees the committed version on the target ref) - see this lane's report for the exact ToTest row needed once merged.

- Added `scripts/gen_third_party_notices.py` (issue #34): generates `THIRD-PARTY-NOTICES.md` from the real dependency tree - .NET packages (direct + transitive) via `dotnet list package --include-transitive` and the local NuGet cache's own `.nuspec` files, no network call; Python packages via `worker/pyproject.toml`'s declared dependencies/extras and `worker/.venv`'s installed metadata (PEP 639 `license_expression`), also no network call.
- Classifies every shipped dependency as allowed (MIT/Apache-2.0/BSD-family), denied (GPL/LGPL/AGPL with no recorded carve-out - fails the check), denied-with-carve-out (`pyrodigal`, GPL-3.0, per issue #301's owner recommendation), or unrecognized (never silently allowed). Dev-only dependencies (`worker`'s `[dev]` extra, per issue #402's recorded decision) and Microsoft's own Windows App SDK/WebView2/Windows SDK platform redistributables (proprietary licence terms, not a chosen dependency) are excluded from the gate as their own separate scopes.
- Rewrote `scripts/check_third_party_notices.py` from its documented no-op (it passed because `THIRD-PARTY-NOTICES.md` did not exist) into a real staleness gate: regenerates via the generator's `--stdout` mode and fails if the committed file differs, and separately fails if the generator itself reports an unresolved licence problem even when the file is otherwise byte-identical.
- Generated the real `THIRD-PARTY-NOTICES.md` against this repo's actual dependency tree. One genuine open finding it surfaces: `evo2`'s licence is cited from `docs/tech_stack.md`'s own MEASURED entry (Apache-2.0, checked against `github.com/ArcInstitute/evo2`'s LICENSE file) rather than independently re-verified here (GPU-only, never installed on the laptop, no network access from this generator).
- Rewrote `scripts/tests/test_check_third_party_notices.py` (the old tests asserted the pre-#34 "the file does not exist yet" state, which is now false) and added `scripts/tests/test_gen_third_party_notices.py`. Both include a test against the real repo tree, not just synthetic fixtures.
- Updated `docs/packaging_design.md` with a new section 8 documenting the generator/checker and CI scanning state; corrected its own stale "app/, worker/vm/, and worker/Dockerfile.* do not exist yet" header (all three now exist).
- Filed #413 (P3, DECISION, agent-made/reversible): whether Hard Rule 21 reaches the Windows App SDK/WebView2/Windows SDK platform redistributables' proprietary licence, or only a chosen dependency.

- Wired #123 end to end: surprisal was groundwork-only (a module, tests, and a
  knowingly-unwired `SurprisalSummary`), closed anyway; the wiring the closed issue's own
  title promised was never actually done. `analysis/direction.py::analyze_direction` now
  computes surprisal from the SAME `fwd.probs`/`rev.probs` entropy already used (zero
  extra predictor calls) and combines it by the identical forward/reverse selection rule
  as entropy, exposed as `DirectionResult.surprisal_values`.
- Added `RunConfig.include_surprisal` (`worker/src/dna_entropy/config.py`, default `True`)
  and `--surprisal/--no-surprisal` on the CLI's `run` command, matching every other
  writer-suppression flag's shape (issue #304's `include_*` pattern).
- `writers/bedgraph.py`, `writers/wig.py`, and `writers/geneious.py` grew a `metric=`
  parameter (default `"entropy"`, byte-identical output for every existing caller) so
  surprisal reuses them wholesale instead of a parallel writer class: `<name>.surprisal.bedgraph`
  / `.wig` / `.geneious.gff3`.
- `writers/tsv.py`'s plain (non-`Both, separate tracks`) shape grows an optional 4th
  `surprisal_bits` column (`surprisal_blocks=`); `writers/summary.py`'s `write`/`write_multi`
  grew an optional `surprisal=` parameter reporting `SurprisalSummary`'s mean and total
  log-likelihood alongside the entropy stats. Both default to the pre-#123 shape when
  omitted.
- `pipeline.py` wires all of the above behind `cfg.include_surprisal`, for both the
  standard (FASTA/paste) and GenBank output paths.
- Deliberately scoped out (documented in `docs/science_and_formats.md` section 2b, not a
  gap left silent): the TSV's `Both, separate tracks` 5-column shape does not yet grow
  forward/reverse surprisal columns. An early draft added
  `DirectionResult.forward_surprisal`/`.reverse_surprisal` for that future writer;
  `scripts/check_unused_fields.py` immediately flagged both as genuinely unread (no writer
  consumed them yet), so they were removed rather than left "for later" — the guard
  catching this repo's own named bug class within the session that was fixing an instance
  of it.
- `scripts/unused_fields_allowlist.json`'s `SurprisalSummary.*` entry is now stale (it
  said "DELETE THIS ENTRY WHEN #123 LANDS") and must be removed by whoever owns that file;
  `check_unused_fields.py` fails on the stale entry until then — this is the mechanical
  proof the wiring landed, not a leftover bug.
- Tests: `worker/tests/test_direction.py` (6 new: forward-only/reverse-only surprisal,
  combined-follows-the-same-seam, averaged, both-separate, and the "only a hand-built
  DirectionResult can have `surprisal_values=None`" case), `worker/tests/test_writers.py`
  (6 new: `metric="surprisal"` for bedgraph/wig/geneious, plus summary surprisal
  reporting), `worker/tests/test_tsv.py` (3 new), `worker/tests/test_pipeline.py` (7 new,
  including the issue's own named observable and the CLI flag's actual effect on disk),
  `worker/tests/test_cli.py` (2 new, matching the existing `--tsv`/`--no-tsv` regression-guard
  shape). Mutation-checked: reverted the CLI wiring line and the surprisal `_combine` call
  by hand, watched the matching tests go red, restored byte-exactly.

- Added `writers/provenance.py` for #82: `provenance.json`, written unconditionally
  (never gated by an `include_*` flag) into `cfg.out_dir` at the end of every
  `pipeline.run()`, including a partial/cancelled run's best-effort partial write
  (`docs/job_contract.md` §6: "partial results are always kept, never discarded"). Records
  worker version, predictor kind/model/device/seed, run direction/ambiguity
  policy/RNA flag, per-contig windowing/direction (`context_length`, `window`, `stride`,
  `k_used`, `ceiling`, `direction`, `seam`, `reduced_context_count`), and wall time.
- `pipeline.run()` grew a `provenance_extra` keyword: the worker-layer-only fields this
  module cannot know on its own (GPU name/driver, torch/evo2/flash-attn versions,
  container image digest, input sha256 — none of which this package can compute without
  either importing `torch` outside `evo.py`, forbidden by Hard Rule 1, or seeing the raw
  input bytes the worker layer downloads before `pipeline.run()` is ever called) default
  to `None` and are overlaid additively when a caller supplies them. `worker/runner.py`
  (out of this lane) is the natural caller for that overlay; see this session's report for
  the exact call shape.
- `k_used` is deliberately `window - stride`, not the nominal `context_length`:
  `DirectionResult.context_length` is not updated after an OOM-halving retry (issue #313)
  shrinks K, so it can disagree with the K a halved pass actually ran with. Filed as #407
  (THEORY, then reproduced by hand and fixed the same session): the theory that
  `context_length` "going stale" was itself the bug was disproven — reporting the nominal
  K is legitimate — but reproducing it surfaced the REAL bug one layer down:
  `analysis/direction.py::_combine` used that same stale, shared value as the
  qualification threshold for BOTH directions, so a one-sided halving could permanently
  lock the halved direction out of ever "qualifying" again, silently collapsing
  combined/averaged mode to the other direction with no error. Fixed in `_combine` itself
  (see `fix-407-combine-per-direction-context.md`); `k_used`'s derivation here was already
  correct and needed no change.
- This also closes the remaining "provenance.json records the final W, S" half of #81's
  Done-when (its OOM catch/halve/retry half was already implemented); #81 itself is not
  closed here since closing an issue is the orchestrator's call, not this lane's.
- `docs/science_and_formats.md` was not touched for this specific issue (it already had
  no provenance.json section to correct); `docs/job_contract.md`'s `output/provenance.json`
  layout line (section 2) already named this file before this work started.
- Tests: `worker/tests/test_provenance.py` (10 new: shape, the issue's own named
  Observable — two builds of the same config differ only in `generated_at`/
  `wall_time_seconds` — the OOM-halving `k_used` derivation, worker-layer-field
  defaults/overlay, and `ProvenanceWriter`'s JSON/UTF-8/LF output), plus 5 new tests in
  `worker/tests/test_pipeline.py` (always written, records seam/reduced-context, the
  two-runs reproducibility Observable end to end, and a partial/cancelled run still gets
  one). Mutation-checked: removed the `_write_provenance` call and the `k_used` derivation
  by hand in turn, watched the matching tests go red, restored byte-exactly.

- Corrected #345: an earlier pass this session concluded no cross-check was needed for
  `analysis.stride`, reasoning that `S = W - K` is a pure function of `context_length` and
  `ceiling` with no independent degree of freedom. That reasoning was right about the
  WORKER's own windowing math (`analysis/windowing.py::compute_window`/`plan_windows` have
  no parameter to accept an externally supplied stride) but wrong about the manifest
  contract: `worker/manifest.py::AnalysisSpec` parses its own `stride` field independently
  of `window`/`contextLength`, and `build_run_config` reads `analysis.window` (as
  `RunConfig.max_len`) but never `analysis.stride` — a manifest declaring a `stride` that
  disagreed with `window - contextLength` was silently ignored rather than refused.
- Fixed: `AnalysisSpec.from_dict` now cross-checks `stride == window - contextLength` at
  parse time and raises `ManifestError` naming both numbers on a mismatch — the same
  validate-and-refuse pattern already used for `predictor.precision` (issue #343),
  `analysis.direction`, `analysis.format`, and `inputs[].ambiguityPolicy`/`fastaRecords` in
  the same file.
- `docs/job_contract.md`'s `analysis.contextLength`/`window`/`stride` row is corrected to
  state plainly that `window` is consumed and `stride` was not, rather than repeating the
  earlier "settled, no gap" claim.
- Kept `worker/tests/test_windowing.py`'s two invariant-locking tests (they document a
  true, narrower claim about the windowing module itself, not the manifest contract) but
  corrected the surrounding comment block, which had drawn the wrong conclusion from a
  true premise.
- Tests: `worker/tests/test_worker_manifest.py` (3 new: a matching stride parses, a
  disagreeing stride is rejected naming both numbers, and the defaults are internally
  consistent so a manifest omitting `analysis` entirely still parses); one pre-existing
  fixture (`test_unknown_nested_fields_are_tolerated`) needed its `analysis.window`/
  `stride` values updated to stay consistent with its `contextLength` override, since it
  had been incidentally relying on nothing checking that before now. Mutation-checked:
  disabled the cross-check condition by hand, watched the new rejection test go red,
  restored byte-exactly.

- Fixed #403: `readers/genbank.py`'s `GenBankReadError` message had an empty gap
  ("Could not parse the GenBank file: . Check...") whenever Biopython's scanner raised a
  bare, message-less `AssertionError` (issue #368's own regression fixture,
  `genbank_qualifier_missing_slash.gb`, is exactly this case). New helper
  `_describe_bare_assertion` recovers a true, non-empty reason from the exception's own
  traceback: for the one shape actually reachable through malformed GenBank content (a
  feature qualifier continuation line missing its leading `/`, `Scanner.py`'s
  `assert len(qualifiers) > 0` / `assert key == qualifiers[-1][0]`), it names the real,
  checkable cause; for any other bare assertion, it falls back to naming the internal
  check that failed (still concrete, never silent). A `ValueError` (which already carries
  real text from Biopython) is unaffected.
- Mutation-checked: temporarily skipping `_describe_bare_assertion` reproduces the empty
  "Could not parse the GenBank file: . " gap on `genbank_qualifier_missing_slash.gb`;
  restoring the fix turns it back into a named reason.

- Fixed #407 the same session it was filed: reproduced the theorized
  `DirectionResult.context_length` staleness by hand with an OOM-halving stub run all the
  way through `analyze_direction()` (not just `run_windowed()`), and found the theory was
  half right. Disproven part: `context_length` reporting the nominal, configured K after a
  halving retry is legitimate, not a bug. Real bug found one layer down:
  `analysis/direction.py::_combine` used that same stale, shared `context_length` as the
  qualification threshold (`fwd_context >= context_length`) for BOTH directions, even
  though an OOM-halving retry (issue #313) can shrink ONE direction's actually-achieved K
  independently (each `run_windowed` call only reacts to its OWN OOM). A halved direction's
  context is then capped below the un-halved, stale threshold, so it can never "qualify"
  again for the rest of the sequence — combined/averaged mode silently collapses to the
  other direction alone, with no error or notice.
- Fixed: `_combine` now takes `fwd_context_length`/`rev_context_length` separately,
  computed in `analyze_direction()` as each direction's own `window - stride` (always
  accurate regardless of a halving, same derivation `writers/provenance.py::contig_provenance`
  already used for `k_used`). Backward compatible: in the non-halved case,
  `window - stride == context_length` exactly, so every existing test was unaffected.
- Tests: `worker/tests/test_direction.py::test_both_combined_after_a_forward_only_oom_halving_lets_forward_requalify_at_its_own_k`
  reproduces the exact scenario (a shared predictor OOMs on its first call, landing during
  the forward pass) and asserts the combined track matches forward-only at a position that
  qualifies under forward's own post-halving K but not under the stale nominal K.
  Mutation-checked: reverted the per-direction thresholds to the shared `context_length`
  by hand, watched the test go red with the exact numbers hand-derived before the fix
  (1.0925992 vs 1.6316695), restored byte-exactly.

- Added `IComputeGateway.FindByJobIdAsync` (`app/src/DnaEntropyGraph.Core/Cloud/IComputeGateway.cs`): every VM labelled with a job id, across every zone - Hard Rule 9's "discovery is by label, never by a fixed name" - and the reconciler `CloudJobRunner` (issue #58) calls before ever attempting a zone.
- Reworked `FakeGcp`'s internal VM storage from a name-keyed dictionary to a `(Name, Zone)`-keyed one, matching what Compute Engine actually enforces (a name is unique only within a zone). A repeated `CreateVmAsync` for the exact same `(spec, zone)` now returns the ORIGINAL successful result instead of silently overwriting it or re-running project-wide checks - simulating `requestId == JobId` idempotent replay (issue #257's own Done-when). `WithAlreadyExists`, `WithPreemption` and `Stop`/`Delete`/`Get` were updated to the same `(Name, Zone)` key; `WithPreemption` now takes an explicit `zone` parameter.
- MEASURED (2026-09-19): reproduced issue #389's THEORY directly against `FakeGcp` - two `CreateVmAsync` calls for the same job in two different zones both succeed, and `FindByJobIdAsync` reports two VMs for one job id. Updated `docs/cloud_design.md` section 3 from `THEORY (unverified)` to `MEASURED`, with the fix recorded as `CloudJobRunner.ProvisionAsync`'s reconcile-first ordering (issue #58), not a gateway-level restriction.
- Added `app/tests/DnaEntropyGraph.Cloud.Tests/FakeGcpRetrySafetyTests.cs`: the issue's own observable ("replaying the insert of a job with FakeGcp yields exactly one instance"), the idempotent replay not re-running project-wide checks, the #389 reproduction, `WithAlreadyExists`'s every-time-throw behaviour, and `GetProjectStateAsync`/`WithProjectState` (issue #388, closed alongside this work - see the #58 fragment).

- Added `DnaEntropyGraph.Core.Cloud.JobStateMachine` (`app/src/DnaEntropyGraph.Core/Cloud/JobStateMachine.cs`): the legal-transition table over the existing `JobPhase` enum (#61 skeleton), plus `HasAlreadyPassed`/`IsTerminal` helpers `CloudJobRunner` uses to make a resumed run's already-completed steps no-ops instead of illegal backward transitions.
- Added `DnaEntropyGraph.Core.Cloud.CloudJobRunner` (`app/src/DnaEntropyGraph.Core/Cloud/CloudJobRunner.cs`), `CloudJobRequest` and `CloudJobResult`: preflight (project state, billing, Compute API, GPU quota) -> Validating -> Uploading -> Provisioning (reconcile-then-zone-walk, issue #257) -> Preparing -> Running (a second VM-status poll to catch a preemption discovered mid-run) -> Finalizing (Hard Rule 11: stop/delete, then independently re-verify the terminal state) -> Downloading -> Completed/PartiallyCompleted/Failed. Every phase change is committed to `IRunRepository` before any `onPhaseChanged` callback fires. No separate "resume" method: `RunAsync` is safe to call again for the same job id from a brand-new instance.
- `CloudJobRunner.ProvisionAsync` is `CloudErrorClassifier`'s (#57) first production caller: it applies docs/cloud_design.md section 5's abort-vs-continue rule for real over a zone list, using `OperationPoller` (#256) to turn a thrown `CloudOperationException` into a classified outcome.
- Closed #388: added `IProjectSetupGateway.GetProjectStateAsync`/`ProjectLifecycleState` (preflight step 1) and `FakeGcp.WithProjectState` scripting.
- Added `app/tests/DnaEntropyGraph.Cloud.Tests/JobStateMachineTests.cs` and `CloudJobRunnerTests.cs`: the happy path, the stockout ladder, a project-wide abort, both preflight failures, cancel, a discovered preemption, and a crash-and-resume that proves no duplicate VM is created (issue #257/#389).
- Fixed a stale `DEAD-REGISTRATION` allowlist entry in `scripts/app_wiring_allowlist.json`: `IComputeGateway`/`IStorageGateway`/`IProjectSetupGateway`/`IQuotaGateway` are now genuinely consumed by `CloudJobRunner`'s constructor; deleted their entries per the guard's own "delete the moment its page lands" rule.
- Filed #414 (VmSpec has no accelerator-type field, so the quota preflight check uses a placeholder string) and #415 (resume re-uploads/re-downloads unconditionally).

- Added `DnaEntropyGraph.Core.Cloud.OperationPoller`/`OperationPoll<T>`/`OperationOutcome<T>` (`app/src/DnaEntropyGraph.Core/Cloud/OperationPoller.cs`): polls-with-backoff (1s doubling to a 10s cap) against a caller-given deadline, distinguishing its own `OPERATION_POLL_TIMEOUT` (classifies as `network`) from a real error the polled operation itself reported. No real time is awaited unless the caller's own injectable delay function does so.
- Added `app/tests/DnaEntropyGraph.Cloud.Tests/OperationPollerTests.cs`: an immediate success, an immediate real error (proving it never reads as a generic timeout - the issue's own observable), the exact backoff sequence with its 10s cap, deadline clipping so the total never overshoots, and cancellation.
- Precheck note: issue #256 was marked `SUSPECT` (one commit naming it touched 18 source files) but that commit's diff is entirely under `worker/`, naming seven other issues alongside #256 - read and confirmed unrelated; no C# work existed before this fragment.
- `CloudJobRunner` (issue #58) is the first consumer, wrapping every `CreateVmAsync` attempt as a single-shot poll (`FakeGcp` resolves synchronously today; a real gateway's multi-poll LRO needs no runner-side change).

- Replaced `DnaEntropyGraph.Persistence`'s in-memory placeholders (`RunRepository`, `SettingsStore`) with real, disk-backed implementations (issue #67).
- Added `SqliteDatabase` (`app/src/DnaEntropyGraph.Persistence/SqliteDatabase.cs`): opens `app.db` with WAL, `foreign_keys=ON` and a 5000ms busy timeout, and applies numbered SQL migrations forward under `PRAGMA user_version`, starting from 0 (a brand-new file) or from wherever an existing file's `user_version` already sits.
- Added `Migrations/0001_initial.sql`, copied verbatim from `docs/superpowers/specs/2026-09-18-appendix-a-app-design.md` section 3's DDL: `Accounts`, `Projects`, `Runs`, `RunInputs`, `RunOutputs`, `RunEvents`, `CloudResources`, `CostLedger`, `MonthlySpend`, `LocalEngine`.
- `RunRepository` is now SQLite-backed against the real `Runs` table. `RunRecord` (`app/src/DnaEntropyGraph.Core/Abstractions/IRunRepository.cs`) grew from 3 required fields to every `Runs` column, all new ones optional with defaults, so every pre-existing 3-argument call site kept compiling unchanged.
- Added `CloudResourceRepository`/`ICloudResourceRepository`, `ProjectRepository`/`IProjectRepository`, `CostLedgerRepository`/`ICostLedgerRepository` (`app/src/DnaEntropyGraph.Persistence/`), each backed by their own DDL table. These interfaces live in `DnaEntropyGraph.Persistence` rather than `Core.Abstractions` for now, since no ViewModel consumes them yet and registering them in DI tonight would itself be a wired-to-nothing service; promote them to `Core.Abstractions` unchanged the moment a Cloud Resources / cost ledger ViewModel needs one.
- `SettingsStore` is now backed by `settings.json` with an atomic (temp-file + `File.Move` overwrite) write, matching the worker side's own `LocalBlobstore` temp+rename convention. `ISettingsStore`'s `GetString`/`SetString` shape is unchanged (see the DECISION note in `SettingsStore.cs`).
- DECISION (agent-made, reversible): a corrupt or unreadable `app.db` / `settings.json` (a lab PC force-restarted mid-write) is quarantined (renamed aside, never deleted outright) and replaced with a fresh, empty, migrated file rather than throwing and taking the whole app down on launch. Nothing in `docs/` had settled this before tonight; filed as issue (see this lane's report for the number) rather than silently assumed.
- Added `app/tests/DnaEntropyGraph.Persistence.Tests/{SqliteDatabaseTests,RunRepositoryTests,SettingsStoreTests,CloudResourceRepositoryTests,ProjectRepositoryTests,CostLedgerRepositoryTests}.cs` (26 tests): migrations forward from an empty file and from an already-migrated populated file, corrupt-file recovery for both the database and settings.json, cross-instance persistence (the "kill the app after pressing Run, relaunch, see the row" observable from issue #67's own acceptance criteria), and column round-trips for every repository.
- Pinned `Microsoft.Data.Sqlite` 10.0.12 and `Dapper` 2.1.86 via `VersionOverride` in `DnaEntropyGraph.Persistence.csproj` (not the shared `Directory.Packages.props`, to avoid colliding with five concurrent lanes tonight). `Microsoft.Data.Sqlite` 9.0.9's transitive `SQLitePCLRaw.lib.e_sqlite3` 2.1.10 trips NU1903 (GHSA-2m69-gcr7-jv3q); 10.0.12 does not (`dotnet list package --vulnerable --include-transitive` reports none).

- Issue #367 (parent #160, windowing and direction scope): added the real, Hypothesis-
  generated property tests `#160` itself filed as follow-up work once `hypothesis` could
  be installed. New file `worker/tests/test_property_windowing_direction.py`, generating
  rather than table-driving: window coverage (every position covered by exactly one
  winning window) and the closed-form pass-count formula over the whole valid `(L, K,
  ceiling)` space (`k` 1..5000, `length` 1..50000); the Hard Rule 3 `(L, 4)` contract
  (shape, dtype, row-sum, entropy bound) for generated A/C/G/T sequences and seeds;
  entropy invariance under the reverse-complement column permutation for generated
  probability distributions; and the seam recorded in provenance matching where the
  combiner actually switched, checked against an independently computed `BOTH_SEPARATE`
  run for generated `(K, L)` pairs. The existing table-driven tests in
  `test_windowing.py`/`test_direction.py`/`test_entropy.py` (landed under #160) are
  unchanged and stay as real regression coverage, not superseded.
- `hypothesis` added to `worker/pyproject.toml`'s `dev` extra. MEASURED 2026-09-19: it is
  MPL-2.0, not MIT as #368's own issue body assumed without checking (confirmed against
  the installed package's own `License-Expression: MPL-2.0` metadata) -- Hard Rule 21's
  text scopes the MIT/Apache/BSD requirement to "anything that ships", and this extra is
  explicitly dev/CI-only (never installed into the container image), but
  `docs/hard_rules.md`'s own carve-out section records no dev-only-dependency carve-out
  yet, so a `DECISION (agent-made, reversible):` issue was filed recording the call rather
  than adding it silently. `sortedcontainers` (MIT), hypothesis's own dependency, was
  pulled in transitively.
- Every property has an explicit `@settings` budget (`max_examples=200` for pure
  Python/NumPy properties, `40` for properties driving `MockPredictor.predict`), never the
  Hypothesis default, and `deadline=None` (a shared box running six concurrent agents
  makes wall-clock-based flakiness a false property failure). Every property was mutation-
  checked by patching the real function it exercises in memory (never editing
  `analysis/windowing.py` or `analysis/direction.py`, which are Lane E's) and watching the
  test go red on a planted bug, then restoring: a dropped `+1` in the window pass-count
  formula, a column-asymmetric `shannon_entropy`, and an off-by-one `>`/`>=` threshold in
  the forward/reverse combiner were each caught.
- `docs/tests.md` updated to name the new property test file and its budget.

- Issue #368 (parent #162): added the property-based (Hypothesis) half of the GenBank/
  FASTA reader fuzzing `test_fuzz_readers.py`'s own docstring named as missing. New file
  `worker/tests/test_property_fuzz_readers.py`, generating over two strategies named in
  #368's own body: arbitrary bytes (`st.binary()`), and mutated real files (a
  `mutated_bytes()` `@st.composite` strategy applying random byte flips, deletions,
  insertions, truncations and encoding swaps to `worker/tests/data/sample.fasta`/
  `sample.gb`). Property under test matches `test_fuzz_readers.py`'s own bar exactly: every
  generated input either parses to a genuine, non-empty record set or raises one of the
  three clean, registered exception types (`FastaReadError`/`GenBankReadError`/
  `ValidationError`), never any other exception, and a clean success is never a silently
  empty record list or a record with a `None`/empty sequence.
- **Real bug found and fixed** (MEASURED 2026-09-19, via the mutated-file property on
  `sample.gb`): a feature-qualifier continuation line missing its leading `/` (e.g.
  `gene="geneB"` instead of `/gene="geneB"`) drives `Bio.GenBank.Scanner`'s internal
  feature-table parser into one of its own bare `assert` statements
  (`assert len(qualifiers) > 0` / `assert key == qualifiers[-1][0]`), which raises a raw
  `AssertionError`, not a `ValueError`. `readers/genbank.py`'s `except ValueError` clause
  did not catch it, so `read_genbank` leaked a raw, unnamed exception instead of a clean
  `GenBankReadError` -- exactly the bug class #162/#368 exist to prevent. Fixed by widening
  the except clause to `(ValueError, AssertionError)`. This DISPROVES issue #349's own
  landed claim ("every malformed-content failure this scanner raises for GenBank/EMBL is
  ... observed to be a ValueError") -- Hard Rule 18: the disproven claim was replaced in
  place in `readers/genbank.py`'s own comment, not left beside a correction. Permanent
  regression fixture added: `worker/tests/data/malformed/genbank_qualifier_missing_slash.gb`
  (auto-collected by `test_fuzz_readers.py`'s existing `GENBANK_FIXTURES` glob, no test
  code change needed). Mutation/revert-checked: reverting the except clause to
  `ValueError`-only reproduces the raw `AssertionError` on this exact fixture; restoring the
  widened clause turns it back into a clean `GenBankReadError`.
- `hypothesis` licence and dependency-addition details: see
  `docs/changelog.d/test-367-property-windowing-direction.md`.
- `.gitattributes`' `worker/tests/data/malformed/** -text` rule already covers the new
  fixture (checked, not re-created); the new fixture is plain ASCII with LF line endings,
  so it carries no CRLF/byte-integrity risk itself, but it lives under the same glob the
  rule already protects.
- `docs/tests.md` updated to name the new property test file, its two generation
  strategies, and its budget.

- Added `DnaEntropyGraph.Core.Inputs.InputResolver`/`InputResolution`/`InputResolutionKind` (`app/src/DnaEntropyGraph.Core/Inputs/InputResolver.cs`, issue #211): pure decision logic distinguishing a dropped file, a typed/pasted path, and a pasted DNA sequence, ported from the prototype's double-click wizard (`DNA-Entropy-Genbank/packaging/launcher.py::_resolve_input`, read directly from the sibling prototype checkout since the file was deleted from this repo's worker under issue #291).
- Quote-stripping (double then single quotes, matching Explorer's "Copy as path"), the "looks like a path but does not exist" vs "pasted sequence" distinction, and the empty-input case all mirror the prototype exactly.
- Returns a plain result record for the ViewModel to bind to - no exceptions, no console I/O, no hardcoded user-visible strings (Hard Rule 13: those belong in `Resources.resw`, keyed by `InputResolutionKind`).
- Added `app/tests/DnaEntropyGraph.Core.Tests/Inputs/InputResolverTests.cs` covering every branch, including that the drop zone and the paste box resolve the same existing path identically (issue #211's own "Why").
- Not yet wired into the New Run page/paste dialog/drop zone - see this lane's report, "Needed outside my lane", for the exact method the ViewModel should call.

- Expanded `RunOptions` (`app/src/DnaEntropyGraph.Core/RunOptions.cs`, issue #65) from its 3-property placeholder to the full option set in spec section 4.3, cross-checked against `worker/src/dna_entropy/worker/manifest.py` and `docs/contract/manifest.json` so an option the worker cannot honour never reaches the UI.
- Every new property has a default; the pre-existing `ModelId`, `RunTarget`, and `OutputFolder` are unchanged in name, type, and requiredness, so every existing construction site (`NewRunViewModel.StartRunAsync`, `JobEngine`) keeps compiling untouched.
- Added enums `InputFormat`, `FastaRecordsSelection`, `GpuTier`, `PredictorSelection`, `Direction`, `OutputFileKinds` (`[Flags]`), `TrackFormat`, `AfterTaskAction`, `AfterKeepAliveAction`, each doc-commented with its manifest wire name.
- Reused `DnaEntropyGraph.Core.Inputs.AmbiguityPolicy` (added for issue #64) rather than a second copy, so a `RunOptions.AmbiguityPolicy` value and a `SequenceValidator.Validate` call use the same type.
- The spec's "Budget (Settings)" row is deliberately excluded: those are app-wide settings (`ISettingsStore`/`settings.json`), not a per-run choice.
- `Window`/`Stride` are recorded (matching the worker's own dataclass defaults) but documented as derived, not user-chosen; `GpuPlanner`/`ModelGpuLinker` (separate work) compute the real values before a run starts.
- Filed #394 (P3, DECISION, agent-made/reversible): the Local/notifications group implements Appendix A section 2.3's finer 5-item breakdown rather than spec 4.3's plainer 3-item row, since the two disagree on shape and the richer set is a strict superset.
- Added `RunOptionsTests.Every_spec_4_3_option_has_its_documented_default` and `...still_compile_and_get_every_new_default` to `app/tests/DnaEntropyGraph.Core.Tests/JobPhaseTests.cs` - the decisive snapshot-style test issue #65 asked for.

- Added `app/src/DnaEntropyGraph.Core/Inputs/` (issue #64): a C# port of the worker's local-preflight rules (Hard Rule 2 - the app validates locally, with the same rules, before it creates a single cloud resource).
- `TextDecoder` (`TextDecoder.cs`) ports `readers/encoding.py`'s BOM/encoding detection (UTF-8 BOM, UTF-16 LE/BE BOM, replacement-character fallback) byte-for-byte.
- `SequenceValidator` (`SequenceValidator.cs`) ports `validation/validators.py::validate_sequence` line-for-line: header stripping, whitespace/digit normalization, RNA detection, ACGT/IUPAC-ambiguity checks with `keep`/`mask`/`error` policies, the replacement-character diagnosis, and the length/short-input rules. Every rejection carries the `INPUT_INVALID` code from `docs/contract/error-codes.json`.
- `SequenceSniffer` (`SequenceSniffer.cs`) ports `readers/detect.py::detect_kind` (extension first, then a content sniff of the first non-blank line).
- `FastaLite` (`FastaLite.cs`) ports `readers/fasta.py::read_fasta`, including issue #350/#351's measured bug: two records whose header keys to the same id (up to its first whitespace) are both kept and flagged, never silently overwritten.
- `GenBankLite` (`GenBankLite.cs`) is a deliberately partial local read (LOCUS/ORIGIN/gene-CDS count only, per the design's own scope note for this class) - not a full parse of `readers/genbank.py`'s feature table.
- Added `app/tests/DnaEntropyGraph.Core.Tests/Inputs/SequenceValidatorParityTests.cs`: a data-driven test that replays every case in `tests/contract-fixtures/cli_validate_parity.json` (the real worker CLI's own golden vector) through `SequenceValidator` and asserts byte-identical output - the decisive parity test issue #64 asked for.
- Added neighbour tests for `TextDecoder`, `SequenceSniffer`, `FastaLite`, `GenBankLite`, and additional `SequenceValidator` cases the fixture does not exercise (ambiguity codes, invalid characters, empty input, over-length input).
- None of `Core/Inputs/` is called from a production entry point yet - the New Run page/ViewModel wiring is separate work (see this lane's report, "Needed outside my lane").
- Filed #391 (P2, area:worker): `FastaReadError`/`GenBankReadError` are misclassified as `WORKER_CRASH` instead of `INPUT_INVALID` by `runner.py`'s exception classifier.
- Filed #392 (P2, area:worker): `dna-entropy validate` always refuses ambiguity codes (its own strict function default), diverging from a real run's `keep` default, with no `--ambiguity` flag to override it.
- Filed #393 (P3, area:docs): no shared contract fixture exists for `readers.fasta`/`readers.genbank` notice-and-error parity, the way `cli_validate_parity.json` exists for `validate_sequence`.

- Added `DnaEntropyGraph.Core.Cloud.CloudError`/`CloudErrorKind`/`CloudOperationException` (`app/src/DnaEntropyGraph.Core/Cloud/CloudError.cs`): the Google-free structured-error shape (Hard Rule 7) both `FakeGcp` and issue #57's classifier share, so a scripted fake failure and a real classified failure are provably the same contract.
- Rewrote `FakeGcp` (`app/src/DnaEntropyGraph.Cloud/FakeGcp.cs`) from an always-succeeds baseline into a scriptable fake: `WithBillingOff`, `WithComputeApiOff` (cleared by `EnableComputeApiAsync`, matching the real API's idempotent enable), `WithPermissionDenied`, `WithOrgPolicyBlocked` (project-wide, abort); `WithZoneStockout`/`WithQuotaExceededOnCreate` (per-zone/per-region, scriptable for a fixed number of attempts or forever); `WithAlreadyExists` (persistent, adoptable via `GetVmAsync`); `WithNetworkFailures` (transient, clears after N); `WithGpuQuota`/`WithAllRegionsGpuCap` (the `GPUS_ALL_REGIONS` override); `WithPreemption` (needs the new `FakeGcp(TimeProvider)` constructor for deterministic tests); `WithSignedOut`/`WithSelectedProject`. Every failure throws `CloudOperationException` with the exact `CloudErrorKind` a real gateway mapping would produce. `FakeGcp` keeps its parameterless constructor for DI.
- Added `VmDescriptor.StatusReason` (optional, defaults null) so a preempted VM can be told apart from a user-stopped one.
- Added `app/tests/DnaEntropyGraph.Cloud.Tests/FakeGcpScriptedFailureTests.cs`: one test per scripted failure shape, several round-tripping the thrown `CloudError` back through `CloudErrorClassifier.Classify` to prove the fake and the classifier agree; `ManualTimeProvider` added for the preemption tests.
- Filed #390 (P3): FakeGcp fidelity gaps this wave declined - LRO delay on API enable, bucket generations, a structured 404 on stop/delete of an unknown VM, a distinct free-trial/no-permission flag.

- Moved `VmSpec`/`VmDescriptor` out of `IComputeGateway.cs` into their own file, `app/src/DnaEntropyGraph.Core/Cloud/VmSpec.cs`, and finished `EnsurePreconditions()`: it now rejects a spec missing any of the six Hard Rule 10 labels or `maxRunDuration`/`instanceTerminationAction` *and* a value the real Compute API would itself reject - a label value outside `^[a-z0-9_-]{1,63}$`, or a computed `VmName` (`deg-<jobId>`) outside Compute Engine's resource-name charset (an underscore, for example, is legal in a label value but not in a resource name).
- Added `VmSpec.ProjectId` (required) and `VmSpec.VmName` (computed `deg-<jobId>`, Hard Rule 9's naming, never stored separately).
- `AppVersion` is sanitized into its label value (lowercased, disallowed characters replaced with `-`, truncated to 63) rather than validated as-is, since it is normally a semantic version like `0.1.0` containing dots - DECISION #387 records why this one field is treated differently from the other five.
- Added `IComputeGateway.CreateVmAsync`'s `zone` parameter (matching `GetVmAsync`/`StopVmAsync`/`DeleteVmAsync`, which already took one) - DECISION #386.
- Added `DnaEntropyGraph.Core.Cloud.JobId`: `NewId(DateTimeOffset, Random)` generates the `yyyymmdd-hhmmss-<6 lowercase base32 chars>` convention; `MatchesConvention` checks it. Not required of every job id `VmSpec` accepts - only that the id is safe as a label value and inside the computed resource name.
- Added `app/tests/DnaEntropyGraph.Cloud.Tests/VmSpecTests.cs` and `JobIdTests`: a data-driven test per required field proving `EnsurePreconditions()` names the missing field, plus the label-safe-but-not-resource-name-safe case, the dotted-`AppVersion` sanitization case, and the `JobId` format/reproducibility cases.
- Updated `docs/cloud_design.md` section 4 with the implemented guard's exact behaviour.

- Added `DnaEntropyGraph.Core.Cloud.CloudErrorClassifier` (`app/src/DnaEntropyGraph.Core/Cloud/CloudErrorClassifier.cs`): classifies a `CloudError` into `billing | api_disabled | quota | stockout | already_exists | permission | org_policy | network | other`, checking structured `Code`/`HttpStatus` first (docs/cloud_design.md section 5's table) and falling back to the prototype's own fixed substring evaluation order only where GCP gives no structured signal. `org_policy` has no substring fallback, matching the prototype (it never had this bucket).
- Added `app/tests/DnaEntropyGraph.Cloud.Tests/CloudErrorClassifierTests.cs`: a data-driven theory over every case in `tests/contract-fixtures/cloud_error_classification.json` (read-only), plus structured-signal cases the stderr-only fixture cannot express, an explicit billing-before-quota ordering test, and a quota-vs-stockout structured-signal test.
- Updated `docs/cloud_design.md` section 5 to point at the implementation and name its two callers (#58's runner, #208's Health page) as not yet built - the classifier has no production caller today.
- Filed #388 (P2): `IProjectSetupGateway` has no method for preflight step 1 ("project ACTIVE").

- `scripts/check_app_wiring.py` is the ninth repo guard and the C# half of the
  bug class `check_unused_fields.py` already covers in Python: code that
  compiles, runs, passes its tests and does nothing. Nine finding codes, each
  with its own allowlist key: `UNBOUND-COMMAND` and `UNBOUND-OBSERVABLE` (a
  `[RelayCommand]` or `[ObservableProperty]` that no `.xaml` binds, so the
  generated `ICommand` and the `PropertyChanged` notification both go
  nowhere), their `TEST-ONLY-` and `CODE-ONLY-` weaker variants,
  `MISSING-UID-RESOURCE` (an `x:Uid` with no `.resw` entry, which renders a
  blank label rather than failing the build), `ORPHAN-RESOURCE`,
  `UNREGISTERED-DEPENDENCY`, `DEAD-REGISTRATION` and `DANGLING-BINDING`.
- Its first run on the real tree reported 25 findings, which is what a skeleton
  app with ViewModels and no Views actually looks like. They are seeded into
  `scripts/app_wiring_allowlist.json`, each entry naming the issue whose page
  will bind it, to be deleted in that issue's own pull request.
- The allowlist is checked in both directions, so an entry whose finding stopped
  firing fails the run: the Python-side allowlist went stale within an hour of
  being written, and its guard said so.
- `--suggest-allowlist` prints a skeleton with every reason left EMPTY and
  writes no file, on purpose. An allowlist entry whose reason nobody wrote is a
  baseline silently raised, which is the one thing this guard exists to stop.
- `--self-test` (13 checks) asserts both directions: that each finding fires on
  a tree broken in exactly that one way, and that a correctly wired tree is
  clean. `scripts/tests/test_check_app_wiring.py` (20 tests) asserts the same
  rules independently, because a test file whose only assertion is "the
  self-test said PASS" would have agreed with the self-test this repo shipped
  that could not fail.
- Two bugs in the guard were found by its own self-test before it ever ran on
  the repository: a greedy `[^\]]*` in the `[ObservableProperty]` pattern that
  matched the wrong field, and a `DEAD-REGISTRATION` check that called every
  implementation type in `AddSingleton<IFoo, FooService>()` dead. A third was
  found on first real contact: skipping `ServiceRegistration.cs` when looking
  for consumers made every `sp => sp.GetRequiredService<FakeGcp>()` factory
  delegate invisible, so the one object behind five interfaces read as dead.

- `OWNER_TODO.md`'s decisions table now carries the six calls an agent made during the 2026-09-19
  wave, separated from the rest because they are different in kind: each is already implemented, so
  overruling one means reopening work rather than just choosing. #379 (what the worker does when the
  store is unreachable), #364 (the model gate fails closed on an unknown id), #353 (the Geneious
  track bins above 200,000 positions), #332 (the prototype launcher is deleted), #333 (per-exon
  segments, decided but not implemented) and the #343/#345 pair still to settle.
- #15's row is updated: half of the week-1 spike is measured now (an unpackaged WinUI 3 app builds
  from the `dotnet` CLI alone, XAML compiler included, no Visual Studio workload), and the half that
  answers the question, actually launching it, is still unrun.

- `scripts/check_unused_fields.py` caught two things on its first CI run that no local run had:
  `JobManifest.raw` was deleted as part of #361, which made its allowlist entry stale, and
  `SurprisalSummary`'s fields are unread because `analysis/surprisal.py` is knowingly unwired
  groundwork for #123. The stale entry is gone and the summary is allowlisted with an explicit
  instruction to delete that entry when #123 lands, so the wiring cannot quietly leave the summary
  unread. The both-directions allowlist check was the whole reason for building it that way.

- MEASURED 2026-09-19: `ci-worker.yml`'s "manifest schema drift" step ran with `--with jsonschema`
  alone and broke the moment `worker/manifest.py` began importing `predictors.hardware` to validate
  a model id. That pulls `predictors/__init__.py`, which imports numpy, so the step died with
  `ModuleNotFoundError` instead of reporting drift. It now installs the worker package itself, since
  the generator introspects the worker's own dataclasses and its dependency footprint is therefore
  the worker's, not a hand-maintained subset.
- `scripts/tests/test_check_version_lockstep.py` asserted that `app/` did not exist and that the
  guard said so, on the reasoning that this was "a stable fact for tonight". It stopped being true
  about two hours later when issue #61 landed the solution skeleton, and the test went red on the
  first CI run afterwards. It now asserts the rule itself: `app/Directory.Build.props` and
  `worker/pyproject.toml` carry the same version string, and the guard is enforcing rather than
  noticing.

- Fixed #341: `StatusWriter.consecutive_write_failures` counted store write failures and nothing
  escalated. Five consecutive failures now crosses from "blip" to "outage": an `ESCALATED` line on
  stderr, distinguishable from the per-blip line, plus a best-effort local-disk snapshot of the
  current status. A `result.json` write that fails outright writes a local fallback copy too. The
  design call (escalate and leave a breadcrumb, rather than fail the run or retry forever silently)
  is recorded as #379, agent-made and reversible.
- The reason this is not a counter problem: from the app's side a stale `status.json` means the run
  is dead, so the app stops the VM while the worker is alive and burning GPU minutes on a job whose
  result nobody will collect. The sharp end is a run that finishes successfully and cannot say so.
- The local fallback is honest about what it is not. A VM that reaches
  `instanceTerminationAction=DELETE` loses its whole disk, fallback included, so this helps only a
  VM that is merely stopped or one a human inspects via a disk snapshot. Marked
  `THEORY (unverified)` in the code and now a `docs/ToTest.md` row.
- Fixed #344: `manifest.worker.version` was parsed and dropped. A disagreement between the version
  the app declares and the build actually running is now a notice in `progress.jsonl` naming both.
  Visibility only, per the issue's scope: it never refuses or fails the job, and an undeclared
  expectation is not a mismatch against anything.
- Registered `MODEL_UNKNOWN` in the worker's error-code taxonomy and regenerated
  `docs/contract/error-codes.json`. #346's fix in `predictors/hardware.py` added the code without
  the registry entry, and `test_every_code_literal_in_worker_python_source_is_registered` caught it,
  which is the guard doing exactly its job across a lane boundary.

- Fixed #366: `pipeline.run()` built the run's output folder and every file name inside it from
  `cfg.name` with no sanitization at all, so a `--name` containing a path separator, a leading `..`,
  or a few hundred characters went straight into a filesystem path the user never chose. Hard Rule
  14 says the user's files are read-only to us and outputs go to the chosen output folder, and a
  `--name` of `..\..\Documents` is that rule broken by a string, so `sanitize_run_name()` refuses or
  neutralises a traversal rather than merely tidying a name. `cli.py`'s own private `_sanitize_name`
  is gone: `run()` now applies the same function unconditionally at its own top, so the folder name
  and the file names inside it can never disagree.
- Found while verifying #350 end to end. The contig-name hardening that issue asked for was real,
  and protected nothing the user actually gets, because the run folder never went through it.
- Added `analysis/surprisal.py` for #123: per-position `-log2 P(actual base)` out of the same
  `(L, 4)` matrix at no extra GPU cost, with a documented finite ceiling (surprisal is unbounded
  above as P tends to 0, unlike entropy's natural `[0.0, 2.0]`), an ambiguity-fallback rule, and a
  summary carrying total log-likelihood over defined positions only. **It is not wired to anything
  yet**: no writer emits it and no CLI flag turns it on, so #123 stays open. The module and its 13
  tests are groundwork, not the feature.

- Issue #68's five guards all exist now, in `app/tests/DnaEntropyGraph.Guards.Tests`: the `.csproj`
  scan for a `Google.*` reference outside `DnaEntropyGraph.Cloud` and for any inference package
  (Hard Rules 6 and 7), the code-behind scan for anything that branches in a `*.xaml.cs`
  (Hard Rule 8), the `.resw` em-dash scan and the XAML inline-string scan (Hard Rule 13), and
  `VmSpec.EnsurePreconditions()` refusing a spec missing any of the six standard labels, a positive
  `maxRunDuration` or an `instanceTerminationAction` (Hard Rule 10), called by `FakeGcp` before it
  will pretend to create anything.
- Each scanner reports what it actually **read**, not only what it objected to, because a violation
  count of zero means "found nothing to check" and "checked everything and it was fine" equally
  well. Every guard has a test for that exact false pass: an empty `.resw` reports zero entries
  scanned, a glob that matches nothing reports zero files scanned, and `RepoPaths` throws rather
  than scanning nothing if it cannot find `DnaEntropyGraph.sln`.
- MEASURED 2026-09-19: two of the guards were broken against the **real** tree and watched failing.
  An em dash in `Resources.resw`'s `AppDisplayName` failed the resw guard naming the file, the entry
  and the rule; a `Google.Cloud.Storage.V1` reference added to `DnaEntropyGraph.Core.csproj` failed
  the csproj guard naming Hard Rule 7. Central Package Management refused the reference first, which
  is a second layer nobody had counted on.
- `app/Directory.Build.props` gained the `<Version>` that `scripts/check_version_lockstep.py`
  (issue #33) requires. The guard had been soft-noticing while `app/` did not exist and went red the
  moment it did, which is the guard working.

- Fixed #349: `read_genbank` wraps Biopython's own parser (`Bio.GenBank.Scanner`, whose
  every malformed-content failure mode is a `ValueError` or a subclass of one) in a
  `try/except`, re-raising as `GenBankReadError` -- agreeing with `read_fasta`'s own
  blanket guarantee that a malformed file never reaches the caller as a raw traceback.
  Line endings are also normalized before the text reaches Biopython's scanner, matching
  `read_fasta`'s `text.splitlines()`: a lone `\r` (classic Mac, some sequencing
  instruments) now parses successfully instead of merely failing cleanly.
- Docs: `docs/science_and_formats.md` section 4 documents both fixes.

- MEASURED 2026-09-19 (run 35448261797): `ci-app.yml`'s test step had never run a single test.
  It carried `--filter`, `--logger "trx;..."` and `--collect:"XPlat Code Coverage"`, all three
  VSTest options, while the app's projects run on xunit.v3 / Microsoft.Testing.Platform, where the
  runner does not recognise them and gives up: every one of the six test assemblies reported
  `Zero tests ran`, exit code 5. The plain `dotnet test -c Release -p:Platform=x64 --no-build`
  runs 31 tests, 30 passing and 1 skipped. The UI tests need no filter because they are all
  `[Fact(Skip=...)]`; TRX and coverage need the `Microsoft.Testing.Extensions.*` packages first.
- Removed `ci-app.yml`'s `if app/DnaEntropyGraph.sln exists` soft gate now that the solution has
  landed and a real run has executed every step against it, which is the confirmation
  `scripts/check_guard_drift.py` asks for. Leaving it would have turned a future accidental
  deletion of the solution into a green job.
- Corrected the claim, made a few hours earlier in `docs/dev_commands.md` and a `ToTest` row, that
  `ci-app.yml` "will need `working-directory: app`". It already sets it on every step. The
  `dotnet test` working-directory trap is real but is a trap for a human or an agent running the
  command by hand from the repo root, never for CI.

- Added `app/`: the C# solution skeleton for issue #61 (App, Presentation, Core, Cloud, Persistence, LocalEngine, tools/CloudCli, six test projects).
- Added `app/DnaEntropyGraph.sln`, `app/Directory.Build.props` (net10.0 default TFM, x64, Nullable, TreatWarningsAsErrors, central package management), `app/Directory.Packages.props` (CPM), `app/global.json` (SDK 10.0.401 pinned, `test.runner=Microsoft.Testing.Platform` for the .NET 10 `dotnet test` experience), `app/nuget.config` (nuget.org only).
- Added `DnaEntropyGraph.Core`: `RunOptions`, `JobPhase`, `ErrorCatalog`/`UserFacingError`, contract DTOs (`JobManifest`, `ProgressEvent`, `WorkerStatus`, `WorkerResult`), the Cloud gateway interfaces (`IComputeGateway`, `IStorageGateway`, `IProjectSetupGateway`, `IQuotaGateway`, `VmSpec`), and the Presentation-facing abstractions (`IJobEngine`, `IRunRepository`, `ISettingsStore`, `IGcpAccount`, `IDispatcher`, `IFilePicker`, `IToastService`, `INavigator`, `IDialogService`). No `Google.*` reference anywhere in this project (Hard Rule 7).
- Added `DnaEntropyGraph.Cloud`: `FakeGcp`, an in-memory implementation of every Core/Cloud interface at once. No real `Google.*` package reference yet; the real gateways are a follow-up issue under this epic.
- Added `DnaEntropyGraph.Persistence`: in-memory `RunRepository` and `SettingsStore` placeholders for the SQLite-backed versions a follow-up issue will add.
- Added `DnaEntropyGraph.LocalEngine`: `LocalEngineManager`, `LocalJobRunner` (throws `NotSupportedException` - no real worker launch yet), `RunTargetResolver`.
- Added `DnaEntropyGraph.Presentation`: the eight ViewModels from the design spec (`ShellViewModel`, `WizardViewModel`, `NewRunViewModel`, `RunProgressViewModel`, `ResultsViewModel`, `HistoryViewModel`, `CloudResourcesViewModel`, `SettingsViewModel`), CommunityToolkit.Mvvm, no WinUI reference.
- Added `DnaEntropyGraph.App`: the WinUI 3 executable (`App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `app.manifest` with per-monitor-v2 DPI), `Strings/en-US/Resources.resw`, `Services/` (`DispatcherAdapter`, `NavigationService`, `ToastService`, `FilePickerService`, `DialogService` - the latter three are structural placeholders), `JobEngine`, and `Startup/ServiceRegistration.AddDnaEntropyGraph` - the one real production DI graph, called by both `App.xaml.cs` and `Guards.Tests`.
- Added `tools/DnaEntropyGraph.CloudCli`: a console host stub, no subcommands yet.
- Added `app/tests/DnaEntropyGraph.Guards.Tests/DiResolutionTests.cs`: builds the real `ServiceRegistration.AddDnaEntropyGraph` graph with `ValidateOnBuild=true`, resolves all 8 ViewModels (with a vacuity assertion on the scan) and every Presentation-facing/Cloud-gateway interface. Proved red by commenting out the `INavigator` registration (`Unable to resolve service for type 'DnaEntropyGraph.Core.Abstractions.INavigator' while attempting to activate '...ViewModel'`), then green again after restoring it.
- Added `app/tests/DnaEntropyGraph.Core.Tests`, `.Cloud.Tests`, `.Persistence.Tests`, `.Presentation.Tests`, `.App.UiTests` (the last has one `[Fact(Skip=...)]` placeholder pending a real page and an interactive session).
- Only `DiResolutionTests` from the five guards named for issue #68 is implemented here; the csproj `Google.*`/torch scan, the code-behind branching scan, the `.resw` em-dash scan, and the `VmSpec` label/`maxRunDuration` precondition test are left for #68, on purpose, rather than shipped as guards that cannot fail.
- Found: on the .NET 10 SDK, `dotnet test` requires `global.json`'s `test.runner=Microsoft.Testing.Platform` to run an `xunit.v3` project at all (VSTest mode is rejected outright); that setting is resolved from the current working directory, not from the project path, so `dotnet test app/tests/DnaEntropyGraph.Guards.Tests` only works when the shell's working directory is `app/` or a descendant of it, not the repo root. See the PR/report for the exact repro.

- The malformed-input fuzz corpus is now marked `-text` in `.gitattributes` and two tests assert
  its bytes. MEASURED 2026-09-19: `*.fasta text` with `eol=lf` meant
  `fasta_crlf_and_lonecr_mixed.fasta` was committed with its CRLFs already rewritten to LF, while
  the suite kept passing because it ran against the working tree, where the bytes were still
  right. A fresh clone would have tested a different file and nothing would have said so. The new
  tests fail if that normalization ever returns, and were watched failing on a deliberately
  normalized copy before being believed.

- #162: added a corpus-driven fuzz test for the GenBank and FASTA readers.
  `worker/tests/data/malformed/` holds 29 deliberately broken fixtures (empty files,
  binary garbage, truncated mid-record, missing ORIGIN/LOCUS, lone-CR line endings, null
  bytes, an HTML error page saved with a `.fasta`/`.gb` extension, gzip magic bytes, a
  mismatched multi-LOCUS file, and more); `worker/tests/test_fuzz_readers.py`
  parametrizes over every fixture and asserts each produces either a clean
  `FastaReadError`/`GenBankReadError`/`ValidationError` or a clean success, never any
  other exception. `hypothesis` is not installed in `worker\.venv`; per this round's
  brief, nothing was installed to add it, so the property-based half of #162's own
  intent is filed separately as #368 rather than half-done here.
- The fuzz corpus itself doubled as an integration check for every fix landed this
  round: `genbank_bad_coordinates.gb` exercises #331's drop-with-notice and #352's
  corrected grammar together; `genbank_lone_cr.gb` and `genbank_missing_origin.gb`
  exercise #349 directly.
- Mutation-checked: temporarily disabling #349's try/except was caught immediately by
  two fixtures in the corpus (`genbank_missing_origin.gb`,
  `genbank_truncated_mid_feature_table.gb`), proving the fuzz test is a real regression
  guard, not decoration.
- Docs: `docs/tests.md` was named in #162's own body as touched, but is outside this
  lane's owned paths this wave -- not edited; noting it here so it is not missed.

- Fixed #348: `validate_sequence` now special-cases a literal U+FFFD replacement
  character (produced by `readers/encoding.py`'s own `errors="replace"` fallback for a
  byte that was never valid UTF-8) with a distinct error naming the real cause ("the
  file was not saved as UTF-8") and one action (re-save with UTF-8 encoding), instead of
  the generic "Invalid character" wording used for a real typo.
- Docs: `docs/science_and_formats.md` section 4 documents the new diagnosis.

- Fixed #350: `_safe_contig_name` (`readers/input.py`) now disambiguates a name that
  sanitizes to a Windows-reserved device name (`CON`, `NUL`, `PRN`, `COM1`-`9`,
  `LPT1`-`9`) and caps length for a realistic output path, truncating only the base
  portion so the `_<n>` disambiguator that keeps records in one multi-record file from
  colliding always survives truncation intact. A defensive `_assert_unique_contig_names`
  guard raises rather than silently letting a name collision overwrite one record's
  output with another's.
- CORRECTED per Hard Rule 18: #350's own original claim that "Windows refuses to create
  CON.fasta" does not reproduce on this dev box (Windows 11 Home 10.0.26200) -- measured
  directly via Python, .NET and PowerShell, all of which created it successfully. Kept
  the hardening anyway as free insurance (see the code comment and the correction
  comment on #350 for the full reasoning) and filed #366 for the real gap found while
  verifying this end-to-end: `pipeline.py` builds the actual output folder/file name
  from `cfg.name` directly, never from this sanitized form.
- Docs: `docs/science_and_formats.md` section 4 documents the hardening and the
  correction.

- Fixed #351: `read_fasta`'s duplicate-header detection now keys on the record ID (the
  header up to its first whitespace, the part BLAST/samtools/IGV treat as the
  identifier) rather than the full header line, so two records sharing an ID but
  carrying different free-text descriptions are correctly flagged as a genuine ID
  collision.
- Fixed #352: `read_genbank`'s zero-feature summary notice now reads "0 gene feature(s)"
  instead of the grammatically broken "0 no gene feature(s)".
- Docs: `docs/science_and_formats.md` section 4 documents the ID-based duplicate check.

- Fixed #346 (DECISION recorded at #364, agent-made and reversible): `predictors/hardware.py`
  now fails CLOSED for an Evo model id not in `MODEL_REQUIREMENTS`, raising a new
  `UnknownModelError` (`code = "MODEL_UNKNOWN"`) naming the bad id and every supported id,
  before any Hopper/gpu-count check. `model_requirement()`'s own permissive lookup default
  is unchanged; none of the four real `MODEL_REQUIREMENTS` entries changed.
- Fixed #347: `check_probability_matrix`'s row-sum error did not name NaN when a NaN
  probability was the actual cause (it was already caught, just via a confusing "worst
  deviation nan" message). Now checks for NaN explicitly and names the affected row
  indices before falling through to the range/row-sum checks.
- Fixed `WindowPlan.context` (the field the lane-B round-1 report flagged and correctly
  did not fix): unlike `WindowPlan.ceiling` (which had a genuine gap -- nothing else
  recorded it, so it was threaded into `DirectionResult`/`SummaryWriter`), `context` (K)
  was already independently recorded on `DirectionResult.context_length`, sourced from
  the same parameter every caller already has in scope. Deleted rather than wired up, with
  the reasoning recorded in `WindowPlan`'s own docstring.
- Issue #160 (property-based tests), windowing and direction scope only (validation is
  Lane A's, deliberately not covered): `hypothesis` is not installed on this laptop and
  nothing was installed to get it, so this is the table-driven equivalent over a
  deliberately adversarial `(L, K, ceiling)` grid. New tests: window coverage for
  `L < K`/`L == K`/`L == 2K`/`L == 2K+1`/`L == 1` across K-bound and ceiling-bound windows
  (`test_windowing.py`); entropy invariance under the complement column permutation and
  entropy bounded in `[0.0, 2.0]` across a degenerate-row/softmax-temperature grid
  (`test_entropy.py`, `test_direction.py`); the seam position matching where the combiner
  actually switched, checked against an independent `BOTH_SEPARATE` run rather than
  asserted `== K` on faith (`test_direction.py`). All use a tolerance, never exact float
  equality. Each was mutation-checked against a planted bug (an off-by-one in
  `plan_windows`'s pass-count formula, an off-by-one in `_combine`'s context threshold, a
  column-asymmetric bug in `shannon_entropy`) and caught it. The real Hypothesis version is
  filed separately as #367.

- Added `scripts/check_unused_fields.py`, an eighth guard: it parses every `@dataclass` under
  `worker/src/dna_entropy/` and fails on any field that is set and never read, counting an
  attribute load or `getattr` as a read and a plain assignment or a construction keyword
  argument as not one. Reads that happen only inside a serialization method are reported
  separately, because a field that only travels back out to JSON still does nothing. Reads are
  also collected from `scripts/`, since a generator there can legitimately be a field's only
  consumer, as `ErrorCodeSpec.raised_by` is.
- MEASURED 2026-09-19: its first run found nine fields that were parsed, validated,
  schema-checked and ignored, including `Lifecycle.afterTask="keep"` leaving a VM running with
  no expiry at all (a Hard Rule 11 violation worth about twenty dollars a day), and
  `ModelRequirement.min_gpu_count` letting a single-GPU machine past the gate for a model that
  needs two cards. All nine are fixed (#292, #293, #296, #304, #306, #338, #340) or tracked
  (#343, #344, #345, #361).
- The guard documents its own blind spot rather than hiding it: it matches attribute reads by
  name, not by type, so a field whose name collides with a read attribute on another class is
  silently counted as read. `WindowPlan.context` and `StoreSpec.bucket/prefix/root` are masked
  that way today. A clean run means no field with a name nothing reads, not no unread field.
- `scripts/unused_fields_allowlist.json` carries the exceptions, each with a written reason, and
  supports `Class.*` for a document serialized wholesale with `dataclasses.asdict()`. A stale
  entry fails the guard, so the file cannot rot into a list of things that used to be true.

- Fixed issue #304: `manifest.json`'s `outputs` array is now honored literally instead of
  being silently ignored beyond three of eight writers. `RunConfig` (`worker/src/dna_entropy/
  config.py`) gained `include_fasta`/`include_track`/`include_geneious`/`include_stats`/
  `include_genbank`/`include_genes_gff3`, each defaulting to `True` so every existing caller
  (the CLI, every bare `RunConfig()` in tests) keeps writing the full output set unchanged.
  `pipeline.py`'s `_write_genbank_outputs`/`_write_standard_outputs` gate every writer on its
  matching flag. `JobManifest.build_run_config` (`worker/src/dna_entropy/worker/manifest.py`)
  maps `outputs` onto all six flags: an empty/omitted array still means "everything" (no
  behaviour change for a manifest that never mentions `outputs`), a non-empty array now
  actually suppresses anything not named. The genes-computation trigger (`cfg.genes`, which
  gates a real Prodigal compute cost) is kept independent of this "unspecified means
  everything" fallback on purpose - only an explicit `input.genes` or an explicit
  `genes_gff3` entry in a non-empty `outputs` turns Prodigal on, exactly as before.
- Found and fixed while auditing #304: `analysis.format` (`AnalysisSpec.track_format`) was
  accepted as any unvalidated string and, separately, never actually consulted by
  `build_run_config` - the track format was derived purely from `outputs` membership and
  always fell back to `bedgraph` regardless, so a manifest declaring `analysis.format:
  "wig"` with no `outputs` key silently got bedgraph. `AnalysisSpec.track_format` is now
  typed `TrackFormat` (validated at parse time like `analysis.direction`) and used as the
  fallback whenever `outputs` is empty/omitted; a non-empty `outputs` array remains
  authoritative (issue #304's own scope, unchanged). Regenerated `docs/contract/
  manifest.schema.json` (`python scripts/gen_manifest_schema.py`) - `analysis.format` now
  carries a real `enum: [bedgraph, wig]` constraint instead of a bare `"type": "string"`;
  `--check` passes.
- Fixed issue #306: `manifest.json`'s per-input `fastaRecords: "first"` is now honored.
  `RunConfig.fasta_records` (default `"all"`) is consulted in `pipeline.run()` right after
  `load_input()` returns, truncating to the first contig when `fasta_records == "first"` and
  the input is FASTA (GenBank multi-record input is untouched - the field is documented as
  FASTA-specific in `docs/job_contract.md` section 3), and records a notice naming how many
  records were dropped. `readers/input.py` (Lane A's file) was not touched: the truncation
  happens entirely in `pipeline.py`, which this lane owns. `worker.manifest.InputSpec.
  fasta_records` now validates against `{"all", "first"}` at parse time (previously any
  string was accepted silently and treated as `"all"`).
- Fixed issue #291: deleted `worker/packaging/launcher.py` (the double-click PyInstaller
  wizard, which shelled out to the retired `cloudrun` CLI command removed in #277) and
  `worker/packaging/build_exe.ps1` (which existed only to build `launcher.py` into an exe)
  outright, rather than rewriting the dead call. The migration inventory already recorded
  both as GUT/REFERENCE-only, not shipped, with no C# equivalent needed - the WinUI app's
  own drop-zone/paste dialog (design section 4.2) replaces the wizard. Design call recorded
  at issue #332 (DECISION).
- Verified issue #299 is already satisfied: `scripts/gen_manifest_schema.py`, `worker/src/
  dna_entropy/worker/schema_gen.py`, `docs/contract/*.schema.json`, and the CI-wired
  `--check` drift guard (`.github/workflows/ci-worker.yml`) all exist and pass. Recommended
  closure in a comment on the issue; not closed here (only the orchestrator closes issues).
- Fixed (mechanical unused-field guard, `scripts/check_unused_fields.py`, orchestrator-
  owned): `limits.heartbeatSeconds` was parsed but `run_job` built `StatusWriter` with no
  `interval_seconds` at all, so the worker always ticked at `status.py`'s own hardcoded
  10 s regardless of the manifest. `run_job` now passes
  `interval_seconds=min(heartbeatSeconds, 10)` - never slower than declared, never above
  the pre-existing 10 s ceiling (`progress.jsonl`'s own separate 5-10 s cadence is
  unaffected). Filed as #340 before the fix; comment added noting it is now resolved.
- Fixed (same guard): `limits.cancelPollSeconds` was parsed but `CancelWatcher.poll()`
  (`worker/cancel.py`) did a real store `exists()` round-trip on *every* cooperative-
  cancellation checkpoint (once per window, once per contig) with no throttling - a long
  batch tiled into many windows meant a real store query per window. `CancelWatcher` now
  takes `poll_interval_seconds`/`time_source` and throttles its real round-trip to at
  most once per interval, returning the last-known (non-cancelled) result in between; the
  checkpoint is still called every window/contig, only the underlying store query is
  throttled, so job_contract.md section 6's latency-to-effect budget is unchanged.
  `run_job` wires `poll_interval_seconds=manifest.limits.cancel_poll_seconds`. Updated two
  existing tests (`test_cancellation_mid_job_keeps_partial_results_for_completed_inputs`,
  `test_cancellation_partway_through_one_inputs_records_uploads_completed_contigs_first`)
  to set `cancelPollSeconds: 0` ("always due"), since they were relying on the pre-fix
  unthrottled behavior to observe a cancellation within the same instant, not testing the
  throttle itself. Filed as #338 before the fix; comment added noting it is now resolved.
- Fixed (same guard; Hard Rule 11 violation, flagged P1 by the orchestrator):
  `lifecycle.afterTask == "keep"` used to skip `run_job`'s entire lifecycle block, and
  `lifecycle.keepAliveMinutes`/`lifecycle.afterKeepAlive` were parsed but never consumed
  anywhere - a manifest requesting `"keep"` left the VM running with **zero** worker-side
  expiry, ever ("keep alive always has an expiry, never indefinitely"). The real
  keep-alive queue/idle-timer feature (waiting for a follow-up job) is issue #93 and is
  NOT built here tonight - building it correctly (queue polling, `lease.json`, an `idle`
  stage) needs its own deliberate design, not a rushed partial version. Until #93 lands,
  `run_job` now safely degrades `"keep"` to `lifecycle.afterKeepAlive` (default `"stop"`)
  with a notice explaining why, and forces `"stop"` if `afterKeepAlive` is itself `"keep"`
  rather than propagate a second unbounded keep. This is a worker-side safety net only -
  the VM's own `maxRunDuration`/`instanceTerminationAction=DELETE` (Hard Rule 10) remains
  the real backstop, and is itself unverified against a real GCP project tonight
  (`docs/ToTest.md`).
- `docs/job_contract.md` section 3's field-notes table updated in the same change: a new
  `outputs` row documents the suppression fix and the pre-existing (unresolved)
  bedgraph+wig-always-both asymmetry between GenBank and FASTA/paste input on the GenBank
  path; the `fastaRecords` row now says the field is honored instead of "if ever needed";
  `heartbeatSeconds`, `cancelPollSeconds`, and `afterTask` rows document the three fixes
  immediately above.
- Added tests: `worker/tests/test_pipeline.py` (11 new tests: per-`include_*`-flag
  suppression asserted against real files on `tmp_path`, not just the returned `RunConfig`
  or `RunResult.outputs` list; `fastaRecords` truncation, its default, its notice text, and
  its GenBank non-interference) and `worker/tests/test_worker_manifest.py` (9 new tests:
  `build_run_config`'s outputs-to-flags mapping, including a regression guard that the
  "unspecified outputs means everything" fallback does not also turn on Prodigal by
  itself). Updated `worker/tests/test_worker_runner.py::test_genbank_input_end_to_end`,
  which previously relied on the outputs-are-ignored bug (its manifest fixture's outputs
  list excludes `"genbank"`) to get a `.gb` file; it now asks for `"genbank"` explicitly.

- Fixed #293: `EvoPredictor._extract_logits` could leak a bare `AttributeError` instead
  of a `PredictorError` when the model's unwrapped output had no `.ndim`. The unwrap
  steps (nested tuple/list -> `.logits` -> drop batch dim) moved, unchanged, into a new
  torch-free `predictors/logits.py::normalize_model_output`, so the failure mode is now
  unit-tested on any machine (`test_evo_predictor.py`, using the same fake-`torch`/`evo2`
  module technique `test_model_gating.py` already used) instead of being reachable only
  on the GPU box. `evo.py`'s `_extract_logits` is unchanged in behavior, only in what it
  raises on an unrecognized shape.
- Fixed #292: `aligned_acgt_probs` crashed with an `IndexError` on a zero-length input
  (`aligned[0] = ...` on a `(0, 4)` array). Now returns an empty `(0, 4)` float32 array,
  which satisfies the `(L, 4)` contract vacuously and `check_probability_matrix` agrees
  (row-sum-to-1.0 is vacuous for zero rows).
- Fixed #296: `GeneiousWriter`'s per-base GFF3 track had unmeasured cost at large context
  lengths. MEASURED 2026-09-19: an unbinned 1,000,000-position track is 87.78 MB / 1.587s
  to write; a 10,000-position track is 0.82 MB / 0.012s. `GeneiousWriter` now switches to
  fixed-size mean-entropy bins above `DEFAULT_MAX_PER_BASE_FEATURES` (200,000) combined
  positions, recording a `# NOTE` line in the file; after the fix, both a 1,000,000- and a
  10,000,000-position run land at ~22-23 MB. `max_per_base_features=None` forces unbinned
  output. Design call recorded at #353 (`DECISION`).
- Fixed #339 (found during this audit): `GenBankWriter` wrote CRLF line endings on
  Windows (Hard Rule 5) because it handed `Bio.SeqIO.write` a bare path instead of an
  already-open UTF-8/LF text handle. Now opens the file itself first.
- Fixed #342 (found during this audit): `GffWriter`'s GFF3 column-9 percent-encoding
  omitted `%` itself, which the GFF3 spec requires escaping (it is the escape character).
  `%` is now escaped first, before the delimiter characters, so a literal `%` in a gene
  id can never be misread as part of another escape sequence.
- Fixed (orchestrator-reported unused-field findings, `scripts/check_unused_fields.py`):
  `predictors/hardware.py`'s `ModelRequirement.min_gpu_count` was set (evo2_40b needs
  two H100s) and never read by `require_hardware` -- a single-GPU H100 box would have
  passed the gate and failed later, deep inside multi-GPU model loading. `require_hardware`
  now takes an optional `gpu_count` and refuses with a named error when it is below
  `min_gpu_count`; `EvoPredictor.__init__` passes the real `torch.cuda.device_count()`.
  `ModelRequirement.precision` was also set and never read; it now appears directly in
  every `ModelNeedsHopperError` message. `analysis/windowing.py`'s `WindowPlan.ceiling`
  was set and never read; `DirectionResult` now carries the same `ceiling` value
  `analyze_direction` was called with, and `SummaryWriter`'s provenance section prints it,
  so a report can show why `window < 2*context_length` without reading notice text.
- Filed #346 (P3, `needs-criteria`): `predictors/hardware.py` fails OPEN (treats as
  bf16/no-Hopper) for an Evo model id not yet in `MODEL_REQUIREMENTS`; a future
  Hopper-only model added to the evo2 library before this table catches up would sail
  through the gate. Not fixed this wave -- needs an explicit policy call.
- Filed #347 (P3, `good-first-issue`): `check_probability_matrix`'s row-sum error message
  does not name NaN when a NaN probability is the actual cause (it is still caught, just
  via the row-sum-not-1.0 path, with a confusing "worst deviation nan" message).

- Fixed #330: a UTF-8 or UTF-16 byte-order mark broke FASTA and GenBank reading outright
  (rejected as "no records found") and a UTF-16 file decoded as UTF-8 turned into a wall
  of `�` replacement characters. Added the single shared
  `worker/src/dna_entropy/readers/encoding.py` that every reader (`paste.py`,
  `fasta.py`, `genbank.py`, `detect.py`) now decodes through: a UTF-8 BOM is stripped, a
  UTF-16 BOM (either byte order) is decoded in full, and everything else falls back to
  UTF-8 with `errors="replace"` (#294's fix, now centralized in one place instead of
  three, so it cannot drift into a fourth, fifth answer).
- Routing GenBank through this same decoder also fixed an unrelated inconsistency:
  Biopython's own file-opening used the OS locale encoding (cp1252 on this Windows box,
  MEASURED 2026-09-19), silently different from FASTA/paste's forced UTF-8, and would
  have decoded differently again on a UTF-8-locale Linux box.
- Docs: `docs/science_and_formats.md` section 4 now describes the shared decoder and
  the GenBank-locale-encoding fix.

- Fixed #331: a GenBank gene feature whose coordinates fell outside its own record's
  sequence length (a feature-table/ORIGIN mismatch -- truncation, hand-editing,
  corruption) was accepted silently, and would have let `writers/genbank.py` slice a
  wrong, out-of-range span for its mean-entropy note. `readers/genbank.py` now drops
  such a feature with a loud notice naming the gene id, its coordinates, and the
  record's real length -- rather than a hard failure for the whole record, so the
  record's other genes are still reported.
- Two deliberate exceptions, reasoned in the code: a feature carrying GenBank's own `>`
  partial-end marker is allowed to report an end past the record's length (GenBank's own
  way of saying "known to continue beyond what was given"; THEORY (unverified),
  confirmed only against this reader's own synthetic fixture); a compound
  (spliced/origin-wrapping) feature's segments must each individually fit in bounds
  regardless of a partial marker, which a legitimate origin-wrapping feature on a
  circular plasmid (issue #128's intended shape) already satisfies per-segment, so this
  check does not block it.
- New fixture: `worker/tests/data/out_of_range.gb` (a good gene, an out-of-range gene,
  and a legitimately-partial gene on one record).
- Docs: `docs/science_and_formats.md` section 4 documents the check and both exceptions.

- Round 2 audit: caught and fixed a Hard Rule 5 (ASCII-safe console output) violation in
  my own #331 notice string before reporting it done -- an em dash that a Windows
  console codepage mangled into `?` at runtime, found by actually running the real CLI
  end to end against the new fixture, not by reading the source.
- Round 2 audit, filed as issues rather than fixed (deeper pass over readers/ and
  validation/, per the shapes named in the round-2 brief): a U+FFFD-from-bad-encoding
  "Invalid character" error that never says it hit an encoding problem (#348); the
  GenBank reader letting Biopython's own parser exceptions escape as raw tracebacks on a
  missing ORIGIN block or lone-CR line endings (#349); `_safe_contig_name` accepting a
  Windows-reserved device name (CON, NUL, PRN, COM1, ...) or an unbounded-length id
  (#350); FASTA duplicate-header detection comparing the full header line instead of
  just the record ID, missing a real ID collision with a different description (#351);
  a grammatically broken "0 no gene feature(s)" summary notice (#352).

- Fixed #314: `readers/input.py::load_input()` validated `cfg.max_total_len` against each
  GenBank/FASTA record individually, never against the sum, so a multi-record file many
  times over the documented whole-input cap could pass cleanly one small record at a time.
  Both the GenBank and FASTA branches now track a running total across records and raise
  `ValidationError` (naming the length reached, the cap, and by how much it is over) the
  moment the sum exceeds the cap. Acceptance criteria were written into #314 as a comment
  before implementing (it carried `needs-criteria`); the issue's own per-record/whole-input
  framing was corrected in that comment against the real mechanism (`cfg.max_len` is a
  GPU per-window ceiling only, never a per-record validation bound — see
  `docs/science_and_formats.md` section 4).
- Filed #330 (P2): a UTF-8 BOM breaks FASTA and GenBank reading outright and gives a
  confusing "invalid character" error on the paste path; not fixed this wave.
- Docs: `docs/science_and_formats.md` section 4 (validation rules) now describes the
  encoding-robustness fix, the corrected whole-input-cap mechanism, and the
  compound-location notice behaviour.

- Fixed #295: a GenBank `join(...)`/`complement(join(...))` (spliced) gene location was
  silently collapsed to its outer bounding box, with no indication the reported
  begin/end (and any downstream mean-entropy figure) included intron sequence.
  `readers/genbank.py` now detects a Biopython `CompoundLocation` and raises an explicit
  notice per compound feature naming its exact exon segments; the bounding box itself is
  unchanged (a `GeneFeature` change would touch `annotators/base.py`, outside this lane) —
  see the tracked `DECISION` issue on adding per-exon `GeneFeature.exons`.
- New GenBank fixture `worker/tests/data/spliced.gb`: one plain gene plus two compound
  (spliced) genes, one on each strand, for #295's tests.
- Filed #331 (P2): the GenBank reader accepts a gene feature whose coordinates fall
  outside its own record's sequence length with no validation or notice; not fixed this
  wave.
- Filed #333 (DECISION): whether `GeneFeature` should carry per-exon segments so a spliced
  gene's mean-entropy figure can be computed from exon bases only; needs changes in
  `annotators/base.py` and `writers/genbank.py`, outside this lane's ownership.

- Fixed #294: `PasteReader.read()` crashed with `UnicodeDecodeError` on any non-UTF-8 byte
  in a pasted file or on stdin. Both branches now decode with `errors="replace"`, matching
  `readers/fasta.py` and `readers/detect.py`'s existing behaviour, so a bad byte reaches the
  normal validation-stage checks instead of crashing the process.

- Added `docs/branching_and_prs.md` and indexed it: nothing lands on `main` by a direct push any
  more, every unit of work is a branch, a pull request and a merge, one issue per PR. It records
  why `--merge` and never `--squash` (local `main` already holds the branch commit, so a squash
  makes the next `git merge --ff-only` fail), that CI here fires on demand only so a PR that looks
  green has been checked by nothing, and the local guard list to run before every merge.

- The .NET 10 SDK is now installed machine-wide at `C:\Program Files\dotnet\sdk` (10.0.401), by
  the owner from an elevated prompt, so `docs/onboarding.md` leads with that and keeps the
  per-user `dotnet-install.ps1` route for the unattended case only. The `PATH` and `DOTNET_ROOT`
  overrides that pointed at `%USERPROFILE%\.dotnet` have been removed, so the box has exactly one
  SDK and no ambiguity about which one a build used (#35).

- Documented the .NET 10 SDK install route that actually works unattended on the dev laptop.
  `winget install Microsoft.DotNet.SDK.10` fails with exit code 1602 because the machine-wide
  installer wants elevation and a non-interactive session cannot answer the UAC prompt;
  `dotnet-install.ps1 -InstallDir "$env:USERPROFILE\.dotnet"` needs no administrator. The
  shared host on `PATH` only finds SDKs beside itself, so a per-user SDK stays invisible until
  `PATH` and `DOTNET_ROOT` prefer it, which reads exactly like a failed install (#35).
- MEASURED 2026-09-19: an unpackaged, self-contained WinUI 3 app on .NET 10.0.401 with
  Microsoft.WindowsAppSDK 1.8.250916003 restores, compiles its XAML and builds with zero
  warnings using only the `dotnet` CLI, with no Visual Studio workload installed. The evidence
  and the four things still unverified are recorded on #35.

**2026-09-19: the first night.** The five entries below are this repository's entire
history to date - an empty scaffold to a documented, guarded, worker-gutted starting
point, in one overnight session across several branches merged in parallel. Read newest
first, as always (oldest to newest below: conventions/skills/hooks foundation, GitHub
labels/issue-forms/CI guards, the prototype's `dna_entropy.cloud` package gutted down to
what the C# port has to reproduce, the repo-safety hooks and diagnostics scripts ported,
and the design/science/job-contract reference docs, most recent), but the shape of the
night is the same either way: get the guardrails and conventions in place first, then
build on top of them, rather than writing product code before anything could check it.

- docs: add the design and science reference set: `architecture.md`, `job_contract.md`, `science_and_formats.md`, `cloud_design.md`, `gcp_setup_manual.md`, `ui_conventions.md`, `packaging_design.md`, `release_runbook.md`, `threat_model.md`. `science_and_formats.md` carries the coordinate systems each output format uses, since bedGraph is 0-based half-open and GenBank is 1-based inclusive and mixing them shifts every feature by one base in a way that looks plausible in a viewer. `job_contract.md` specifies `manifest.json`, `status.json`, `progress.jsonl`, `result.json` and `control/cancel` field by field as the contract #278 has to satisfy. `threat_model.md` is explicit about the size of the `cloud-platform` scope this app asks a lab user to grant. Closes #286, #27; progresses #26 and #287. For #287, no new issues were needed: every behaviour in the retired cloud package was already tracked, and the prototype's exact behaviour and line references went as comments onto the twelve issues that own them, with the mapping recorded in `docs/migration/2026-09-19-worker-migration-inventory.md`.

- chore(scripts): port the four repo-safety PreToolUse hooks (`block_git_stash`, `block_recursive_delete`, `block_agent_dispatch_in_worktree`, and the new `block_unlabelled_vm_create`), rebased onto Windows and PowerShell and covered by tests that exercise both arms of every guard; port `issue_precheck.py` and `compile_sprint_log.py` with this repo's real label set, milestones and repo name; port `sync_memory.py` with the secret scan that is the point of it, since this repo is public; and port `triage_diagnostics.py` against the documented manifest and status schema, with every field path in one `SCHEMA_FIELDS` block. Two real defects were fixed in the port: a hard-coded `post-alpha` milestone check that could never fire in this repo, and `find_stray_changelog_dirs()`, which was defined and never called. Closes #271, #272, #273, #274. Files #299 and #300 for the two pieces of the donor's automation that have no home here yet.

- refactor(worker): gut the prototype's `dna_entropy.cloud` package and its CLI verbs, extracting first what the C# port has to reproduce. The quota regexes, the error taxonomy and its evaluation order are now shared JSON vectors at `tests/contract-fixtures/`, in the repo root where the future xUnit suite reads the same files rather than a second copy. The package itself moves to `worker/legacy/cloud/` with a provenance note, because `keeper.py`'s setup diagnosis is still unported (#213) and a deleted file is a bad place to read it from. `cloudrun` and `keep-gpu` leave the CLI, `keep_gpu.py` and `keep-gpu.spec` go, and `pyproject.toml` drops the PyInstaller extras. Suite went from 147 passed / 2 skipped to 92 passed / 2 skipped: 66 tests left with the module they covered, 11 new ones replaced what still applies. Closes #284, #280, #277, #285. Six bugs found while reading the prototype are filed as #291 to #296.

- ci(packaging): add `.github/`: `labels.yml` as the source of truth for the 22 labels, issue forms that carry the Done-when and Observable contract so an issue filed through the UI cannot skip it, a decision form that demands a recommendation and a reversal cost, the PR template, `dependabot.yml` with the CUDA-pinned packages deliberately ignored, and the `ci-worker`, `ci-docs`, `ci-app` and `codeql` workflows. Every step that guards a file which does not exist yet prints a GitHub notice, passes, and starts enforcing the moment that file lands, so the pipeline tightens itself instead of needing an edit per issue. `ci-docs` adds two guards the repo did not have: the private conventions donor must never become a tracked file, and no absolute user-home path may appear in one. The second is `scripts/check_user_home_paths.py`, which allows a fictional account name in an example, rejects a real one, self-tests both arms, and found three more real paths on its first run. Closes #29, #297; progresses #31 and #197.

- docs: rewrite `CLAUDE.md` from the CLAIR template (21 Hard Rules, Stack table, Critical Pitfalls), add the `AGENTS.md` stub, port the 5 ADOPT skills plus `working-on-gcp` and `winui-dev`, seed `.claude/memory/` with 27 lesson files, add `.claude/agents/cold-diff-reviewer.md` and `.claude/README.md`, derive `.claude/settings.json` from the Appendix C template, author `.editorconfig`/`.ignore`/`OWNER_TODO.md`, and draft the developer half of the day-one `docs/` set (`README.md`, `hard_rules.md`, `entry_points.md`, `onboarding.md`, `dev_commands.md`, `tests.md`, `project_structure.md`, `environment.md`, `tech_stack.md`, `ToTest.md`, `sprint_log.md`, `changelog.d/README.md`). Closes #267, #268, #269, #270, #275, #289, #290 (partial: developer half of #25, #276, #30). Files a DECISION issue (#301) for a GPLv3 dependency (`pyrodigal`) found while writing `tech_stack.md`, and a P0 issue (#297) for two pre-existing literal user-home paths the new `ci-docs.yml` guard would have failed on.
