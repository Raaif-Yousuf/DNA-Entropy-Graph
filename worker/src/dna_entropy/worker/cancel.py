"""``control/cancel``: cooperative cancellation (docs/job_contract.md §6).

The app requests cancellation by writing a 0-byte object at ``control/cancel``; presence
is the entire signal, there is no payload. The worker checks for it **between windows and
between contigs — never mid-window**, since a window is one model forward pass and cannot
be interrupted partway.
"""

from __future__ import annotations

import time
from collections.abc import Callable

from .blobstore import Blobstore

CANCEL_PATH = "control/cancel"

# manifest.limits.cancelPollSeconds' own default (job_contract.md §3) — used here too so a
# CancelWatcher built with no explicit interval (every call site before issue #338) still
# throttles to a sane cadence rather than reverting to "check on literally every call".
DEFAULT_POLL_INTERVAL_SECONDS = 10.0


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
    would be surprising and unsafe).

    **Found auditing #304/#306 (issue #338):** ``check_or_raise`` is called once per
    completed window AND once per completed contig (``pipeline.run()``, via ``runner.py``'s
    ``on_window``/``on_contig`` hooks) — before this fix, every one of those calls did a
    REAL store round-trip (``store.exists()``) with no throttling at all, even though
    ``manifest.limits.cancelPollSeconds`` (job_contract.md §3, default 10s) explicitly
    declares the intended cadence. A long batch tiled into many small windows could mean
    tens of thousands of ``exists()`` calls purely for cancellation checks. ``poll()`` now
    does a real round-trip at most once per ``poll_interval_seconds`` of wall-clock time
    (via ``time_source``, injectable for deterministic tests), returning the last-known
    result on calls in between — the SAME cooperative-cancellation checkpoint granularity
    (still called once per window/contig) but a throttled STORE query underneath it.
    """

    def __init__(
        self,
        store: Blobstore,
        *,
        poll_interval_seconds: float = DEFAULT_POLL_INTERVAL_SECONDS,
        time_source: Callable[[], float] = time.monotonic,
    ) -> None:
        self._store = store
        self._cancelled = False
        self._poll_interval = poll_interval_seconds
        self._time_source = time_source
        self._last_poll_time: float | None = None

    def poll(self) -> bool:
        """Return the current cancelled state. Does a real store round-trip only when not
        already sticky-true AND at least ``poll_interval_seconds`` has elapsed since the
        last real check (always true on the very first call)."""
        if not self._cancelled:
            now = self._time_source()
            due = self._last_poll_time is None or (now - self._last_poll_time) >= self._poll_interval
            if due:
                self._last_poll_time = now
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
