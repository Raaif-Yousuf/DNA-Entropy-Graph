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
from dataclasses import dataclass, field
from pathlib import Path

from .. import __version__ as WORKER_VERSION
from .. import pipeline
from ..predictors.base import PredictorError
from ..validation.validators import ValidationError
from .blobstore import Blobstore, BlobstoreError, write_json
from .cancel import CancelWatcher, JobCancelledError
from .lifecycle import LifecycleError, apply_lifecycle
from .manifest import InputSpec, JobManifest, ManifestError
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


@dataclass
class InputResult:
    """One ``result.json`` ``inputs[]`` entry."""

    id: str
    status: str  # "done" | "failed" | "cancelled"
    outputs: list[str] = field(default_factory=list)
    error: dict | None = None


@dataclass
class JobResult:
    """The parsed shape of ``result.json`` (docs/job_contract.md §7)."""

    schema: int
    job_id: str
    status: str  # "done" | "failed" | "cancelled" (job level; see per-input status too)
    inputs: list[InputResult]
    started_at: str
    finished_at: str
    error: dict | None = None

    def to_dict(self) -> dict:
        return {
            "schema": self.schema,
            "jobId": self.job_id,
            "status": self.status,
            "inputs": [dataclasses.asdict(i) for i in self.inputs],
            "timing": {"startedAt": self.started_at, "finishedAt": self.finished_at},
            "gpu": {"name": None, "zone": None, "spot": False},  # filled in on a real GPU VM
            "error": self.error,
        }


def _run_one_input(
    store: Blobstore, manifest: JobManifest, input_spec: InputSpec, tmp: Path,
    status: StatusWriter, cancel: CancelWatcher,
) -> InputResult:
    """Stage, run, and upload results for one manifest input. Raises
    :class:`~.cancel.JobCancelledError` if cancellation is seen (job-level, not per-input);
    any other failure is caught and reported as a "failed" :class:`InputResult` so a batch
    with one bad input still finishes the rest (job_contract.md §7)."""
    status.update(stage="running", detail={"input": input_spec.id})
    cancel.check_or_raise()

    def _on_window() -> None:
        cancel.check_or_raise()

    def _on_contig(contig) -> None:
        cancel.check_or_raise()
        status.update(detail={"input": input_spec.id, "contig": contig.name})

    # Staging the input, building its RunConfig, AND running the pipeline are all inside
    # this one try/except: a missing/unreadable input file must fail only THIS input, the
    # same as a bad sequence inside it — not crash the whole job (job_contract.md §7,
    # regression-tested by test_missing_input_file_fails_that_input_not_the_whole_job).
    try:
        local_input = tmp / "input" / Path(input_spec.path).name
        store.download_file(input_spec.path, local_input)

        local_out = tmp / "output" / input_spec.name
        cfg = manifest.build_run_config(
            input_spec, local_input_path=str(local_input), local_out_dir=str(local_out),
        )
        result = pipeline.run(cfg, on_window=_on_window, on_contig=_on_contig)
    except JobCancelledError:
        raise  # job-level: stop the whole job, not just this input
    except (ValidationError, PredictorError, ManifestError, BlobstoreError) as exc:
        return InputResult(
            id=input_spec.id, status="failed",
            error={"code": "INPUT_INVALID", "message": str(exc), "retriable": False},
        )
    except Exception as exc:  # anything else is an unexpected worker-side crash
        status.notice(f"{input_spec.id}: worker crashed: {exc}", level="error")
        return InputResult(
            id=input_spec.id, status="failed",
            error={
                "code": "WORKER_CRASH", "message": str(exc), "retriable": False,
                "detail": traceback.format_exc(),
            },
        )

    uploaded: list[str] = []
    for local_path in result.outputs:
        dest = f"output/{input_spec.name}/{Path(local_path).name}"
        store.upload_file(Path(local_path), dest)
        uploaded.append(dest)
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
        store, manifest.job_id,
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

            for input_spec in manifest.inputs:
                input_results.append(_run_one_input(store, manifest, input_spec, tmp, status, cancel))

    except JobCancelledError:
        job_status = "cancelled"
        status.notice("job cancelled (control/cancel seen)", level="notice")
    except Exception as exc:  # a whole-job-level crash outside any single input's handling
        job_status = "failed"
        job_error = {"code": "WORKER_CRASH", "message": str(exc), "retriable": False}
        status.notice(f"job-level crash: {exc}", level="error")

    finished_at = _utc_now_iso()
    status.update(
        stage=job_status, percent=100.0,
        error=job_error, detail={"inputs": [dataclasses.asdict(r) for r in input_results]},
    )

    result = JobResult(
        schema=1, job_id=manifest.job_id, status=job_status, inputs=input_results,
        started_at=started_at, finished_at=finished_at, error=job_error,
    )
    # Written LAST, after every output is confirmed uploaded — job_contract.md §7: its
    # mere presence, not just its contents, is the app's "this job reached a terminal
    # state" signal, checked before status.json's heartbeat on every app launch.
    write_json(store, RESULT_PATH, result.to_dict())

    status.stop()

    # Lifecycle only applies to a real cloud VM; a local run has no VM to stop/delete, and
    # `keep` is a genuine no-op even in the cloud case. This is the module the overnight
    # brief says to implement but never call for real — a local/localdir job never reaches
    # the `apply_lifecycle` call at all, so no test in this repo exercises it against a
    # real network no matter how this function is invoked.
    if manifest.store.kind == "gcs" and manifest.lifecycle.after_task != "keep":
        try:
            apply_lifecycle(manifest.lifecycle.after_task)
        except LifecycleError:
            pass  # best-effort; instanceTerminationAction=DELETE is the backstop

    return result
