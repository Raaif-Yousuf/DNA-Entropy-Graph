"""Gene summary table writer (issue #124): ``<name>.genes.tsv`` and ``<name>.genes.csv``.

The same table twice: TSV for scripts and IGV-adjacent tools, CSV because that is what a
spreadsheet opens without asking. Entropy and surprisal are in bits, to 4 decimals (the
precision of every other track). Both files are UTF-8 with LF (Hard Rule 5); the CSV uses
Python's ``csv`` module so a gene name containing a comma or a quote is quoted, not corrupted.
A TSV cannot quote, so a tab or line break inside a text field is replaced by a space.
"""

from __future__ import annotations

import csv
import io
from collections.abc import Sequence
from pathlib import Path

from ..analysis.gene_summary import GeneRow
from .base import write_text_lf

GENE_SUMMARY_COLUMNS: tuple[str, ...] = (
    "contig",
    "gene_id",
    "begin",
    "end",
    "strand",
    "length_nt",
    "partial",
    "wraps_origin",
    "segments",
    "mean_entropy_bits",
    "min_entropy_bits",
    "max_entropy_bits",
    "mean_surprisal_bits",
    "fraction_low_entropy",
)


def _fmt(value: float | None) -> str:
    return "" if value is None else f"{value:.4f}"


def _cells(row: GeneRow) -> list[str]:
    return [
        row.contig,
        row.gene_id,
        str(row.begin),
        str(row.end),
        row.strand,
        str(row.length),
        "true" if row.partial else "false",
        "true" if row.wraps_origin else "false",
        row.segments,
        _fmt(row.mean_entropy),
        _fmt(row.min_entropy),
        _fmt(row.max_entropy),
        _fmt(row.mean_surprisal),
        _fmt(row.fraction_low_entropy),
    ]


def _tsv_safe(cell: str) -> str:
    return cell.replace("\r", " ").replace("\n", " ").replace("\t", " ")


class GeneSummaryWriter:
    """Writes ``<name>.genes.tsv`` and ``<name>.genes.csv``."""

    def write_multi(self, *, name: str, rows: Sequence[GeneRow], out_dir: str) -> list[str]:
        """Write both files (header-only when ``rows`` is empty); return ``[tsv_path, csv_path]``."""
        table = [_cells(r) for r in rows]
        tsv_lines = ["\t".join(GENE_SUMMARY_COLUMNS)] + [
            "\t".join(_tsv_safe(c) for c in cells) for cells in table
        ]
        tsv = write_text_lf(Path(out_dir) / f"{name}.genes.tsv", "\n".join(tsv_lines) + "\n")
        buffer = io.StringIO()
        writer = csv.writer(buffer, lineterminator="\n")
        writer.writerow(GENE_SUMMARY_COLUMNS)
        writer.writerows(table)
        return [tsv, write_text_lf(Path(out_dir) / f"{name}.genes.csv", buffer.getvalue())]
