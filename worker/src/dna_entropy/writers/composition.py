"""``<name>.codons.tsv``: per-CDS codon usage (issue #191). The GC track reuses
``BedGraphWriter`` (``metric="gc"``). UTF-8 with LF (Hard Rule 5); a tab or line break in a gene
id is replaced by a space, since a TSV cannot quote."""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path

from ..analysis.composition import CodonRow
from .base import write_text_lf

CODON_COLUMNS: tuple[str, ...] = (
    "contig",
    "gene_id",
    "begin",
    "end",
    "strand",
    "codon",
    "amino_acid",
    "count",
    "frequency",
    "rscu",
    "deviation",
)


def _text(value: str) -> str:
    return " ".join(value.replace("\t", " ").replace("\r", " ").replace("\n", " ").split())


class CodonUsageWriter:
    """Writes ``<name>.codons.tsv``."""

    def write_multi(self, *, name: str, rows: Sequence[tuple[str, CodonRow]], out_dir: str) -> str:
        lines = ["\t".join(CODON_COLUMNS)]
        for contig, r in rows:
            lines.append(
                "\t".join(
                    [
                        _text(contig),
                        _text(r.gene_id),
                        str(r.begin),
                        str(r.end),
                        r.strand,
                        r.codon,
                        r.amino_acid,
                        str(r.count),
                        f"{r.frequency:.4f}",
                        f"{r.rscu:.4f}",
                        f"{r.deviation:.4f}",
                    ]
                )
            )
        return write_text_lf(Path(out_dir) / f"{name}.codons.tsv", "\n".join(lines) + "\n")
