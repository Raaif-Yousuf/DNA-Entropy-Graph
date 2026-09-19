# Copy catalog: narration, phase titles, and the error catalog

**Status: specification, not yet implemented.** `app/` does not exist yet (issue #61), so
none of this is bound to a `.resw` file today. This document is the table the app
implements rather than a description of what it already shows: every string here is
written once, in the voice already established in `docs/user_guide/`, so the app speaks
one dialect instead of eight. CLAUDE.md rule 13 (user-facing copy rules) and rule 18
(mark theories as theories) both apply throughout; nothing here invents a fact `docs/user_guide/`
or the spec did not already establish.

**Resource key convention:** every key below is `PascalCase`, built by removing
underscores from the source name and capitalizing each word (`SIGNIN_EXPIRED` ->
`SigninExpired`). A key is a *prefix*: the actual `.resw` entries are `<Key>_Title`,
`<Key>_Body`, `<Key>_Action1` through `<Key>_Action3` for the error catalog, or a single
`<Key>_Text` for a narration line and a `<Key>_Title`/`<Key>_SubText` pair for a phase.
This is a naming convention this document fixes now so the first implementation has a
shape to land in; the guard each issue asks for (`working an issue's` Guards.Tests) checks
that every enum value has a matching key set, not that the exact suffix spelling above is
followed to the letter, so treat the suffixes as a strong recommendation, not a second
contract to version.

**Voice, restated from `docs/user_guide/README.md`:** the reader has never opened a cloud
console and is nervous about spending money that is not theirs. No jargon without the
plain phrase first. Never pretend a hard thing is easy: the quota step genuinely fails
for new accounts, the first run genuinely takes longer, a stopped computer genuinely
keeps costing money. Every error names one thing to do. No em dashes, anywhere in this
file's Text/Title/Body/Action columns.

---

## 1. `JobPhase` to title, sub-text, and icon

One row per `JobPhase` value from `docs/architecture.md` section 4 (plus the local-run
target's own `PreparingEngine`). This is the *static* title and sub-text; where the
sub-text is itself dynamic (narrated live), it names which table in section 2 supplies
the live text rather than repeating it here, so the two tables cannot drift apart.

| `JobPhase` | Key | Title | Sub-text | Icon |
|---|---|---|---|---|
| `Draft` / `Validating` | `PhaseValidating` | Checking your files | Making sure everything is ready before anything uploads. | checkmark-list |
| `Uploading` | `PhaseUploading` | Uploading | Sending your sequence to your own private cloud storage. | cloud-arrow-up |
| `Provisioning` | `PhaseProvisioning` | Starting a computer | Live text from the Zone ladder rows in section 2. | server |
| `Preparing` | `PhasePreparing` | Preparing the computer | Live text from the Cache and first-run rows in section 2. | gear |
| `Running` | `PhaseRunning` | Analysing | Live text from the Analysing rows in section 2. | chart-multiple |
| `Finalizing` | `PhaseFinalizing` | Saving results | Uploading your results to your own cloud storage. | cloud-arrow-up |
| `Downloading` | `PhaseDownloading` | Downloading | Bringing your results to this computer. | arrow-download |
| `Completed` | `PhaseCompleted` | Done | Your results are ready. Live tail text from the Lifecycle rows in section 2 (stopped / deleted / kept running for N minutes). | checkmark-circle |
| `PartiallyCompleted` | `PhasePartiallyCompleted` | Done, with some problems | {n} of {m} files finished; see which ones did not. | warning |
| `Failed(code)` | `PhaseFailed` | The error catalog's own `<Key>_Title` for that code (section 3) | The error catalog's own `<Key>_Body` for that code | error-circle |
| `Cancelling` | `PhaseCancelling` | Cancelling | Stopping the run. Anything already finished is kept. | dismiss-circle |
| `Cancelled` | `PhaseCancelled` | Cancelled | Partial results kept, if any were finished. | dismiss-circle |
| `PreparingEngine` (local target only) | `PhasePreparingEngine` | Preparing this computer | Getting the analysis software ready on your own graphics card. | gear |

`Icon` names a plain Segoe Fluent glyph concept (`FontIcon`/`PathIcon`, per
`docs/ui_conventions.md` section 5), not a literal glyph code point; the implementer picks
the exact glyph, since this table's job is to fix the *meaning* shown to the user, not the
font metrics.

---

## 2. Progress narration microcopy

Grouped by the moment they fire, matching the stage list in `docs/user_guide/03-run.md`
and the `Stage list row` column of `docs/ui_conventions.md` section 7. Every row is
checked against this table's own rule: no bare "zone", "quota", or "instance" in the
`Text` column (issue #246's own Observable), since a lab biologist reads a location name
or "a graphics-card computer" and has no reason to know either of those three words.

### Checking, uploading, saving, downloading

| Key | Text |
|---|---|
| `ProgressChecking` | Checking your files... |
| `ProgressUploading` | Uploading your sequence... |
| `ProgressSaving` | Saving your results to your cloud storage... |
| `ProgressDownloading` | Downloading your results to this computer... |

### The zone ladder, narrated honestly

The user is watching a screen do nothing, visibly, for minutes while the app tries one
location after another; every line below exists so that time reads as "working" instead
of "stuck". `{location}` is a real place name (e.g. "us-central1-a"), never the word
"zone" itself.

| Key | Text | Fires when |
|---|---|---|
| `ProgressZoneTrying` | Trying {location}... | One sequential attempt, per `docs/cloud_design.md` section 3 |
| `ProgressZoneNoCapacityHere` | {location}: none free right now, trying the next location... | A stockout at one location (continue) |
| `ProgressZoneNotAllowedHere` | {location}: not allowed to use a {gpuLabel} computer here yet, trying elsewhere... | A quota gap at one region, distinct from stockout, see below |
| `ProgressZoneTryingSeveral` | Trying {n} locations at once... | The ladder's parallel phase (3/5/7 at a time) |
| `ProgressZoneFound` | Found one in {location}. | A location wins |
| `ProgressZoneEscalatingTier` | No {tierA} computers free anywhere. Offering a bigger, pricier {tierB} computer instead. | Every location for the current GPU tier exhausted; this is an offer requiring a click (`GPU_STOCKOUT`'s "Try A100" action in section 3), never automatic |
| `ProgressZoneAllExhausted` | No {gpuLabel} computers free anywhere right now. This is temporary. | Total ladder failure; pairs with `GpuStockout` in section 3 |

**Quota is not a stockout, and the two must never share a message.** A stockout ("none
free right now") is transient and the app retries or tries another location on its own.
A quota gap ("not allowed here yet") is a one-time Google approval the user has to act on
themselves, in a completely different place (the setup wizard or
`docs/gcp_setup_manual.md`'s Step 4). `ProgressZoneNotAllowedHere` above is the in-flight
narration for a quota gap discovered mid-ladder; the user-actionable version is
`NoGpuQuota` / `QuotaNotEligible` in section 3.

### First run in a project, and the model cache

| Key | Text | Fires when |
|---|---|---|
| `ProgressCacheFirstRun` | First run in this project takes longer, about 8 minutes, while the analysis model is downloaded and cached. Every run after this one will be faster. | The bucket's `cache/models/<id>/` prefix is empty for this model; must appear **before** the wait, not after, per the coordinator's own framing: the user must not be left to conclude the app has hung |
| `ProgressCacheWarm` | Reusing the model that was cached from a previous run. | The cache prefix already has this model |
| `ProgressModelLoading` | Loading the analysis model onto the computer... | Worker `model-loading` stage |

### Analysing

| Key | Text |
|---|---|
| `ProgressAnalysingWindow` | {inputName}: record {i} of {n}, window {w} of {totalWindows}, {direction} |
| `ProgressAnalysingReducedContext` | {inputName}: this part of the sequence is shorter than the context length, so results here use less context than usual. | (the `science_and_formats.md` section 3 "reduced context" notice, surfaced live) |

### Lifecycle (starting, stopping, deleting, keeping alive)

| Key | Text |
|---|---|
| `ProgressLifecycleStopping` | Stopping the rented computer... |
| `ProgressLifecycleStopped` | Computer stopped. Its storage still costs a small amount until you delete it. |
| `ProgressLifecycleDeleting` | Deleting the rented computer... |
| `ProgressLifecycleDeleted` | Computer deleted. Nothing left running. |
| `ProgressLifecycleKeepingAlive` | Keeping the computer running for {n} more minutes in case you run something else. |
| `ProgressLifecycleKeepAliveExpiring` | Idle time is almost up; stopping the computer soon unless you start another run. |
| `ProgressLifecycleKeepAliveExpired` | Idle time is up. Stopping the computer now. |

`ProgressLifecycleStopped`'s "still costs a small amount" line is the same fact
`docs/user_guide/06-costs-and-cleanup.md` gives a full worked number for (about $15 a
month for the default disk); this narration line is deliberately short, and the Cloud
page (not narration) is where the actual estimate lives.

---

## 3. The error catalog

One row per error code. Source: `docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md`
section 7, which spec section 5.9 names as the authoritative full table; every code in
spec 5.9's own list is included, plus the additional codes Appendix B's table carries
that 5.9 does not enumerate by name (`CONSENT_INCOMPLETE`, `PROJECT_QUOTA`,
`BILLING_NO_PERMISSION`, `PERMISSION`, `QUOTA_DENIED`, `QUOTA_OTHER`,
`MODEL_DOWNLOAD_FAILED`, `CANCELLED`, `BUCKET_MISSING`, `NETWORK`), so the catalog is
exhaustive rather than requiring a second pass later. Wording is reconciled against
`docs/user_guide/07-when-something-goes-wrong.md` (cross-checked row by row); the 24
codes that page already covers keep that page's wording exactly, since it is the more
developed, more plain-spoken voice; the codes only in the appendix are brought to the
same voice here for the first time.

**Every row's body ends the same way in the app, not repeated per row below**: a "Copy
details for support" control showing the raw technical error, the job id, and the
timestamp, exactly as `docs/user_guide/07-when-something-goes-wrong.md`'s own header
promises ("Copy details, since it carries the exact technical error and your run's id").
The `Body` column below is the plain-language text only; the technical text never appears
in it.

| Code | Key | Title | Body | Actions |
|---|---|---|---|---|
| `SIGNIN_EXPIRED` | `SigninExpired` | Your sign-in expired | This happens on its own, roughly once a week, while the app's Google approval is still in its early testing phase. | [Sign in again] |
| `CONSENT_INCOMPLETE` | `ConsentIncomplete` | Permission wasn't granted | The app didn't get permission to manage your Google Cloud project. | [Grant permission] |
| `NOT_PROJECT_OWNER` | `NotProjectOwner` | You can't set up this project | You can use this project but not set it up. Ask the owner to make you an Owner, or create your own project. | [Create project] / [Copy request text] |
| `PROJECT_QUOTA` | `ProjectQuota` | Too many projects | You've reached Google's limit on how many Google Cloud projects you can have. | [Pick an existing project] / [Request more from Google] |
| `ORG_POLICY_BLOCK` | `OrgPolicyBlock` | Your organization blocks this | Your organization's Google Cloud settings block this action. | [Copy message for IT] |
| `NO_BILLING` | `NoBilling` | No payment method on file | This project has no billing account, so Google won't start any computer. | [Link billing] / [Create billing account] |
| `BILLING_NO_PERMISSION` | `BillingNoPermission` | Can't link this billing account | You can use this billing account but you're not allowed to link projects to it. | [Copy request for the billing admin] |
| `FREE_TRIAL_NO_GPU` | `FreeTrialNoGpu` | Free Trial can't use graphics cards | Free Trial accounts can't rent a graphics-card computer at all. Activate the full account; your free credit carries over. | [Open billing] |
| `API_DISABLED` | `ApiDisabled` | A Google service isn't switched on | Compute Engine isn't switched on in this project yet. | [Turn it on] |
| `PERMISSION` | `Permission` | Not allowed to create computers | Your account isn't allowed to create computers in this project. | [Copy request for the project owner] |
| `PERMISSION_ACTAS` | `PermissionActAs` | Not allowed to run as the worker identity | You are not allowed to run computers as the app's own worker identity. | [Grant the role] / [Use the default identity] |
| `NO_GPU_QUOTA` | `NoGpuQuota` | No graphics-card allowance yet | Your project isn't allowed any graphics cards yet. This is a one-time approval from Google, not a retry. | [Request one for me] / [Open the request page and copy the text] |
| `QUOTA_NOT_ELIGIBLE` | `QuotaNotEligible` | Google won't accept the request yet | Google won't accept graphics-card requests from this billing account yet, since it's new. | [Wait for a computer instead] / [Learn what to do] |
| `QUOTA_DENIED` | `QuotaDenied` | The request was denied | Google denied the graphics-card request. {stateDetail} | [Try another location] / [Open the request page] |
| `QUOTA_OTHER` | `QuotaOther` | Project limit reached | Your project has reached its limit for {metric} in {location}. | [Delete stopped computers] / [Try another location] |
| `GPU_STOCKOUT` | `GpuStockout` | No graphics cards free right now | No graphics-card computers are free right now in {n} locations. This is temporary. | [Retry in 10 minutes] / [Try the cheaper interruptible option] / [Wait for one to free up] / [Try a bigger computer, {price}/hour] |
| `NO_EXTERNAL_IP` | `NoExternalIp` | Your organization blocks internet access | Your organization blocks internet access for rented computers. | [Copy message for IT] |
| `VM_BOOT_TIMEOUT` | `VmBootTimeout` | The computer never became ready | The computer started but never became ready. | [Delete it and retry] |
| `GPU_NOT_VISIBLE` | `GpuNotVisible` | The graphics card didn't come up | The computer started but its graphics card never came up. | [Delete it and retry] |
| `IMAGE_PULL_FAILED` | `ImagePullFailed` | Couldn't download the analysis software | Couldn't download the analysis software onto the computer. | [Retry] |
| `MODEL_DOWNLOAD_FAILED` | `ModelDownloadFailed` | Couldn't download the model | Couldn't download the Evo 2 model. | [Retry] |
| `MODEL_NEEDS_HOPPER` | `ModelNeedsHopper` | This model needs a bigger, rarer graphics card | This model needs a specific, hard-to-get graphics card that you didn't pick. | [Switch to the default model] |
| `MODEL_OOM` | `ModelOom` | The sequence window is too big | The sequence window is too big for this graphics card's memory. | [Lower the context length] / [Use a bigger computer] |
| `BATCH_LIMIT_EXCEEDED` | `BatchLimitExceeded` | This batch is too large | This batch is {files} file(s), {nt} letters total. That's over the configured limit of {maxFiles} files or {maxNt} letters. Cost is GPU time, not file count, so a shorter batch costs less to run. | [Split into smaller batches] / [Raise the limit] |
| `INPUT_INVALID` | `InputInvalid` | Something is wrong with your input | Verbatim from the validator, already plain (see `docs/user_guide/03-run.md`). | [Fix the input] / [Turn on Treat as RNA] |
| `HEARTBEAT_LOST` | `HeartbeatLost` | The computer stopped reporting progress | The computer stopped reporting progress. It may just be slow, or it may have genuinely stopped. | [Stop it] / [Delete it] / [Keep waiting] |
| `VM_DIED` | `VmDied` | The computer stopped before finishing | The computer was shut down before finishing (hardware or a safety limit). Outputs so far were saved. | [Download partial results] / [Retry] |
| `SPOT_PREEMPTED` | `SpotPreempted` | The interruptible computer was reclaimed | The cheaper, interruptible computer was reclaimed before finishing. Outputs so far were saved. | [Download partial results] / [Retry on the full-price option] |
| `WORKER_CRASH` | `WorkerCrash` | The analysis program failed | The analysis program failed. Logs are attached. | [View log] / [Report a problem] |
| `RUN_TIME_LIMIT` | `RunTimeLimit` | Stopped at your safety limit | Stopped at your {n}-hour safety time limit. | [Raise the limit] / [Re-run] |
| `CANCELLED` | `Cancelled` | Cancelled | Cancelled. Partial results kept. | [Download] |
| `RETENTION_EXPIRED` | `RetentionExpired` | These results were deleted | These results were deleted after your {n}-day retention period. | [Re-run] |
| `BUCKET_MISSING` | `BucketMissing` | Your cloud storage is gone | Your results storage was deleted outside the app. | [Recreate it] |
| `DOWNLOAD_FAILED` | `DownloadFailed` | Can't reach Google Cloud | Can't reach Google Cloud right now. Check your internet connection. Your results stay in the cloud for your full retention period regardless. | [Retry] |
| `SPEND_CAP` | `SpendCap` | This would go over your spending cap | This run would go over your monthly warning cap. | [Run anyway] / [Change the cap] |

**`INPUT_INVALID`'s Actions column is not one fixed pair - it depends on which validator
check actually failed** (`science_and_formats.md` section 4 names five, in order: RNA,
empty, alphabet/ambiguity, length-vs-cap, minimum-length), the same way the zone ladder's
narration above is not one fixed line. `[Turn on Treat as RNA]` is only correct for the
RNA check (a `U` seen with the option off); showing it for, say, an ambiguity-policy
refusal would be a button that does nothing for that user's actual problem. The one added
here now that `ambiguityPolicy: "error"` (issue #249) is a real, user-reachable refusal:
a sequence containing an IUPAC ambiguity code (`N`, or one of the rarer codes), refused
because the run was configured to require every position be an unambiguous base call.
`Body` stays verbatim from the validator (it already names the code, its position, and the
count, per `science_and_formats.md` section 4) and `Actions` is `[Fix the input]` /
`[Allow ambiguity codes]`, the second one switching this run's `ambiguityPolicy` from
`error` to `keep` (or `mask`) rather than requiring the user to edit their file - the
`error` policy exists for a user who wants to be asked, not one who has no other option.

`NETWORK` (Appendix B's own alias for a subset of `DOWNLOAD_FAILED`-shaped failures) is
folded into the `DownloadFailed` row above rather than kept as a separate one, matching
`docs/cloud_design.md` section 5's error-classifier table, which already treats
`network` as one classification bucket, not two.

---

## 4. Cross-check discipline

This file and `docs/user_guide/07-when-something-goes-wrong.md` describe the same 24
spec-5.9 failures; a future change to either one's wording for a shared code must update
both in the same commit (CLAUDE.md rule 16), or the app and the guide will describe the
same failure two different ways, which is worse than either one alone since a user who
reads both loses trust in whichever one turns out to be stale. The ten additional codes
in section 3 above that the user guide does not separately cover (they are rarer,
project-setup-only failures already walked in `docs/gcp_setup_manual.md` or
`docs/user_guide/02-connect-google-cloud.md`) do not need a matching row there; this
catalog is the more exhaustive of the two on purpose.

## Related

[`docs/ui_conventions.md`](ui_conventions.md) section 7 (how `JobPhase` maps to buttons,
cost ticker, and history chip state, the UI-behavior half this file's title/sub-text
table complements), [`docs/cloud_design.md`](cloud_design.md) section 5 (the structured
error *classifier* that produces the codes this catalog gives copy to; the classifier and
the catalog are different layers on purpose, see that section's own note),
[`docs/user_guide/07-when-something-goes-wrong.md`](user_guide/07-when-something-goes-wrong.md)
(the reader-facing version of section 3, reconciled against it),
[Appendix B, section 7](superpowers/specs/2026-09-18-appendix-b-cloud-design.md#7-error-taxonomy-plain-language-one-action-each)
(the authoritative source table).
