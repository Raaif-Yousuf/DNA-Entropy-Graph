# Threat model: assets, trust boundaries, and what each level of access buys an attacker

**Status (2026-10-15): mostly design, two parts built.** The app exists; the sign-in code
(section 5a) and the diagnostics bundle's leak scrubber (section 5b) are built and tested. Not
yet: no real OAuth client (owner step, issue #21), so sign-in has never run against Google; no published container image; no signed
installer. Every control described below that touches Google Cloud has been exercised against
the in-memory fake only, never a real project. This doc is honest about a real tradeoff this project has made and
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
| The worker service account's credentials (metadata-token scoped, live only on a running VM) | `compute.instances.get/stop/delete` and `compute.zoneOperations.get` through the custom role `dnaEntropyWorker`, bound with an IAM condition that matches only instances named `deg-*` (a VM the user named anything else is outside it), and `roles/storage.objectAdmin` on the one results bucket only (every job's files in it, not one job's), per `cloud_design.md` section 7. If an organization policy forbids service accounts, the VM runs as the project's default Compute Engine account instead (issue #54): that account has the same two bindings added, but it may also hold a broad role (THEORY, unverified: in older projects often Editor; newer organizations do not grant it), so this row then understates what a stolen token can do, and the app says so in a yellow note | Create new VMs, touch any other bucket or project resource, escalate its own IAM role, read anything outside the one results bucket it was scoped to |
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

## 5a. Sign-in specifics (issue #48, implemented)

- **Flow:** authorization code with PKCE (S256) and a loopback redirect to `http://127.0.0.1:<random port>/`
  (RFC 8252). The redirect carries a random `state` that the app checks before it exchanges anything, so a
  request forged onto the loopback port is refused. PKCE means a code stolen from the redirect is useless
  without the verifier, which never leaves the process.
- **Client "secret":** a Desktop-app client secret is not confidential per Google. It is still never
  committed: it is read from `oauth_client.local.json` (gitignored in dev, injected by CI in a release).
  The loader looks next to the executable, in the app data folder, and in `secrets/` or `app/secrets/` up to
  eight folders above the executable; a file planted there by something already running as the user buys
  an attacker only the ability to point the sign-in at their own OAuth client, which shows Google's consent
  screen under that client's name.
- **At rest:** `%LOCALAPPDATA%\DNAEntropyGraph\auth\<sub>.tok` is the whole token response encrypted with DPAPI
  (CurrentUser, fixed app entropy). `accounts.json` beside it lists `sub` and email for the switcher and holds
  no token. Account email is therefore on disk in clear; it is never logged or sent anywhere.
- **The id token is read, not verified.** It arrives over TLS straight from Google's token endpoint in answer
  to this app's own request, which OpenID Connect Core 3.1.3.7 allows to be trusted without a signature
  check. Only `sub` and `email` are read, only to key and label the local account. No authorization decision
  uses them: Google authorizes every call from the access token.
- **Sign out** revokes the refresh token at Google, then deletes the file. When Google cannot be reached the
  file is still deleted and the user is told the revoke could not be confirmed (the token then stays valid at
  Google until it is revoked at myaccount.google.com).
- **Prompt `select_account consent`:** `consent` forces Google to return a refresh token on every sign-in.
## 5b. The diagnostics bundle (issue #106, implemented)

**Settings > Diagnostics > Save diagnostics** writes one zip the user sends to support. What it contains, and what
it can never contain, is decided by code in `DnaEntropyGraph.Core/Diagnostics/`, not by care at the call site.

- **Allowlist of paths.** Only `settings.json`, `logs/*.log`, and under `runs/<job id>/` the `status.json`,
  `result.json`, `progress*.jsonl` and `logs/*.log` files are read. `auth/` (the DPAPI token files), `inputs/` and
  `pasted/` (copies of the user's sequences), the SQLite file and every output file are never listed, let alone read.
  A file is never read whole past 2 MB: a log is cut to its last 2 MB and marked truncated, a longer JSON file is left out
  with a note.
- **Allowlist of fields.** The run history is projected to ids, phase, target, error code, timings, hardware, costs and
  versions; the run name, the free-text error detail, the output folder, the project, bucket and VM names, notes and
  tags are never copied. `settings.json` keeps only `Theme` and `installation_id`; every other key is listed with its
  value omitted. Each of `status.json`, `result.json` and `progress.jsonl` has its own list of keys (`KeySpec`): a key
  not on the list is dropped with everything under it, and a kept string value must be short and made of plain
  characters. Free-text fields (`message`, `data`, `detail.input`, `detail.contig`, output paths, contig lists) are not on
  any list. The cost: a progress line's message is not in the bundle. MEASURED 2026-10-03 (issue #590): every `status.json` leaf on the list is a constant, a number, a worker-made id, a hardware string or an error code. The worker writes a user-derived string only into `detail.input`, `detail.contig`, `detail.inputs`, `vm.name`, `error.message`, `error.detail` and `error.remediation` (`worker/src/dna_entropy/worker/runner.py`, `status.py`), and none is on the list, so an imported run with no manifest cannot ship one. `DiagnosticsStatusLeafTests` pins the exact shipped leaf set: allowlisting a free-text leaf turns it red.
- **Scrub list.** Everything the bundle does not copy is also removed from the logs. The list is built from the user's
  email and Windows account name, each run's name, output folder, project, bucket, VM name and notes, every name and
  path in the run's manifest and options, and every key that looks like a name plus every string inside the dropped parts
  of status and result files (contig and record names). Each is scrubbed as written, as a file name and as a stem.
  Weakness: a data-named key with no digit, underscore, dot, hyphen or space (for example a bare `patient`) is not
  added to the list from a dropped subtree; it is still never copied from that file.
- **Text redaction** (every log line and kept string): any Windows path (drive letter or UNC, spaces allowed, including a folder path with spaces and no file extension) becomes `<path>`,
  and one under the user profile becomes `<user>`; sequence-file names (every extension in `SequenceFileTypes` plus
  `.fsa .ape .gp .sbd` and others, up to three words long) become `<file>`; email-shaped text becomes `<email>`;
  credentials (`ya29.` access tokens, `1//` refresh tokens, JWTs, `Bearer` headers, `GOCSPX-` client secrets and
  `access_token`, `refresh_token`, `id_token`, `client_secret` pairs) become `<token>`.
- **Sequence in any layout.** Letters A C G T U N and the other IUPAC codes, plus `-` and `*`, in either case. A run of 20 or
  more is replaced inline; a line that is, ignoring digits and spaces, at least 80% sequence characters is a sequence line,
  and a block of consecutive sequence lines holding 20 or more characters in total (GenBank `ORIGIN`, 10-mers, 60-column
  wraps, a sequence split over two lines) is replaced whole.
- **Final scan.** `DiagnosticsLeakScan` is a separate implementation, deliberately stricter than the redactor. It joins each
  entry, ignores whitespace and digits, so a wrapped, numbered or spaced sequence is still one run of 20 or more, reads the
  entry names as well as the text, and refuses on any credential shape. It also backstops the user's own identifiers: the
  scrub list (Windows user name, email, every harvested input, stem, contig, project, bucket and VM name) and the profile
  path are passed into it, and a case-insensitive hit refuses the zip. A value of 3 or more characters hits as a substring;
  a 2-character value (a user called "jo") only as a whole word, so "job" and "json" do not trip it and the redactor scrubs
  it the same way; a 1-character or all-digit value is not checked at all (it would match ordinary text and numbers). A leaf
  or stem derived from a path also matches as a whole word only (an output folder "out" must not refuse a bundle that says
  "timeout"). Independently of any list, it refuses on any email-shaped string (none is allowlisted yet) and on any drive
  letter or UNC path, since a path never survives redaction. The fixed placeholders (`<redacted>`, `<user>`, `<path>` and the
  like) are blanked first. A hit throws `DiagnosticsLeakException` naming the entry and the check that fired (`credential`, `path`, `email`, `identifier` or `sequence`, fixed words that carry no user text; Settings traces the check name only), and nothing is written. Job ids are handled by `JobIdWithholder`. A job id in the app's own shape (`yyyymmdd-hhmmss-xxxxxx`) is shipped as it is, and a run name (or bucket prefix `jobs/<id>/`) that is exactly that id is not put on the scrub list, because the history copies the id on purpose and the value tells a reader nothing the id does not (MEASURED 2026-10-03, issue #626: a run named by its own id made every save refuse with rule `identifier` on `run-history.json`; a real cloud run's `jobs/<id>/` prefix is the same shape). A job id in any other shape (an imported, hand-edited or older row; the owner's real run has one) may hold user text, so it is never shipped: it becomes `withheldid<n>` (n by run row, then by unmatched `runs/` folder) in `run-history.json` and in entry names such as `files/runs/withheldid1/status.json`, and it and the run's name and prefix stay on the scrub list. Inside file bodies the id is rewritten too, independently of the scrub list (which drops an all-digit or 1-character value, a record number among them): a withheld id is rewritten to its placeholder in the parsed string leaves of a status, result or progress file (never a key; a number leaf is rewritten only when its whole text is a withheld id, and then ships as a string, so an id of `1` leaves `"percent": 100` alone but turns `"id": 1` into `"id": "withheldid1"`; an escaped spelling such as `\u0031` is decoded first) and, in a log, only as a whole token (not glued to a letter or digit), case-insensitively (the not-glued guard applies only at an edge of the id that is a letter or digit, so `#1` is found in `job#1`), longest id first and in one pass so a placeholder is never rewritten again; a file that does not parse is omitted whole, never copied raw; a blank id (an empty or whitespace-only row id, which the app never writes) ships as `withheldid0` and is never part of the rewrite pattern; the match is by which id matched, so a character that only case-folds to an id (a Kelvin sign for `k`) is rewritten and never throws. Accepted residuals (MEASURED 2026-10-03, by test: the rewrite is token-bounded): a short or all-digit withheld id glued to other text (`job12` for id `12`) still ships, because the scrub list drops such values and the leak scan does not check them; and an id of `1` also rewrites the token in `seq-1` to `seq-withheldid1` (an over-rewrite, harmless); and the `jobId` leaf of any file under `runs/<id>/` is set to that folder's shipped id (so a status naming a different id cannot ship it). Residual risk (MEASURED 2026-10-15, by test, issue #637 review round 2): the number-leaf rewrite is a whole-value match, so a withheld all-digit id that is only a part of a number (`120` for id `12`) or spelled in another number form (`12.0`) is THEORY (unverified) not caught in a leaf, and a number that happens to equal a withheld id (`"percent": 12` for id `12`) is shipped as a string; the first is the same glued-to-other-text case as above, the second is a harmless over-rewrite. A standard id is exactly `\A[0-9]{8}-[0-9]{6}-[a-z2-7]{6}\z` (ASCII digits only, nothing after the last character). The exemption is equality only: an output folder path that ends in a standard id has the part before the id scrubbed, and any other name stays scrubbed and checked. Known safe-fail: run A's name equal to run B's standard job id still refuses the zip (B's id is shipped and A's name is scrubbed); the user is told to report the problem without a file. A 32-character-
  or-longer hex digest is exempt. Known cost: a free-text run note that equals a word the bundle itself uses ("failed")
  refuses the zip; refusal is the safe failure and the user is told to report the problem without a file. The zip is
  written to a temp file and moved into place; if the export is refused, cancelled (even before it started) or fails, the
  empty file the save picker created is deleted, and a destination that already held content is left alone.
- **Neutral entry names.** Every copied log is stored as `files/logs/log-NN.log` or `files/runs/<job id>/logs/log-NN.log`
  (numbered in the order of the original names); the original name is written nowhere in the zip. The job id is the app's
  own. `README.txt` is text from `Resources.resw`, passed in by the app.
- **Unreadable JSON.** A status, result or progress file (or a manifest) that cannot be parsed for any reason, duplicate
  keys included, is left out with a note (`<omitted: file is not valid json>`) and never blocks the export.- **Deliberately included:** the installation id (it is a label on every cloud resource and is needed to find them),
  the app, OS, .NET and WebView2 versions.
- **Not guaranteed:** the redaction is pattern-based. A new field is added to a key list, never to a blocklist. The app has no
  Serilog file log yet (issue #164, which will drop its `logs/` folder into the bundle with no change here), so today the
  bundle holds the worker and run logs and per-run files only.

## 5c. The network proxy (issue #239, implemented)

A proxy sees which Google hosts the app talks to (not the contents: the traffic is TLS, and the app does not trust a proxy to re-sign it silently, so a TLS-inspecting proxy whose root this PC lacks fails as `network_certificate` instead of being bypassed). The app never sets `ServerCertificateCustomValidationCallback`, so there is no setting that turns validation off. A manual proxy's password is DPAPI-encrypted for the Windows user in `auth\proxy.cred`, never in settings.json, SQLite, the logs or the diagnostics zip; the zip records only the mode. A password cannot be typed into the address box (the parser refuses an `@`).
## 5d. The cloud audit log (issue #245, implemented)

`logs/cloud-audit.jsonl` records every mutating cloud call (operation, a resource name the app made, request and operation ids, the
outcome class, the duration). It is an identifiers-only file: no sequence, no input file name, no object name below `jobs/<id>`, no
error message, no email, no billing account id (pinned by `CloudAuditTests`). It is local, size-bounded (about 2 MB across two files),
copied into the diagnostics zip through the same redactor and leak scan as a log, and a failure to write it never reaches the run.
It is evidence for support and for the user, not a tamper-proof record: anyone with access to the data folder can edit it
(section 4, local access), and Google's own Cloud Audit Logs are the authoritative record.
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
- The worker identity's condition (`resource.type != Instance || resource.name.contains("/instances/deg-")`) is
  THEORY (unverified, no live project) until `docs/ToTest.md`'s #54 row is run: if Compute does not evaluate it the way
  `cloud_design.md` section 7 says, the worker account could stop or delete a VM it should not reach. It still cannot create
  one, and it exists only for the life of a job's VM.
- The `deg-` condition limits the worker account to VMs named `deg-*`, not to the one VM the token belongs to (issue #54
  cold review, round 2). Every worker VM shares one account, so a compromised or buggy `deg-<job A>` token can stop or delete
  `deg-<job B>`, in this project only, and cannot reach a VM named anything else. That is inherent to one shared account plus
  a name-prefix condition. Narrowing it to the VM itself needs per-instance IAM (`instances.setIamPolicy` at create time,
  with a condition on the instance's own name); it is tracked as a follow-up issue under #56 and is not built.
- The default-Compute-account fallback (issue #54) gives a worker VM an identity with whatever that account already holds,
  possibly Editor (THEORY, unverified: older projects often, newer organizations not). The app cannot narrow it and says so in the wizard. It also grants `roles/storage.objectAdmin` on the results
  bucket to the default account, which every workload in the project that runs as the default account holds, so any such VM
  (not only the app's) can read, overwrite and delete every job's results in that bucket. The yellow note says so, and that the default account may hold a broad role.
- THEORY (unverified): Cloud Storage answers a failed precondition (an etag that did not match) with HTTP 412, and an
  organization-policy refusal with 412 too; the app treats a 412 as a conflict only when it positively says so (reason
  `conditionNotMet` or "precondition" wording, no `constraints/`), and every other 412 stays an org-policy refusal that is not
  retried. If a real etag conflict is worded differently the bucket binding fails as `org_policy` (safe: nothing is bound,
  the user sees the policy action); `docs/ToTest.md` has the row that captures the real shapes.
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
