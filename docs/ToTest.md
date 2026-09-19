# ToTest queue

Hard Rule 17's one carve-out from "GitHub Issues is the only tracker": a
closed issue whose behaviour could not be proven on the thing it actually
ships on. Not a backlog — a queue that must drain. See
[`docs/README.md`](README.md)'s house-style table for how this surface
relates to Issues and `sprint_log.md`.

## Rules

1. Add a row when you close an issue whose behaviour you could not prove on
   the thing it ships on: an installer build, a real GPU VM, a second
   Google account, a local GPU. **"Tests pass" is never that proof** — see
   the `wired-to-nothing` skill for why a green suite does not prove a
   feature is reachable.
2. A row names: the issue, the closing commit sha (validated against the
   real commit graph, not typed from memory), what a human needs (`Needs`),
   the exact action to take, what passing looks like, and what a **false**
   pass looks like — the thing that would fool a quick check into reporting
   success when the behaviour is actually still broken.
3. Delete the row when someone verifies it. If verification fails, reopen
   the issue with the measured evidence, then delete the row — a failed
   verification does not stay in this table, it goes back to being an open
   issue.
4. Group rows by `Needs`, and drain by group, because the cost here is
   setup (booting an installer build, spinning up a GPU VM), not the
   individual check. Doing five `installer`-needs rows in one sitting costs
   one installer build, not five.
5. [`docs/release_runbook.md`](release_runbook.md) drains every `installer`
   row as part of every release. A release with `installer` rows still open
   is not a release.
6. `scripts/check_totest_format.py --max-age-days 45` (once that script
   exists) fails CI on any row older than 45 days, so this table cannot
   quietly turn into a second backlog.

`Needs` is one of: `app-dev`, `installer`, `gpu-vm`, `cpu-vm`,
`two-accounts`, `local-gpu`.

## Row format

```
| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |
```

## Queue

Three rows, all `Needs: cpu-vm` and all from the same closing commit, so they
drain in one sitting per Rule 4: the worker subpackage (#278) implements
every cloud-facing path (the Compute API lifecycle calls, the real GCS REST
path, and the app/worker version-mismatch refusal #250 also asks for) and
tests every one of them against `LocalBlobstore` and a scripted fake, per
its own closing comment, because nothing tonight was allowed to spend
money. None of it has ever touched a real Google Cloud project.

| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |
| --- | --- | --- | --- | --- | --- |
| #278 | dcd89a7 | cpu-vm | On a real Compute Engine VM (any machine type, no GPU needed) with the worker service account attached, run a real job through to a terminal status with `lifecycle.afterTask=stop`, then again with `afterTask=delete`, and watch the VM's real state (Console or `instances.aggregatedList`) rather than only the worker's own exit code. | The VM reaches `TERMINATED` (stop) or disappears (delete) within seconds of the worker's terminal status, and the Compute operation history shows the stop/delete request came from the worker's service account via the metadata token, well before `maxRunDuration` could have expired. | The VM eventually reaches `TERMINATED` or is deleted, but only because `scheduling.maxRunDuration` expired and `instanceTerminationAction=DELETE` fired, indistinguishable from a real pass by looking at the VM's final state alone; only the operation's actor and timing tell them apart. |
| #278 | dcd89a7 | cpu-vm | From that same VM, run the CPU smoke test (mock predictor, `-cpu` image) against a real `gs://` bucket so `GcsBlobstore` writes `status.json`/`progress.jsonl`/`result.json`/outputs through the real metadata-token REST path, then read every one of them back. | Every object the worker claims to have written is a real, readable object in the bucket (`gsutil ls`/Console), the bytes round-trip exactly, and the app (or `gsutil cp`) can download `result.json` and every output afterward. | `pytest -m "not gpu"` stays green (309 passed, 2 skipped), it already is, and proves nothing here, since every one of those tests runs against the in-memory fake `Blobstore`, which does not exercise the real REST calls, auth header, retries, or GCS's own generation semantics. |
| #278 | dcd89a7 | cpu-vm | Build or point the app's dev override at a worker image whose supported schema disagrees with a real app build's `manifest.json.schema`, run one real job through the actual container on a real VM, and watch what the app shows (#250's scope; #278 shipped the worker-side half). | The job fails within seconds of the container starting; `status.json.error.code` is `WORKER_VERSION_MISMATCH` naming both the manifest's declared schema and the worker's supported version; the app (once `app/`, issue #61, exists) shows exactly one action. | `worker/tests/test_worker_manifest.py` and `test_worker_runner.py`'s in-process schema-mismatch tests pass (they already do), they prove the Python refusal logic is correct, not that a real container boot-and-refuse cycle reads as a clean, one-action failure to a user rather than a hang or a raw stack trace, since `app/` does not exist yet to render that action at all. |
