# Packaging design: installer, signing, update channel, version lockstep, container images

**Status: specification, not yet implemented.** `app/`, `worker/vm/`, and
`worker/Dockerfile.*` do not exist yet. This doc records the decisions already made (spec
D2, D3, D5, D6) and what each one is for, so a later change has to argue against a
reasoned choice instead of rediscovering the tradeoffs from nothing.

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

An unsigned build (7-day artifact retention) comes out of every `ci-app.yml` run for
review; only a tagged push runs `release.yml`, which is the only path that signs and
publishes.

## 3. Signing

Azure Trusted Signing, wired into `release.yml`. **The known footgun, worth repeating
here because it has bitten other projects silently:** the signing step can no-op quietly
if its secret is unset, producing an unsigned build that looks like a normal release
artifact until a user's SmartScreen warns them. `release.yml` therefore asserts every
required secret (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
`TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE`) exists
and fails the build loudly before attempting to sign, rather than letting the signing
step's own silent-no-op behaviour be the only signal. Do not remove this assertion to
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

## 6. Container images (spec D6)

Two images per release: `ghcr.io/raaif-yousuf/dna-entropy-worker:<ver>-cuda` (built on an
NGC PyTorch base with Transformer Engine and flash-attn prebuilt, plus `evo2` and the
worker package) and `:<ver>-cpu` (worker and the mock predictor only, no GPU stack, used
for the smoke test and the CPU-only `v0.1` walking skeleton). Both are published to GHCR
only, publicly and anonymously pullable, built by GitHub Actions from a tagged commit with
provenance attestation. The app refuses to launch a worker image whose digest is not in
its own allowlist unless an explicit "developer mode" override is set (`cloud_design.md`
section 8's supply-chain note); this is what makes "the VM runs released bytes, not your
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

- `Setup.exe` (Velopack, signed) attached to a GitHub Release, plus the delta-update
  assets Velopack generates alongside it.
- `dna-entropy-worker:<ver>-cuda` and `:<ver>-cpu` published to GHCR, with their digests
  recorded in the release notes so the app's pinned digest is auditable against what was
  actually published.
- Release notes drawn from `docs/sprint_log.md`'s entries since the previous tag.

The exact command sequence and verification steps for cutting one are in
[`release_runbook.md`](release_runbook.md); this doc is the "why it is built this way",
that one is the "how to do it right now".

## Related

[`release_runbook.md`](release_runbook.md) (the step-by-step checklist),
[`architecture.md`](architecture.md) (the project layout `dotnet publish` builds from),
[`threat_model.md`](threat_model.md) (why the container is pinned by digest and what an
attacker who compromised GHCR or the OAuth client id could and could not do), [Appendix A,
section 7](superpowers/specs/2026-09-18-appendix-a-app-design.md#7-testing-ci-distribution)
(the full CI job list and the distribution-decision reasoning this doc summarizes).
