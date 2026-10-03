"""Smoothed entropy tracks (issue #126): a rolling mean over the entropy track.

Per-base entropy is noisy; a rolling mean over 11, 51 or 201 bases shows the landscape. It is
cheap, popular and easy to over-read, so the **raw track stays primary**: a smoothed track is
an extra file beside it, never a replacement. Pure numpy over the ``(L,)`` entropy track the
run already produced (Hard Rule 3).

Edge handling (the part users ask about):

- **Linear** molecule: the window **shrinks** at the ends. Base ``i`` averages
  ``[i - h, i + h]`` clipped to the sequence, ``h = (W - 1) // 2``, so the first base of a
  window-51 track averages 26 bases, not 51, and the very ends are noisier than the middle.
  Nothing is padded and nothing is wrapped.
- **Circular** molecule (issue #128): the window **wraps** around the origin, so every base has
  a full window. A window longer than one turn is clamped to one turn (``L`` for odd ``L``,
  ``L - 1`` for even ``L``) so no base is counted twice.

``W`` must be odd (a centred window), ``W == 1`` is the raw track unchanged (bit for bit), and
a track never leaves the range of the values it averages.
"""

from __future__ import annotations

from collections.abc import Iterable

import numpy as np

DEFAULT_SMOOTHING_WINDOWS: tuple[int, ...] = (51,)
MAX_WINDOW = 100_001
MAX_WINDOWS = 8  # one file per window per contig set; 8 is far more than anyone reads


def validate_smoothing_windows(windows: Iterable[object]) -> tuple[int, ...]:
    """Return ``windows`` sorted and de-duplicated; raise :class:`ValueError` naming the
    problem. Each must be an odd whole number from 1 to :data:`MAX_WINDOW`, at most
    :data:`MAX_WINDOWS` of them. An empty list is valid (no smoothed tracks)."""
    seen: set[int] = set()
    for w in windows:
        if not isinstance(w, int) or isinstance(w, bool):
            raise ValueError(f"smoothing window {w!r} must be a whole number of bases (for example 51).")
        if w < 1 or w > MAX_WINDOW:
            raise ValueError(f"smoothing window {w} must be between 1 and {MAX_WINDOW} bases.")
        if w % 2 == 0:
            raise ValueError(
                f"smoothing window {w} must be odd so it is centred on a base; use {w - 1} or {w + 1}."
            )
        seen.add(w)
    if len(seen) > MAX_WINDOWS:
        raise ValueError(f"smoothing windows: at most {MAX_WINDOWS} different windows are allowed.")
    return tuple(sorted(seen))


def rolling_mean(values: np.ndarray, window: int, *, circular: bool) -> np.ndarray:
    """The centred rolling mean of ``values`` over ``window`` (odd) bases, ``float32``, same
    length. See the module docstring for the edge rules."""
    length = len(values)
    if length == 0:
        return np.zeros(0, dtype=np.float32)
    if window == 1:
        return values.astype(np.float32, copy=True)  # the mean of one base is that base, exactly
    half = (window - 1) // 2
    work = values.astype(np.float64)
    if circular:
        half = min(half, (length - 1) // 2)
        extended = np.concatenate((work[length - half :], work, work[:half])) if half else work
        csum = np.concatenate(([0.0], np.cumsum(extended)))
        out = (csum[2 * half + 1 : 2 * half + 1 + length] - csum[:length]) / (2 * half + 1)
    else:
        csum = np.concatenate(([0.0], np.cumsum(work)))
        index = np.arange(length)
        lo = np.maximum(index - half, 0)
        hi = np.minimum(index + half + 1, length)
        out = (csum[hi] - csum[lo]) / (hi - lo)
    # A mean is bounded by what it averages; cumulative-sum rounding must not push a zero-entropy
    # stretch to -1e-17 (the entropy track's [0, 2] contract) or a 2.0 stretch past 2.0.
    return np.clip(out, work.min(), work.max()).astype(np.float32)
