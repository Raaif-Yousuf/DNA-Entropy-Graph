# Cloud design: preflight, zone selection, error classes, labels, cost, termination

**Status: `DnaEntropyGraph.Cloud` exists.** Issues #49 (FakeGcp's scripted failures), #55
(`VmSpec`'s label/name guard), #57 (`CloudErrorClassifier`), #256 (`OperationPoller`), #257
(retry-safe mutations) and #58 (`CloudJobRunner`) landed against this doc as their
reference - `CloudJobRunner` is this table's first production caller: it runs the abort-vs-
continue rule in section 5 for real, over `FakeGcp`. Still not built: the escalating-
parallelism zone ladder (`GpuPlanner`, issue #86 - `CloudJobRunner`'s own zone list is a
plain sequential walk, not that ladder), the setup health checks (issue #204), and a real
(non-fake) gateway implementation against `Google.Cloud.Compute.V1` -
`DnaEntropyGraph.Cloud` today holds only `FakeGcp`. Authoritative detail lives in
[Appendix B](superpowers/specs/2026-09-18-appendix-b-cloud-design.md); this doc is the
"what a developer needs while implementing" summary with the tables filled in, not a
shorter copy of the whole appendix.

---

## 1. The one decision everything else follows from (D1)

**Per-job VM, found by label, never by fixed name.** The prototype kept one shared,
always-on VM (`dna-entropy-box`) alive 24/7 and reused it across every run. That design
cannot survive "two lab members may share one Google account" (an owner-stated
constraint, not a hypothetical): a fixed name collides the moment two runs overlap. Every
VM this app creates is named `deg-<jobId>` (spec D13), carries the full label set in
section 4 below, and is discovered by `labels.app=dna-entropy-graph` plus whichever of
`job-id`/`installation-id` the caller needs to filter on - never by a name comparison
against a constant. This single decision is why almost everything else in this document
(the zone ladder using the same name across parallel zone attempts, the Cloud page's
inventory query, the IAM condition on `deg-*`, the reconciler's lookup) is shaped the way
it is.

## 2. Preflight order

Every project-wide precondition is checked **in this order**, before any VM `Insert` is
attempted, because a failure at an earlier step produces the exact same symptom as a
failure at any later step (an `Insert` that never succeeds), and only checking in order
gives a user an actionable, specific answer instead of "no capacity anywhere":

```
project ACTIVE -> billing enabled -> Compute Engine API enabled (auto-enable, idempotent)
               -> GPU quota > 0 in at least one region -> bucket exists and is readable
```

MEASURED, carried forward from the prototype's own recorded field experience
(`docs/entry_points.md`'s seeded disproven-diagnosis row, and CLAUDE.md's Critical
Pitfalls): **billing off and the Compute API being off look exactly like a stockout**,
because GCP's own error text for all of these can be superficially similar ("could not
create the resource"). The whole point of checking in this fixed order - and of the
`CloudErrorClassifier` in section 5 separating them at all - is to turn that
one misleading symptom back into the four different causes it can actually be.

This preflight sequence is also the setup wizard's own step order (steps 3-6 in the table
below) and the health page's cached row set (`docs/superpowers/specs/2026-09-18-appendix-
b-cloud-design.md` section 2.2) - the same four checks run at first-run setup, before
every run (fast, cached 10 minutes), and inside the zone ladder's own abort logic
(section 3).

**Closed, issue #388:** `IProjectSetupGateway.GetProjectStateAsync` (returning a
`ProjectLifecycleState` of `Active` / `NotFound` / `Other`) is step 1's interface method,
`FakeGcp.WithProjectState(projectId, state)` scripts it, and `PreflightChecks.RunAsync`
runs it first, before billing, before the Compute API check, before quota - the full
documented order now has a caller and a test (`CloudJobRunnerTests
.Preflight_fails_the_run_before_any_upload_when_the_project_is_not_active`).

**Implemented** (`app/src/DnaEntropyGraph.Cloud/FakeGcp.cs`, issue #49): every project-wide
step this section names can be scripted on `FakeGcp` with a fluent one-liner -
`WithProjectState(projectId, state)`, `WithBillingOff(projectId)`,
`WithComputeApiOff(projectId)` (`EnableComputeApiAsync` clears it, mirroring the real API's
idempotent enable), `WithPermissionDenied(projectId)`, `WithOrgPolicyBlocked(projectId)` -
plus the per-zone/per-region steps 4-5's failure shapes: `WithZoneStockout(zone, times)`,
`WithQuotaExceededOnCreate(zone, times)`, `WithGpuQuota(region, accelerator, available)`
and `WithAllRegionsGpuCap(cap)` (the `GPUS_ALL_REGIONS` override from section 6 below),
plus `WithAlreadyExists`, `WithNetworkFailures` and `WithPreemption` for the failure shapes
outside the preflight chain proper. Every one of these throws the same
`CloudOperationException`/`CloudError` shape section 5's classifier consumes -
`FakeGcpScriptedFailureTests.cs` round-trips several of them back through
`CloudErrorClassifier.Classify` to prove the two agree.

**Implemented** (`app/src/DnaEntropyGraph.Core/Cloud/PreflightChecks.cs`, sequenced by `CloudJobRunner.RunAsync`, issue #58):
`CloudJobRunner.RunAsync` runs this exact preflight chain (steps 1-4; step 5, bucket-exists,
is folded into the Uploading phase's own `EnsureBucketAsync` call) before a run ever reaches
Uploading, and aborts to `Failed` on the first failure with the failing
`CloudErrorKind` recorded - see `CloudJobRunnerTests`'s `Preflight_*` tests. This is a
walking-skeleton preflight (one candidate region checked for quota, not the zone ladder's
full per-tier walk), not issue #86's eventual implementation.

## 3. Zone and GPU-tier selection (the ladder)

Reference implementation to port: `worker/legacy/cloud/orchestrator.py`'s `_create_box`
and its `_try_sequential`/`_try_parallel` helpers (see the exact behaviour breakdown and
filed issues in `docs/migration/2026-09-19-worker-migration-inventory.md`'s "Prototype
behaviours" section). The C# port is issue #86 (zone ladder) plus #210 (last-good-zone and
per-region quota memory, persisted per project instead of the prototype's
`%APPDATA%\dna-entropy\config.json`).

1. **Zone universe**: `AcceleratorTypesClient.AggregatedList` filtered to the accelerator
 the selected GPU tier needs, replacing the prototype's hardcoded ~90-zone list with a
 live query.
2. **Filter to regions with quota** `>= 1` for the tier's metric (and `GPUS_ALL_REGIONS >=
 1`, which overrides a positive regional number - see section 6). If the quota query
 itself fails, fall through to *all* zones rather than none - "unknown" is not "zero"
 (the prototype's `_apply_quota_health` made exactly this distinction; carrying it
 forward is the point of issue #86's "region quota pre-filter").
3. **Order**: last-good zone for this project first, then zones in the bucket's
 continent, then the rest of the world only behind an explicit "allow other continents"
 toggle (cross-continent egress of the cached weights costs real money per job).
4. **Escalating parallelism, per GPU tier**: 3 sequential creates (fast feedback for the
 common case) -> 3 parallel -> 5 parallel -> batches of 7 until the tier's zone list is
 exhausted. Parallel attempts use the **same VM name** (`deg-<jobId>`) in different
 zones - names are zonal, so this is safe - and any extra winner from a race is deleted
 immediately via an `aggregatedList` filtered to that exact name, except a VM that
 already existed before this job started (adopted, not created by this attempt).

 MEASURED (against `FakeGcp`, 2026-09-19, issue #257): issue #389's THEORY was correct -
 `FakeGcpRetrySafetyTests.MEASURED_a_second_zones_create_for_the_same_job_succeeds_
 independently_reproducing_issue_389` reproduces it deliberately: two `CreateVmAsync`
 calls for the same spec, in two different zones, both succeed, and
 `IComputeGateway.FindByJobIdAsync` (new, issue #257) then reports two VMs for one job id.
 Nothing at the gateway level stops this, on purpose - it is what a real, per-zone-unique
 name genuinely allows. The fix is caller-side, not gateway-side: `VmProvisioner.ProvisionAsync` (issue #58) calls `FindByJobIdAsync` and adopts whatever it finds BEFORE
 attempting any zone, every single time it runs - including the very first time - so a
 resumed ladder after a crash never reaches a second zone's create at all.
 `CloudJobRunnerTests.A_crash_after_provisioning_resumes_without_creating_a_second_vm`
 proves this end to end: a VM created in the ladder's second zone (as if the first had
 stocked out), a discarded runner instance, and a fresh one that finds and adopts the
 existing VM rather than creating a new one in the ladder's first zone. The escalating-
 parallelism ladder itself (issue #86) still needs to call `FindByJobIdAsync` the same way
 before its own first attempt, not just on an explicit resume path - `CloudJobRunner`
 does this by treating every call to its one entry point as resume-safe, with no separate
 "resume" method to forget to call.
5. **A project-wide error (billing, API disabled, permission, org policy) aborts the
 entire ladder at the first sighting**, with the exact structured error and one
 remediation action - retrying a different zone cannot fix a project-wide problem, so
 doing so only wastes the user's time and produces misleading "still trying" UI.
 Per-zone errors (stockout) and per-region errors (quota) do not abort; they are
 recorded and the ladder continues.
6. **Tier escalation** (L4 -> A100-40 -> A100-80) only happens after the current tier's
 entire zone list is exhausted, and **only** behind the user's "allow fallback GPU"
 toggle (default off) - a roughly 4x-6x price jump must be a conscious choice, never an
 automatic one, even on total stockout.
7. **Total failure**: one summarized message, one exact error example per failure kind
 seen (not one line per zone tried), plus the applicable next steps: retry in ~10
 minutes, try Spot, wait with Flex-start, or try a bigger GPU tier explicitly.

## 4. Labels and naming

Covered field-by-field in [`job_contract.md`](job_contract.md) section 2 (identifiers) -
not repeated here. The short version: `app=dna-entropy-graph` is the universal discovery
label; `job-id` and `installation-id` are what make a resource individually addressable
and correctly attributable to the PC/account that created it; `deg-` is the name prefix
the worker service account's IAM condition keys on (section 7 below).

**Implemented** (`app/src/DnaEntropyGraph.Core/Cloud/VmSpec.cs`, issue #55):
`VmSpec.EnsurePreconditions()` rejects a spec missing any of the six labels or
`maxRunDuration`/`instanceTerminationAction` **before** `VmName`/`ToLabels()` is ever
called, and additionally rejects a value the real Compute API would itself reject: a
label value outside `^[a-z0-9_-]{1,63}$` (five of the six labels - `app-version` is
sanitized instead, see issue #387's DECISION, since a semantic version like `0.1.0`
naturally contains a dot), and a computed `VmName` (`deg-<jobId>`) outside Compute
Engine's resource-name charset (a job id containing an underscore, for example, is a
valid label value but not a valid resource-name fragment - `VmSpecTests` has a case for
exactly this). `VmSpec` also gained a required `ProjectId` field (issue #386's DECISION)
and `IComputeGateway.CreateVmAsync` now takes the target `zone` as its own parameter,
matching `GetVmAsync`/`StopVmAsync`/`DeleteVmAsync`. `JobId.NewId()`
(`app/src/DnaEntropyGraph.Core/Cloud/JobId.cs`) generates the `yyyymmdd-hhmmss-<6
lowercase base32 chars>` convention; `VmSpec` does not require an externally-supplied job
id to match that exact convention, only that it is safe as a label value and inside the
computed resource name - the two are deliberately separate contracts.

## 5. Error classification

Structured-error successor to the prototype's stderr-substring `classify_create_error`
(`worker/legacy/cloud/gcloud.py`; the exact bucket conditions and the 20+ test cases that
specify them are preserved as JSON fixtures at
`tests/contract-fixtures/cloud_error_classification.json`, consumed by C# issue #213).
**Implemented**: `DnaEntropyGraph.Core.Cloud.CloudErrorClassifier.Classify(CloudError)`
(`app/src/DnaEntropyGraph.Core/Cloud/CloudErrorClassifier.cs`), checking structured
`Code`/`HttpStatus` first per the table below and falling back to the fixture's own
substring evaluation order only where neither is populated - see that file's own doc
comment for the exact two-stage algorithm and
`app/tests/DnaEntropyGraph.Cloud.Tests/CloudErrorClassifierTests.cs` for the fixture-driven
theory plus the structured-signal cases the fixture cannot express (it is stderr-only).
`CloudError` is Core's own Google-free DTO (Hard Rule 7): a real gateway in
`DnaEntropyGraph.Cloud` is responsible for mapping a real `Operation.Error`/
`RpcException`/`GoogleApiException` into it before this classifier ever runs - no such
mapping exists yet (there is no real, non-fake gateway implementation), only
`FakeGcp`, which constructs `CloudError` directly when scripting a failure.
**Consumed, issue #58**: `VmProvisioner.ProvisionAsync` (`app/src/DnaEntropyGraph.Core/Cloud/VmProvisioner.cs`)
is the first production caller - it classifies every `CloudOperationException` a create
attempt throws and applies exactly the abort-vs-continue column below (project-wide kinds
return the failure immediately; quota/stockout/other try the next zone; **network aborts the ladder too**, because another zone cannot fix an unreachable API and trying them all would end as a false stockout). The
Health page (#208) is still unbuilt and still has no caller of its own.

| Signal | Class | Ladder behaviour |
|---|---|---|
| `ZONE_RESOURCE_POOL_EXHAUSTED[_WITH_DETAILS]`, `RESOURCE_NOT_FOUND` for the accelerator in that zone, `UNSUPPORTED_OPERATION` mentioning the accelerator | `stockout` | Per-zone; continue the ladder |
| `QUOTA_EXCEEDED` (message names the metric) | `quota` | Per-region; skip that region. `GPUS_ALL_REGIONS` exhausted aborts straight to the quota-request flow |
| HTTP 403 `accessNotConfigured` / "has not been used in project" | `api_disabled` | Abort; route to the setup wizard's "enable APIs" step |
| HTTP 403 mentioning "billing" / `BILLING_DISABLED` | `billing` | Abort; route to "link billing" |
| HTTP 403 `forbidden`, `IAM_PERMISSION_DENIED`, mentioning "actAs" | `permission` | Abort |
| HTTP 412 / `CONDITION_NOT_MET`, message mentions `constraints/` | `org_policy` | Abort; copyable text for IT |
| HTTP 409 `alreadyExists` | `already_exists` | Adopt the existing resource rather than retry-as-failure |
| `HttpRequestException` / timeout | `network` | Retry with backoff (see `docs/migration/2026-09-19-worker-migration-inventory.md`'s note on the prototype's differentiated backoff, which does not carry over unchanged - the always-on keeper's infinite retry loop is exactly what D1 removes; only the *mechanical* polling backoff, issue #256, survives into the per-job model) |

The full plain-language, one-action-per-code user-facing table (`SIGNIN_EXPIRED` through
`SPEND_CAP`) is [Appendix B, section
7](superpowers/specs/2026-09-18-appendix-b-cloud-design.md#7-error-taxonomy-plain-language-one-action-each),
and that table is the error taxonomy; this section is the *classifier* that produces the
`billing | api_disabled | quota | stockout | already_exists | permission | org_policy |
network | other` bucket the taxonomy keys off of. They are two different layers and two
different issues (#57 for the classifier, the `.resw`-backed `ErrorCatalog` issue #176 for
the user-facing cards) on purpose: a classifier bucket is a stable, small enum a test can
assert against; a user-facing message is copy that can be rewritten without touching the
classifier at all.

## 6. Quota reading

Two distinct quota reads matter, and conflating them is a real bug shape:

- **Regional metrics** (`NVIDIA_L4_GPUS`, `NVIDIA_A100_GPUS`, `NVIDIA_A100_80GB_GPUS`,
 `NVIDIA_H100_GPUS`, ...): available quota per region, read once via `regions.list` and
 cached, not probed per zone.
- **`GPUS_ALL_REGIONS`**: a **project-wide** cap via `projects.get`. A value of `0` here
 **overrides** a positive regional number - a project can show `NVIDIA_L4_GPUS: 4` in
 `us-central1` and still be unable to create a single GPU VM anywhere, because the
 project-wide cap is the binding constraint. Checking the regional metric alone and
 concluding "should work" is the mistake this section exists to prevent.
- A quota **query failure** (API off, network, permission) must be represented as
 *unknown*, distinct from a successful query returning *zero* - the prototype's
 `list_region_gpu_quota` returning `None` versus `{}` is exactly this distinction, and
 the C# port must preserve it (see the comment on issue #86 filed as part of #287's
 survey).

## 7. Least-privilege IAM for the worker service account

A custom role (`projects/<p>/roles/dnaEntropyWorker`) grants exactly
`compute.instances.get`, `compute.instances.stop`, `compute.instances.delete`, and
`compute.zoneOperations.get`, bound at the project level with an IAM condition:
`resource.type == "compute.googleapis.com/Instance" && resource.name.extract("/instances/
{name}").startsWith("deg-")`. On the results bucket, the worker SA gets
`roles/storage.objectAdmin` and nothing else. No `logging.logWriter`, no SSH-related
permission (SSH itself is never used - see section 9). The signed-in user separately needs
`iam.serviceAccounts.actAs` on this SA (Owner/Editor have it implicitly; a bare Compute
Admin role does not, which is the `PERMISSION_ACTAS` error class).

## 8. Termination semantics - the pitfall this project has already been burned by once

**`instanceTerminationAction=DELETE` fires only when *Compute Engine itself* stops the VM
at its `maxRunDuration` deadline - not when the guest OS runs `shutdown`.** MEASURED
against Compute Engine's own documented semantics (CLAUDE.md's Critical Pitfalls; the
originating incident is CLAIR issue #2908, referenced by the migration inventory as a
lesson explicitly carried over, without naming or quoting the donor repository further per
this repo's redaction rules). Consequences this drives directly:

- Every VM this app creates has `scheduling.maxRunDuration` set (default 4 h, max 24 h,
 user-configurable within that range) **and** `instanceTerminationAction=DELETE` as the
 backstop - but the backstop is not the primary cleanup mechanism.
- `worker/vm/startup.sh` ends every path - success, failure, or cancellation - with the VM
 stopped or deleted **through the Compute API on itself**, using its own metadata-token
 credentials, per the job's `lifecycle.afterTask`. A stop is `POST .../instances/<name>/stop`;
 a delete is `DELETE .../instances/<name>` (a DELETE on the instance resource, not a POST to
 a `/delete` sub-path). It never calls `shutdown -h` as the primary mechanism.
- **Who applies it.** The worker applies the lifecycle itself when it can (worker issue #44)
 and then exits `10` (it stopped the VM) or `11` (it deleted the VM). Those exit codes mean
 "already applied": the script only uploads `logs/startup.log` and exits, and never calls
 the Compute API a second time. If the worker's own call failed it exits `0`/`2`/`3`
 (its ordinary outcome) and the script's `cleanup "$LIFECYCLE"` runs as the backstop.
 `lifecycle=keep` (issue #464, 0657dda): with `keepAliveMinutes > 0` the worker makes no
 Compute call and `startup.sh keep_hold` holds the VM for the window, measured from
 `/proc/uptime` and capped 10 minutes before `maxRunDuration`, then applies
 `afterKeepAlive` (default stop). Only a successful run (exit 0) is held; a failed or
 cancelled run applies `afterKeepAlive` at once (DECISION, agent-made, reversible: an idle
 GPU after a run that produced nothing is pure cost). The app cannot send a window yet
 (#476), so today every keep run is held for 0 minutes.
- A `shutdown -h +N` deadman is armed as a genuine last resort only, in case the API call
 itself is what failed.
- The app **independently verifies** the VM reached its terminal state after
 `result.json` is written - it does not simply trust that the stop/delete request it
 observed in the log actually completed.
- `maxRunDuration` is cleared and recomputed from the latest start on every VM start, so a
 resumed stopped VM gets a fresh deadline window, not the remainder of its original one.

## 9. Why there is no SSH

The prototype's `cloud/gcloud.py::ssh`/`scp` are explicitly **not** ported (see the
migration inventory's "Prototype behaviours" section). The bucket *is* the transport:
inputs travel to `jobs/<jobId>/input/`, the worker reads its manifest and writes its
status/progress/outputs to the same prefix, and the app never opens a remote shell on any
VM it creates. This removes an entire class of prototype-era concerns (host-key
auto-acceptance, SSH reachability as a false-positive health signal - "SSH-reachable is
not GPU-healthy" was the prototype's own hard-won lesson, now expressed as "the container
running with `--gpus all` and the worker reaching `model-loading` is the only real health
signal," per CLAUDE.md's Critical Pitfalls) without needing an equivalent replacement,
because the failure mode SSH reachability was approximating simply does not exist in a
bucket-mediated, no-inbound-port design. `block-project-ssh-keys=true` on every instance
makes this a property of the resource, not just an implementation choice nobody happens to
exercise.

## 10. Cost model (summary; full table in Appendix B section 6)

- A shipped `pricing.json` per release, refreshed best-effort from the Billing Catalog
 API at most once a day, with the shipped table as the fallback when that refresh fails
 or is stale. Seeded from [`docs/research/2026-09-19-gpu-pricing-and-instances.md`](research/2026-09-19-gpu-pricing-and-instances.md)
 (every figure there is THEORY (unverified), not vendor-confirmed, pending issue #303 and
 a real browser read of the vendor pricing pages) until issue #214's live lookup ships.
- **Boot disk**: `pd-balanced`, 150 GB for the default (7B) model tier, 300 GB for the 40B
 tier (spec section 5.4), autoDelete with the instance. At ~$0.10/GB/month (THEORY
 (unverified), same research note), this is ~$15/month (150 GB) or ~$30/month (300 GB)
 while the disk exists, whether the VM is running or stopped, which is why the app's
 7-day stopped-VM sweep (spec D10) exists as the backstop against a forgotten Stop, not
 just the after-task setting itself: see `docs/user_guide/06-costs-and-cleanup.md` for
 the user-facing version of this same number.
- A **live ticker** per running job: `(now - lastStartTimestamp) * hourlyRate / 3600 +
 disk`, labelled "estimate" everywhere it appears, because there is no billing-export
 access - every number in this app is a modeled estimate, never a real invoice figure.
- A **live ticker** per running job: `(now - lastStartTimestamp) * hourlyRate / 3600 +
 disk`, labelled "estimate" everywhere it appears, because there is no billing-export
 access - every number in this app is a modeled estimate, never a real invoice figure.
- A **pre-run estimate** from historical run durations for the same model tier (defaults:
 12 min fresh / 5 min warm for the 7B tier, before any real measurement exists -
 THEORY (unverified) until the first GPU acceptance run records real numbers).
- Thresholds: warn above a single-job estimate of $2 (user setting), warn at month-to-date
 above $25 (cap $50, user setting), a hard `maxRunHours` cap per job (default 4, max 24)
 enforced via `maxRunDuration`, and an explicit per-job confirmation before any A100/H100
 tier showing the hourly price.
- **"Nothing left running"**: `instances.aggregatedList` filtered to
 `labels.app=dna-entropy-graph`, checked on launch, on exit, and every 5 minutes, across
 every signed-in account/project on this PC - the single most important money guard in
 the app, and the direct successor to the prototype keeper's `main()` exit-time reminder
 ("the GPU VM is still RUNNING and billing... delete it when done").

## 11. The job runner and operation polling (issues #58, #256)

**Implemented**: `app/src/DnaEntropyGraph.Core/Cloud/CloudJobRunner.cs` (a sequencer over the collaborators listed in `architecture.md` section 3) turns one
`CloudJobRequest` into a finished run over `IComputeGateway`/`IStorageGateway`/
`IProjectSetupGateway`/`IQuotaGateway` (today, `FakeGcp` implementing all four - there is
no real gateway yet), driven by `JobStateMachine`'s legal-transition table over the
existing `JobPhase` enum (`app/src/DnaEntropyGraph.Core/JobPhase.cs`, from the #61
skeleton). Sequence: preflight (section 2's four checks, then the bucket) -> Validating ->
Uploading (the input bytes, then `manifest.json`) -> Provisioning (section 3's reconciler-
first zone walk) -> Preparing -> Running (wait for the worker's `result.json`, see
"Transfers" below) -> Finalizing (`result.json` read and judged) -> Downloading (the files
`result.json` lists, THEN Hard Rule 11: stop or delete per the request and independently
re-`GetVmAsync` to verify the terminal state actually landed, never just trusting the call
succeeded) -> Completed/PartiallyCompleted/Failed. The download comes first because the
bucket outlives the VM and the worker has usually ended its own VM by then (see "The wait,
the download and the lifecycle check" below).

**Transfers (issue #460).** Before this the request carried empty object keys and the
transfer was a stub: a user pressing Run uploaded and downloaded nothing. Now:

- `JobEngine` copies the chosen file under `%LOCALAPPDATA%\DNAEntropyGraph\runs\<jobId>\input\`
  (`LocalRunInputStore`, Hard Rule 14: the user's file is only read, never written next to)
  *before* any cloud resource exists, and a missing or unreadable file is recorded `Failed`
  with `input_missing`.
- `UploadAsync` streams that copy (a seekable `FileStream`, so the #258 retry pipeline can
  rewind and replay it) to `jobs/<jobId>/input/<name>`, then writes `manifest.json`
  (`WorkerManifestBuilder`, built from `RunOptions` per `job_contract.md` section 3) last, so
  its presence means every input is already there. The row records `Bucket`, `JobPrefix` and
  `ManifestJson`. A run resumed past `Uploading` never uploads again: the manifest is
  immutable once a worker may be reading it.
- While `Running` the runner polls `jobs/<jobId>/result.json` with `IStorageGateway.TryDownloadAsync`
  (null means "not yet") and looks at the VM between polls; the full rules are under "The wait,
  the download and the lifecycle check" below.
- `result.json` is read strictly (`WorkerResultReader`): anything that is not a recognised
  `done | failed | cancelled` is `worker_failed`, never success. A failed job, or a `done`
  job in which no input finished, is `Failed` (`worker_failed`) and the VM is ended;
  some inputs failing is `PartiallyCompleted`.
- Outputs go to a fresh `<output folder>\<input name>[_2]` (default Downloads), recorded as
  `OutputDir` so a resumed download reuses it. Each file is downloaded to `*.part`, checked
  against the byte count and SHA-256 from `result.json`, then renamed; a path that is not
  `output/<plain relative path>` is refused, since it comes from a bucket object.
  `download_failed` covers an unwritable folder or a network failure mid-download; a result path outside `output/`
  and a listed file that is not in the bucket are the worker breaking the contract and are `worker_failed` (the action there
  is to send diagnostics, not to free up space); a size or checksum mismatch is `download_corrupt`. In both the results
  are still in the bucket, so the copy says download again (Hard Rule 14), not start again.

### The wait, the download and the lifecycle check (cold review of #460, 2026-10-02)

- **A transient failure never ends the wait.** The poll loop catches every `CloudOperationException`
  except the project-wide classes (billing, API off, permission, org policy: these end the run at
  once, after a best-effort end of the VM) and looks again on the next tick. The VM and the worker
  carry on without the app, so a network blip of a minute must not cost the user a second run.
  Pinned by `More_transient_503s_than_the_retry_pipeline_absorbs...` (14 scripted 503s behind the real
  retry pipeline and breaker, then a valid result: Completed with the track on disk).
- **Per-call deadline.** Every gateway call the runner makes runs under `CallTimeout` (60 s) through
  `Task.WaitAsync`, so a callee that ignores its token is cut too: the preflight calls, `EnsureBucket`,
  every `FindByJobId`, the create-collision `GetVm`, the wait, the lifecycle check, `CancelAsync`,
  `StopVmAsync` and `DeleteVmAsync`, and each read of a downloaded file. A call that outlives it is a
  network failure: the wait loops look again, anywhere else the run (or the cancel) is recorded Failed
  instead of hanging. **Uploads** have their own deadline, `UploadTimeout`, default a floor of 2 minutes plus
  1 second per 128 KiB of the object (a link as slow as 1 Mbit/s still finishes); a flat 60 s would cut every
  large input short. The resilience pipeline underneath retries inside that one deadline.
- **The deadline is relative to the VM's own limit.** The default wait is `maxRunDuration` minus 3
  minutes (`CloudJobRunner.ResultWaitLimit`), measured from the VM's creation, not from `Running`. The platform deletes the VM at
  `maxRunDuration` (Hard Rule 10) and the worker's own limit is the same number, so a wait that
  ended later could only ever find a lost VM and `result_timeout` was unreachable (the old limit was
  `maxRunDuration` plus 5 minutes). Ending first lets the runner give up on its own terms: end the
  VM per the user's choice, verify it, record `result_timeout`. A worker still uploading in the last
  3 minutes loses that tail; that is the honest outcome of a run that used its whole limit.
- **RUNNING is not working: the runner reads the heartbeat (issue #498).** While it waits for `result.json`, `ResultWaiter`
  also reads `status.json` on every poll and feeds `HeartbeatWatch`, which judges only whether `heartbeatSeq`/`updatedAt`
  CHANGED between looks, on the app's own clock (never the worker's `updatedAt` against the app's wall clock: the two
  machines can disagree by minutes). Three verdicts, each ending the VM before the run is recorded Failed: no
  worker-written heartbeat (a `status.json` whose `worker.version` is non-empty; the startup script's own infra snapshots
  do not count) within `FirstHeartbeatTimeout` (25 min from the start of the wait: booting 8 + image pull 15 + 2 margin,
  the stage deadlines of job_contract.md section 5; THEORY (unverified) on a real VM) records `worker_no_heartbeat`; a
  heartbeat unchanged for `HeartbeatStaleTimeout` (default 20 x `limits.heartbeatSeconds` = 600 s, job_contract.md section 5's
  "older than 600 s regardless of instance state") records `worker_heartbeat_stale`; and a first progress line
  (`worker starting; GPU: ...`) that says `no GPU detected` on a GPU machine type records `gpu_not_visible` and DELETES the VM
  (as `startup.sh` does for a box whose GPU never came up). A last look for `result.json` precedes each verdict, because a
  worker writes it before it stops heartbeating. A transient failure reading `status.json` never ends the run. **Scope: a dead or frozen worker PROCESS only.** The real worker's heartbeat is a free-running daemon thread (`status.py` `StatusWriter._loop`), so a hung main thread under a still-ticking heartbeat is NOT caught (issue #522). The first-heartbeat deadline also cannot tell a slow CUDA image pull from a never-started worker, because `startup.sh`'s installing-stage progress is not read (recorded on #90). Not done
  here (issue #90 remains): per-stage deadlines other than the first heartbeat, the 180 s "not RUNNING" death rule, the
  cancel-ack 60 s rule, and showing the heartbeat in the UI. `FakeGcp` scripts `NoHeartbeat`, `HeartbeatStopsAfterRunning`,
  `NoGpuFirstLine` and a healthy `HeartbeatingThenDone` worker.
- **Giving up records what was verified.** After the VM end is attempted the runner re-reads the VM:
  confirmed ended is `result_timeout` ("we shut it down"); not confirmed (calls failing, still running
  at `LifecycleTimeout`) is `vm_end_unconfirmed` ("open the Cloud page and delete it"), and the raw detail
  says why. No message claims a stop nobody checked. This is general, not only for timeouts: whenever any
  failed run's VM end is not confirmed (a stop that never lands, calls refused, a create that may still land)
  the recorded code is `vm_end_unconfirmed`, whose copy sends the user to the Cloud page, and the original
  code and cause stay in `ErrorDetail` ("Original code: model_oom"). `RunRecord` has no separate field for
  this, so the code takes precedence.
- **Download first, lifecycle after.** As soon as a valid `result.json` is in hand and it reports at
  least one finished input, the files are downloaded; only then is the VM ended and verified. The
  worker has usually stopped or deleted its own VM already (`startup.sh` exit codes 10 and 11), and
  the old order (verify, then download) failed the whole run, results never fetched, whenever it had.
- **The lifecycle check tolerates a worker that already acted.** Delete chosen: a VM already gone, or a
  delete answered 404, is success. Stop chosen: `STOPPING` is polled to `LifecycleTimeout` (3 minutes);
  `STOPPED` and `TERMINATED` both count as stopped; a VM already gone counts too. THEORY (unverified):
  Compute Engine reports a stopped instance as `TERMINATED` (the API's own name for the state); nobody
  here has measured it against a real instance, so both spellings are accepted rather than guessed.
- **A failed lifecycle check after a good download keeps the results.** The run still ends `Completed`
  (or `PartiallyCompleted`) with `ErrorCode = lifecycle_unverified` and the reason in `ErrorDetail`; the
  copy tells the user to delete the VM from the Cloud page. A download that fails still ends the VM
  (best effort, recorded in the detail) before the run is `Failed`.
- **A boot failure reads as its own cause.** `startup.sh` writes `status.json` with an `error.code` and
  ends the VM for `GPU_NOT_VISIBLE`, `IMAGE_PULL_FAILED`, `MANIFEST_INVALID` and `WORKER_CRASH` and
  never writes `result.json`; the container's own failures do write it. Whenever the VM is found not
  running with no `result.json`, the runner reads `status.json` and records `gpu_not_visible`,
  `image_pull_failed`, `manifest_invalid` or `worker_crashed` (each with its own copy and one action);
  any other or missing code stays `vm_unhealthy`. (An earlier version of this document said
  `result.json` is always written first. It is written first only by a worker that got as far as a
  result.)
- **Resume.** A run resumed from Provisioning onward (a create can have been accepted before the app died)
  first looks for `result.json`; if the worker already finished, it goes straight to the download and
  never provisions, so it cannot create a second billed VM. A resumed run whose worker is still going
  adopts the VM as before.
- **Every failure once a VM may exist ends the VM.** The guard covers the whole run, not only the part after a
  create: it applies from the moment the row is at or past `Provisioning` (a resume can find a VM already
  running) and from the first create attempt. Any exception that leaves without an end attempt (a failed
  look, a bad transition, a failing repository, a failed preflight or probe on a resume) ends the VM first,
  by the zone when known and by the job-id label when not (best effort, per the user's choice, verified). A
  transient failure of the `result.json` probe on a resume is not fatal: the run carries on and adopts the
  VM. Exceptions that already went through an end attempt are marked so it is not waited out twice.
  Cancellation is the exception: `CancelAsync` owns that delete. A failed run honours the user's choice: Delete, or keep-alive with Delete afterwards,
  deletes; everything else stops (`startup.sh` applies `afterKeepAlive` at once for a run that did not
  succeed).
- **A booting VM is waited for.** Right after create the runner waits for RUNNING; PROVISIONING and
  STAGING are "still starting", polled to `BootTimeout` (8 minutes), then the VM is ended and the run is
  `vm_unhealthy`. THEORY (unverified): a real instance reads PROVISIONING then STAGING for a short while
  after insert; nobody here has measured it. A VM that never reached RUNNING is **deleted** at the boot deadline,
  whatever the user chose for after a run. THEORY (unverified): `instances.stop` on an instance that is not
  RUNNING is rejected, and a VM that never booted has no disk worth keeping. A VM found already stopped, gone
  or terminated at the first look is judged exactly like one lost while waiting: `result.json` first (a fast
  worker can finish and stop its VM between two looks, and is downloaded), then `status.json` for a mapped
  boot-failure code. A transient failure of the look is retried, not fatal; a class
  that aborts (billing, API off, permission, org policy) ends the VM and the run at once.
- **A hung create ends at its deadline.** Each zone's create runs under `CreateTimeout` (30 s) with
  `Task.WaitAsync`, because the poller checks its deadline only between polls. A create that timed out may
  still be accepted and land later, so the runner keeps the insert task and, before the zone ladder moves on or
  the run is recorded, waits for it to settle (bounded by `CreateSettleTimeout`, 30 s) and then deletes
  whatever carries the job's label. If the insert is still in flight after that, the run is recorded
  `vm_end_unconfirmed` (the Cloud page); the VM's own `maxRunDuration` is the backstop. `CancelAsync` does
  the same with the inserts this runner instance started for the job: it waits for them (bounded) before it
  looks for VMs, and records `cancel_failed` if one is still in flight. THEORY (unverified): a different
  runner instance cannot see another's in-flight insert, so a cancel from a fresh instance after a crash only
  has the label lookup.
- **Round 4 guarantees (framing-free review, 2026-10-02).** Pinned by `CloudJobRunnerGuaranteesTests`.
  - *A gateway's own deadline is a failure, not a cancel.* Only an `OperationCanceledException` whose cause is the
    CALLER's token skips the end-the-VM guard; the one a gateway throws on its own HTTP or gRPC deadline (the caller's
    token still live) ends the VM like any other failure.
  - *A create that fails may still have landed.* After a Network, Other or Stockout create failure (not only a timeout) the
    runner looks by job-id label and deletes what it finds before it moves to the next zone or records the run, so a lost
    response after the server accepted the insert cannot leave one VM per zone. A sweep that cannot be confirmed records
    `vm_end_unconfirmed`. `VmTerminator.EnsureVmsEndedByLabelAsync` ends every VM it finds even when an earlier one could not be ended.
  - *Cancel always records one terminal state.* `CancelAsync` tolerates a 404 on delete (the worker or platform got there
    first), re-looks by label afterwards (polling to `LifecycleTimeout`) before it records `Cancelled`, and when the caller's
    token is cancelled midway makes one last uncancellable attempt and records `Cancelled` or `Failed/cancel_failed` instead of
    leaving the row in `Cancelling`. A row that is already terminal (a second cancel racing the first) is left alone and
    nothing throws. `RunAsync` on a row in `Cancelling` finishes the cancel (it used to attempt `Cancelling -> Validating`).
  - *The result wait is measured from the VM's creation.* `VmDescriptor.CreatedAt` is the instance's `creationTimestamp`;
    the real Compute gateway MUST fill it (there is no real gateway yet, so this is a contract for it). The default deadline
    is `maxRunDuration - 3 min` from that instant, so an 8 minute boot or a resume no longer lets the platform delete the VM
    before the runner gives up (it read as `vm_unhealthy`). A gateway that leaves it null falls back to "from Running".
    An explicit `ResultTimeout` still measures from Running. `FakeGcp.WithMaxRunDurationEnforced()` makes the fake delete a
    VM at its limit so a test can see the difference.
  - *A notification never changes the outcome.* The `onPhaseChanged` callback runs after the row is committed and its
    exceptions are swallowed: a throwing callback can no longer fail a run, end a kept-alive VM, or make the returned result
    disagree with the row. When `RunAsync` cannot record `Failed` because the row is already `Completed` or `Cancelled`, it
    returns that state.
  - *A booting VM is deleted, not stopped.* When the VM reads PROVISIONING or STAGING at the moment a failure ends it, the end
    is a delete whatever the user chose (THEORY, unverified: stop is rejected on an instance that is not RUNNING).
  - *A resumed cancel cannot fail blind.* If `CancelAsync` throws before its own delete logic on a resume, the runner ends the VM
    by label and records `Cancelled` or `cancel_failed`; a repository that keeps failing leaves the row `Cancelling`.
  - *A terminal row answers with its own code,* a cancel tries every VM, and a VM found by label is ended under its own name.
  - *The end helpers never throw.* `VmTerminator.EnsureVmEndedAsync` and `VmTerminator.EnsureVmsEndedByLabelAsync` catch any exception that is not the
    caller cancelling and report it as an unconfirmed end (`vm_end_unconfirmed`).

- **The input is validated before anything is created (Hard Rule 2, #479).** `JobEngine` runs
  `InputFileValidator` on the staged copy before it calls the runner; a problem records the run `Failed`
  with an `input_*` code (`InputProblemErrorCodes`), and no bucket object or VM exists. `ErrorDetail` carries
  only the problem code, record number and position, never the problem text.
- **The output folder is proven before anything is created.** After `Validating` the run's folder is made
  (recorded as `OutputDir`) and a probe file is written and deleted in it; failure is `output_folder_unusable`
  before the bucket or a VM exists. The download reuses that folder. A run that ends `Failed` with nothing in
  its folder removes the empty folder (never a file, Hard Rule 14) and clears `OutputDir`.
- **What the run record does and does not carry.** `ErrorDetail` goes to SQLite and the diagnostics zip, so
  the runner's own messages name the input id and file number ("Result file 1 of input in1"), never a path
  or file name, and drop the worker's free-text message (it can carry a record name) in favour of its code.
  That is the whole claim. The row does store `ManifestJson` (the manifest as uploaded), which carries each
  input's `name` and `path` (the file name without and with its extension), and `OutputDir`, which is named
  after the first input. Both are the user's own data on their own machine and the manifest is needed to
  re-run; they are not in `ErrorDetail`. Known gap: a gateway's own exception text can still carry an object
  key, and `RunAsync`'s catch-all records `ex.Message` as is.
- **A finished input must have a track.** An input the worker reports `done` with no files is not counted
  as finished: if no input finished the run is `Failed` (`worker_failed`, "A finished input listed no result
  files"), otherwise `PartiallyCompleted`.
- **Worker codes with their own user action get their own run code**, from `status.json` and from
  `result.json`: `MODEL_OOM`, `MODEL_NEEDS_HOPPER`, `BATCH_LIMIT_EXCEEDED`, `INPUT_INVALID`,
  `WORKER_VERSION_MISMATCH` plus the four boot failures above. `MODEL_UNKNOWN` has no row in
  `docs/copy_catalog.md`, so it is generic (`worker_failed`) on purpose
  (`RunErrorCodes.WorkerStatusCodesWithoutDedicatedCopy`). `WorkerContractGuardTests` fails when a code in
  `docs/contract/error-codes.json` is neither mapped nor listed, and when a list entry goes stale; a second
  test ties the manifest builder's FP8 model list to `MODEL_REQUIREMENTS` in `hardware.py`. Both read repo
  files and need the default artifacts path.
- **Partial results are kept.** Every input that lists files is downloaded, whatever its status: a failed
  or cancelled input's partial files land beside the finished ones (docs/job_contract.md sections 6 and 7),
  the run reads `PartiallyCompleted` when some inputs finished, and when none did the partial files are still
  fetched before the run is recorded `Failed` (its detail says partial files were kept).
- **Known gap.** The copy for `download_failed` and `download_corrupt` points at downloading again from
  the Runs page while the objects exist (Hard Rule 14). No Runs-page "download again" command exists
  yet, and a `Failed` run is terminal, so today the only way to act on that copy is a new feature.
  Presentation owns it.

**No real cloud yet.** The production composition (`ServiceRegistration`) still backs every gateway
with `FakeGcp`. It now registers it with `WithCloudNotConnected()`, so preflight throws a
`CLOUD_NOT_CONNECTED` error before anything is uploaded or created and the run is recorded `Failed`
with `cloud_not_connected` ("not connected in this version of the app yet, nothing was sent to Google
and nothing was billed"). Before this, the simulated worker wrote `fake-track:` files into the user's
real output folder and the run read `Completed`. Tests and the future `--fake-cloud` switch (#69) use
a plain `new FakeGcp()`, which simulates success.

`FakeGcp` is a real in-memory bucket for this: `ObjectKeys`, `GetObjectBytes`, `PutObject`,
and a simulated worker (`WithWorker(FakeWorkerMode)`) that writes outputs then `result.json`
for any VM created while a manifest is in the bucket. For the behaviours above it also scripts
`WithWorkerEndingItsOwnVm(Stop|Delete)` (acts the first time anyone looks for `result.json`, like
`startup.sh` exit codes 10 and 11), `WithDeleteOfMissingVmNotFound()` and
`WithNextDeleteRacingAWorkerDelete()` (a 404 like real Compute), `WithStopBehaviour(stoppingPolls, status)`
(`STOPPING` for N polls, then `STOPPED` or `TERMINATED`), `WithHungCalls(n)`, the boot-failure worker modes
(`GpuNotVisible`, `ImagePullFailed`, `ManifestInvalid`, `WorkerCrash`) and `WithCloudNotConnected()`.

Every phase change is written to `IRunRepository` before any `onPhaseChanged` callback
fires (issue #58's own Done-when). There is deliberately no separate "resume" method:
`RunAsync` is safe to call again for the same job id from a brand-new `CloudJobRunner`
instance - already-passed happy-path phases are silently skipped
(`JobStateMachine.HasAlreadyPassed`), and `ProvisionAsync` always reconciles via
`FindByJobIdAsync` first (section 3, issue #257) - so a second call after a crash *is* the
resume path, not a distinct one someone has to remember to call.

**Reached from the UI (issue #428)**: `JobEngine` (App) is the production caller; see
`architecture.md` section 3. Three runner behaviours exist for that caller:
`RunAsync` turns an exception nothing classified into a recorded `Failed` phase (a
`CloudOperationException` keeps its `CloudErrorKind`, anything else is `Other`;
cancellation still propagates), so a run never stays in its last phase after an unexpected
error (Hard Rule 11); `RunRowStore.SetPhaseAsync` updates the existing run row (`existing with {
Phase }`) and stamps `FinishedAt` on a terminal phase, because the SQLite repository
replaces every column on upsert and a blank record would wipe the options and project the
engine wrote first; and `CancelAsync` ignores a job id it never saw, records `Cancelled`
for a run cancelled while still in `Draft` (during preflight), and otherwise goes through
`Cancelling`. `StopVmAsync`/`DeleteVmAsync` find the job's VMs by label and act on each,
so "Stop VM now" works from any runner instance.

**`OperationPoller`** (`app/src/DnaEntropyGraph.Core/Cloud/OperationPoller.cs`, issue #256):
the one place that polls-with-backoff (1s doubling to a 10s cap) against a deadline,
distinguishing a real operation error from its own `OPERATION_POLL_TIMEOUT` (classified as
`network`, never confused with a genuine `stockout`/`quota` the operation itself reported -
see `OperationPollerTests`). `VmProvisioner.ProvisionAsync` wraps every `CreateVmAsync`
call in it today as a single-shot poll (since `FakeGcp` resolves synchronously, with no
"not done yet" phase) - a real gateway polling an actual Google LRO would report several
"not done yet" snapshots first, and needs no runner-side change to do so, only a real
`poll` delegate.

**Walking-skeleton scope, not issue #86**: `CloudJobRequest.Zones` is a plain, caller-
supplied, sequential list - not the escalating-parallelism (3 -> 3 -> 5 -> batches of 7),
last-good-zone-first, tier-escalating ladder section 3 describes. `PreflightChecks`'s GPU
quota check also uses a placeholder accelerator-type string (`"gpu"`), because `VmSpec` has
no accelerator-type field yet (only `MachineType`) - see this round's changelog fragment
and issue tracker for the follow-up.

## 12. Retry, token refresh and the offline breaker (issue #258)

**Implemented**: `CloudCallPipeline` (`app/src/DnaEntropyGraph.Cloud/CloudCallPipeline.cs`,
Polly.Core 8.6.5, BSD-3-Clause) is the one policy in front of every gateway call.
`ResilientComputeGateway`/`ResilientStorageGateway`/`ResilientProjectSetupGateway`/
`ResilientQuotaGateway` wrap each interface method; `ServiceRegistration` resolves every
gateway through them, so the real Google-backed gateways go *inside* the wrappers and get
the policy for free.

| Failure | What happens |
| --- | --- |
| HTTP 429/500/502/503/504 **whose error does not classify as a permanent kind**, a transport failure (`HttpRequestException`, or a `CloudOperationException` of kind `network` with no status), a timeout (an `OperationCanceledException` while the caller's own token is not cancelled) | Retried up to `CloudRetryOptions.MaxRetryAttempts` (4) with exponential backoff (1s base, 20s cap) and jitter. When retries run out the caller gets a `CloudOperationException` of kind `network` (the original error text and status are kept) |
| Stockout, quota, billing, API-off, permission, org-policy or already-exists, **whatever the HTTP status** (Compute reports a stockout as 503 `ZONE_RESOURCE_POOL_EXHAUSTED`) | **Not retried and not counted by the breaker.** Waiting cannot fix them, and counting a four-zone stockout ladder would open the breaker and make a stockout read as offline (CLAUDE.md: quota is not stockout). The pipeline classifies before deciding |
| HTTP 401 | `ICloudTokenRefresher.RefreshAsync` once, then the call is replayed once. A second 401 becomes kind `permission` (sign in again) |
| 403, 409, 412, stockout, anything else | Untouched: `CloudErrorClassifier` and the zone ladder own them |
| Most recent calls failed (6+ calls in 30s, 80% transient failures) | The breaker opens for 30s. Calls fail fast as kind `network` with code `OFFLINE` and are not sent. **Cleanup bypasses the breaker**: `StopVm`, `DeleteVm` and `FindByJobId` go through a second pipeline with the same retries and no breaker, because they are what stops the meter; if a cancel's cleanup still fails the run is recorded `Failed` with code `cancel_failed`, never left in `Cancelling`. `CloudCallPipeline.IsOffline`/`OfflineChanged` report it; it closes after a successful half-open call |

Retrying a mutating call is safe only because of two earlier decisions: `CreateVmAsync`
carries a deterministic request id (issue #257, so a replay is idempotent) and stop/delete
are idempotent. An upload is replayed only when its stream can seek (the decorator rewinds
it); a non-seekable stream gets one attempt and no 401 replay.

Every retry is reported to `ICloudCallObserver` (operation, attempt, delay, status, kind,
the API error text, never a sequence, file name or email). `CloudRetryLog` keeps the last
200 in memory. THEORY (unverified): Serilog is not wired into the app yet, so the log is
not in the diagnostics zip; when it is, forward `OnRetry` there.

The pipeline's kind is kept end to end: `VmProvisioner.ProvisionAsync` uses the kind the
gateway threw instead of re-classifying the error text (which turned `network` into `other`,
walked every zone and reported a stockout), and a run failure is recorded as a code
(`RunErrorCodes`) with the raw text only in `ErrorDetail`; the UI shows the `RunError_<code>`
resource string (Hard Rule 13).

**Not yet wired**: nothing in the UI reads `IsOffline` (the offline banner), and
`ICloudTokenRefresher` is implemented only by `FakeGcp` (the real OAuth refresh arrives with
the real auth service). `ResilientGatewayTests.Every_method_of_every_gateway_goes_through_the_pipeline`
fails if a method is added to a gateway interface without being routed through the pipeline.
`FakeGcp.WithTransientFailures(status, count)` and `WithUnauthorized(count)` script these
failures against any gateway method.

## 13. The startup script and its metadata (issue #261)

**Design correction.** Issue #261 was filed as "render the script from a template with
escaped values". The script is not rendered: `worker/vm/startup.sh` is a fixed template
(see `worker/vm/README.md`) that reads every per-job value back from the instance's own
metadata attributes with `meta()`. So there is nothing to escape into the script text. The
real risk is on the *attribute* side: the script then uses those values inside a JSON
document, a `docker pull`, and a shell arithmetic expansion (`shutdown -h "+$(( MAX_RUN_MIN
+ 15 ))"`, where a value such as `a[$(cmd)]` would execute). A quoting layer in C# cannot
make that safe, so the builder **validates** each value against the exact shape the script
can consume and throws before a request is built.

**Implemented**: `StartupMetadata` (`app/src/DnaEntropyGraph.Core/Cloud/StartupMetadata.cs`).
`worker/vm/startup.sh` is embedded in `DnaEntropyGraph.Core` as an `EmbeddedResource`
(single source of truth; CRLF normalised to LF, because a CR breaks the shebang on the VM).
`StartupMetadata.Build` returns `startup-script` plus the six attributes the script reads:

| Attribute | Accepted shape | Why |
| --- | --- | --- |
| `deg-job-id` | 1-63 of `a-z 0-9 _ -` | printed into JSON by `status()` and into object names |
| `deg-bucket` | 3-63 of `a-z 0-9 . _ -`, starts and ends alphanumeric | used in `gs://` and REST URLs |
| `deg-worker-image` | `name[:tag]@sha256:<64 hex>` | pulled by digest, never a mutable tag; also stops a value like `--privileged` reaching `docker` |
| `deg-expect-gpu` | `true` / `false` | compared literally by the script |
| `deg-lifecycle` | `stop` / `delete` / `keep` | `case` word in `cleanup()` |
| `deg-max-run-min` | whole minutes, 1 to 100000 (rounded up) | goes into shell arithmetic, so digits only by construction |

`StartupMetadata.EnsureSize` enforces Compute Engine's limits in UTF-8 bytes: 256 KiB per
value, 512 KiB in total. `VmSpec.EnsurePreconditions` calls it on the new `VmSpec.Metadata`
property, so every gateway (and `FakeGcp`) rejects an oversized spec before a request is
built. The real script is about 9 KB, well inside both limits.

`CloudJobRunner` attaches the metadata once the bucket name is known (`CloudJobRequest.WorkerImage`
set means attach; null means create the VM with none). An invalid value fails the run before
any VM exists.

**Where the image comes from (issue #458).** `IWorkerImageProvider` (Core) is implemented by
`PinnedWorkerImageProvider` (App), which reads `worker-images.json`, embedded in the App
assembly: `{"images":[{"version","cuda","cpu"}]}`, one entry per app version, each reference
pinned by digest (`PinnedWorkerImageList` drops and counts any entry that is not). The same list
is the allowlist. `JobEngine` asks for the image of the running app version (`-cuda` for a GPU
machine type, `-cpu` otherwise) before it stages anything, and **fails the run** instead of
creating a VM with no script: `no_worker_image` when the list has nothing for this version,
`worker_image_refused` when a `worker_image` override is not acceptable. The override, read from
`settings.json`, is accepted when it is on the list, or when `developer_mode` is `true` and it is
pinned by digest; there is no UI for either on purpose. **The shipped list is empty until the
release pipeline writes the digests of a tagged build into it**, so until then every run ends
`no_worker_image` unless a developer sets the override; this is deliberate and honest rather than
a VM that boots and does nothing.

**Verification**: `StartupMetadataTests` (Core.Tests) prove the embedded text equals
`worker/vm/startup.sh`, that every `meta instance/attributes/<x>` the script reads is set by
`Build`, that unusual job ids, buckets and images are rejected, and the size limits.
`shellcheck worker/vm/startup.sh` already runs in CI (`ci-worker.yml`, "shellcheck the VM startup
script") and exits 0 locally via `uv run --with shellcheck-py shellcheck`. Because the script
text never varies with a job, one shellcheck run covers every render; a per-render shellcheck
would be vacuous. There is no Verify snapshot: the embedded-equals-source test is the stronger
check, and the attribute set is asserted key by key.

## 14. Model weights cache (issue #75)

`cache/models/<modelId>/` lives at the bucket root, not under `jobs/<id>/`, so every job in
the project shares it. For an Evo job, before building the predictor, the worker sets stage
`restoring-cache` and, if `_COMPLETE.json` has `"complete": true`, restores the files into the
Hugging Face cache (`HF_HOME=/hf-cache`, host folder `/var/cache/deg-hf` owned by uid 10001,
mounted by `startup.sh`). After a load that downloaded into an empty cache, it mirrors the
files back and writes the marker last; a re-mirror first overwrites an existing marker with
`"complete": false`, so an interrupted re-mirror cannot vouch for mixed files. Both directions
are best effort: a failure is a progress notice and the job continues. Symlinks are recorded
in the marker, not uploaded. Cost: about 14 GB of bucket storage per model. Until #496 the
GCS blobstore reads each shard fully into memory.

## 15. Sign-in, the token store and account switching (issue #48)

`GoogleAccountService` (`DnaEntropyGraph.Cloud/Auth/`) is the one implementation of Core's `IGcpAccount`,
`IGcpAccessTokenSource` and `ICloudTokenRefresher`. Production DI registers it as `IGcpAccount` and `IGcpAccessTokenSource`; `ICloudTokenRefresher` stays `FakeGcp` until #56 (below). The gateways are
still `FakeGcp` until the real ones land, so who is signed in is real while what the gateways do is not.
Two seams are deliberately still the fake, with the switch point recorded in `ServiceRegistration.cs`: `ICloudTokenRefresher`
(a fake 401 must not call Google's token endpoint; switch to `GoogleAccountService` when the first real gateway is
wrapped, #56) and the project id (`ProjectIdUntilSelectionExists = "fake-project"` while signed in, until #520 stores a
per-account choice). `IGcpAccessTokenSource` is registered with no consumer yet; the real gateways read it.

- **Flow.** `PkceGoogleAuthorizationCodeFlow` from Google.Apis.Auth (it sends `code_challenge`,
  `code_challenge_method=S256` and the matching `code_verifier`) driven by `AuthorizationCodeInstalledApp`, with our
  own `LoopbackCodeReceiver` instead of Google's `LocalServerCodeReceiver`, because that one opens the browser itself
  (nothing to inject in a test) and does not check `state`. Scopes `openid email https://www.googleapis.com/auth/cloud-platform`,
  `access_type=offline`, prompt `select_account consent`.
- **Files** under `%LOCALAPPDATA%\DNAEntropyGraph\auth\`: `<sub>.tok` (DPAPI, one per account, key = the id token's
  `sub`, held to `[A-Za-z0-9_-]` because it becomes a file name) and `accounts.json` (`activeSub` and a list of
  `{sub, email, needsSignIn}`, no token). Token refresh is done by Google's `UserCredential` and written back through
  the same store, so a restart needs no browser.
- **Errors** are `AccountAuthException` with a code from `AuthErrorCodes`; the English is `AuthError_<code>` in
  `Resources.resw`. `SIGNIN_EXPIRED` (Google answered `invalid_grant`) deletes the dead token file, sets `needsSignIn`
  on the account and offers **Sign in again**. `SIGNIN_NETWORK` does not expire anything. `OAUTH_CLIENT_MISSING` and
  `OAUTH_CLIENT_INVALID` name the installer as the action: the user never has this file, the build does.
- **Switching** only changes `activeSub`. **Sign out** revokes the refresh token at Google, deletes the token file
  and the account entry whatever Google said, and makes the next remaining account current.
- **Not here yet:** the selected project (a placeholder until #520), and any UI that starts a sign-in, lists
  accounts, switches or signs out: no page binds `WizardViewModel.SignInCommand` yet (#99, #519), so today nothing
  in the shipped UI can start a sign-in. What is wired in the UI is the shell status pill, which follows
  `AccountChanged` and shows the account email.
- **Failures that are not Google's answer** are mapped too, so a command never crashes: browser cannot start
  (`SIGNIN_BROWSER`), no loopback port (`SIGNIN_LOOPBACK`), token folder not writable (`SIGNIN_STORAGE`), HTTP
  timeout (`SIGNIN_NETWORK`). The interactive browser wait does not hold the lock that token calls use, so a
  pending sign-in for a second account never stalls the first. A request on the loopback port with the wrong
  `state` gets a 400 and is ignored; the wait ends on the real redirect or the timeout.
- **Roster:** `AuthErrorCodes.All`, `docs/copy_catalog.md` and `scripts/triage_diagnostics.py` agree, enforced by
  `Guards.Tests/AuthErrorResourceTests`.
- **Testing.** `Cloud.Tests/Auth` runs the whole flow with no network and no browser: a fake that answers Google's
  real token and revoke URLs and checks the PKCE proof, and a fake browser that calls the real loopback listener back.
## 16. The real Google gateways (issues #50 to #52)

**Library decision (agent-made, reversible; filed as a DECISION issue).** The real gateways use the REST discovery
clients `Google.Apis.CloudResourceManager.v3` (#50), `Google.Apis.Cloudbilling.v1` (#51) and `Google.Apis.ServiceUsage.v1` (#52),
not the `Google.Cloud.*` gRPC-first packages the CLAUDE.md stack row names. Evidence (inspected 2026-10-02 in the
restored `Google.Cloud.ResourceManager.V3` 2.6.0 and `Google.Api.Gax.Grpc` 4.12.1 packages): the only REST transport in
that stack is `RestGrpcAdapter.Default`, which builds its own `HttpClient` and exposes no `HttpMessageHandler` or
`HttpClient` seam, so a unit test cannot intercept a request. The discovery clients take
`BaseClientService.Initializer.HttpClientFactory`, so the tests script an `HttpMessageHandler` and assert the method, the
URL, the body and the bearer token. All are Apache-2.0 and share `Google.Apis.Core` with `Google.Apis.Auth`, which moved
from 1.67.0 to 1.77.0 so the whole family is one version. The owner edits the CLAUDE.md stack row.

- **Where it lives.** `DnaEntropyGraph.Cloud/Rest/`. `GoogleCloudGateways.Create(IGcpAccessTokenSource, CloudCallPipeline,
  GoogleCloudOptions?)` is the one construction point: it builds each client with a bearer-token interceptor that reads
  the token source on every request (so a refreshed token is used by the replayed call) and wraps each gateway in its
  `Resilient*` decorator. Google.Apis's own 503 retry is switched off (`ExponentialBackOffPolicy.None`) so
  `CloudCallPipeline` is the only retry. MEASURED 2026-10-02: with the library default, three scripted 503s were all
  consumed inside one gateway call and the pipeline's retry saw a different error; with `None` the pipeline retried
  twice and gave up as `network` (THEORY, unverified: that the default is the cause of that, not the scripted handler).
- **The production switch.** Production DI still resolves every gateway to `FakeGcp.WithCloudNotConnected()`
  (`ServiceRegistration.cs`, "SWITCH POINT (#56)"). The switch is one call to `GoogleCloudGateways.Create(...)`, made when
  every preflight step has a real implementation (#56) and the placeholder project id is gone (#520). Until then the
  real gateways are built and exercised only by `Cloud.Tests/Rest`.
- **Errors.** `GoogleApiErrors` reads Google's `google.rpc.Status` (an HTTP error body, or the `error` of a polled
  operation) and throws `CloudOperationException`. A `QuotaFailure` detail, or "quota" in a `RESOURCE_EXHAUSTED`
  message, is `Quota`; an `ORG_POLICY` reason, a `constraints/` id or the words "organization policy" is `OrgPolicy`;
  everything else goes through `CloudErrorClassifier`. A setup step may give those kinds its own code
  (`SetupErrorCodes`, below). THEORY (unverified, no live project): the exact markers Resource Manager uses for a
  project-limit and an organization-policy refusal. The test fixtures are Google's documented shapes, not captures.
- **Roster.** `SetupErrorCodes.All`, `docs/copy_catalog.md` and `scripts/triage_diagnostics.py` agree, enforced by
  `Guards.Tests/SetupErrorResourceTests`. The English is `SetupError_<code>` in `Resources.resw`.
- **Wizard wiring.** No wizard page exists yet (#99). The interfaces are in Core, `FakeGcp` implements them, and the
  ViewModels that call them arrive with the page.

### Project list and create (issue #50, wizard step 3)

`IProjectCatalogGateway`: `ListActiveProjectsAsync`, `GetProjectAsync`, `CreateProjectAsync`.

- **List.** `GET /v3/projects:search?query=state:ACTIVE`, following `nextPageToken`. Projects labelled
  `app=dna-entropy-graph` sort first (`ProjectCatalogOrder`), then by name. An account with no projects gets an empty
  list, which the wizard answers with one click on Create.
- **Create.** `POST /v3/projects` with `projectId` `dna-entropy-<8 random lowercase alphanumerics>`
  (`ProjectIdGenerator`), display name "DNA Entropy Graph", labels `app=dna-entropy-graph` and `installation-id`
  (the VM-only labels `job-id`, `model`, `app-version` and `lifecycle` do not apply to a project). The response is a
  long-running operation, polled with `OperationPoller` (1 s doubling to 10 s, 5 minute deadline); a timeout is
  `OPERATION_POLL_TIMEOUT`, classed `network`.
- **Replay safety.** The resilience pipeline replays a whole create after a dropped connection. A `409 ALREADY_EXISTS`
  is followed by a `projects.get`: a project of ours (it carries the app label) is returned, anyone else's is an
  error. One wizard click therefore never makes two projects.
- **Errors.** A project-limit refusal is `PROJECT_QUOTA` (kind `quota`, never retried; action: pick an existing
  project); an organization-policy refusal is `ORG_POLICY_BLOCK` (kind `org_policy`; action: copy the message for IT).
  A 403 on create stays `PERMISSION_DENIED` (kind `permission`). A 429 with no quota marker is a rate limit and is
  retried.
- **Not proven without a real account:** `docs/ToTest.md`.
### Billing check and link (issue #51, wizard step 4)

`IBillingGateway`: `GetBillingStatusAsync`, `ListOpenBillingAccountsAsync`, `LinkProjectAsync`; `BillingSetup` (Core) is the policy over it.

- **Requests.** `GET /v1/projects/{id}/billingInfo`; `GET /v1/billingAccounts?filter=open=true` (paged; a closed account is
  also dropped client-side); `PUT /v1/projects/{id}/billingInfo` with `{"billingAccountName": "billingAccounts/..."}`.
  Google omits `billingEnabled` when it is false, so an absent value reads as off. Billing is "enabled" only when an
  account is linked and `billingEnabled` is true.
- **Policy.** On: nothing is linked. Off with one open account: linked for the user, then the status is read back
  (a link Google accepted that did not turn billing on is `NeedsAccount`, not success). Several: `ChooseAccount`,
  and `LinkAsync` links the one the user picked. None: `NeedsAccount` with `BillingLinks.ForProject(projectId)`, the
  console page for that project; calling `EnsureAsync` again after the user adds a payment method is the re-check.
  The wizard action for the code `NO_BILLING` is that link (`SetupAction_LinkBilling`).
- **No permission.** A 403 on the link is `BILLING_NO_PERMISSION` (kind `permission`), decided on the status, because the
  classifier reads the word "billing" in a 403 message as a billing-off error. The action is a copyable request:
  `SetupBillingRequestText` (with `{project}` and `{account}`) filled by `BillingRequestText.Fill`.
- **Not here yet.** The wizard page (#99) and the health row that must go green on its own after a link: nothing in
  the shipped UI calls this until then. `ResilientProjectSetupGateway.IsBillingEnabledAsync` (the preflight step) is
  still backed by the fake; the composite real `IProjectSetupGateway` that delegates it to `GetBillingStatusAsync`
  lands with #52.
- **Proven only by a real account:** `docs/ToTest.md`.
## Related

[`job_contract.md`](job_contract.md) (the files the worker on this VM reads/writes),
[`gcp_setup_manual.md`](gcp_setup_manual.md) (the same preflight steps, in plain voice,
for when the in-app wizard cannot complete them), [`threat_model.md`](threat_model.md)
(why the OAuth scope this whole design needs is a large ask, and what it does and does not
expose), `docs/migration/2026-09-19-worker-migration-inventory.md` (the exhaustive,
line-referenced prototype-behaviour inventory this doc summarizes for implementers).
