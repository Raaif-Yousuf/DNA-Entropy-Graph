"""Read a GFF3 annotation file into gene features per sequence name (issue #135).

A FASTA + GFF3 pair is how many pipelines hand over an annotated sequence. The GFF3 supplies
the genes, so Prodigal never runs: like the GenBank reader, the file's own ``gene`` features
are used as-is, else its ``CDS`` features, decided per sequence name. Hand-rolled (GFF3 is
nine tab-separated columns), so it needs no dependency.

Coordinates in GFF3 are 1-based inclusive, the same as :class:`GeneFeature`. Discontinuous
features (several ``CDS`` rows sharing one ``ID``) become one feature whose ``begin..end`` is
the outer span and whose ``segments`` carry the real parts, with a notice, exactly like a
GenBank ``join(...)`` (issue #295).

Notices never carry a feature id or sequence name (issue #253: free text a user chose).
"""

from __future__ import annotations

from urllib.parse import unquote

from ..annotators.base import GeneFeature
from ..validation.validators import ValidationError
from .encoding import read_text


class Gff3ReadError(ValidationError):
    """The GFF3 file has nothing usable. A ``ValidationError``, so the run reports
    ``INPUT_INVALID`` and names one action."""


_NAME_KEYS = ("Name", "gene", "locus_tag", "product", "ID")


def _attributes(col9: str) -> dict[str, str]:
    out: dict[str, str] = {}
    for part in col9.split(";"):
        key, sep, value = part.partition("=")
        if sep and key.strip():
            out[key.strip()] = unquote(value.strip())
    return out


def read_gff3(path: str) -> tuple[dict[str, list[GeneFeature]], list[str]]:
    """Return ``({seqid: [GeneFeature, ...]}, notices)``. Raises :class:`Gff3ReadError` when the
    file holds no ``gene`` or ``CDS`` feature at all."""
    text = read_text(path).replace("\r\n", "\n").replace("\r", "\n")
    rows: dict[str, dict[str, list[tuple[int, int, str, bool, dict[str, str]]]]] = {}
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
        ftype = cols[2]
        if ftype not in ("gene", "CDS"):
            continue
        attrs = _attributes(cols[8])
        strand = "-" if cols[6] == "-" else "+"
        partial = attrs.get("partial", "").lower() == "true"
        rows.setdefault(cols[0], {"gene": [], "CDS": []})[ftype].append((begin, end, strand, partial, attrs))

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
        groups: dict[str, list[tuple[int, int, str, bool, dict[str, str]]]] = {}
        order: list[object] = []
        for row in source:
            fid = row[4].get("ID", "") if use_cds else ""
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
                notices.append(
                    f"A GFF3 CDS has {len(parts)} segments ({', '.join(f'{a}..{b}' for a, b in segments)}). "
                    f"Its reported boundary {begin}..{end} is the OUTER SPAN and includes the sequence "
                    "between segments (docs/science_and_formats.md)."
                )
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
    return result, notices
