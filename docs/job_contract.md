# The job contract: `manifest.json`, `status.json`, `progress.jsonl`, `result.json`, `control/cancel`

**Status: specification, not yet implemented.** Nothing under `worker/src/dna_entropy/`
reads or writes any of the files below today (MEASURED 2026-09-19: no `worker/` module
imports `json` against a `manifest`-shaped path, and `dna_entropy/worker/` does not exist
as a directory). This document is what worker issue #278 (the `dna_entropy.worker`
subpackage: `manifest.py`, `status.py`, `blobstore.py`, `cancel.py`, `lifecycle.py`) and
the C# `DnaEntropyGraph.Core` contract DTOs (`JobManifest`, `WorkerStatus`, `ProgressEvent`,
`WorkerResult`) must both satisfy. Treat every JSON example below as the acceptance test
for those issues, not as a description of running code. The authoritative source this doc
transcribes is `docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md` section 3;
where this doc and that appendix ever disagree, the appendix loses (the main spec's body
wins over appendices per its own header) and this doc should be corrected.

A reader should be able to write a valid `manifest.json` from this file alone and have a
correctly-implemented worker accept it.

---

## 1. Where these files live

```
gs://deg-<projectNumber>-<rand6>/               (or a local dir for LocalBlobstore/local runs)
  app-config.json
  cache/models/<modelId>/...                    # HF weights, mirrored after first download
  jobs/<jobId>/
    manifest.json                               # written by the app, immutable once the worker starts reading it
    input/<original filename(s)>
    control/cancel                              # presence (0 bytes) = cancel requested; absence = not cancelled
    status.json                                 # worker overwrites atomically; small; includes the heartbeat
    progress.jsonl                              # re-uploaded whole every 5-10 s (object storage has no append)
    logs/worker.log
    logs/startup.log
    output/<name>/...                           # the file set from science_and_formats.md section 5, per input
    output/provenance.json
    result.json                                 # written LAST, after every output is uploaded
  vms/<vmName>/queue/<jobId>                    # keep-alive follow-up pointers
  vms/<vmName>/lease.json                       # {installationId, jobId, leasedUntil}; conditional write
```

For a local run (`storage.kind = "localdir"`), the same relative layout is rooted at
`%LOCALAPPDATA%\DNAEntropyGraph\runs\<jobId>\` instead of a bucket prefix; every file name
and field below is identical, only the transport (`GcsBlobstore` vs. `LocalBlobstore`)
differs (spec 5.5). This is why `Blobstore` is a `Protocol`, not a class: the worker never
special-cases "am I on a VM".

---

## 2. Identifiers, before any file is written

- **`installationId`**: a GUID generated once at first app launch, stored locally,
 rendered as a 26-character Crockford base32 string for use as a label value
 (`[a-z0-9_-]`, max 63 chars).
- **`jobId`**: `{yyyyMMdd}-{HHmmss}-{6 base32}`, UTC. Example: `20260918-142233-k7q2vx`.
 Sortable in a bucket listing; unique across every PC without any coordination between
 them (spec D13).
- **VM name**: `deg-{jobId}` (26 characters; Compute's 63-char / `[a-z]([-a-z0-9]*[a-z0-9])?`
 limits both satisfied with room to spare). The `deg-` prefix is what the worker service
 account's IAM condition keys on - nothing else may create a `deg-*` instance and expect
 the worker SA to be allowed to touch it.
- **Labels** on every VM, disk, and bucket object this job touches: `app=dna-entropy-graph`,
 `job-id=<jobId>`, `installation-id=<installationId>`, `model=<modelId>`,
 `app-version=<semver>`, `lifecycle=stop|delete|keepalive`, `purpose=job|smoke`.

---

## 3. `manifest.json` (schema version 1)

Written by the app before any GCP resource is created; **immutable after the worker
starts reading it** - a re-run is a *new* `jobId`, never an edit of an existing manifest.

```json
{
  "schema": 1,
  "jobId": "20260918-142233-k7q2vx",
  "createdAt": "2026-09-18T14:22:33Z",
  "createdBy": {
    "installationId": "01H9X5G6K7M8N9P0Q1R2S3T4U5",
    "appVersion": "1.0.0",
    "accountSub": "104852...redacted"
  },
  "worker": {
    "image": "ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:9f2b...redacted",
    "version": "1.0.0"
  },
  "inputs": [
    {
      "id": "in1",
      "path": "input/SetTnpB-Evo.gb",
      "name": "SetTnpB",
      "informat": "auto",
      "start": 1,
      "rna": false,
      "genes": true,
      "allowAmbiguity": true,
      "fastaRecords": "all"
    }
  ],
  "predictor": {
    "kind": "evo",
    "model": "evo2_7b",
    "precision": "bf16",
    "device": "cuda",
    "seed": 0
  },
  "analysis": {
    "contextLength": 4096,
    "window": 8192,
    "stride": 4096,
    "direction": "both-combined",
    "format": "bedgraph"
  },
  "outputs": ["genbank", "fasta", "bedgraph", "wig", "geneious_gff3", "genes_gff3", "stats", "tsv"],
  "limits": {
    "maxRunSeconds": 14400,
    "cancelPollSeconds": 10,
    "heartbeatSeconds": 30
  },
  "lifecycle": {
    "afterTask": "stop",
    "keepAliveMinutes": 30,
    "afterKeepAlive": "stop"
  },
  "store": {
    "kind": "gcs",
    "bucket": "deg-123-abc",
    "prefix": "jobs/20260918-142233-k7q2vx/"
  }
}
```

For a local run, `store` is instead `{"kind": "localdir", "root": "C:\\...\\runs\\<jobId>"}`
and `predictor.device` may be `cuda:0`.

**Field notes:**

| Field | Meaning | Notes |
|---|---|---|
| `schema` | Contract version, integer | See section 7 (versioning). A worker that reads `schema != 1` (once other versions exist) must refuse the job with a named error rather than guess at compatibility. |
| `inputs[].informat` | `auto \| genbank \| fasta \| paste` | `auto` reproduces the prototype's `readers/detect.py` extension-then-content sniff. |
| `inputs[].allowAmbiguity` | Whether IUPAC ambiguity codes are accepted (N-tolerant mode) | `true` for GenBank/FASTA, `false`-equivalent for a strict pasted sequence - see `science_and_formats.md` section 4. |
| `inputs[].fastaRecords` | `all \| first` | Spec D14: all records is now the default; `first` exists only for parity with the prototype's old behaviour if ever needed. |
| `analysis.contextLength` / `window` / `stride` | `K`, `W`, `S` from `science_and_formats.md` section 3 | `window` and `stride` are **derived, not chosen** by the user, but recorded here so the worker does not have to re-derive them and so `provenance.json` and the manifest agree by construction. |
| `analysis.direction` | `forward \| reverse \| both-combined \| both-averaged \| both-separate` | See the Direction option in `science_and_formats.md` section 3. |
| `limits.heartbeatSeconds` | How often the worker must touch `status.json` | 30 s (section 5 below). |
| `lifecycle.afterTask` | `stop \| delete \| keep` | What the worker does to its own VM once every output is uploaded and `result.json` is written. |
| `store.prefix` | Always `jobs/<jobId>/`, matching the bucket layout in section 1 | Redundant with `jobId` by construction; carried explicitly so the worker never has to assemble the path itself from parts that could drift. |

---

## 4. `status.json` and `progress.jsonl`

`status.json` is the **current snapshot**, overwritten atomically (write to a temp object/
file, then rename - never a partial write is visible). `progress.jsonl` is the **history**,
since object storage has no append: the worker keeps the whole small file in memory and
re-uploads it every 5-10 s, capped at 1 MB (older lines roll into `progress.1.jsonl`).

```json
{
  "schema": 1,
  "jobId": "20260918-142233-k7q2vx",
  "stage": "running",
  "percent": 42.5,
  "detail": {
    "input": "in1",
    "contig": "SetTnpB_3",
    "window": 5,
    "windows": 11,
    "direction": "forward"
  },
  "startedAt": "2026-09-18T14:23:10Z",
  "updatedAt": "2026-09-18T15:20:03Z",
  "heartbeatSeq": 118,
  "vm": {
    "name": "deg-20260918-142233-k7q2vx",
    "zone": "us-central1-a",
    "gpu": "NVIDIA L4",
    "driver": "580.x"
  },
  "worker": {
    "version": "1.0.0",
    "image": "sha256:9f2b...redacted"
  },
  "error": null
}
```

`error`, when non-null, is `{code, message, detail, retriable, remediation}`, with `code`
drawn from the error taxonomy in `cloud_design.md` section 5.

**`progress.jsonl`**: one JSON object per line, appended (in memory, then re-uploaded
whole):

```json
{"seq": 117, "ts": "2026-09-18T15:19:58Z", "stage": "running", "level": "info", "percent": 42.1, "message": "SetTnpB - record 2 of 5 - window 4 of 11, forward", "data": {}}
{"seq": 118, "ts": "2026-09-18T15:20:03Z", "stage": "running", "level": "notice", "percent": 42.5, "message": "Window 5: reduced context (L < 2K) on this input", "data": {"input": "in1"}}
```

`level: "notice"` is the worker's equivalent of the prototype's yellow console lines -
non-fatal, user-visible observations (a reduced-context window, a retried OOM, a kept
ambiguity code) that do not change `stage`.

### Stage list, in order

```
queued -> provisioning -> booting -> installing -> restoring-cache -> model-loading
       -> running -> uploading -> done | failed | cancelled
       -> idle (keep-alive only) -> finalizing (applying the after-task lifecycle)
```

| Stage | Who writes it | Meaning |
|---|---|---|
| `queued` | App | Manifest written; no VM exists yet. |
| `provisioning` | App / worker startup script | VM create/start request accepted through the first line the startup script writes. |
| `booting` | Startup script | First line the script writes on boot, before `nvidia-smi` is confirmed. |
| `installing` | Startup script | `docker pull <digest>` in progress. |
| `restoring-cache` | Worker | Pulling cached model weights from `cache/models/<id>/` in the bucket, if present. |
| `model-loading` | Worker | Constructing the predictor (once per batch, not once per input). |
| `running` | Worker | Per input: validating, running (per contig, per window, per direction), writing. |
| `uploading` | Worker | Final output upload, after all inputs are processed. |
| `done` / `failed` / `cancelled` | Worker | Terminal. `result.json` is written immediately after reaching one of these. |
| `idle` | Worker | Keep-alive only: polling `vms/<vm>/queue/` for a follow-up job. |
| `finalizing` | Worker | Applying the after-task or after-keep-alive lifecycle action (stop/delete via the Compute API). |

---

## 5. Heartbeat and death detection

- The worker touches `status.json` (bumping `heartbeatSeq` and `updatedAt`) **every 30 s**
 (`limits.heartbeatSeconds`), regardless of stage, as long as it is alive.
- The app polls `status.json` **every 5 s for the first 10 minutes, then every 10 s**,
 using `ifGenerationNotMatch` so an unchanged object costs one 304.
- The app cross-checks `instances.get` **every 30 s**.
- **Dead** if `updatedAt` is older than **180 s and the instance is not `RUNNING`**, or
 older than **600 s regardless of instance state** (a hung worker on a running instance).
- Per-stage deadlines, each independently enforced: provisioning 10 min, booting 8 min,
 image pull (`installing`) 15 min, model-loading 15 min, running computed from `nt` count
 and window/direction count, uploading 10 min.
- On death, the app offers "Delete VM and show logs" - `logs/startup.log` (uploaded by the
 startup script's own trap) and, as a last resort before any worker log exists,
 `instances.getSerialPortOutput`.

---

## 6. `control/cancel` and cancellation

- The app requests cancellation by writing a **0-byte object** at `control/cancel`.
 Presence of the object is the entire signal; there is no payload to parse.
- The worker checks for it **between windows and between contigs** - never mid-window,
 since a window is one forward pass and cannot be interrupted partway - so latency to a
 cancel taking effect is at most one window's compute time, budgeted at **≤ 15 s** in the
 common case.
- On seeing `control/cancel`, the worker writes `stage: "cancelled"`, uploads whatever
 outputs were already produced (partial results are always kept, never discarded), and
 applies the normal after-task lifecycle.
- If no heartbeat arrives within **60 s** of the app writing `control/cancel`, the app
 calls `instances.stop`/`instances.delete` on the VM directly rather than continuing to
 wait for a worker that may already be gone.

---

## 7. `result.json`: written last, on purpose

```json
{
  "schema": 1,
  "jobId": "20260918-142233-k7q2vx",
  "status": "done",
  "inputs": [
    {"id": "in1", "status": "done", "outputs": ["output/SetTnpB/SetTnpB.gb", "..."]}
  ],
  "timing": {"startedAt": "2026-09-18T14:23:10Z", "finishedAt": "2026-09-18T15:41:02Z"},
  "gpu": {"name": "NVIDIA L4", "zone": "us-central1-a", "spot": false},
  "error": null
}
```

`result.json` existing at all is the app's signal that the worker reached a terminal
state - the app's `JobReconciler` checks for its existence *first*, before falling back
to `status.json`'s heartbeat, on every launch (`architecture.md` section 4). This is why
every stage above that isn't terminal must never write `result.json`: its mere presence
is load-bearing, not just its contents. Writing it last, after every output object is
confirmed uploaded, is the same discipline the prototype's cloud modules already knew
(`docs/migration/2026-09-19-worker-migration-inventory.md`'s note on relaying logs/status
continuously but writing the terminal marker last) and the reason a torn or half-uploaded
result is not a state this contract allows.

`status` is one of `done | failed | cancelled`; a batch where some inputs succeeded and
others failed still writes `status: "done"` at the job level with per-input `status`
values distinguishing them (the app's `JobPhase` maps this combination to
`PartiallyCompleted` - see `architecture.md`).

---

## 8. Versioning rule

`schema` is a single integer, present on `manifest.json`, `status.json`, and
`result.json`. The rule is intentionally simple because there is, as of this doc, exactly
one version:

- A worker that receives a manifest with `schema` greater than the highest version it
 understands **must refuse the job** with a named, retriable-false error - never attempt
 to interpret unknown fields optimistically.
- A worker may accept a manifest with a **lower** `schema` than its own only if it ships
 an explicit compatibility shim for that exact older version; absent one, it refuses the
 same way.
- Unknown fields on an otherwise-matching schema version are tolerated (ignored), so a
 newer app talking to an older worker degrades gracefully rather than failing on an
 additive field.
- Bumping `schema` is a breaking-contract change: it requires updating this doc, the C#
 DTOs, the Python `manifest.py` dataclasses, and every fixture under
 `tests/contract-fixtures/` in the same change (CLAUDE.md rule 16, docs in the same
 commit).

---

## Related

[`science_and_formats.md`](science_and_formats.md) (what `analysis.*` and `outputs[]`
mean and produce), [`cloud_design.md`](cloud_design.md) (how the VM that runs this
contract is provisioned and torn down, and the error taxonomy `status.json.error.code`
draws from), [`architecture.md`](architecture.md) (the app-side `JobPhase` state machine
that consumes these files). Authoritative source:
[Appendix B, section 3](superpowers/specs/2026-09-18-appendix-b-cloud-design.md#3-job-orchestration).
