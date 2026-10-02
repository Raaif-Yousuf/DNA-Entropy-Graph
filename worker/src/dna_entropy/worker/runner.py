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
import hashlib
import os
import shutil
import tempfile
import time
import traceback
from collections.abc import Callable
from pathlib import Path

from .. import __version__ as WORKER_VERSION
from .. import pipeline
from ..analysis.windowing import WindowingError
from ..config import RunConfig
from ..predictors.base import Predictor, PredictorError
from ..readers.input import load_input
from ..validation.validators import ValidationError
from .batch_limits import BatchLimitError, check_batch_limits
from .blobstore import Blobstore, BlobstoreError, GcsBlobstore, write_json
from .cancel import CancelWatcher, JobCancelledError
from .errors import is_retriable
from .gpu import detect_gpu
from .lifecycle import LifecycleError, apply_lifecycle
from .manifest import InputSpec, JobManifest, ManifestError, StoreSpec
from .result import InputResult, JobResult, ResultFile, ResultGpu, ResultStats, ResultTiming
from .status import (
    DEFAULT_INTERVAL_SECONDS,
    GpuInfo,
    StatusWriter,
    WorkerInfo,
    write_local_fallback,
)

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


def _upload_local_outputs(
    store: Blobstore, local_out: Path, input_spec: InputSpec
) -> tuple[list[str], list[ResultFile]]:
    """Upload every file currently sitting in ``local_out`` to ``output/<input name>/``
    and return ``(destination paths, the same paths with sha256 and size)`` (issues #252,
    #41).

    ONE upload path, used for a normal completion AND for whatever a crash or a
    cancellation left behind — not two separately-maintained ones. ``pipeline.run()``
    already writes whatever contigs/records completed to ``local_out``, best-effort,
    before re-raising ANY exception out of its per-contig loop (job_contract.md §6:
    "partial results are always kept, never discarded" — that promise was already true on
    local disk; the gap this closes is that nothing then uploaded them). Safe to call on a
    ``local_out`` that does not exist yet (nothing was ever written) — returns ``([], [])``.
    The hash is taken from the local bytes that are uploaded.
    """
    if not local_out.is_dir():
        return [], []
    uploaded: list[str] = []
    files: list[ResultFile] = []
    for local_path in sorted(p for p in local_out.iterdir() if p.is_file()):
        dest = f"output/{input_spec.name}/{local_path.name}"
        digest = hashlib.sha256(local_path.read_bytes()).hexdigest()
        store.upload_file(local_path, dest)
        uploaded.append(dest)
        files.append(ResultFile(path=dest, sha256=digest, bytes=local_path.stat().st_size))
    return uploaded, files


class _SharedPredictor:
    """Builds the batch's predictor lazily, at most ONCE, and remembers a failure.

    Issue #41 ("one model load per batch"): ``pipeline.run()`` used to build a fresh
    predictor for every input, so a 3-input Evo batch loaded the model three times. The
    first input's ``RunConfig`` is the one the predictor is built from (``predictor.*`` is
    job-level in the manifest, identical for every input). A build that raises is cached
    and re-raised for every remaining input: a missing-weights or Hopper-only refusal is
    not going to succeed on the second try, and retrying it per input would repeat a
    multi-minute model load N times.
    """

    def __init__(self, factory: Callable[[RunConfig], Predictor]) -> None:
        self._factory = factory
        self._predictor: Predictor | None = None
        self._error: Exception | None = None

    def get(self, cfg: RunConfig, status: StatusWriter) -> Predictor:
        if self._error is not None:
            raise self._error
        if self._predictor is None:
            status.update(stage="model-loading")
            try:
                self._predictor = self._factory(cfg)
            except Exception as exc:
                self._error = exc
                raise
        return self._predictor


def _check_store_matches_manifest(store: Blobstore, spec: StoreSpec) -> None:
    """Refuse a manifest whose declared GCS ``bucket``/``prefix`` is not the store this
    worker was started against (issue #448).

    The worker's store is built from its command line before the manifest can even be read
    (the manifest lives in it), so a manifest naming a different bucket or prefix means the
    app and the VM disagree about where the job lives; carrying on would write outputs and
    ``result.json`` where the app is not looking. A field the manifest leaves empty is "not
    declared" and is not checked. ``store.root`` (a local folder) is deliberately NOT
    cross-checked: it is the user's own folder on the same machine, and Windows path
    spellings (case, 8.3 names, junctions) make an equality test a source of false
    refusals for no protection a GCS bucket mismatch would need.
    """
    if not isinstance(store, GcsBlobstore) or spec.kind != "gcs":
        return
    declared_prefix = spec.prefix.strip("/")
    actual_prefix = store.prefix.strip("/")
    if spec.bucket and spec.bucket != store.bucket:
        raise ManifestError(
            f"manifest.json says this job lives in bucket {spec.bucket!r}, but this worker was "
            f"started against bucket {store.bucket!r}. Start the job again from the app."
        )
    if declared_prefix and declared_prefix != actual_prefix:
        raise ManifestError(
            f"manifest.json says this job lives under {declared_prefix!r}, but this worker was "
            f"started against {actual_prefix!r}. Start the job again from the app."
        )


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
    shared_predictor: _SharedPredictor,
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
        predictor = shared_predictor.get(cfg, status)
        status.update(stage="running", detail={"input": input_spec.id})
        result = pipeline.run(
            cfg,
            on_window=_on_window,
            on_contig=_on_contig,
            provenance_extra={"input_sha256": hashlib.sha256(local_input.read_bytes()).hexdigest()},
            predictor=predictor,
        )
    except JobCancelledError:
        # Cancellation mid-input (between two windows/contigs of THIS input), distinct
        # from the pre-start check above: this input already did real work, so it is
        # reported — with that work uploaded — rather than silently vanishing from
        # result.json, which would orphan the very files just uploaded. The caller
        # (run_job) checks `cancel.is_cancelled` after every input to stop the loop; it
        # does not need this to propagate as an exception to do that.
        uploaded, files = _upload_local_outputs(store, local_out, input_spec)
        return InputResult(id=input_spec.id, status="cancelled", outputs=uploaded, files=files)
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
            # WindowingError (K below 128, an input under 10 nt, a ceiling below K) and
            # PipelineError (an unusable run name) are user-correctable input problems
            # whose messages already name the fix (issue #455): not a crash, no traceback.
            if isinstance(
                exc,
                (
                    ValidationError,
                    WindowingError,
                    pipeline.PipelineError,
                    PredictorError,
                    ManifestError,
                    BlobstoreError,
                ),
            ):
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
        uploaded, files = _upload_local_outputs(store, local_out, input_spec)
        return InputResult(id=input_spec.id, status="failed", error=error, outputs=uploaded, files=files)

    uploaded, files = _upload_local_outputs(store, local_out, input_spec)
    for note in result.notices:
        status.notice(note, data={"input": input_spec.id})

    return InputResult(
        id=input_spec.id,
        status="done",
        outputs=uploaded,
        files=files,
        notices=list(result.notices),
        stats=ResultStats(
            contigs=result.contigs,
            totalNt=result.total_nt,
            meanEntropy=float(result.all_values.mean()),
            minEntropy=float(result.all_values.min()),
            maxEntropy=float(result.all_values.max()),
        ),
    )


@dataclasses.dataclass(frozen=True)
class RunOutcome:
    """What :func:`run_job_outcome` returns: the job's :class:`JobResult` (also written to
    ``result.json``) plus the one thing ``result.json`` deliberately does not carry.

    ``lifecycle_applied`` is ``"stop"`` or ``"delete"`` when this worker successfully asked
    the Compute API to stop/delete its own VM (issue #44), else ``None`` (a local run, a
    failed API call, or no lifecycle to apply). ``dna-entropy-worker run`` turns it into
    exit code 10/11 so the startup script knows the action was already applied.
    """

    result: JobResult
    lifecycle_applied: str | None = None


def run_job(
    store: Blobstore,
    *,
    worker_version: str = WORKER_VERSION,
    predictor_factory: Callable[[RunConfig], Predictor] | None = None,
) -> JobResult:
    """Run the full job described by ``manifest.json`` in ``store`` and return the
    :class:`JobResult` that was also written to ``result.json``. See :func:`run_job_outcome`
    for the variant that also reports whether the VM lifecycle was applied."""
    return run_job_outcome(store, worker_version=worker_version, predictor_factory=predictor_factory).result


def run_job_outcome(
    store: Blobstore,
    *,
    worker_version: str = WORKER_VERSION,
    predictor_factory: Callable[[RunConfig], Predictor] | None = None,
) -> RunOutcome:
    """Run the full job described by ``manifest.json`` in ``store``. Returns a
    :class:`RunOutcome` whose ``result`` is the :class:`JobResult` that was also written
    to ``result.json``.

    This is the ``worker-run`` CLI command's real implementation (see
    ``dna_entropy.cli.worker_run``). A manifest schema mismatch or any other manifest
    parse failure is raised BEFORE any ``status.json``/heartbeat exists — there is nothing
    useful to heartbeat about a manifest the worker cannot even read — and is the caller's
    responsibility to report (the CLI does this via its normal error path).
    """
    manifest_text = store.read_text(MANIFEST_PATH)
    manifest = JobManifest.parse(manifest_text)  # ManifestSchemaError/ManifestError propagate
    _check_store_matches_manifest(store, manifest.store)  # before anything is written (#448)

    # Found auditing #304/#306: manifest.limits.heartbeatSeconds was parsed but never
    # reached here, so the worker always ticked at status.py's own hardcoded
    # DEFAULT_INTERVAL_SECONDS regardless of what the manifest declared. CLAUDE.md calls
    # the status.json heartbeat the ONLY health signal the app has, so never ticking
    # SLOWER than the manifest's own declared cadence matters; `min()` with the existing
    # default keeps this at least as fast as before for the common (30s) case, and never
    # slower than a manifest asking for tighter liveness detection either. progress.jsonl's
    # own 5-10s re-upload cadence (job_contract.md §4) is an independent design constant,
    # not something heartbeatSeconds is meant to relax — so this never goes ABOVE
    # DEFAULT_INTERVAL_SECONDS regardless of how large a manifest's heartbeatSeconds is.
    heartbeat_interval = min(float(manifest.limits.heartbeat_seconds), DEFAULT_INTERVAL_SECONDS)
    status = StatusWriter(
        store,
        manifest.job_id,
        interval_seconds=heartbeat_interval,
        worker=WorkerInfo(version=worker_version, image=manifest.worker.image),
    )
    status.start()
    # issue #344: manifest.worker.version is the app's DECLARED expectation, not
    # necessarily the actually-running build's own version (WorkerRef's own docstring) --
    # this was never compared to anything, so a real disagreement (an app talking to a
    # stale or newer worker image than it expects) was invisible. Visibility only, per
    # the issue's own scope: never refuses or fails the job, and an undeclared
    # expectation (the default "") is not a mismatch against anything.
    if manifest.worker.version and manifest.worker.version != worker_version:
        status.notice(
            f"manifest declares worker.version={manifest.worker.version!r} but this "
            f"build is {worker_version!r} -- proceeding anyway (visibility only, not "
            "a refusal)."
        )
    # issue #338: manifest.limits.cancelPollSeconds now actually reaches the throttle
    # CancelWatcher applies to its own store round-trips (see cancel.py's docstring).
    cancel = CancelWatcher(store, poll_interval_seconds=float(manifest.limits.cancel_poll_seconds))
    # Issue #41: ONE predictor for the whole batch, built lazily from the first input's config.
    shared_predictor = _SharedPredictor(predictor_factory or pipeline.build_predictor)
    started_at = _utc_now_iso()

    input_results: list[InputResult] = []
    job_status = "done"
    job_error: dict | None = None
    gpu = GpuInfo()

    try:
        with tempfile.TemporaryDirectory(prefix="deg-job-") as tmpdir:
            tmp = Path(tmpdir)
            # Issue #74: the GPU identity comes from nvidia-smi (worker/gpu.py), never from
            # the manifest. "GPU: none" on a CPU VM or a local run is said in words, so the
            # first progress line always tells the app what hardware this really is.
            gpu = detect_gpu()  # (pre-initialised below the try so result.json can read it)
            status.set_vm(gpu, name=os.environ.get("DEG_VM_NAME") or None)
            gpu_label = f"{gpu.name} (driver {gpu.driver})" if gpu.name else "no GPU detected"
            status.notice(f"worker starting; GPU: {gpu_label}; free disk {_free_disk_gb(tmp)} GB")

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
                input_results.append(
                    _run_one_input(store, manifest, input_spec, tmp, status, cancel, shared_predictor)
                )
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
        gpu=ResultGpu(name=gpu.name, zone=gpu.zone),
        error=job_error,
    )
    # Written LAST, after every output is confirmed uploaded — job_contract.md §7: its
    # mere presence, not just its contents, is the app's "this job reached a terminal
    # state" signal, checked before status.json's heartbeat on every app launch.
    #
    # issue #320: this write, status.stop(), and apply_lifecycle() used to run as three
    # unguarded statements in a row - a BlobstoreError writing result.json skipped BOTH
    # the status stop and the lifecycle call, leaving status.json stuck at a non-terminal
    # stage with no result.json ever appearing, and (separately) a VM that failed to stop
    # or delete itself with nothing recording that it happened. Each step is now
    # independent: one failing must not skip the next. `status.notice()` itself can never
    # raise (issues #318/#319), so calling it from inside these except blocks is safe even
    # if the SAME store outage caused the failure being reported.
    #
    # issue #41, the TRUE write order: every output upload, the heartbeat's final
    # status.json/progress.jsonl snapshot (stop() below), then result.json, and only then
    # progress.jsonl notices. Those later notices are lifecycle-related or error reports
    # (the keep-window, no-window and operation-accepted notices after this point, and the
    # result-write-failure notices just below), and each re-uploads progress.jsonl. So
    # result.json is the newest object when it appears, but it is NOT the last write the
    # worker ever makes; that residual is deliberate and the app does not wait for it.
    status.stop()
    try:
        write_json(store, RESULT_PATH, result.to_dict())
    except BlobstoreError as exc:
        status.notice(f"result.json write failed: {exc}", level="error")
        # issue #341, DECISION: the sharp end of a persistent store outage is a run that
        # finished successfully and cannot say so. A local-disk fallback copy, independent
        # of the (broken) configured store, is the only thing standing between "the app
        # concludes this job died" and the job's own real outcome being recoverable at all
        # (see status.py's LOCAL_FALLBACK_DIR docstring for exactly what this can and
        # cannot guarantee - it does not survive instanceTerminationAction=DELETE).
        try:
            fallback_path = write_local_fallback(manifest.job_id, "result", result.to_dict())
            status.notice(f"result.json written to a local fallback instead: {fallback_path}")
        except OSError as fallback_exc:
            status.notice(f"local result.json fallback also failed: {fallback_exc}", level="error")

    # Lifecycle only applies to a real cloud VM; a local run has no VM to stop/delete. This
    # is the module the overnight brief says to implement but never call for real — a
    # local/localdir job never reaches the `apply_lifecycle` call at all, so no test in
    # this repo exercises it against a real network no matter how this function is invoked.
    lifecycle_applied: str | None = None
    if manifest.store.kind == "gcs":
        # Hard Rule 11: "keep" always has an expiry. Issue #464 (MEASURED by reading
        # the code, 2026-10-02): "keep" used to degrade to afterKeepAlive at once, so a
        # keep-alive run was stopped the moment the job ended. Now:
        #   - keepAliveMinutes > 0: the worker makes NO Compute call and reports no
        #     lifecycle applied (exit 0/2/3). worker/vm/startup.sh holds the VM for the
        #     window, capped inside maxRunDuration, then applies afterKeepAlive.
        #   - no window: nothing to hold the VM for, so afterKeepAlive is applied now (a
        #     "keep" with both fields "keep" falls back to "stop").
        # The follow-up-job queue that makes a warm VM useful is issue #93.
        effective_action = manifest.lifecycle.after_task
        if effective_action == "keep":
            after_window = manifest.lifecycle.after_keep_alive
            if after_window not in ("stop", "delete"):
                after_window = "stop"
            window = manifest.lifecycle.keep_alive_minutes
            if window > 0:
                status.notice(
                    f"lifecycle: keeping the VM for {window} minutes, then applying "
                    f"{after_window!r}; startup.sh holds the window and enforces the expiry "
                    "(Hard Rule 11), the worker makes no Compute call."
                )
                return RunOutcome(result=result, lifecycle_applied=None)
            effective_action = after_window
            status.notice(
                f"lifecycle: afterTask='keep' with no keepAliveMinutes - applying "
                f"afterKeepAlive={effective_action!r} now instead of leaving the VM "
                "running with no expiry (Hard Rule 11)."
            )
        try:
            # issue #321: a 2xx HTTP status only means the Compute API ACCEPTED the
            # operation, not that stop/delete actually completed — apply_lifecycle()
            # now returns the operation's own response body (or raises if that body
            # itself already reports an error) instead of discarding it.
            operation = apply_lifecycle(effective_action)
            lifecycle_applied = effective_action
            op_id = (operation.get("name") or operation.get("id")) if operation else None
            if op_id:
                status.notice(f"lifecycle {effective_action}: operation {op_id} accepted")
        except LifecycleError as exc:
            # best-effort; instanceTerminationAction=DELETE and startup.sh's own
            # exit-code-driven cleanup dispatch are both independent backstops for
            # exactly this case (see #320's closing report for why that softens the
            # consequence) — but a failure here is now AT LEAST visible, not silently
            # swallowed the way `contextlib.suppress(LifecycleError)` used to leave it.
            status.notice(f"lifecycle apply failed: {exc}", level="error")

    return RunOutcome(result=result, lifecycle_applied=lifecycle_applied)
