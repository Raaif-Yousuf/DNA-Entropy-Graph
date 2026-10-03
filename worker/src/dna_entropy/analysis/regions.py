"""Low- and high-entropy region caller (issue #125): "where are the conserved stretches" as
features instead of a graph to eyeball.

Pure numpy over the ``(L,)`` entropy track the run already produced (Hard Rule 3).

The rule, in order:

1. A base is a **hit** when its entropy is strictly below ``threshold`` (kind ``"low"``) or
   strictly above the mirrored ``MAX_ENTROPY_BITS - threshold`` (kind ``"high"``; the caller
   passes the already-mirrored value). Exactly on the threshold is not a hit.
2. Consecutive hits form a run. Runs separated by **at most** ``merge_gap`` non-hit bases are
   merged, and the bridged bases belong to the region.
3. A merged region shorter than ``min_length`` bases is dropped. (Merging comes first, so two
   short runs bridged into one long region survive.)

Coordinates are 0-based half-open ``[begin, end)``. On a **circular** molecule (issue #128)
the molecule has no ends: a run at the tail and a run at the head are one region when the
gap across the origin is within ``merge_gap``. Such a region has ``end > length`` (it wraps);
:meth:`Region.segments` splits it into its real, in-range pieces. A region that would cover
the whole molecule is reported as ``[0, length)`` and does not wrap.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

from .entropy import MAX_ENTROPY_BITS

DEFAULT_THRESHOLD_BITS = 0.5
DEFAULT_MIN_LENGTH = 20
DEFAULT_MERGE_GAP = 5


@dataclass(frozen=True)
class Region:
    """One called stretch. ``end`` exceeds the molecule length only for a region that wraps the
    origin of a circular molecule."""

    kind: str  # "low" | "high"
    begin: int  # 0-based
    end: int  # exclusive
    mean_entropy: float

    @property
    def length(self) -> int:
        return self.end - self.begin

    def segments(self, length: int) -> list[tuple[int, int]]:
        """The in-range ``[begin, end)`` pieces: one, or two for a region across the origin."""
        if self.end <= length:
            return [(self.begin, self.end)]
        return [(self.begin, length), (0, self.end - length)]


def validate_region_options(*, threshold: float, min_length: int, merge_gap: int) -> None:
    """Refuse options that cannot mean anything; raises :class:`ValueError` naming the option.

    ``threshold`` must be in ``(0, 1]`` bits: the high caller mirrors it (``2 - threshold``),
    and above 1 bit the low and high callers would overlap.
    """
    if not (isinstance(threshold, int | float) and math.isfinite(threshold) and 0.0 < threshold <= 1.0):
        raise ValueError(
            f"region threshold {threshold!r} must be a number above 0 and at most 1 (bits); "
            "the high-entropy caller uses 2 minus this. Choose a value such as 0.5."
        )
    if not isinstance(min_length, int) or isinstance(min_length, bool) or min_length < 1:
        raise ValueError(f"region minimum length {min_length!r} must be a whole number of at least 1 base.")
    if not isinstance(merge_gap, int) or isinstance(merge_gap, bool) or merge_gap < 0:
        raise ValueError(f"region merge gap {merge_gap!r} must be a whole number of 0 or more bases.")


def mirrored_threshold(threshold: float) -> float:
    """The high-entropy caller's threshold for a given low-entropy threshold."""
    return MAX_ENTROPY_BITS - threshold


def _runs(hit: np.ndarray) -> list[tuple[int, int]]:
    edges = np.diff(np.concatenate(([0], hit.astype(np.int8), [0])))
    return list(zip(np.flatnonzero(edges == 1).tolist(), np.flatnonzero(edges == -1).tolist(), strict=True))


def call_regions(
    values: np.ndarray,
    *,
    kind: str,
    threshold: float,
    min_length: int,
    merge_gap: int,
    circular: bool,
) -> list[Region]:
    """Call ``kind`` ("low" or "high") regions in ``values`` against ``threshold``.

    For ``kind="high"`` pass the already-mirrored threshold (:func:`mirrored_threshold`).
    Returns regions sorted by ``begin``.
    """
    if kind not in ("low", "high"):
        raise ValueError(f"unknown region kind {kind!r}")
    length = len(values)
    if length == 0:
        return []
    hit = values < threshold if kind == "low" else values > threshold
    merged: list[list[int]] = []
    for begin, end in _runs(hit):
        if merged and begin - merged[-1][1] <= merge_gap:
            merged[-1][1] = end
        else:
            merged.append([begin, end])
    if circular and merged:
        first, last = merged[0], merged[-1]
        if first[0] + (length - last[1]) <= merge_gap:
            # one run that reaches round the origin is the whole molecule; otherwise the last
            # and first runs join into one that wraps (end > length)
            whole = len(merged) == 1
            merged = [[0, length]] if whole else [*merged[1:-1], [last[0], first[1] + length]]
    regions = []
    for begin, end in sorted(merged):
        if end - begin < min_length:
            continue
        index = np.arange(begin, end) % length
        regions.append(
            Region(
                kind=kind, begin=begin, end=end, mean_entropy=float(values[index].astype(np.float64).mean())
            )
        )
    return regions
