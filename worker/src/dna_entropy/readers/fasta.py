"""Read a FASTA file into ALL its records.

Hand-rolled (no third-party dep) so the FASTA path works even without Biopython. Design
D14 makes FASTA process every record, the same as GenBank already does — not just the
first, which was the prototype's original shortcut (see issue #283/#47). Returned
sequences are *raw* (not yet validated/uppercased) — validation runs downstream, per
record, like every other input.
"""

from __future__ import annotations

from dataclasses import dataclass

from ..redact import describe_len, fingerprint
from .encoding import read_text


class FastaReadError(ValueError):
    """Raised when a FASTA file has no usable sequence records."""


@dataclass
class FastaRecordRaw:
    """One raw (unvalidated) record from a FASTA file."""

    header: str  # text after '>' on the header line, verbatim (may be empty)
    seq: str  # joined sequence lines, raw (not yet uppercased/validated)


def read_fasta(path: str) -> tuple[list[FastaRecordRaw], list[str]]:
    """Return ``(records, notices)`` for EVERY record in ``path``.

    A record whose header line is followed by no sequence lines at all (empty after
    joining) is skipped with a notice, mirroring ``readers.genbank.read_genbank``'s
    handling of an empty ORIGIN block. Missing (bare ``>``) or duplicated headers are
    tolerated — they never block reading or collide as *contig* names, since
    ``readers.input._safe_contig_name`` numbers contigs by position, not by header text —
    but both are flagged with a notice since a biologist looking at the input may not
    have intended them.
    """
    notices: list[str] = []
    parsed: list[tuple[str, list[str]]] = []  # (header, sequence-lines)
    text = read_text(path)  # #330: shared BOM/encoding handling, see readers/encoding.py

    header: str | None = None
    lines: list[str] = []
    for line in text.splitlines():
        if line.startswith(">"):
            if header is not None:
                parsed.append((header, lines))
            header = line[1:].strip()
            lines = []
        elif header is not None:
            lines.append(line.strip())
    if header is not None:
        parsed.append((header, lines))

    if not parsed:
        raise FastaReadError("No FASTA records found (expected a '>' header line).")

    records: list[FastaRecordRaw] = []
    skipped = 0
    for idx, (rec_header, seq_lines) in enumerate(parsed, start=1):
        seq = "".join(seq_lines)
        if not seq:
            skipped += 1
            # Never the header text itself (issue #253: a log artifact must not leak
            # anything a user typed) — just which record, and how long its header was.
            header_desc = describe_len(rec_header) if rec_header else "no header text"
            notices.append(f"Skipped FASTA record {idx} ({header_desc}): no sequence lines after its header.")
            continue
        records.append(FastaRecordRaw(header=rec_header, seq=seq))

    if not records:
        raise FastaReadError("The FASTA file has no records with a sequence.")

    n_missing_header = sum(1 for r in records if not r.header)
    if n_missing_header:
        notices.append(f"{n_missing_header} record(s) have an empty header line (a bare '>' with no name).")

    # issue #351: key on the record ID -- the header up to its first whitespace, the
    # part every other FASTA-consuming tool (BLAST, samtools, IGV) actually treats as
    # the sequence's identifier -- not the full header line. Two records sharing an ID
    # but carrying different free-text descriptions (">seq1 first"/">seq1 second") are
    # a genuine ID collision from every downstream tool's perspective; comparing full
    # header lines missed exactly this shape.
    seen: dict[str, int] = {}
    for r in records:
        if r.header:
            rec_id = r.header.split(None, 1)[0]
            seen[rec_id] = seen.get(rec_id, 0) + 1
    dupes = sorted(h for h, n in seen.items() if n > 1)
    if dupes:
        # fingerprint(), not the id text itself (issue #253) — enough to correlate
        # "these two records share an id" without ever writing the id out.
        shown = ", ".join(fingerprint(d) for d in dupes[:5])
        more = "..." if len(dupes) > 5 else ""
        notices.append(
            f"{len(dupes)} id(s) repeat across records (fingerprints: {shown}{more}); "
            "records are still kept and numbered separately."
        )

    if len(records) > 1:
        notices.append(f"Read {len(records)} record(s) from the FASTA (all processed).")

    return records, notices
