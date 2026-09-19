"""``control/cancel``: cooperative cancellation (docs/job_contract.md §6).

The app requests cancellation by writing a 0-byte object at ``control/cancel``; presence
is the entire signal, there is no payload. The worker checks for it **between windows and
between contigs — never mid-window**, since a window is one model forward pass and cannot
be interrupted partway.
"""

from __future__ import annotations

from .blobstore import Blobstore

CANCEL_PATH = "control/cancel"


class JobCancelledError(RuntimeError):
    """Raised (not returned) when a cooperative cancellation point sees ``control/cancel``.

    An exception, not a sentinel return value, so a cancellation check can be dropped into
    any call site (``analysis/direction.py``'s per-window loop, the runner's per-contig
    loop) without every intermediate caller needing to thread a "should I stop" flag back
    up through its own return type.
    """


class CancelWatcher:
    """Polls ``control/cancel`` on demand; sticky once seen (job_contract.md never
    describes an "un-cancel", and treating a later-removed object as "actually fine now"
    would be surprising and unsafe)."""

    def __init__(self, store: Blobstore) -> None:
        self._store = store
        self._cancelled = False

    def poll(self) -> bool:
        """Check ``control/cancel`` now (a real store round-trip unless already sticky-
        true) and return the current cancelled state."""
        if not self._cancelled:
            self._cancelled = self._store.exists(CANCEL_PATH)
        return self._cancelled

    def check_or_raise(self) -> None:
        """Call at every cooperative cancellation point (between windows, between
        contigs). Raises :class:`JobCancelledError` the moment cancellation is seen."""
        if self.poll():
            raise JobCancelledError("control/cancel was requested")

    @property
    def is_cancelled(self) -> bool:
        return self._cancelled
