"""GFF3 writer for gene boundaries (IGV feature track).

Emits features on the same contig(s) as the entropy track, offset by the same ``start``
coordinate, so genes and entropy line up in IGV. Two sources feed this:

- **Prodigal** (FASTA/paste path): predicted CDS emitted as ``gene`` features via ``write``.
- **GenBank** (GenBank path): the record's **existing** genes via ``write_multi`` — one
  block per record, keyed by contig name — so IGV shows the curated genes, not Prodigal's.
"""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path

from ..annotators.base import GeneFeature
from .base import write_text_lf
from .genbank import wrap_parts, wraps_origin

# GFF3 column-9 reserved characters that must be percent-encoded in attribute values.
# "%" MUST be escaped first, and before every other character here: it is the escape
# character itself, so escaping it first guarantees none of the OTHER escapes' own "%"
# output (e.g. the "%3B" produced for a literal ";") gets re-escaped into "%253B", and
# that a literal "%1;" in the input can never be misread as an escaped "%1" plus a bare
# ";" by a downstream percent-decoder (issue found during the lane-B writers audit).
_GFF3_RESERVED = {
    "%": "%25",
    ";": "%3B",
    "=": "%3D",
    "&": "%26",
    ",": "%2C",
    "\t": "%09",
    "\n": "%0A",
}


def _escape(value: str) -> str:
    for ch, code in _GFF3_RESERVED.items():
        value = value.replace(ch, code)
    return value


def _feature_lines(
    chrom: str, source: str, f: GeneFeature, offset: int, length: int, circular: bool
) -> list[str]:
    """One row per gene, or (issue #694) one row per segment of a gene crossing the origin of a
    circular molecule, all sharing the gene's ``ID``: the GFF3 convention for a discontinuous
    feature, which the GFF3 reader maps back to the same wrapping gene."""
    gid = _escape(f.gene_id)
    attrs = f"ID={gid};Name={gid}"
    if f.partial:
        attrs += ";partial=true"
    spans = wrap_parts(f, length) if wraps_origin(f, length, circular) else [(f.begin, f.end)]
    return [
        "\t".join([chrom, source, "gene", str(b + offset), str(e + offset), ".", f.strand, ".", attrs])
        for b, e in spans
    ]


def _region_line(chrom: str, start: int, length: int) -> str:
    """The ``region`` row that declares a molecule circular (``Is_circular=true``), so a reader of
    this file (ours, IGV, Artemis) knows a gene split across the origin is one gene."""
    attrs = f"ID={_escape(chrom)};Is_circular=true"
    return "\t".join(
        [chrom, "dna-entropy", "region", str(start), str(start + length - 1), ".", "+", ".", attrs]
    )


class GffWriter:
    """Writes ``<name>.genes.gff3``."""

    def write(
        self,
        *,
        name: str,
        features: Sequence[GeneFeature],
        length: int,
        start: int,
        out_dir: str,
        source: str = "pyrodigal",
        circular: bool = False,
    ) -> str:
        return self.write_multi(
            name=name,
            blocks=[(name, features, length)],
            start=start,
            out_dir=out_dir,
            source=source,
            circular=[circular],
        )

    def write_multi(
        self,
        *,
        name: str,
        blocks: Sequence[tuple[str, Sequence[GeneFeature], int]],
        start: int,
        out_dir: str,
        source: str = "genbank",
        circular: Sequence[bool] | None = None,
    ) -> str:
        """Write one GFF3 with a ``(chrom, features, length)`` block per record in ``blocks``.

        ``offset`` keeps genes aligned with the entropy track's coordinates; each block's
        ``chrom`` matches its FASTA contig so IGV places the genes on the right sequence.
        ``circular`` is parallel to ``blocks`` (default: all linear): a circular block gets a
        ``region`` row with ``Is_circular=true`` and writes a gene crossing the origin as two rows
        sharing one ``ID`` (issue #694).
        """
        offset = start - 1
        lines = ["##gff-version 3"]
        for chrom, _features, length in blocks:
            lines.append(f"##sequence-region {chrom} {start} {start + length - 1}")
        flags = list(circular) if circular is not None else [False] * len(blocks)
        for (chrom, features, length), is_circular in zip(blocks, flags, strict=True):
            if is_circular:
                lines.append(_region_line(chrom, start, length))
            for f in features:
                lines.extend(_feature_lines(chrom, source, f, offset, length, is_circular))
        text = "\n".join(lines) + "\n"
        return write_text_lf(Path(out_dir) / f"{name}.genes.gff3", text)
