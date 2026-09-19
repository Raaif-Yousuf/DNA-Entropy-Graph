# Release runbook: cutting a DNA Entropy Graph release

> Written 2026-09-19, before the thing it prepares for was done: no release has shipped
> yet, `app/` does not exist, and `release.yml` is not written. This is left as written per
> Hard Rule 18 rather than deleted, so the first real release has a checklist to follow and
> correct against, instead of inventing one under time pressure. The first person to
> actually run this end to end should update it with what was true, not what this draft
> guessed, and note the correction in section 7 below.

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
| `dotnet test` is green (once `app/` exists) | `dotnet test app/DnaEntropyGraph.sln --filter "FullyQualifiedName!~UiTests"` |
| No open `P0` issues remain | `gh issue list --label P0 --state open` (empty) |
| Azure Trusted Signing secrets are present on this repo | `gh secret list` shows `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE` (`OWNER_TODO.md` item 3; without these, `release.yml`'s own precondition assertion fails the build on purpose rather than shipping unsigned) |
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
automatically.

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

Pushing the tag triggers `release.yml` (`on: push: tags: v*`), which:

1. Asserts every signing secret exists (section 0 above); fails loudly and stops here if
   any are missing, rather than producing an unsigned artifact silently.
2. Runs the same build and test steps as `ci-app.yml`.
3. Publishes: `dotnet publish -r win-x64 --self-contained
   -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None`.
4. Signs with `azure/trusted-signing-action`.
5. `vpk pack` with signing enabled, producing `Setup.exe` and the delta-update assets.
6. Builds and pushes `dna-entropy-worker:vX.Y.Z-cuda` and `:vX.Y.Z-cpu` to GHCR, with
   provenance attestation.
7. `vpk upload github` as a **draft** release, then attaches the worker image digests to
   the release notes.
8. Publishes the release only after every asset is attached. This ordering matters: the
   in-app updater reads the Releases API, so a half-uploaded but already-published
   release would be visible and offered to users before every asset exists.

Watch the workflow run to completion (`gh run watch`, or the Actions tab); a failure at
step 1 or 2 needs a fix-forward commit and a new tag (never force-push an existing tag),
per this repo's own git safety rules.

## 5. Verification on a clean machine

The false pass this step exists to catch: **the workflow going green does not prove the
installer actually installs and runs** on a machine that has never had this app, its
dependencies, or a dev environment on it.

1. On a clean Windows 11 VM (no Visual Studio, no .NET SDK, no prior install of this
   app): download `Setup.exe` from the just-published release.
2. `signtool verify /pa Setup.exe` must accept the signature. This is the real proof for
   signing, not "the workflow's signing step reported success" (which, per
   `packaging_design.md` section 3, can silently no-op on a config problem class that a
   green workflow step would not itself have caught if the assertion in section 0 above
   had somehow been bypassed).
3. Run `Setup.exe`, complete first-run sign-in and setup with a real Google account, run
   `sample.gb` with the mock predictor on `e2-small` (the v0.1 walking skeleton's own
   defined observable). Confirm `sample.entropy.bedgraph` appears both on disk and in the
   embedded viewer.
4. Confirm the in-app "Check for updates" reports "up to date" against the version just
   installed (proves the Releases-API-based update check itself works, not just that the
   installer ran).
5. Confirm the Cloud page shows the VM as Stopped (or Deleted, per the run's after-task
   setting) after the run finishes. An orphaned RUNNING VM here is a release-blocking
   finding, not a follow-up issue.

Only once every check in this section passes on the clean VM does the release count as
verified; a green CI run alone is not sufficient evidence per this repo's own
verification discipline.

## 6. Rollback

- **Before the release is published** (workflow still running or failed before step 7
  above): delete the tag (`git push --delete origin vX.Y.Z` after confirming with
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
