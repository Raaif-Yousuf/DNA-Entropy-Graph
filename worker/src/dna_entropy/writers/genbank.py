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


def _build_record(
    contig_name: str,
    seq: str,
    features: Sequence[GeneFeature],
    values: np.ndarray,
    source_id: str,
    circular: bool = False,
):
    """Build one Biopython ``SeqRecord`` with per-gene mean-entropy notes. ``circular`` (issue
    #524) writes ``circular`` in the LOCUS line; a linear record writes no topology word, as
    Biopython does by default, so every linear file is byte-identical to before this existed."""
    from Bio.Seq import Seq
    from Bio.SeqFeature import FeatureLocation, SeqFeature
    from Bio.SeqRecord import SeqRecord

    locus = contig_name[:16] or "locus"  # GenBank LOCUS ids are short; avoid a warning
    desc = f"DNA-Entropy per-position Shannon entropy for {contig_name}"
    if source_id and source_id != contig_name:
        desc += f" (source: {source_id})"
    record = SeqRecord(Seq(seq), id=locus, name=locus, description=desc)
    record.annotations["molecule_type"] = "DNA"
    record.annotations["source"] = "DNA-Entropy"
    if circular:
        record.annotations["topology"] = "circular"

    for f in features:
        begin0 = max(f.begin - 1, 0)  # 1-based inclusive -> 0-based half-open
        end = min(f.end, len(seq))
        if end <= begin0:
            continue
        segment = np.asarray(values[begin0:end], dtype=float)
        mean_h = float(segment.mean()) if segment.size else 0.0
        quals: dict[str, list[str]] = {"note": [f"mean_entropy={mean_h:.3f} bits"]}
        if f.gene_id:
            quals["gene"] = [f.gene_id]
        if f.partial:
            quals["note"].append("partial")
        record.features.append(
            SeqFeature(
                FeatureLocation(begin0, end, strand=1 if f.strand == "+" else -1),
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

        Each ``records`` item is ``(contig_name, seq, features, values, source_id)``.
        ``circular`` (issue #524), when given, has one entry per record: the topology the contig
        was analyzed with, written into its LOCUS line. ``None`` means every record is linear.
        """
        from Bio.SeqIO import write as _seqio_write

        if circular is None:
            circular = [False] * len(records)
        if len(circular) != len(records):
            raise ValueError("circular needs one entry per record.")
        seq_records = [
            _build_record(contig_name, seq, features, values, source_id, is_circular)
            for (contig_name, seq, features, values, source_id), is_circular in zip(
                records, circular, strict=True
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
