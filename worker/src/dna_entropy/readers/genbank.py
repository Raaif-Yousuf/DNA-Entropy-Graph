"""Read a GenBank file into its records: sequence + EXISTING gene features, per record.

Key rule (professor's brief): when the input is a GenBank, we **use its genes as-is** and
never re-annotate. A GenBank may hold **multiple records** (the ``SetTnpB-Evo.gb`` test file
has five); we return **all** of them so the pipeline processes each. This reader extracts
each record's sequence and maps its ``gene`` (or, failing that, ``CDS``) features onto our
:class:`GeneFeature` type. Biopython does the parsing.
"""

from __future__ import annotations

import re
import traceback
import warnings
from dataclasses import dataclass, field

from ..annotators.base import GeneFeature
from ..redact import describe_len
from ..validation.validators import ValidationError
from .encoding import read_text


class GenBankReadError(ValidationError):
    """Raised when a GenBank file has no usable sequence record.

    Issue #391: subclasses :class:`~dna_entropy.validation.validators.ValidationError`
    (itself a `ValueError`), not `ValueError` directly, so that
    `worker/runner.py`'s existing `isinstance(exc, (ValidationError, ...))` classifier
    -- unedited by this change, since `isinstance` is structural -- reports
    `INPUT_INVALID` ("your file is invalid") rather than falling through to the generic
    `WORKER_CRASH` ("the worker crashed"). The real cause of a malformed GenBank is
    exactly the situation `ValidationError` already exists to report correctly.
    """


def _record_sequence(rec) -> str:
    """The record's bases, or ``""`` when it has none to read.

    Issue #536, MEASURED 2026-10-03: a truncated file (a LOCUS line declaring a length, an
    ORIGIN header, and no sequence lines) parses into a record whose ``seq`` is an
    UNDEFINED-content sequence of the declared length, and ``str()`` on it raises Biopython's
    own ``UndefinedSequenceError`` (a ``ValueError``) OUTSIDE the parse try-block above, so it
    used to escape as a raw exception. It is the same situation as an empty ORIGIN: no
    nucleotides, so the caller skips the record with a notice, and a file with no readable
    record at all is then refused with :class:`GenBankReadError` (``INPUT_INVALID``), exactly
    like the app's ``GenBankLite`` (skip, then refuse when nothing is left).
    """
    from Bio.Seq import UndefinedSequenceError

    try:
        return str(rec.seq)
    except UndefinedSequenceError:
        return ""


def _refuse_non_ascii_origin(text: str) -> None:
    """Refuse a non-ASCII character in any record's ORIGIN block, before Biopython sees it.

    Issue #492, MEASURED 2026-10-02: Biopython upper-cases the ORIGIN text itself, and
    ``str.upper()`` folds some non-ASCII letters into ASCII (long s U+017F becomes ``S``, a
    valid IUPAC code), so ``validate_sequence`` downstream never saw the original letter
    and the run accepted a character that is not DNA. Scanning the raw text here, on the
    same ``\\n``-normalised lines Biopython reads, is the only place the original survives.

    The position matches ``validate_sequence``'s ("Invalid character U+XXXX at position N"):
    1-based, counted over the record's own sequence characters, so the position numbers
    GenBank prints at the start of each ORIGIN line and the spacing between 10-base groups
    are skipped, exactly as Biopython skips them. Only ASCII spaces, tabs and digits are
    skipped: a non-ASCII space or digit is a non-ASCII character and is refused too, never
    silently treated as layout.
    """
    record = 1
    position = 0
    in_origin = False
    for line in text.split("\n"):
        if line.startswith("//"):
            record += 1
            position = 0
            in_origin = False
        elif line.startswith("ORIGIN"):
            in_origin = True
        elif in_origin and not line[:1].isspace() and line[:1]:
            in_origin = False  # a new section keyword (CONTIG, LOCUS, ...) ends the sequence block
        elif in_origin:
            for char in line:
                if char in " \t" or (char.isascii() and char.isdigit()):
                    continue
                position += 1
                if not char.isascii():
                    raise GenBankReadError(
                        f"Invalid character U+{ord(char):04X} at position {position} of GenBank "
                        f"record {record}'s sequence (its ORIGIN block). Only A, C, G, T are "
                        "allowed. Remove or correct that character in the file and try again."
                    )


def _describe_bare_assertion(exc: AssertionError) -> str:
    """Recover a true, non-empty reason from a bare (message-less) `AssertionError`.

    Issue #403: several of `Bio.GenBank.Scanner`'s internal `assert` statements carry no
    message (e.g. `assert len(qualifiers) > 0`, `assert key == qualifiers[-1][0]` in its
    feature-table parser), so `str(exc)` is `""` and a naive `f"...: {exc}."` wrap left a
    fact-free gap: `'Could not parse the GenBank file: . Check...'`. Hard Rule 13's
    principle -- every error names one action the user can take -- means silence here is
    not acceptable even though the *next* sentence already names an action; the reason
    slot itself must say something true.

    Biopython's own internal state (which INPUT line it had reached) is not exposed to a
    caller of the high-level `Bio.SeqIO.parse()`, so this cannot recover the user's exact
    line number. What IS recoverable for free, via the exception's own traceback, is the
    Biopython SOURCE line the assertion fired on. For the one shape #368's own Hypothesis
    fuzzing actually found reachable through malformed GenBank content (a feature
    qualifier continuation line missing its leading `/`), that source line lets us name
    the real, checkable cause directly. For any other bare assertion, naming the internal
    check itself is still strictly better than silence -- concrete enough to search or to
    paste into a bug report, even though it is Biopython's code, not the user's file.
    """
    frames = traceback.extract_tb(exc.__traceback__)
    biopython_frames = [f for f in frames if "Bio" in f.filename and f.line]
    source_line = biopython_frames[-1].line if biopython_frames else None

    if source_line and "qualifiers" in source_line:
        return (
            "a feature qualifier line appears to be missing its leading '/' "
            "(Biopython expected a continuation of the previous qualifier)"
        )
    if source_line:
        return f"an internal GenBank format check failed ({source_line!r})"
    return "an internal GenBank format check failed with no further detail available"


@dataclass
class GenBankRecord:
    """One record from a GenBank file: its id, sequence, and pre-existing gene features."""

    record_id: str
    seq: str
    features: list[GeneFeature] = field(default_factory=list)
    circular: bool = False  # the LOCUS line says "circular" (issue #128)


def _feature_id(feature) -> str:
    """Pick a stable label for a feature from its qualifiers."""
    # "label" last: SnapGene (issue #134) names a feature by its label, GenBank rarely carries one.
    for key in ("gene", "locus_tag", "product", "label"):
        vals = feature.qualifiers.get(key)
        if vals:
            return str(vals[0])
    return feature.type


_DROPPED_LOCATION_WARNING = "setting feature location to None"
_DROPPED_SHOWN = 3  # how many dropped features a notice spells out before saying "and N more"
_LOCATION_TEXT_MAX = 40  # a location text longer than this is cut in the notice


def _location_text_of_warning(message: str) -> str | None:
    """The quoted location text in one of Biopython's "setting feature location to None"
    warnings (``Could not parse feature location 'abc'; ...``), ``""`` if it carries no quote, or
    ``None`` when the message is some other warning. The text is the one quoted span (greedy, so an
    apostrophe inside the location survives)."""
    if _DROPPED_LOCATION_WARNING not in message:
        return None
    match = re.search(r"'(.*)'", message)
    return match.group(1) if match else ""


def _shown_location_text(text: str) -> str:
    """The location text as it may appear in a notice: cut, then ``ascii()`` so a non-ASCII
    character is escaped (Hard Rule 5: the notice reaches the console)."""
    cut = text if len(text) <= _LOCATION_TEXT_MAX else text[: _LOCATION_TEXT_MAX - 3] + "..."
    return ascii(cut)


def _dropped_location_notice(
    record, source: list, location_texts: list[str] | None, label: str, index: int
) -> str | None:
    """Issue #543: Biopython sets the location of a feature it cannot parse (``abc``, ``0..42``,
    ``42..1`` on a non-circular record, ``1..?``) to ``None`` and only warns, so the feature used
    to vanish from the exported tracks with no message.

    One notice per record. It counts only the features in ``source``, the ones that would have
    become genes (a record's ``gene`` features, else its ``CDS`` features), so a CDS that was never
    going to be a gene is not reported as lost. ``location_texts`` are the warnings' quoted texts
    in feature order, one per feature whose location is ``None`` anywhere in the record; they are
    paired with the features only when the counts agree, otherwise the feature key alone is named
    (a wrong text is worse than none)."""
    unreadable = [f for f in record.features if f.location is None]
    paired = location_texts if location_texts is not None and len(location_texts) == len(unreadable) else None
    texts = {id(f): paired[i] for i, f in enumerate(unreadable)} if paired is not None else {}
    dropped = [f for f in source if f.location is None]
    if not dropped:
        return None
    named = [f"{f.type} {_shown_location_text(texts[id(f)])}" if id(f) in texts else f.type for f in dropped]
    kinds = " and ".join(sorted({f.type for f in dropped}))
    shown = ", ".join(named[:_DROPPED_SHOWN])
    more = f", and {len(named) - _DROPPED_SHOWN} more" if len(named) > _DROPPED_SHOWN else ""
    return (
        f"{label} record {index}: {len(dropped)} {kinds} feature(s) were dropped because their "
        f"location text could not be read ({shown}{more}). A gene with an unreadable location is "
        "missing from the exported tracks. Correct the location in the source file (for example "
        "1..42 or complement(85..126)) and run again."
    )


def parse_recording_dropped_locations(text: str, fmt: str):
    """Parse ``text`` as ``fmt`` (``"genbank"`` or ``"embl"``) with Biopython, yielding
    ``(record, location_texts)`` per record, where ``location_texts`` is the quoted location of
    every "setting feature location to None" warning raised while that record was read, in feature
    order (issue #543). Every OTHER warning is re-emitted unchanged, even when the record's parse
    raises, so nothing else Biopython says is swallowed. Exceptions propagate to the caller."""
    import io

    from Bio import SeqIO

    iterator = iter(SeqIO.parse(io.StringIO(text), fmt))
    while True:
        texts: list[str] = []
        record = None
        try:
            with warnings.catch_warnings(record=True) as caught:
                warnings.simplefilter("always")
                record = next(iterator, None)
        finally:
            for w in caught:
                location_text = _location_text_of_warning(str(w.message))
                if location_text is None:
                    warnings.warn_explicit(w.message, w.category, w.filename, w.lineno)
                else:
                    texts.append(location_text)
        if record is None:
            return
        yield record, texts


def _features_of(
    record, label: str = "GenBank", location_texts: list[str] | None = None, index: int = 1
) -> tuple[list[GeneFeature], list[str]]:
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
    circular = record.annotations.get("topology") == "circular"

    features: list[GeneFeature] = []
    notices: list[str] = []
    dropped_notice = _dropped_location_notice(record, source, location_texts, label, index)
    if dropped_notice:
        notices.append(dropped_notice)
    for f in source:
        loc = f.location
        if loc is None:
            continue  # reported by the notice above (issue #543), never silently
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
                f"{label} feature {gene_id!r} has coordinates {begin}..{end}, which fall "
                f"outside record {record.id!r}'s own sequence length of {seq_len} nt; "
                "dropped from the gene features (not reported) rather than kept with a "
                "wrong span. This means the file's feature table disagrees with its own "
                "ORIGIN block; check the file was not truncated or hand-edited."
            )
            continue

        segments: tuple[tuple[int, int], ...] = ()
        if is_compound:
            # Sorted ascending by genomic start, not transcript/part order (Biopython
            # writes a minus-strand join()'s parts in transcription order, i.e. highest
            # coordinate first) — a biologist reading the notice wants segments in
            # genomic order regardless of strand.
            parts = sorted((int(p.start) + 1, int(p.end)) for p in loc.parts)
            segments = tuple(parts)  # issue #124: the gene table summarises these, not the outer span
            parts_desc = ", ".join(f"{a}..{b}" for a, b in parts)
            # Same rule as analysis/gene_summary.py::_wraps_origin, which is what reads these
            # segments: a circular record with one part ending at the last base and another
            # starting at base 1 is a gene crossing the origin, not a spliced gene.
            wraps = (
                circular
                and len(parts) > 1
                and any(b == 1 for b, _ in parts)
                and any(e == seq_len for _, e in parts)
            )
            if wraps:
                notices.append(
                    f"{label} feature {gene_id!r} wraps the origin of this circular record, "
                    f"in {len(parts)} segments ({parts_desc}). Its reported boundary "
                    f"{begin}..{end} is the OUTER SPAN, the whole molecule; the gene table "
                    "uses the segment coordinates above, so its length and mean entropy cover "
                    "only the gene's own bases (docs/science_and_formats.md)."
                )
            else:
                notices.append(
                    f"{label} feature {gene_id!r} has a compound (spliced) location with "
                    f"{len(parts)} segments ({parts_desc}). Its reported boundary "
                    f"{begin}..{end} is the OUTER SPAN and includes the intron sequence "
                    "between segments; a per-gene mean entropy computed over that span is "
                    "not exon-only. Use the segment coordinates above if exon-only entropy "
                    "is needed (docs/science_and_formats.md)."
                )
        features.append(
            GeneFeature(
                begin=begin, end=end, strand=strand, partial=partial, gene_id=gene_id, segments=segments
            )
        )
    return features, notices


def read_genbank(path: str) -> tuple[list[GenBankRecord], list[str]]:
    """Return ``(records, notices)`` for **every** record in ``path``.

    Features are 1-based inclusive, sequence-relative (matching :class:`GeneFeature`).
    Records whose ORIGIN block is empty (no nucleotides) are skipped with a notice.
    """
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
    _refuse_non_ascii_origin(text)
    parsed: list = []
    dropped_texts: list[list[str]] = []  # per parsed record, parallel to ``parsed`` (issue #543)
    try:
        # #349's own claim here ("every malformed-content failure this scanner raises for
        # GenBank/EMBL is documented and observed to be a ValueError") is DISPROVEN
        # (Hard Rule 18: replacing the claim rather than leaving it beside a correction) --
        # MEASURED 2026-09-19 (issue #368's Hypothesis property fuzzing, mutating a real
        # fixture): a feature qualifier continuation line missing its leading '/' (e.g.
        # `gene="geneB"` instead of `/gene="geneB"`) drives Bio.GenBank.Scanner into one of
        # its own internal bare `assert` statements (Scanner.py's feature-table parser has
        # several: `assert len(qualifiers) > 0`, `assert key == qualifiers[-1][0]`, and
        # others elsewhere in the same module), which raises a raw `AssertionError`, not a
        # `ValueError` -- see worker/tests/data/malformed/genbank_qualifier_missing_slash.gb
        # and test_fuzz_readers.py's corpus-driven regression test for this exact fixture.
        # `AssertionError` is caught alongside `ValueError` for exactly the same reason
        # `ValueError` is: it is Biopython's OWN signal that the input violates an
        # assumption its scanner makes, not a genuine internal-logic-error class this code
        # would want to keep visible as a crash. list() is what actually drives the parser,
        # since SeqIO.parse() returns a lazy generator that raises only once iterated.
        # Iterated by hand (not list()) so the failing record's position is known: the
        # generator raises while reading record `len(parsed) + 1`. Named by INDEX, never by
        # id (issue #253: a record id is user free text).
        # Issue #543: parse_recording_dropped_locations also returns each record's unreadable
        # feature locations (Biopython only warns about them) so the notice can name them.
        for parsed_record, location_texts in parse_recording_dropped_locations(text, "genbank"):
            parsed.append(parsed_record)
            dropped_texts.append(location_texts)
    except (ValueError, AssertionError, IndexError) as exc:
        # readers/fasta.py never has this failure class at all (it is hand-rolled, no
        # third-party parser to escape from) -- this is GenBank agreeing with FASTA's
        # blanket guarantee that a malformed file never reaches the caller as a raw
        # traceback, only ever as a *ReadError naming one action.
        # #403: str(exc) is empty for a bare AssertionError, which used to leave this
        # message reading "Could not parse the GenBank file: . Check..." -- a fact-free
        # gap. _describe_bare_assertion recovers a true, non-empty reason in that case;
        # a ValueError already carries real text from Biopython, so it passes through.
        # Issue #536, MEASURED 2026-10-03 (a seeded mutation sweep of sample.gb: three
        # seeds x 6000 files, the ONLY non-reader exception type seen was IndexError): a
        # feature line shorter than the scanner's qualifier column indexes past its end in
        # Scanner.parse_features. Its text ("string index out of range") is a Python
        # internal, not a fact about the file, so it is replaced with one the user can check.
        if isinstance(exc, IndexError):
            reason = (
                "a feature-table line is shorter than the GenBank layout allows "
                "(a misaligned feature key or location)"
            )
        elif isinstance(exc, AssertionError) and not str(exc):
            reason = _describe_bare_assertion(exc)
        else:
            reason = str(exc)
        raise GenBankReadError(
            f"Could not parse the GenBank file (record {len(parsed) + 1}): {reason}. Check the "
            "file was not truncated or hand-edited, and that its ORIGIN block matches its own "
            "LOCUS/FEATURES."
        ) from exc
    return records_from_parsed(parsed, "GenBank", dropped_texts)


def records_from_parsed(
    parsed: list, label: str, dropped_texts: list[list[str]] | None = None
) -> tuple[list[GenBankRecord], list[str]]:
    """Turn Biopython ``SeqRecord``s into ``(records, notices)``; shared by the GenBank, EMBL and
    SnapGene readers (issue #134), so all three map features identically. ``label`` is the format
    name used in user-facing text ("GenBank" keeps the original wording byte for byte)."""
    if not parsed:
        raise GenBankReadError(f"No {label} records found in the file.")

    notices: list[str] = []
    records: list[GenBankRecord] = []
    total_features = 0
    for idx, rec in enumerate(parsed, start=1):
        seq = _record_sequence(rec)
        if not seq or set(seq.upper()) <= {"N"}:
            # Never the record id itself (issue #253) — a GenBank LOCUS/ACCESSION id is
            # free text a user or their sequencing core chose, same privacy class as a
            # FASTA header or a file name.
            notices.append(
                f"Skipped {label} record {idx} (id {describe_len(str(rec.id))}): no nucleotide sequence."
            )
            continue
        texts = dropped_texts[idx - 1] if dropped_texts is not None else None
        feats, feat_notices = _features_of(rec, label, texts, idx)
        notices += feat_notices
        total_features += len(feats)
        records.append(
            GenBankRecord(
                record_id=rec.id,
                seq=seq,
                features=feats,
                # Biopython reads the LOCUS line's topology word into annotations.
                circular=rec.annotations.get("topology") == "circular",
            )
        )

    if not records:
        raise GenBankReadError(
            f"The {label} file has no records with a nucleotide sequence (a truncated or "
            "sequence-less export). Re-export the file from the tool that made it, with its "
            "sequence included."
        )

    # issue #352: `total_features` being 0 already says "no gene features were found" on
    # its own -- a separate "no gene" word produced "0 no gene feature(s)", which reads
    # as broken English rather than the ordinary "0 gene feature(s)" it means.
    notices.append(
        f"Read {len(records)} record(s) with {total_features} gene feature(s) from the "
        f"{label} (not re-annotated)."
    )
    return records, notices
