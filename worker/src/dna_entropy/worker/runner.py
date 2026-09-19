"""The manifest-driven run loop: manifest.json -> pipeline.run() per input -> result.json.

docs/job_contract.md is the contract this satisfies. The high-level shape:

1. Read + parse ``manifest.json`` (a bad/unsupported schema fails fast, before anything
   else — see ``manifest.py``).
2. Start the :class:`~.status.StatusWriter` heartbeat.
3. For each input: stage ``running``, download it locally, run
   :func:`dna_entropy.pipeline.run` with cooperative-cancellation hooks wired to
   :class:`~.cancel.CancelWatcher`, upload every output file, record its result.
4. Write ``result.json`` **last**, after every output is confirmed uploaded — its mere
   existence is the app's signal the job reached a terminal state (job_contract.md §7).
5. Apply the after-task lifecycle (stop/delete/keep) via the Compute API — **only** for a
   real cloud (``store.kind == "gcs"``) job; a local run has no VM to manage. Not
   exercised against real GCP by anything in this repo tonight (see ``lifecycle.py``).
"""

from __future__ import annotations

import dataclasses
import shutil
import tempfile
import time
import traceback
from pathlib import Path

from .. import __version__ as WORKER_VERSION
from .. import pipeline
from ..predictors.base import PredictorError
from ..readers.input import load_input
from ..validation.validators import ValidationError
from .batch_limits import BatchLimitError, check_batch_limits
from .blobstore import Blobstore, BlobstoreError, write_json
from .cancel import CancelWatcher, JobCancelledError
from .errors import is_retriable
from .lifecycle import LifecycleError, apply_lifecycle
from .manifest import InputSpec, JobManifest, ManifestError
from .result import InputResult, JobResult, ResultTiming
from .status import GpuInfo, StatusWriter, WorkerInfo

RESULT_PATH = "result.json"
MANIFEST_PATH = "manifest.json"


def _utc_now_iso() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def _free_disk_gb(path: Path) -> float:
    try:
        usage = shutil.disk_usage(path)
        return round(usage.free / (1024**3), 1)
    except OSError:
        return -1.0


def _upload_local_outputs(store: Blobstore, local_out: Path, input_spec: InputSpec) -> list[str]:
    """Upload every file currently sitting in ``local_out`` to ``output/<input name>/``
    and return the destination paths (issue #252).

    ONE upload path, used for a normal completion AND for whatever a crash or a
    cancellation left behind — not two separately-maintained ones. ``pipeline.run()``
    already writes whatever contigs/records completed to ``local_out``, best-effort,
    before re-raising ANY exception out of its per-contig loop (job_contract.md §6:
    "partial results are always kept, never discarded" — that promise was already true on
    local disk; the gap this closes is that nothing then uploaded them). Safe to call on a
    ``local_out`` that does not exist yet (nothing was ever written) — returns ``[]``.
    """
    if not local_out.is_dir():
        return []
    uploaded: list[str] = []
    for local_path in sorted(p for p in local_out.iterdir() if p.is_file()):
        dest = f"output/{input_spec.name}/{local_path.name}"
        store.upload_file(local_path, dest)
        uploaded.append(dest)
    return uploaded


def _measure_batch_total_nt(store: Blobstore, manifest: JobManifest, tmp: Path) -> int:
    """Sum every input's actual nt count, cheaply, before any predictor runs (issue #248).

    Downloads and parses each input with the same :func:`~..readers.input.load_input`
    the real per-input run uses later — no duplicated parsing logic — but does none of
    the expensive part (no predictor call, no windowing). Re-downloads each input a
    second time (the real run stages it again under its own path); accepted as a cheap
    trade-off rather than restructuring the per-input download into a two-phase
    measure-then-run design, since I/O is not what a cost limit exists to protect against.

    A bad/missing/malformed input is silently skipped HERE (counted as 0 nt) rather than
    raised: this function's only job is measuring total nt for the batch-level guardrail,
    and reporting *that* particular input's own real problem is `_run_one_input`'s job,
    later, on the real pass — raising it here instead would turn an isolated per-input
    failure into a batch-wide refusal, a regression from today's per-input isolation
    (job_contract.md §7).
    """
    total = 0
    for input_spec in manifest.inputs:
        try:
            local_input = tmp / "prescan" / Path(input_spec.path).name
            store.download_file(input_spec.path, local_input)
            cfg = manifest.build_run_config(
                input_spec,
                local_input_path=str(local_input),
                local_out_dir=str(tmp / "prescan-out"),
            )
            loaded = load_input(cfg)
            total += sum(len(c.seq) for c in loaded.contigs)
        except Exception:
            continue
    return total


def _run_one_input(
    store: Blobstore,
    manifest: JobManifest,
    input_spec: InputSpec,
    tmp: Path,
    status: StatusWriter,
    cancel: CancelWatcher,
) -> InputResult:
    """Stage, run, and upload results for one manifest input. A cancellation or any other
    failure seen BEFORE this input even starts propagates out (job-level: stop the whole
    job before doing any work on it, nothing to upload). A cancellation or failure seen
    WHILE this input is running is caught here and reported as a "cancelled"/"failed"
    :class:`InputResult` — with whatever contigs/records completed first already uploaded
    (issue #252) — so a batch with one bad input still finishes the rest, and a
    cancellation mid-input never silently drops that input out of ``result.json``
    (job_contract.md §7)."""
    status.update(stage="running", detail={"input": input_spec.id})
    cancel.check_or_raise()  # pre-start: nothing has run yet, nothing to upload if this fires

    def _on_window() -> None:
        cancel.check_or_raise()

    def _on_contig(contig) -> None:
        cancel.check_or_raise()
        status.update(detail={"input": input_spec.id, "contig": contig.name})

    # local_out is computed before the try block (just a path join, no I/O) so the except
    # branches below can always find it, even if staging the input itself is what failed.
    local_out = tmp / "output" / input_spec.name

    # Staging the input, building its RunConfig, AND running the pipeline are all inside
    # this one try/except: a missing/unreadable input file must fail only THIS input, the
    # same as a bad sequence inside it — not crash the whole job (job_contract.md §7,
    # regression-tested by test_missing_input_file_fails_that_input_not_the_whole_job).
    try:
        local_input = tmp / "input" / Path(input_spec.path).name
        store.download_file(input_spec.path, local_input)

        cfg = manifest.build_run_config(
            input_spec,
            local_input_path=str(local_input),
            local_out_dir=str(local_out),
        )
        result = pipeline.run(cfg, on_window=_on_window, on_contig=_on_contig)
    except JobCancelledError:
        # Cancellation mid-input (between two windows/contigs of THIS input), distinct
        # from the pre-start check above: this input already did real work, so it is
        # reported — with that work uploaded — rather than silently vanishing from
        # result.json, which would orphan the very files just uploaded. The caller
        # (run_job) checks `cancel.is_cancelled` after every input to stop the loop; it
        # does not need this to propagate as an exception to do that.
        uploaded = _upload_local_outputs(store, local_out, input_spec)
        return InputResult(id=input_spec.id, status="cancelled", outputs=uploaded)
    except Exception as exc:
        # A specific exception's OWN `.code` (ModelNeedsHopperError -> MODEL_NEEDS_HOPPER,
        # PredictorOOMError -> MODEL_OOM, ...) always wins over the generic fallback below
        # — a bug this fixed: (ValidationError, PredictorError, ManifestError,
        # BlobstoreError) used to be caught as ONE tuple and always labeled
        # "INPUT_INVALID", which silently mislabeled every PredictorError subclass with
        # its own more specific code (see errors.py's module docstring / #254's closing
        # comment for the two cases this caught: a Hopper-only model request and a
        # second-OOM both used to report INPUT_INVALID instead of their real code).
        code = getattr(exc, "code", None)
        if code is None:
            if isinstance(exc, (ValidationError, PredictorError, ManifestError, BlobstoreError)):
                code = "INPUT_INVALID"
            else:
                code = "WORKER_CRASH"
                status.notice(f"{input_spec.id}: worker crashed: {exc}", level="error")
        error: dict = {"code": code, "message": str(exc), "retriable": is_retriable(code)}
        if code == "WORKER_CRASH":
            error["detail"] = traceback.format_exc()
        # issue #252: whatever contigs/records completed before the crash are already on
        # local disk (pipeline.run()'s own best-effort write) — upload them so one bad
        # record doesn't cost the whole input, not just the whole job.
        uploaded = _upload_local_outputs(store, local_out, input_spec)
        return InputResult(id=input_spec.id, status="failed", error=error, outputs=uploaded)

    uploaded = _upload_local_outputs(store, local_out, input_spec)
    for note in result.notices:
        status.notice(note, data={"input": input_spec.id})

    return InputResult(id=input_spec.id, status="done", outputs=uploaded)


def run_job(store: Blobstore, *, worker_version: str = WORKER_VERSION) -> JobResult:
    """Run the full job described by ``manifest.json`` in ``store``. Returns the
    :class:`JobResult` that was also written to ``result.json``.

    This is the ``worker-run`` CLI command's real implementation (see
    ``dna_entropy.cli.worker_run``). A manifest schema mismatch or any other manifest
    parse failure is raised BEFORE any ``status.json``/heartbeat exists — there is nothing
    useful to heartbeat about a manifest the worker cannot even read — and is the caller's
    responsibility to report (the CLI does this via its normal error path).
    """
    manifest_text = store.read_text(MANIFEST_PATH)
    manifest = JobManifest.parse(manifest_text)  # ManifestSchemaError/ManifestError propagate

    status = StatusWriter(
        store,
        manifest.job_id,
        worker=WorkerInfo(version=worker_version, image=manifest.worker.image),
    )
    status.start()
    cancel = CancelWatcher(store)
    started_at = _utc_now_iso()

    input_results: list[InputResult] = []
    job_status = "done"
    job_error: dict | None = None

    try:
        with tempfile.TemporaryDirectory(prefix="deg-job-") as tmpdir:
            tmp = Path(tmpdir)
            status.set_vm(GpuInfo())  # no GPU identity available off a real VM (yet)
            status.notice(f"worker starting; free disk {_free_disk_gb(tmp)} GB")

            # Cost guardrail (issue #248), before any predictor call: cheap to measure
            # (I/O + parsing only, no GPU), so a batch that exceeds manifest.limits is
            # refused before spending anything, not partway through.
            total_nt = _measure_batch_total_nt(store, manifest, tmp)
            check_batch_limits(
                n_inputs=len(manifest.inputs),
                total_nt=total_nt,
                max_inputs=manifest.limits.max_inputs,
                max_total_nt=manifest.limits.max_total_nt,
            )

            for input_spec in manifest.inputs:
                input_results.append(_run_one_input(store, manifest, input_spec, tmp, status, cancel))
                if cancel.is_cancelled:
                    break  # stop the whole job, not just this input (job_contract.md §6)

    except BatchLimitError as exc:
        job_status = "failed"
        job_error = {"code": exc.code, "message": str(exc), "retriable": False}
        status.notice(f"batch refused: {exc}", level="error")
    except JobCancelledError:
        # Seen BEFORE an input even started (_run_one_input's own pre-start check raises
        # directly, since nothing ran yet and there is nothing to upload) — the mid-input
        # case is handled inside _run_one_input itself, which returns a "cancelled"
        # InputResult instead of raising, so the `for` loop above can finish appending it
        # and stop cleanly via `cancel.is_cancelled` rather than unwinding through here.
        pass
    except Exception as exc:  # a whole-job-level crash outside any single input's handling
        job_status = "failed"
        job_error = {"code": "WORKER_CRASH", "message": str(exc), "retriable": False}
        status.notice(f"job-level crash: {exc}", level="error")

    if job_status != "failed" and cancel.is_cancelled:
        job_status = "cancelled"
        status.notice("job cancelled (control/cancel seen)", level="notice")

    finished_at = _utc_now_iso()
    status.update(
        stage=job_status,
        percent=100.0,
        error=job_error,
        detail={"inputs": [dataclasses.asdict(r) for r in input_results]},
    )

    result = JobResult(
        schema=1,
        jobId=manifest.job_id,
        status=job_status,
        inputs=input_results,
        timing=ResultTiming(startedAt=started_at, finishedAt=finished_at),
        error=job_error,
    )
    # Written LAST, after every output is confirmed uploaded — job_contract.md §7: its
    # mere presence, not just its contents, is the app's "this job reached a terminal
    # state" signal, checked before status.json's heartbeat on every app launch.
    #
    # issue #320: this write, status.stop(), and apply_lifecycle() used to run as three
    # unguarded statements in a row — a BlobstoreError writing result.json skipped BOTH
    # the status stop and the lifecycle call, leaving status.json stuck at a non-terminal
    # stage with no result.json ever appearing, and (separately) a VM that failed to stop
    # or delete itself with nothing recording that it happened. Each step is now
    # independent: one failing must not skip the next. `status.notice()` itself can never
    # raise (issues #318/#319), so calling it from inside these except blocks is safe even
    # if the SAME store outage caused the failure being reported.
    try:
        write_json(store, RESULT_PATH, result.to_dict())
    except BlobstoreError as exc:
        status.notice(f"result.json write failed: {exc}", level="error")

    status.stop()

    # Lifecycle only applies to a real cloud VM; a local run has no VM to stop/delete, and
    # `keep` is a genuine no-op even in the cloud case. This is the module the overnight
    # brief says to implement but never call for real — a local/localdir job never reaches
    # the `apply_lifecycle` call at all, so no test in this repo exercises it against a
    # real network no matter how this function is invoked.
    if manifest.store.kind == "gcs" and manifest.lifecycle.after_task != "keep":
        try:
            # issue #321: a 2xx HTTP status only means the Compute API ACCEPTED the
            # operation, not that stop/delete actually completed — apply_lifecycle()
            # now returns the operation's own response body (or raises if that body
            # itself already reports an error) instead of discarding it.
            operation = apply_lifecycle(manifest.lifecycle.after_task)
            op_id = (operation.get("name") or operation.get("id")) if operation else None
            if op_id:
                status.notice(f"lifecycle {manifest.lifecycle.after_task}: operation {op_id} accepted")
        except LifecycleError as exc:
            # best-effort; instanceTerminationAction=DELETE and startup.sh's own
            # exit-code-driven cleanup dispatch are both independent backstops for
            # exactly this case (see #320's closing report for why that softens the
            # consequence) — but a failure here is now AT LEAST visible, not silently
            # swallowed the way `contextlib.suppress(LifecycleError)` used to leave it.
            status.notice(f"lifecycle apply failed: {exc}", level="error")

    return result
