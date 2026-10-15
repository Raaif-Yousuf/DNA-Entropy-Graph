"""Read a SnapGene ``.dna`` file into the same records as a GenBank file (issue #134).

The binary format is parsed by Biopython's ``snapgene`` SeqIO parser (BSD-3-Clause, already a
dependency), so no new package is added. SnapGene keeps one sequence per file. Its features
(``gene``, ``CDS``, ...) go through the shared ``genbank.records_from_parsed``.

MEASURED 2026-10-15: the parser decodes the DNA packet as ASCII, so a non-ASCII byte raises
``UnicodeDecodeError`` (a ``ValueError``) and is refused here with a named action; there is no
case-folding trap like GenBank's, because nothing upper-cases the bytes first.
"""

from __future__ import annotations

from pathlib import Path

from .genbank import GenBankReadError, GenBankRecord, records_from_parsed

MAGIC = b"SnapGene"
_MAX_BYTES = 64 * 1024 * 1024  # same ceiling the app applies to any staged input


def is_snapgene(head: bytes) -> bool:
    """True if ``head`` (the first bytes of a file) is SnapGene's cookie packet: type 0x09, a
    4-byte length, then ``SnapGene``."""
    return len(head) >= 13 and head[0] == 0x09 and head[5:13] == MAGIC


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
        parsed = list(SeqIO.parse(path, "snapgene"))
    except Exception as exc:  # noqa: BLE001 - Biopython/xml/struct raise several families on bad bytes
        reason = str(exc) or type(exc).__name__
        raise GenBankReadError(
            f"Could not read the SnapGene file ({reason}). Check it opens in SnapGene, or export "
            "it from SnapGene as GenBank and add that file instead."
        ) from exc
    return records_from_parsed(parsed, "SnapGene")
