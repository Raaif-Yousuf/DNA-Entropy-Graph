# Cloud design: preflight, zone selection, error classes, labels, cost, termination

**Status: `DnaEntropyGraph.Cloud` exists.** Issues #49 (FakeGcp's scripted failures), #55
(`VmSpec`'s label/name guard), #57 (`CloudErrorClassifier`), #256 (`OperationPoller`), #257
(retry-safe mutations) and #58 (`CloudJobRunner`) landed against this doc as their
reference - `CloudJobRunner` is this table's first production caller: it runs the abort-vs-
continue rule in section 5 for real, over `FakeGcp`. Still not built: the escalating-
parallelism zone ladder (`GpuPlanner`, issue #86 - `CloudJobRunner`'s own zone list is a
plain sequential walk, not that ladder), the setup health checks (issue #204), and a real
(non-fake) gateway implementation (`Google.Apis.Compute.v1`, section 16; production resolves these real gateways since #609; `FakeGcp` is tests and developer mode only). Authoritative detail lives in
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
               -> worker identity ensured (issue #606; after the bucket, because it binds the bucket)
```

Step 6, the worker identity, is not a project-wide precondition the Insert error can be confused with; it runs in
`CloudJobRunner` right after step 5 and before the VM is created, and its account email is what the VM runs as
(section 16, "The worker identity").

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

**What is built (issues #86a, #670).** `ZoneCandidates.Build` (pure: accelerator filter, the three-way quota rule, the ordering) and the ladder in
`VmProvisioner` (`PlanZonesAsync`, `RunLadderAsync`, `TryZoneAsync`):

- The zone universe is `IComputeGateway.ListAcceleratorZonesAsync(accelerator)` (`acceleratorTypes.aggregatedList` filtered on the name, partial success
  asked for; `FakeGcp.WithAcceleratorZones` scripts it). The accelerator is the one the REQUESTED machine carries (`ZoneCandidates.AcceleratorFor`): the
  ladder never reads, and so never tries, a bigger tier (item 6 stays a click). A machine with no GPU, an offer that fails or comes back empty, fall back to
  `CloudJobRequest.Zones` ("unknown is not none"). The quota read is `IQuotaGateway.ReadQuotaAsync` (#88); a read that fails narrows nothing. The
  preference is the request's first zone; the bucket's location (`ListBucketsAsync`, by the run's bucket name) orders its continent next. The last-good zone
  is not wired yet (null: #210). **A zone the user chose is a constraint, not a preference** (`RunOptions.ZonePreference`; Auto is null): the ladder tries
  that zone only and a stockout there is reported as one, because data may have to stay in one place (#149). The default zone list is not a pin.
- Attempts run in batches of 1, 1, 1, then 3, 5 and 7 in parallel until the zones are used up, all with the same VM name in different zones. A failed
  attempt cleans up ITS zone only (`VmTerminator.EndVmAfterFailureAsync(request, zone)`); a sweep by job label would delete a parallel batch's winner.
  A parallel batch is acted on at its FIRST winner, not when its slowest attempt ends: the stragglers are cancelled, and every other zone of the batch
  (one another attempt landed in, or a straggler whose create may still land) is ended BY ZONE from what the batch knows (`SettleAbandonedCreateAsync`,
  `VmTerminator.EnsureVmEndedAsync`), then a listing by label sweeps as a backstop (`DeleteOtherZonesAsync`; a listing can lag, so it never replaces the
  known zones). Every extra that cannot be confirmed deleted is named in the note; the run then ends with `vm_end_unconfirmed` after ending everything by
  label. The claim `jobs/<jobId>/control/zone` (`CloudJobRunner.ClaimZoneAsync`, called through `VmProvisioner.ClaimAsync`) is set by EVERY provision before the VM it makes can read it: a single zone writes its own zone before the create, a parallel batch clears it (an empty object means no claim) before the first create and writes the winner THE MOMENT a winner exists, before anything is cancelled or deleted (so it is there in the one case it exists for, an extra that cannot be confirmed gone), a reuse writes the reused VM's zone before the start, and an adopted VM's zone is written too. A claim left by an earlier run of the job therefore never makes a legitimate VM delete itself. It is best effort: `GatewayCalls` turns a write that outlives its deadline into a `CloudOperationException` (Network), which is swallowed; the caller cancelling still propagates. `worker/vm/startup.sh` (`foreign_zone_exit`) reads it immediately before `status booting`, before `status installing` and before the worker container, and a VM in any other zone stops its relay, deletes ITSELF without touching the job prefix and, if that DELETE fails, shuts the guest down (the one other `shutdown -h`, a last resort besides the deadman), so an extra that boots before the API deletes it cannot overwrite the winner's `status.json` or log. THEORY (unverified): the window between a loser's first guard and the claim landing is seconds at most; a loser that writes `booting` in it is overwritten by the winner's next snapshot. (Chosen over "the worker refuses by instance name": every zone's VM has the same name.)
  A resume that finds several VMs labelled with the job (a crash mid-batch) keeps ONE, preferring a running one, then a booting one, then the request's
  zone order, and deletes and verifies the rest (`KeepOneOwnedVmAsync`; also on the path that finds `result.json` already there).
- A zone that does not offer the requested machine type at all (HTTP 404 on the insert, issue #772) is that zone's stockout: the ladder moves on (`ZoneLadderTests.A_zone_without_the_machine_type_is_skipped_and_the_next_zone_starts_the_run`). The zone universe is filtered by the tier's ACCELERATOR, not its machine type, so a zone that has the accelerator but not that machine shape can still reach the insert; filtering by machine type (`machineTypes.aggregatedList`) was not built because the class already makes the miss cost one failed insert and no VM.
- A project-wide kind (billing, API off, permission, org policy) or a network failure aborts at the first batch that sees it (a batch settles first, so
  nothing is left in flight; a winner of the same batch is still kept). Quota and stockout move on.
- Total failure: what the creates answered decides first. Any real stockout makes it `stockout` (the transient one worth waiting out; a quota READ of zero
  must not hide it, the quota API can under-report); every failure a quota error makes it `quota` (the copy that names raising the allowance); when the
  creates said neither, a quota read of zero in every candidate zone (`ZoneCandidateResult.QuotaZeroEverywhere`) makes it `quota`, otherwise `stockout`.
  Every zone is still tried.
- Not built: the user-facing "other continents" toggle (the ladder walks every offered zone, the bucket's continent first), per-tier escalation on a click,
  and the last-good-zone memory (#210).

### 3.1 The model and GPU catalog (issue #87)

`GpuCatalog.Shipped` (`app/src/DnaEntropyGraph.Core/Cloud/GpuCatalog.cs`) is the one table of models and GPU tiers: per model the
precision, size, card count, memory and whether it needs Hopper (copied from the worker's `hardware.py`
`MODEL_REQUIREMENTS`, which refuses a run whose hardware disagrees); per tier the machine type, accelerator id, quota metric, card count,
memory, Hopper, Spot-only and the 7B window ceiling. It is a Core table, not a JSON file: it needs no
file, DI or load-failure path, a wrong row fails a test, and `CloudJobRequestFactory.MachineTypeFor` reads it, so the machine a run
starts and the machine the pickers and the estimate name cannot drift apart. GPUs are listed cheapest first.

- **Fit**: Hopper first, then card count, then memory (`GpuFit`). MEASURED (project memory, prototype): Evo 2 7B runs on an L4 and an A100
  only; 1B, 20B and 40B need Hopper FP8 (H100), 40B two of them (`a3-highgpu-2g`, Spot or Flex-start only).
- **Parity with the worker**: `WorkerContractGuardTests.The_gpu_catalog_agrees_with_the_workers_model_requirements` parses `hardware.py`
  `MODEL_REQUIREMENTS` and fails when the model set, precision, Hopper need or card count differ; a second test mutates a copy of the text
  (40B card count) and proves the check goes red.
- **Spot-only tiers**: both A3 shapes (`a3-highgpu-1g` and `-2g`) are `SpotOnly`. Source: Appendix B section 4.1, "not offered on-demand for small
  A3 shapes". THEORY (unverified against a real project): that wording covers the 1g shape too. A run on a SpotOnly tier carries
  `RunOptions.SpotVm = true` and its estimate is asked for Spot (the price list has no A3 rows, so it says no estimate). Mapping `SpotVm` to the VM's
  scheduling is issue #91's `VmSpec.Spot`; until it lands a SpotOnly tier would be requested on-demand, which is why no SpotOnly tier is offered today.
- **Nothing fits**: when no offered card can run the chosen model the page says to choose another model, offers no "Run anyway", and Run stays off.
- **Linking** (`ModelGpuLinker`, pure): a model pick raises an unpinned GPU to the cheapest offered tier that fits; a GPU pick moves an
  unpinned model to the largest offered model that fits (a tie keeps the current one); a pinned side never moves, so the pair can be
  infeasible, which the page warns about and asks the user to confirm. "Automatic" (`CheapestAvailable`) stays automatic in
  `RunOptions.GpuTier` while the cheapest tier is the answer and names the tier otherwise, so a model that needs a bigger card never
  starts on an L4.
- **What is offered is issue #14's switch.** `Offered` per model and tier in the catalog is the only thing to change when #14 (ship
  evo2_40b and evo2_1b_base, or hide them; an owner DECISION, open) is decided. MEASURED 2026-10-15: the app had offered only evo2_7b
  (no model picker existed), so only evo2_7b and the L4, A100 40 and A100 80 tiers are offered, and the model picker is hidden while one
  model is offered. `GpuCatalogTests.Today_only_the_standard_7b_model...` pins that state.
- **Zone availability (not handled here)**: the zone ladder is `us-central1-a/b/c/f` for every tier, and an A100 80 GB or A3 machine type may not exist
  in all of them; see the issue filed from the #87 cold review about how a "machine type not found in zone" error classifies.
- **Accelerator and quota** per tier (`GpuEntry.Accelerator`, `QuotaMetric`) are what issue #414's accelerator-type field and the
  preflight quota check can read instead of the `"gpu"` placeholder; this change does not wire them into `VmSpec`.
- **Window ceiling**: `GpuEntry.ContextCeiling` is 8,192 for the L4 and null (unmeasured, never guessed) for the others; the clamp offer
  of science_and_formats.md section 3 is not built on it yet.

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
| HTTP 412 (unless it positively looks like a precondition conflict, next row), `CONDITION_NOT_MET`, or any message mentioning `constraints/` | `org_policy` | Abort; copyable text for IT |
| HTTP 412 that positively looks like a failed precondition (reason `conditionNotMet`, or "precondition" wording, and no `constraints/` or org-policy wording) | `other` | THEORY (unverified): Cloud Storage uses 412 for an etag or generation mismatch. The caller that sent the precondition decides (the worker-identity bucket write re-reads and retries). One shared rule, `CloudErrorClassifier.IsPreconditionConflict`, used by the classifier, `GoogleApiErrors.KindOf` and the gateway. Every other 412 stays `org_policy`. `docs/ToTest.md` has the row that captures the real shapes. No new enum value: `other` is the existing class for an error nothing else claims, so no `DECISION` was needed |
| HTTP 404 "The resource 'projects/<p>/zones/<z>/machineTypes/<type>' was not found" on an insert (issue #772) | `stockout` | Per-zone; continue the ladder. THEORY (unverified, no live project): this is the documented text, not a recorded payload (`docs/ToTest.md` has the row that captures a real one). Decided in the structured stage so a project id containing `iam` cannot turn it into `permission` through the substring stage; only a *machine type* that is not found counts, any other missing resource stays what it was. It needs a real 404 whose structured code is absent or not-found (a status-less message, or a 404 carrying another code, is left to the other rules; `CloudErrorClassifier.IsMachineTypeNotFound`), and the ladder counts such a zone as "other" for its total-failure verdict so a quota of zero everywhere still reports `quota` and nothing else in the message, once the quoted resource name is cut out, may read as billing, API-off, quota or permission. The fixture entries carry `http_status` and `recorded: false` Before #772 it classified as `other`, which also moved on, so the ladder never stopped on it; MEASURED 2026-10-29 (FakeGcp ladder test): the ladder moved on for other and ended a total failure as stockout already, so the fix is the class itself, not the ladder |
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

### Paging (issue #933)

Every Google list loop in `DnaEntropyGraph.Cloud/Rest` (billing accounts, projects, price catalog, buckets, objects, region quotas, quota preferences,
VMs, disks, accelerator zones, zone operations: 11 loops) reads the next page token through one helper, `PageTokenGuard.Advance`
(`app/src/DnaEntropyGraph.Cloud/Rest/PageTokenGuard.cs`), one guard per walk. It remembers every token it was given, not only the last, so the same
token for ever and a cycle (A, B, A) both fail on the first repeat, with `CloudErrorCodes.ListPaginationStuck` (`LIST_PAGINATION_STUCK`, the one constant `PageTokenGuard.StuckCode` and the delete copy both read), one extra request after the first page (A, B, A: the third).
A short list must never read as a complete one: an empty VM list reads as "no VM" (a duplicate or a leak), an empty quota table as "no quota".
`Guards.Tests/PageTokenGuardWiringTests` scans every `Rest/*.cs` (comments removed, strings blanked) and fails on a next page token read outside `.Advance(`, a string that names the JSON member, a hand comparison of a token, or a `PageTokenGuard` built inside a loop.
Before this, `GoogleStorageGateway.ListOursAsync` looped for ever on a repeated token, the billing and project listings had no guard, and the accelerator
zone list returned a partial answer silently (MEASURED 2026-10-29, read in code; the new tests prove each now fails).

DECISION (agent-made, reversible): the kind of `LIST_PAGINATION_STUCK` is `Network`, everywhere (what compute, quota and storage already used; the price
catalog's `Other` is changed to match). A first draft of this decision chose `Other`; a cold review disproved it (MEASURED 2026-10-29, code read of
`JobReconciler`). `IsNoAnswer` is `Kind == Network`: with `Other` the look for a Running row's VM returns Unknown instead of Deferred, the run is handed to
`CloudJobRunner`, whose own `FindByJobIdAsync` fails the same way, the runner records the row terminal Failed, and `EndVmAfterFailureAsync` cannot list the
VM to end it: a live, billing VM orphaned and its result never downloaded. A repeated token is not definitive like billing or permission, so the reconciler
must defer. Locked by `JobReconcilerTests.A_stuck_listing_looking_for_a_live_VM_defers_the_run_and_leaves_the_row_and_the_VM_alone` (outcome Deferred, row
still Running, nothing written, VM still there; the exception it throws is the real `PageTokenGuard`'s). Consumers of the kind, all read: the reconciler
defers (wanted); `VmProvisioner` treats a Network failure of a create as "adopt by label", not applicable to a listing; `CatalogPricingSource` maps Network to
fallback reason `Network` instead of `Other` (the same one-hour back-off and the same shipped-prices fallback; only the reason label differs, which is not harmful).
Retry (a side effect, kept): `CloudCallPipeline.IsTransient` retries a status-less Network error, but only a gateway wrapped whole in the pipeline is replayed:
in `GoogleCloudGateways.Create` that is the billing gateway alone (compute, storage, quota, projects and pricing route each HTTP request through the pipeline instead,
so a stuck token, which is found after a page's request has completed, is never replayed there; MEASURED 2026-10-29, scripted handler: the compute listing makes 2
requests whatever the retry setting). For billing, a token repeated once that then recovers succeeds on the replayed walk (a fresh walk, a fresh guard: 3 requests), and a
token repeated for ever fails after 8 requests (1 attempt + 3 replays, 2 requests each) with `LIST_PAGINATION_STUCK`, kind Network. THEORY (unverified, no live project):
that a real server's repeated token is ever transient, i.e. that a replay is useful; the bound is what makes it harmless. The guard's message names no list on purpose:
`IsTransient` also reads `CloudErrorClassifier.Classify` over the text, and "the billing account list" classified as Billing and "the quota list" as Quota (permanent: the
replay was silently switched off; MEASURED 2026-10-29, the billing replay test was red with a list-naming message), so a test pins the message to `Other`.To reverse: change the kind in `PageTokenGuard` and the expectations in `ListPagingGuardTests`, `JobReconcilerTests` and the compute and quota gateway tests.
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

### 6.1 The real read (issue #88, item 1)

`GoogleQuotaGateway` (`app/src/DnaEntropyGraph.Cloud/Rest/`, `GoogleCloudGateways.Create(...).Quota`, not wrapped in
`ResilientQuotaGateway`) implements `IQuotaGateway` over two Compute v1 calls, each a single small HTTP request through
`CloudCallPipeline` with `singleRequest: true`:

- `regions.list` (`compute.regions.list`, paged by `pageToken`; a repeated token fails the read instead of returning a partial
  table): every region's `quotas[]` (`metric`, `limit`, `usage`), kept whole, so `NVIDIA_L4_GPUS` and the other GPU metrics, `CPUS`
  and `SSD_TOTAL_GB` are all in the table.
- `projects.get` (`compute.projects.get`): the project-wide `GPUS_ALL_REGIONS` line.

`ReadQuotaAsync(projectId)` returns a `QuotaReadResult` (Core, `QuotaReadResult.cs`): the region table, the global line, and a
`Failures` list. The two reads fail independently and **a failed read is a value, never a zero**. `GpuState(region, metric)` is the
three-way answer:

| Situation | State |
| --- | --- |
| Smaller of (regional limit minus usage, global limit minus usage) is at least 1 | `Available` |
| Either known remainder is below 1 (a global 0 overrides a regional 1; a regional 0 binds without the cap) | `None` |
| The region is listed but has no such metric | `None` (that region offers none of it) |
| `regions.list` failed, the region is not in the reply, the `projects.get` read failed or lacks `GPUS_ALL_REGIONS`, and no known value is below 1 | `Unknown` |

`GetGpuQuotaAsync` keeps its `int` answer (the smaller remainder) and **throws the classified `CloudOperationException`** when the state is
`Unknown`, so a preflight can never read an unreadable quota as "no quota". The accelerator type maps to its regional metric in
`QuotaMetrics.ForAccelerator` (`gpu` and `nvidia-l4` are `NVIDIA_L4_GPUS`); an unmapped type throws rather than guessing.
A metric line the reply carries without a `limit` is `Unknown` for that metric (`QuotaMetric.Limit` and `Remaining` are null), never zero and never dropped
from the table; only a definite zero elsewhere (a global cap of 0) still wins. `FakeGcp` answers both `ReadQuotaAsync` and `GetGpuQuotaAsync` from ONE table
built from `WithGpuQuota`, `WithAllRegionsGpuCap` and `WithQuotaReadFailing(kind)`, so they agree: a scripted read failure makes `GetGpuQuotaAsync` throw the
classified `CloudOperationException` (code `QUOTA_READ_FAILED`) as the real gateway does, and a region or accelerator nobody scripted is the always-succeeds
default of 1 through either.
THEORY (unverified, no live project): that `projects.get` lists `GPUS_ALL_REGIONS` for a project that never raised it (when absent it is a
failed read, `GPUS_ALL_REGIONS_MISSING`, so the answer is `Unknown`). Switched on in production by #609. The request flow
(`quotaInfos.get` eligibility, `quotaPreferences.create`) and the ineligible-account guidance are the sub-issues of #88 listed in the changelog.

**Spot quota (issue #718).** MEASURED 2026-10-15 (docs.cloud.google.com/compute/resource-usage): the Spot twin of the L4 line is `PREEMPTIBLE_NVIDIA_L4_GPUS` (name confirmed), but without a preemptible grant Spot VMs "consume standard quota", and once one is granted they "can only consume preemptible quota". `QuotaMetrics.SpotRegionalMetric` therefore reads the preemptible line only when the region reports it with a limit above zero, else the standard line. No production path reads it yet (the provisioner uses the on-demand metric); a Spot preflight must call it. THEORY (unverified): whether an ungranted region lists the preemptible line as 0 or omits it; both read as standard.

### 6.2 What preflight does with a quota it cannot read (issue #88)

DECISION (agent-made, reversible): an unreadable quota is UNKNOWN, and unknown is not "none". `PreflightChecks` calls `GetGpuQuotaAsync` and:

| The read | The run |
| --- | --- |
| Returns a number of 0 or less | Fails with `quota`, as before. |
| Fails with a project-wide kind (`permission`, `billing`, `api_disabled`, `org_policy`) | Fails at once with that classified error (its own run error code), before any bucket, upload or VM: the create would fail the same way. |
| Fails any other way (`network`, a 5xx, the read's own budget running out, `other`) | UNKNOWN: preflight passes and provisioning goes on. The prototype tried every zone when quota was unknown; quota is pre-filterable, not a stop sign, and a real shortage still surfaces as a classified `quota` error on the create, which the zone ladder handles. |

The whole read has its own budget, `CloudRunSettings.QuotaReadTimeout` (5 minutes; `CloudJobRunner.QuotaReadTimeout`), because it is several sequential
requests (`regions.list` pages, `projects.get`) in one gateway method and the 60 s `CallTimeout` is sized for one. THEORY (unverified, sized from the
pipeline's worst case, not measured): 5 minutes covers a slow but healthy read; one cut by it is a `network` failure and so UNKNOWN. Proven over
`FakeGcp` by `CloudJobRunnerQuotaPreflightTests`; the real-project row is in `docs/ToTest.md`.

### 6.x Asking for the quota (issue #99, the wizard's GPU step)

`GpuQuotaRequester.RequestAsync(project, region, contactEmail)` asks Google for one more `NVIDIA_L4_GPUS` in the region and one more `GPUS_ALL_REGIONS`. Per quota, in
this order: skip it when `ReadQuotaAsync` already shows at least one GPU free (limit minus usage); stop with `Failed` `QUOTA_READ_FAILED` when the read failed (the limit is unknown
and a request built on it could ask for less than the project has: no request is created and the user retries); read eligibility
(`IQuotaGateway.GetQuotaIncreaseEligibilityAsync`, the Cloud Quotas API's `quotaInfos.get` and its `quotaIncreaseEligibility`); only an explicit yes creates
the request (`RequestQuotaIncreaseAsync`) for `max(1, limit + 1)`, one more than the project has now. An ineligible project gets the reason (`NoValidBillingAccount`, `NotEnoughUsageHistory`,
`NotSupported`, `Other`), and an eligibility that says neither yes nor no is a `Failed` with `QUOTA_ELIGIBILITY_UNKNOWN`, never permission. One quota's failure never
stops the other. The preference id is `deg-<metric-slug>[-<region>]` (no date). MEASURED 2026-10-15 (docs.cloud.google.com/docs/quotas/reference/rest/v1/projects.locations.quotaPreferences): "There is only one QuotaPreference resource for a quota value targeting a unique set of dimensions", a preference cannot be deleted, an `etag` that no longer matches is rejected `ABORTED`, and the list `filter` covers state, type and time only. So the gateway lists the project's preferences (paged), matches on service, quota id and dimensions under ANY id (a Console request, an older app version), returns one that already asks for enough (`AlreadyExisted`, nothing written), raises a lower one in place with `PATCH` and its etag (one attempt: a replay meets a changed etag), and creates one only when there is none (a replayed create whose answer was lost meets its own id: `AlreadyExists`, read as `AlreadyRequested`).
The outcomes are `AlreadyAvailable`, `Granted` (approved at once), `Requested` (with Google, review can take days), `AlreadyRequested`, `NotEligible`, `Failed`.
**DECISION (agent-made, reversible):** raw REST over the same bearer-token HTTP stack, not a package: NuGet has no discovery client for this API
(`Google.Apis.CloudQuotas.v1` does not exist) and the gRPC `Google.Cloud.CloudQuotas.V1` would bring its own transport and break the injectable HTTP handler every other
gateway and test uses. Quota preferences are not billable and carry no labels, so Hard Rules 9 and 10 do not apply to them. The API itself (`cloudquotas.googleapis.com`)
must be enabled in the project: while it is not, both calls fail `api_disabled`. The wizard's service step enables it: `RequiredServices.Ids` is `[compute, storage, cloudquotas]` (#705, `GoogleServiceUsageGatewayTests` asserts the batch), so the earlier note that it did not was stale (rc99 review, 2026-10-15).
Quota ids, MEASURED 2026-10-15: the docs' own `CreateQuotaPreference` example for a GPU quota is `GPUS-PER-GPU-FAMILY-per-project-region` with a `region` dimension, and Compute's resource-usage page names the standard L4 quota `GPU_FAMILY:NVIDIA_L4`, so the L4 request is that quota id with dimensions `region` and `gpu_family=NVIDIA_L4` (the earlier guess `NVIDIA-L4-GPUS-per-project-region` is gone). THEORY (unverified, no live project): the `GPUS-ALL-REGIONS-per-project` id (the docs name the global quota only as "GPUs (all regions)"), the `gpu_family` dimension key, and that a fresh project is `isEligible: true` for them; a wrong id is a 404 read as unknown (nothing asked) and a wrong dimension a rejected create. Every Cloud Quotas call sends `X-Goog-User-Project: <selected project>`: MEASURED 2026-10-15 (docs.cloud.google.com/docs/quotas/set-quota-project) that a request to a client-based API can fail when the quota project cannot be identified and that for REST the `x-goog-user-project` header sets it; THEORY (unverified): the docs do not say the Cloud Quotas API is client-based, so the header may be unnecessary (it is harmless: it needs `serviceusage.services.use` on the project the user chose). **Spot:** no preemptible request is made. MEASURED 2026-10-15 (docs.cloud.google.com/compute/resource-usage): Spot VMs consume the standard quota until a preemptible quota is requested, and after a request "the applicable resources can only consume preemptible quota"; asking for `PREEMPTIBLE_NVIDIA_L4_GPUS` would move the project off the quota this request raises, so `QuotaMetrics.QuotaIdFor` answers null for it. `FakeGcp`: `WithQuotaEligibility`, `WithQuotaEligibilityFailure`, `WithQuotaRequestFailure`, `WithQuotaAutoGrant` (moves the
table `GetGpuQuotaAsync` reads), `QuotaCalls` (the order of reads and creates), `QuotaRequests`.

## 7. Least-privilege IAM for the worker service account

The VM runs as the service account `dna-entropy-worker@<project>.iam.gserviceaccount.com`
(`WorkerIdentityNames`, issue #54). A custom role (`projects/<p>/roles/dnaEntropyWorker`)
grants exactly `compute.instances.get`, `compute.instances.stop`,
`compute.instances.delete` and `compute.zoneOperations.get`.

**What the VM actually calls (MEASURED 2026-10-03 by reading `worker/vm/startup.sh`):** only
`DELETE .../instances/<name>` and `POST .../instances/<name>/stop` on itself (both with the
metadata token, both `|| true`), plus Cloud Storage object calls with `curl`. It never reads
its own instance, never polls a zone operation, never sets metadata. So `instances.get` and
`zoneOperations.get` are not needed by the script today; they stay because the issue asks for
them (the worker reading its own state and the operation a stop or delete returns) and they
are read-only. Nothing the script calls is missing from the role.

The role is bound at the project, to the worker account, with an IAM condition:
`resource.type != "compute.googleapis.com/Instance" || resource.name.contains("/instances/deg-")`
(`WorkerIdentityNames.ConditionExpression`). `deg-` is the prefix `VmSpec.VmName` gives every
VM (`deg-<jobId>`). A VM the user named anything else (`other-x`) fails the condition, so the
worker account cannot stop or delete it. The first half passes any resource that is not an
Instance (a zone operation read is checked against a `ZoneOperation`), so the permission that
needs it is not blocked by a condition written for instances. THEORY (unverified, no live
project): that Compute evaluates the condition this way for `instances.stop`/`instances.delete`
and for `zoneOperations.get`; the earlier text of this section and Appendix B wrote
`resource.type == ... && resource.name.extract(...)`, which would also deny the zone-operation
read, and asked for the CEL to be verified once. `docs/ToTest.md` carries that row. On the
results bucket the worker account gets `roles/storage.objectAdmin` and nothing else (a bucket
policy binding, not a project one). No `logging.logWriter`, no SSH-related permission (SSH
itself is never used - see section 9).

**Honest limit of the condition (cold review, round 2).** The condition scopes the account to VMs named `deg-*`, not to
the VM the token belongs to: every worker VM runs as the same account, so one `deg-` VM's token can stop or delete any other
`deg-` VM in the project (never a VM named anything else, and it cannot create one). Per-instance IAM (`instances.setIamPolicy`
at create time) would scope it to the VM itself; it is a follow-up under #56 and is not built. See `threat_model.md`.

The signed-in user separately needs `iam.serviceAccounts.actAs` on this account (Owner/Editor
have it implicitly; a bare Compute Admin role does not). A VM create that fails for that reason
is the `PERMISSION_ACTAS` error: `CloudErrorClassifier.IsActAsDenial` recognises a 403 naming
`iam.serviceAccounts.actAs` or `roles/iam.serviceAccountUser`, `GoogleApiErrors.ToException`
(used for an HTTP error body and for the `error` of a polled operation alike, which `FromOperationError` converts first)
gives it the code `PERMISSION_ACTAS` (still the `permission` bucket, so it still aborts), and the
one action is Copy request for the project owner. **Run row (#604):** the specific code travels on `CloudError.Code`
(`CloudOperationException.Error`), not in a new field. `RunErrorCodes.For(CloudError, kind)` maps `PERMISSION_ACTAS`,
`WORKER_DEFAULT_ACCOUNT_MISSING` and `WORKER_IDENTITY_NOT_APPLIED` to the run codes `permission_actas`,
`worker_default_account_missing` and `worker_identity_not_applied` (each with its own `RunError_*` text), and treats a 403
whose text names actAs as `permission_actas` even when the gateway did not label it. Both ways a run can meet one of these use
it: `VmProvisioner` puts the code on `ProvisionResult.FailureCode` for a refused create, and `CloudJobRunner`'s catch maps an
exception from the worker-identity step. The Runs page shows the message and a **Copy request for the project owner** button
(`RunListItem.CopyOwnerRequestCommand`, only for `permission_actas` with a known project) that copies
`Runs_OwnerRequest_ActAs`, which names `roles/iam.serviceAccountUser` (`WorkerIdentityNames.ActAsRole`) on
`WorkerIdentityNames.ServiceAccountEmail(project)`, or, when an organization policy made the VM run as the project's default
Compute Engine account (`<number>-compute@developer.gserviceaccount.com`), that account: the runner records the account the VM was
asked to run as on the failed row as a trailing `[vm-account=...]` mark in `ErrorDetail` (`WorkerAccountDetail`, no new column) and the
request reads it back. The request also names the signed-in Google account as the grantee (`Runs_OwnerRequest_Grantee`, clipboard only,
never logged) and leaves that clause out when nobody is signed in. THEORY (unverified, no live project): Compute's exact wording for an actAs
refusal; docs/ToTest.md carries the row for the real insert.

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
 a real browser read of the vendor pricing pages) until the first real catalog read is recorded (issue #214's lookup is below; docs/ToTest.md carries the row).
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
- A **pre-run estimate** (issue #98, shipped): `Core/Cost/`. `PricingTable.Parse` reads
 `app/src/DnaEntropyGraph.App/Assets/pricing.json` (schema 1: `asOf`, `source`, `region`,
 `disk` (`usdPerGbMonth` only: the size is the run's own `RunOptions.BootDiskGb`), per-machine `onDemandUsdPerHour` and a nullable `spotUsdPerHour`; a null spot price
 is "unknown", never zero) and returns a `PricingLoadResult` with a `PricingProblem`
 (`FileMissing`, `Unreadable`, `Malformed`, `UnsupportedSchema`, `InvalidValue`), never an
 exception. The app copies the file to `<output>\Assets\pricing.json` (a csproj `Content` item,
 kept by publish) and `FilePricingSource` reads it from `AppContext.BaseDirectory`;
 `Guards.Tests/CostEstimateWiringTests` fails if it is missing from the build output, if
 `ci-app.yml` stops asserting `publish/Assets/pricing.json` (the "publish carries the price
 list" step), or if any `GpuTier` the app can start lacks an on-demand price. The one
 exclusion is H100 (`a3-highgpu-1g`): no figure has been gathered for it and an unsourced
 price would be invented, so its estimate line says there is no estimate for that machine
 yet (#572); the guard fails the day that row is added, so the exclusion cannot go stale. `CostEstimator.Estimate` is pure:
 `minutes * (machine $/h + bootDiskGb * $/GB-month / 730) / 60`. Minutes with history are
 the median of the newest 10 completed cloud runs on that machine on this PC (`RunHistory`)
 as the fixed overhead (boot, pull, model load) PLUS this input's own predict time (2 s per
 kb): a 5 kb and a 5 Mb file never price the same. The history does not record the past
 inputs' sizes, so a past duration cannot be scaled; a long past run therefore overstates
 the overhead a little (THEORY (unverified) until sizes are recorded, #571). With
 none, the `RunTimeModel` gives a range: 5 min (warm) to 12 min (fresh) plus the same predict time.
 The estimate also carries `StoppedDiskUsdPerDay` (the run's disk over 24 h), because the
 default after a run is Stop (Hard Rule 11) and the disk keeps billing while stopped; the page
 shows it as a second line labelled an estimate.
- **Catalog prices** (issue #214): `IPricingCatalogGateway` (`Core/Cloud`) lists the Compute Engine SKUs of the Cloud Billing Catalog
 (`services/6F81-5844-456A/skus`, USD, all pages; the real one is `GooglePricingCatalogGateway`, the only code that touches Google, and `FakeGcp`
 scripts 403, API disabled and network failures; a half-read catalog is a failure, never a shorter list). `CatalogPriceResolver` (pure) turns SKUs into the table:
 for each machine, cores x core rate + GB x memory rate + GPUs x GPU rate, on demand and Spot (`usageType` `Preemptible`), plus zonal Balanced PD
 per GB-month, in the shipped table's region. A SKU that is another region, a commitment, a custom shape or a regional disk is ignored; a component that
 is missing, not positive or priced two ways leaves that machine to its shipped row (Spot becomes null, never zero); a disk price the catalog cannot give (missing, or two prices) keeps the shipped disk price (named in `source`); a catalog that prices no machine is
 `NoMatch` and the shipped list is used whole. `CatalogPricingSource` is the app's `IPricingSource`: `Load()` is instant and never touches the network
 (today's cache, an older cache made beside this exact shipped list and resolver version (the fingerprint below), or the shipped list; a cache dated more than a day ahead of this PC's clock is not trusted, one a day ahead counts as today's); `RefreshIfStaleAsync` asks the catalog at most once a day (cache
 file `pricing-cache.json` in the app data folder, the same schema as `pricing.json`, `asOf` = the day it was read), has a 90 second budget for the whole paged read, and after a failure
 does not ask again for an hour (a sign-in that is gone is retried at once; a network or other auth failure backs off). `CostEstimateService` starts it on each estimate but waits at most 3 seconds for a read still running: that estimate uses what is known now and the read finishes in the background (one at a time, not tied to the page's cancellation), so the next estimate uses the new prices. The read is owned by the service (`Dispose` cancels it; MEASURED 2026-10-29: nothing in the shipped app disposes the container yet, so at exit the process ends the read, which is safe because the cache write is tmp-then-move) and starts on the thread pool, never on the caller's thread. When it finishes with different prices the service raises `ICostEstimateService.PricesChanged` (from a pool thread, only when the machine rows or the disk price differ; a new as-of date alone is not a change), and `NewRunViewModel` asks for the estimate again through `IDispatcher`, so the first estimate of the day does not stay on the shipped prices. The 90 second budget is a budget, not a promise: a very slow link can still run out of it and fall back with reason Network. The Google gateway bypasses the shared circuit breaker (the price list is optional and must not open the breaker that guards creating and polling a run) and fails a listing that repeats any page token seen earlier in the walk as `LIST_PAGINATION_STUCK`.
 `IPricingStatus.Current` is `(Origin: Shipped | Catalog | Cache | CacheStale, AsOf, Reason: None | SignedOut | PermissionDenied | ApiDisabled | Network | Other | NoMatch)`
 for the page to say which list the numbers come from and why it fell back (the page line is issue #814). A kept Spot price (the catalog gave none) is
 named in the table's `source` note. A cache is only used beside the exact shipped list it was made with (a hash of that list is stored in it), so an app update never serves rows kept from an older build.
 MEASURED 2026-10-16 (SKU explorer, public pages cloud.google.com/skus/sku-groups/{on-demand,spot-preemptible}-{g2,a2}-vms and on-demand-balanced-persistent-disk): the
 Compute Engine service id is `6F81-5844-456A` and the Americas SKU descriptions (id) are: `G2 Instance Core running in Americas` (D18E-3563-415F), `G2 Instance Ram running in Americas`
 (F0A4-E3D3-33BD), `Nvidia L4 GPU running in Americas` (A88A-5A60-E821), `Spot Preemptible G2 Instance Core running in Americas` (59A6-9C49-BDDB), `Spot Preemptible G2 Instance Ram running in Americas`
 (BF80-650A-78E6), `Nvidia L4 GPU attached to Spot Preemptible VMs running in Americas` (AEC2-3D5C-61BF), `A2 Instance Core running in Americas` (2922-40C5-B19F), `A2 Instance Ram running in Americas`
 (2390-DCAF-DA38), `Nvidia Tesla A100 GPU running in Americas` (039F-D0DA-4055), `Nvidia Tesla A100 80GB GPU running in Americas` (EDA0-3A70-3138), `Spot Preemptible A2 Instance Core running in Americas`
 (3178-715E-CFB6), `Spot Preemptible A2 Instance Ram running in Americas` (65A3-16DB-D57A), `Nvidia Tesla A100 GPU attached to Spot Preemptible VMs running in Americas` (39D4-516A-0317),
 `Nvidia Tesla A100 80GB GPU attached to Spot Preemptible VMs running in Americas` (DD90-547C-2AAA), and the disk `Balanced PD Capacity` (6AE1-525F-8B80, with `Balanced PD Capacity in <place>` per location).
 Separate `G2 Custom Instance Core/Ram` SKUs exist for custom shapes and are ignored on purpose, as are `Regional Balanced PD Capacity`. The explorer lists no `A2 Ultra` core or memory SKU, so
 THEORY (unverified): a2-ultragpu-1g is the A2 core and memory SKUs plus the 80GB GPU; until that is measured the resolver does NOT price it from the catalog (`MachineShape.CatalogPriced` false, resolver version 4): its shipped row is kept and named in `source`, so no theory-priced row is ever labelled "Cloud Billing Catalog". THEORY (unverified, no live read): the `resourceFamily`/`usageType`/`serviceRegions` values of these SKUs
 (the explorer does not show them), whether a signed-in user's token may list the catalog, and which exact `serviceRegions` the Americas SKUs carry; docs/ToTest.md carries the row.
 Resolver, gateway and fallback behaviour are MEASURED 2026-10-16 only against scripted payloads that use these real descriptions.
- `scripts/gen_pricing_json.py --catalog <pages.json> --as-of <date>` regenerates `Assets/pricing.json` from a catalog dump a person with a project fetched (the
 script never calls Google). Same rules as the resolver; `scripts/tests/test_gen_pricing_json.py` fails if its machine shapes drift from the C# ones.
 Those model constants are THEORY (unverified) until the first GPU acceptance run records
 real numbers, and apply to every tier (an A100 is faster, so its model range overstates).
 An unknown size is `UnknownSize` (no line on the page), history or not, not a guess. A model range that rounds to one figure says "no earlier runs", never "based on your earlier runs". `NoPriceForMachine` and `NoSpotPrice` each have their own copy (not an error the user can fix, so it says the estimate is not available for that machine yet); only an unreadable list says to reinstall. Starting a new check (Treat as RNA, a new validation) clears the line so a Checking pill never shows the old estimate. The
 New run page (`NewRunViewModel.EstimateText`) shows it from the selected, valid file's
 base count, always labelled an estimate. The shipped prices are THEORY (unverified):
 gathered 2026-09-19 for us-central1 from public pages, not checked against the Billing
 Catalog (#214, #303); the Spot prices were read from Google's own pricing pages on 2026-10-16 (#303, MEASURED).
 Still open under #98: the daily Billing Catalog refresh (#214), the live ticker (#217) and
 the actual cost on the Results page (#557).
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

### The consumer side: the Runs page reason line (#459)

- `HistoryViewModel.ReasonFor` turns `RunRecord.ErrorCode` into `RunListItem.ReasonText` through
  `RunErrorCodes.ResourceKey` and `IStringResourceProvider`; `RunsPage.xaml` binds it under the status line.
  A Failed run always shows one (a null, blank or unknown code gets `RunError_other`). Any other finished run
  shows one only when it carries a code, so a Completed run recorded as `lifecycle_unverified` or
  `vm_end_unconfirmed` shows its warning. A run still going shows none.
- `ErrorDetail` is never shown: it is raw exception or API text for the diagnostics zip. The copy names the
  action; the Runs row already has Run again and Download again buttons for the ones that name them.
- Not yet true: `Save diagnostics in Settings` (#106) has no button, so the copy of `RunError_other` and four
  others names a control that does not exist until #106 lands. The run progress and results pages do not
  show the reason yet.

### The wait, the download and the lifecycle check (cold review of #460, 2026-10-02)

- **A transient failure never ends the wait.** The poll loop catches every `CloudOperationException`
  except the project-wide classes (billing, API off, permission, org policy: these end the run at
  once, after a best-effort end of the VM) and looks again on the next tick. The VM and the worker
  carry on without the app, so a network blip of a minute must not cost the user a second run.
  Pinned by `More_transient_503s_than_the_retry_pipeline_absorbs...` (14 scripted 503s behind the real
  retry pipeline and breaker, then a valid result: Completed with the track on disk).
- **`EnsureBucket` budget (issue #600).** `EnsureBucketAsync` is a sequence of up to about ten requests (project get, list, insert, read-back,
  second list, loser delete or config write, patch), each with its own retry backoff inside the gateway, so it runs under its own
  `EnsureBucketTimeout` (5 min, `CloudRunSettings`) and not the 60 s of one request. A cut still lands as a network failure; the next call finds
  the bucket by label and writes a missing config (`GoogleStorageGateway.ConvergeAsync`, proven by
  `A_second_listing_that_keeps_failing_strands_a_created_bucket_and_the_next_call_adopts_and_configures_it`). The duplicate a lost
  two-PC race leaves is not cleaned up by a cut; it stays an empty labelled bucket. A request that never answers is not left to that 5 minutes: see the per-attempt cap in section 12.
- **Per-call deadline.** Every gateway call the runner makes runs under `CallTimeout` (60 s) through
  `Task.WaitAsync`, so a callee that ignores its token is cut too: the preflight calls, every `FindByJobId`, the create-collision `GetVm`, the wait, the lifecycle check, `CancelAsync`,
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
- **The platform's limit is the user's cap plus 15 minutes (issue #89).** `CloudJobRequestFactory` sets `VmSpec.MaxRunDuration`
  to `RunOptions.MaxRunDurationMinutes + 15 min` (`VmGrace`), with `instanceTerminationAction=DELETE` (`VmSpec` rejects anything else).
  The worker enforces the user's cap itself (manifest `limits.maxRunSeconds`, `runner.py` `_RunDeadline`): past it no further record or input starts,
  finished work is uploaded and `result.json` carries `RUN_TIME_LIMIT` (mapped to the app's `run_time_limit`), so a worker that is merely slow
  stops its own VM before Compute Engine deletes it; the VM's own deadline is the backstop for a worker that hangs.
  **The arithmetic.** The platform's `maxRunDuration` = cap + 15 min counts from VM start (THEORY (unverified) for a VM that was stopped and started again, see ToTest #94). The worker therefore counts its own cap from VM start too: it reads `/proc/uptime` once at start (only when `DEG_VM_NAME` is set, i.e. on an app-created VM, so a laptop's uptime never shortens a local run) and treats that as already elapsed. Boot (up to 8 min), image pull (15) and manifest download (15) are stage allowances that come OUT of the cap, not on top of it, so the worker's graceful stop always lands at cap from VM start and the 15 min grace is left for the upload, `result.json` and the VM's own stop/delete. The verdict is taken at a checkpoint (a window or contig boundary, or an input start), never from a clock read after the work ended: a last window that finished in time whose upload ran past the cap is a finished run. `startup.sh` reads the VM limit
  (`deg-max-run-min`), so keep-alive is capped against the platform deadline, not the cap. A VM that was seen created, is now gone, and whose
  `maxRunDuration` has passed with no `result.json` and no worker error code in `status.json` is recorded `run_time_limit` (not `vm_unhealthy`):
  the run hit its limit while the app was not looking (a closed laptop lid, a resume). When the app is looking it ends the run first
  (`result_timeout`, above). THEORY (unverified): Compute Engine deletes at exactly `creationTimestamp + maxRunDuration`; ToTest #89 measures it.
  The reconciler (a run found after an app restart) judges a gone VM with no result the way `VmLostAsync` does: it reads `status.json` first (a worker error code is the cause), and records `run_time_limit` only when the worker's own last heartbeat shows the VM lived to its limit (`updatedAt` minus `startedAt`, one clock, within 10 minutes of `maxRunDuration` + grace; THEORY (unverified): the gap between VM boot and worker start is under that). The row's age is NOT evidence (it counts every hour the app was closed after the VM died); with no usable `status.json` the code is `vm_unhealthy` (issue #679 review). The row's `VmName` starts as the planned name (the diagnostics redaction key for a run that fails before any VM exists) and the runner overwrites it with the VM the run used (a reused VM keeps its old name); a failed write of it is swallowed, never a reason to abandon a live VM.
  Already built before #89 and now covered by name: labels and `DELETE` guard (`VmSpec`), the Insert fields (`GoogleComputeGateway`),
  the post-result stop/delete verification (`VmTerminator.EnsureVmEndedAsync`, `lifecycle_unverified` / `vm_end_unconfirmed`).
- **Per-stage deadlines (issue #90).** `HeartbeatWatch` also remembers the `stage` in `status.json` and when it last CHANGED (on the app's clock, any
  writer: the startup script's `booting`/`installing` count). A stage past its deadline (`booting` 8 min, `installing` 15, `restoring-cache` 15,
  `model-loading` 15, `uploading` 10; `CloudJobRunner.StageDeadlines` overrides them) ends the run as `worker_stage_stuck` and the VM per the
  user's choice, whatever the heartbeat says: this catches a hung main thread under a ticking heartbeat thread while it is in one of those
  stages (a hang inside `running` is the progress rule below). Numbers are the contract's (THEORY, unverified on a real VM).
- **Forward progress (issue #723, app half of #522).** `WorkerStatusReader` reads `windowsDone` and `lastProgressAt`. In the `running` stage, when either field is present and
  `(stage, windowsDone, lastProgressAt)` has not changed across looks for `ProgressStallTimeout` (default 20 min, job_contract.md section 5; compared between looks, never with the
  app's clock) while the heartbeat keeps changing, the watch returns `worker_progress_stalled` (stop, via the same stall prompt). A status with neither field (an older worker) is never
  judged by it. THEORY (unverified): 20 min is above any healthy L4 window; ToTest #522.
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
    the real Compute gateway fills it (`GoogleComputeGateway`, issue #56, from `creationTimestamp`). The default deadline
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

**On launch (issue #59)**: `JobReconciler` (Core/Cloud) is what calls that resume path for every run a killed app left
non-terminal. It reads the VM by label and `result.json`, decides (architecture.md section 6 has the table), and hands the run to
`RunAsync`; it never creates a VM for a run past `Provisioning` whose VM is gone. `FakeGcp.WithCloudNotConnected()` now also makes
`FindByJobIdAsync` throw `CLOUD_NOT_CONNECTED`, because a listing cannot succeed with no connection: the reconciler reads that, and a network
error, as "no answer" and leaves the row alone instead of calling the run lost.

**Lifecycle enforcement and idle VMs (issue #530)**: the same `JobReconciler` pass also revisits runs that already ended. A finished run's VM
found by job-id label in a state its `lifecycle` label forbids is deleted (`delete`), stopped when still running (`stop`), or ended per
`afterKeepAlive` once its keep-alive expiry has passed (Hard Rule 11); the expiry mirrors `startup.sh` `keep_hold`: only a run the worker finished with exit 0 (Completed, PartiallyCompleted) is held for its window, Failed (exit 2) and Cancelled (exit 3) end at once. Lookups have a 30 s deadline, delete and stop their own 5 minute one (they are operations), and a Deferred, refused or throwing row or VM never ends the pass: the rest are still handled. Separately, `IComputeGateway.ListByInstallationAsync` (real gateway:
`instances.aggregatedList` filtered on `labels.app` and `labels.installation-id`) feeds an idle sweep: a stopped VM of this installation older
than `idle_stopped_vm_hours` is deleted (default 72, range 1 to 720, no off switch: 0, a negative or an unreadable value is 72, above 720 is clamped to 720; DECISION #555). `VmDescriptor` now carries `Labels` and `StoppedAt` (the instance's
`lastStopTimestamp`; a gateway that cannot fill it makes the sweep skip the VM, never delete it). Only VMs with our app label AND this
installation's id are touched (two users may share one account). The pass runs at launch and again when the pipeline reports the
connection is back (`ReconcileOnReconnect`, single-flight: reconnects while a pass runs coalesce into at most one follow-up pass, and a pass ends once the lifecycle and the sweep have run and the reattached runs are handed to their own drivers, not when those runs end (`JobReconciler.BeginReconcileAsync`; launch still awaits them through `ReconcileAsync`); the observer
is rarely invoked by the pipeline because reconciler calls bypass the breaker, so #559 adds the reconnect probe below). THEORY (unverified): `aggregatedList` returns `lastStopTimestamp` for a `TERMINATED` instance
and its label filter matches as `FakeGcp` models it; nothing here has touched a real project (docs/ToTest.md).

**Reconnect probe (issue #559)**: a launch with no network defers every run it could not look at, and the breaker's `OnClosed` (the only source of
`OnConnectivityChanged(offline: false)`) almost never fires because every reconciler call passes `bypassBreaker: true`. `ReconcileOnReconnect` therefore
also probes: while `JobReconciler.HasDeferred` is true (a reattach ended Deferred and no later pass judged that job, or the last lifecycle enforcement
had a Deferred outcome) it runs the same pass again after 30 s, then 60 s, doubling up to a 5 minute cap, on the `TimeProvider` in the container (tests move virtual time);
it stops when nothing is deferred and starts over at 30 s next time. The launch pass runs outside this class, so `AppStartup.BeginAsync` calls
`StartProbeIfDeferred()` once the pass has judged every run (`BeginReconcileAsync`'s outer task now also waits for each run's look at the cloud, so `HasDeferred` is settled when
it ends; a resumed run is judged as it is handed to its driver and does not hold it, and neither does a run whose half-done cancel is being finished: it is judged at the start of that work, so a stuck cancel never holds
the single-flight slot, and `ReconcileOnReconnect` and `AppStartup` look at the probe again when the reattached runs end, because such a run can still end Deferred). The call is in a `finally`, so a launch pass that throws (a network
timeout reading the run table) still arms the probe: the pass has already recorded itself deferred. Passes can overlap (the launch pass is outside the single flight): each takes a generation and only the latest-started pass may write
the pass-level deferred state (`_lifecycleDeferred`, `_reattachDeferred`: the reattach could not even read the run table), so an older pass that ends last never overwrites a newer one's answer. A run's own mark (`_deferredRuns`) is different: the pass that DRIVES the run owns it whatever its generation, and a pass that was Skipped because another drives the run neither sets nor clears it. If `BeginReconcileAsync`'s lifecycle step throws, the failure is logged and the reattached runs are still returned (never orphaned). A probe whose own `hasDeferred` check throws re-arms on the current delay (logged) rather than stopping. DECISION (agent-made, reversible): a probe that re-runs the pass, over
(a) `NetworkChange.NetworkAvailabilityChanged`, which cannot be tested without a network stack and fires on LAN changes that say nothing about Google reachability, and
(b) feeding reconciler failures into the breaker, which changes what `bypassBreaker` means for every caller. Cost while offline: one pass (a few failing lookups, no retries beyond
the pipeline's own) per backoff step, never while nothing is deferred. THEORY (unverified): the 30 s first delay and 5 minute cap are reasonable for a laptop waking from sleep; nothing measured them.
What counts as deferred (DECISION, agent-made, reversible; two cold reviewers disagreed): a run or a lifecycle pass is deferred if and only if the failure is a
network class (no connection, `CloudErrorKind.Network`, or a lookup deadline). Any other exception while judging a run ends as `ReattachAction.Errored`: logged
by error class, row untouched, left for the next launch, and it does not keep the probe alive. A lifecycle pass that throws sets deferred by the same rule and
rethrows. A deferred run that later became terminal (cancelled, deleted) is pruned from the deferred set at the start of the next pass.

**Reconciler follow-ups (issue #575)**: `FileDiagnosticsLog` caps `logs\app.log` at 1 MB and keeps one rotated `app.1.log` (about 2 MB on disk at most), and swallows every exception but a fatal one,
because it is called from inside the reconciler's catch blocks. A rotation that cannot move the file (the copy is held open or read-only) keeps appending up to twice the cap, then drops lines until a rotation works, so a failing rotation cannot grow the log without bound; every control character and the Unicode line and paragraph separators in a field become a space, so one event is always one line. A lifecycle step that throws no longer orphans the reattach: `EnforceLifecycleAsync` itself rethrows, and `BeginReconcileAsync` logs the error class and still returns the task for the reattached runs, so
`WhenIdleAsync` waits for them. Item 4 (the "handed off" claim) was already delivered by #559, not #575: the outer pass task ends only after every candidate run has been looked at and registered as a driver in `ActiveRuns`
(#559's `judged` signal), so two passes' reattach listings do not overlap, and `TryStart` stays the backstop against a double drive; the test for it is a regression guard, green before #575. Item 5 is fixed, not accepted: `ReconcileOnReconnect` keeps one task per pass whose runs are still going and drops it when they end, instead of nesting `WhenAll`.

**Reattach bounds (issue #551)**: a reattached run stopped by a user cancel waits for that cancel to settle for at most the mutation timeout plus a 30 s margin (5 min 30 s by default; `JobReconciler`'s `cancelSettleTimeout` overrides it; on its `TimeProvider`), so the report never gives up on the very edge of a legitimate slow cancel that succeeds;
on expiry it logs the error class and reports the row as it is (non-terminal), so one cancel stuck in a gateway call that ignores its token holds back only its own run's outcome (the other runs are not blocked
on a wait that never ends). The reported `ReattachAction` is derived from the row's final phase (`ActionForFinalPhase`): Cancelled is `CancelFinished`, and a `Failed*` action is reported only for a row that is Failed. A reattach that a user cancel took over (or that was finishing a cancel it found half done) and that reports a row which is not terminal is `CancelInterrupted`, never `CancelFinished` and never `Resumed` (`ActionAfterUserCancel`). The interruptions that reach it: the settle wait expired, the app began to shut down during the wait or while the cancel was being finished (the shutdown is reported, not thrown, so it does not hide the other runs' outcomes), the user cancel failed, or the terminal write did not land. The row stays non-terminal (normally Cancelling), so the next launch's reattach finishes the cancel and records the terminal state (Hard Rule 11; `ReattachOnStartupTests` checks this through the production container). Known gap, #610: a cancel cut short before `VmCanceller` records Cancelling (its driver hung) leaves the row Running, and the next launch resumes the run instead of finishing the cancel. `CancelInterrupted` is a snapshot, not a final answer: the cancel's delete cannot be cancelled once started (`VmCanceller` retries it with no token after a shutdown), so it may still complete after the report. A terminal row is reported from its phase; a Failed row from a cancel that could not confirm its VM gone (`cancel_failed`, or any Failed row reached while finishing a cancel) is `CancelFailed`, never `Resumed` or `CancelFinished` (DECISION #599, comment). DECISION #599 (agent-made, reversible) records `CancelInterrupted` and `Errored` as separate values over reusing `Deferred`, which would keep the reconnect probe alive for a run that is not waiting on the network.
The settle deadline is DOCUMENTED, not derived (DECISION agent-made, reversible): a legitimate cancel is the sum of the in-flight create settle, one delete or stop (up to the mutation timeout), the verify and the token-less retry (`VmCanceller`), which can exceed mutation timeout plus 30 s, so `CancelInterrupted` can be reported for a cancel that then succeeds. That is safe because the report is only a snapshot and, since the same review, `ActiveRuns.TryStart` refuses to start a driver for a job while a user cancel of it is running (`_cancels`), so a later pass (a reconnect) reports that run `Skipped` and cannot resume it, and a VM the cancel is deleting is never re-provisioned (#551 review r4). A shutdown during a resumed (not cancelled) run is reported too, as `Resumed` with the non-terminal phase the row has, so it does not hide the other runs' outcomes (DECISION #599, comment); the next launch resumes it.
Decision (agent-made, reversible): a per-run deadline over per-run outcome callbacks, because no consumer reads outcomes yet and the deadline is the smaller change.

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

### 11.x Slow stages and stalls: the notice and the three-way choice (issue #675)

`CloudJobRunner.OnNotice` (`Action<RunNotice>`) is told `SlowStage` once per stage when it passes 75 percent of its deadline (`HeartbeatWatch.SlowFraction`, stage, time in stage,
deadline), `StallDetected` when the heartbeat watch fires and a question is opened, and `StallCleared` when the worker recovers while it is open. `CloudJobRunner.OnStall`
(`StallDecision`: `StallPrompt` in, `StallChoice` out: `Stop`, `Delete`, `KeepWaiting`) replaces "end the VM at once" for `worker_stage_stuck`, `worker_progress_stalled`, `worker_heartbeat_stale` and `worker_no_heartbeat`;
null keeps the old behaviour. While the question is open `ResultWaiter` keeps polling `result.json` and does not repeat it; Stop and Delete end the run under the same code with the VM
stopped or deleted whatever the run's own end choice was; `KeepWaiting` restarts every watch allowance (`HeartbeatWatch.Snooze`) so the question comes back after a fresh one; the result
limit still applies, so waiting is never unbounded (Hard Rule 11); a handler that throws or is cancelled counts as no answer and the VM is ended the run's own way. A box with no GPU is never
asked about. The question's token is cancelled on every exit. Graceful cancel: `docs/job_contract.md` section 6. `FakeGcp`: `WithWorkerIgnoringCancel`, `WithUploadFailure`.

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

**Per-attempt cap (issue #600).** A request that never answers would otherwise wait for the caller's own deadline (the 5 minute `EnsureBucketTimeout`) or the HTTP client's 100 s default. `CloudCallPipeline.ExecuteAsync(..., singleRequest: true)` adds an innermost Polly timeout of `CloudRetryOptions.AttemptTimeout` (30 s) to that call, so each hung attempt is cut (a `TimeoutRejectedException`, transient: retried, counted by the breaker) and the call ends as kind `network`, code `TIMEOUT`, after the retries. Only the single small metadata requests pass it: `GoogleStorageGateway` list, insert, get, delete, patch and the ~300 byte config write (`GoogleStorageGateway.cs`, `singleRequest: true` sites) and `GoogleProjectCatalogGateway.FetchAsync`. **Never capped:** `GoogleStorageGateway.UploadAsync` and the two downloads (a large input or result must be allowed to take as long as the link needs; the runner's size-scaled `UploadTimeout` and the result wait bound them), and anything that wraps several requests (the `Resilient*` wrappers).

Retrying a mutating call is safe only because of two earlier decisions: `CreateVmAsync`
carries a deterministic request id (issue #257, so a replay is idempotent) and stop/delete
are idempotent. An upload is replayed only when its stream can seek (the decorator rewinds
it); a non-seekable stream gets one attempt and no 401 replay.

Every retry is reported to `ICloudCallObserver` (operation, attempt, delay, status, kind,
the API error text, never a sequence, file name or email). `CloudRetryLog` keeps the last
200 in memory. MEASURED 2026-10-29: Serilog is not wired into the app; the
log is not in the diagnostics zip. `logs\app.log` (`FileDiagnosticsLog`, `DiagnosticsTraceListener`) is; forward `OnRetry` there when wanted.

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
  `{sub, email, needsSignIn, projectId?}`, no token). `projectId` is the project that account chose in the wizard (issue #520):
  `IGcpAccount.SelectProjectAsync` validates it with `ProjectIdGenerator.IsValid` and stores it in the active account's record, so it
  follows the account on a switch, survives a restart, is dropped with the record on sign-out and is kept when an expired account
  signs in again; a file written before it existed loads with no project. `SelectedProjectId` reads it. While every gateway is
  `FakeGcp`, a signed-in account with no chosen project still answers `GoogleAccountOptions.ProjectIdUntilSelectionExists`
  (`"fake-project"`); a chosen project always wins, and the fallback goes with the real gateways (#609). Token refresh is done by Google's `UserCredential` and written back through
  the same store, so a restart needs no browser.
  `AccountRegistry` (issue #616) writes `accounts.json` under a session-wide named mutex (`Local\DnaEntropyGraph.accounts.<hash of the path>`, so shared by every copy one user runs in one Windows session, and an elevated copy may not be able to open it: that is `ACCOUNTS_FILE_LOCKED`, a ToTest row) to a unique temp name, waiting up to 10 s for a save and 150 ms for a read, so two saves at once serialise; the lock is cross-process by construction, the test drives two
  instances on two threads. A missing file is a normal empty list. A file that does not parse is moved to `accounts.json.bad` (`.bad.1`,
  `.bad.2`, never over an earlier one) before anything can overwrite it, and the first sign-in, project choice or switch afterwards fails
  once with `ACCOUNTS_FILE_UNREADABLE` (action Sign in again) before a browser opens. `Load` reads under the same mutex and retries a failed read
  (3 tries, 40 ms steps; a load blocks at most `AccountRegistry.WorstCaseLoadBudget`, about 270 ms). A file that still cannot be read (held open by another program or copy) is not empty and not set aside: the registry
  is `Unreadable`, an `Update` refuses to replace it, the service does not cache the empty list, and the sign-in, project choice or switch fails with
  `ACCOUNTS_FILE_LOCKED` (action Try again); a read is not retried more often than every 2 s while it is locked (the result is remembered, so a UI read never waits on the file each time), and a token request or sign-out in that state fails with the same code instead of reading as signed out; the next call after the window reads the file again and succeeds once the hold is gone. A save that cannot take the lock in time, or whose mutex the system refuses to open, is `AccountsFileLockedException` (not a disk failure) and reaches the user as `ACCOUNTS_FILE_LOCKED` from sign-in, switch and project choice; a disk failure keeps `SIGNIN_STORAGE` or `PROJECT_SAVE_FAILED`. The save runs on a pool thread (`UpdateAsync`) so a 10 s lock wait never blocks a dispatcher, and a load's worst case is about 270 ms (150 ms lock wait plus two read-retry sleeps), because the state getter can run on the UI thread. A damaged file the folder will not let the app move aside reports `SIGNIN_STORAGE` (the folder is the problem, Try again would never help). That holds when a running copy finds the damage in a change too (`AccountsFileFolderException` from `Update`, mapped to `SIGNIN_STORAGE` for sign-in, switch and project choice, and the cached list is dropped). A set-aside notice not yet reported (the write after it failed) survives the next load, and a successful sign-in clears a pending notice, because the user has just signed in against the list as it is. A sign-in stores its token inside the same registry lock as its list change, not before it, so a sign-out's check-and-delete cannot remove a token stored between its check and its delete. The token file and the list change together, under the registry lock: an expiry mark or a sign-out first takes a stamp (a SHA-256 of the token file's bytes, so equal write times cannot hide a new token) and, for a sign-out, reads the token; inside the `Update` lambda it compares the stamp with the file as it is then, and if another copy signed the account in meanwhile it leaves the list unchanged and deletes and revokes nothing. The token file is deleted by `Update`'s `afterWrite`, still under the lock and only after the list was written (a failed list save leaves the token), and a failed delete is reported as `SIGNIN_STORAGE` after the revoke ran. The revoke is of the token read with the stamp, through a flow with no token store, so it can never be the fresh token of a sign-in that landed later. Known limits (not changed): a sign-out that loses the race to a fresh sign-in answers `true` with no message and leaves the account signed in; a token refresh by another copy between the stamp and the lock makes the sign-out a no-op for the same reason; a refresh token the Google flow's own refresh superseded in that window is neither revoked nor kept; and the flow's own refresh writes (`_store` handed to the Google flow) stay outside the registry lock. A save also deletes leftover `accounts.json.<guid>.tmp` files older than 5 minutes (including the old fixed `accounts.json.tmp`), best effort.
  THEORY (unverified): a virus scanner or a second app instance is what damaged the file in the field. Two processes changing the list at once
  no longer lose an update (next paragraph, issue #620).
  **Concurrent changes (issue #620).** Every change to the list goes through `AccountRegistry.Update(change)`, which, under the same mutex, reads the
  file again, applies `change` to what it finds and writes the result; `GoogleAccountService` never saves its cached list. So two copies of the app
  changing different things (a sign-in in one, a project choice in the other) both persist. Each change touches only its own fields of its own
  account: a project choice sets `projectId` on the account that was current when it started and leaves `activeSub` alone; a switch sets only
  `activeSub`; expiry sets only `needsSignIn`; a sign-in replaces its own record (keeping the `projectId` the file has now) and makes it current;
  a sign-out removes its own record and keeps another copy's current account. **Same account, same field: the later writer wins** (the later one
  to take the lock; two project choices end with the second). A project choice for an account another copy signed out fails with `SIGNIN_EXPIRED`
  and writes nothing. The account is never put back when the file does not list it, because that can be one another copy is signing out right now
  (entry removed, revoke pending, token file not yet deleted); a list that lost the account by hand therefore also reads "sign in again". A copy's
  cached list is refreshed from what it wrote, not from other copies' later changes: another copy's change shows here after a restart or the next
  change made here. `Save` no longer exists: `Update` is the only writer, and the registry's `Unreadable`, `QuarantineFailed` and `QuarantinedTo`
  properties are set by `Load` alone (a test reads them); the service acts on the outcome `LoadWithOutcome` and `Update` hand back, never on shared
  flags. An update that is the first to find the file damaged sets it aside (`.bad`) and, if its change then gives up, fails with
  `ACCOUNTS_FILE_UNREADABLE` itself; if the change went through, the next change says it once. Marking an account expired takes a stamp (a SHA-256 of the token file's bytes) first; under the registry lock it
  compares the stamp with the file as it is then and, only if equal, marks the account and deletes the token file under that same lock, so a fresh sign-in of the same account in another copy keeps
  its new token and its listing. A token file that cannot be deleted does not change the answer: the user still gets `SIGNIN_EXPIRED` and the UI is told.
  **Locked at launch (issue #624).** A list that cannot be read is not shown as signed out: `IGcpAccount.IsAccountsFileLocked` is true, the setup
  page's sign-in card shows `ACCOUNTS_FILE_LOCKED` with a Try again button (it calls `RecheckAccountsFile()`, which re-reads the file at once and does not
  open the browser, so a user who is already signed in is never sent to Google), and the service raises `AccountChanged` when the file turns readable, whether
  from Try again or from an ordinary read after the 2 s window. Chosen: Try again only, no timer (a timer would read the file on a pool thread behind the UI's
  back and needs a clock seam every test would drive; the message says what to do and the button is one click). No new strings: the message and the button
  label are the existing `AuthError_ACCOUNTS_FILE_LOCKED` and `AuthAction_TryAgain`.
- **Errors** are `AccountAuthException` with a code from `AuthErrorCodes`; the English is `AuthError_<code>` in
  `Resources.resw`. `SIGNIN_EXPIRED` (Google answered `invalid_grant`) deletes the dead token file, sets `needsSignIn`
  on the account and offers **Sign in again**. `SIGNIN_NETWORK` does not expire anything. `OAUTH_CLIENT_MISSING` and
  `OAUTH_CLIENT_INVALID` name the installer as the action: the user never has this file, the build does.
- **Granted scopes (issue #259).** Under granular consent a user can untick `cloud-platform` and still be signed in. MEASURED 2026-10-16
  (https://developers.google.com/identity/protocols/oauth2/native-app): the token response `scope` field lists what the token grants,
  a user `may not grant your app access to all` requested scopes, and `Incremental authorization is not supported for installed apps`,
  so `include_granted_scopes` is not used; the re-ask is the same flow with `prompt=select_account consent`. At sign-in a `scope` without
  `cloud-platform` (or none) fails `CONSENT_INCOMPLETE` before anything is stored; on a refresh a `scope` that names scopes without it
  deletes the token file, sets `needsSignIn` and fails the same way (a refresh answer with no `scope` keeps the stored grant). The action
  is **Sign in again and allow access**, which is the ordinary sign-in command. Decision #18 (single `cloud-platform` scope) is what makes this one check enough.
- **Switching** only changes `activeSub`. **Sign out** revokes the refresh token at Google, removes the account entry and deletes the token file under the registry lock (the same stamp guard: a
  sign-in of the same account by another copy since the sign-out read the token makes it a no-op), then revokes the token it read, whatever Google said, and makes the next remaining account current.
  `AccountChanged` is raised whenever the list changed, also when the token file then could not be deleted (that failure is `SIGNIN_STORAGE`).
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
  the token source on every request (so a refreshed token is used by the replayed call) and puts each gateway behind the
  pipeline: single-call gateways (billing) in their `Resilient*` decorator, the two that are a POST plus polled reads
  (project catalog, service enablement) by sending each HTTP call through the pipeline themselves. Google.Apis's own 503 retry is switched off (`ExponentialBackOffPolicy.None`) so
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

`IProjectCatalogGateway`: `ListActiveProjectsAsync`, `GetProjectAsync`, `CreateProjectAsync`. It has no production
consumer and no DI registration until the wizard (#99) and its ViewModel (#56) arrive; it is not wired here on purpose.

- **List.** `GET /v3/projects:search?query=state:ACTIVE`, following `nextPageToken`. Projects labelled
  `app=dna-entropy-graph` sort first (`ProjectCatalogOrder`), then by name. An account with no projects gets an empty
  list, which the wizard answers with one click on Create.
- **Create.** `POST /v3/projects` with `projectId` `dna-entropy-<8 random lowercase alphanumerics>`
  (`ProjectIdGenerator`), display name "DNA Entropy Graph", labels `app=dna-entropy-graph` and `installation-id`
  (the VM-only labels `job-id`, `model`, `app-version` and `lifecycle` do not apply to a project). The response is a
  long-running operation, polled with `OperationPoller` (1 s doubling to 10 s, 5 minute deadline); a timeout is
  `OPERATION_POLL_TIMEOUT`, classed `network`.
- **One retry per HTTP call, never per composite.** `GoogleProjectCatalogGateway` is not wrapped in
  a `Resilient*` decorator (none exists; `ResilientGatewayTests` fails if one is added): it sends every call through `CloudCallPipeline` itself. The mutating
  `POST /v3/projects` is retried alone; each `operations.get` is its own retried idempotent read. A 429 or 5xx on a
  poll read therefore re-reads and never re-POSTs. The poll deadline is wall-clock (`GoogleCloudOptions.TimeProvider`)
  and covers the time inside each read: `OperationPoller` hands every read a token that ends at the deadline, so a hung
  GET (the HTTP client would wait about 100 s) ends at the deadline as `OPERATION_POLL_TIMEOUT`, not at the client's
  timeout. The caller's own cancel still surfaces as a cancel. `CloudCallPipeline.IsTransient` also never replays a
  spent poll deadline (defence in depth: no caller runs `PollAsync` inside the pipeline today; pinned by a direct
  pipeline test). The last read is never given a fresh deadline: once the waits have spent the deadline the poll ends
  as `OPERATION_POLL_TIMEOUT` without another read. A finished operation with no error and no project in its response is read back with
  `projects.get` for the requested id; if that finds nothing the error is `OPERATION_NO_RESULT`, never a success with
  an empty id.
- **Replay safety.** The POST is replayed after a dropped connection. A `409 ALREADY_EXISTS`
  is followed by a `projects.get`: a project of ours (it carries the app label) is returned, anyone else's is an
  error. One wizard click therefore never makes two projects.
- **Errors.** A project-limit refusal is `PROJECT_QUOTA` (kind `quota`, never retried; action: pick an existing
  project); an organization-policy refusal is `ORG_POLICY_BLOCK` (kind `org_policy`; action: copy the message for IT).
  A permanent create failure carries a setup code whose button fits (never the Try again catch-all): `PERMISSION`
  (kind `permission`; Copy request for the project owner), `API_DISABLED` (Turn it on), `NO_BILLING` (Link billing).
  A per-minute rate limit is retried and never `PROJECT_QUOTA`, whether it carries the ErrorInfo reason
  `RATE_LIMIT_EXCEEDED` or only says so in a RESOURCE_EXHAUSTED message ("Quota exceeded for quota metric 'Requests' ...
  Requests per minute", no details); THEORY (unverified): the project-limit refusal carries a QuotaFailure or the word
  "quota" without per-minute or per-second wording. A PERMISSION_DENIED 403 that merely quotes a `constraints/` id is a
  permission error, not an organization-policy one, so `GetProjectAsync` still answers "not visible"; only an
  ORG_POLICY reason, a 412 that is not a positively identified precondition conflict (issue #54), or the wording on a non-PERMISSION_DENIED error is `org_policy`. `GetProjectAsync` answers
  null, with no request, for an id outside Google's project-id grammar.
- **Not proven without a real account:** `docs/ToTest.md`.
### Billing check and link (issue #51, wizard step 4)

`IBillingGateway`: `GetBillingStatusAsync`, `ListOpenBillingAccountsAsync`, `LinkProjectAsync`; `BillingSetup` (Core) is the policy over it.

- **Requests.** `GET /v1/projects/{id}/billingInfo`; `GET /v1/billingAccounts?filter=open=true` (paged; a closed account is
  also dropped client-side); `PUT /v1/projects/{id}/billingInfo` with `{"billingAccountName": "billingAccounts/..."}`.
  Google omits `billingEnabled` when it is false, so an absent value reads as off. Billing is "enabled" only when an
  account is linked and `billingEnabled` is true.
- **Policy.** On: nothing is linked. Off with one open account: linked for the user, then the status is read back
  (a link Google accepted that did not turn billing on is `LinkedButStillOff`, code `BILLING_STILL_OFF`, action Pick another billing account, with exactly the other open accounts in `Accounts`; never `NO_BILLING`, which would loop the user back to Link). When there is no other open account to pick the outcome is `FixLinkedAccount`, code `BILLING_ACCOUNT_OFF`, action Fix billing account (`BillingLinks.ForProject`), never "pick another" with nothing to pick. A failure listing accounts after a link that worked (a `CloudOperationException`) also yields `FixLinkedAccount` with an empty list, never a throw out of `LinkAsync` (issue #566). A project that already has an account linked while billing is off is never linked over unasked: `EnsureAsync` answers `ChooseAccount` (the other open accounts, carrying code `BILLING_STILL_OFF` because the linked account is off; a plain several-accounts choice carries no code) or `FixLinkedAccount`, so a re-check cannot loop on `LinkProjectAsync`. Several: `ChooseAccount`,
  and `LinkAsync` links the one the user picked. None: `NeedsAccount` with `BillingLinks.ForProject(projectId)`, the
  console page for that project; calling `EnsureAsync` again after the user adds a payment method is the re-check.
  The wizard action for the code `NO_BILLING` is that link (`SetupAction_LinkBilling`).
- **No permission.** The error reason and kind decide first (a Billing API that is off is `api_disabled`, a billing quota 403 is `quota`, billing off is `billing`, a 403 naming a non-billing permission such as `resourcemanager.projects.createBillingAssignment` is a plain `permission`); only a remaining permission 403 on the link is `BILLING_NO_PERMISSION` (kind `permission`). The classifier (issue #539) no longer reads the word "billing" in a permission denial as billing-off, only reads a permission-denial wording on a 403 or an error with no status (a 412 or 409 that happens to say "does not have permission" keeps its org-policy or already-exists meaning), and a 403 counts as quota only when it says a quota was *exceeded* and does not read as a permission denial: Google's standard USER_PROJECT_DENIED 403 ("Caller does not have required permission to use project ... or use another project to pass your quota and billing") is `permission`, never quota, and on a create never `PROJECT_QUOTA`. The action is a copyable request:
  `SetupBillingRequestText` (with `{project}` and `{account}`) filled by `BillingRequestText.Fill`.
- **Not here yet.** The wizard page (#99) and the health row that must go green on its own after a link: nothing in
  the shipped UI calls this until then. `ResilientProjectSetupGateway.IsBillingEnabledAsync` (the preflight step) is
  still backed by the fake; the composite real `IProjectSetupGateway` that delegates it to `GetBillingStatusAsync`
  lands with #52.
- **Proven only by a real account:** `docs/ToTest.md`.
### Enable services (issue #52, wizard step 5) and the real preflight gateway

`IServiceEnablementGateway`: `IsServiceEnabledAsync`, `EnableServicesAsync`. `RequiredServices.Ids` is
`compute.googleapis.com`, `storage.googleapis.com`, `cloudquotas.googleapis.com` in that order (enabling Compute also
creates the project's default network).

- **Requests.** `POST /v1/projects/{id}/services:batchEnable` with `{"serviceIds": [...]}` returns an operation;
  `GET /v1/operations/{name}` is polled every 5 s (`OperationPoller` with its new `fixedInterval`, the doubling
  backoff would be wrong for a documented cadence); once the operation is done,
  `GET /v1/projects/{id}/services/{service}` is asked every 5 s until each reads `ENABLED`. A finished operation is not
  a ready service, so each service is checked after the operation, but both phases share ONE 5 minute deadline for the
  whole call (two separate polls could wait twice that), and the deadline starts BEFORE the `batchEnable` POST, so the
  POST, its retries and their HTTP timeouts share it too: the whole call waits at most one deadline. A deadline that
  ends the POST is the same `OPERATION_POLL_TIMEOUT`; the caller's own cancel stays a cancel. The deadline is
  wall-clock and covers the time inside each read (a hung GET ends at the deadline, not at the HTTP client's 100 s).
  A timeout in either is `OPERATION_POLL_TIMEOUT`, classed `network`.
- **One retry per HTTP call, never per composite.** `GoogleServiceUsageGateway` is not wrapped in
  a `Resilient*` decorator (none exists; `ResilientGatewayTests` fails if one is added): the `batchEnable` POST is retried alone, and each `operations.get` and
  `services.get` is its own retried idempotent read. A 429 or 5xx while polling re-reads and never re-POSTs; a poll
  read that keeps failing ends the call as `network` after one POST. The caller's own cancel still surfaces as a cancel.
- **Errors.** The reason and kind decide first: a 403 that says Service Usage is off is `api_disabled`, billing off is `billing`, an organization policy is `org_policy`. A remaining plain permission 403, on the call or as `PERMISSION_DENIED` inside the operation, is `NOT_PROJECT_OWNER` (kind
  `permission`; action: create a project of your own). Enabling Compute on a project with no billing keeps the billing
  kind (Google answers a precondition failure that names billing), so the wizard sends the user back to step 4 rather
  than to "ask the owner". A Service Usage API that is itself off reads as `api_disabled`.
- **The preflight gateway.** `GoogleProjectSetupGateway` implements the existing `IProjectSetupGateway` from the
  three real gateways (project state from Resource Manager, billing from Cloud Billing, the Compute API from Service
  Usage). It is not wrapped in `ResilientProjectSetupGateway` (the catalog and service gateways it is built from
  already retry per HTTP call, and a second layer would replay the enable POST when a poll read fails; billing arrives
  already wrapped). `GoogleCloudGateways.Create(...)` returns it as
  `ProjectSetup`, so every call a run's preflight makes (`GetProjectStateAsync`, `IsBillingEnabledAsync`,
  `IsComputeApiEnabledAsync`, `EnableComputeApiAsync`) now has a real implementation; the quota read does since #88 (section 6.1), the storage gateway since #53,
  and the token refresher does not, so production still resolves everything to `FakeGcp` (#56, #520).
- **Not here yet:** the wizard page, and the health row "Compute Engine" turning green by itself (#99).
- **Proven only by a real project:** `docs/ToTest.md`.

### The results bucket (issue #53, wizard step 5)

`IStorageGateway` (`EnsureBucketAsync`, `UploadAsync`, `DownloadAsync`, `TryDownloadAsync`); the real one is
`GoogleStorageGateway` over `Google.Apis.Storage.v1` (Apache-2.0), returned as `GoogleCloudGatewaySet.Storage`. Production
still resolves the fake until #56 switches DI. It is never wrapped in `ResilientStorageGateway` (the ordinary decorator for a
single-call gateway): `EnsureBucketAsync` is a list, a create, a read-back and possibly a patch, and a whole-method retry would
replay the create. Each HTTP call goes through `CloudCallPipeline` on its own, as the project and service gateways do.

- **Name and place.** `deg-<projectNumber>-<rand6>` (6 lowercase base32 characters), in the region-group multi-region
  (`GoogleCloudOptions.BucketLocation`, default `US`; data residency #149 changes it). The project NUMBER comes from
  `projects.get` (`name: projects/<number>`), via `GoogleProjectCatalogGateway.GetProjectNumberAsync`; a project the account
  cannot see is a `permission` error and nothing is sent to Storage.
- **Settings asked for.** Uniform bucket-level access on, `publicAccessPrevention=enforced`, and two lifecycle rules:
  Delete when age >= the retention (`GoogleCloudOptions.ResultsRetentionDays`, the user's "Cloud results retention",
  `RunOptions.CloudResultsRetentionDays`, default 90) for objects matching prefix `jobs/`, and Delete at age 365 for `cache/`.
- **Labels.** `app=dna-entropy-graph`, `installation-id`, `app-version` (sanitized), `lifecycle=results`. `job-id` and `model`
  are left off: a bucket serves every run and every model, the same exemption a project has. DECISION (agent-made,
  reversible): see #582; the carve-out is recorded under Rules 9 and 10 in `hard_rules.md`. A missing installation id fails before any request (Hard Rule 10).
- **Applied is not present.** After an insert, and after a patch, the bucket is read back and compared: UBLA, PAP, and both
  rules with the configured ages. A difference fails with `BUCKET_CONFIG_NOT_APPLIED` (kind `other`) naming what differs; the
  name is not returned and no `app-config.json` is written. The next call finds the labelled bucket, sees it drifted, and patches it.
- **Discovery by label, adoption.** `buckets.list` with prefix `deg-` for the project, keep the ones labelled
  `app=dna-entropy-graph`, prefer this installation's, then the oldest, then by name. A second PC of the same installation, or
  another installation in the same project, adopts it: no insert. An adopted bucket that reads back drifted (for instance the
  user changed the retention on the other PC) is patched and read back; one that reads back right is left alone. The patch
  keeps every lifecycle rule that is not ours (see the next point for what counts as ours) and
  replaces only ours. Two installations with different retention settings never shorten each other: see the next point.
- **Never shorten the `jobs/` or `cache/` age.** DECISION (agent-made, reversible; issue #597): an adopting installation may LENGTHEN the shared
  bucket's `jobs/` and `cache/` ages, never shorten them, because shortening makes Cloud Storage delete other installations' files
  (results, the weights cache) early (Hard Rule 14). A Delete rule at or above the configured age (`jobs/`: the retention; `cache/`: 365) is not
  drift (no patch); a shorter one is lengthened to the configured age; a patch made for any other reason keeps the longest own age it found for
  each prefix. So the bucket holds the longest age any installation asked for. A rule counts as ours only if its condition is exactly an age and
  the one prefix with a Delete action; a user's `jobs/` rule with any extra condition (storage class, live state, noncurrent time, suffix...)
  is neither counted toward our age nor replaced.
  **Where the retention comes from (issue #114).** `settings.json` key `results_retention_days` (`ResultsRetentionSetting.Days`; absent or
  unusable is 90, above 3650 is clamped), read at each use through `GoogleCloudOptions.ResultsRetentionDaysSource`, which `ServiceRegistration`
  sets for the real build. So a change reaches the bucket on the next `EnsureBucketAsync` (the start of the next run: that call lists, compares and
  patches), and the same value is what the call's own read-back compares the rule against. The gateway reads the option once per `EnsureBucketAsync` (an `AsyncLocal` snapshot) because each read hits `settings.json`: a change landing mid-call must not make a bucket look drifted or fail the read-back. A separate Cloud page health comparison does not exist on this branch. The Settings "Cloud results" group (`CloudResultsSettingsViewModel`: 7, 30, 90, 180, 365 days, plus a hand-edited value) writes the key and says
  the change applies at the start of the next run and that fewer days never shrink a longer bucket. A fixed value at startup, the earlier state, left the rule at 90 whatever was saved.
  Consequence: lowering retention in Settings against a longer bucket is silently ignored today. #598 owns the fix: an explicit, user-confirmed
  shortening, and the user seeing that the bucket keeps the longer age (copy names the action).
- **Two PCs racing.** Both can list nothing and both insert. After its insert and read-back, a call lists again (same preference
  order); if the preferred bucket is not the one it just made, it deletes its own (still empty: the config is written after this
  check) and adopts the preferred one. A delete Google refuses (not empty, because the other PC adopted it meanwhile) is
  swallowed and the bucket stays labelled. A bucket this call did not create is never deleted. Two installations in one project
  each prefer their own bucket, so they may keep one each; that is the same accepted behaviour as before.
- **409 on insert.** Our own insert replayed after a dropped connection also answers 409: if the named bucket reads back as
  ours it is kept (its read is the read-back); otherwise (403 or not ours) a new suffix is drawn. Five names at most, then
  `BUCKET_NAME_TAKEN` (kind `already_exists`).
- **`app-config.json`** at the bucket root: schema, installation id, app version, both retentions, creation time (built with
  `System.Text.Json`, so ids are escaped). Written on create and again on every adopt or repair, always with
  `ifGenerationMatch=0` ("only if absent"): a 412 means the file is already there and stands, so a first write that failed is
  made good by the next call. THEORY (unverified): Cloud Storage answers a failed precondition (`ifGenerationMatch`, `ifMetagenerationMatch`)
  with 412, and an organization-policy denial is also a 412 whose message carries `constraints/`, so only a 412 without `constraints/`
  is swallowed and the other surfaces as `org_policy`; the documented shapes are not captured from a real project (a ToTest row covers it). It is not
  rewritten when a retention changes (the lifecycle rule on the bucket is the truth; a `retention change patches the rule` flow is #114).
- **Objects.** `UploadAsync` is a resumable `objects.insert` (the stream is rewound for a replay; a stream that cannot seek is
  tried once). `DownloadAsync` and `TryDownloadAsync` are `objects.get?alt=media` read into memory (manifests, `status.json` and `result.json`
  are small; the multi-GB weights cache is the worker's, #496). Result files (a probability matrix can be hundreds of MB) never go through them: issue #588 added
  `TryDownloadToFileAsync(bucket, key, path)`, which the Google client copies (one range-less GET, MEASURED 2026-10-28 over the scripted handler) into the caller's `.part` file, so memory stays flat at any size
  (MEASURED 2026-10-28: a generated 256 MB body allocates under 32 MB, the in-memory path allocated 672 MB). It answers false for a 404, and every failure,
  retry attempt or cancel deletes the partial. The gateway checks no md5/crc32c; `RunTransfer` and `RunCloudResults` verify the finished `.part` file against the size and
  SHA-256 `result.json` lists, then move it into place, so a corrupt or short file never carries the final name. There is no byte-range resume: a retry restarts the file.
  Both consumers go through `StreamedDownload.RunAsync`: a unique `<name>.<guid>.part` per attempt (two downloads of one file never share a temp file), a STALL
  deadline (`CloudRunSettings.DownloadStallTimeout`, 90 s without a byte landing cancels the attempt, reported as `network`) plus a total cap derived from the size
  (256 KiB/s floor, 5 minute minimum, 30 minutes when the size is unknown), so a silent socket cannot hang a run and a slow live link is not cut. On a timeout the inner
  token is cancelled and the callee awaited (30 s grace) before the partial is deleted. `RunTransfer` never overwrites a file already on disk (Hard Rule 14): identical bytes
  are kept, anything else is renamed `name (changed time).ext` first, as the History re-download does. `DiskRunFileMover.Move` retries a transient sharing violation (an
  antivirus scan holding the new file) five times with a growing pause. Round 2 (cold review): only the app's own temp names (`<file>.<12 lowercase hex>.part`) older than a day are swept, only in the run folder and the folders a result file lives in (never a recursive walk, never through a junction or symlink), so a user's own `x.part` survives (Hard Rule 14); file times are real file-system times, so the cutoff uses the real clock, not the injected `TimeProvider`. `RunTransfer` reuses a recorded run folder only while it is strictly inside the run's output folder and not a reparse point (the `RunCloudResults` rule). The stall watcher treats an `IOException` while reading the temp file's length as "no progress observed yet" (a probe that keeps failing still ends in the stall deadline). `ChangedFileAside.Move` moves to the next name at once only when the name is taken; a failure on a free name (a sharing violation the mover already retried) is thrown, and the whole search is bounded at 5 s. If the final move fails after a changed file was renamed aside, `RunTransfer` puts the user's file back (as `RunCloudResults` does), and a run that kept a changed file aside finishes with `changed_files_kept_aside` ("Open the output folder"). Round 3: ONE containment rule, `RunOutputRoot.IsContained` (strictly inside AND no junction or symlink on any component below the root, `NoReparseBetween`), is used by `RunTransfer` and `RunCloudResults` for the recorded run folder, `StalePartSweeper.Sweep` refuses a root that is itself a reparse point, and both consumers refuse a result file whose destination folder passes through a junction below the run folder (nothing is written, renamed or moved through it). The count of changed files renamed aside lives in a `DownloadTally` that survives a failure partway through, and `RunOutcomeRecorder` reports it on every path: the finished run's `changed_files_kept_aside` code, and on a failed run, a failed download or an unconfirmed VM end as a sentence in the run detail beside the primary code (Hard Rule 13: the headline code and its one action are unchanged). A refused restore (`UnauthorizedAccessException`) no longer replaces the original error. A callee that ignores cancellation has its temp file deleted once it lets go (the continuation observes its exception). The destination race between two downloads of one file is not closed: two renames of one file are serialized only by the file system, and the loser's move fails and is reported as a failed download (known, bounded, rare). Every `IStorageGateway`
  in the Cloud assembly must declare its own `TryDownloadToFileAsync` (a Guards test), because the interface default buffers. `ResilientStorageGateway` wraps only `FakeGcp`; the real
  gateway is never wrapped, so retries do not nest. `TryDownloadAsync` answers null for a 404 only; every other failure
  throws, so a transport error never reads as "the worker has not finished".
- **Error roster.** `BUCKET_CONFIG_NOT_APPLIED` and `BUCKET_NAME_TAKEN` are `SetupErrorCodes.BucketConfigNotApplied` and
  `BucketNameTaken` (in `SetupErrorCodes.All`, `Resources.resw`, `copy_catalog.md` and `triage_diagnostics.py`); both name Try
  again. A project the account cannot see is `PERMISSION`, whose message names Copy request for owner.
- **Retention is validated.** `ResultsRetentionDays` outside 1 to 3650 throws `ArgumentOutOfRangeException` before any request
  (0 would delete job results at once). The default is `ResultsBucket.DefaultRetentionDays`, which `RunOptions.CloudResultsRetentionDays` reuses.
- **Proven only by a real project:** `docs/ToTest.md`.

### Compute gateway (issue #56)

`Rest/GoogleComputeGateway` implements `IComputeGateway` over `Google.Apis.Compute.v1` (Apache-2.0; the same REST-over-gRPC
choice as above, DECISION #535) and `GoogleCloudGateways.Create(...).Compute` is it, NOT wrapped in `ResilientComputeGateway`:
every HTTP request goes through `CloudCallPipeline` on its own (like the project and service gateways), so a transient error
on one poll retries that poll and never replays the insert. A second wrapping layer would replay the whole create, so
production DI must not add one (#609). **Production DI still resolves `IComputeGateway` to `FakeGcp` until #609**; nothing in
the running app calls this class yet.

- **Project.** Create carries `VmSpec.ProjectId`; get, stop, delete, find and list take the optional `selectedProjectId`
  argument of `Create` (production will pass `IGcpAccount.SelectedProjectId`, #609); none selected throws
  `InvalidOperationException` and sends nothing. A create whose `VmSpec.ProjectId` differs from the selected project (or with none
  selected) is refused as `PROJECT_MISMATCH` before any request, so a VM can never be made where the later lookups do not look.
- **Create.** `VmSpec.ToLabels()` runs first (Hard Rule 10; `VmSpec` also rejects any `TerminationAction` other than `DELETE`), then a spec with no
  `ServiceAccountEmail` is refused by name (`WORKER_SERVICE_ACCOUNT_MISSING`, kind `other`) before any request: the run path fills it from the worker
  identity (#54) via #606/#609, and nothing guesses `deg-worker@<project>`. Then `instances.insert` is built with: the six standard labels,
  `scheduling.maxRunDuration` (seconds), `instanceTerminationAction` DELETE, `onHostMaintenance=TERMINATE`, `automaticRestart=false`, every
  `VmSpec.Metadata` item (the startup script and `deg-*` keys) plus `block-project-ssh-keys=true` (section 9), a `pd-balanced` 150 GB boot disk with
  `autoDelete` and the VM's labels on the disk (Hard Rule 10), the default network with an external address (the VM pulls its image), and the spec's service account with the `cloud-platform` scope.
  `VmSpec` does not carry image or disk, so they are fixed in `ComputeVmShape`: the DLVM family for EVERY machine type, CPU included, because
  `startup.sh` needs docker, python3 and a writable root (Container-Optimized OS has none of them; DECISION #615), and `startup.sh` passes `--gpus all` only
  when `deg-expect-gpu` is true. Each `CreateVmAsync` call fixes ONE random `requestId` at its start and every pipeline replay of that insert POST carries it
  (Compute answers a replay with the same operation); a NEW call gets a new id, never one derived from the VM name: Compute replays the
  ORIGINAL operation for a repeated id within its idempotency window, so a derived id would return an old stockout to a retry, or an old DONE operation
  (no VM made) to a re-create of the same `deg-<job>` after a delete. Instance names are unique per zone, so a new call after a lost response is answered 409
  (`already_exists`), which the runner adopts. THEORY (unverified): an abandoned create (the provisioner gave up) can still be finishing in Compute,
  so a later retry may adopt an instance whose original operation then fails; the run then ends `vm_unhealthy` rather than trying the next zone.
- **Operations.** insert, stop and delete return a zone operation, polled with `OperationPoller` (1 s doubling to 10 s,
  `OperationDeadline`) until `DONE`. `operation.error` becomes a `CloudOperationException` classified by
  `CloudErrorClassifier` alone (code, `httpErrorStatusCode`, message), across EVERY entry of `errors` (the most specific kind wins: stockout, quota,
  permission, billing, api_disabled, org_policy, already_exists, then generic), so `ZONE_RESOURCE_POOL_EXHAUSTED` is `stockout`
  and `QUOTA_EXCEEDED` is `quota`. A refused HTTP call (billing off and API off arrive as a 403 on the insert) goes
  through `GoogleApiErrors`, which uses the same classifier. A spent deadline is `OPERATION_POLL_TIMEOUT` (`network`), and a create whose operation said DONE but whose VM then reads 404 is
  `CREATE_OUTCOME_UNKNOWN` (`network`): in both the VM MAY exist, so the error is retryable (a retry lands on 409 `already_exists` if it does) and **the runner
  must list by label (`FindByJobIdAsync`) before trying another zone**.
- **Get, stop, delete.** A 404 on get is `null`; a 404 on stop or delete, or an operation that reports not found, is
  "already gone", not an error. A 404 on stop or delete means "not in that zone" (the caller may have the wrong zone), not "the VM does not exist anywhere":
  `VmTerminator`'s label re-check (`FindByJobIdAsync`, sound because of the next point) is the authority on whether it is really gone.
- **Find and list.** `instances.aggregatedList` with `filter=(labels.app = "dna-entropy-graph") AND (labels.job-id = "<id>")`
  (or `labels.installation-id`), with `returnPartialSuccess=true` (added to the query by hand: `Google.Apis.Compute.v1` has no typed property for it; THEORY
  (unverified) that without it Compute fails the whole list instead of filling `unreachables`), following `nextPageToken` across every zone (a repeated token fails
  with `CloudErrorCodes.ListPaginationStuck` (`LIST_PAGINATION_STUCK`), kind `network` so the reconciler reads it as no answer, never a silent truncation; the delete copy reads the code and does not tell the user to check the internet); a value no label can hold (matched with `\A...\z`, so a trailing newline is refused) returns nothing
  without a request, and each returned VM's labels are re-checked client-side. A page that lists `unreachables` (zones Compute could not read) fails
  the WHOLE listing with `ZONES_UNREACHABLE` (`network`, retryable) and never returns the partial list: an empty answer feeds `VmTerminator` ("confirmed gone") and
  the runner ("adopt or create"), and one unreachable zone would otherwise leak or duplicate a billable VM. Zone is parsed from the instance's zone URL;
  `Labels` and `StoppedAt` (`lastStopTimestamp`) are filled, `CreatedAt` from `creationTimestamp`.
- **Reuse of a stopped VM (issue #94).** The default after a run is Stop (Hard Rule 11), so the next job finds this installation's stopped VM and
  starts it (about 2 minutes) instead of paying for a new VM and an image pull. `VmProvisioner` does this AFTER the adopt-by-label look and BEFORE the
  zone ladder: it lists the installation's VMs (`ListByInstallationAsync`, by label, Hard Rule 9) and takes the most recently stopped one that is
  `STOPPED`/`TERMINATED`, has the same `model` label and machine type (`VmDescriptor.MachineType`), and whose last job's run row is terminal or absent
  (`CloudJobRunner.JobIsFinishedAsync`; a history that cannot be read answers "not finished", so a VM that may belong to a run underway is never taken).
  `IComputeGateway.ReuseVmAsync` then does, in this order: `instances.get` (refuses a VM that is not stopped with `VM_NOT_STOPPED` before any change),
  `setLabels` (the CLAIM, FIRST: the `job-id` label becomes this job's, with the label fingerprint read at the start, so of two jobs racing for one VM only
  one passes and the loser, answered 412, has written nothing), `disks.setLabels` on the BOOT disk (issue #678: the disk keeps the old job's labels from its creation, so it is read for its own label fingerprint and relabelled (a regional disk through the regional API; no boot disk found throws before any write; a 412 on either label write is re-read and accepted when the labels already equal the target, which is a lost answer to our own write); a failure is a start failure and nothing later is written), `setScheduling` (this job's `maxRunDuration` and `DELETE`; it has no fingerprint, so a racer
  that lost the claim must never have written it), `setMetadata` (the new startup script and `deg-*` attributes, with the fingerprint read at the start), then
  `start`. The composite is NOT idempotent as a whole: `ResilientComputeGateway` attempts it once (no retries, no 401 replay; the real gateway already retries each
  HTTP request itself), because a replay after a lost answer meets a RUNNING VM, answers `VM_NOT_STOPPED` and used to delete the VM that had just started. A
  `Network` failure of the reuse is an unknown outcome: the provisioner looks by label and adopts the one VM that is booting or running for this job. The reuse is
  tracked as an in-flight create, so a cancel waits for it to land and then deletes the VM. THEORY (unverified, ToTest #94 measures it): the platform's `maxRunDuration` clock restarts at each start; a
  reused VM keeps its `creationTimestamp`. The app is safe under either reading. For a VM this run started (a create or a reuse) the result deadline counts the runner's OWN elapsed clock from just before the request (`RunProgress.VmStartMark`); it never subtracts a platform timestamp from the laptop's wall clock, so a clock an hour fast cannot fire it early. Only a VM the run adopted has no mark and uses `VmDescriptor.RunClockStart` (`lastStartTimestamp`, else the creation) against the laptop's clock PLUS a skew (issue #691): `GoogleComputeGateway.GetVmAsync` reads the answer's HTTP `Date` header into `VmDescriptor.ServerNow`, and `ResultWaiter` keeps a `PlatformReading` (the server time and a MONOTONIC mark taken then) from the last look that found the VM; "platform now" is `ServerNow + monotonic elapsed`, never a wall-clock difference stored once (an NTP step or a resume from sleep between two looks would break that). A server time more than a day from the laptop's clock is refused (a broken proxy or clock), and so is an answer carrying an `Age` header (a cached replay). The same reading decides whether a VM that is GONE (`ResultWaiter.VmLostAsync`, `VmClockBasis`) passed its run limit: `platform now >= RunClockStart + maxRunDuration`, so a laptop an hour fast or slow, or one that slept, still records `run_time_limit` and `vm_unhealthy` correctly. A gateway that reports no usable server time (or a VM never seen) uses the laptop's own clock, the old behaviour. PRECISION: `Date` has one-second resolution (up to 1 s behind) and left the server before the answer crossed the network (0.1 to 1.5 s more), so platform now runs at most about 2.5 s BEHIND true, never ahead: nothing reads `run_time_limit` early, and a VM that vanished within that margin of its limit may read `vm_unhealthy`. THEORY (unverified, no live project): the real `Date` header is within that margin, and the monotonic clock keeps counting through sleep on Windows; ToTest #691 measures both. If the clock does NOT restart, a VM created days ago is deleted by the platform soon after its start: the run ends in a recorded terminal state (`vm_unhealthy` or `run_time_limit`) with nothing left billing, and `FakeGcp.WithRunClockFromCreation` tests exactly that. The VM keeps its old name (`deg-<earlierJobId>`; an instance cannot be renamed): `VmSpec.ExistingVmName` carries it and
  `RunProgress.Resolve` makes every later look at, and the end of, the run use it; discovery stays by the label. A start that fails is never a failed run
  by itself: whatever carries this job's label is deleted (the claim may already have been made), then a stockout, quota or any other per-VM failure
  falls through to the create ladder, while billing, API-off, permission, org-policy and network failures abort like the ladder's. Only one VM is tried.
  THEORY (unverified, no live project): that `setScheduling` accepts `maxRunDuration` on a stopped instance; that a stockout on a start reads like one on
  an insert; that the old VM's boot disk still carries the OLD job's `job-id` label (only the instance's labels are rewritten: a finding, not fixed here).
  Not built: the run page's "Starting your computer from yesterday" line (a run-page string, owned by the Presentation lane).
- **THEORY (unverified, no live project):** that `maxRunDuration` is accepted on a standard-provisioning VM with these
  scheduling settings; that the `labels.job-id = "x"` filter syntax matches; that the DLVM image family boots and runs docker on a machine with no GPU;
  the real label filter syntax and the shape of `unreachables`. `docs/ToTest.md` carries the cpu-vm row. The reuse (like a create) is registered as in flight immediately BEFORE the gateway call is made (`VmProvisioner.TrackInflightCreate`, a placeholder that completes with the real task), because the gateway's synchronous part runs before it returns its task: MEASURED 2026-10-16 (#784), a cancel that looked in that window settled nothing and the VM then landed RUNNING under a Cancelled record. A cancel that arrives earlier (between the claim and the start of the call) is not in flight yet; it is covered instead by the cancel's label lookup, which finds the claimed VM by its job-id label.

### The worker identity (issue #54)

`GoogleIamGateway` (IAM v1, Resource Manager v3 and Cloud Storage v1; `Google.Apis.Iam.v1` 1.77.0.4285, Apache-2.0, the
version NuGet resolved online because the offline cache did not hold it) implements `IWorkerIdentityGateway`. Like the
catalog and the bucket gateways it is never wrapped in a `Resilient*` decorator (a test fails if one appears): it creates an
account and a role and edits two policies, and a whole-method retry would replay the creates. Each HTTP call goes through
`CloudCallPipeline` itself.

- **Order.** Read the project (its number names the fallback account; a project the user cannot see fails first), then the
  account, then the role, then the project binding, then the bucket binding.
- **Account.** `serviceAccounts.create`. A 409 is adopted (confirmed with a `get`). An organization-policy refusal
  (`iam.disableServiceAccountCreation`, which classifies as `org_policy`) falls back to the default Compute Engine account
  `<projectNumber>-compute@developer.gserviceaccount.com` with the same role and bindings, and the result carries
  `WorkerIdentity.NoteCode = WORKER_DEFAULT_ACCOUNT`: the wizard shows `SetupError_WORKER_DEFAULT_ACCOUNT` as a yellow note
  with Continue, not as a failure. THEORY (unverified, no live project): that account may already hold a broad role (in older
  projects often Editor; organizations with `iam.automaticIamGrantsForDefaultServiceAccounts` enforced do not grant it), which the
  app cannot narrow, and the note says "may". If the default account is missing too, the code is `WORKER_DEFAULT_ACCOUNT_MISSING`. Any other refusal (a plain 403) does not fall back. THEORY (unverified, no live project): the exact wording of
  the org-policy refusal; the classifier keys on the `constraints/` id, and the gateway also accepts the bare
  `iam.disableServiceAccountCreation` name. A 409 on create followed by a 404 on the read (the account vanished between the
  two) creates once more, then fails with `WORKER_IDENTITY_NOT_APPLIED`. The default account also receives
  `roles/storage.objectAdmin` on the results bucket, which every workload running as it holds (see `threat_model.md`); the
  yellow note says so.
- **Role.** `roles.create`. A 409 reads the role back: `deleted=true` is undeleted (a deleted custom role keeps its id for about
  7 days), a role whose permission set is not exactly the four is patched with `updateMask=includedPermissions` and read back,
  and a patch that does not read back fails with `WORKER_IDENTITY_NOT_APPLIED` ("applied is not present", as for the bucket).
- **The answer is read.** Each `setIamPolicy` returns the policy it stored. The project answer must hold the role, the member
  and the `deg-` condition; the bucket answer must hold `roles/storage.objectAdmin` and the member (no condition). A 200 whose
  answer lacks the binding fails with `WORKER_IDENTITY_NOT_APPLIED` (Try again) instead of reporting success for a binding that
  is not there.
- **Policies.** Conditional bindings need policy version 3: the read sends `requestedPolicyVersion=3` and the write sets
  `version=3`. A binding is its role plus its condition; it is added only when missing, so a second run writes nothing, and
  nothing the app did not add is touched (other bindings, audit configs). The write carries the etag it read; a 409 re-reads
  and re-applies, five attempts at most. THEORY (unverified): Cloud Storage words the same etag conflict as a 412, so a 412
  that positively looks like a failed precondition (the shared rule in section 5) is treated alike (re-read and retry, three
  writes at most, then `WORKER_IDENTITY_NOT_APPLIED`); any other 412, a constraint-naming one included, stays `org_policy` and
  is not retried. A default account that "does not exist" at `setIamPolicy` was deleted or disabled (preflight already enabled Compute, so
  "Turn it on" could not help and would loop): it is `WORKER_DEFAULT_ACCOUNT_MISSING` at once, with no wait, whose one action
  is a link to the project's Service Accounts page (`ServiceAccountLinks.ForProject`), where the user or their administrator can
  restore it or allow the app to create its own. A just-created account
  can be briefly invisible to `setIamPolicy` (400 "does not exist"); that case alone waits on the injected delay (2 s, 4 s,
  ... ) and re-reads, six tries and five waits at most, then fails with `WORKER_IDENTITY_NOT_APPLIED` whose button is Try
  again. The wait applies only to an account this call just created: an adopted or default account that "does not exist" is a
  real failure and surfaces at once (Try again could never fix it): the default account as `WORKER_DEFAULT_ACCOUNT_MISSING`, an
  adopted one as the raw error.
- **Hard Rule 10 and labels.** A service account and a custom role cannot carry labels (Google's IAM has no label field on
  either). They are found by their fixed ids, not by label, which Rule 9 allows because they are not compute resources and are
  per project, shared by design: a second PC adopts them. The carve-out is recorded in `docs/hard_rules.md`; no guard covers
  labels on these two, so there is no allowlist entry. Everything that bills (the VM, the bucket) keeps its labels.
- **Caller (issue #606).** `CloudJobRunner.CompleteSpecAsync` calls `EnsureWorkerIdentityAsync(projectId, bucket)` after
  `EnsureBucketAsync`, and `VmProvisioner.ProvisionAsync` invokes it only after its adoption lookup (`FindByJobIdAsync`) found
  no VM, once, before the first create. It sets the returned `ServiceAccountEmail` on the spec it creates with
  (`with { ServiceAccountEmail = ... }`); without it `GoogleComputeGateway` refuses the create (`WORKER_SERVICE_ACCOUNT_MISSING`).
  A run that adopts a VM, or whose job already finished, never calls IAM, so an IAM failure can never end a healthy working
  VM (cold review of #606). The step runs under its own `CloudRunSettings.WorkerIdentityTimeout` (5 minutes, not the 60 s
  `CallTimeout`), because the gateway is a dozen or more HTTP calls in one method (account create, up to 6 tries with 2 s to 12 s
  visibility waits, role create and read-back, two policy edits of up to 5 attempts) and abandoning it at 60 s would leave it
  writing policies after the run failed `network`. THEORY (unverified): 5 minutes is sized from that worst case, not measured. A refusal is a `CloudOperationException` that ends the
  run through the ordinary classifier (`permission`, `org_policy`, ...) before any VM is created. `WORKER_DEFAULT_ACCOUNT` is a
  note: the run continues and the row carries `RunErrorCodes.WorkerDefaultAccount` (`worker_default_account`, copy
  `RunError_worker_default_account`) as an `ErrorCode` on a row that still completes, which the Runs list shows as its warning line
  (#459); a later failure or `lifecycle_unverified` replaces it. DI registers `IWorkerIdentityGateway` as `FakeGcp` (never wrapped
  in a `Resilient*` decorator); the real gateway switch is #609.
  **DECISION (agent-made, reversible):** the call lives in the runner, not a setup-wizard step, because this codebase has no
  bucket step in the wizard (the bucket is made in the runner) and the account binds the bucket, so the bucket must exist first;
  the wizard may also call it for early feedback later. It is not run from `UploadInputsAsync` (no VM there).
  `FakeGcp`
  implements the member
  with scripted failures (`WithServiceAccountCreationBlockedByOrgPolicy`, `WithWorkerIdentityPermissionDenied`,
  `WithWorkerIdentityActAsDenied`) and adopts an existing account on a repeat call.
- **Proven only by a real project:** `docs/ToTest.md`.

### 16.x The Cloud page's gateway methods (issue #105)

What the Cloud page stands on, in `Core/Cloud` interfaces, the Google gateways, `FakeGcp` and the `Resilient*` wrappers:

- `IComputeGateway.StartVmAsync(vm, zone)`: `instances.start` on the VM as it is (no label, scheduling or metadata write: that is reuse, #94), polled
  to `DONE`. A 404 is `VM_NOT_FOUND`; a stockout on the start is `stockout`. THEORY (unverified): a VM stopped past its `maxRunDuration` is accepted.
- `IComputeGateway.ListDisksByInstallationAsync(installationId)`: `disks.aggregatedList` filtered on `labels.app` AND `labels.installation-id`
  (`returnPartialSuccess=true`; an unreachable zone fails the listing as `ZONES_UNREACHABLE`, never a short list; every disk is re-checked for both
  labels), returning `DiskDescriptor` (name, zone, size in GB, status, labels, creation time, the VMs holding it). `DeleteDiskAsync(disk, zone)`:
  `disks.delete`, already gone is a success.
- `IStorageGateway.GetPrefixUsageAsync`, `ListChildPrefixesAsync`, `DeletePrefixAsync`. The bucket has no per-installation prefix (objects live under
  `jobs/<jobId>/`, one bucket per project shared by every installation), so an installation's footprint is the `jobs/<id>/` folders whose
  `manifest.json` has `createdBy.installationId` equal to it. **DECISION (agent-made, reversible):** ownership is read from the manifest, not guessed from a
  job id list on this PC (the Cloud page must see another PC's data), and a folder with no readable manifest belongs to nobody and is never deleted.
  `DeletePrefixAsync` refuses any prefix that is not a folder path (`StoragePrefixes.RequireDeletable`: not empty, not the root, ends in `/`, no `..`).
- `JobObjectDeleter` (Core/Runs, issues #262 and #534): the real `IJobObjectDeleter` behind the Runs page "Delete cloud copy" (replaces the stand-in that always said
  "not connected"). It deletes exactly `jobs/<job id>/` and nothing else: the bucket must be a `deg-` bucket and the prefix must match `^jobs/[a-z0-9_-]{1,63}/$`
  (so `jobs/`, `cache/`, an empty or partial prefix, a nested path or a `..` is refused with no request, `CloudDeleteRefusedException(NotAJobFolder)`), and
  `RunCloudResults.DeleteAsync` has already checked the stored prefix equals `jobs/<run's own job id>/`. **Ownership:** when the folder has objects, its
  `manifest.json` must name this installation (`createdBy.installationId`, the `InstallationCleanup` test); another computer's run (an imported row, #111) is `CloudDeleteRefusal.OtherComputer` -> `CloudResultsStatus.OtherComputer`; a missing, unreadable or
  wrong-shaped manifest, or an unreadable installation id, is `Unconfirmed` -> `NotConfirmed` (its own copy, "could not confirm this run is yours"); both leave
  everything untouched and offer Open storage page. `DeletePrefixAsync` (Google and Fake) deletes `jobs/<id>/manifest.json` LAST: Cloud Storage lists lexicographically,
  so otherwise the manifest would go before `output/`, `result.json` and `status.json`, and a sweep that died after it would strand the rest behind a false refusal
  on the retry. (THEORY, unverified: persisting a "verified" flag on the run row would also survive an outside deletion of the manifest; not built, it needs a
  migration.) The installation id is a write-once file untouched by Reset all settings (`SettingsStore.cs:172-191`, #558/#565), and manifests have carried
  `createdBy.installationId` since `69707244` (2026-10-02, #460), before any release. An empty prefix is success only when the bucket listing succeeded; a 404 bucket is
  `StorageNotFound` and the row is not changed. The shared name of the manifest object is `ResultsBucket.ManifestObject`.
  Known limits (no code): (1) an object written under the prefix after the manifest was deleted (a late worker write) is stranded, and a later Delete cloud copy
  answers `NotConfirmed` (the manifest is gone), so that leftover is removed from Open storage page; (2) `StorageNotFound` offers no storage button, so a user whose
  bucket is in another project looks at the Cloud page instead. Open storage page links are only built for a `jobs/<id>/` folder in a bucket-shaped name, with each segment escaped. It then calls `DeletePrefixAsync` and reads the prefix back with `GetPrefixUsageAsync`, comparing with the count before:
  none left is success even if the call threw afterwards; the same count left means nothing was deleted, so the real classified error surfaces
  (`CloudDeleteResult.ErrorKind`: permission, billing and API-off each have their own Runs copy, anything else the general one); only some-gone-some-left throws
  `PartialCloudDeleteException(Remaining)` -> `PartiallyDeleted` with the count (singular copy for one). The classified cause rides along in `CloudDeleteResult.ErrorKind` whatever the count, and `CloudDeleteResult.SweepCompleted` says the sweep itself finished without error (a flag `PartialCloudDeleteException.SweepCompleted` set at each place the exception is made, not inferred from its inner exception). Every toast states only what the app knows (sweep completed? count known? cause?): sweep completed with an unreadable count says the delete finished but could not be confirmed empty and to choose Delete cloud copy again to check (`Runs_DeleteCloud_PartialUnconfirmed_*`, the row stays unmarked); sweep stopped with an unknown count says "may" (never that files remain) and names the cause's action, or the plain "stopped and could not count" copy when no cause was classified; a known count with a cause that pressing again cannot fix (permission, organization policy, billing, API off, request limit) states the count and that cause's action (`Runs_DeleteCloud_PartialKnown_Body_*`, "Files still in your Google Cloud storage: N") instead of only "choose Delete cloud copy again"; any other known count uses the count copy. `GoogleStorageGateway.DeletePrefixAsync` itself classifies a raw `IOException` or `SocketException` from the sweep as `Network` (code `TRANSPORT_FAILED`); it has no arm for `HttpRequestException`, which `CloudCallPipeline` maps and owns, `RunCloudResults` treats an `IOException`, `HttpRequestException` or `SocketException` inner exception of a partial delete as `Network`, and a cause whose code is `CloudErrorCodes.ListPaginationStuck` (a listing that repeated a page token) is read as `Other` with `CloudDeleteResult.ListingStuck` set by `RunCloudResults` whatever kind the gateway gave it, and the page shows `Runs_DeleteCloud_ListingStuck_*` (one action: Save diagnostics in Settings; it does not say to try again, because the same listing repeats). A failed delete (the listing stuck before any delete) uses `Runs_DeleteCloud_ListingStuckNothing_Body` ("nothing was deleted", the app cannot see what is stored); only a partial delete says "some files may have been deleted". The storage, compute and quota gateways keep their own `Network` classification for it (`JobReconciler.IsNoAnswer` treats `Network` as no answer and defers). The
  run row is marked `CloudResultsDeleted` only on a clean read-back, so the button stays offered and the next press finishes it (a repeat delete of an empty prefix is
  success). Deleted objects are not recoverable beyond the bucket's soft-delete window, which the confirm dialog does not promise either way.
  The `cache/` model weights have their own lifecycle rule (`matchesPrefix: ["cache/"]`, 365 days or longer) separate from the `jobs/` rule, so neither the retention
  rule nor this delete can reach the other prefix (`GoogleStorageGatewayTests` pins both rules by prefix). `FakeGcp.WithPrefixDeleteFailingAfter(n, kind)` scripts a
  sweep that dies after n objects.
- `InstallationCleanup` (Core): `InventoryAsync` (this installation's VMs, disks, job-data size and count, and the shared model cache size, reported but
  never deleted) and `DeleteAllAsync` (VMs first, then disks re-listed so a deleted VM's disks are seen detached, then the job folders). Label-scoped on
  every item (a stray one a gateway hands over is `Skipped`), every listing happens before the first delete (a failed listing aborts with nothing deleted),
  one failed delete never stops the others (`Failed` with the error kind), and every delete is read back: `Deleted` means gone, `NotVerified` means the
  platform accepted the delete and the read still finds it. A disk still held by a VM that is not ours is `Skipped`. `CleanupReport.Complete` is true only
  when every item is `Deleted`. The service role and the worker service account are not touched here.
- Resilience: a start keeps the breaker; listing and deleting our disks and the bucket prefix calls bypass it (cleanup that stops the meter).
  `FakeGcp`: `WithDisk`, `WithStartFailure(kind)`, `WithDeleteFailure(name, kind)`, `WithSilentNoOpDeletes()`.
## 17. Overrides in Settings (issue #644)

Settings > Advanced edits `worker_image` (a digest-pinned worker image) and shows whether the cloud is the fake. `PinnedWorkerImageProvider.Resolve` reads `worker_image` and `developer_mode` on every run start (no cache), so a saved value is the next run's image; the box is live only when `developer_mode` is already `true` in settings.json, and the page refuses anything that is not an `@sha256:` reference. The DLVM image family override is NOT built: the family is the constant `GoogleComputeGateway.Image` in the Cloud project and `VmSpec` carries no image, so it needs a Cloud change first (issue filed with #644's close-out).

## 18. The nothing-left-running scan (issue #113)

`LeftRunningScanner` (Core) lists this installation's computers and disks by label through the same gateway calls the Cloud page uses
(`ListByInstallationAsync`, `ListDisksByInstallationAsync`; Hard Rules 9 and 10, so another installation's computers are never reported or touched).
A running computer reports its remaining time (`VmDescriptor.MaxRunDuration`, read from `scheduling.maxRunDuration`, against the run clock start and the platform's
own clock when the read carried one), its hourly rate from the price list, and whether a non-terminal run row carries its job id. A disk no running computer holds is a
"stopped disk" with its monthly cost (size times the price list's per-GB-month rate).

`LeftRunningGuard` (Presentation) runs it three ways: at launch (`AppStartup.BeginAsync`), every 5 minutes on a `TimeProvider` timer (a running computer with no live
run gets one warning toast, again only if it ended and came back), and on close (`AppWindowService` cancels `AppWindow.Closing` at once, asks asynchronously, then closes
again; the UI thread is never blocked). The exit modal offers Stop, Delete or Leave running; Leave running is never indefinite because every computer carries a
`maxRunDuration` (Hard Rule 11). A scan that cannot read Google, or a dialog that cannot be shown, never keeps the window open; a Stop or Delete that fails for some computer
does (it names the action, and the next close asks again).

Bounds (cold-review H1): the scan is abandoned after 10 s (``Task.WaitAsync`` on the ``TimeProvider``, so a listing that ignores its token cannot hold the close), the dialog waits at most 10 s for the window's one-dialog gate (``IChoiceDialogService.ChooseAsync`` with a gate timeout; null answer allows the close), and Stop / Delete wait at most 30 s each, in parallel: a call still unanswered is not a failure, the window closes and an Info toast says it finishes on Google's side. Stop and Delete go through ``InstallationCleanup.EndVmAsync`` (round 3, see below): the exit claims "finishing on Google's side" only for a request Google ACCEPTED; a request never accepted keeps the window open with the failure message. A computer whose run is still live ends that run's row as Cancelled (``cleanup_deleted`` for a delete, ``cleanup_stopped`` for a stop, each with its own ``RunError_`` text) instead of leaving it Running (Hard Rule 11); the modal warns that the results are not downloaded yet. The close decision (cancel at once, ask once, re-close without asking again) is ``CloseConfirmation``, tested without a window. Several computers found in one scan make one toast (the message bar holds one message). A computer whose limit could not be read gets a neutral line, not a claim that it ends itself.

Round 3 (the end of one computer, `InstallationCleanup.EndVmAsync`):

- **Accepted is the insert, not the end of the operation (H1).** `IComputeGateway.EndVmAsync(name, zone, delete, onAccepted, ct)` calls `onAccepted` as soon as the Compute request (the operation insert) was accepted (or answered "already gone"), then waits for the operation to reach DONE and throws what it ends with. `GoogleComputeGateway` and `ResilientComputeGateway` (through the pipeline, breaker bypassed like stop and delete; a replayed attempt may call `onAccepted` again) implement it; `StopVmAsync` and `DeleteVmAsync` keep their meaning for every other caller. `FakeGcp.HoldEndVmCompletion()` / `ReleaseEndVmCompletion()` script "accepted now, done later". A GPU stop that takes over 30 s therefore reads as "finishing on Google's side", not as Failed, and the run row is written at acceptance.
- **The driver is stopped only after acceptance (M1).** A refused request (403, quota) leaves the run, its driver and its row exactly as they were.
- **After acceptance nothing the caller's token can cancel decides the row (M3).** The driver wait (bounded by `DriverStopWait`) and the row write take no caller token, so a caller that gives up still gets the run's end recorded (Hard Rule 11). THEORY (unverified): an operation that fails AFTER it was accepted leaves the row Cancelled with the computer still running; the end then reports Failed and the next scan finds a running computer with no live run and warns.
- **A second end for the same job reports what really happened (M2).** The first end publishes its outcome; the second waits for it (bounded by `InFlightWait` = 20 s, then Failed: nothing is known to be finishing, so nothing is claimed). Accepted: Done and nothing more is sent, except a Delete asked behind an accepted Stop, which is sent and then rewrites the row's code from `cleanup_stopped` to `cleanup_deleted`. Refused: the second makes its own attempt. A cancel some other code owns (the runner's, "Delete everything") is still `AlreadyEnding`.
- **A run row phase write is atomic per job (H2).** `RunRowStore` takes one process-wide lock per job id for the read and the write, and writes through `IRunRepository.TryUpsertIfPhaseAsync(row, phaseThatWasRead)`, which the SQLite repository does as one `BEGIN IMMEDIATE` transaction (check and write, never resurrecting a removed row). A writer that lost the race re-reads and re-validates; a terminal phase is never left (`JobStateMachine` throws for any move out of one), so a late driver cannot overwrite the cleanup's Cancelled. The interface default is a plain upsert for single-writer test doubles; a repository other writers can reach must override it.
- Logging: a fault the guard awaits is logged once by the awaiting code; only a call the timeout abandoned is observed for a late fault (with the job id when there is one).

Round 4 (same method):

- **The job is released once the row is ended, not when the operation is done (M1).** The cancel scope (`ActiveRuns.TryBeginCancel`) is disposed right after the run's row is Cancelled; a runner cancel, "Delete everything" and the reconciler no longer see `AlreadyEnding` or wait out a GPU stop. The end's entry stays published until the operation is done, so a second `EndVmAsync` for the job still joins it.
- **Taking the job and publishing the entry are one step (L4)** under one lock, so a concurrent second call always finds either our entry or someone else's cancel.
- **A superseding Delete owns the outcome (M2).** When a Delete is accepted behind an accepted Stop, the Stop's end reports the Delete's outcome if the Stop's own operation later ends in error (the computer was deleted under it). THEORY (unverified): that a real Stop operation errors when its computer is deleted; the guard never shows "stop failed" for a deleted computer either way.
- **A caller token cancelled after acceptance is acceptance (L3)**: Done, row Cancelled, driver stopped. Cancelled before acceptance throws and touches nothing. Both are pinned by tests (the code already did this).
- **Annotations are conditional too (L5)**: `RunRowStore.UpdateRowAsync` takes the same per-job lock and writes through `TryUpsertIfPhaseAsync`, re-reading and re-applying the change if it lost a race.

THEORY (unverified, ToTest #113): `instance.scheduling.maxRunDuration` comes back populated in an instances aggregatedList on a real project.

## 19. Spot VMs (issue #91)

`RunOptions.SpotVm` (the property itself defaults to off, the owner decision is #10; the New run page sets it to `Gpu.RequiresSpot`, which is true only when the chosen graphics card tier is Spot-only, and its cost estimate prices the same `Gpu.RequiresSpot`, so on that page the estimate and the run read one value and cannot disagree; any other caller that builds `RunOptions` gets on-demand unless it sets `SpotVm`) becomes `VmSpec.Spot` in `CloudJobRequestFactory.Create`. The Compute body (`ComputeVmShape.Scheduling`) then carries `provisioningModel=SPOT` next to the unchanged Hard Rule 10 fields: `maxRunDuration` and `instanceTerminationAction=DELETE`. An on-demand spec names no provisioning model. THEORY (unverified, no live project): Compute accepts SPOT with `maxRunDuration`, `DELETE` and `onHostMaintenance=TERMINATE` on a GPU VM (ToTest row #91).

- **Preemption: positive evidence only.** `ResultWaiter.VmLostAsync` marks a loss `SpotPreempted` only when the spec is Spot, `status.json` carries no worker error, the limit has not passed, this app did not itself ask to end the VM (`VmTerminator.NoteAppRequestedEnd`, set by the run page's Stop and Delete and by a cancel, and cleared when the runner starts provisioning the job's first VM, so a Delete of an earlier kept-alive VM never silences a later run's retry) AND `IComputeGateway.WasPreemptedAsync` confirms it for THIS incarnation of the VM: a zone operation of type `compute.instances.preempted` whose `targetId` is the instance id the run saw alive (`VmDescriptor.InstanceId`, unique per incarnation, so an old operation for a reused name never counts), else matched by target name and an operation no older than the VM's creation, else not confirmed. `GoogleComputeGateway` filters the zone-operations list on the operation type only (no target in the server filter, so a difference in how Compute spells the target link cannot hide a real preemption) and matches on the client. Compute may record the operation a moment after the instance is gone, so the lookup is made up to three times, `PreemptionLookupInterval` (10 s) apart. A VM that is merely gone or stopped proves nothing (the person's Delete, an admin, a zone outage, a limit with no clock basis all look the same), so none of those is retried. A lookup that fails is "not confirmed": the run fails as `vm_unhealthy` with a note saying the check failed. THEORY (unverified, no live project): a preempted instance records that operation with `targetLink` set to the instance, and the filter syntax matches (ToTest row #91). It is an internal flag on `RunFailureException`, not a run code. The lost VM is deleted whatever the after-run choice was, and an unconfirmed delete is never a preemption (it stays `vm_end_unconfirmed`).
- **One retry.** `CloudJobRunner.RunStepsAsync` answers a confirmed preemption by (1) deleting the preempted attempt's `status.json` and `progress.jsonl` (`IStorageGateway.DeleteObjectAsync`; attempt 2 writes the same keys and its watcher must not read attempt 1's heartbeat, stage or GPU line), (2) `RunRowStore.RestartProvisioningAsync`, one write that sends the row back to Provisioning (the state machine allows Preparing and Running to Provisioning for this only), sets `IsSpot=false` and adds one to `Runs.PreemptionCount` (migration 0002), then (3) re-provisioning the same job with `Spot=false` (the ladder, the zone claim and the reuse rules run as for any create). A cancel that wins the race makes the row Cancelling, which may not go back, so the run stands down and nothing is counted. The second attempt is on-demand and is never asked about preemption: at most one retry, never a loop. `Runs.IsSpot` is written per attempt (true, then false). A resume after a crash keeps `Spot=false` once `PreemptionCount > 0` (`JobReconciler`). The run restarts from the input; partial outputs of the preempted attempt are NOT carried over (issue #768).
- **No reuse.** A Spot run never starts a stopped VM an earlier run left (`VmProvisioner.TryReuseAsync`), because a stopped VM does not report its provisioning model.
- **Quota.** `QuotaMetrics.SpotRegionalMetric` (issue #718) decides which line a Spot L4 consumes (the standard one until a preemptible grant exists). The zone filter (`VmProvisioner.PlanZonesAsync`) still reads the standard metric for every run, which is right until a preemptible grant exists.
- `FakeGcp.WithSpotPreemptions(n)` preempts the next n Spot creates and records the preemption operation for them; `WithOnDemandVmLosses(n)` loses on-demand VMs with no record; `WithPreemptionLookupFailure()`; `PreemptionLookups`; `CreatedSpecs[i].Spot` shows which attempt was Spot.

## 20. Wait for a GPU: flex-start (issue #92)

`RunOptions.WaitForGpu` (off by default; `WaitForGpuMinutes`, default 120) becomes `VmSpec.FlexStart` plus `VmSpec.FlexStartWait` in `CloudJobRequestFactory.Create` (a flex run is never Spot: the factory turns Spot off). The Compute body (`ComputeVmShape`) then carries `scheduling.provisioningModel=FLEX_START` next to the unchanged Hard Rule 10 fields (`maxRunDuration`, `instanceTerminationAction=DELETE`, the six labels) and `params.requestValidForDuration` (the "relative deadline for waiting for capacity"). `VmSpec.EnsurePreconditions` refuses flex together with Spot, a wait of zero or over seven days, a `maxRunDuration` over seven days, and a wait without flex.

- **The field is added by hand.** MEASURED 2026-10-16: `Google.Apis.Compute.v1` 1.77.0.4282 documents `InstanceParams.RequestValidForDuration` but its `Instance` class has no `Params` property, so `ComputeInstanceBody` (a subclass of `Instance`) adds `params` and Newtonsoft serializes it by name. THEORY (unverified, no live project): the v1 `instances.insert` accepts `params.requestValidForDuration` together with `FLEX_START`, `maxRunDuration`, `DELETE` and `onHostMaintenance=TERMINATE` on a GPU VM, and a seven-day cap applies to both durations (ToTest row #92).
- **The create wait.** A queued create is bounded by `VmSpec.FlexStartCreateBound` (`FlexStartWait + FlexStartMargin`, margin 5 minutes, one value on the spec) instead of `CreateTimeout` (30 s), in the provisioner (`TryZoneAsync`) and in the gateway's operation poll (`AwaitOperationAsync(deadline:)`). The wait is NOT tied to `maxRunDuration` (round 2 L1): that coupling was our own invention, not a Compute rule we know of, and it would have shortened a wait for a short run for no reason now that the run's clock starts at the grant. `EnsurePreconditions` only keeps the wait at or under seven days. THEORY (unverified, no live project): Compute may cap `requestValidForDuration` lower than seven days (and may require it above some minimum); a rejected value is an `INVALID_FIELD_VALUE` and reads as `Other` (not a stockout), ToTest #92. While it waits the runner tells whoever shows the run (`RunNoticeKind.WaitingForGpu`, then `WaitingForGpuEnded`; copy `RunNotice_WaitingForGpu`, shown by the run page's notice line). The phase stays Provisioning: nothing is wrong, the run is waiting by choice.
- **One zone.** A queued request waits in the zone it was made in, so the ladder would sit in its first zone for the whole wait; a flex run asks the first planned zone only (`VmProvisioner`), and a wait that runs out is the stockout it is (`stockout`, then `RunError_stockout_waited`, with no second Wait offered). THEORY (unverified): asking several zones at once is better, and costs one queued request per zone to cancel.
- **The run's clock starts at the grant (review H1).** The queue wait is not part of the result limit: `VmProvisioner` sets `RunProgress.VmStartMark` again when a flex create succeeds. THEORY (unverified, no live project): Compute's `maxRunDuration` also counts from the grant, and a flex instance's `creationTimestamp` is the request time (the lost-VM `pastLimit` look reads the platform's start and could judge a long-queued VM past its limit; ToTest #92).
- **Notices are ordered, per job.** `WaitingForGpu` then `WaitingForGpuEnded` go through one chained queue PER JOB in `VmProvisioner` (dropped once the job's Ended is delivered), so a slow handler or an instant failure cannot reorder them and a blocking handler for one job never holds back another's. `WaitingForGpuEnded` is sent in a `finally`, so a cancel or the app closing withdraws the line too. `CloudJobRunner.NoticeChainCount` (internal) is the test seam that proves no chain outlives its job (F6).
- **Cancel.** `JobEngine.CancelRunAsync` stops the run's driver (which ends the client's wait) and then `VmCanceller` deletes by job label AND, defence in depth, by the request's own name and zone: `VmProvisioner` remembers each queued request (`TakePendingRequests`) and `VmTerminator.DeleteQueuedRequestsAsync` deletes it and reads it back, so a request the label listing cannot see is still removed and a cancel never reads Cancelled over a live request.
- **App closed while queued.** A request stays queued on Google's side. On the next launch the reconciler resumes the Provisioning row, finds the pending request by label, adopts it and waits for its grant (`A_queued_run_resumed_after_the_app_closed_adopts_the_pending_request...`); no second create is made. A granted flex VM whose run ended is ended by `startup.sh`'s own lifecycle call to the Compute API (and `maxRunDuration` is the backstop), and the Cloud page's cleanup (`InstallationCleanup`) lists every labelled VM. An adopted request still queued (PROVISIONING) is waiting by choice, not booting: `ResultWaiter.WaitForBootAsync` restarts `BootTimeout` while an ADOPTED flex request is PROVISIONING inside its own window (`RunProgress.VmAdopted`, set by `VmProvisioner` only when the VM was found already there; round 3 F2), so the queue place is not lost to the 8-minute boot timeout (`A_resumed_run_waits_out_a_request_still_queued_past_the_boot_timeout...`). A request this run created, was granted and that then hangs booting keeps the ordinary boot hang detector. The window is `FlexStartCreateBound` from the request's `CreatedAt` only when the answer also carries `ServerNow` (both on the platform's clock); otherwise it runs from the adoption on the app's monotonic clock, never the app's wall clock minus a platform timestamp. A failed look while the adopted request may still be queued (never seen granted, inside the window) does not count toward `BootTimeout`; after the window the ordinary timeout applies, a request still PROVISIONING then ends as `vm_unhealthy`, and one that expired and vanished (no VM) leaves the loop as a lost VM at once (`F2i`..`F2v`). THEORY (unverified): a queued flex instance reads PROVISIONING until it is granted.
- **Pending requests are forgotten when a run ends terminal** (`CloudJobRunner.RunAsync`, `VmProvisioner.ForgetPendingRequests`); a cancel takes them instead, and a timed-out create that settled clears its own entry. THEORY (unverified, no live project): a queued flex request is a listable instance in PROVISIONING that `instances.delete` removes; if it is not, the request would stay queued after the cancel until its own deadline, and the ToTest row checks for it.
- **Never reused.** A flex run never starts a stopped VM an earlier job left (`VmProvisioner`), like Spot.
- **Offered from the stockout card.** A failed run whose code is `stockout` and that did not already wait shows **Wait for a graphics card** on the Runs page (`RunListItem.WaitForGpuCommand`, built by `HistoryViewModel.WaitForGpuFor`): the same run again with `WaitForGpu` on. The `RunError_stockout` text does not name the button (it is hidden when the run's options cannot be read); a separate hint line (`RunsWaitForGpuHint`) is shown with the button, and a guard test keeps the message free of the button's label. There is no Advanced-options toggle yet: the New run page has no GPU or Spot pickers until #87 lands, and the option is on `RunOptions` for them to use.
- **Expiry classification (review L7).** THEORY (unverified): the real shape of an expired request is not measured; `CloudErrorClassifier` treats a code naming `FLEX_START`, and a message about capacity that was not available, not granted, not fulfilled (or "could not be fulfilled") or timed out, as `Stockout`; for a `FLEX_START` code a `requestValidForDuration` message that says the wait elapsed or expired also counts, never on an HTTP 400. The `FLEX_START` code counts only AFTER the quota, permission, 403 and org-policy rules and only when the code or message is about capacity (a `FLEX_START_QUOTA_EXCEEDED` is Quota, a 403 is Permission, an invalid field is Other). Round 3: a message that says "quota" never counts as a flex expiry (quota wins, even with "no capacity" or "exhausted" in it; "exhausted" alone is not capacity wording), plain "unavailable" counts only beside "resource", and connection phrasing ("connection", "could not reach", "network is unreachable") wins over capacity wording on the flex path as well and reads as Network. A message that merely names `requestValidForDuration` is not a stockout. ToTest #92 records the real one.
- **Fake.** `FakeGcp.WithFlexStartHeld()` / `GrantFlexStart()` hold a queued request until the test grants it (the request lives on the fake's "server": a caller that gives up does not remove it), `WithQueuedRequestsHiddenFromLabelListing()` hides it from the label listing, `FakeGcp.WithFlexStartGrantedAfter(wait)` queues a flex create (the VM exists, PROVISIONING, from the first moment, as a pending request does; a stockout script does not turn a flex create away) and grants it after the wait; `WithFlexStartExpiringAfter(wait)` ends it as a stockout with nothing left; `FlexStartQueued` completes when the request is queued.

## 21. Network proxy (issue #239)

Numbering note: this was section 19 on its own branch; main now holds 19 (Spot VMs, #91) and 20 (flex-start, #92), so the proxy section is 21 (#883).

**One policy, applied in one place.** `ProxyMode` is `System` (default), `Manual` (`host:port`) or `None`, saved as `proxy_mode` and `proxy_address` in settings.json. The sign-in for a manual proxy is NOT there: `ProxyCredentialFile` keeps it DPAPI-encrypted under `%LOCALAPPDATA%\DNAEntropyGraph\auth\proxy.cred` (Hard Rule 12). `ProxyPolicy` (Core, singleton) holds the live choice; `PolicyWebProxy` is the one `IWebProxy` every client holds, so a change in Settings > Advanced > Network proxy reaches the next request of clients that already exist (no restart; pooled connections to the old proxy age out).

- `System` asks `HttpClient.DefaultProxy` per request (WinHTTP: Internet Options, PAC script, automatic detection) and offers `CredentialCache.DefaultNetworkCredentials` to a proxy that asks (NTLM, Kerberos). THEORY (unverified, no proxy lab): the PAC and the Windows sign-in paths; `ToTest.md` has the row.
- `Manual` uses the typed proxy for everything except loopback, with the saved user name and password (Basic or NTLM), else the Windows sign-in.
- `None` goes direct even when Windows names a proxy.

**Every outbound path (MEASURED 2026-10-16, by reading `app/src`).** There is no gRPC anywhere: the Google clients are the REST discovery clients of section 16, so there is no second transport that skips the policy.

| Path | Client | Before | After |
| --- | --- | --- | --- |
| Sign-in token exchange, refresh, revoke | `Google.Apis.Auth` flow, `HttpClientFactory` from `GoogleAccountService.NewFlow` | THEORY (unverified): the Google.Apis default handler follows `HttpClient.DefaultProxy` (system proxy, PAC) but sets no proxy credentials, so an NTLM proxy answers 407 | `ProxyHttpClientFactory` over the policy |
| Resource Manager, Billing, Service Usage, Compute, IAM, Quota | `Google.Apis.*` discovery services via `GoogleRestClient.CreateInitializer` | same as above | same factory |
| Storage upload and download | `Google.Apis.Storage.v1` (same initializer) | same as above | same factory |
| Update check and download | Velopack `GithubSource` with its default `HttpClientFileDownloader` | THEORY (unverified): the `HttpClientHandler` default, the system proxy with no credentials | `ProxiedFileDownloader` over the policy |
| igv.js viewer | WebView2, local files only through the `viewer.deg` mapping | no outbound traffic | unchanged (WebView2 follows Windows itself if it ever fetches) |
| Browser sign-in page, open-in-IGV, folder launch | `Process.Start` of other programs | their own settings | unchanged |
| Loopback receiver | `HttpListener` on 127.0.0.1 | n/a | loopback never uses a proxy |

Cloud Quotas (`GoogleRestClient.CreateHttpClient`, the quota preflight before every create) and the NCBI accession lookup (#684, `ProxiedHttpClient`) are plain `HttpClient`s over the same `ProxyHttpClientFactory.NewHandler`, so they follow the policy too (both were bare clients before the round-3 review); `ProxyBareClientGuardTests` fails on a `new HttpClient`, `SocketsHttpHandler` or `HttpClientHandler` anywhere in `app/src` outside the three proxy builder files. A scripted test handler (`HttpHandler` option) still replaces the whole stack, proxy included.

**Failure classes.** A proxy failure is `CloudErrorKind.Network` (so it still aborts as a network class) but carries its own `CloudError.Code`, because the `network` copy ("check your internet connection") would send the user the wrong way:

| Signal (typed, never message text) | Error code | Run code | Action |
| --- | --- | --- | --- |
| A refused CONNECT with status 407 or 403 (`HttpRequestError.ProxyTunnelError`), or HTTP 407 on an API answer. A 502, 503, 504 or a dropped tunnel is the proxy being unwell: a plain, retried `network` failure | `PROXY_REFUSED` | `proxy_blocked` | Open Settings > Advanced > Network proxy |
| The handshake reached a certificate and the chain was refused for an unknown root or a partial chain. `ProxyHttpClientFactory` records WHEN that happened per host from the validation callback (it only records; validation is the default one) and tags a failed request's TLS exception only if the evidence is newer than the request's start (nothing is consumed, so concurrent requests all see it, and a cancelled request's leftovers never label a later failure; a proxy refusal or connection error is never tagged) (`ProxyFailure.UntrustedChainKey`). A reset or end-of-stream during the handshake, a captive portal and a wrong clock raise the same exception types and stay plain `network` failures | `NETWORK_CERTIFICATE` | `network_certificate` | Ask a network administrator for the organization's certificate |

The handler matches Google.Apis's own defaults (MEASURED 2026-10-16 against `HttpClientFactory.CreateHandler` in 1.77.0: no auto-redirect, cookies on, no decompression) and ends a pooled connection after ten minutes. MEASURED 2026-10-16: `SocketsHttpHandler` reads `proxy.Credentials` once and keeps the object, so `PolicyWebProxy.Credentials` is a live `ICredentials` that asks the policy at each challenge (a plain `NetworkCredential` kept the old password after a change in Settings). `ProxyPolicy.Apply` saves the sign-in first, then settings.json, and puts the sign-in back if the second step fails, using the values read from settings.json just before the write (never the in-memory fallback), and a failure while rolling back never replaces the original error. A saved proxy sign-in is offered only to a challenge from the typed proxy host:port and only for Basic, Digest, NTLM, Negotiate or Kerberos. A tunnel error's message (it names the proxy host:port) is replaced by a fixed one before it is recorded. The sign-in outcomes `SIGNIN_PROXY` and `SIGNIN_CERTIFICATE` use the same two failure kinds. `CloudCallPipeline.IsTransient` treats both as permanent: a refusing proxy refuses again, so there is no retry and no offline banner. A refused CONNECT to a proxy host that does not answer at all is still a plain connection error (`network`): the app cannot tell it from no internet.

**Diagnostics.** `versions.json` records `proxyMode` (`system`/`manual`/`none`) and nothing else: the address can name the user's institution, and `proxy_address` is `<omitted>` in the bundle's settings.json like every unlisted key. No credential is ever recorded.

## 22. The cloud audit log (issue #245)

Every call that changes something in the user's Google Cloud project leaves one line in `logs\cloud-audit.jsonl` under the data root
(so portable mode and `--profile` follow it). Support reads it to answer "what did the app do, and when".

- **Where it hooks.** Not in `CloudCallPipeline` (the pipeline sees an operation name and a closure, not the resource, and the real
  Google gateways run it once per HTTP request, so a pipeline hook would log a retry as a second mutation). It is a decorator per gateway
  interface (`Audited*Gateway` in `DnaEntropyGraph.Cloud/AuditedGateways.cs`) around whatever the container hands out: the
  `Resilient*` wrapper over `FakeGcp`, or the real Google gateway. One entry per logical call, with the whole duration including retries.
  `CloudAuditRegistration.Add` (ServiceRegistration) wraps the registered descriptors after the Google/Fake switch, so both backends are
  audited the same way. `CloudAuditTests.Classified` is a closed list of every method of every gateway interface, as a mutation (with the
  operation name its entry must carry) or a read. A reflection scan of every `I*Gateway` in Core
  (`Every_gateway_interface_in_Core_is_classified...`) fails for a gateway nobody listed, `Every_method_of_every_gateway_interface_is_classified...`
  for an unlisted method, and `Every_classified_mutation_has_an_audited_decorator_that_records_it` invokes EVERY listed mutation through
  its decorator over a stub inner and asserts exactly one entry with that operation (and none for a read). `ProductionCloudWiringTests`
  resolves every scanned gateway from the production container and requires its audit decorator, seeing through `IAuditedGateway.Inner`
  to check the real gateway underneath.
- **Not audited: the developer CloudCli.** `app/tools/DnaEntropyGraph.CloudCli` (dev-only, never shipped in the installer; driven by
  `scripts/cloud_gpu_test.ps1`) builds the gateways itself with `GoogleCloudGateways.Create` in `CloudCliSessions`, outside
  `CloudAuditRegistration`, so what it creates or deletes in the owner's test project leaves NO line in `cloud-audit.jsonl`. The
  Settings explainer says so. Its trail is the script's own log and the resource labels.
- **What is a mutation.** Compute create, reuse (claim, scheduling, metadata, start), stop, delete; Storage `EnsureBucket` (create and
  lifecycle patch) and object upload; the worker identity (service account, role, project and bucket bindings); Service Usage enable;
  `EnableComputeApi`; billing link; project create; start VM; delete disk; delete a job prefix; the quota-increase request
  (resource `projects/<p>/quotas/<metric>[/regions/<r>]`, never the justification text or contact email). Reads (get, list, find, download, quota) are never logged. MEASURED 2026-10-16
  (read of the interfaces): there is no `setLabels`, bucket-patch or object-delete call at the interface today; they are inside `ReuseVm` and `EnsureBucket`, so
  each shows as that one entry. When one becomes its own interface method, the guard test above names it.
- **An entry** (`CloudAuditEntry`): `timeUtc`, `installationId`, `jobId` (when the VM name or object key carries one), `operation`
  (`Compute.CreateVm`), `resource` (`zones/<zone>/instances/deg-<job>`, `buckets/<bucket>/jobs/<job>`, `projects/<project>`),
  `requestId` and `operationId` (the Compute insert reports both through `CloudAuditContext`; other calls leave them null because
  the gateway interface does not surface them). The insert's `requestId` is client-made: the create decorator mints it BEFORE the started
  line (`mintRequestId`), the gateway sends that same id (`CloudAuditContext.RequestIdForCall`), so the started line and a finished line
  for a refused insert (403) both carry it; `operationId` is added once Google answers. `outcome` (`ok`; `cancelled` only when the caller's own token was cancelled; `timeout` for any other cancellation, such as an HttpClient timeout; or the `CloudErrorClassifier` bucket), `errorCode`
  (Google's structured code, never the message) and `durationMs`.
- **One way to start a call under the app's deadline.** Any code that hands an audited gateway call a token linked to an app-made deadline (`GatewayCalls`, the reconciler's `WithDeadline`) starts it through `CloudAuditContext.StartUnderDeadline(callerToken, deadlineToken, call)`, which names the caller's own token to the auditor; a new site that links a deadline and calls a gateway uses the same helper. Both sites also give up on the call through `CloudAuditContext.StopAbandonedAsync(deadline, task)` when the wait ends (timeout or user cancel): it cancels the call's token so it ends and is audited at once, observes a fault the abandoned task may raise later, and does not let a cancellation callback the callee registered replace the outcome: `CancelAsync` raises an `AggregateException` when the callback is registered directly on the source being cancelled (MEASURED 2026-10-29: the helper's own swallow test) that would otherwise replace the cancellation or timeout that callers such as the reconciler's quiet-cancel filter look for. That covers the app's own abandon of a call; the deadline token itself is made by `DeadlineSource` (`Core/Cloud/Audit`), never by `CreateLinkedTokenSource` plus `CancelAfter` or a timed `CancellationTokenSource` (`GatewayCalls`, the reconciler's `WithDeadline` on its injected clock, `OperationPoller`, `CatalogPricingSource` and `GoogleServiceUsageGateway` all use it; `DeadlineSource` is public so the Cloud project can). MEASURED 2026-10-29: with a raw linked source a callee's throwing cancellation callback surfaced out of the user's own `Cancel()` (PR #944 CI, Release, twice) and, on a 200 ms CallTimeout, ended the test host from the timer thread (3 of 3 runs). `DeadlineSource` relays the caller's cancel and the timer through its own `Cancel()`, which swallows a throwing callback and traces only its class. `DeadlineSourceTests` forces both shapes deterministically (a cancel from inside the call; a manually fired timer) and `GatewayDeadlineGuardTests` scans every `app/src` file that names a gateway (whitespace and line breaks tolerated, comments removed) for `CancelAfter`, a timed `CancellationTokenSource` (also the target-typed `new(limit)`), a timer whose callback calls Cancel, and any `CreateLinkedTokenSource` not on its reviewed allowlist (by path and count: `JobReconciler`, `ResultWaiter`, and `VmProvisioner`, whose caller link reaches `CreateVmAsync` raw and so is still exposed to a throwing callee callback: tracked in #961). It is a text scan, not a compiler: a timer that reaches Cancel through another method, or a timed source made in a file that names no gateway and handed to one, is not seen. A zero deadline cancels at construction (like `new CancellationTokenSource(TimeSpan.Zero)`); any other negative one throws. THEORY (unverified): the Google client libraries do not register throwing callbacks.
- **Known race (accepted).** A user cancel that lands after the app's deadline already fired, but before the abandoned inner call ends, is recorded as `cancelled`, because the auditor reads the caller's token when the call ends. The entry is then wrong about the first cause only; the call did not succeed either way.
- **Never in it:** a sequence, an input file name, an object name below `jobs/<id>` (only the prefix is named), an error message, the
  email, a billing account id or the service account email. `CloudAuditTests.No_entry_names_an_input_file_the_email_or_a_message` and
  `Object_prefix_stops_at_the_job...` guard it.
- **Started and finished lines.** The multi-step, non-replayable mutations (create, reuse, start and delete a VM; `Storage.EnsureBucket`;
  `Storage.DeletePrefix`; `Iam.EnsureWorkerIdentity`) write a `phase: started` line (outcome `started`) BEFORE the call and a
  `phase: finished` line with the outcome after, both with the same `correlationId`. A process killed mid-call leaves the started line
  with no finished line: that is the evidence. Single-request writes (stop VM, delete disk, upload, enable API or services, billing
  link, project create, quota request) write only the finished line.
- **Arguments are read inside the audit.** Each decorator passes the resource and job id as a function the auditor runs inside its
  guarded region, so a null argument is a recorded failure (`resource: (unknown)`, outcome `other`), not a synchronous throw.
- **Never throws.** `CloudAuditor` and `JsonLinesCloudAuditLog` swallow every failure to write (full disk, locked file, bad path): the
  line is lost, the run is not.
- **Size.** 1 MB, then the file moves to `cloud-audit.1.jsonl`, replacing the older one: at most about 2 MB. The cap is soft: if the move
  fails (the previous file is open in a reader or antivirus), the line is still appended to the current file and the rotation is tried
  again on the next write. Rotate plus append run under a machine-wide named mutex (`Global\deg-cloud-audit-<hash of the full path>`,
  `JsonLinesCloudAuditLog.MutexName`), so two processes (or two log instances) sharing one data root cannot both move the same full file
  and clobber `.1` with a nearly empty one. The wait is 250 ms; on a timeout, or where the platform gives no mutex, the line is appended
  WITHOUT rotating, so the audit never holds a caller up (the next write rotates).
- **Support bundle.** `DiagnosticsBundleBuilder` copies both generations as `files/logs/cloud-audit.jsonl` and `.1.jsonl`, redacted
  like a log. Settings > Diagnostics > Open cloud activity log reveals the live file (`CloudActivityLogSettingsViewModel`).

## 23. The Cloud Billing budget (issues #148, #222)

#222 is the standing-costs-card half of #148 ("merges with the earlier budget issue; keep one"): one budget, one
implementation, and the card (#219, not built) binds the same view model (`BudgetSettingsViewModel`: `SaveCommand`,
`HasBudget`, `StatusText`, `ErrorText`, `ConsoleLink`, `CopyRequestCommand`) or hosts it.

- **Interface.** `Core/Cloud/IBudgetGateway` (`FindAsync`, `UpsertAsync`, `DeleteAsync`); the policy is `BudgetService`
  (`IBudgetService`: look, save, remove, console link) over it, `IBillingGateway` (the project's linked account) and
  `IServiceEnablementGateway`. `FakeGcp.Budgets.cs` implements the gateway with `WithBudgetPermissionDenied()`; the Budgets
  API off, not-project-owner and billing-off scenarios come from the existing fake switches.
- **Write only after a successful read (round 9).** `BudgetSettingsViewModel.StateKnown` is true only while the last read of the budget state succeeded, and `_knownProject` records the project that read was FOR; `SaveCommand` and `RemoveCommand` need both, with `_knownProject` equal to the project selected now (the selection can change without a Refresh; the method bodies check too, because `ExecuteAsync` does not consult `CanExecute`). Every look starts with no budget shown (`HasBudget` false) and the default request text, so a failed read never leaves the previous project's budget or request behind. Any failed read disables both buttons and the status line names the one action for its kind: a billing administrator (budget list refused, `BudgetStatusNoAccess`), a project owner (refused read of the API state, `BudgetStatusApiReadNoAccess` plus the copy request, or refused read of the project's billing link, `BudgetErrorCodes.BillingReadPermission`, `BudgetStatusBillingReadNoAccess`), Google's billing page (`BudgetStatusBilling`), the organization's administrator with the copy request (`BudgetStatusOrgPolicy`), the API's page (`BudgetStatusApiDisabled`), the internet check then Settings again (`BudgetStatusNetwork`), or open Settings again (anything else, and a cancelled look: `BudgetStatusCouldNotCheck`). THEORY (unverified): `BudgetService.GetAsync` can raise every kind except `AlreadyExists` and `Stockout` (the billing, API-state and budget-list reads all go through the call pipeline's classifier); only Permission, Network and Other were seen from a real project, and the API page the ApiDisabled link opens is the Budget API's even when another API is the one switched off. `BudgetService` tags a refused billing-link read on Save too, so Save names a project owner rather than a billing role. `BudgetService.GetAsync` and therefore `RemoveAsync` raise `ApiReadPermission` for a refused API-state read, exactly like Save. The copy confirmation follows the request copied (`BudgetRequestCopied`, `_ApiRead`, `_OrgPolicy`). A box the user cleared is not refilled with the default.
- **Real gateway.** `Cloud/Rest/GoogleBudgetGateway` over `Google.Apis.CloudBillingBudget.v1` (the DECISION #535 family,
  Apache-2.0): `billingAccounts.budgets.list`, `create`, `patch` (`updateMask=amount,budgetFilter,thresholdRules`), `delete`.
  Every HTTP call goes through the pipeline; the create is never replayed (`replayable: false`), and `UpsertAsync` lists
  first, so a budget whose create response was lost is patched, not doubled. Never wrapped in a `Resilient*` decorator.
- **Discovery (Hard Rule 9).** Budgets carry no labels, so this installation's budget is the one whose display name is
  `DNA Entropy Graph <first 8 characters of the installation id> <project id>` on the account (`BudgetNaming.DisplayName`; at most 57 of Google's 60 characters). Two users on one billing account get two budgets, one project of one user gets one, and
  each sees only its own. A budget of another name is never touched.
- **Shape.** One project in `budgetFilter.projects` (the project NUMBER, read from Resource Manager), `amount.specifiedAmount`
  with no currency (the account's own is used), threshold rules 0.5, 0.9 and 1.0 on `CURRENT_SPEND`, and NO
  `notificationsRule`, so billing administrators and users are emailed by default. Default amount 50.
- **Quota project.** Each request carries `x-goog-user-project: <the user's project>`; without it Google bills the call to the
  OAuth client's own project, where the Budget API is not on. The API is switched on lazily at the first save
  (`RequiredServices.BillingBudgets`, deliberately not in `RequiredServices.Ids`: an optional extra must never fail the wizard).
- **Permission is the common case.** The user needs `billing.budgets.create` on the BILLING ACCOUNT (Billing Account Costs
  Manager or Administrator); a project owner often lacks it. The view model turns `CloudErrorKind.Permission` into one sentence,
  a link to the account's console budgets page and a copyable request for the billing administrator; `Billing` (no account)
  links to the billing home. No new `SetupErrorCodes`: the group maps kinds itself, so the three-way code guard is untouched.
  Three refinements (review): a refusal to switch the Budget API on is a right on the PROJECT, so `BudgetService` rethrows it as `Permission` with
  `BudgetErrorCodes.ApiEnablePermission` and the group offers the API's own page as its one action (no billing-administrator request); a refused READ of the API's state while saving (no `serviceusage.services.get`) stops the save before any budget call, as `Permission` with `BudgetErrorCodes.ApiReadPermission`: saving blind would patch a budget the page could not show (a 500 budget would become the 50 in the box) and would name the wrong person to ask. The group's one action is a copyable request to a project owner for the Service Usage Viewer role (`BudgetRequestText_ApiRead`); the original error is kept as the inner exception and the diagnostic says the read was refused (a refused enable says "Switching on" and has its own code); a
  refused Remove says so and links the budgets page (no create request); and a budget that follows last period's spend (`BudgetInfo.AmountKnown`
  false) is shown without an amount and leaves the amount box EMPTY (Save with an empty box asks for an amount and changes nothing, so a follow-last-month budget is never turned into a fixed default by accident). The project-change reset of the box runs only on a real change of project, never on the first open. The threshold phrase ("50%, 90% and 100%") is built from resw keys (`BudgetThresholdItem`, `BudgetThresholdsJoiner`, `BudgetThresholdsAnd`); `BudgetCopyTests` reads the real resw and checks every Budget key's placeholders against the arguments the code passes. `GoogleBudgetGateway` patches `thresholdRules` (update mask `amount,budgetFilter,thresholdRules`) to 50/90/100 on every Save, so the "no email thresholds" status is expected not to repeat after a Save. When a budget has no thresholds and no known amount the box is empty, so that status (`BudgetStatusNoThresholdsNoAmount`) tells the user to type an amount first. After a missing budget is found or a Remove succeeds, an empty amount box gets the default amount back (typed text is never overwritten).
- **Already exists.** Google allows two budgets with one display name, so there is no 409 to handle; idempotence is the
  find-then-patch above.
- **THEORY (unverified, no live billing account):** that an unset currency and an absent `notificationsRule` behave as documented,
  that `projects/{number}` is the filter form, and that `x-goog-user-project` is honoured with a user token. `docs/ToTest.md`
  carries the real-account row.

## Related

[`job_contract.md`](job_contract.md) (the files the worker on this VM reads/writes),
[`gcp_setup_manual.md`](gcp_setup_manual.md) (the same preflight steps, in plain voice,
for when the in-app wizard cannot complete them), [`threat_model.md`](threat_model.md)
(why the OAuth scope this whole design needs is a large ask, and what it does and does not
expose), `docs/migration/2026-09-19-worker-migration-inventory.md` (the exhaustive,
line-referenced prototype-behaviour inventory this doc summarizes for implementers).

Review fixes (rc105, 2026-10-15):

- Every step of `DeleteAllAsync` after the up-front listings is contained: a failed VM read back, second disk listing or prefix read back is an item (`NotVerified` / `Failed`
  with the error kind) and the sweep goes on; only the caller's cancellation propagates. A job folder whose key is not a deletable folder path (`jobs/../`, which a
  bucket writer can create) is `Failed` with `PREFIX_REFUSED` and never deleted. Ids are compared exactly (`ab` never matches `abc`).
- Regional disks: the listing returns a region as the disk's scope, and `DeleteDiskAsync` sends a region (a name not ending in `-<letter>`) to `regionDisks.delete`;
  the zonal call would 404, which reads as "already gone".
- Soft delete. MEASURED 2026-10-15 (cloud.google.com/storage/docs/soft-delete): a bucket created without a setting, including through the JSON API, gets a 7 day soft delete
  retention, "soft-deleted objects continue to accrue storage charges", and the only supported operations on them during retention are list and restore. So a deleted job folder
  stays billed and recoverable for 7 days. `BucketDescriptor.SoftDeleteRetentionDays` carries the bucket's policy (null unknown, 0 off) and a deleted `JobData` item carries it as
  `RecoverableForDays`: the Cloud page says "deleted; Google keeps a recoverable copy for N days" instead of a bare "deleted". **DECISION (agent-made, reversible):** the bucket is
  NOT created with soft delete off. The same bucket holds the shared model cache (a multi-GB re-download if deleted by mistake) and recoverability is the safe default; the cost
  is a week of storage for small outputs. Changing it is one field on the bucket insert if the owner wants the cleanup to stop billing at once.
- A run whose VM the cleanup deletes is ended (Hard Rule 11): the VM carries `job-id`, the run's driver is stopped through `ActiveRuns.CancelAsync` first (one writer), and once
  the VM reads back gone a non-terminal row becomes Cancelling then Cancelled with `RunErrorCodes.DeletedByCleanup` (`cleanup_deleted`, copy `RunError_cleanup_deleted`). A VM whose delete failed leaves its row alone.
  A VM whose run has a user cancel in progress is `Skipped` (`CANCEL_IN_PROGRESS`).
- `ResilientStorageGateway.DeletePrefixAsync` is one attempt at this layer (`replayable: false`): the real gateway already runs each list page and each object delete through the
  pipeline with its own retries, 401 replay and per-attempt deadline, so a second retrying layer around the whole sweep multiplied retries and replayed whole sweeps.
- `singleRequest:` on `CloudCallPipeline.ExecuteAsync` exists in this branch's base (`CloudCallPipeline.cs`, added before #105).
- DI: `InstallationCleanup` is registered with the run repository, `ActiveRuns` and the phase callback in all three backends (`DiResolutionTests`); `IComputeGateway.StartVmAsync` resolves through the same registration.

Review fixes (rc675, 2026-10-15):

- A no-GPU verdict (`gpu_not_visible`, delete at once) while a stall question is open wins: the question is withdrawn (its token cancelled, `StallCleared`) and the VM is deleted in the same look, never swallowed because "the question is already open".
- The stall question runs OFF the poll loop (`Task.Run`) and is cut at `StallAnswerTimeout` (default 10 minutes; null never cuts it): a handler that blocks its thread, never returns or throws is "no answer", and the run ends the way it chose (Hard Rule 11). Notices (`OnNotice`) are queued in order on the thread pool and the end of a wait lets the ones already given finish for at most 2 s, so a blocking notice handler costs a run at most that.
- Production defaults in DI (`ServiceRegistration`): `OnStall = StallDecisions.EndAsRunChose` (answers at once with `StallChoice.EndAsRunChose`: nobody to ask, so a stall ends as it did before the question existed), `OnNotice` logs the job id and notice kind to the diagnostics log (never a sequence, file name or email), `CancelAckTimeout` stays 60 s. The app lane replaces the first two with real UI. `DiResolutionTests.The_production_runner_has_safe_stall_and_notice_defaults_that_need_no_UI`.
- Cancel: the worker's explicit `cancelSeen` is the acknowledgement (see `job_contract.md` section 6); the graceful ask runs inside the delete-guaranteeing guard; an acknowledged cancel records the job folder on the row so partial outputs stay downloadable. The tests run the ack and finish windows on virtual time (`VirtualTimeProvider` plus a `PollDelay` that advances it), and `A_cancel_racing_a_driver_that_is_finishing_...` races a finishing `RunAsync` against `ActiveRuns.CancelAsync` over fifteen moments of the run. THEORY (unverified, ToTest #675): the stall tests that use `StuckRunner` / `StaleRunner` still measure their 60 to 80 ms windows on the real clock (not converted in this pass).
