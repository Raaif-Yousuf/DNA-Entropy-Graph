"""``status.json`` / ``progress.jsonl``: the worker's heartbeat (docs/job_contract.md §4-5).

``status.json`` is the current snapshot, overwritten atomically. ``progress.jsonl`` is the
history: object storage has no append, so the worker keeps the whole (capped) file in
memory and re-uploads it whole on every tick.
"""

from __future__ import annotations

import json
import sys
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass

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
    *persistent* outage rather than a blip (nothing in this repo reads it yet).
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
        else:
            with self._lock:
                self._consecutive_write_failures = 0
