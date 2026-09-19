# Appendix B: Cloud and worker design (detailed draft)

**Status:** Design draft produced 2026-09-18 by the cloud-orchestration planning pass and reconciled into the [main spec](2026-09-18-dna-entropy-graph-design.md). Where this appendix and the spec body disagree, **the spec body wins**; in particular the long-sequence section here was superseded by spec 5.6 (context length K, bidirectional combined prediction), and the naming is spec D13. This appendix is kept for the API-level detail: OAuth and consent, the wizard's API mapping, the bucket layout and schemas, the instance spec, the startup script, the worker package layout, the cost model, the full error table, IAM, testing and the verification list.

Scope: everything between "user clicks Run" and "results are on disk", for a WinUI 3 app that drives the user's own GCP project through Google APIs (no gcloud, no SSH), plus the Python worker (`dna_entropy`) that runs on the VM or locally. Label on every resource: `app=dna-entropy-graph`.

Lessons carried over from the prototypes (`DNA-Entropy-Genbank/src/dna_entropy/cloud/*.py`) and CLAIR (`docs/gce_wave_brief.md`):

- Project-wide failures (billing off, API off, permission) must abort on the first zone with remediation; only stockout/quota are per-region (`orchestrator._SETUP_ERROR_KINDS`, `gcloud.classify_create_error`).
- Read quota once (`regions.list`) and only try regions that have it (`keeper._apply_quota_health`).
- `maxRunDuration` + `instanceTerminationAction=DELETE` is a backstop, not the cleanup: the termination action fires only when Compute Engine stops the VM at the deadline, **not** when the guest runs `shutdown`. The VM must stop/delete itself through the API and the app must verify it (CLAIR #2908).
- Relay logs/status to GCS while running, not at the end; write the terminal marker last (CLAIR #2789).
- Disk-full reads as a crash; size the disk and log `df -h`.
- SSH-reachable is not GPU-healthy: check `nvidia-smi` (`keeper._check_gpu`).
- The singleton `dna-entropy-box` and the keeper are gone; every job gets its own VM.

---

## 1. OAuth and consent

### 1.1 Client type and flow
- **OAuth client**: type "Desktop app", owned by the author's GCP project (call it `dna-entropy-graph-oauth`). Desktop clients use the loopback redirect `http://127.0.0.1:{ephemeral}/authorize/`; no redirect URI registration needed. The client id and "secret" ship in the binary; Google explicitly treats desktop client secrets as non-confidential.
- **Library**: `Google.Apis.Auth` `GoogleAuthorizationCodeFlow` + `LocalServerCodeReceiver`. Use the flow directly rather than the broker so we control `prompt=select_account`, `access_type=offline`, `include_granted_scopes=true`, and the `IDataStore`. The resulting `UserCredential` is an `ICredential`, which the `Google.Cloud.*` client builders accept.
- Also request `openid email` so the id_token gives a stable `sub` and the email for the account picker.
- **Verify on a non-admin lab PC**: `HttpListener` on `127.0.0.1:<port>` normally works unelevated, but confirm; if it does not, implement `ICodeReceiver` over `TcpListener` (a 60-line class).

### 1.2 Scopes: one scope, `cloud-platform`
Request exactly `https://www.googleapis.com/auth/cloud-platform` (+ `openid email`).

Why not a narrow set: the wizard needs Resource Manager (create project, set IAM policy), Service Usage (enable APIs), Cloud Billing (link account), IAM (create service account, custom role), Cloud Quotas (quota preference), Compute, Storage (incl. `buckets.setIamPolicy`, which needs `devstorage.full_control`). Cloud Quotas and IAM accept **only** `cloud-platform`. The narrowest set that works is six scopes, every one of them classed "sensitive" (identical verification burden), and Google's granular consent lets users untick individual ones, which the app then has to detect and re-prompt for. `cloud-platform` is what gcloud itself asks for. No scope in the set is "restricted", so **no CASA assessment**; standard sensitive-scope verification only.

If the owner insists on visible least privilege: incremental authorization (sign in with `compute` + `devstorage.full_control` + `cloudplatformprojects.readonly` + `cloud-billing.readonly`, request `cloud-platform` only when the wizard needs to create/modify things). Documented option, not the default. (`DECISION` issue.)

### 1.3 Verification: what the owner must do
- Consent screen audience **External**. In **Testing** status: hard cap of 100 test users, each added by hand, **and refresh tokens expire after 7 days**. Testing is for development only.
- Move to **In production** and complete **brand verification + sensitive-scope verification**. Needs: app homepage (GitHub Pages is fine), **privacy policy URL** on a domain the owner verifies in Search Console, terms URL, an unlisted YouTube demo video showing the sign-in and how the scope is used, and a written justification ("creates and manages Compute Engine VMs and a Cloud Storage bucket in the user's own project to run an ML model on their data"). Budget 1 to 4 weeks. Until approved, a production-status unverified app shows the "Google hasn't verified this app" interstitial and still enforces the 100-user cap.
- Workspace admins can mark the client "trusted" for their org (no warning, no cap there).

Sources: Google Cloud console help "Manage App Audience", "When verification is not needed", "Submitting your app for verification"; OAuth 2.0 scopes list.

### 1.4 Client-based vs resource-based APIs (important, easy to get wrong)
With user tokens minted by the author's OAuth client, **client-based APIs** (Resource Manager, Service Usage, Cloud Billing, Cloud Quotas, IAM, Billing Catalog) attribute quota and enablement checks to the project that owns the OAuth client, the author's, unless `x-goog-user-project` is set. **Resource-based APIs** (Compute Engine, Cloud Storage) check the project that owns the resource, the user's. Therefore:
- Owner enables in the author's project, once: `cloudresourcemanager`, `serviceusage`, `cloudbilling`, `cloudquotas`, `iam`, `compute`, `storage`.
- The wizard enables in the user's project: `compute.googleapis.com`, `storage.googleapis.com`, `cloudquotas.googleapis.com`.
- Do **not** send `x-goog-user-project` for the client-based calls (it would require those APIs enabled in the user's project before we can enable anything).

### 1.5 Token storage on Windows
- Custom `IDataStore` (`DpapiFileDataStore`): serialize `TokenResponse` to JSON, `ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser)`, write to `%LOCALAPPDATA%\DNAEntropyGraph\auth\<sub>.tok`. Do **not** use Windows Credential Manager: its blob limit is 2,560 bytes and a `TokenResponse` with id_token exceeds it.
- `accounts.json` (unencrypted): `[{sub, email, picture, lastUsed, defaultProjectId}]` for the account switcher.
- Multiple accounts per PC: each account is a separate token file keyed by `sub`; "Add account" runs the flow with `prompt=select_account consent`. Per-account settings (project, bucket, region group) live beside it.
- Sign out: `RevokeTokenAsync` then delete the file.
- **Same account on two PCs**: confirmed independent. Each authorization issues its own refresh token; Google keeps up to ~100 live refresh tokens per (account, client) and silently revokes the oldest beyond that. Revoking on PC A does not affect PC B. The app must handle `invalid_grant` (password change, admin revocation, 6 months unused, Testing-mode 7-day expiry) by returning to the sign-in page with a plain message.

---

## 2. First-run setup wizard

Principles: every step is idempotent and re-runnable; the wizard persists setup state per (account, project) so a second PC on the same account *discovers* rather than *recreates*; every step shows "what we're doing / why / what it costs".

| # | Step | API call | Automated? | Fallback |
|---|---|---|---|---|
| 0 | Sign in | section 1 | yes | |
| 1 | Pick/create project | `projects.search` (v3, `query="state:ACTIVE"`); prefer any project labelled `app=dna-entropy-graph`; "Create new" -> `projects.create {projectId:"dna-entropy-<8rand>", displayName:"DNA Entropy Graph", labels}`; poll LRO | yes | Per-user project quota (no org) is small; on `RESOURCE_EXHAUSTED` show "pick an existing project" + the Google form link; org policy -> `ORG_POLICY_BLOCK` |
| 2 | Link billing | `billingAccounts.list` (open=true) -> 0: deep link `console.cloud.google.com/billing/create` + re-check; 1: `projects.updateBillingInfo` automatically; N: pick | yes if user owns an account | Needs `billing.resourceAssociations.create`; failure -> copyable request text for the billing admin. **Free Trial** accounts cannot use GPUs or request GPU quota; detected via Cloud Quotas eligibility and create-VM errors |
| 3 | Enable APIs | `services.batchEnable` on `projects/<n>` with `[compute, storage, cloudquotas]`; poll `operations.get` every 5 s up to 5 min | yes | Needs `serviceusage.services.enable` (Owner/Editor). 403 -> "you are not an owner of this project" |
| 4 | Results bucket | Discover by label; create `deg-<projectNumber>-<rand6>` in multi-region `US`/`EU`/`ASIA` from the user's region group (VM zone floats with stockouts; in-continent GCS egress to Compute is free); `uniformBucketLevelAccess`, `publicAccessPrevention=enforced`, STANDARD, labels, lifecycle `Delete age=<retention> matchesPrefix jobs/` and `Delete age=365 matchesPrefix cache/`; write `app-config.json` at the root | yes | Retention change patches the rule |
| 5 | GPU quota | `regions.list` -> `quotas[]` for `NVIDIA_L4_GPUS`, `PREEMPTIBLE_NVIDIA_L4_GPUS`, `NVIDIA_A100_GPUS`, `NVIDIA_A100_80GB_GPUS`, `NVIDIA_H100_GPUS`; `projects.get` -> `GPUS_ALL_REGIONS` (project-wide; 0 overrides regional 1), also `CPUS`, `A2_CPUS`, `A3_CPUS`, `SSD_TOTAL_GB` | read: yes; request: partly (2.1) | |
| 6 | Worker identity | `serviceAccounts.create dna-entropy-worker@<p>.iam.gserviceaccount.com`; custom role `dnaEntropyWorker`; project IAM binding with condition on the `deg-` instance-name prefix; bucket IAM `roles/storage.objectAdmin` to the SA on this bucket only | yes | `iam.disableServiceAccountCreation` -> default compute SA with the same two bindings; never rely on its Editor grant. IAM propagation up to ~60 s |
| 7 | Smoke test | `e2-small`, Container-Optimized OS, `-cpu` worker image, `predictor=mock`, lifecycle DELETE, `maxRunDuration=900s`, input `tests/data/sample.fasta`. Validates SA permissions, bucket read/write, manifest/status protocol, log relay, self-delete via metadata token, app polling and download. ~$0.01, ~4 min | yes | Any failure maps to the taxonomy |

### 2.1 GPU quota request flow
- **Cloud Quotas API can submit the increase programmatically**: `QuotaPreferences.Create(parent:"projects/<n>/locations/global", quotaPreferenceId:"compute_googleapis_com-gpus-<region>-NVIDIA_L4", body:{service:"compute.googleapis.com", quotaId:"GPUS-PER-GPU-FAMILY-per-project-region", quotaConfig:{preferredValue:1}, dimensions:{region, gpu_family:"NVIDIA_L4"}, justification:"Running the Evo 2 genomic language model for DNA sequence analysis with the DNA Entropy Graph desktop application; one GPU, short jobs.", contactEmail:<user>})`, likewise `GPUS-ALL-REGIONS-per-project` = 1. Poll `reconciling==false`, read `grantedValue`/`stateDetail`. Needs `roles/cloudquotas.admin` (Owner has it).
- **Eligibility first**: `QuotaInfos.Get(...quotaInfos/GPUS-PER-GPU-FAMILY-per-project-region)` -> `quotaIncreaseEligibility.isEligible`, `ineligibilityReason` (`NO_VALID_BILLING_ACCOUNT`, `NOT_ENOUGH_USAGE_HISTORY`). Brand-new billing accounts are commonly ineligible; Free Trial accounts cannot request GPU quota at all.
- If ineligible or denied: deep link `console.cloud.google.com/iam-admin/quotas?project=<id>` filtered to the metric, copy-to-clipboard justification (the prototype's `_quota_msg` wording), and the truthful note that new billing accounts are often denied until they have payment history.
- Two escape hatches to **verify on a real new project**: (a) GPU VMs with `maxRunDuration <= 7 days` and `instanceTerminationAction=DELETE` "can consume either preemptible or standard allocation quotas", so if `PREEMPTIBLE_NVIDIA_L4_GPUS > 0` an on-demand launch may work; (b) `provisioningModel: FLEX_START` (Dynamic Workload Scheduler) queues until capacity is found, <= 7 days, deletes itself after. Offer Flex-start as "Wait for a GPU (cheaper, may take a while)".

### 2.2 Health page (every launch, background, each check independent, cached 10 min)
1. Token refresh OK (else sign-in). 2. Project `ACTIVE`. 3. Billing enabled. 4. `compute.googleapis.com` enabled. 5. Bucket exists, UBLA on, lifecycle age == setting, `app-config.json` readable. 6. SA + custom role + bucket binding present. 7. GPU quota table. 8. **Orphan scan**: `instances.aggregatedList(filter:"labels.app=dna-entropy-graph")` -> RUNNING (burning money) and TERMINATED (disk cost) VMs from any installation, with Stop/Delete buttons; `disks.aggregatedList` for stray disks. 9. Worker image digest pinned in this app version vs `app-config.json` (informational). 10. Local GPU present? -> enables "Run on this PC".

---

## 3. Job orchestration

### 3.1 Identifiers (spec D13)
- `installationId`: GUID generated at first launch, stored locally; label value lowercase `[a-z0-9_-]` <= 63 chars -> 26-char Crockford base32.
- `jobId`: `{yyyyMMdd}-{HHmmss}-{6 base32}` UTC, e.g. `20260918-142233-k7q2vx`; sortable in bucket listings, unique across PCs.
- VM name: `deg-{jobId}` (26 chars; limit 63; regex `[a-z]([-a-z0-9]*[a-z0-9])?`). Prefix `deg-` is what the IAM condition keys on.
- Labels on VM, disk, objects: `app=dna-entropy-graph`, `job-id`, `installation-id`, `model`, `app-version`, `lifecycle` (stop|delete|keepalive), `purpose` (job|smoke).

### 3.2 Bucket layout
```
gs://deg-<projectNumber>-<rand6>/
  app-config.json                       # {schemaVersion, projectNumber, regionGroup, workerServiceAccount, createdBy}
  cache/models/evo2_7b/...              # HF weights mirrored after first download (worker does this, best effort)
  jobs/<jobId>/
    manifest.json                       # written by app, immutable after submit
    input/<original filename(s)>        # extension preserved (GenBank/FASTA autodetect), or locus.txt for paste
    control/cancel                      # presence = cancel requested
    status.json                         # worker overwrites atomically (small), includes heartbeat
    progress.jsonl                      # re-uploaded whole every 5 to 10 s (GCS has no append)
    logs/worker.log  logs/startup.log   # re-uploaded every 30 s and on exit
    output/<name>/...                   # exactly the FEATURES.md section 7 file set per input (+ TSV, + fwd/rev tracks when requested)
    output/provenance.json
    result.json                         # {status, inputs[], timing, gpu, error?}; written LAST
  vms/<vmName>/queue/<jobId>            # keep-alive follow-up pointers
  vms/<vmName>/lease.json               # {installationId, jobId, leasedUntil}; conditional write
```

### 3.3 `manifest.json` (schema version 1; the single contract for cloud and local)
```json
{
  "schema": 1, "jobId": "20260918-142233-k7q2vx", "createdAt": "2026-09-18T14:22:33Z",
  "createdBy": {"installationId": "...", "appVersion": "1.0.0", "accountSub": "..."},
  "worker": {"image": "ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:...", "version": "1.0.0"},
  "inputs": [
    {"id": "in1", "path": "input/SetTnpB-Evo.gb", "name": "SetTnpB", "informat": "auto",
     "start": 1, "rna": false, "genes": true, "allowAmbiguity": true, "fastaRecords": "all"}
  ],
  "predictor": {"kind": "evo", "model": "evo2_7b", "precision": "bf16", "device": "cuda", "seed": 0},
  "analysis": {"contextLength": 4096, "window": 8192, "stride": 4096, "direction": "both-combined",
               "format": "bedgraph"},
  "outputs": ["genbank", "fasta", "bedgraph", "wig", "geneious_gff3", "genes_gff3", "stats", "tsv"],
  "limits": {"maxRunSeconds": 14400, "cancelPollSeconds": 10, "heartbeatSeconds": 30},
  "lifecycle": {"afterTask": "stop", "keepAliveMinutes": 30, "afterKeepAlive": "stop"},
  "store": {"kind": "gcs", "bucket": "deg-123-abc", "prefix": "jobs/20260918-142233-k7q2vx/"}
}
```
For local runs `store` is `{"kind": "localdir", "root": "C:\\...\\runs\\<jobId>"}` and `predictor.device` may be `cuda:0`.

### 3.4 `status.json` and progress
```json
{"schema":1,"jobId":"...","stage":"running","percent":42.5,
 "detail":{"input":"in1","contig":"SetTnpB_3","window":5,"windows":11,"direction":"forward"},
 "startedAt":"...","updatedAt":"2026-09-18T15:20:03Z","heartbeatSeq":118,
 "vm":{"name":"deg-...","zone":"us-central1-a","gpu":"NVIDIA L4","driver":"580.x"},
 "worker":{"version":"1.0.0","image":"sha256:..."},
 "error":null}
```
Stages, in order: `queued` (app wrote manifest) -> `provisioning` (insert accepted) -> `booting` (startup script first line) -> `installing` (image pull) -> `restoring-cache` -> `model-loading` -> `running` -> `uploading` -> `done` | `failed` | `cancelled`, then `idle` (keep-alive) / `finalizing` (applying lifecycle). `error = {code, message, detail, retriable, remediation}` with codes from section 7. Each `progress.jsonl` line is `{seq, ts, stage, level, percent, message, data}`; notices (the prototype's yellow lines) arrive as `level: "notice"`.

### 3.5 Heartbeat and death detection (app side)
Poll `status.json` every 5 s for the first 10 min, then 10 s (GET with `ifGenerationNotMatch`). Cross-check `instances.get` every 30 s. Dead if `updatedAt` older than 180 s **and** instance not `RUNNING`, or older than 600 s regardless (hung worker). Per-stage deadlines: provisioning 10 min, booting 8 min, image pull 15 min, model-loading 15 min, running computed from nt count and direction count, uploading 10 min. On death the app offers "Delete VM and show logs" (`logs/startup.log` uploaded by the bootstrap trap; serial console via `instances.getSerialPortOutput` as last resort).

### 3.6 Cancellation, batch, keep-alive
- Cancel: app writes `control/cancel` (0 bytes); worker checks between windows and contigs (<= 15 s latency); writes `cancelled`, uploads partial outputs, applies lifecycle. If no heartbeat within 60 s of cancel, the app calls `instances.stop`/`delete` directly.
- Batch: `inputs[]` with many files; one model load; outputs per input.
- Keep-alive (`afterTask=keep`): worker enters `idle`, polls `vms/<vmName>/queue/` every 10 s; the app submits follow-ups by uploading a manifest and a pointer object; the idle timer (`keepAliveMinutes`) resets on each job; on expiry the worker applies `afterKeepAlive`. The app routes follow-ups only to VMs whose `installation-id` label matches, unless the user explicitly picks a lab-mate's warm VM on the Cloud page (lease). A stopped GPU VM may fail to restart later on stockout; the app treats `start` failure as "provision a new VM" (same manifest).

---

## 4. VM provisioning via the Compute API

### 4.1 Model to GPU matrix

| Model | Precision / stack | Fits on | Machine type | Quota metric | ~$/h on-demand (us-central1) | Spot ~$/h | Notes |
|---|---|---|---|---|---|---|---|
| `evo2_7b` (default), `evo2_7b_262k` | bf16, flash-attn, no Transformer Engine | any 24 GB+ | **g2-standard-8** (1x L4 24 GB) | `NVIDIA_L4_GPUS` | ~0.85 | ~0.18 to 0.30 | default tier; context ceiling 8,192 |
| same | bf16 | 40 GB | a2-highgpu-1g (1x A100 40 GB) | `NVIDIA_A100_GPUS` | ~3.67 | ~1.1 to 2.1 | stockout fallback / bigger windows |
| same | bf16 | 80 GB | a2-ultragpu-1g (1x A100 80 GB) | `NVIDIA_A100_80GB_GPUS` | ~5.07 | ~1.5 to 2.0 | largest single-GPU bf16 option |
| `evo2_40b` | **FP8 via Transformer Engine, Hopper required**; NIM matrix: 2x H100 80 GB or 1x H200 | | **a3-highgpu-2g** (2x H100 80 GB), **Spot or Flex-start only** | `NVIDIA_H100_GPUS` | not offered on-demand for small A3 shapes | ~4 to 5 per GPU-hour x 2 | vortex auto-splits across devices; experimental |
| `evo2_1b_base`, `evo2_20b` | FP8/Hopper per README | | | | | | **hidden**: no cheap tier exists; 1B needs the same H100 as 40B |

Prices shift and vary by region; embed `pricing.json` per release and refresh from the Billing Catalog API.

### 4.2 Software delivery (spec D6: container)
- (a) Install on every boot (prototype): `pip install evo2` + flash-attn source build (no cu12 wheel for torch 2.9; the 2.8.3 wheels for torch 2.9 are cu13) ~10 min on L4, Transformer Engine from source for 40B ~20 to 30 min, 14 GB weights from Hugging Face. Fragile (PyPI/GitHub/HF in the critical path). **Fallback only.**
- (b) Per-project image bake: ~30 to 40 min job at setup, `images.insert(sourceDisk)`; boot-to-running ~2 min; ~$3 to 5/month image storage; re-bake on every stack change. **Post-v1 opt-in.**
- (c) **Author-published container** on a DLVM host: `ghcr.io/raaif-yousuf/dna-entropy-worker:<ver>-cuda` (NGC PyTorch base `nvcr.io/nvidia/pytorch:25.xx-py3` ships Transformer Engine and flash-attn prebuilt; add evo2 + worker) and `:<ver>-cpu` (worker + mock only, ~500 MB). VM boots the public DLVM image (driver + Docker + NVIDIA Container Toolkit preinstalled; verify), `docker pull` (10 to 20 GB, ~3 to 5 min), runs the worker. Weights are **not** in the image; the worker fetches them from `cache/models/` in the user's bucket (first run: from HF, then mirrors to the bucket). Pin the image by **digest**. Publish only to GHCR (public, anonymous pull). **Chosen.** Fresh-VM time-to-running ~6 to 8 min (7B) vs ~2 min for a restarted stopped VM. One image serves both bf16 and FP8 paths; `predictor.precision` selects the code path. Verify evo2/vortex version pins against the NGC versions (README recommends torch 2.6/2.7 and flash-attn 2.8.0.post2; the prototype proved torch 2.9.1 + flash-attn 2.8.3 for 7B bf16). If the NGC base proves incompatible, ship `-cuda-bf16` and `-cuda-fp8`.

### 4.3 Instance spec (`InstancesClient.InsertAsync(project, zone, Instance)`)
```
Name                      deg-<jobId>
MachineType               zones/<z>/machineTypes/g2-standard-8
GuestAccelerators         [{AcceleratorType: zones/<z>/acceleratorTypes/nvidia-l4, AcceleratorCount: 1}]
Scheduling                {OnHostMaintenance: TERMINATE, AutomaticRestart: false,
                           ProvisioningModel: STANDARD|SPOT|FLEX_START,
                           InstanceTerminationAction: DELETE,
                           MaxRunDuration: {Seconds: maxRun + 900}}
Disks                     [{Boot: true, AutoDelete: true, InitializeParams: {
                             SourceImage: projects/deeplearning-platform-release/global/images/family/pytorch-2-9-cu129-ubuntu-2404-nvidia-580,
                             DiskSizeGb: 150 (7B) | 300 (40B), DiskType: zones/<z>/diskTypes/pd-balanced,
                             Labels: {app, job-id, installation-id}}}]
NetworkInterfaces         [{Network: global/networks/default, AccessConfigs: [{Type: ONE_TO_ONE_NAT, NetworkTier: STANDARD}]}]
ServiceAccounts           [{Email: dna-entropy-worker@<p>.iam.gserviceaccount.com, Scopes: [cloud-platform]}]
ShieldedInstanceConfig    {EnableSecureBoot: false, EnableVtpm: true, EnableIntegrityMonitoring: true}
Metadata.Items            startup-script=<bash>, deg-bucket, deg-job-id, deg-manifest=gs://.../manifest.json,
                          deg-worker-image, deg-expect-gpu=true, deg-lifecycle, deg-idle-minutes,
                          install-nvidia-driver=True, block-project-ssh-keys=true, enable-oslogin=TRUE
Labels                    {app=dna-entropy-graph, job-id, installation-id, model, lifecycle, app-version, purpose=job}
DeletionProtection        false
```
- `cloud-platform` scope on the SA: IAM (custom role + condition + bucket binding) is the control; narrowing scopes on top only causes confusing 403s.
- Secure Boot off: NVIDIA kernel modules on DLVM are unsigned.
- External IP needed for GHCR/HF egress; no inbound firewall rule is added. If the org enforces `constraints/compute.vmExternalIpAccess` -> `ORG_POLICY_BLOCK` with "ask IT to allow external IPs or add Cloud NAT" (Cloud NAT is post-v1).
- Spot: `SPOT` + `InstanceTerminationAction=DELETE` (a preempted spot VM should not sit stopped); UI shows "may be interrupted; partial results are kept". Spot uses `PREEMPTIBLE_NVIDIA_*_GPUS` quota. Flex-start (`FLEX_START`, `RequestedRunDuration` <= 7 days) queues instead of failing on stockout.
- `maxRunDuration` constraints: 30 s to 120 days; STANDARD and SPOT supported; the timer is cleared on stop and recomputed from the latest start.

### 4.4 Zone selection and stockout escalation (API port of `_create_box`)
1. Zone universe: `AcceleratorTypesClient.AggregatedList(project, filter:"name=nvidia-l4")` -> zones that actually offer the GPU (replaces the hardcoded 77-zone list).
2. Filter to regions with `available >= 1` for the metric (and `GPUS_ALL_REGIONS >= 1`, `SSD_TOTAL_GB` headroom). `null` (query failed) -> all regions, as in the prototype.
3. Order: last-good zone (per account settings) -> zones in the bucket's continent -> rest of the world (only with the "allow other continents" toggle; cross-continent egress of the weight cache costs ~$1/job).
4. Ladder: 3 sequential inserts, then batches of 3 in parallel, then 5, then 7. Parallel inserts use the **same** VM name in different zones (names are zonal); extra winners are deleted immediately via `aggregatedList(filter="name=deg-<id>")`. Project-wide errors abort the ladder at the first sighting.
5. After exhausting L4: only with the user's "allow fallback GPU" toggle (default off; a 4x price jump should be a conscious choice) escalate to A100-40 with the price shown.
6. On total failure: "No GPUs available right now (tried N zones). Retry in 10 minutes / Try Spot / Wait with Flex-start / Try A100" with one exact error per kind.

Error classification from `Operation.Error.Errors[].Code` and HTTP status (port of `classify_create_error`, now on structured fields):

| Signal | Kind |
|---|---|
| `ZONE_RESOURCE_POOL_EXHAUSTED`, `ZONE_RESOURCE_POOL_EXHAUSTED_WITH_DETAILS`, `RESOURCE_NOT_FOUND` for acceleratorType in that zone, `UNSUPPORTED_OPERATION` mentioning accelerator | stockout (per-zone, continue) |
| `QUOTA_EXCEEDED` (message names the metric; parse it) | quota (per-region, skip; if `GPUS_ALL_REGIONS` -> abort with quota flow) |
| HTTP 403 `accessNotConfigured` / "has not been used in project" | api_disabled (abort -> wizard step 3) |
| HTTP 403 containing "billing" / `BILLING_DISABLED` | billing (abort -> step 2) |
| HTTP 403 `forbidden`, `IAM_PERMISSION_DENIED`, "actAs" | permission (abort) |
| HTTP 412 / `CONDITION_NOT_MET`, `constraints/` in message | org_policy (abort) |
| HTTP 409 `alreadyExists` | already_exists (retry of the same job -> adopt) |
| HttpRequestException / timeouts | network (retry with backoff) |

### 4.5 Startup script (metadata `startup-script`, bash, idempotent, runs on every boot including restarts)
```
set -uo pipefail; exec > >(tee -a /var/log/deg-startup.log) 2>&1
MD=http://metadata.google.internal/computeMetadata/v1; H='Metadata-Flavor: Google'
meta(){ curl -sf -H "$H" "$MD/$1"; }
BUCKET=$(meta instance/attributes/deg-bucket); JOB=$(meta instance/attributes/deg-job-id)
IMAGE=$(meta instance/attributes/deg-worker-image); EXPECT_GPU=$(meta instance/attributes/deg-expect-gpu)
LIFECYCLE=$(meta instance/attributes/deg-lifecycle)
NAME=$(meta instance/name); ZONE=$(basename $(meta instance/zone)); PROJECT=$(meta project/project-id)
JOBURI=gs://$BUCKET/jobs/$JOB
token(){ curl -s -H "$H" "$MD/instance/service-accounts/default/token" | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])'; }
put_object(){ curl -s -X POST -H "Authorization: Bearer $(token)" -T "$1" "https://storage.googleapis.com/upload/storage/v1/b/$BUCKET/o?uploadType=media&name=$2"; }
status(){ printf '{"schema":1,"jobId":"%s","stage":"%s","updatedAt":"%s","vm":{"name":"%s","zone":"%s"},"error":%s}' \
          "$JOB" "$1" "$(date -u +%FT%TZ)" "$NAME" "$ZONE" "${2:-null}" > /tmp/s.json; put_object /tmp/s.json jobs/$JOB/status.json; }
relay(){ while true; do put_object /var/log/deg-startup.log jobs/$JOB/logs/startup.log; sleep 30; done; }; relay &
cleanup(){  # NEVER `shutdown -h`; call the API and verify (CLAIR #2908)
  put_object /var/log/deg-startup.log jobs/$JOB/logs/startup.log
  URL=https://compute.googleapis.com/compute/v1/projects/$PROJECT/zones/$ZONE/instances/$NAME
  case "$1" in
    delete) curl -s -X DELETE -H "Authorization: Bearer $(token)" $URL ;;
    *)      curl -s -X POST   -H "Authorization: Bearer $(token)" $URL/stop ;;
  esac; }
trap 'status failed "{\"code\":\"WORKER_CRASH\"}"; cleanup "$LIFECYCLE"' ERR
shutdown -h +$(( $(meta instance/attributes/deg-max-run-min) + 15 )) &   # deadman, last resort
# already finished on a previous boot? (startup scripts re-run on every boot)
if curl -sf -H "Authorization: Bearer $(token)" "https://storage.googleapis.com/storage/v1/b/$BUCKET/o/jobs%2F$JOB%2Fstatus.json?alt=media" | grep -Eq '"stage":"(done|failed|cancelled)"'; then cleanup "$LIFECYCLE"; exit 0; fi
status booting; df -h / | tail -1
if [ "$EXPECT_GPU" = true ]; then
  for i in $(seq 1 30); do nvidia-smi -L && break; sleep 10; done
  nvidia-smi -L || { status failed '{"code":"GPU_NOT_VISIBLE","message":"GPU driver did not come up"}'; cleanup delete; exit 1; }
fi
status installing
docker pull "$IMAGE" || { status failed '{"code":"IMAGE_PULL_FAILED"}'; cleanup "$LIFECYCLE"; exit 1; }
mkdir -p /work; curl -sf -H "Authorization: Bearer $(token)" "https://storage.googleapis.com/storage/v1/b/$BUCKET/o/jobs%2F$JOB%2Fmanifest.json?alt=media" > /work/manifest.json
# worker owns stages restoring-cache .. done/failed/cancelled, heartbeat, progress, keep-alive loop, and lifecycle
docker run --rm --gpus all --shm-size=8g -v /work:/work -v /var/cache/deg-hf:/root/.cache/huggingface \
  -e DEG_JOB_URI=$JOBURI -e DEG_BUCKET=$BUCKET -e DEG_VM_NAME=$NAME -e DEG_ZONE=$ZONE -e DEG_PROJECT=$PROJECT "$IMAGE" \
  dna-entropy-worker run --manifest /work/manifest.json --store gcs
rc=$?   # 0 done, 2 failed, 3 cancelled, 10 request-stop, 11 request-delete
case $rc in 10) cleanup stop;; 11) cleanup delete;; *) cleanup "$LIFECYCLE";; esac
```
Notes: the HF cache on the host disk survives STOP/START so a restarted VM does not re-download weights. The worker, not the script, uploads outputs and writes terminal status; the script only writes `booting`/`installing` and infra-level failures. `put_object` uses curl unconditionally so the same script runs on Container-Optimized OS for the smoke test.

### 4.6 Least-privilege IAM for the worker SA
Custom role `projects/<p>/roles/dnaEntropyWorker`: `compute.instances.get`, `compute.instances.stop`, `compute.instances.delete`, `compute.zoneOperations.get`. Project-level binding with IAM condition `resource.type == "compute.googleapis.com/Instance" && resource.name.extract("/instances/{name}").startsWith("deg-")` (verify the CEL once in the smoke test). Bucket: `roles/storage.objectAdmin` on the results bucket only. No `logging.logWriter` needed. The signed-in user needs `iam.serviceAccounts.actAs` on the SA (Owner/Editor have it; a bare Compute Admin does not -> `PERMISSION_ACTAS`).

---

## 5. Worker (Python) changes in `dna_entropy`

Hard rules preserved: only `predictors/evo.py` imports torch/evo2; `pipeline.run` unchanged in signature; `pytest -m "not gpu"` green on a laptop.

New subpackage `src/dna_entropy/worker/`:
- `blobstore.py`: `class Blobstore(Protocol)`: `read_bytes(rel)`, `write_bytes(rel, data, content_type)`, `exists(rel)`, `list(prefix)`, `download_dir(rel, local)`, `upload_dir(local, rel)`. `LocalBlobstore(root: Path)` (atomic writes via temp+rename), `GcsBlobstore(bucket, prefix)` (metadata-token REST via `urllib`, retries with backoff; single writer so no generation matching). Contract tests run against both.
- `manifest.py`: dataclasses mirroring section 3.3, `load(bytes)`, strict `schema` check, defaults, unknown fields tolerated.
- `status.py`: `StatusWriter(store)`: throttled `status.json` (>= 1 s apart or on stage change), `progress.jsonl` batching, `Heartbeat` daemon thread every 30 s, log handler that re-uploads `logs/worker.log` every 30 s. All best-effort: a failed upload never fails the job.
- `cancel.py`: `CancelWatcher(store)`: polls `control/cancel` every N s; `raise_if_cancelled()` at contig/window boundaries.
- `lifecycle.py`: `apply(policy)`: `keep`: idle loop over `vms/<name>/queue/` with `idleMinutes` deadline, then `afterKeepAlive`; `stop`/`delete`: Compute REST via metadata token (same calls as the bootstrap), then exit code 10/11 so the script does not double-apply.
- `weights.py`: resolve `cache/models/<id>/` in the store into the HF cache before load; after an HF download, mirror to the cache (best effort). Keeps torch out of everything but `evo.py`.
- `runner.py`: `run_job(manifest, store, predictor_factory)`: `restoring-cache`, `model-loading` (predictor constructed once for the batch), then per input: download -> `readers.input.load` -> validate (existing) -> windowing plan (spec 5.6) -> forward and reverse-complement passes -> direction combine -> existing writers filtered by `outputs` -> upload; `result.json` written last -> terminal status. Batch mode = many inputs, one model load. Exit codes: 0 done, 2 failed, 3 cancelled, 10 request-stop, 11 request-delete.
- `cli.py`: `dna-entropy-worker run --manifest <path|gs://...> --store gcs|localdir [--root DIR]`; `dna-entropy-worker selftest` (mock, tiny) used by the container build. Console script under a new extra `[worker]`.
- `analysis/windowing.py` and `analysis/direction.py`: per spec 5.6 (pure NumPy; `plan(L, K, ceiling) -> windows`, `merge(...)`, `combine(fwd, rev, K, mode)`), with the seam position and reduced-context flags in provenance.
- `predictors/evo.py`: `EvoPredictor(model_id, precision, device, max_context)`: bf16 path unchanged; `precision="fp8"` requires Transformer Engine and `torch.cuda.get_device_capability() >= (9,0)`, else `PredictorError(MODEL_NEEDS_HOPPER)` before weights download.
- Determinism: fixed `seed`; inputs processed in manifest order; `torch.backends.cudnn.deterministic=True`; bf16 results are bit-identical on the same GPU model/driver/image digest and may differ in the last bits across GPU types; `provenance.json` records model id, worker version, torch/flash-attn/evo2 versions, GPU name, driver, image digest, K/W/S, direction, seam, input sha256s, wall time.

Where the worker code comes from: **baked into the container at the release tag** (spec D6). The app pins the image digest, so app-version to worker-version lockstep is preserved.

---

## 6. Cost model and guards

- `pricing.json` shipped per release: `{machineType: {onDemand, spot, byRegionGroup}}`, GPU, pd-balanced $/GB-month (~0.10), GCS standard (~0.02 to 0.026/GB-month). Optional live refresh from the Cloud Billing Catalog API (`services/6F81-5844-456A/skus`) at most once a day; fall back to the shipped table.
- **Live ticker** per running job: `(now - instance.lastStartTimestamp) x hourlyRate / 3600 + disk`, updated every 10 s; history rows store machine type, provisioning model, zone, start/stop, estimated cost, disk GB, lifecycle.
- Pre-run estimate: `(expected minutes) x rate`, expected minutes from history for the same model tier (default 12 min fresh / 5 min warm for 7B), shown with "up to $X (your max-hours cap)".
- **Monthly estimate** from local history + currently stopped disks (`sum diskGb x 0.10 x days/30`) + bucket size. No billing export.
- Thresholds (user settings, defaults): warn if a single job estimate > $2; warn if month-to-date > $25 (cap $50); **hard cap** `maxRunHours` per job (default 4, max 24) -> `maxRunDuration`; A100/H100 tiers require an explicit per-job confirmation with the hourly price.
- **"Nothing left running"**: on launch, on exit, and every 5 min: `instances.aggregatedList(project, filter:"labels.app=dna-entropy-graph")` for every signed-in account/project; on exit, if anything is RUNNING that is not in `keep_alive` with a live idle timer, show a modal "1 GPU computer is still running ($0.85/h). Stop / Delete / Leave running (it will stop itself in N min anyway)". Also list TERMINATED VMs with their monthly disk cost.

---

## 7. Error taxonomy (plain language, one action each)

| Code | User sees | Action |
|---|---|---|
| `SIGNIN_EXPIRED` (invalid_grant) | "Your Google sign-in expired (this happens weekly while the app is in testing)." | [Sign in again] |
| `CONSENT_INCOMPLETE` | "The app didn't get permission to manage Google Cloud." | [Grant permission] (re-prompt with `prompt=consent`) |
| `NOT_PROJECT_OWNER` | "You can use this project but not set it up. Ask the owner to make you an Owner, or create your own project." | [Create project] / [Copy request text] |
| `PROJECT_QUOTA` | "You've reached the limit of Google Cloud projects." | [Pick an existing project] / [Request more (Google form)] |
| `ORG_POLICY_BLOCK` | "Your organization's Google Cloud settings block this (...constraint...)." | [Copy message for IT] |
| `NO_BILLING` | "This project has no billing account, so Google won't start any computer." | [Link billing] / [Create billing account] |
| `BILLING_NO_PERMISSION` | "You can use this billing account but not link projects to it." | [Copy request for billing admin] |
| `FREE_TRIAL_NO_GPU` | "Free Trial accounts can't use GPUs. Activate the full account (credits stay)." | [Open billing] |
| `API_DISABLED` | "Compute Engine isn't switched on in this project yet." | [Turn it on] (auto) |
| `PERMISSION` | "Your account isn't allowed to create computers in this project." | [Copy request for project owner] |
| `PERMISSION_ACTAS` | "You are not allowed to run computers as the worker identity." | [Grant role] / [Use default identity] |
| `NO_GPU_QUOTA` | "Your project isn't allowed any GPUs yet (a one-time approval from Google)." | [Request 1 L4 GPU for me] / [Open quota page + copy text] |
| `QUOTA_NOT_ELIGIBLE` | "Google won't accept quota requests from this billing account yet (new account)." | [Wait for a GPU (Flex-start)] / [Learn what to do] |
| `QUOTA_DENIED` | "Google denied the GPU request: <stateDetail>." | [Try another region] / [Open quota page] |
| `QUOTA_OTHER` (CPUS/SSD) | "Project limit reached for <metric> in <region>." | [Delete stopped computers] / [Try another region] |
| `GPU_STOCKOUT` | "No GPUs free right now in <n> locations. This is temporary." | [Retry in 10 min] / [Try Spot] / [Wait with Flex-start] / [Try A100 ($/h)] |
| `NO_EXTERNAL_IP` | "Your organization blocks internet access for cloud computers." | [Copy message for IT] |
| `VM_BOOT_TIMEOUT` / `GPU_NOT_VISIBLE` | "The computer started but never became ready." | [Delete it and retry] (logs attached) |
| `IMAGE_PULL_FAILED` | "Couldn't download the analysis software onto the computer." | [Retry] |
| `MODEL_DOWNLOAD_FAILED` | "Couldn't download the Evo 2 model." | [Retry] |
| `MODEL_NEEDS_HOPPER` | "This model needs an H100 GPU; you picked L4." | [Switch to evo2_7b] |
| `MODEL_OOM` | "The sequence window is too big for this GPU's memory." | [Lower context length] / [Use a bigger GPU] |
| `INPUT_INVALID` (existing validator messages) | verbatim, already plain | [Fix input] / [Turn on RNA] |
| `HEARTBEAT_LOST` | "The computer stopped reporting progress." | [Stop it] / [Delete it] / [Keep waiting] |
| `VM_DIED` / `SPOT_PREEMPTED` | "The computer was shut down before finishing (Spot interruption or hardware). Outputs so far were saved." | [Download partial results] / [Retry on-demand] |
| `WORKER_CRASH` | "The analysis program failed. Logs are attached." | [View log] / [Report issue] (pre-filled GitHub issue; nothing auto-sent) |
| `RUN_TIME_LIMIT` | "Stopped at your N-hour safety limit." | [Raise limit] / [Re-run] |
| `CANCELLED` | "Cancelled. Partial results kept." | [Download] |
| `RETENTION_EXPIRED` | "These results were deleted after your N-day retention." | [Re-run] |
| `BUCKET_MISSING` | "The results storage was deleted outside the app." | [Recreate] |
| `DOWNLOAD_FAILED` / `NETWORK` | "Can't reach Google Cloud. Check your internet. Results stay in the cloud for N days." | [Retry] |
| `SPEND_CAP` | "This run would go over your monthly warning cap." | [Run anyway] / [Change cap] |

Every error card has "Copy technical details" (raw API error + job id + timestamps).

---

## 8. Security and privacy

- Sequences: app -> user's bucket (TLS) -> VM in user's project -> outputs in user's bucket -> app. Nothing transits author infrastructure. Bucket: uniform bucket-level access, public access prevention enforced, only the user (Owner) and the worker SA (objectAdmin, this bucket only) can read.
- VM: dedicated SA with a custom role limited to stop/delete of `deg-*` instances and one bucket; no SSH keys (`block-project-ssh-keys=true`), no ports opened by us. Optional hardening (DECISION): a dedicated network `deg-net` with no ingress rules.
- Logs: worker logs contain names, lengths, timings, never sequence content.
- Author-hosted surface: OAuth client id (public by design), GHCR images (pinned by digest, built by GitHub Actions from a tagged commit with provenance attestation), GitHub Releases for the app. No servers.
- Telemetry: none by default. Optional opt-in crash reports open a pre-filled GitHub issue; no automatic upload.
- Secrets on disk: DPAPI-protected tokens only.
- Supply chain: pin evo2/flash-attn/TE/torch versions in the image; app refuses to launch a worker image whose digest is not in its allowlist unless "developer mode".

---

## 9. Testing

**C# (unit/integration without GCP)**: gateway interfaces (`IAuthGateway`, `IProjectsGateway`, `IBillingGateway`, `IServiceUsageGateway`, `IIamGateway`, `IQuotasGateway`, `IComputeGateway`, `IStorageGateway`, `IBlobstore`). One `FakeGcp` in-memory world implementing all: projects, billing accounts (flags free-trial / no-permission), API enablement with simulated LRO delay, per-zone GPU capacity and per-region quota tables, instance state machine (PROVISIONING -> STAGING -> RUNNING -> TERMINATED, spot preemption), bucket objects with generations, a scripted fake worker. Scenario tests: wizard from zero, second-PC discovery, each error row in section 7, zone ladder (stockout x3 -> parallel win + duplicate deletion; quota in region -> skipped; billing error -> abort first zone), heartbeat stale -> unresponsive, exit-scan modal, cost ticker arithmetic, DPAPI store round-trip, account switching, lease-based adoption.

**Python (`pytest -m "not gpu"`)**: `test_manifest.py`, `test_blobstore.py` (parametrized over Local and fake GCS), `test_status.py` (throttling, terminal-after-outputs ordering, heartbeat thread stops), `test_cancel.py`, `test_runner_mock.py` (batch of 3 inputs, expected output set per input kind, `result.json` hashes, exit codes, keep-alive queue consumption with a fake clock), `test_windowing.py` (L <= W equals single pass; every position equals the mock's prediction computed directly on its assigned window; right-aligned last window; OOM-halving path via a predictor stub raising OOM once), `test_direction.py` (revcomp twice is identity, last base uniform on reverse, combined seam at K, averaged mode, reduced-context flag when L < 2K), provenance content, schema rejects `schema: 2`. GPU-marked: fp8 refused on non-Hopper, existing `test_evo_predictor.py`.

**End-to-end cheap integration** (nightly, author's project, ~$0.02/run): the smoke test on COS + `e2-small` with the `-cpu` image; asserts `done`, output hashes match the mock baseline, and `instances.aggregatedList` is empty afterwards (self-delete verified).

**Real-GPU acceptance checklist** (per release, on L4 + once on A100): fresh project through the wizard on a brand-new Google account; 7B job on `SetTnpB-Evo.gb` in Forward-only mode matches the prototype's numbers (mean 1.34 bits, min 0.008, 29 genes preserved) within 1e-3; bidirectional combined default: forward vs reverse-complement distributions compared, seam at K checked; a 30 kb FASTA windows correctly; STOP policy then a second job restarts the VM in < 3 min; DELETE policy leaves no instance/disk; keep-alive 5 min idles out with the app closed; cancel mid-batch keeps partial outputs; Spot preemption path -> `SPOT_PREEMPTED`; `maxRunDuration` = 30 min fires and deletes a deliberately hung worker; leak scan clean.

---

## 10. Risks, unknowns to verify, owner-only decisions

**Risks**: OAuth verification timeline gates the user cap and token lifetime; GPU quota for new billing accounts is routinely denied (the single most likely first-run failure, outside the app's control); 40B tier is 2x H100 Spot/Flex only; stack drift between evo2 pins and NGC/DLVM versions (the container isolates users, the author re-validates per evo2 release); stopped-VM disk leak (~$15/month each) mitigated by the 7-day sweep; cross-continent egress if worldwide zones are allowed.

**Unknowns to verify (cheap experiments, filed as spike issues)**: DLVM image ships Docker + NVIDIA Container Toolkit + gcloud; `HttpListener` loopback unelevated on a locked-down PC; Google.Cloud.* builders accept `UserCredential`; IAM condition CEL on instance-name prefix from the metadata-token path; Cloud Quotas eligibility and `ineligibilityReason` on a fresh project; NGC PyTorch base + `pip install evo2` imports cleanly and reproduces prototype numbers; the "<= 7-day maxRunDuration consumes preemptible quota" rule on a fresh project; quota-project semantics via the author's OAuth client; actual prices per region group at release time.

**Owner-only decisions** (issues labelled DECISION): single scope vs incremental; 40B in v1; Stop vs Delete default; Spot default; dedicated VPC; privacy policy hosting domain and who owns the OAuth project; consent publish timing; max run duration and retention defaults.
