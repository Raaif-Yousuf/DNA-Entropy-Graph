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
    watcher = CancelWatcher(store)
    watcher.check_or_raise()  # must not raise yet

    store.write_text(CANCEL_PATH, "")
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
