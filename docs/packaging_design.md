# Packaging design: installer, signing, update channel, version lockstep, container images

Last updated 2026-10-15. Built today: the self-contained publish, an unsigned `vpk pack`
in `ci-app.yml` on every run, the version-lockstep check and the notices and scanning
(section 8). Not built: `release.yml` (issue #178), signing, the update check in the app
(issue #179), published container images.

**Status update (2026-09-19):** the line below used to read "specification, not yet
implemented" and name `app/`, `worker/vm/`, and `worker/Dockerfile.*` as not existing yet.
All three now exist (`app/DnaEntropyGraph.sln`, issue #61; `worker/vm/startup.sh`, issue
#45; `worker/Dockerfile.cpu`/`.cuda`, issue #36) - corrected here rather than left as a
stale assertion about a state that has since changed (see the "assertion that encodes a
temporary fact" lesson: assert the rule/decision, not a snapshot of today's tree). This doc
records the decisions already made (spec D2, D3, D5, D6) and what each one is for, so a
later change has to argue against a reasoned choice instead of rediscovering the tradeoffs
from nothing; section 8 (supply-chain notices and scanning) is the one part of this file
actually built and verified as of this session.

---

## 1. Installer: Velopack, not MSIX, not Inno (D2)

**Decision:** Velopack (`Setup.exe` plus delta updates, fed from GitHub Releases), signed
with Azure Trusted Signing, packaging an unpackaged, self-contained WinUI 3 build.

**Why not MSIX:** MSIX sideloading needs a trusted certificate chain, and an unsigned or
untrusted-chain MSIX simply will not install on a locked-down lab PC without an admin
pre-installing a trust certificate first. "App Installer + GitHub Releases" redirects
(the usual MSIX side-load path) are also flaky in practice. A tool aimed at lab
biologists on managed institutional PCs cannot assume an IT department will pre-approve a
certificate before day one.

**Why not Inno Setup:** Inno would work as an installer, but then in-app updates become a
hand-rolled polling-and-replace mechanism instead of a maintained library's job. Given
the app already needs an update channel (below), reusing the same tool for both the
installer and the updater is the smaller total system.

**What Velopack gives instead:** per-user install with no admin elevation required, delta
updates (only the changed bytes download, not a full reinstall), and
`UpdateManager(new GithubSource(repoUrl, null, false))` as the entire in-app update-check
implementation, called from `VelopackApp.Build().Run()` as the first line of `Main`.
Velopack's WinUI 3 unpackaged support and its unpackaged `AppNotification` registration
are both verified in the week-1 spike (`packaging: spike: WinUI 3 unpackaged + Velopack`)
rather than assumed.

**Fallback documented, not built:** Inno Setup plus an Octokit-based update check, kept
as a written-down fallback if the Velopack spike fails, not as a second implementation
maintained in parallel.

## 2. Build target

`.NET 10 LTS`, current stable Windows App SDK (2.x as of September 2026, verified in the
same week-1 spike per D3), `win-x64`, self-contained, unpackaged
(`-p:WindowsPackageType=None`). Self-contained means the .NET runtime ships inside the
installer; a lab PC with no .NET installed, or an old one, still works. `dotnet publish -r
win-x64 --self-contained -p:WindowsAppSDKSelfContained=true
-p:WindowsPackageType=None`, then `vpk pack`.

`WindowsAppSDKSelfContained` is also set in `DnaEntropyGraph.App.csproj` itself, conditioned on `_IsPublishing` (#475), so `dotnet publish` carries the runtime even without the flag; `msbuild -t:Publish` and VS publish profiles do not set `_IsPublishing` and must pass it.
MEASURED 2026-10-03: `dotnet publish -r win-x64 --self-contained` without it produced 252 files and no `Microsoft.ui.xaml.dll`; with it, 441 files including `Microsoft.ui.xaml.dll`. `SelfContained` alone covers the .NET runtime only. It is publish-only because, set for every build, a self-contained Windows App SDK drops the bootstrap auto-initializer and the Guards.Tests host failed with `COMException: ClassFactory cannot supply requested class` in `DispatcherAdapter` (MEASURED 2026-10-03). `Guards.Tests/AppPublishPropertiesGuardTests` fails if the property goes missing.

An unsigned build (7-day artifact retention) comes out of every `ci-app.yml` run for
review once #513 lands (MEASURED 2026-10-03: vpk 1.2.161 refuses to pack an app that does not call
`VelopackApp.Build().Run()`, needs `--mainExe DnaEntropyGraph.App.exe`, and has no flag to skip the check; until then the
pack step warns and skips, and switches itself on when app source mentions `VelopackApp`; locally `vpk pack` against a real
publish folder took about 1 s to fail, so no pack duration is known yet); only a tagged push runs `release.yml`, which is the only path that signs and
publishes.

## 3. Signing

Azure Trusted Signing, wired into `release.yml`. **The known footgun, worth repeating
here because it has bitten other projects silently:** the signing step can no-op quietly
if its secret is unset, producing an unsigned build that looks like a normal release
artifact until a user's SmartScreen warns them. `release.yml` therefore asserts every
required secret (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
`TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE`, plus `OAUTH_CLIENT_JSON`, see section 9) exists
and fails the build loudly before attempting to sign, rather than letting the signing
step's own silent-no-op behaviour be the only signal. The assert is the `verify` job and every other job
`needs` it (pinned by `scripts/tests/test_release_workflow.py`), so a missing secret stops the run before anything is built,
pushed or signed. The signing step is `azure/artifact-signing-action` (Microsoft renamed Trusted Signing to Artifact Signing;
the action is the renamed `azure/trusted-signing-action`, MIT, pinned by SHA). It signs this project's own binaries
(`DnaEntropyGraph.*.dll`, `DnaEntropyGraph.App.exe`) before `vpk pack`, then `Setup.exe` after it, and a
`signtool verify /pa` step proves both, because a green signing step alone is not proof. THEORY (unverified): Velopack's own
`Update.exe`, which lives inside the package, is not signed by this flow; if SmartScreen objects on updates, move signing to
`vpk pack --signParams`. Do not remove this assertion to
"simplify" the workflow; it is there because the no-op is a real, documented Azure
Trusted Signing behaviour, not a hypothetical.

Trusted Signing identity validation is not instant (`OWNER_TODO.md` item 3 flags starting
it early). Until it completes, v0.1 and v0.2 may ship with an explicit
`-p:SkipSigning=true` escape hatch that shows a red banner in the built app, so early
internal testing is never blocked on it; v1.0 (lab release) cannot ship this way.

Verification on a clean VM: `signtool verify /pa Setup.exe` must accept the signed
artifact before a release is considered done (see `release_runbook.md`).

**The dev-build banner (issue #263).** An unsigned exe is a development build or a CI artifact, never a release, so the title bar
names it: `Development build 0.0.1 (abc1234): not a release` in the caution colour, and Settings > About shows `Build 0.0.1 (abc1234)` on every build.
Chosen: a **run-time Authenticode check of the running exe** (`BuildIdentityFactory.HasAuthenticodeSignature`, which asks
`X509Certificate.CreateFromSignedFile` for an embedded signer), not a build-time constant in `release.yml`. Why: the constant would have to be
set by the same workflow whose signing step can silently no-op, so it could say "signed" for an unsigned exe; the file itself cannot.
It checks that a signature is present, not that the chain is valid (a revoked certificate still reads as signed; `signtool verify /pa` in the
verify job is what proves validity). The check is on the app's own binary (`ResolveOwnBinary`: the apphost exe beside the assembly, else the dll; `ProcessPath` only for a single-file publish), never the launching host, because `dotnet App.dll` makes `ProcessPath` Microsoft's signed dotnet.exe. Any failure to read reads as unsigned and is traced by class, so start-up never crashes. Chain validation (WinVerifyTrust) was judged not cheap (P/Invoke plus a revocation lookup at every start) and is not done. A `dotnet run` or CI artifact build is always a dev build. The sha stamp ignores git's stderr, accepts only 7 to 40 hex digits and otherwise stamps `unknown`.
The short sha comes from `StampGitRevision` in the App csproj (`git rev-parse --short=7 HEAD` into `SourceRevisionId`, so the informational version is
`0.0.1+abc1234`; a `-p:SourceRevisionId=` from CI wins, and a checkout without git shows no sha). The simulated-cloud banner (#69) and the sandbox-profile
badge (#638) are separate, and all three can show together.

**Prerequisites and uninstall (issue #264).**

- *WebView2.* The embedded viewer needs the Evergreen WebView2 Runtime. Windows 11 and current Windows 10 ship it, so the installer does
  not bundle it. The app checks when the viewer opens (`WebViewRuntimeDecision`, issue #117) and shows the runtime-missing card with one
  action, a link to Microsoft's Evergreen bootstrapper; a missing runtime never shows a blank pane. `vpk pack --framework webview2` would
  make Setup.exe install it up front: not set in `release.yml` today (that file is not part of this change), recorded as an option.
- *Portable mode (#144, #805).* An empty file named `portable.flag` makes `AppDataRoot.Resolve` use `<base>\data` for every piece of state
  (settings, `app.db`, logs, run inputs, the WebView2 profile, sign-in files) and nothing under `%LOCALAPPDATA%`. `<base>` is the folder of the exe,
  except in a Velopack install, where the exe runs from `<root>\current` and an update replaces that folder: there `<base>` is `<root>`, the folder
  holding `Update.exe`, and a flag placed inside `current` is ignored (an update would delete it). Order: `--profile`, then `DEG_DATA_DIR`, then
  `portable.flag`, then `%LOCALAPPDATA%\DNAEntropyGraph`. Portable is the real profile for that copy (no sandbox banner, no simulated cloud).
  - Results: the default output folder is `<base>\outputs`, not the host PC's Downloads. Paths under `<base>` are stored in `app.db`
    relative (`portable:outputs\run`, `DataPathMapper`, used by `RunRepository` for `Runs.OutputDir`) and expanded on read, so a stick that gets another drive
    letter still opens its results; a folder the user chose outside `<base>` is stored as given. Only `Runs.OutputDir` is written by code today
    (the `RunOutputs` and `RunInputs` tables are unused); a new path column must go through `DataPathMapper`.
  - Database: `journal_mode=DELETE` instead of WAL in portable mode, and on any removable or network drive (`SqliteDatabase.ChooseJournalMode`), because WAL's
    shared-memory file is unreliable on shares and leaves `-wal` files when a stick is pulled.
  - Registry: Open with (file associations) is refused in portable mode (`FileAssociationRefusal.PortableMode`, Settings says why) so no entry points at
    a stick. The notification registration (`AppNotificationManager.Register`) is a per-user registry entry that is needed for notifications to work;
    it already lives only while the app runs (`Unregister` on window close, `App.xaml.cs`), so it is kept and a crash can leave one stale entry that the next
    start overwrites. It names the exe path, so notification clicks only work for the copy that registered last.
  - If the data folder cannot be written (a copy under Program Files) the launch stops with portable-specific text (move the folder, or delete
    `portable.flag`) and never falls back to the real folder.
  - Limitation: sign-in files are DPAPI-encrypted for one Windows user on one PC, so they do not travel; `DpapiTokenStore.GetAsync` treats bytes it
    cannot decrypt as signed out and the user signs in again. History, settings and results (outputs on the stick) travel.- *Uninstall keeps the user's data by default.* The data folder is `%LOCALAPPDATA%\DNAEntropyGraph` (history, settings, saved input copies,
  logs, sign-in). Velopack's before-uninstall hook (`Program.OnBeforeUninstall`) now asks once, "Remove your data too?", default **No**
  (`UninstallDataPrompt`, a plain message box because the uninstaller has no window); only Yes deletes the folder
  (`UninstallDataCleanup`, tested with a fake prompt and delete). Result files in the output folder are never touched (Hard Rule 14).
  A prompt that cannot be shown, or a locked file, keeps or leaves the data and never fails the uninstall.
  **The hook's limit.** MEASURED 2026-10-15 from Velopack 1.2.161's `Velopack.xml`: `OnBeforeUninstallFastCallback` code that has not finished within **30 seconds** is
  terminated. So: a silent or non-interactive uninstall (`Environment.UserInteractive` false, or `DEG_SILENT_UNINSTALL=1`) asks nothing and keeps the data
  (`NotInteractive`); the message box is `MessageBoxTimeoutW` with a 10 second timeout, and a timeout counts as No; a Yes first renames the folder to
  `DNAEntropyGraph.removing-xxxxxxxx` (one atomic rename on the same volume) and then deletes that, waiting at most 12 seconds, so a kill leaves a clearly
  named leftover and never a half-deleted live folder (a leftover is safe to delete by hand). A folder that cannot be renamed is left exactly as it was.
  The dialog text comes from the resource loader, and falls back to embedded English constants (`UninstallDataStrings`) when the loader returns the key or
  throws, because the hook runs before `Application.Start`; a Guards test keeps the constants equal to the resw values. The overlap check compares the
  real locations (`FinalPath`: junctions and links followed, 8.3 names expanded) and is a refusal that never deletes. With the packId `DnaEntropyGraph.Desktop`
  (#712) the install root is `%LOCALAPPDATA%\DnaEntropyGraph.Desktop`, so the folders do not overlap and the question is asked.
- *Collision, MEASURED 2026-10-15 from the files:* `release.yml` packs with `--packId DnaEntropyGraph`, so Velopack installs into
  `%LOCALAPPDATA%\DnaEntropyGraph`, and the data folder is `%LOCALAPPDATA%\DNAEntropyGraph`. Windows paths are not case sensitive, so these are
  the same folder. THEORY (unverified): Velopack's uninstaller deletes its whole root, which would remove the user's data whatever they answered; and
  an update replaces only `current\`, so updates are safe. While the names collide the hook asks nothing (`SharedWithInstall`) because "keep"
  would be a promise it cannot make. The fix is a different `--packId` (for example `DnaEntropyGraphApp`), which is a `release.yml` change; see the issue
  filed for it. Until then ToTest verifies what an uninstall really leaves. Resolved by #712: the packId is `DnaEntropyGraph.Desktop`, so the hook asks.

## 4. Update channel

The in-app updater reads the GitHub **Releases** API, not the raw git tags. Two
consequences worth remembering: a **draft** release is invisible to the updater (so a
`release.yml` run must publish, not merely create, the release once every asset is
attached), and a **pre-release** is visible only if the update channel logic says so
(issue #145). The update check runs on
launch (a setting, default on) and via a manual "Check now" in Settings; it never
restarts the app during an active run, since a run continues on its own in the cloud
regardless of whether the app is open (see the "you can close the app" banner in
`ui_conventions.md` section 7).

**How it is built (issues #179, #145).** `UpdateViewModel` (Presentation) is one singleton shared by
the shell's `Update available: <version>` InfoBar (a non-blocking bar with a Restart to update button) and the
Settings > About and updates group (version, channel, the launch switch, Check now, the release notes).
It talks to `IAppUpdateSource`; the only implementation, `VelopackUpdateSource` (App), is
`UpdateManager(new GithubSource(repoUrl, null, prerelease: channel == Beta))`, so the Beta channel is
exactly `prerelease: true`, and Stable never sees a pre-release (the flag is not trusted alone: `UpdateChannelPolicy` also refuses a version with a semver pre-release tag on Stable, and a check has a 30 second deadline). Tests use a fake source. The release notes are the
GitHub release body (`NotesMarkdown`), shown as plain text. A copy the installer did not put there (a dev build, a CI artifact)
reports `NotInstalled` and tells the user to use the installer.

**Never mid-run.** Restart to update asks `IActiveRunProbe` (over `ActiveRuns`, the one registry of run drivers,
which the engine and the reattach both register in) before downloading and again after the download, because the
download can take minutes. While a run is active the command only says so; it does not queue a restart. The launch check
is silent on failure (no network is not the user's problem at start-up); only a press of Check now reports one.
Channel and the launch switch are saved in `settings.json` (`update_channel`, `update_check_on_launch`) and go back to
the defaults on Reset all settings. Changing channel drops an offer made on the other channel.

THEORY (unverified): that `GithubSource` fills `NotesMarkdown` from the release body, and that `prerelease: true` also
offers a newer full release (it should: Velopack picks the newest of both); both need a real installed build (ToTest).

## 5. Version lockstep

The app version, the worker version, and the git tag move together: a release is cut for
`vX.Y.Z`, the same string appears in the app's assembly version, in the worker's
`pyproject.toml` version, and in the container image tag
(`ghcr.io/raaif-yousuf/dna-entropy-worker:vX.Y.Z-cuda` /
`:vX.Y.Z-cpu`). The app additionally pins the container by **digest**, not by tag alone
(spec D5), so a floating tag being repointed later cannot silently change what bytes an
already-released app version pulls onto a VM. `provenance.json` (per
`science_and_formats.md`) and `status.json`'s `worker.version`/`worker.image` fields
(per `job_contract.md`) both record the running worker's exact version and digest, so a
support conversation about "which worker actually ran this job" never has to guess.

**Enforcement.** `scripts/check_version_lockstep.py` compares `app/Directory.Build.props`'s `<Version>` with
`worker/pyproject.toml`'s `[project].version` and exits 1 naming both files when they differ, or when
`Directory.Build.props` is missing (a guard that skips a missing file can never fail). `ci-docs.yml` runs it on
every PR through its `for script in scripts/check_*.py` glob, so a grep for its name in the workflow finds nothing.
With `--tag vX.Y.Z` it also requires the tag to equal both; `release.yml`'s `verify` job makes that call (#178).
MEASURED 2026-10-03: a planted pair (`0.0.1` vs `0.0.2`) printed `ERROR: version mismatch: worker/pyproject.toml says
'0.0.1', app/Directory.Build.props says '0.0.2'` and exited 1.

## 6. Container images (spec D6)

Two images per release: `ghcr.io/raaif-yousuf/dna-entropy-worker:<ver>-cuda` (built on an
NGC PyTorch base with Transformer Engine and flash-attn prebuilt, plus `evo2` and the
worker package) and `:<ver>-cpu` (worker and the mock predictor only, no GPU stack, used
for the smoke test and the CPU-only `v0.1` walking skeleton). Both are published to GHCR
only, publicly and anonymously pullable, built by GitHub Actions from a tagged commit with
provenance attestation. The app refuses to launch a worker image whose digest is not in
its own allowlist unless an explicit "developer mode" override is set (`cloud_design.md`
section 8's supply-chain note, and section 13 for the mechanism: `worker-images.json`
embedded in the App project, read by `PinnedWorkerImageProvider`, issue #458); this is what makes "the VM runs released bytes, not your
working tree" (CLAUDE.md's Critical Pitfalls) an enforced property rather than a
convention.

Model weights are **not** baked into the image; they live in the user's own bucket under
`cache/models/<id>/`, mirrored there by the worker after the first Hugging Face download
(`cloud_design.md`, and `job_contract.md`'s bucket layout). This keeps the image itself a
fixed, small, reviewable artifact independent of which model a given job happens to use.

Two documented fallbacks, neither the default: install-on-boot (the prototype's own
approach: `pip install evo2` plus a source build of flash-attn, roughly 10 minutes on an
L4, fragile because it puts PyPI/GitHub/Hugging Face in the critical path of every VM
boot) and a per-project pre-baked image (roughly 30 to 40 minutes to build once, boot-to-
running in about 2 minutes, a recurring storage cost of a few dollars a month per project,
and a re-bake required on every stack change). Both are explicitly post-v1 opt-in options,
not the shipped default, because the author-published container (D6) already gets the
fast-boot benefit of a pre-baked image without the per-project storage cost or the
re-bake maintenance burden.

## 7. What a release actually produces

**Image pinning (issue #458):** the app's pinned list is
`app/src/DnaEntropyGraph.App/worker-images.json` and ships **empty** in git. A release writes the just-built
`-cuda` and `-cpu` digests, keyed by the release version without the `v`, into that file **before** the app is built, or
every run in that build ends `no_worker_image`. `release.yml`'s `app` job does this with
`scripts/write_worker_images.py` (tested by `scripts/tests/test_write_worker_images.py`), and a test pins that the step
runs before `dotnet publish`. The file in git stays empty; the entry exists only in the release build.
The provider allows an image only if it is exactly the entry for the running app version and
the variant the run needs (`-cuda` for a GPU tier, `-cpu` otherwise); a pinned image of another
version or variant is refused (a `-cpu` image on a GPU VM has no torch). Developer mode
(`developer_mode` in settings.json, no UI) still accepts any digest-pinned reference and marks
the resolution `IsDeveloperOverride`. That flag is exposed but **not yet recorded**: App has no logger and the run record has no column for it, so nothing reads it today. `PinnedWorkerImageProviderTests` carries a placeholder
test (`ISSUE_480_...`) asserting the shipped list is empty; it goes red when #480 fills the list
and must then be replaced by a guard that the app's own version has both entries.

- `Setup.exe` (Velopack, signed) attached to a GitHub Release, plus the delta-update
  assets Velopack generates alongside it.
- `dna-entropy-worker:<ver>-cuda` and `:<ver>-cpu` published to GHCR, with their digests
  recorded in the release notes so the app's pinned digest is auditable against what was
  actually published.
- Release notes drawn from `docs/sprint_log.md`'s entries since the previous tag.

The exact command sequence and verification steps for cutting one are in
[`release_runbook.md`](release_runbook.md); this doc is the "why it is built this way",
that one is the "how to do it right now".

## 8. Supply-chain notices and scanning (issue #34, #197)

**`THIRD-PARTY-NOTICES.md`** is generated, never hand-edited, by
`scripts/gen_third_party_notices.py`, and `scripts/check_third_party_notices.py` fails CI
if the committed file disagrees with a fresh regeneration (Hard Rule 16: regenerate and
commit in the same commit as any dependency change). The generator enumerates:

- Every .NET package (direct + transitive) reachable from the six shipped projects
  (`App`, `Core`, `Cloud`, `Persistence`, `Presentation`, `LocalEngine` - never the test
  projects or the `CloudCli` dev tool), via `dotnet list package --include-transitive`
  and the already-restored package's own `.nuspec` in the local NuGet cache. No network
  call.
- Every Python dependency `worker/pyproject.toml` declares: its core `dependencies`
  (always installed) plus the `genes`/`evo` extras (installed into the `-cuda`/`-cpu`
  container images and the local engine's pinned venv, per issue #301's own scoping) count
  as shipped; the `dev` extra (pytest, jsonschema, hypothesis) does not (issue #402's
  recorded decision: Hard Rule 21 scopes to shipped code). Licence data comes from
  `worker/.venv`'s own installed package metadata (PEP 639 `license_expression`), never a
  network call.

A shipped dependency's licence is classified **allowed** (MIT/Apache-2.0/BSD-family),
**denied** (GPL/LGPL/AGPL with no recorded exception - fails the check), **denied with a
recorded carve-out** (currently only `pyrodigal`, GPL-3.0, per issue #301's owner
recommendation - not yet formally written into `docs/hard_rules.md` rule 21's own
carve-out line, which the generator's own output says plainly), or **unrecognized** (a
non-SPDX licence string with no curated override - also fails the check, never silently
passed). A **platform** scope (the Windows App SDK / WebView2 / Windows SDK build tools,
under Microsoft's own proprietary licence terms rather than a dependency this project
chose) is excluded from the gate entirely pending a scoping decision (issue #413,
DECISION). As of this session the one still-open, unresolved item the generator itself
reports is `evo2`'s licence, cited from `docs/tech_stack.md`'s own MEASURED entry rather
than independently re-verified (no network access, GPU-only, never installed on the
laptop) - the honest state, not silently passed.

**CI scanning** (`.github/workflows/codeql.yml`, `.github/dependabot.yml`): CodeQL runs
for Python, GitHub Actions, and (added this session, now that `app/` exists per #61) C#,
with an explicit `dotnet restore`/`build` step for the C# language rather than relying on
`autobuild`'s heuristics against an unpackaged, central-package-managed WinUI 3 solution.
Dependabot watches `github-actions`, `pip` (`worker/`), and `nuget` (`app/`) weekly; the
`docker` ecosystem is deliberately absent until issue #36's Dockerfiles need their own
update cadence (they already exist as of this session but the ecosystem entry itself is a
separate, not-yet-filed follow-up). `dependency-review-action` on pull requests
(deny-licenses matching Hard Rule 21's own list) would fail immediately today because the
repository's Dependency graph setting is off (issue #325, an owner-only action), so it is
not enabled. (The CI workflows do run on `pull_request`, MEASURED 2026-10-16, so #325 is the
only blocker.) Enabling it before #325 is done would create a permanently red check.

### The packId (#712)

**Decision: `--packId DnaEntropyGraph.Desktop`** (release.yml and ci-app.yml). MEASURED 2026-10-15 from Velopack source
(`src/bins/src/commands/install.rs`): the default install root is `<LocalAppData>\<manifest id>`, and `commands/uninstall.rs` runs
`remove_dir_contents(root_path)` on that root. The app data folder is `%LOCALAPPDATA%\DNAEntropyGraph` (`AppDataRoot.Default`). With the old
`--packId DnaEntropyGraph` the two were the same folder on NTFS (case-insensitive), so an uninstall would have emptied history, settings,
auth and saved inputs. The new id is a different folder, so the uninstaller never touches the data folder. Installer file names follow
the id (`DnaEntropyGraph.Desktop-win-Setup.exe`, `DnaEntropyGraph.Desktop-<ver>-win-full.nupkg`).
THEORY (unverified): the packId is immutable once shipped. It is the install folder, the update feed's package id and the nupkg file
prefix, so an installed copy would look for updates under the old id; the Velopack docs say only that it is "the unique ID of your
application". That is why it is fixed before the first public release.
`scripts/tests/test_release_workflow.py` fails if a `--packId` equals the data folder name ignoring case; the name is read from
`AppDataRoot.cs`, not hard-coded.

## 9. release.yml (issues #178, #518, #165, #255, #181)

Jobs, in order: `verify` (secrets, lockstep with the tag, tag is on `main`), `images` (matrix `cuda`/`cpu`), `wheel`, `app`
(signing secrets, READ token), `upload` (write token, no secret), `publish`, `verify-reminder`. `verify` and `app` run under the
`release` GitHub environment (tag rule `v*`, required reviewers, environment-scoped secrets; owner steps in #703 and
`release_runbook.md` section 0). Every checkout sets `persist-credentials: false`. Steps are addressed by exact name in the test, and
the app job order (sign, pack, sign Setup.exe, verify, stage, attest, hand off) and the publish order (attach, asset check, flip) are
pinned step by step. Structure is pinned by `scripts/tests/test_release_workflow.py`; the file is also linted with
actionlint (`uvx --from actionlint-py actionlint .github/workflows/release.yml`). MEASURED 2026-10-15: actionlint exits 0 on it, `check_actions_pinned.py` passes, and `git ls-remote` confirmed each of the 13 distinct pinned SHAs exists and matches its `# vX.Y.Z` comment (two tags are annotated: the peeled commit matches).
Nothing here has run on a real tag yet; `docs/ToTest.md` carries the row.

- **OAuth client (#518).** `OAUTH_CLIENT_JSON` is the file Google's console downloads for the Desktop-app client. The `app` job
  is read by `scripts/write_oauth_client.py`, which keeps ONLY `installed.client_id` and `installed.client_secret` (validated
  non-empty strings; every other field is dropped), masks both before printing or writing anything, and writes
  `oauth_client.local.json` into the publish folder next to the exe, which `OAuthClientLoader.DefaultCandidates` checks first, so
  `vpk pack` carries it in the payload. It never touches git and is never an artifact on its own. The signed installer payload does
  hold it, and the draft/public release is public by design (Google says a Desktop-app client secret is not confidential,
  CLAUDE.md Hard Rule 12). A job cannot read another job's disk, so the `app` job hands the staged release files to the `upload` job
  through ONE artifact, `release-files` (path `stage/*`, one day of retention). DECISION (agent-made, reversible): this is the only
  artifact that may carry packages; `test_release_workflow.py` forbids every other upload-artifact step from touching
  `app/releases`, `app/publish`, `*.nupkg`, `*.exe` or the OAuth file in any slash form. The alternative (one job with the write token
  and the secrets) was rejected so the job that builds the repo never holds contents: write.
- **SBOM and provenance (#165).** CycloneDX SBOMs (anchore/sbom-action, Apache-2.0) for the app publish folder (taken before the
  OAuth file is written) and for each image digest. `actions/attest` (MIT) attaches SLSA provenance and the SBOM as GitHub
  attestations: to the installer and nupkg, and to each image in GHCR (`push-to-registry`). The images are also built with
  buildkit `provenance: mode=max` and `sbom: true`. Check with `gh attestation verify <file> --repo Raaif-Yousuf/DNA-Entropy-Graph`.
- **Signatures.** After signing, every signed file (the exe, every `DnaEntropyGraph.*.dll`, `Setup.exe`) must pass `signtool verify /pa`,
  `Get-AuthenticodeSignature` status `Valid`, AND carry the owner's identity in its signer subject (`RELEASE_SIGNER_SUBJECT`, an
  environment variable). A file signed by someone else fails the build.
- **Which files are this release (#178, #179).** MEASURED 2026-10-15 from velopack 1.2.161 source (tag `1.2.161`, which exists on
  NuGet): `vpk download github` fetches only the latest FULL nupkg (no feed file); `vpk pack` writes `assets.win.json` naming what it
  produced; `vpk upload github` uploads exactly the files that names, then `releases.win.json` and, for the default Windows channel,
  a legacy `RELEASES`. So the feed names to expect on a release are `releases.win.json` and `RELEASES`. A prior release's nupkg left
  in `app/releases` is never uploaded, but a `*.nupkg` glob would have attested it: `scripts/release_assets.py stage` reads
  `assets.win.json` instead (refusing a nupkg of another version, requiring the full nupkg and `Setup.exe`), and `check` verifies the
  published asset NAMES, anchored (the full nupkg of this version, not only a delta).
- **Containers (#255).** The `images` job runs `scripts/release_dockerfile_base_pinned.py` (named `release_*`, not `check_*`, so premerge and ci-docs do not run it on every PR: it gates a release) (tested against fixture Dockerfiles): EVERY
  `FROM` must be pinned by `@sha256` (a multi-stage file with one pinned stage fails; `FROM <earlier stage>` and `scratch` are
  exempt). Both Dockerfiles now carry a literal digest with the human-readable tag it was resolved from in a comment above the
  `FROM` (the NGC base digest was read from the registry's manifest list on 2026-10-16, a manifest HEAD with no pull). To move a
  base, resolve the new digest and change the tag comment and the digest together.
  `Dockerfile.cpu` bootstraps `uv` and the build backend (setuptools, wheel) from `worker/build-requirements.txt` (exact versions, sha256, PyPI JSON URLs recorded in the file, `--require-hashes`; every transitive build dependency is pinned too, today `packaging` via `wheel`, because `--require-hashes` refuses any unpinned requirement, MEASURED 2026-10-28 on PR #900 CI. To bump: read `requires_dist` of each pinned tool at `https://pypi.org/pypi/<name>/<ver>/json`, pin each new dependency with its wheel hash, and re-run `pip download --require-hashes -r build-requirements.txt --only-binary=:all: --platform manylinux2014_x86_64 --python-version 3.12 --implementation cp -d <tmp>`; `worker/tests/test_dockerfiles.py` guards the `packaging` pin), installs from `uv.lock` with its hashes kept: `uv export --locked` (fails if `uv.lock` is stale) then
  `pip install --require-hashes --no-deps`, so a file PyPI serves under a pinned version but with a different hash fails the
  build, and installs the project with `--no-deps --no-build-isolation` so no unhashed build requirement is fetched. The `Dockerfile.cuda` NGC digest could not be checked against a real pull offline: the first release run is what verifies it (ToTest row).
  **Summary: the CPU image is fully hash-locked; the CUDA image's base is pinned by digest but its Python dependencies are not yet locked** (`pip install \".[genes,evo]\"` and `pip install pytest`, built in pip's isolated environment, which fetches an unhashed setuptools and wheel): `uv.lock` cannot know which torch/evo2 the NGC base is compatible with. The lock is tracked in #921, deferred until #37's spike (open) has run on a GPU.
  The job then builds with
  `SOURCE_DATE_EPOCH` and `rewrite-timestamp=true`, pushes, then scans the pushed digest with Trivy (Apache-2.0, CI only, never
  shipped). Three steps, in this order: a SARIF report of every HIGH and CRITICAL finding (fixable or not, never fails),
  uploaded to code scanning under category `trivy-<variant>` (THEORY (unverified): code scanning accepts SARIF from a tag ref; the first release must run `gh api 'repos/Raaif-Yousuf/DNA-Entropy-Graph/code-scanning/analyses?tool_name=Trivy'` and see both `trivy-cuda` and `trivy-cpu` categories); then the gate, which fails on a **fixable HIGH or CRITICAL** finding
  (`ignore-unfixed`; an unfixed one is only in the report). The upload is `if: always()` and `continue-on-error`, so a code-scanning outage
  never blocks a release, while the gate step itself is never non-fatal. The publish job needs the gate, and a digest that failed the scan
  is never written into `worker-images.json`, so no released app can run it. The tag it pushed stays in GHCR.
  Accepted CVEs live in `.trivyignore.yaml` (`id`, `statement`, `expired_at`). `scripts/check_trivy_allowlist.py` (a
  `check_*` gate, so premerge runs it on every change, and the `images` job runs it before the build) fails on an entry that
  expires today or earlier, one more than 90 days out, or one missing a field (it accepts Trivy's optional `paths` and `purls` lists and a trailing `# comment` on the id and date lines). The gate runs with `ignore-unfixed`, so an unfixed CVE never fails it and must not be allowlisted: allowlist only a **fixable** HIGH or CRITICAL finding you cannot take yet, with a reason and an expiry. An expired entry fails `check_trivy_allowlist.py` always, and fails the scan too when the CVE is fixable (Trivy stops honouring it). **The first release may go red on a fixable HIGH or CRITICAL finding in a pinned base** (the pins are deliberately old snapshots); the remedy is a base digest bump (preferred, change tag comment and digest together) or an expiring allowlist entry. The Trivy and upload-sarif actions are pinned by commit sha. MEASURED 2026-10-16 (trivy-action at the pinned sha, `entrypoint.sh` lines 22-73, https://raw.githubusercontent.com/aquasecurity/trivy-action/ed142fd0673e97e23eac54620cfb913e5ce36c25/entrypoint.sh): a single `*.yaml` `trivyignores` value is exported as `TRIVY_IGNOREFILE` and parsed by Trivy as YAML (so `expired_at` is honoured); only a list of plain files is concatenated, and mixing the two is an error. Both scan steps pass exactly `.trivyignore.yaml` (tested).
  MEASURED 2026-09 (issue #255 thread):
  two clean `docker build`s of `Dockerfile.cpu` had identical installed package versions but different image digests, so
  "same tag gives the same digest" is NOT yet true; the timestamp rewrite is the next lever and is THEORY (unverified) until two
  release builds are compared. Worker image digests are written into `worker-images.json` by `scripts/write_worker_images.py`
  in the `app` job before `dotnet publish` (#480, already in place).
- **Draft until complete.** `vpk upload github` (in the `upload` job) without `--publish` creates a draft. The `publish` job downloads it, adds the
  wheel, SBOMs, `image-digests.txt` and `SHA256SUMS`, asserts every expected asset name is present (exact, anchored), and only then runs
  `gh release edit --draft=false` (a tag containing `-` becomes a pre-release instead of the latest release).
- **Reminder (#181).** The last job opens `Verify <tag> on a clean VM` with the runbook's checklist, unless an issue with that
  exact title already exists, so re-running the workflow never opens a second one.
- **Not in the workflow.** The test suites. A release is cut from a commit whose `ci-app` and `ci-worker` runs were green
  (runbook section 0); the release workflow builds with `-warnaserror` through `dotnet publish` only.

Licences of the tools added (Hard Rule 21; none ship in the product): `docker/*` actions Apache-2.0, `azure/artifact-signing-action`
MIT, `actions/attest` MIT, `anchore/sbom-action` and Syft Apache-2.0, `aquasecurity/trivy-action` and Trivy Apache-2.0, `vpk` MIT.

## Related

[`release_runbook.md`](release_runbook.md) (the step-by-step checklist),
[`architecture.md`](architecture.md) (the project layout `dotnet publish` builds from),
[`threat_model.md`](threat_model.md) (why the container is pinned by digest and what an
attacker who compromised GHCR or the OAuth client id could and could not do), [Appendix A,
section 7](superpowers/specs/2026-09-18-appendix-a-app-design.md#7-testing-ci-distribution)
(the full CI job list and the distribution-decision reasoning this doc summarizes).
