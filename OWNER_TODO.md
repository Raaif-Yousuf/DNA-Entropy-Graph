# OWNER TODO — things only you can do

Everything here is blocked on an account, a dashboard, or a decision only you
hold the credentials or authority for. Nothing in this file can be done by an
agent. Each item links to the GitHub issue that tracks it — **that issue is
the source of truth**; this file is a standing index into it so a fresh
session does not have to search for "what's blocked on the owner" from
scratch. Update the issue when you do the work; this file's job is to point,
not to duplicate the checklist.

This file is a HAND file, not generated. If an item here is resolved, close
its issue and delete the row below in the same edit — a stale row here is
worse than no row, because the next reader trusts it.

This is a distinct category from `DECISION`-labelled issues, which are
questions the owner can usually answer async in a GitHub comment (no
dashboard, no hardware). Those are tracked with `gh issue list --label
DECISION --state open`; they are not repeated here.

---

## Ordered by what blocks a real end-to-end run soonest

### 1. Create the OAuth Desktop-app client and consent screen (#21)

The app cannot sign anyone in until an OAuth client exists in an
owner-controlled Google Cloud project, with the consent screen configured
(External, Testing) and the client-based APIs enabled there.

- [ ] Client id available to the build; client secret in
      `app/secrets/oauth_client.local.json` locally and as a GitHub Actions
      secret for CI
- [ ] Resource Manager, Service Usage, Billing, Quotas, IAM, Compute, Storage
      APIs enabled in the owner's project
- [ ] Lab members added as OAuth test users

**Blocks:** any real sign-in, and therefore every other cloud item below.

### 2. GPU quota and a dev project for `cloud_gpu_test.ps1` (#24)

GPU tests only run on a labelled VM in the owner's own project; the dev
laptop has no CUDA-capable GPU (Hard Rule 15).

- [ ] Owner dev project with billing enabled, Compute API enabled,
      `NVIDIA_L4_GPUS >= 1` in at least one region, `GPUS_ALL_REGIONS >= 1`
- [ ] Project id recorded in `.claude/skills/working-on-gcp/SKILL.md` as
      `MEASURED <date>:` — **this file records no project id itself**, on
      purpose, so it cannot go stale; the skill file is the single place
      that number lives once it exists

**Blocks:** every GPU test, every real end-to-end worker run.

### 3. Azure Trusted Signing identity validation and GitHub Actions secrets (#23)

Unsigned installers hit Windows SmartScreen. `release.yml` asserts the
signing secrets exist and fails loudly if they are unset (Critical Pitfalls:
this is a known footgun elsewhere — the step can silently no-op instead of
failing if the assertion is ever removed, so do not remove it).

- [ ] Trusted Signing account and certificate profile created; identity
      validation completed (this step is **not** instant — start it early)
- [ ] `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
      `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`,
      `TRUSTED_SIGNING_PROFILE` set as GitHub Actions secrets on this repo
- [ ] A tagged pre-release produces a `Setup.exe` that
      `signtool verify /pa Setup.exe` accepts on a clean VM

**Blocks:** any signed release; v0.1 can ship unsigned with the explicit
`-p:SkipSigning=true` red-banner escape hatch (Appendix C §8) while this is
pending, but v1.0 lab release cannot.

### 4. Privacy policy page, homepage, and OAuth verification submission (#22)

Google's sensitive-scope verification needs a privacy policy URL on a
verified domain, a homepage, a demo video, and a scope justification. Until
approved, the app is capped at 100 users total and, while the consent screen
stays in Testing, user tokens expire weekly.

- [ ] Privacy policy and homepage published (GitHub Pages is fine)
- [ ] Verification submitted with the demo video
- [ ] Approval recorded on #22 with the date, and this row deleted once #22
      is closed

**Blocks:** more than 100 total users, and anything beyond a small lab
group without re-authenticating weekly.

---

## Not yet blocking, but will be

**AWS sign-in model** (#226, `DECISION`, milestone v1.1 multi-cloud): IAM
Identity Center device flow vs. access-key import, and account scope. Not
blocking v0.1 to v1.0; only relevant once AWS support starts. Tracked as a
`DECISION` issue, not repeated here — see it directly when v1.1 planning
starts.

---

## What is NOT here, and why

The conventions donor's own `OWNER_TODO.md` (mined for this file's shape,
never for its content — see
`docs/migration/2026-09-19-clair-conventions-inventory.md`) carried
licence-server deploys, an email-sending domain, a downloads-CDN config
var, and a campus-network reachability check, because that project shipped
through a small self-hosted backend. None of that exists in this repo's
design: DNA-Entropy-Graph hosts nothing but a public container image and a
GitHub Releases feed (see `CLAUDE.md`'s **App** line), so there is no
server-side owner surface to track here at all. If one appears later (a
telemetry endpoint, a licence check), add it as its own numbered section
with its own issue link, the same shape as the four above.
