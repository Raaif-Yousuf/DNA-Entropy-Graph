"""Read a GFF3 annotation file into gene features per sequence name (issue #135).

A FASTA + GFF3 pair is how many pipelines hand over an annotated sequence. The GFF3 supplies
the genes, so Prodigal never runs: like the GenBank reader, the file's own ``gene`` features
are used as-is, else its ``CDS`` features, decided per sequence name. Hand-rolled (GFF3 is
nine tab-separated columns), so it needs no dependency.

Coordinates in GFF3 are 1-based inclusive, the same as :class:`GeneFeature`, counted over the
sequence letters (FASTA line wrapping and case do not matter). The sequence name (column 1) is
percent-decoded, as GFF3 requires. Rows sharing one ``ID`` are one discontinuous feature: its
``begin..end`` is the outer span and its ``segments`` carry the real parts, exactly like a
GenBank ``join(...)`` (issue #295). The strand stays ``+``, ``-`` or ``.`` (unstranded: ``.`` and
``?`` both); a strand is never invented.

Whether a feature fits its sequence, and whether one crossing the origin of a circular sequence
is a wrap, is decided by :func:`fit_to_sequence`, which knows the sequence length.

Notices never carry a feature id or sequence name (issue #253: free text a user chose).
"""

from __future__ import annotations

import os
from urllib.parse import unquote

from ..annotators.base import GeneFeature
from ..validation.validators import ValidationError
from .encoding import read_text


class Gff3ReadError(ValidationError):
    """The GFF3 file has nothing usable. A ``ValidationError``, so the run reports
    ``INPUT_INVALID`` and names one action."""


_NAME_KEYS = ("Name", "gene", "locus_tag", "product", "ID")
_Row = tuple[int, int, str, bool, dict[str, str]]


def _attributes(col9: str) -> dict[str, str]:
    out: dict[str, str] = {}
    for part in col9.split(";"):
        key, sep, value = part.partition("=")
        if sep and key.strip():
            out[key.strip()] = unquote(value.strip())
    return out


def read_gff3(path: str | os.PathLike[str]) -> tuple[dict[str, list[GeneFeature]], list[str], set[str]]:
    """Return ``({seqid: [GeneFeature, ...]}, notices, circular_seqids)``.

    ``circular_seqids`` are the sequence names with an ``Is_circular=true`` attribute on any row
    (normally the ``region`` row). Raises :class:`Gff3ReadError` when the file holds no ``gene`` or
    ``CDS`` feature at all."""
    text = read_text(path).replace("\r\n", "\n").replace("\r", "\n")
    rows: dict[str, dict[str, list[_Row]]] = {}
    circular: set[str] = set()
    bad_count = 0
    first_bad = 0
    for lineno, line in enumerate(text.split("\n"), start=1):
        if line.startswith("##FASTA"):
            break  # an embedded FASTA section follows: not annotation rows
        if not line.strip() or line.startswith("#"):
            continue
        cols = line.split("\t")
        ok = len(cols) == 9
        if ok:
            try:
                begin, end = int(cols[3]), int(cols[4])
            except ValueError:
                ok = False
            else:
                ok = 1 <= begin <= end
        if not ok:
            bad_count += 1
            first_bad = first_bad or lineno
            continue
        seqid = unquote(cols[0])
        attrs = _attributes(cols[8])
        if attrs.get("Is_circular", "").lower() == "true":
            circular.add(seqid)
        ftype = cols[2]
        if ftype not in ("gene", "CDS"):
            continue
        strand = cols[6] if cols[6] in ("+", "-") else "."  # '.' and '?' are both unstranded
        partial = attrs.get("partial", "").lower() == "true"
        rows.setdefault(seqid, {"gene": [], "CDS": []})[ftype].append((begin, end, strand, partial, attrs))

    notices: list[str] = []
    if bad_count:
        notices.append(
            f"Skipped {bad_count} malformed GFF3 line(s) (the first is line {first_bad}); each needs "
            "nine tab-separated columns with whole-number start and end, 1 <= start <= end."
        )

    result: dict[str, list[GeneFeature]] = {}
    for seqid, by_type in rows.items():
        genes = by_type["gene"]
        use_cds = not genes
        source = by_type["CDS"] if use_cds else genes
        features: list[GeneFeature] = []
        groups: dict[str, list[_Row]] = {}
        order: list[str | _Row] = []
        for row in source:
            fid = row[4].get("ID", "")
            if fid:
                if fid not in groups:
                    groups[fid] = []
                    order.append(fid)
                groups[fid].append(row)
            else:
                order.append(row)
        for item in order:
            parts = groups[item] if isinstance(item, str) else [item]
            parts = sorted(parts, key=lambda r: r[0])
            begin = parts[0][0]
            end = max(p[1] for p in parts)
            attrs = parts[0][4]
            gene_id = next((attrs[k] for k in _NAME_KEYS if attrs.get(k)), "CDS" if use_cds else "gene")
            segments: tuple[tuple[int, int], ...] = ()
            if len(parts) > 1:
                segments = tuple((p[0], p[1]) for p in parts)
            features.append(
                GeneFeature(
                    begin=begin,
                    end=end,
                    strand=parts[0][2],
                    partial=any(p[3] for p in parts),
                    gene_id=gene_id,
                    segments=segments,
                )
            )
        result[seqid] = features

    total = sum(len(v) for v in result.values())
    if total == 0:
        raise Gff3ReadError(
            "The GFF3 file has no gene or CDS features. Check it is the annotation for this FASTA "
            "(or remove it and run without genes)."
        )
    notices.append(
        f"Read {total} gene feature(s) for {len(result)} sequence name(s) from the GFF3 (not re-annotated)."
    )
    return result, notices, circular


def fit_to_sequence(
    features: list[GeneFeature], seq_len: int, circular: bool
) -> tuple[list[GeneFeature], list[str]]:
    """Place a paired GFF3's features on a sequence of ``seq_len`` bases.

    ``circular`` is whether the run analyses this sequence as circular (the GFF3's
    ``Is_circular=true`` or ``--topology circular``). Returns ``(kept, notices)``:

    - a feature inside the sequence is kept; one with several segments (rows sharing an ``ID``) says
      so, and on a circular sequence a segment set that touches both ends is a feature crossing the
      origin: outer span ``1..seq_len`` with its ``segments``, like a GenBank origin join;
    - on a circular sequence a single row whose end is past the length crosses the origin (GFF3's
      "end greater than the sequence length"): it becomes the two segments ``begin..seq_len`` and
      ``1..end-seq_len``, provided the two parts do not overlap;
    - anything else running past the end is dropped and counted in one notice that names both
      possible causes (a circular sequence not declared as such, or a GFF3 for a different FASTA).
    """
    kept: list[GeneFeature] = []
    notices: list[str] = []
    past_end = 0
    for f in features:
        segs = list(f.segments) if f.segments else [(f.begin, f.end)]
        if all(e <= seq_len for _, e in segs):
            if len(segs) > 1:
                touches_origin = any(b == 1 for b, _ in segs) and any(e == seq_len for _, e in segs)
                desc = ", ".join(f"{b}..{e}" for b, e in segs)
                if circular and touches_origin:
                    f = GeneFeature(
                        begin=1,
                        end=seq_len,
                        strand=f.strand,
                        partial=f.partial,
                        gene_id=f.gene_id,
                        segments=f.segments,
                    )
                    notices.append(
                        f"A GFF3 feature crosses the origin of this circular sequence, in {len(segs)} "
                        f"segments ({desc}). Its reported boundary 1..{seq_len} is the OUTER SPAN, the "
                        "whole molecule; the gene table uses the segment coordinates "
                        "(docs/science_and_formats.md)."
                    )
                else:
                    notices.append(
                        f"A GFF3 feature has {len(segs)} segments ({desc}). Its reported boundary "
                        f"{f.begin}..{f.end} is the OUTER SPAN and includes the sequence between "
                        "segments (docs/science_and_formats.md)."
                    )
            kept.append(f)
        elif circular and len(segs) == 1 and f.begin <= seq_len and f.end - seq_len < f.begin:
            wrapped = ((1, f.end - seq_len), (f.begin, seq_len))
            kept.append(
                GeneFeature(
                    begin=1,
                    end=seq_len,
                    strand=f.strand,
                    partial=f.partial,
                    gene_id=f.gene_id,
                    segments=wrapped,
                )
            )
            notices.append(
                f"A GFF3 feature {f.begin}..{f.end} crosses the origin of this {seq_len} nt circular "
                f"sequence, so it is read as the segments {wrapped[1][0]}..{wrapped[1][1]} and "
                f"{wrapped[0][0]}..{wrapped[0][1]}. Its reported boundary 1..{seq_len} is the OUTER "
                "SPAN, the whole molecule; the gene table uses the segment coordinates "
                "(docs/science_and_formats.md)."
            )
        else:
            past_end += 1
    if past_end:
        notices.append(
            f"{past_end} GFF3 feature(s) run past the end of their sequence ({seq_len} nt) and were "
            "dropped. If the sequence is circular and the feature crosses the origin, set "
            "Is_circular=true on its GFF3 region line or run with --topology circular; otherwise "
            "check the GFF3 belongs to this FASTA."
        )
    return kept, notices
