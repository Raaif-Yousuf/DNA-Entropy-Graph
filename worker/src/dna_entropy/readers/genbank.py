"""Read a GenBank file into its records: sequence + EXISTING gene features, per record.

Key rule (professor's brief): when the input is a GenBank, we **use its genes as-is** and
never re-annotate. A GenBank may hold **multiple records** (the ``SetTnpB-Evo.gb`` test file
has five); we return **all** of them so the pipeline processes each. This reader extracts
each record's sequence and maps its ``gene`` (or, failing that, ``CDS``) features onto our
:class:`GeneFeature` type. Biopython does the parsing.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from ..annotators.base import GeneFeature
from ..redact import describe_len
from .encoding import read_text


class GenBankReadError(ValueError):
    """Raised when a GenBank file has no usable sequence record."""


@dataclass
class GenBankRecord:
    """One record from a GenBank file: its id, sequence, and pre-existing gene features."""

    record_id: str
    seq: str
    features: list[GeneFeature] = field(default_factory=list)


def _feature_id(feature) -> str:
    """Pick a stable label for a feature from its qualifiers."""
    for key in ("gene", "locus_tag", "product"):
        vals = feature.qualifiers.get(key)
        if vals:
            return str(vals[0])
    return feature.type


def _features_of(record) -> tuple[list[GeneFeature], list[str]]:
    """Map a record's ``gene`` (preferred) or ``CDS`` features to :class:`GeneFeature`.

    Returns ``(features, notices)``. Issue #295: Biopython's ``CompoundLocation`` (a
    spliced/multi-exon ``join(...)`` or ``complement(join(...))`` location) reports
    ``.start``/``.end`` as the outer bounding min/max across every part — it does NOT mean
    "this gene spans every base in between". ``GeneFeature`` has no way to carry more than
    one ``(begin, end)`` pair without changing its shape (owned by
    ``annotators/base.py``, a different lane's file), so we keep reporting the bounding
    box here — but never silently: every compound-location feature gets an explicit
    notice naming its real segments, so a biologist (and anyone computing a per-gene
    mean entropy over ``begin..end`` downstream) knows that span includes intron bases.
    """
    from Bio.SeqFeature import CompoundLocation

    gene_feats = [f for f in record.features if f.type == "gene"]
    cds_feats = [f for f in record.features if f.type == "CDS"]
    source = gene_feats or cds_feats
    seq_len = len(record.seq)

    features: list[GeneFeature] = []
    notices: list[str] = []
    for f in source:
        loc = f.location
        if loc is None:
            continue
        begin = int(loc.start) + 1  # Biopython is 0-based half-open -> our 1-based inclusive
        end = int(loc.end)
        strand = "-" if loc.strand == -1 else "+"
        partial_end = ">" in str(loc.end)
        partial = "<" in str(loc.start) or partial_end
        gene_id = _feature_id(f)
        is_compound = isinstance(loc, CompoundLocation)

        # issue #331: a feature's own coordinates must fit inside this record's actual
        # ORIGIN length, or the file's feature table disagrees with its own sequence data
        # (truncated download, hand-edited LOCUS/ORIGIN, corruption) — accepting it as-is
        # would let writers/genbank.py silently slice a wrong, out-of-range span for its
        # mean-entropy note. A single (non-compound) feature carrying a `>` PARTIAL end
        # marker is the one legitimate reason its end can sit past the record's own
        # length: that is GenBank's own way of saying "known to continue beyond what was
        # given" (THEORY (unverified): confirmed against this reader's own synthetic
        # fixture, not a corpus of real partial GenBank records — revisit if a real file
        # disagrees). A compound (spliced or origin-wrapping) feature's parts must each
        # fit fully in bounds regardless of a partial marker; a legitimate origin wrap
        # already satisfies this per-part (#128's intended circular-plasmid shape is not
        # affected by this check), so only a genuine mismatch ever trips it. Anything
        # that fails this is a data mismatch, not a biological statement, and is dropped
        # — never silently kept with a wrong span — with a notice naming why.
        parts = loc.parts if is_compound else [loc]
        allow_end_overrun = partial_end and not is_compound
        malformed = any(int(p.start) < 0 or int(p.end) <= int(p.start) for p in parts) or (
            not allow_end_overrun and any(int(p.end) > seq_len for p in parts)
        )
        if malformed:
            notices.append(
                f"GenBank feature {gene_id!r} has coordinates {begin}..{end}, which fall "
                f"outside record {record.id!r}'s own sequence length of {seq_len} nt; "
                "dropped from the gene features (not reported) rather than kept with a "
                "wrong span. This means the file's feature table disagrees with its own "
                "ORIGIN block; check the file was not truncated or hand-edited."
            )
            continue

        if is_compound:
            # Sorted ascending by genomic start, not transcript/part order (Biopython
            # writes a minus-strand join()'s parts in transcription order, i.e. highest
            # coordinate first) — a biologist reading the notice wants segments in
            # genomic order regardless of strand.
            parts = sorted((int(p.start) + 1, int(p.end)) for p in loc.parts)
            parts_desc = ", ".join(f"{a}..{b}" for a, b in parts)
            notices.append(
                f"GenBank feature {gene_id!r} has a compound (spliced) location with "
                f"{len(parts)} segments ({parts_desc}). Its reported boundary "
                f"{begin}..{end} is the OUTER SPAN and includes the intron sequence "
                "between segments; a per-gene mean entropy computed over that span is "
                "not exon-only. Use the segment coordinates above if exon-only entropy "
                "is needed (docs/science_and_formats.md)."
            )
        features.append(GeneFeature(begin=begin, end=end, strand=strand, partial=partial, gene_id=gene_id))
    return features, notices


def read_genbank(path: str) -> tuple[list[GenBankRecord], list[str]]:
    """Return ``(records, notices)`` for **every** record in ``path``.

    Features are 1-based inclusive, sequence-relative (matching :class:`GeneFeature`).
    Records whose ORIGIN block is empty (no nucleotides) are skipped with a notice.
    """
    import io

    from Bio import SeqIO  # local import keeps import cost off unrelated paths

    # #330: decode through the same shared encoding.py as every other reader, rather
    # than letting Biopython open the path itself. MEASURED 2026-09-19: handing
    # Biopython a raw path makes it open the file with Python's default text-mode
    # encoding, which is the OS locale encoding, not UTF-8 -- on this Windows box that
    # is cp1252, silently DIFFERENT from readers/fasta.py and readers/paste.py's forced
    # UTF-8, and would decode differently again on a UTF-8-locale Linux box. Decoding
    # ourselves first makes GenBank agree with every other reader and strips a BOM
    # before Biopython's scanner ever sees a 'LOCUS' line.
    text = read_text(path)
    # #349: normalize line endings the same way readers/fasta.py's text.splitlines()
    # already does for free -- a lone '\r' (classic Mac, and what some sequencing
    # instruments still emit) must parse successfully, not merely fail cleanly.
    # Biopython's line-by-line Scanner reads with a real handle's readline(), which does
    # not get Python's universal-newlines treatment the way str.splitlines() does, so it
    # has to be done here, before the text reaches Bio.GenBank.Scanner.
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    try:
        # #349: every malformed-content failure this scanner raises for GenBank/EMBL is
        # documented and observed to be a ValueError (including its own
        # Bio.GenBank.ParserFailureError, itself a ValueError subclass) -- MEASURED
        # 2026-09-19 against a missing-ORIGIN-block fixture and this repo's own test
        # suite. list() is what actually drives the parser, since SeqIO.parse() returns
        # a lazy generator that raises only once iterated.
        parsed = list(SeqIO.parse(io.StringIO(text), "genbank"))
    except ValueError as exc:
        # readers/fasta.py never has this failure class at all (it is hand-rolled, no
        # third-party parser to escape from) -- this is GenBank agreeing with FASTA's
        # blanket guarantee that a malformed file never reaches the caller as a raw
        # traceback, only ever as a *ReadError naming one action.
        raise GenBankReadError(
            f"Could not parse the GenBank file: {exc}. Check the file was not truncated "
            "or hand-edited, and that its ORIGIN block matches its own LOCUS/FEATURES."
        ) from exc
    if not parsed:
        raise GenBankReadError("No GenBank records found in the file.")

    notices: list[str] = []
    records: list[GenBankRecord] = []
    total_features = 0
    for idx, rec in enumerate(parsed, start=1):
        seq = str(rec.seq)
        if not seq or set(seq.upper()) <= {"N"}:
            # Never the record id itself (issue #253) — a GenBank LOCUS/ACCESSION id is
            # free text a user or their sequencing core chose, same privacy class as a
            # FASTA header or a file name.
            notices.append(
                f"Skipped GenBank record {idx} (id {describe_len(str(rec.id))}): no nucleotide sequence."
            )
            continue
        feats, feat_notices = _features_of(rec)
        notices += feat_notices
        total_features += len(feats)
        records.append(GenBankRecord(record_id=rec.id, seq=seq, features=feats))

    if not records:
        raise GenBankReadError("The GenBank file has no records with a nucleotide sequence.")

    # issue #352: `total_features` being 0 already says "no gene features were found" on
    # its own -- a separate "no gene" word produced "0 no gene feature(s)", which reads
    # as broken English rather than the ordinary "0 gene feature(s)" it means.
    notices.append(
        f"Read {len(records)} record(s) with {total_features} gene feature(s) from the "
        "GenBank (not re-annotated)."
    )
    return records, notices
