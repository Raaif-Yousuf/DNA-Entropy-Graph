# The job contract: `manifest.json`, `status.json`, `progress.jsonl`, `result.json`, `control/cancel`

**Status: implemented on the Python worker side (issue #278, closed).**
`worker/src/dna_entropy/worker/` (`manifest.py`, `status.py`, `blobstore.py`, `cancel.py`,
`lifecycle.py`, `result.py`, `runner.py`, `batch_limits.py`) reads and writes every file
below MEASURED 2026-09-19 against the committed source. Every field named in this document
is what the worker actually parses/writes today, cross-checked against
`docs/contract/*.schema.json` — generated directly from the worker's own dataclasses by
`scripts/gen_manifest_schema.py --check`, and the shape authority where this prose and
that generated schema would ever disagree (this document is the authority for *meaning*,
not shape). The C# `DnaEntropyGraph.Core` contract DTOs (`JobManifest`, `WorkerStatus`,
`ProgressEvent`, `WorkerResult`) do not exist yet (`app/` is not built, issue #61) and
still have this document and the generated schemas to satisfy when they do. None of this
has been exercised against a real GCP project or a real GPU VM yet — see
[`docs/ToTest.md`](ToTest.md) for exactly which cloud-facing paths remain unproven on real
infrastructure despite being implemented and unit-tested. The authoritative source this
doc transcribes is `docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md` section
3; where this doc and that appendix ever disagree, the appendix loses (the main spec's
body wins over appendices per its own header) and this doc should be corrected.

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
      "ambiguityPolicy": "keep",
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
    "heartbeatSeconds": 30,
    "maxInputs": 50,
    "maxTotalNt": 20000000
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
| `inputs[].ambiguityPolicy` | `keep \| mask \| error`, default `keep` | Replaces the old `allowAmbiguity` boolean (issue #249), which was parsed but never actually consulted by any reader - a real "wired to nothing" gap, not just a rename. All three input paths (GenBank, FASTA, paste) now share the same explicit default; see `science_and_formats.md` section 4 for what each policy does to the entropy numbers, and note the behaviour change it documents: paste used to be implicitly stricter than GenBank/FASTA, and no longer is. An old manifest still sending `allowAmbiguity` is unaffected either way - unknown fields are tolerated (section 8 below), it just has no effect, same as before, now honestly so. |
| `outputs` | Array naming every desired output file individually: `genbank \| fasta \| bedgraph \| wig \| geneious_gff3 \| genes_gff3 \| stats \| tsv` (see `science_and_formats.md` section 5 for what each file is) | **Fixed 2026-09-19 (issue #304)**: an earlier revision of the worker parsed this field but only inferred three of eight knobs from it (`track_format`, `include_tsv`, and whether Prodigal ran), silently writing the pipeline's full normal output set regardless of what was actually asked for - a real "wired to nothing" gap (a user deselecting `geneious_gff3` or `stats` still got those files, using storage/egress they opted out of). `RunConfig` now carries one `include_*` flag per writer (`worker/src/dna_entropy/config.py`), and `JobManifest.build_run_config` (`worker/src/dna_entropy/worker/manifest.py`) maps `outputs` onto all of them: an **empty/omitted** array means "unspecified" and is treated as "everything" (so a manifest that never mentions `outputs` keeps the full set, unchanged from before); a **non-empty** array is taken literally - anything not named is suppressed, and the run's output folder and `result.json`'s file list actually contain fewer files. `genbank`/`wig`/`bedgraph` select the same single track-writer choice as before (a GenBank-input job still writes both bedgraph and wig unconditionally when the track is included at all - this asymmetry between the two input kinds predates this fix and is tracked separately, not resolved by it). |
| `inputs[].fastaRecords` | `all \| first` | Spec D14: all records is the default. **Fixed 2026-09-19 (issue #306)**: `first` used to be parsed by `worker.manifest.InputSpec` and then silently ignored (a manifest requesting it still got every record); `pipeline.run()` now truncates to the first record when `fasta_records == "first"` and the input is FASTA (GenBank multi-record handling is untouched - this field is FASTA-specific), recording a notice naming how many records were dropped. An unrecognized value now fails manifest parsing with a named error instead of being silently treated as `"all"`. |
| `analysis.contextLength` / `window` / `stride` | `K`, `W`, `S` from `science_and_formats.md` section 3 | `window` and `stride` are **derived, not chosen** by the user, but recorded here so the worker does not have to re-derive them and so `provenance.json` and the manifest agree by construction. |
| `analysis.direction` | `forward-only \| reverse-only \| both-combined \| both-averaged \| both-separate` | See the Direction option in `science_and_formats.md` section 3. **Corrected 2026-09-19**: an earlier revision of this table showed `forward`/`reverse`; the shipped `dna_entropy.config.Direction` enum (issue #279, already tested before this document's example was written) only ever accepted the canonical `-only` spellings above, and the worker now refuses any manifest using the old, never-actually-supported spelling with a named error rather than silently guessing. |
| `analysis.format` | `bedgraph \| wig` | **Fixed 2026-09-19** (found auditing #304): accepted any string with no validation, and separately - a worse bug - `build_run_config` never actually read the parsed value at all, always deriving the track format from `outputs` membership and falling back to `bedgraph` regardless of what this field said. Now validated at parse time (an unrecognized value fails manifest parsing with a named error, same as `analysis.direction`) and used as the fallback track format whenever `outputs` is empty/omitted; a non-empty `outputs` array remains the literal, authoritative choice (see the `outputs` row above) since that is issue #304's own scope. |
| `limits.heartbeatSeconds` | How often the worker must touch `status.json` | 30 s. **Fixed 2026-09-19** (found auditing #304): parsed but never reached `StatusWriter` - the worker always ticked at a hardcoded 10 s regardless of what the manifest declared. `run_job` now builds `StatusWriter` with `interval_seconds=min(heartbeatSeconds, 10)`: never slower than the manifest's own declared cadence, and never above the pre-existing 10 s default either, since `progress.jsonl`'s separate 5-10 s re-upload cadence (section 4 above) is an independent design constant `heartbeatSeconds` was never meant to relax. |
| `limits.cancelPollSeconds` | How often the worker's `control/cancel` check does a real store round-trip, default `10` | **Fixed 2026-09-19** (found auditing #304, issue #338): parsed but never consumed - `CancelWatcher.poll()` did a real store `exists()` call on *every* cooperative-cancellation checkpoint (once per window, once per contig) with no throttling, regardless of this field. A long batch tiled into many small windows meant a real store query per window. `CancelWatcher` now throttles its real round-trip to at most once per `cancelPollSeconds` of wall-clock time (returning the last-known result in between); the checkpoint itself is still called every window/contig, only the underlying store query is throttled, so the latency-to-effect budget in section 6 below is unchanged. |
| `limits.maxInputs` | Batch-level cost guardrail: max input files per job, default `50` | Issue #248. A policy cap tied to money, not a technical ceiling - windowing already tiles a sequence of any length. The worker re-validates this itself (`worker/batch_limits.py`) even if the app is expected to check first, the same "a misconfigured or bypassed client must never silently produce an unbounded bill" reasoning as every other worker-side re-check in this contract. Exceeding it refuses the **whole batch** before any predictor runs - `result.json` reports `status: "failed"`, `error.code: "BATCH_LIMIT_EXCEEDED"`, `inputs: []` (nothing was attempted). See section 7 below. |
| `limits.maxTotalNt` | Batch-level cost guardrail: max total nt across every input in the job, default `20,000,000` | Same mechanism and disposition as `maxInputs` immediately above; the worker measures the batch's real, actual nt count (via the same input parser the real run uses) before checking either limit, so the refusal message names the batch's own real numbers rather than a generic "too large." |
| `lifecycle.afterTask` | `stop \| delete \| keep` | What the worker does to its own VM once every output is uploaded and `result.json` is written. **Fixed 2026-09-19** (found auditing #304; Hard Rule 11 violation): `"keep"` used to skip the worker's lifecycle step entirely, and `lifecycle.keepAliveMinutes`/`lifecycle.afterKeepAlive` were parsed but never consumed anywhere - a manifest requesting `"keep"` left the VM running with **zero** worker-side expiry, ever, violating "keep alive always has an expiry, never indefinitely." The real keep-alive queue/idle-timer feature (waiting for a follow-up job via the `vms/<vm>/queue/` layout in section 1) is issue #93 and is still not implemented. Until it is, `run_job` now safely degrades `"keep"` to `lifecycle.afterKeepAlive` (default `"stop"`) instead of a true no-op, logging a notice explaining why; a manifest that sets `afterKeepAlive` to `"keep"` too falls back to `"stop"` rather than propagate a second unbounded keep. Note this is a **worker-side** safety net only - the VM's own `maxRunDuration`/`instanceTerminationAction=DELETE` hard ceiling (Hard Rule 10) is the real backstop regardless, and is itself unverified against a real GCP project tonight (`docs/ToTest.md`). |
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
drawn from the error taxonomy in `cloud_design.md` section 5. **The worker's own subset of
that taxonomy - the codes Python code under `dna_entropy` (or `worker/vm/startup.sh`) can
actually raise - is generated into `docs/contract/error-codes.json`** from
`dna_entropy.worker.errors.WORKER_ERROR_CODES`, the same generate-and-`--check` pattern as
the manifest/status/result schemas (issue #39/#254), so the worker and a future C#
`ErrorCatalog` cannot silently drift. As of this revision it names nine codes:
`MANIFEST_INVALID`, `WORKER_VERSION_MISMATCH`, `MODEL_NEEDS_HOPPER`, `MODEL_OOM`,
`INPUT_INVALID`, `WORKER_CRASH`, `BATCH_LIMIT_EXCEEDED`, `GPU_NOT_VISIBLE`,
`IMAGE_PULL_FAILED` - cross-checked against `docs/copy_catalog.md` section 3's error
catalog, which every worker code except `MANIFEST_INVALID` and `WORKER_VERSION_MISMATCH`
appears in. Those two are a deliberate, tracked gap, not an oversight: the manifest is
app-written and app-trusted, so a malformed one or a schema mismatch is an app-side bug a
user should never actually see, and no user-facing copy has been invented for a case the
design doesn't expect to reach a user.

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

**A known gap, MEASURED 2026-09-19 against `worker/runner.py`**: the implementation is
coarser than this table. Per-input output uploads happen with `stage` still reading
`"running"` (there is no separate write moving through `uploading`), and applying the
after-task lifecycle happens with no corresponding `finalizing` write to `status.json` at
all - the worker calls `apply_lifecycle()` after `status.stop()` has already sent its last
heartbeat. Neither omission loses information the app strictly needs (`result.json`'s
existence, not any particular `stage` string, is what signals a terminal state - see
section 7), but an app polling `status.json` for a live "what is it doing right now" label
will never actually observe `uploading` or `finalizing` today. This table describes the
intended granularity; narrowing it to match the implementation, or having the worker
actually emit these two stages, is an open question for whoever picks it up next.

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
- **MEASURED 2026-09-19 (issue #252, closed):** the input that was actively running when
 the cancel was seen gets its own `result.json` entry with `status: "cancelled"` and
 whatever of its contigs/records finished are listed in its `outputs`, rather than that
 input silently vanishing from the result entirely (its files were already being uploaded
 to the bucket by this point; leaving it out of `result.json` would have orphaned them,
 nothing pointing at them). A crash mid-input gets the equivalent treatment for
 `status: "failed"` - completed contigs/records are uploaded and listed before the input
 is marked failed, the same "one bad record doesn't cost the whole input" partial-results
 discipline this section already describes at the job level, now also applied one level
 down, inside a single input.
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

**MEASURED 2026-09-19 (issue #320, closed):** the worker's own tail - write `result.json`,
stop the heartbeat, apply the after-task lifecycle - is three independent steps, each
wrapped so a failure in one does not silently skip the next. Earlier, a transient store
failure writing `result.json` itself (the one write this whole section is about) would
skip both the heartbeat's final write and the lifecycle call outright; now each step's own
failure is caught and recorded as a `progress.jsonl` notice rather than propagating. A
lifecycle failure specifically - the one that costs real money, since it means the VM did
not stop or delete itself - is backstopped by `instanceTerminationAction=DELETE` at
`manifest.limits.maxRunSeconds` and, per `worker/cli.py`'s own exit-code contract, an
independent cleanup dispatch in the VM startup script keyed off the worker process's exit
code; see `docs/ToTest.md` for why none of this - including the lifecycle Compute API
calls themselves - has ever run against a real GCP project.

`status` is one of `done | failed | cancelled`; a batch where some inputs succeeded and
others failed still writes `status: "done"` at the job level with per-input `status`
values distinguishing them (the app's `JobPhase` maps this combination to
`PartiallyCompleted` - see `architecture.md`). Per-input `status` is one of
`done | failed | cancelled` too (MEASURED 2026-09-19, issue #252 - `cancelled` used to be
a documented value nothing ever actually produced; see section 6 above for when each one
appears), each carrying whatever `outputs` were uploaded before that input's outcome was
decided, and `failed`/`cancelled` inputs also carry an `error` object shaped like
`status.json`'s (`{code, message, retriable, detail?, remediation?}`).

**A whole-batch refusal, before any input is attempted (issue #248):** if the batch
exceeds `manifest.limits.maxInputs` or `maxTotalNt`, the worker refuses the entire job
before running a single predictor call, and `result.json` looks like this instead:

```json
{
  "schema": 1,
  "jobId": "20260918-142233-k7q2vx",
  "status": "failed",
  "inputs": [],
  "timing": {"startedAt": "2026-09-18T14:23:10Z", "finishedAt": "2026-09-18T14:23:11Z"},
  "gpu": {"name": null, "zone": null, "spot": false},
  "error": {
    "code": "BATCH_LIMIT_EXCEEDED",
    "message": "This batch is 63 file(s), 4,200,000 nt total - over the configured limit (63 files (limit 50)). ...",
    "retriable": false
  }
}
```

`inputs` is empty because nothing was attempted, not because every input individually
failed - `timing` still spans real wall-clock time, since the worker measures the batch's
actual total nt (downloading and parsing every input, cheaply, with no predictor call)
before it can know whether to refuse. The message names the batch's own real, measured
numbers, not a generic "too large" - see `science_and_formats.md`'s cross-reference and
`worker/batch_limits.py`'s own docstring for why it deliberately does not also quote a
dollar estimate for the refused batch (no verified nt/second throughput figure exists
anywhere in this repository to make that honest).

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
