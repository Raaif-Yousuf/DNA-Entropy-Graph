"""GenBank writer — sequence + gene features, each annotated with its mean entropy.

Self-contained result for sequence editors (SnapGene / Benchling / Geneious): every gene
carries a ``/note="mean_entropy=... bits"`` qualifier, so the coarse per-gene value shows
right on the feature. The full per-base entropy graph lives in the companion WIG track for
genome browsers (IGV/UCSC/JBrowse) — GenBank has no per-base numeric channel.

Supports a single sequence (``write``) or many (``write_multi``, one GenBank record per
contig in one file) so a multi-record GenBank input round-trips to a multi-record output.

Unlike the uniform :class:`Writer` protocol, this writer also takes ``features``, so the
pipeline calls it directly.
"""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path

import numpy as np

from ..annotators.base import GeneFeature


def wraps_origin(feature: GeneFeature, length: int, circular: bool) -> bool:
    """True when ``feature`` crosses the origin of a circular molecule of ``length`` bases (issue
    #694): two or more real segments, one ending at the last base and one starting at base 1. The
    same rule as ``analysis/gene_summary.py`` and the readers, so a feature the readers call a wrap
    is the one the writers write as a join. On a linear molecule such segments are a splice (the
    outer span is then the right ``gene`` feature, as GenBank itself writes it)."""
    segments = feature.segments
    return (
        circular
        and len(segments) > 1
        and any(end == length for _, end in segments)
        and any(begin == 1 for begin, _ in segments)
    )


def wrap_parts(feature: GeneFeature, length: int) -> list[tuple[int, int]]:
    """A wrapping feature's segments read from where it starts: the one ending at the last base
    first (``120..126,1..10``), the way GenBank writes an origin join."""
    segs = sorted(feature.segments)
    first = next(i for i, (_, e) in enumerate(segs) if e == length)
    return segs[first:] + segs[:first]


def _build_record(
    contig_name: str,
    seq: str,
    features: Sequence[GeneFeature],
    values: np.ndarray,
    source_id: str,
    circular: bool = False,
):
    """Build one Biopython ``SeqRecord`` with per-gene mean-entropy notes."""
    from Bio.Seq import Seq
    from Bio.SeqFeature import CompoundLocation, FeatureLocation, SeqFeature
    from Bio.SeqRecord import SeqRecord

    locus = contig_name[:16] or "locus"  # GenBank LOCUS ids are short; avoid a warning
    desc = f"DNA-Entropy per-position Shannon entropy for {contig_name}"
    if source_id and source_id != contig_name:
        desc += f" (source: {source_id})"
    record = SeqRecord(Seq(seq), id=locus, name=locus, description=desc)
    record.annotations["molecule_type"] = "DNA"
    record.annotations["source"] = "DNA-Entropy"
    if circular:
        record.annotations["topology"] = "circular"  # LOCUS says circular, so a reader sees the wrap

    for f in features:
        strand = {"+": 1, "-": -1}.get(f.strand)  # "." stays unstranded
        wraps = wraps_origin(f, len(seq), circular)
        if wraps:
            # issue #694: write the real parts (join), never the whole-molecule outer span.
            parts = wrap_parts(f, len(seq))
            location = CompoundLocation(
                [FeatureLocation(b - 1, e, strand) for b, e in (parts if strand != -1 else parts[::-1])]
            )
            index = np.concatenate([np.arange(b - 1, e) for b, e in parts])
            segment = np.asarray(values[index], dtype=float)
        else:
            begin0 = max(f.begin - 1, 0)  # 1-based inclusive -> 0-based half-open
            end = min(f.end, len(seq))
            if end <= begin0:
                continue
            location = FeatureLocation(begin0, end, strand)
            segment = np.asarray(values[begin0:end], dtype=float)
        mean_h = float(segment.mean()) if segment.size else 0.0
        quals: dict[str, list[str]] = {"note": [f"mean_entropy={mean_h:.3f} bits"]}
        if f.gene_id:
            quals["gene"] = [f.gene_id]
        if f.partial:
            quals["note"].append("partial")
        record.features.append(
            SeqFeature(
                location,
                type="gene",
                qualifiers=quals,
            )
        )
    return record


class GenBankWriter:
    """Writes ``<name>.gb`` (GenBank flat file with per-gene entropy notes)."""

    def write(
        self,
        *,
        name: str,
        values: np.ndarray,
        seq: str,
        start: int,
        features: Sequence[GeneFeature],
        out_dir: str,
        circular: bool = False,
    ) -> str:
        """Write a single-record GenBank."""
        return self.write_multi(
            name=name, records=[(name, seq, features, values, "")], out_dir=out_dir, circular=[circular]
        )

    def write_multi(
        self,
        *,
        name: str,
        records: Sequence[tuple[str, str, Sequence[GeneFeature], np.ndarray, str]],
        out_dir: str,
        circular: Sequence[bool] | None = None,
    ) -> str:
        """Write one GenBank file holding one record per contig.

        Each ``records`` item is ``(contig_name, seq, features, values, source_id)``. ``circular``
        is parallel to ``records`` (default: all linear); a circular record is written with a
        ``circular`` LOCUS line, and a gene crossing its origin as a ``join`` (issue #694).
        """
        from Bio.SeqIO import write as _seqio_write

        seq_records = [
            _build_record(contig_name, seq, features, values, source_id, is_circular)
            for (contig_name, seq, features, values, source_id), is_circular in zip(
                records, circular if circular is not None else [False] * len(records), strict=True
            )
        ]
        out_path = Path(out_dir) / f"{name}.gb"
        out_path.parent.mkdir(parents=True, exist_ok=True)
        # Open the file ourselves (Hard Rule 5: UTF-8, LF newlines) rather than handing
        # SeqIO.write a bare path -- Biopython then opens it itself in the platform's
        # default text mode, which on Windows means CRLF (MEASURED 2026-09-19, issue
        # found during the lane-B science/writers audit).
        with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
            _seqio_write(seq_records, fh, "genbank")
        return str(out_path)
