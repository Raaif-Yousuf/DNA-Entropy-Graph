# Packaging design: installer, signing, update channel, version lockstep, container images

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

## 4. Update channel

The in-app updater reads the GitHub **Releases** API, not the raw git tags. Two
consequences worth remembering: a **draft** release is invisible to the updater (so a
`release.yml` run must publish, not merely create, the release once every asset is
attached), and a **pre-release** is visible only if the update channel logic says so
(none of this is wired yet; v1.0 ships stable-channel-only). The update check runs on
launch (a setting, default on) and via a manual "Check now" in Settings; it never
restarts the app during an active run, since a run continues on its own in the cloud
regardless of whether the app is open (see the "you can close the app" banner in
`ui_conventions.md` section 7).

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
(deny-licenses matching Hard Rule 21's own list) cannot actually run yet for two
independent, already-documented reasons: every workflow in this repo is
`workflow_dispatch`-only (the owner's on-demand-CI decision, so there is no `pull_request`
trigger for the job's `if:` condition to ever satisfy), and even a restored PR trigger
would fail immediately because the repository's Dependency graph setting is off (issue
#325, an owner-only action). Restoring the PR trigger before #325 is done would recreate a
permanently red check.

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
- **Containers (#255).** The `images` job runs `scripts/check_dockerfile_base_pinned.py` (tested against fixture Dockerfiles): EVERY
  `FROM` must be pinned by `@sha256` (a multi-stage file with one pinned stage fails; `FROM <earlier stage>` and `scratch` are
  exempt). The job then builds with
  `SOURCE_DATE_EPOCH` and `rewrite-timestamp=true`, pushes, then runs Trivy (Apache-2.0) on the pushed digest and fails on a
  fixable HIGH or CRITICAL finding. The publish job needs it, and a digest that failed the scan is never written into
  `worker-images.json`, so no released app can run it. The tag it pushed stays in GHCR. MEASURED 2026-09 (issue #255 thread):
  two clean `docker build`s of `Dockerfile.cpu` had identical installed package versions but different image digests, so
  "same tag gives the same digest" is NOT yet true; the timestamp rewrite is the next lever and is THEORY (unverified) until two
  release builds are compared. `Dockerfile.cuda` still builds from an unpinned NGC tag (#37), so a release cannot be cut until
  that base is pinned: the gate is working as intended, not a bug.
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
