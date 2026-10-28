"""The Writer contract and shared helpers for output files.

All writers share one signature so the pipeline can call them uniformly; each ignores
the arguments it doesn't need. Files are written UTF-8 with LF newlines for
deterministic, IGV-friendly output (CLAUDE.md hard rule #11).
"""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path
from typing import Protocol, runtime_checkable

import numpy as np


@runtime_checkable
class Writer(Protocol):
    """Writes one output artifact and returns its path."""

    def write(
        self,
        *,
        name: str,
        values: np.ndarray,
        seq: str,
        start: int,
        out_dir: str,
    ) -> str:
        """Write the artifact for ``name`` into ``out_dir``; return the file path."""
        ...


@runtime_checkable
class TrackWriter(Writer, Protocol):
    """A position-indexed track writer (bedGraph or WIG) that can also write one block per
    contig. ``pipeline._select_track_writer`` returns either; this is the type they share, so
    the type checker (``scripts/check_pyright.py``) can see ``write_multi`` exists on both."""

    def write_multi(
        self,
        *,
        name: str,
        blocks: Sequence[tuple[str, np.ndarray]],
        start: int,
        out_dir: str,
        variant: str | None = None,
        metric: str = "entropy",
    ) -> str:
        """Write one file with a block per ``(contig name, values)``; return the file path."""
        ...


def write_text_lf(path: Path, text: str) -> str:
    """Write ``text`` with UTF-8 encoding and LF newlines; return the path as str."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    return str(path)
