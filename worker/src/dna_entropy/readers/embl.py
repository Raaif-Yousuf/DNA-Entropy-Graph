"""Read an EMBL flat file into the same records as a GenBank file (issue #134).

Biopython parses the format; features and notices are mapped by the one shared
``genbank.records_from_parsed``, so a record read from EMBL and the same record read from its
GenBank export give the same genes. Decoding goes through the shared ``encoding.py`` and line
endings are normalised before Biopython sees them, exactly like the GenBank reader.
"""

from __future__ import annotations

import io

from .encoding import read_text
from .genbank import GenBankReadError, GenBankRecord, records_from_parsed


def _refuse_non_ascii_sequence(text: str) -> None:
    """Refuse a non-ASCII character in any record's ``SQ`` block, before Biopython upper-cases it
    (the same trap as GenBank issue #492: ``str.upper()`` folds long s U+017F into a valid ``S``).
    Positions are 1-based over the record's own sequence characters; spaces, tabs and ASCII digits
    (the layout and the trailing position counts) are skipped."""
    record = 1
    position = 0
    in_seq = False
    for line in text.split("\n"):
        if line.startswith("//"):
            record += 1
            position = 0
            in_seq = False
        elif line.startswith("SQ"):
            in_seq = True
        elif in_seq and line[:1].isspace():
            for char in line:
                if char in " \t" or (char.isascii() and char.isdigit()):
                    continue
                position += 1
                if not char.isascii():
                    raise GenBankReadError(
                        f"Invalid character U+{ord(char):04X} at position {position} of EMBL "
                        f"record {record}'s sequence (its SQ block). Only A, C, G, T are "
                        "allowed. Remove or correct that character in the file and try again."
                    )
        elif in_seq:
            in_seq = False


def read_embl(path: str) -> tuple[list[GenBankRecord], list[str]]:
    """Return ``(records, notices)`` for every record in an EMBL file."""
    from Bio import SeqIO

    text = read_text(path).replace("\r\n", "\n").replace("\r", "\n")
    _refuse_non_ascii_sequence(text)
    parsed: list = []
    try:
        for rec in SeqIO.parse(io.StringIO(text), "embl"):
            parsed.append(rec)
    except (ValueError, AssertionError, IndexError, KeyError) as exc:
        raise GenBankReadError(
            f"Could not parse the EMBL file (record {len(parsed) + 1}): "
            f"{str(exc) or type(exc).__name__}. Check the file was not truncated or hand-edited, "
            "or re-export it from the tool that made it."
        ) from exc
    return records_from_parsed(parsed, "EMBL")
