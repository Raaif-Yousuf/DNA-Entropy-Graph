"""Read a SnapGene ``.dna`` file into the same records as a GenBank file (issue #134).

The binary format is parsed by Biopython's ``snapgene`` SeqIO parser (BSD-3-Clause, already a
dependency), so no new package is added. SnapGene keeps one sequence per file. Its features
(``gene``, ``CDS``, ...) go through the shared ``genbank.records_from_parsed``.

The file is a run of packets (a type byte, a big-endian uint32 length, the data). This module
walks the packets itself first, so a hostile or damaged file is refused with a named action before
Biopython sees a byte: a cut-off packet, an XML packet over the size cap, a DOCTYPE or ENTITY
declaration, a non-ASCII byte in the DNA packet.

MEASURED 2026-10-15: the parser decodes the DNA packet as ASCII, so a non-ASCII byte raises
``UnicodeDecodeError`` (a ``ValueError``); it is refused here by position instead. There is no
case-folding trap like GenBank's, because nothing upper-cases the bytes first.

MEASURED 2026-10-15 (Python 3.12.10, expat 2.7.1): Biopython parses the XML packets with
``xml.dom.minidom.parseString``. This build's expat already stops a billion-laughs document
("limit on input amplification factor", 0.36 s) and a quadratic-blowup one. That protection is a
property of the expat bundled with each Python build, not of this code, so the reader refuses any
DOCTYPE or ENTITY declaration itself rather than relying on it.
"""

from __future__ import annotations

import io
import re
import struct
from pathlib import Path
from xml.parsers.expat import ExpatError

from .genbank import GenBankReadError, GenBankRecord, records_from_parsed

MAGIC = b"SnapGene"
_MAX_BYTES = 64 * 1024 * 1024  # same ceiling the app applies to any staged input
# A real plasmid map's feature, primer and notes XML is kilobytes. Biopython hands each XML packet
# to minidom, so one packet is capped on its own, far below the whole-file cap.
_MAX_XML_PACKET_BYTES = 8 * 1024 * 1024
# SnapGene never writes a DTD. Whitespace is allowed after "<!" because a lenient XML reader would.
_DTD = re.compile(rb"<!\s*(?:doctype|entity)", re.IGNORECASE)
_COOKIE_PACKET = 0x09
_DNA_PACKET = 0x00
_ACTION = "Check it opens in SnapGene, or export it from SnapGene as GenBank and add that file instead."


def is_snapgene(head: bytes) -> bool:
    """True if ``head`` (the first bytes of a file) is SnapGene's cookie packet: type 0x09, a
    4-byte length, then ``SnapGene``."""
    return len(head) >= 13 and head[0] == _COOKIE_PACKET and head[5:13] == MAGIC


def _refuse(reason: str) -> GenBankReadError:
    return GenBankReadError(f"Could not read the SnapGene file ({reason}). {_ACTION}")


def _check_packets(raw: bytes) -> None:
    """Walk the packets and refuse anything unsafe or damaged before Biopython parses them."""
    if not is_snapgene(raw[:13]):
        raise _refuse("it does not start with SnapGene's file header")
    pos = 0
    dna_packets = 0
    while pos < len(raw):
        if pos + 5 > len(raw):
            raise _refuse("a packet is cut off, so the file looks truncated")
        ptype = raw[pos]
        (length,) = struct.unpack(">I", raw[pos + 1 : pos + 5])
        data_start = pos + 5
        if data_start + length > len(raw):
            raise _refuse("a packet is cut off, so the file looks truncated")
        data = raw[data_start : data_start + length]
        pos = data_start + length
        if ptype == _DNA_PACKET:
            dna_packets += 1
            # data[0] is the topology flag byte; the sequence follows.
            for offset, byte in enumerate(data[1:], start=1):
                if byte >= 0x80:
                    raise _refuse(f"the DNA packet has a non-ASCII byte at position {offset}")
        elif ptype != _COOKIE_PACKET:
            if length > _MAX_XML_PACKET_BYTES:
                mib = _MAX_XML_PACKET_BYTES // (1024 * 1024)
                raise _refuse(f"a metadata packet is larger than {mib} MiB, which a plasmid map never is")
            if _DTD.search(data):
                raise _refuse(
                    "a packet contains a DOCTYPE or ENTITY declaration, which SnapGene never writes"
                )
    if dna_packets == 0:
        raise _refuse("it has no DNA sequence packet")


def read_snapgene(path: str) -> tuple[list[GenBankRecord], list[str]]:
    """Return ``(records, notices)`` for a SnapGene file (always exactly one record or an error)."""
    from Bio import SeqIO

    try:
        size = Path(path).stat().st_size
    except OSError as exc:
        raise GenBankReadError(
            "Could not open the SnapGene file. Check the file still exists and try again."
        ) from exc
    if size > _MAX_BYTES:
        raise GenBankReadError(
            "The SnapGene file is larger than 64 MiB, which is not a plasmid map. Export it "
            "from SnapGene as GenBank and add that file instead."
        )
    try:
        raw = Path(path).read_bytes()
    except OSError as exc:
        raise GenBankReadError(
            "Could not open the SnapGene file. Check the file still exists and try again."
        ) from exc
    _check_packets(raw)
    try:
        parsed = list(SeqIO.parse(io.BytesIO(raw), "snapgene"))
    except ExpatError as exc:
        raise _refuse(f"its feature XML is not well formed: {exc}") from exc
    except Exception as exc:  # noqa: BLE001 - Biopython/xml/struct raise several families on bad bytes
        reason = str(exc) or type(exc).__name__
        raise _refuse(reason) from exc
    return records_from_parsed(parsed, "SnapGene")
