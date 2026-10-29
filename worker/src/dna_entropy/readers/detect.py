"""Detect the input file kind so the pipeline can route it to the right reader.

Order of evidence: file extension first, then a content sniff of the first non-blank line.
``None`` (stdin/paste) is always ``"paste"``.
"""

from __future__ import annotations

from pathlib import Path

from .encoding import read_text
from .snapgene import is_snapgene

# Source kinds used throughout the pipeline.
GENBANK = "genbank"
FASTA = "fasta"
PASTE = "paste"
EMBL = "embl"  # issue #134: read like GenBank (an annotated flat file)
SNAPGENE = "snapgene"  # issue #134: binary .dna, read like GenBank

_GENBANK_EXT = frozenset({".gb", ".gbk", ".genbank", ".gbff"})
_EMBL_EXT = frozenset({".embl", ".emb"})
_FASTA_EXT = frozenset({".fa", ".fasta", ".fna", ".ffn"})


def detect_kind(path: str | None) -> str:
    """Return ``"genbank"``, ``"fasta"``, or ``"paste"`` for ``path`` (None => paste)."""
    if path is None:
        return PASTE
    ext = Path(path).suffix.lower()
    # issue #134: SnapGene is binary, so the magic bytes decide, whatever the extension says
    # (a renamed file still works; a ".dna" that is plain text is not claimed).
    try:
        with open(path, "rb") as fh:
            if is_snapgene(fh.read(13)):
                return SNAPGENE
    except OSError:
        pass  # unreadable here: fall through to the extension, the later open reports the real error
    if ext in _GENBANK_EXT:
        return GENBANK
    if ext in _EMBL_EXT:
        return EMBL
    if ext in _FASTA_EXT:
        return FASTA
    # Unknown extension: sniff the first non-blank line. #330: decode through the same
    # shared encoding.py as every other reader, so a BOM-prefixed file sniffs correctly
    # instead of falling through to PASTE because the BOM defeated the checks below.
    try:
        head = read_text(path)
    except OSError:
        return PASTE
    for line in head.splitlines():
        s = line.strip()
        if not s:
            continue
        if s.startswith("LOCUS"):
            return GENBANK
        if s.startswith("ID   "):
            return EMBL
        if s.startswith(">"):
            return FASTA
        break
    return PASTE
