"""Compare two entropy tracks (issue #129): B minus A, by position or by an offset.

Reads the ``<name>.entropy.tsv`` a run writes (1-based positions, a ``# contig: <name>`` line per
record in a multi-record run). B index ``j`` is compared with A index ``j + offset``; with no
offset the two tracks must be the same length. Reported positions are A's coordinates (A's own
``start`` applied), so the difference track lines up under A in a viewer.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np

from ..readers.encoding import read_text
from ..validation.validators import ValidationError


class CompareError(ValidationError):
    """The two tracks cannot be compared. A ``ValidationError`` so callers report it like bad input."""


Track = tuple[int, np.ndarray]  # (start position, values)


def read_entropy_tsv(path: str) -> dict[str, Track]:
    """``{contig: (start, entropy values)}`` from a run's ``entropy.tsv``.

    A single-record file has no ``# contig:`` line, so its one block is named for the file
    (``a.entropy.tsv`` -> ``a``). The entropy column is ``entropy_bits``, or ``entropy_combined``
    for a "both, separate tracks" file."""
    text = read_text(path).replace("\r\n", "\n")
    stem = Path(path).name.split(".entropy")[0] or "track"
    blocks: dict[str, list[tuple[int, float]]] = {}
    current = stem
    column = -1
    for line in text.split("\n"):
        if not line.strip():
            continue
        if line.startswith("# contig:"):
            current = line.split(":", 1)[1].strip()
            continue
        if line.startswith("position\t"):
            header = line.split("\t")
            column = (
                header.index("entropy_combined")
                if "entropy_combined" in header
                else header.index("entropy_bits")
            )
            continue
        if line.startswith("#"):
            continue
        if column < 0:
            raise CompareError(
                "This is not a run's entropy.tsv (no header line). Pick the <name>.entropy.tsv file."
            )
        cols = line.split("\t")
        try:
            blocks.setdefault(current, []).append((int(cols[0]), float(cols[column])))
        except (ValueError, IndexError) as exc:
            raise CompareError(
                "A line of the entropy.tsv could not be read. Pick the unedited <name>.entropy.tsv file."
            ) from exc
    if not blocks:
        raise CompareError("The entropy.tsv has no rows. Pick the <name>.entropy.tsv of a finished run.")
    return {k: (v[0][0], np.array([x for _, x in v], dtype=np.float32)) for k, v in blocks.items()}


def difference(a: Track, b: Track, offset: int = 0) -> tuple[np.ndarray, np.ndarray]:
    """``(positions, b - a)`` over the overlap, B index ``j`` against A index ``j + offset``.

    Positions are A's coordinates. Without an offset the lengths must match."""
    (a_start, av), (_, bv) = a, b
    if offset == 0 and av.shape[0] != bv.shape[0]:
        raise CompareError(
            f"The two tracks are {av.shape[0]} and {bv.shape[0]} positions long. "
            "Give --offset N to say where the second one starts on the first, or compare same-length runs."
        )
    lo = max(0, -offset)  # first B index that lands inside A
    hi = min(bv.shape[0], av.shape[0] - offset)  # one past the last
    if hi <= lo:
        raise CompareError(
            f"With --offset {offset} the two tracks do not overlap. Use an offset between "
            f"{-(bv.shape[0] - 1)} and {av.shape[0] - 1}."
        )
    b_part = bv[lo:hi]
    a_part = av[lo + offset : hi + offset]
    positions = np.arange(lo + offset, hi + offset) + a_start
    return positions, (b_part - a_part).astype(np.float32)


def largest_deviations(positions: np.ndarray, diff: np.ndarray, top: int = 10) -> list[tuple[int, float]]:
    """The ``top`` positions with the largest ``|difference|``, ranked, then in position order on ties."""
    if top <= 0 or diff.size == 0:
        return []
    order = np.lexsort((positions, -np.abs(diff)))[:top]
    return [(int(positions[i]), float(diff[i])) for i in order]
