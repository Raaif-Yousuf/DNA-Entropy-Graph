# Threat model: assets, trust boundaries, and what each level of access buys an attacker

**Status: specification, not yet implemented.** No OAuth client, no `app/`, no published
container exists yet. This doc is honest about a real tradeoff this project has made and
will keep having to defend: the app asks a lab biologist, someone with no reason to know
what an OAuth scope is, to grant a **broad** permission (`cloud-platform`) over their own
Google Cloud project. That is a large ask, and this document does not soften it.

---

## 1. The assets

In order of how bad it is if each is compromised:

1. **The user's sequence data.** Often unpublished research: a plasmid design, a
   CRISPR target locus, a locus from an as-yet-unannounced project. Confidentiality
   matters as much as availability here; a leak is not something a re-run fixes.
2. **The user's Google account's OAuth tokens** (specifically, the refresh token this app
   is issued, scoped `cloud-platform openid email`). Whoever holds a live refresh token
   for this scope can act as this app on the user's Google Cloud project: create, list,
   and inspect billing-relevant resources, until the token is revoked.
3. **The user's cloud spend.** A malicious or buggy actor with the ability to create GPU
   VMs in the user's project can run up real money, bounded only by whatever quota and
   budget the user's own project happens to have.
4. **The author's own public surface**: the OAuth client id, the GHCR container images,
   the GitHub Releases artifacts. None of these are secret by design, but their
   *integrity* (that the published container is the one actually built from the tagged
   commit, that the OAuth client id has not been swapped for an attacker's own) matters.

## 2. Trust boundaries

```
[ User's laptop ]  <-- DPAPI, CurrentUser only -->  [ OAuth token file ]
       |
       | HTTPS, user's own credential
       v
[ Google's OAuth/API surface ]  <-- author's OAuth client id (public), no client secret trusted -->
       |
       | scoped to the USER'S OWN project, never the author's
       v
[ User's Google Cloud project ]
   |                    |
   | Compute API        | Storage API
   v                    v
[ Per-job GPU VM ]  <-->  [ Results bucket, this user's own, private ]
   (author-published container, pinned by digest, pulled from public GHCR)
```

Nothing the app does ever transits any server the author operates, because there is no
author-operated server in this design at all (spec section 1: "no hosted API for
inference"). The only author-controlled resources in the entire flow are: the OAuth
client registration itself (an identifier, not a data path), the GHCR image (a published
artifact the user's own VM pulls, over the public internet, into the user's own project),
and GitHub Releases (the installer download). This is the load-bearing security property
of the whole design, and it is what section 8 of `cloud_design.md` calls "sequences only
ever touch the user's own bucket and VM."

## 3. Why the scope is a large ask, honestly

The app requests exactly one scope beyond `openid email`:
`https://www.googleapis.com/auth/cloud-platform`. This is Google's broadest cloud scope:
it is what `gcloud` itself requests, and it covers everything from creating projects to
setting IAM policy. The alternative, a narrower named set (Compute, Storage with
`devstorage.full_control`, Resource Manager, Billing, IAM), is **still six scopes, every
one of them classed "sensitive"** by Google (identical verification burden to the single
broad scope), and Google's own granular-consent UI lets a user untick individual scopes
from that set, which the app then has to detect and re-prompt for, a worse experience for
no real security gain, since the six together already grant nearly everything the one
broad scope does. Cloud Quotas and IAM specifically accept **only** `cloud-platform`, no
narrower equivalent exists for them at all. This project's honest assessment: the
narrower path does not actually reduce what the app *can* do to the user's project in any
meaningful way, only what it visibly *asks for*, and is documented as an
incremental-authorization alternative (request `compute` + `devstorage.full_control` +
read-only project/billing scopes at sign-in, escalate to `cloud-platform` only when the
wizard needs to create or modify something) that the owner can choose via a `DECISION`
issue, not the shipped default.

**What this means in practice for a user deciding whether to trust this app:** a
`cloud-platform`-scoped token, if it leaked, could be used to do essentially anything in
that user's Google Cloud project that a human with the same IAM role could do, not merely
what this specific app's UI exposes. The mitigations below (token storage, no server-side
copy, narrow custom IAM role for the *worker's own* service account, which is a
completely separate, much more limited credential from the user's own OAuth token) reduce
the blast radius of a compromise of the *app* or the *VM*, but do not reduce what a
compromise of the *user's own OAuth token itself* could do, because that is inherent to
the scope Google requires for this feature set to work at all.

## 4. What an attacker with each level of access can do

| Attacker has... | Can do | Cannot do |
|---|---|---|
| The user's OAuth refresh token (stolen from `auth\<sub>.tok`, or a leaked in-memory access token) | Everything the signed-in user's own IAM role permits in their Google Cloud project: create/list/delete VMs and buckets, read the results bucket's contents (the user's sequences), spend the user's quota and money, modify IAM up to what the user's own role allows | Access any *other* user's project (tokens are per-account); bypass the user's own project-level IAM (the token carries the user's actual permissions, no more) |
| The DPAPI-encrypted token file only, without the user's own Windows login (e.g. a copied file, no decryption key) | Nothing. `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` ties the encryption to the Windows user account that created it; the file is unreadable outside that account and machine | Decrypt the token without the originating Windows user's own login session |
| The worker service account's credentials (metadata-token scoped, live only on a running VM) | `compute.instances.get/stop/delete` on `deg-*` named instances only, `storage.objectAdmin` on this one job's bucket only, per the least-privilege IAM role in `cloud_design.md` section 7 | Create new VMs, touch any other bucket or project resource, escalate its own IAM role, read anything outside the one results bucket it was scoped to |
| The GHCR container image, read (it is public and anonymous-pull by design) | Inspect exactly what the worker does, which is intentional, this project ships no secret logic | Modify what a user's VM actually runs, since the app pins the image by digest and refuses an unlisted digest outside developer mode (`packaging_design.md` section 6) |
| A compromise of the author's GHCR publishing credentials | Publish a malicious image under this project's name, which a **new** install or an update would pull if the app's own digest allowlist were also compromised or bypassed | Retroactively change a digest an already-installed app version has pinned, without also compromising the app's own release/update mechanism |
| A compromise of the author's OAuth client id and "secret" | Very little beyond what is already public: Google explicitly treats a Desktop-app client's id and secret as non-confidential, since the security boundary for this client type is the user's own consent screen and loopback redirect, not client-secret confidentiality | Impersonate the app to Google in a way that bypasses the user's own explicit consent step; mint a token without the user completing sign-in |
| Physical access to a running VM's serial console (`instances.getSerialPortOutput`, used as a last-resort diagnostic per `job_contract.md` section 5) | Read boot-time and startup-script log output | Read sequence data (worker logs never contain sequence content, per `cloud_design.md` and CLAUDE.md rule 5's ASCII-console-output rule, which exists partly for this reason) |

## 5. What this app deliberately never sees or stores

- **No telemetry of any kind**, by default (spec section 9). An opt-in crash report opens
  a pre-filled GitHub issue for the user to review and submit themselves; nothing is
  auto-uploaded.
- **Worker logs never contain sequence content**, file names, or the user's email; only
  ids, stage names, and error classes (CLAUDE.md's Stack table, "Logs" row).
- **No SSH keys, ever** (`block-project-ssh-keys=true` on every VM this app creates), so
  there is no persistent remote-access credential living on any VM this app provisions.
- **No secrets committed to this public repository** (CLAUDE.md rule 12; the OAuth client
  id is the one deliberate, documented exception, since Google itself treats it as public
  for this client type).

## 6. Residual risk this document does not pretend to solve

- A user's own Windows account being compromised compromises the DPAPI-protected token
  the same way it would compromise any other CurrentUser-scoped secret on that machine;
  this is inherent to relying on Windows's own user-account security boundary, not a gap
  specific to this app.
- `cloud-platform` scope, once granted, is broad by Google's own design for this API
  surface; section 3 above is this project's honest acknowledgement that no scope
  narrowing available today closes that gap while keeping every wizard feature working,
  and that the incremental-authorization alternative is a real, documented tradeoff for
  the owner to decide on, not a solved problem.
- A quota-eligible new Google Cloud billing account is, in Google's own words, commonly
  ineligible for GPU quota (`cloud_design.md`'s quota section); this is an availability
  constraint on the user's side, not a vulnerability, but it is worth naming here since a
  user who cannot get a GPU at all is a common enough outcome that it should not be
  mistaken for the app withholding something.

## Related

[`cloud_design.md`](cloud_design.md) (the least-privilege IAM role and error taxonomy
that bound what a compromised worker credential can do), [`job_contract.md`](job_contract.md)
(exactly what travels through the bucket, and therefore what a bucket-level compromise
would expose), `docs/hard_rules.md` rule 12 (no secrets in the repo, the rationale and
carve-out), [Appendix B, sections 1 and
8](superpowers/specs/2026-09-18-appendix-b-cloud-design.md#1-oauth-and-consent) (the full
OAuth scope reasoning and the security/privacy section this doc expands on).
