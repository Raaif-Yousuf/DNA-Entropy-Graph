# OWNER TODO: things only you can do

Everything here is blocked on an account, a dashboard, a decision, or a person only you
can reach. Nothing in this file can be done by an agent. Each item links to the GitHub
issue that tracks it, that issue is the source of truth; this file is a standing,
priority-ordered index into it so a fresh session does not have to search for "what's
blocked on the owner" from scratch. Update the issue when you do the work, and delete the
row here in the same edit; a stale row is worse than no row, because the next reader
trusts it.

**Roughly 90 minutes of hands-on time this morning**, ordered by what unblocks the most
work, not by issue number: about 30 minutes for the OAuth client and GPU quota (section
1, both foundational, both worth starting even though quota approval itself finishes
later, asynchronously), about 20 minutes to skim and confirm or overrule 15 decisions
(section 2, most already have a written recommendation, a few just need a yes), 5 minutes
with a browser for the single cheapest high-value item on this list (section 3), and
under 5 minutes to text or Slack a lab member (section 4). Signing and the privacy policy
(section 5) are real work but do not block anything before v1.0, so they are last on
purpose, not forgotten.

---

## 1. Do these first, they block everything else

### 1a. Create the OAuth Desktop-app client and consent screen (#21)

The app cannot sign anyone in until an OAuth client exists in an owner-controlled Google
Cloud project, with the consent screen configured (External, Testing) and the
client-based APIs enabled there.

- [ ] Client id available to the build; client secret in
      `app/secrets/oauth_client.local.json` locally and as a GitHub Actions secret for CI
- [ ] Resource Manager, Service Usage, Billing, Quotas, IAM, Compute, Storage APIs
      enabled in the owner's project
- [ ] Lab members added as OAuth test users

**Blocks:** any real sign-in, and therefore every other cloud item below and most of the
GCP-side engineering work that has not yet been able to touch a real project tonight.

### 1b. GPU quota and a dev project for `cloud_gpu_test.ps1` (#24)

GPU tests only run on a labelled VM in the owner's own project; the dev laptop has no
CUDA-capable GPU (Hard Rule 15). Worth starting in the same sitting as 1a, since it needs
the same project and quota approval is not instant.

- [ ] Owner dev project with billing enabled, Compute API enabled, `NVIDIA_L4_GPUS >= 1`
      in at least one region, `GPUS_ALL_REGIONS >= 1`
- [ ] Project id recorded in `.claude/skills/working-on-gcp/SKILL.md` as
      `MEASURED <date>:`, this file records no project id itself, on purpose, so it
      cannot go stale; the skill file is the single place that number lives once it
      exists

**Blocks:** every GPU test, every real end-to-end worker run, and the whole `docs/ToTest.md`
queue this session seeded (every cloud path in this repo has, as of tonight, only ever
run against an in-memory fake).

---

## 2. The decisions, five minutes each

`DECISION` issues are questions you can usually answer async in a GitHub comment, no
dashboard, no hardware. Fifteen are open. Thirteen already carry a written
recommendation from planning or from tonight's session; read the one line below, and
either comment "agreed" on the issue (or just let it stand) or say what you'd do
differently. Two needed a recommendation written tonight because there wasn't one yet
(#226), or because the shipped work has already overtaken the question (#17); those are
marked.

| # | Question | Recommendation | Your move |
|---|---|---|---|
| [#9](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/9) | Stop or Delete after a run? | Keep **Stop** (already the default); the 7-day idle sweep and per-installation VM reuse are the mitigation | Confirm, or overrule |
| [#10](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/10) | Spot VMs on by default? | **Off** through v0.2; measure real preemption on your own project; reconsider for v0.3 | Confirm, or overrule |
| [#11](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/11) | Publish OAuth consent to Production, or stay in Testing? | **Testing** through v0.2; submit verification and publish once the v0.3 wizard is stable (the demo video needs the real flow) | Confirm, or overrule |
| [#12](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/12) | Default max run duration and hard ceiling? | **4 h default / 24 h ceiling** | Confirm, or overrule |
| [#13](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/13) | Default cloud results retention? | **90 days**, user-changeable in Settings | Confirm, or overrule |
| [#14](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/14) | Ship evo2_40b and evo2_1b_base in v1? | **40B visible, flagged experimental; 1B hidden under Advanced**; neither blocks v1.0 | Confirm, or overrule |
| [#15](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/15) | .NET 10 + WASDK 2.x confirmed? | Whatever the week-1 spike actually builds, publishes self-contained, and passes the Velopack/loopback/toast checks; not yet run, since `app/` does not exist | No action until the spike runs |
| [#16](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/16) | Velopack vs MSIX confirmed? | **Velopack**, unless the same spike finds a real blocker with WinUI 3 unpackaged | No action until the spike runs |
| [#17](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/17) | Multi-record FASTA: all records? | **Already shipped** (all records is the default, #283, closed) matching the original recommendation exactly | **Propose to close**; nothing left to decide |
| [#18](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/18) | Single `cloud-platform` OAuth scope, or incremental? | **Single scope**; `docs/threat_model.md` section 3 has the fuller honest tradeoff this session wrote, if you want the long version before confirming | Confirm, or overrule |
| [#19](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/19) | Dedicated VPC, or the default network? | **Default network** for v1; dedicated network as a post-v1 hardening issue | Confirm, or overrule |
| [#20](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/20) | Archive the two prototype repos? | **Archive both**, once v0.1 ships (it has not yet) | No action until v0.1 ships |
| [#226](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/226) | AWS sign-in: Identity Center device flow, or access-key import? | **Written tonight, new**: device flow as the primary path (matches the no-static-credential posture the GCP side already has), access-key import as an explicit fallback for accounts with no Identity Center set up; how common that is among lab AWS accounts is genuinely unmeasured, worth a cheap check before committing to device-flow-only | Read the full comment; confirm, or overrule |
| [#301](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/301) | pyrodigal (GPLv3) violates the MIT/Apache/BSD-only rule | **A scoped carve-out** (option c): GPLv3 stays confined to the worker's `[genes]` extra and the container images, default-on with a build-argument off-switch, never in `app/`; full obligations checklist is on the issue | Confirm, or overrule |
| [#302](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues/302) | Should memory sync publish personal session notes to this public repo? | **Already implemented** (type-filtered: only `project`/`reference` memory publishes by default, `--include-personal` is an explicit override); needs your sign-off that the filter is the right line to draw, not further engineering | Confirm, or overrule |

---

## 3. The cheapest high-value thing on this list

**Turn the pricing research from THEORY into MEASURED** (#266, reopened, labelled
`owner`). Every GCP price in `docs/research/2026-09-19-gpu-pricing-and-instances.md` (and
therefore in `docs/user_guide/06-costs-and-cleanup.md`'s worked examples) is marked
unverified because the automated research pass this session could not get a real browser
onto Google's own pricing pages. A person with a browser closes this in about five
minutes: open `cloud.google.com/compute/gpus-pricing` and
`cloud.google.com/products/compute/pricing/accelerator-optimized`, read the real numbers
for `us-central1`, and update the note's `THEORY (unverified)` tags to
`MEASURED <date>:` with the URL. Worth doing first among the small items, because the
number it fixes is the one a lab biologist reads before deciding whether they can afford
a run, and because it also resolves the sharper problem #303 found: the design spec's own
A100 Spot price range is off by two to four times from what every other source shows, and
nobody has checked which one is actually right.

## 4. The one thing an agent cannot do

**Hand `docs/user_guide/README.md` to a lab member who is not you** (#182). The guide is
written and reviewed for internal consistency, but its own Done-when explicitly needs a
real, non-owner biologist to read it and try a first run using only the guide, then have
their feedback recorded on the issue. No agent can stand in for this. This can happen any
time; the guide is ready now.

---

## 5. Real work, but does not block v0.1 or v0.2

### 5a. Azure Trusted Signing identity validation and GitHub Actions secrets (#23)

Unsigned installers hit Windows SmartScreen. `release.yml` (not written yet) will assert
the signing secrets exist and fail loudly if they are unset (a known footgun elsewhere is
this kind of step silently no-op'ing instead of failing; the assertion exists specifically
to prevent that, do not remove it once it lands).

- [ ] Trusted Signing account and certificate profile created; identity validation
      completed (this step is **not** instant, worth starting even though it is not
      urgent, so it is done by the time it is)
- [ ] `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
      `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE`
      set as GitHub Actions secrets on this repo
- [ ] A tagged pre-release produces a `Setup.exe` that `signtool verify /pa Setup.exe`
      accepts on a clean VM

**Blocks:** any signed release. v0.1 can ship unsigned with the explicit
`-p:SkipSigning=true` red-banner escape hatch while this is pending, but v1.0 lab release
cannot.

### 5b. Privacy policy page, homepage, and OAuth verification submission (#22)

Google's sensitive-scope verification needs a privacy policy URL on a verified domain, a
homepage, a demo video, and a scope justification. Until approved, the app is capped at
100 users total and, while the consent screen stays in Testing (see #11 above), user
tokens expire weekly.

- [ ] Privacy policy and homepage published (GitHub Pages is fine)
- [ ] Verification submitted with the demo video
- [ ] Approval recorded on #22 with the date, and this row deleted once #22 is closed

**Blocks:** more than 100 total users, and anything beyond a small lab group without
re-authenticating weekly. Per #11's recommendation, this is timed for v0.3, not now.

---

## What is NOT here, and why

The conventions donor's own `OWNER_TODO.md` (mined for this file's shape, never for its
content, see `docs/migration/2026-09-19-clair-conventions-inventory.md`) carried
licence-server deploys, an email-sending domain, a downloads-CDN config var, and a
campus-network reachability check, because that project shipped through a small
self-hosted backend. None of that exists in this repo's design: DNA-Entropy-Graph hosts
nothing but a public container image and a GitHub Releases feed (see `CLAUDE.md`'s
**App** line), so there is no server-side owner surface to track here at all. If one
appears later (a telemetry endpoint, a licence check), add it as its own numbered section
with its own issue link, the same shape as the items above.
