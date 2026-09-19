"""Tests for worker/status.py: status.json heartbeat + progress.jsonl history."""

from __future__ import annotations

import json
import time
from pathlib import Path

from dna_entropy.worker.blobstore import BlobstoreError, LocalBlobstore
from dna_entropy.worker.status import GpuInfo, StatusWriter, WorkerInfo


def _status(store: LocalBlobstore) -> dict:
    return json.loads(store.read_text("status.json"))


def _progress_lines(store: LocalBlobstore) -> list[dict]:
    text = store.read_text("progress.jsonl")
    return [json.loads(line) for line in text.splitlines() if line.strip()]


class _FlakyStore:
    """Wraps a real blobstore, making ``fail_method`` raise :class:`BlobstoreError` the
    first ``fail_times`` calls, then delegating normally — simulates the transient GCS
    blip (401/429/500/timeout) issues #318/#319/#320 are about, without any real
    network."""

    def __init__(self, inner, *, fail_method: str, fail_times: int) -> None:
        self._inner = inner
        self._fail_method = fail_method
        self._fail_times = fail_times
        self.call_count = 0

    def __getattr__(self, name: str):
        attr = getattr(self._inner, name)
        if name != self._fail_method:
            return attr

        def _flaky(*args, **kwargs):
            self.call_count += 1
            if self.call_count <= self._fail_times:
                raise BlobstoreError(f"simulated transient failure #{self.call_count}")
            return attr(*args, **kwargs)

        return _flaky


def test_start_writes_an_initial_snapshot_synchronously(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)  # never ticks during the test
    w.start()
    try:
        status = _status(store)
        assert status["schema"] == 1
        assert status["jobId"] == "job1"
        assert status["stage"] == "queued"
        assert status["heartbeatSeq"] == 1
    finally:
        w.stop()


def test_update_changes_stage_and_writes_immediately(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)
    w.start()
    try:
        w.update(stage="running", percent=42.5, detail={"contig": "SetTnpB_1"})
        status = _status(store)
        assert status["stage"] == "running"
        assert status["percent"] == 42.5
        assert status["detail"] == {"contig": "SetTnpB_1"}
    finally:
        w.stop()


def test_error_field_defaults_to_none_and_can_be_set(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)
    w.start()
    try:
        assert _status(store)["error"] is None
        w.update(error={"code": "MODEL_OOM", "message": "out of memory", "retriable": True})
        assert _status(store)["error"]["code"] == "MODEL_OOM"
    finally:
        w.stop()


def test_heartbeat_seq_increases_monotonically(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)
    w.start()
    try:
        first = _status(store)["heartbeatSeq"]
        w.update(percent=1.0)
        second = _status(store)["heartbeatSeq"]
        assert second > first
    finally:
        w.stop()


def test_background_thread_ticks_without_any_explicit_update(tmp_path: Path) -> None:
    """The failure mode this class exists to prevent: a heartbeat that only advances when
    the main thread calls update()/notice(). A short interval + a real (short) sleep
    proves the background thread itself writes on its own schedule."""
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=0.05)
    w.start()
    try:
        deadline = time.monotonic() + 2.0
        while w.heartbeat_count < 3 and time.monotonic() < deadline:
            time.sleep(0.02)
        assert w.heartbeat_count >= 3
    finally:
        w.stop()


def test_stop_writes_a_final_snapshot(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)
    w.start()
    before = w.heartbeat_count
    w.stop()
    assert w.heartbeat_count > before  # stop() itself wrote one more


def test_set_vm_records_gpu_and_name(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)
    w.start()
    try:
        w.set_vm(GpuInfo(name="NVIDIA L4", zone="us-central1-a", driver="580.x"), name="deg-job1")
        vm = _status(store)["vm"]
        assert vm == {"name": "deg-job1", "zone": "us-central1-a", "gpu": "NVIDIA L4", "driver": "580.x"}
    finally:
        w.stop()


def test_worker_info_is_recorded(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(
        store, "job1", interval_seconds=999, worker=WorkerInfo(version="1.2.3", image="sha256:abc")
    )
    w.start()
    try:
        status = _status(store)
        assert status["worker"] == {"version": "1.2.3", "image": "sha256:abc"}
    finally:
        w.stop()


def test_notice_appends_a_progress_line_and_flushes_immediately(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)
    w.start()
    try:
        w.update(stage="running", percent=10.0)
        w.notice("Window 5: reduced context (L < 2K) on this input", data={"input": "in1"})
        lines = _progress_lines(store)
        assert len(lines) == 1
        assert lines[0]["message"] == "Window 5: reduced context (L < 2K) on this input"
        assert lines[0]["level"] == "notice"
        assert lines[0]["stage"] == "running"
        assert lines[0]["data"] == {"input": "in1"}
    finally:
        w.stop()


def test_notice_sequence_numbers_increase(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)
    w.start()
    try:
        w.notice("first")
        w.notice("second")
        lines = _progress_lines(store)
        assert [line["seq"] for line in lines] == [1, 2]
    finally:
        w.stop()


def test_progress_is_capped_and_keeps_the_recent_tail(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)
    w.start()
    try:
        # Reach into the cap constant via the module rather than hard-coding it twice.
        from dna_entropy.worker.status import _MAX_PROGRESS_LINES

        for i in range(_MAX_PROGRESS_LINES + 10):
            w.notice(f"line {i}")
        lines = _progress_lines(store)
        assert len(lines) == _MAX_PROGRESS_LINES
        assert lines[-1]["message"] == f"line {_MAX_PROGRESS_LINES + 9}"  # most recent kept
    finally:
        w.stop()


def test_context_manager_starts_and_stops(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    with StatusWriter(store, "job1", interval_seconds=999) as w:
        w.update(stage="running")
        assert _status(store)["stage"] == "running"
    # After the context exits, one more (final) write happened via stop().
    assert _status(store)["stage"] == "running"


# --- StatusDocument: the schema-generation source of truth must match what StatusWriter
# actually produces (worker/schema_gen.py, issue #39) ----------------------------------


def test_status_document_fields_match_statuswriter_keys(tmp_path: Path) -> None:
    import dataclasses

    from dna_entropy.worker.status import StatusDocument

    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)
    w.start()
    try:
        actual_keys = set(_status(store).keys())
    finally:
        w.stop()
    documented_fields = {f.name for f in dataclasses.fields(StatusDocument)}
    assert actual_keys == documented_fields


def test_status_document_vm_and_worker_subfields_match(tmp_path: Path) -> None:
    import dataclasses

    from dna_entropy.worker.status import VmRef, WorkerInfo

    store = LocalBlobstore(tmp_path)
    w = StatusWriter(store, "job1", interval_seconds=999)
    w.start()
    try:
        actual = _status(store)
    finally:
        w.stop()
    assert set(actual["vm"].keys()) == {f.name for f in dataclasses.fields(VmRef)}
    assert set(actual["worker"].keys()) == {f.name for f in dataclasses.fields(WorkerInfo)}


# --- resilience to a flaky store (issues #318/#319) ------------------------------------


def test_background_heartbeat_survives_a_transient_write_failure(tmp_path: Path) -> None:
    """issue #318: an unhandled exception in a Python thread's target kills that thread
    silently. Before the fix, ONE transient BlobstoreError from the background loop's
    own write permanently stopped the heartbeat for the rest of the job; the app's
    180-second death-detection rule then gives up on a job that is running fine."""
    real_store = LocalBlobstore(tmp_path)
    flaky = _FlakyStore(real_store, fail_method="write_text", fail_times=3)
    w = StatusWriter(flaky, "job1", interval_seconds=0.02)
    w.start()
    try:
        # Enough ticks to run past the 3 injected failures and land on real, successful
        # writes again -- proving the background thread is still alive afterward.
        deadline = time.monotonic() + 3.0
        while flaky.call_count < 6 and time.monotonic() < deadline:
            time.sleep(0.02)
        assert flaky.call_count >= 6, "the background thread stopped ticking after a failure"
    finally:
        w.stop()

    # A real status.json exists and is the LATEST state, not stuck on a pre-failure one.
    status = _status(real_store)
    assert status["stage"] in ("queued", "done", "failed", "cancelled")


def test_update_never_raises_on_a_transient_write_failure(tmp_path: Path) -> None:
    """issue #319: status.update()/notice() run synchronously on the CALLER's thread
    (e.g. runner.py's per-contig hook, inside pipeline.run()). Before the fix, a
    transient write failure here propagated straight out of update()/notice() and was
    indistinguishable, to a catch-all except Exception upstream, from a real pipeline
    failure -- discarding correctly computed work because telling someone about it
    didn't work."""
    real_store = LocalBlobstore(tmp_path)
    flaky = _FlakyStore(real_store, fail_method="write_text", fail_times=1)
    w = StatusWriter(flaky, "job1", interval_seconds=999)
    w.start()
    try:
        w.update(stage="running", percent=10.0)  # must not raise, despite the injected failure
        w.notice("a notice during the flaky window")  # must not raise either
    finally:
        w.stop()

    # And the writes are not silently lost forever -- once the store recovers, the real
    # state is there.
    status = _status(real_store)
    assert status["stage"] == "running"
