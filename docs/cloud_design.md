# Cloud design: preflight, zone selection, error classes, labels, cost, termination

**Status: specification, not yet implemented.** `DnaEntropyGraph.Cloud` does not exist yet
(`app/` is issue #61 and its siblings). This doc is the reference the C# gateways, the
zone ladder (`GpuPlanner`, issue #86), the setup health checks (issue #204), and the error
classifier (issue #57) are built against. Authoritative detail lives in
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

## 5. Error classification

Structured-error successor to the prototype's stderr-substring `classify_create_error`
(`worker/legacy/cloud/gcloud.py`; the exact bucket conditions and the 20+ test cases that
specify them are preserved as JSON fixtures at
`tests/contract-fixtures/cloud_error_classification.json`, consumed by C# issue #213). The
C# classifier (issue #57) reads `Operation.Error` codes and HTTP status from
`Google.Cloud.Compute.V1`/`GoogleApiException` directly, rather than parsing text, wherever
a structured code exists.

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
- `worker/vm/startup.sh` (not yet written; issue #45/#74) ends every path - success,
 failure, or cancellation - by calling the Compute API **on itself**, using its own
 metadata-token credentials, to `stop` or `delete` per the job's `lifecycle.afterTask`.
 It never calls `shutdown -h` as the primary mechanism.
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
 or is stale.
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

## Related

[`job_contract.md`](job_contract.md) (the files the worker on this VM reads/writes),
[`gcp_setup_manual.md`](gcp_setup_manual.md) (the same preflight steps, in plain voice,
for when the in-app wizard cannot complete them), [`threat_model.md`](threat_model.md)
(why the OAuth scope this whole design needs is a large ask, and what it does and does not
expose), `docs/migration/2026-09-19-worker-migration-inventory.md` (the exhaustive,
line-referenced prototype-behaviour inventory this doc summarizes for implementers).
