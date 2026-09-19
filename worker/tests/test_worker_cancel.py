"""Tests for worker/cancel.py: control/cancel cooperative cancellation."""

from __future__ import annotations

from pathlib import Path

import pytest

from dna_entropy.worker.blobstore import LocalBlobstore
from dna_entropy.worker.cancel import CANCEL_PATH, CancelWatcher, JobCancelledError


def test_not_cancelled_when_no_control_object_exists(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    watcher = CancelWatcher(store)
    assert watcher.poll() is False
    assert watcher.is_cancelled is False


def test_cancelled_when_control_cancel_exists(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text(CANCEL_PATH, "")
    watcher = CancelWatcher(store)
    assert watcher.poll() is True
    assert watcher.is_cancelled is True


def test_check_or_raise_raises_only_when_cancelled(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    clock = {"t": 0.0}
    # poll_interval_seconds=10 (the manifest default) with an explicit fake clock advanced
    # past it below -- issue #338 throttles real store checks to this cadence, so a real
    # wall-clock test would need to actually sleep 10s to observe the second check; a fake
    # clock keeps this deterministic and fast.
    watcher = CancelWatcher(store, poll_interval_seconds=10.0, time_source=lambda: clock["t"])
    watcher.check_or_raise()  # must not raise yet (first call always checks)

    store.write_text(CANCEL_PATH, "")
    clock["t"] += 10.0  # advance past the poll interval so the next check is real
    with pytest.raises(JobCancelledError):
        watcher.check_or_raise()


def test_cancellation_is_sticky_even_if_the_object_is_removed(tmp_path: Path) -> None:
    store = LocalBlobstore(tmp_path)
    store.write_text(CANCEL_PATH, "")
    watcher = CancelWatcher(store)
    assert watcher.poll() is True

    store.delete(CANCEL_PATH)
    assert watcher.poll() is True  # sticky: does not un-cancel


def test_poll_after_sticky_does_not_re_touch_the_store(tmp_path: Path) -> None:
    calls = {"n": 0}

    class _CountingStore(LocalBlobstore):
        def exists(self, path: str) -> bool:  # noqa: D102
            calls["n"] += 1
            return super().exists(path)

    store = _CountingStore(tmp_path)
    store.write_text(CANCEL_PATH, "")
    watcher = CancelWatcher(store)
    assert watcher.poll() is True
    assert calls["n"] == 1
    watcher.poll()
    watcher.poll()
    assert calls["n"] == 1  # sticky after the first True — no further store round-trips


# --- throttling the store round-trip (issue #338) --------------------------------------


def test_poll_does_not_re_touch_the_store_within_one_interval_when_not_cancelled(
    tmp_path: Path,
) -> None:
    """The bug this closes: every check_or_raise() call (once per window, once per contig)
    used to do a real store round-trip with no throttling, even while nothing has been
    cancelled -- a long batch tiled into many windows meant a real store query per window."""
    calls = {"n": 0}

    class _CountingStore(LocalBlobstore):
        def exists(self, path: str) -> bool:  # noqa: D102
            calls["n"] += 1
            return super().exists(path)

    store = _CountingStore(tmp_path)
    clock = {"t": 0.0}
    watcher = CancelWatcher(store, poll_interval_seconds=10.0, time_source=lambda: clock["t"])

    # Many calls within the same 10s window -- only the FIRST does a real store round-trip.
    for _ in range(50):
        assert watcher.poll() is False
    assert calls["n"] == 1


def test_poll_checks_again_once_the_interval_elapses(tmp_path: Path) -> None:
    calls = {"n": 0}

    class _CountingStore(LocalBlobstore):
        def exists(self, path: str) -> bool:  # noqa: D102
            calls["n"] += 1
            return super().exists(path)

    store = _CountingStore(tmp_path)
    clock = {"t": 0.0}
    watcher = CancelWatcher(store, poll_interval_seconds=10.0, time_source=lambda: clock["t"])

    assert watcher.poll() is False
    assert calls["n"] == 1
    clock["t"] += 9.9  # not yet due
    assert watcher.poll() is False
    assert calls["n"] == 1
    clock["t"] += 0.2  # now past 10s total
    assert watcher.poll() is False
    assert calls["n"] == 2


def test_default_poll_interval_matches_the_manifest_default_of_ten_seconds(
    tmp_path: Path,
) -> None:
    from dna_entropy.worker.cancel import DEFAULT_POLL_INTERVAL_SECONDS

    assert DEFAULT_POLL_INTERVAL_SECONDS == 10.0


def test_a_cancellation_that_appears_during_the_throttle_window_is_still_caught_once_due(
    tmp_path: Path,
) -> None:
    store = LocalBlobstore(tmp_path)
    clock = {"t": 0.0}
    watcher = CancelWatcher(store, poll_interval_seconds=10.0, time_source=lambda: clock["t"])
    watcher.check_or_raise()  # establishes the first real check (not cancelled)

    store.write_text(CANCEL_PATH, "")  # app cancels mid-window
    clock["t"] += 5.0  # still within the throttle window
    watcher.check_or_raise()  # too soon -- must not raise yet (uses the cached result)

    clock["t"] += 5.1  # now past the interval
    with pytest.raises(JobCancelledError):
        watcher.check_or_raise()
