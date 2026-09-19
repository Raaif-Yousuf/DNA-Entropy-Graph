"""TSV writer for the per-position entropy track — the spreadsheet-friendly output.

**Format decision (recorded here so nobody re-litigates it elsewhere; confirms the
proposal in ``docs/science_and_formats.md`` section 5, issue #281/#46):**

- **Coordinates: 1-based, inclusive** — matching WIG's ``fixedStep`` and both GFF3
  flavours (the majority convention in this package), NOT bedGraph's 0-based half-open
  convention. A spreadsheet-opened TSV is read directly against 1-based GenBank-style
  positions by a biologist, not fed to a half-open-aware parser.
- **Columns, ``Forward only``/``Reverse only``/``Both, combined``/``Both, averaged``**
  (i.e. every :class:`~dna_entropy.config.Direction` except ``BOTH_SEPARATE``): exactly
  three tab-separated columns, ``position``, ``base``, ``entropy_bits``.
- **Columns, ``Both, separate tracks``**: five tab-separated columns, ``position``,
  ``base``, ``entropy_fwd``, ``entropy_rev``, ``entropy_combined`` — one file holding all
  three tracks side by side, rather than three separate files (bedGraph/WIG DO get
  separate ``.fwd``/``.rev`` files for the viewer; the TSV is for a human reading rows, who
  is better served by one sheet with three number columns to compare directly).
- ``entropy_*`` values are formatted to 4 decimal places (bits), matching every other
  writer here.
- A multi-record file (``write_multi``/``write_multi_separate``) stays exactly 3 (or 5)
  columns rather than growing a ``contig`` column: each contig's block is introduced by a
  ``# contig: <name>`` comment line (most spreadsheet/CSV tooling either ignores a line
  starting with ``#`` or shows it harmlessly in column A), and position numbering
  restarts at ``start`` for each contig — the same per-contig coordinate frame
  bedGraph/WIG already use.
"""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path

import numpy as np

from .base import write_text_lf

_HEADER = "position\tbase\tentropy_bits"
_HEADER_WITH_SURPRISAL = "position\tbase\tentropy_bits\tsurprisal_bits"
_HEADER_SEPARATE = "position\tbase\tentropy_fwd\tentropy_rev\tentropy_combined"


def _block_lines(seq: str, values: np.ndarray, start: int, surprisal: np.ndarray | None = None) -> list[str]:
    lines = []
    for i, v in enumerate(values):
        base = seq[i] if i < len(seq) else "?"  # defensive: should never happen in practice
        line = f"{start + i}\t{base}\t{float(v):.4f}"
        if surprisal is not None:
            # issue #123: "A position where the actual base has probability 0.01 shows
            # surprisal 6.64 bits in the TSV while entropy there is low" -- the exact
            # observable the issue names, as an extra column alongside entropy, never a
            # separate file (BOTH_SEPARATE's 5-column fwd/rev/combined shape does not yet
            # grow surprisal columns -- out of scope for this pass, tracked separately).
            line += f"\t{float(surprisal[i]):.4f}"
        lines.append(line)
    return lines


def _block_lines_separate(
    seq: str,
    fwd: np.ndarray,
    rev: np.ndarray,
    combined: np.ndarray,
    start: int,
) -> list[str]:
    lines = []
    for i in range(len(combined)):
        base = seq[i] if i < len(seq) else "?"
        lines.append(
            f"{start + i}\t{base}\t{float(fwd[i]):.4f}\t{float(rev[i]):.4f}\t{float(combined[i]):.4f}"
        )
    return lines


class TsvWriter:
    """Writes ``<name>.entropy.tsv`` — position, base, entropy, one row per analyzed base.

    Uses the 5-column ``entropy_fwd``/``entropy_rev``/``entropy_combined`` shape when the
    caller supplies separate forward/reverse tracks (:attr:`~dna_entropy.config.Direction.BOTH_SEPARATE`),
    via :meth:`write_separate`/:meth:`write_multi_separate`; otherwise the plain 3-column
    ``position``/``base``/``entropy_bits`` shape.
    """

    def write(
        self,
        *,
        name: str,
        values: np.ndarray,
        seq: str,
        start: int,
        out_dir: str,
        surprisal: np.ndarray | None = None,
    ) -> str:
        return self.write_multi(
            name=name,
            blocks=[(name, seq, values)],
            start=start,
            out_dir=out_dir,
            surprisal_blocks=[surprisal] if surprisal is not None else None,
        )

    def write_multi(
        self,
        *,
        name: str,
        blocks: Sequence[tuple[str, str, np.ndarray]],
        start: int,
        out_dir: str,
        surprisal_blocks: Sequence[np.ndarray] | None = None,
    ) -> str:
        """Write one TSV (3 columns, or 4 with ``surprisal_blocks``) with a block per
        ``(chrom, seq, values)`` in ``blocks``.

        A single block writes no ``# contig:`` comment line, so a single-record run's
        file is identical whether it came from :meth:`write` or :meth:`write_multi` with
        one block. ``surprisal_blocks`` (issue #123), when given, must align 1:1 with
        ``blocks`` and adds a ``surprisal_bits`` 4th column; omitted (the default) keeps
        every existing caller's output byte-identical to before this option existed.
        """
        header = _HEADER_WITH_SURPRISAL if surprisal_blocks is not None else _HEADER
        lines = [header]
        multi = len(blocks) > 1
        for i, (chrom, seq, values) in enumerate(blocks):
            if multi:
                lines.append(f"# contig: {chrom}")
            s = surprisal_blocks[i] if surprisal_blocks is not None else None
            lines.extend(_block_lines(seq, values, start, s))
        text = "\n".join(lines) + "\n"
        return write_text_lf(Path(out_dir) / f"{name}.entropy.tsv", text)

    def write_separate(
        self,
        *,
        name: str,
        forward_values: np.ndarray,
        reverse_values: np.ndarray,
        combined_values: np.ndarray,
        seq: str,
        start: int,
        out_dir: str,
    ) -> str:
        return self.write_multi_separate(
            name=name,
            blocks=[(name, seq, forward_values, reverse_values, combined_values)],
            start=start,
            out_dir=out_dir,
        )

    def write_multi_separate(
        self,
        *,
        name: str,
        blocks: Sequence[tuple[str, str, np.ndarray, np.ndarray, np.ndarray]],
        start: int,
        out_dir: str,
    ) -> str:
        """Write one TSV (5 columns: fwd/rev/combined) for :attr:`Direction.BOTH_SEPARATE`.

        Each block is ``(chrom, seq, forward_values, reverse_values, combined_values)``.
        """
        lines = [_HEADER_SEPARATE]
        multi = len(blocks) > 1
        for chrom, seq, fwd, rev, combined in blocks:
            if multi:
                lines.append(f"# contig: {chrom}")
            lines.extend(_block_lines_separate(seq, fwd, rev, combined, start))
        text = "\n".join(lines) + "\n"
        return write_text_lf(Path(out_dir) / f"{name}.entropy.tsv", text)
