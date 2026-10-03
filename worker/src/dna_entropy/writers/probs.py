"""Probability matrix writer (issue #127): ``<name>.probs.tsv.gz`` and ``<name>.probs.npy``.

Downstream users (and a future ANN) want the raw probabilities, not just the entropy derived
from them. This serialises the ``(L, 4)`` matrix the contract guard already validated
(Hard Rule 3: it is never recomputed here) and is off by default because it is large.

``.probs.tsv.gz``: gzip-compressed TSV, UTF-8 with LF (Hard Rule 5), columns ``position``
(1-based, offset by the run's start like ``entropy.tsv``), ``base`` (the actual base there),
``pA, pC, pG, pT`` (the A/C/G/T column order is fixed across the package). Each probability is
written with 9 significant digits, which round-trips a ``float32`` exactly, so entropy
recomputed from the file equals the entropy track to far better than 1e-6. A multi-contig file
introduces each contig with a ``# contig: <name>`` line and restarts numbering, exactly like
``entropy.tsv``. The gzip stream carries no timestamp and no file name, so the same matrix is
the same bytes (and the same ``result.json`` hash) on every run.

``.probs.npy``: the ``float32`` matrix as a NumPy ``.npy``; several contigs are concatenated in
file order (the contig lengths are in ``provenance.json``).
"""

from __future__ import annotations

import gzip
import io
from collections.abc import Sequence
from pathlib import Path

import numpy as np

PROBS_COLUMNS: tuple[str, ...] = ("position", "base", "pA", "pC", "pG", "pT")

_CHUNK = 50_000


def _rows(seq: str, probs: np.ndarray, start: int, first: int, last: int) -> str:
    return "".join(
        f"{start + i}\t{seq[i]}\t{p[0]:.9g}\t{p[1]:.9g}\t{p[2]:.9g}\t{p[3]:.9g}\n"
        for i, p in zip(range(first, last), probs[first:last].tolist(), strict=True)
    )


class ProbsWriter:
    """Writes ``<name>.probs.tsv.gz`` and/or ``<name>.probs.npy``."""

    def write_tsv_gz(
        self,
        *,
        name: str,
        blocks: Sequence[tuple[str, str, np.ndarray]],
        start: int,
        out_dir: str,
    ) -> str:
        """Write the gzip TSV from one ``(chrom, seq, probs)`` block per contig; return its path."""
        path = Path(out_dir) / f"{name}.probs.tsv.gz"
        path.parent.mkdir(parents=True, exist_ok=True)
        multi = len(blocks) > 1
        # filename="" and mtime=0: no timestamp or name in the header, so the bytes are stable.
        with (
            open(path, "wb") as raw,
            gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as gz,
            io.TextIOWrapper(gz, encoding="utf-8", newline="\n") as text,
        ):
            text.write("\t".join(PROBS_COLUMNS) + "\n")
            for chrom, seq, probs in blocks:
                if multi:
                    text.write(f"# contig: {chrom}\n")
                for first in range(0, len(probs), _CHUNK):
                    text.write(_rows(seq, probs, start, first, min(first + _CHUNK, len(probs))))
        return str(path)

    def write_npy(self, *, name: str, blocks: Sequence[tuple[str, str, np.ndarray]], out_dir: str) -> str:
        """Write the matrices, concatenated in block order, as a float32 ``.npy``; return its path."""
        path = Path(out_dir) / f"{name}.probs.npy"
        path.parent.mkdir(parents=True, exist_ok=True)
        matrix = np.concatenate([p for _, _, p in blocks]) if blocks else np.zeros((0, 4), dtype=np.float32)
        with open(path, "wb") as fh:
            np.save(fh, matrix.astype(np.float32, copy=False), allow_pickle=False)
        return str(path)
