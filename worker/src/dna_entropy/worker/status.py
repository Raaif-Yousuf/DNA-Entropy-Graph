"""``status.json`` / ``progress.jsonl``: the worker's heartbeat (docs/job_contract.md §4-5).

``status.json`` is the current snapshot, overwritten atomically. ``progress.jsonl`` is the
history: object storage has no append, so the worker keeps the whole (capped) file in
memory and re-uploads it whole on every tick.
"""

from __future__ import annotations

import json
import sys
import tempfile
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from .blobstore import Blobstore, BlobstoreError, write_json

STATUS_PATH = "status.json"
PROGRESS_PATH = "progress.jsonl"

SCHEMA = 1
# Comfortably under the spec's 1 MB progress.jsonl cap; older lines roll off (the contract
# calls the rolled-off destination progress.1.jsonl — not implemented here since nothing
# yet reads it, and the design's own retention rules for it are unspecified; the priority
# was never losing the RECENT tail that death-detection and the UI actually read).
_MAX_PROGRESS_LINES = 2000

# Comfortably under BOTH the 30s status-heartbeat deadline and the 5-10s progress.jsonl
# re-upload cadence (job_contract.md §4-5) — one shared tick satisfies both instead of two
# independent timers.
DEFAULT_INTERVAL_SECONDS = 10.0

# issue #341, DECISION (agent-made, reversible): after this many CONSECUTIVE store-write
# failures, an outage is treated as persistent rather than a blip and escalated (see
# StatusWriter._escalate_persistent_outage). Comfortably under the app's own "dead if not
# RUNNING" 180s death-detection window (job_contract.md §5) at the fastest heartbeat
# interval this build ever uses (DEFAULT_INTERVAL_SECONDS=10s -> 50s to reach this
# threshold), so an operator watching stderr/the serial console sees the escalation well
# before the app gives up on the job — not because the worker can prove the outage will
# never end (it cannot: a store outage and a bucket permanently gone look identical from
# here), but because "possibly recoverable" and "silent forever" must not read the same.
PERSISTENT_OUTAGE_THRESHOLD = 5

# issue #341: a local-disk breadcrumb, independent of the configured (possibly broken)
# Blobstore, for the two documents whose loss is most expensive: the last known status
# snapshot, and — the sharp end — a finished run's own result.json if the store cannot
# take the final write at all. NOT a replacement for the real store, and not guaranteed to
# survive: a VM that reaches instanceTerminationAction=DELETE (Hard Rule 10) loses its
# whole disk, fallback included. This only helps a VM that is merely STOPPED (the app's
# own death-detection default) or one a human inspects via a disk snapshot before deletion
# — a real gap this session cannot close (no SSH anywhere in this design, cloud_design.md
# §9), only shrink. THEORY (unverified): this has never been checked against a real VM's
# actual disk layout or lifecycle; see docs/ToTest.md.
LOCAL_FALLBACK_DIR = Path(tempfile.gettempdir()) / "dna-entropy-fallback"


def write_local_fallback(job_id: str, kind: str, data: dict) -> Path:
    """Best-effort local-disk copy of a status/result document. ``kind`` is ``"status"``
    or ``"result"`` (names the file only). Raises :class:`OSError` on failure — callers
    decide whether that is fatal; both call sites in this package treat it as best-effort
    and swallow it, the same discipline as every other store-outage path here."""
    LOCAL_FALLBACK_DIR.mkdir(parents=True, exist_ok=True)
    path = LOCAL_FALLBACK_DIR / f"{job_id}-{kind}.json"
    path.write_text(json.dumps(data, indent=2), encoding="utf-8", newline="\n")
    return path


def utc_now_iso() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


@dataclass
class GpuInfo:
    name: str | None = None
    zone: str | None = None
    driver: str | None = None


@dataclass
class VmRef:
    """``status.json``'s ``vm`` field."""

    name: str | None = None
    zone: str | None = None
    gpu: str | None = None
    driver: str | None = None


@dataclass
class ErrorInfo:
    """``status.json``/``result.json``'s ``error`` field, when non-null
    (docs/job_contract.md §4)."""

    code: str
    message: str
    retriable: bool = False
    detail: str | None = None
    remediation: str | None = None


@dataclass
class WorkerInfo:
    version: str = "0.0.0"
    image: str = ""


@dataclass
class StatusDocument:
    """The documented shape of ``status.json`` (docs/job_contract.md §4), kept here purely
    as the schema-generation source of truth for ``worker/schema_gen.py`` — NOT the type
    :class:`StatusWriter` mutates internally (it keeps a plain dict for cheap, lock-held
    in-place updates). ``test_status_document_fields_match_statuswriter_keys`` in
    ``test_worker_status.py`` is the regression guard tying the two together: if a field is
    added/renamed on one side without the other, that test fails.
    """

    schema: int
    jobId: str
    stage: str
    percent: float
    detail: dict
    startedAt: str
    updatedAt: str
    heartbeatSeq: int
    vm: VmRef
    worker: WorkerInfo
    error: ErrorInfo | None = None


class StatusWriter:
    """Writes ``status.json`` (heartbeat) and ``progress.jsonl`` (history) for one job.

    Ticks on a background thread every ``interval_seconds`` **regardless of what the main
    thread is doing**, so a slow window (real GPU compute) never causes a heartbeat gap —
    "a heartbeat that stops when the work is slow" is exactly the failure mode this class
    exists to avoid (job_contract.md §5: the app calls a job dead after 180-600s of
    silence). :meth:`update`/:meth:`notice` ALSO write immediately on every call, so a
    stage transition or a notice is never delayed by up to a full tick either.

    **A store write here never raises** (issues #318/#319): every actual write goes
    through :meth:`_safe_write`, which catches :class:`~.blobstore.BlobstoreError` and
    counts it rather than propagating. Two distinct failure modes this closes: (1) an
    unhandled exception in a background thread's target silently kills that thread —
    without this, one transient GCS blip (401/429/500/timeout) would permanently stop the
    heartbeat for the rest of the job, and the app's own 180-second death-detection rule
    would give up on a job that is running fine; (2) :meth:`update`/:meth:`notice` are
    called SYNCHRONOUSLY from inside ``pipeline.run()``'s cooperative-cancellation hooks
    (``runner.py``'s ``_on_contig``), so a write failure there used to be indistinguishable
    from a real pipeline failure to the catch-all ``except Exception`` around it —
    discarding correctly computed work because telling someone about it didn't work.
    :attr:`consecutive_write_failures` is exposed for a caller that wants to notice a
    *persistent* outage rather than a blip. **issue #341, DECISION (agent-made,
    reversible):** :meth:`_safe_write` itself is now that caller — crossing
    :data:`PERSISTENT_OUTAGE_THRESHOLD` escalates on stderr and writes a local-disk
    fallback snapshot (:func:`write_local_fallback`) rather than retrying silently
    forever. The run is never aborted from in here: this class has no way to distinguish
    "the bucket is gone forever" from "a three-minute network blip", and guessing wrong
    toward abort would throw away already-completed, expensive GPU compute for a job that
    might otherwise finish and simply need its result reported another way.
    """

    def __init__(
        self,
        store: Blobstore,
        job_id: str,
        *,
        interval_seconds: float = DEFAULT_INTERVAL_SECONDS,
        worker: WorkerInfo | None = None,
        vm_name: str | None = None,
    ) -> None:
        self._store = store
        self._job_id = job_id
        self._interval = interval_seconds
        self._lock = threading.Lock()
        self._heartbeat_seq = 0
        now = utc_now_iso()
        self._state: dict = {
            "schema": SCHEMA,
            "jobId": job_id,
            "stage": "queued",
            "percent": 0.0,
            "detail": {},
            "startedAt": now,
            "updatedAt": now,
            "heartbeatSeq": 0,
            "vm": {"name": vm_name, "zone": None, "gpu": None, "driver": None},
            "worker": {
                "version": worker.version if worker else "0.0.0",
                "image": worker.image if worker else "",
            },
            "error": None,
        }
        self._progress: list[dict] = []
        self._progress_seq = 0
        self._stop_event = threading.Event()
        self._thread: threading.Thread | None = None
        self._consecutive_write_failures = 0

    @property
    def consecutive_write_failures(self) -> int:
        """How many store writes in a row have failed just now (0 once one succeeds)."""
        with self._lock:
            return self._consecutive_write_failures

    def set_vm(self, gpu: GpuInfo, *, name: str | None = None) -> None:
        """Report the VM/GPU identity — job_contract.md's own callout: "its first line
        reports the GPU and the free disk" (free disk goes through :meth:`notice`, since
        status.json has no dedicated field for it)."""
        with self._lock:
            self._state["vm"] = {
                "name": name if name is not None else self._state["vm"].get("name"),
                "zone": gpu.zone,
                "gpu": gpu.name,
                "driver": gpu.driver,
            }
        self._write_status()

    def start(self) -> None:
        """Write an initial snapshot immediately, then start the background heartbeat."""
        self._write_status()
        self._write_progress()
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def _loop(self) -> None:
        while not self._stop_event.wait(self._interval):
            self._write_status()
            self._write_progress()

    def stop(self) -> None:
        """Stop the background thread and write one final snapshot."""
        self._stop_event.set()
        if self._thread is not None:
            self._thread.join(timeout=self._interval + 5)
            self._thread = None
        self._write_status()
        self._write_progress()

    def __enter__(self) -> StatusWriter:
        self.start()
        return self

    def __exit__(self, *exc) -> bool:
        self.stop()
        return False

    def update(
        self,
        *,
        stage: str | None = None,
        percent: float | None = None,
        detail: dict | None = None,
        error: dict | None = None,
    ) -> None:
        """Update the in-memory snapshot and write it out immediately."""
        with self._lock:
            if stage is not None:
                self._state["stage"] = stage
            if percent is not None:
                self._state["percent"] = float(percent)
            if detail is not None:
                self._state["detail"] = detail
            if error is not None:
                self._state["error"] = error
        self._write_status()

    def notice(self, message: str, *, level: str = "notice", data: dict | None = None) -> None:
        """Append one ``progress.jsonl`` line and flush immediately.

        ``level: "notice"`` is the worker's equivalent of the prototype's yellow console
        lines — non-fatal, user-visible observations (a reduced-context window, a retried
        OOM, a kept ambiguity code) that do not change ``stage``.
        """
        with self._lock:
            self._progress_seq += 1
            entry = {
                "seq": self._progress_seq,
                "ts": utc_now_iso(),
                "stage": self._state["stage"],
                "level": level,
                "percent": self._state["percent"],
                "message": message,
                "data": data or {},
            }
            self._progress.append(entry)
            if len(self._progress) > _MAX_PROGRESS_LINES:
                self._progress = self._progress[-_MAX_PROGRESS_LINES:]
        self._write_progress()

    @property
    def heartbeat_count(self) -> int:
        """Number of status.json writes so far (tests use this to prove the background
        thread actually ticked, not just that start()/stop() wrote once each)."""
        with self._lock:
            return self._heartbeat_seq

    def _write_status(self) -> None:
        with self._lock:
            self._heartbeat_seq += 1
            self._state["heartbeatSeq"] = self._heartbeat_seq
            self._state["updatedAt"] = utc_now_iso()
            snapshot = json.loads(json.dumps(self._state))  # deep copy under the lock
        self._safe_write(lambda: write_json(self._store, STATUS_PATH, snapshot), what="status.json")

    def _write_progress(self) -> None:
        with self._lock:
            lines = [json.dumps(p) for p in self._progress]
        text = "\n".join(lines) + ("\n" if lines else "")
        self._safe_write(lambda: self._store.write_text(PROGRESS_PATH, text), what="progress.jsonl")

    def _safe_write(self, write: Callable[[], None], *, what: str) -> None:
        """Run one store write; never let it propagate (issues #318/#319 — see the class
        docstring). Only :class:`~.blobstore.BlobstoreError` is swallowed — a store's own
        transport/auth/HTTP failures are ALWAYS wrapped in that (both
        :class:`~.blobstore.LocalBlobstore` and :class:`~.blobstore.GcsBlobstore` do this
        uniformly), so a genuine bug elsewhere (e.g. a non-JSON-serializable value
        accidentally placed in ``detail``) still surfaces loudly instead of being eaten
        forever by an overly broad catch.
        """
        try:
            write()
        except BlobstoreError as exc:
            with self._lock:
                self._consecutive_write_failures += 1
                n = self._consecutive_write_failures
            # progress.jsonl/status.json may themselves be unreachable right now, so this
            # can't rely on either — stderr is the one channel not gated on the store
            # being up. Never includes anything from `snapshot`/`text` (no sequence
            # content, no file names — same discipline as issue #253's log redaction).
            print(f"status writer: {what} write failed ({n} consecutive): {exc}", file=sys.stderr)
            # issue #341: escalate exactly once per outage episode (not every tick past
            # the threshold, which would just be the same blip line repeated forever).
            if n == PERSISTENT_OUTAGE_THRESHOLD:
                self._escalate_persistent_outage()
        else:
            with self._lock:
                self._consecutive_write_failures = 0

    def _escalate_persistent_outage(self) -> None:
        """issue #341, DECISION: crossing :data:`PERSISTENT_OUTAGE_THRESHOLD` prints an
        ESCALATED line (distinguishable on stderr/serial console from the per-blip line
        above) and best-effort writes the current status snapshot to local disk — see
        :data:`LOCAL_FALLBACK_DIR`'s own docstring for exactly what this can and cannot
        guarantee. Never raises: a failure writing the FALLBACK must not become a new,
        different way for the worker to crash.
        """
        with self._lock:
            n = self._consecutive_write_failures
            snapshot = json.loads(json.dumps(self._state))
        print(
            f"status writer: ESCALATED - {n} consecutive store writes have failed for "
            f"job {self._job_id}; this looks like a persistent outage, not a blip. "
            "Writing a local fallback status snapshot.",
            file=sys.stderr,
        )
        try:
            write_local_fallback(self._job_id, "status", snapshot)
        except OSError as exc:
            print(f"status writer: local fallback write also failed: {exc}", file=sys.stderr)
