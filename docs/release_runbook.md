# Release runbook: cutting a DNA Entropy Graph release

> Rewritten 2026-10-15 against the real `release.yml` (#178, #180). No release has been cut yet, so every step
> below is still unproven end to end. The first person to run it should correct what was wrong in place and add a dated
> note to section 7 (Hard Rule 18), not rewrite it as if it had always been right.

This is the checklist for cutting a tagged release: version bump, tag, watch the
workflow, verify the signature, install on a clean VM, publish notes, drain the relevant
`ToTest.md` rows. Follow it in order. A failure at any step names what to check before
moving on; do not skip a step because a later one "would probably catch it anyway."

---

## 0. Preconditions (what must be true; the command that proves each)

| Precondition | How to check |
|---|---|
| Working tree is clean, `main` is the branch, and `main` is up to date with `origin` | `git status --short` (empty) and `git log -1 origin/main..main` (empty) |
| `pytest -m "not gpu"` is green | `worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu" -q` |
| The app tests are green | From `app/`, each test project (`dotnet test <project>`); the solution-wide command hangs on CI (see `ci-app.yml`'s header) |
| `ci-app` and `ci-worker` were green on the commit you will tag | `gh run list --commit <sha> --limit 10`; the release workflow does not run the test suites, it only builds |
| Both worker Dockerfiles pin their base by `@sha256` digest | `Select-String '^FROM' worker/Dockerfile.*`; the `images` job refuses a release otherwise (#255 pinned both) |
| `.trivyignore.yaml` has no expired accepted CVE | `worker\.venv\Scripts\python.exe scripts\check_trivy_allowlist.py` prints `OK:`; an entry past its `expired_at` fails the image scan on purpose (#255) |
| `app/Directory.Build.props` and `worker/pyproject.toml` already say the new version | `python scripts\check_version_lockstep.py --tag vX.Y.Z` |
| No open `P0` issues remain | `gh issue list --label P0 --state open` (empty) |
| The `release` GitHub environment exists with a tag rule `v*`, required reviewers, and the signing secrets as ENVIRONMENT secrets (not repository secrets), plus the environment variable `RELEASE_SIGNER_SUBJECT` (owner steps: #703, `OWNER_TODO.md` item 5c) | `gh api repos/Raaif-Yousuf/DNA-Entropy-Graph/environments/release --jq ".protection_rules"` lists a reviewer; `gh secret list` (repository level) shows NONE of the names below; `gh secret list --env release` shows them |
| Azure Trusted Signing secrets are present in that environment | `gh secret list --env release` shows `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE`, and `OAUTH_CLIENT_JSON` (the Desktop-app client file from Google's console, issue #21; the first six are `OWNER_TODO.md` item 3; without these, `release.yml`'s own precondition assertion fails the build on purpose rather than shipping unsigned) |
| The GPU acceptance checklist has been run on this exact commit (v0.2 and later only) | `scripts/cloud_gpu_test.ps1` output, archived per `dev_commands.md` |

## 1. What changed since last time

`docs/sprint_log.md` is append-only and already has one entry per merge; read it back to
the previous release tag rather than re-deriving the change list from `git log`:

```
git log --oneline <previous-tag>..HEAD
```

Cross-check against `docs/sprint_log.md`'s entries in the same range; a commit with no
matching `sprint_log.md` entry means `docs/changelog.d/` was not folded for that branch
(Hard Rule 16 violation) and should be fixed before tagging, not after.

## 2. Version line (what it must be and why)

Decide `vX.Y.Z` (semantic versioning; a `v0.x` release is still pre-1.0 and may break
compatibility between minor versions). This exact string must appear, identically, in:

- The git tag itself (`vX.Y.Z`)
- `app/Directory.Build.props` (or wherever the app's assembly version is centralized once
  `app/` exists)
- `worker/pyproject.toml`'s `version` field
- The container image tags `ghcr.io/raaif-yousuf/dna-entropy-worker:vX.Y.Z-cuda` and
  `:vX.Y.Z-cpu`

This is the version lockstep rule from `packaging_design.md` section 5. A release where
these four strings disagree is the specific failure mode that rule exists to prevent;
check all four by hand before tagging if `release.yml` does not yet assert it
automatically. The first two plus the tag are checked by
`python scripts\check_version_lockstep.py --tag vX.Y.Z` (run it before tagging; `ci-docs` already runs the
no-tag form on every PR).

## 3. Pre-flight gates (commands and their real outputs)

Run each of these and read the actual output; do not assume green from habit.

```
worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu" -q
```
Expected: every test passes, 0 failures. A skip count above the known baseline (currently
2, both whole-module `importorskip` skips per `docs/tests.md`) is worth a second look
before proceeding, not an automatic block.

```
dotnet build app/DnaEntropyGraph.sln -c Release -p:Platform=x64 -warnaserror
```
Expected: builds clean with zero warnings treated as errors. A warning that was
previously suppressed and now trips `-warnaserror` is a real signal, not release-blocking
noise to silence with a blanket suppression.

```
python scripts/gen_manifest_schema.py --check
```
Expected: no diff between the committed `docs/contract/*.schema.json` and what the
current Core DTOs would generate. A diff here means the contract drifted from its schema
without the schema being regenerated in the same commit, i.e. `job_contract.md`'s
versioning rule was not followed for whatever changed.

## 4. The exact command sequence

```
git tag vX.Y.Z
git push origin vX.Y.Z
```

Pushing the tag triggers `release.yml` (`on: push: tags: v*`). Its jobs, in order (`packaging_design.md` section 9 has the why):

1. `verify` (needs your approval once, the `release` environment): asserts every secret in section 0 exists (names only, never values) and fails the run before anything is built
   if one is missing; runs `check_version_lockstep.py --tag`; checks the tagged commit is on `main`.
2. `images` (cuda and cpu in parallel): checks the base is pinned by digest, builds and pushes
   `ghcr.io/raaif-yousuf/dna-entropy-worker:vX.Y.Z-cuda` and `-cpu`, uploads a Trivy SARIF report (HIGH and CRITICAL) to the Security tab, runs the Trivy gate (a fixable HIGH or CRITICAL fails the release; an unfixed one is only reported), writes SBOMs and attestations. If the gate fails, read the Security tab category `trivy-cuda` or `trivy-cpu`, bump the base digest or the locked dependency, or add a dated entry to `.trivyignore.yaml` (at most 90 days out, with the issue that removes it).
3. `wheel`: builds the worker wheel.
4. `app` (approval again; read token only): writes the image digests into `worker-images.json`, publishes, writes `oauth_client.local.json` from `OAUTH_CLIENT_JSON`,
   signs the app binaries, `vpk pack`, signs `Setup.exe`, verifies EVERY signed file (chain and signer subject), stages exactly this
   release's files, attests them and hands them on.
5. `upload`: the only job with a write token besides `publish`; `vpk upload github` creates a **draft** release.
6. `publish`: attaches wheel, SBOMs, `image-digests.txt` and `SHA256SUMS`, checks every expected asset is present, and only then
   makes the release public (a tag with a `-` becomes a pre-release). This ordering matters: the in-app updater reads the
   Releases API, so a release that is public before every asset exists would be offered to users half-uploaded.
7. `verify-reminder`: opens `Verify vX.Y.Z on a clean VM` (one per version) with the checklist of section 5.

Watch the run (`gh run watch`, or the Actions tab). A failure in jobs 1 to 5 leaves no public release (the draft, if it was
created, stays a draft): fix forward with a commit and a new tag, and never force-push an existing tag. A failure in `publish` (including a missing asset)
leaves the draft in place; fix the cause and re-run the failed job.

## 5. Verification on a clean machine

The false pass this step exists to catch: **the workflow going green does not prove the
installer actually installs and runs** on a machine that has never had this app, its
dependencies, or a dev environment on it.

1. On a clean Windows 11 VM (no Visual Studio, no .NET SDK, no prior install of this
   app): download `Setup.exe` from the just-published release. Also run
   `gh attestation verify Setup.exe --repo Raaif-Yousuf/DNA-Entropy-Graph` and compare `SHA256SUMS`.
2. `signtool verify /pa Setup.exe` must accept the signature. This is the real proof for
   signing: the signing action can no-op on a bad configuration and still report success.
3. Run `Setup.exe` with no admin prompt. Complete first-run sign-in with a real Google account: Google's page must open
   (proves `oauth_client.local.json` shipped; `OAUTH_CLIENT_MISSING` means it did not). Run
   `sample.gb` with the mock predictor on `e2-small` (the v0.1 walking skeleton's own
   defined observable). Confirm `sample.entropy.bedgraph` appears both on disk and in the
   embedded viewer.
4. Confirm the in-app "Check for updates" reports "up to date" against the version just
   installed (proves the Releases-API-based update check itself works, not just that the
   installer ran).
5. Update in place: install the PREVIOUS release on the VM first, then publish or point it at this one and accept the update
   prompt. The app must restart into the new version with its run history intact. (Skip for the first release.)
6. Confirm the Cloud page shows the VM as Stopped (or Deleted, per the run's after-task
   setting) after the run finishes. An orphaned RUNNING VM here is a release-blocking
   finding, not a follow-up issue.
7. Uninstall from Windows Settings. Once #264 lands it asks whether to keep history and the local engine; run it twice, once
   keeping and once removing, and look under `%LOCALAPPDATA%\DNAEntropyGraph` each time.
8. Drain every `installer` row of `docs/ToTest.md` that this build can now prove (Rule 5 of that file), and append the dated
   note of section 7 below.

Only once every check in this section passes on the clean VM does the release count as
verified; a green CI run alone is not sufficient evidence per this repo's own
verification discipline.

## 6. Rollback

- **Before the release is published** (workflow still running or failed before the
  `publish` job made it public): delete the draft release on the Releases page (a draft the updater cannot see,
  but a re-tag would collide with it), then delete the tag (`git push --delete origin vX.Y.Z` after confirming with
  whoever asked for the release; this repo's git safety rules require this to be an
  explicit, discussed action, not a reflex) and re-tag once the underlying issue is
  fixed.
- **After the release is published** and a release-blocking defect is found: do not
  delete the published release (the updater may already have offered it to a test user).
  Instead, cut a new patch release (`vX.Y.Z+1`) with the fix, following this runbook from
  section 0 again. Mark the bad release "pre-release" in GitHub if it must stop being
  offered to new installs immediately, and open a `P0` issue describing exactly what was
  wrong and who might have gotten the bad build.
- The signed container images are immutable once pushed (pinned by digest, per
  `packaging_design.md` section 6); a bad worker image is fixed by a new tag and a new
  digest, never by overwriting the old one.

## 6b. Dev worker image (a branch build, no release)

A first real GPU run cannot wait for a signed release (`release.yml` needs a `v*` tag and eight signing and OAuth
secrets). `.github/workflows/image-dev.yml` is the small, separate path: `workflow_dispatch` only, `GITHUB_TOKEN` with
`packages: write` only, no signing, no environment, no Trivy or attestation (those stay `release.yml`'s gate). It builds
`worker/Dockerfile.cuda` and/or `.cpu` and pushes `ghcr.io/raaif-yousuf/dna-entropy-worker:dev-<shortsha>-<variant>`. A released
app never sees it: `worker-images.json` is written only by `release.yml` and is `{"images": []}` today.

1. Merge the workflow to `main` (a workflow cannot be dispatched until its file is on the default branch).
2. Actions > `image-dev` > Run workflow, pick the branch and `variant` (`cuda` default, `cpu`, or `both`), or
   `gh workflow run image-dev.yml --ref <branch> -f variant=cuda`. Runs are serialised (`concurrency: image-dev`).
   Expect 20 to 40 minutes for `cuda`; `Dockerfile.cuda` has never been built (#37), so the first run can fail in
   `pip install ".[genes,evo]"`. Read the log, fix, re-run.
3. The digest is in the job summary ("Digest: `sha256:...`") and the last log line is the full `name@sha256:...` reference.
4. Make the package public, once (the first push of a new package is PRIVATE and the VM pulls anonymously, `startup.sh`
   `docker pull`, so a private package fails with `IMAGE_PULL_FAILED`): github.com > your profile > Packages >
   `dna-entropy-worker` > Package settings > Danger Zone > Change visibility > Public. Check from a logged-out shell:
   `docker manifest inspect ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:<digest>`.
   WARNING, read first: public is effectively one-way (GitHub only lets you flip a public package back to private in some
   cases, and anyone may already have pulled it), it is visible to the whole internet and not just the org, and EVERY layer
   of the image becomes world-readable, including everything the Dockerfile COPYs from `worker/`. Pre-check: the COPY lines
   are `COPY pyproject.toml ...`, `COPY src ./src` and (cuda) `COPY tests ./tests`; confirm nothing secret is under them:
   `git ls-files worker/src worker/tests worker/pyproject.toml | Select-String -Pattern '\.env|secret|key|token|credential|\.pem'`
   must print nothing you did not expect, and `Select-String '^COPY' worker/Dockerfile.*` must show no new COPY. There is no
   `.dockerignore`, but the three COPYs above name exact paths, so only those are sent into the image layers.
5. Run the GPU tests on it: `pwsh scripts/cloud_gpu_test.ps1 -ProjectId <id> -ServiceAccount <worker sa email> -Image
   ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:<digest> -Apply -Lifecycle delete` (dry run first, without `-Apply`).
   Always pass the lifecycle: CloudCli's own default is `test`, not `delete`. See `dev_commands.md`.
6. To run it from the app instead: in `%LOCALAPPDATA%\DNAEntropyGraph\settings.json` set `developer_mode` to `true` and
   `worker_image` to the same `name@sha256:...` reference (`PinnedWorkerImageProvider`). Outside developer mode the app
   refuses any digest not in the allowlist.

Disk: `ubuntu-latest` has about 14 GB free; the NGC PyTorch base is larger, so the workflow runs the same free-disk step as
`release.yml` (removes dotnet, Android, ghc, CodeQL) and prunes docker images before building. THEORY (unverified): that is
enough; if the build dies with "no space left on device", switch the job to a larger runner. The base is
`nvcr.io/nvidia/pytorch:25.06-py3`; MEASURED 2026-10-28: its manifest-list digest is
`sha256:025d9b102b5436d4af8af58f12c6a46b7e5d16f19543b1d2cc4446bf2650b4f1` (public manifest HEAD against nvcr.io). The
workflow does not require a pinned base, so it works on `main` today; branch `feat/255-reproducible-image-r2` pins that exact
digest in `Dockerfile.cuda` and adds hash-locked installs to `Dockerfile.cpu`, and will take effect in this workflow with no
change to it. A dev image is not reproducible until then, and is never a release image.

Tags: `dev-<shortsha>-r<run_id>-<attempt>-<variant>` is unique per run, so a re-dispatch never overwrites an earlier tag, but
tags stay mutable in principle: only the `@sha256:` digest is ever passed to the app or the script. The app accepts only a
digest reference: `PinnedWorkerImageProvider.Resolve` (`app/src/DnaEntropyGraph.App/Services/PinnedWorkerImageProvider.cs`
:42-55) refuses anything outside the allowlist unless `developer_mode` is `true`, and even then requires
`StartupMetadata.IsPinnedImageReference`. Tests: `PinnedWorkerImageProviderTests` (a tag or free text is refused in developer
mode; an image outside the list is refused with developer mode off).

## 7. DONE notes (appended after, dated)

*(Empty. The first real release should append a dated entry here: what in sections 0
through 6 above was accurate, what needed correcting, and what surprised whoever ran it.
Per Hard Rule 18, a wrong step above gets corrected in place with a note here explaining
what changed and why, not silently rewritten as if it had always been right.)*

## Related

[`packaging_design.md`](packaging_design.md) (why the installer, signing, and update
channel are built this way), [`architecture.md`](architecture.md) (what `dotnet publish`
is building), [Appendix A, section
7](superpowers/specs/2026-09-18-appendix-a-app-design.md#7-testing-ci-distribution) (the
full CI job list `release.yml` extends).
