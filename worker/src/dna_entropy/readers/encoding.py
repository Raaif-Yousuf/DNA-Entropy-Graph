"""The single point of encoding/BOM detection for every text-reading input path.

Issue #330: ``readers/fasta.py``, ``readers/genbank.py`` (via Biopython opening the path
itself) and ``readers/detect.py``'s content sniff each decoded raw bytes independently.
A UTF-8 BOM defeated the ``'>'``/``'LOCUS'`` checks in three different places (the BOM
decodes to a literal U+FEFF character that ``str.startswith()`` does not see through),
so a perfectly valid file was rejected outright. A UTF-16 file -- exactly what Windows
Notepad's "Unicode" save option produces -- decoded as UTF-8 turned into a wall of
U+FFFD replacement characters with no explanation once the #294 fix stopped it from
crashing instead.

Every reader in this package decodes through :func:`decode_bytes`/:func:`read_text`
below, never ``Path.read_text()`` directly and never a raw path handed to a third-party
parser, so this logic has exactly one place to drift from, not four.
"""

from __future__ import annotations

from pathlib import Path

_UTF16_BOMS = (b"\xff\xfe", b"\xfe\xff")
_UTF8_BOM = b"\xef\xbb\xbf"


def decode_bytes(raw: bytes) -> str:
    """Decode raw file/stream bytes into text, honouring a byte-order mark if present.

    - A UTF-16 BOM (either byte order) means the file genuinely IS UTF-16. Decoding it
      in full costs nothing extra over refusing it, and a clean refusal would be the
      wrong call here: this is exactly what "Save As... Encoding: Unicode" produces in
      Notepad on Windows, an ordinary and unremarkable way to save a plain-text file, not
      a corrupt one. ``errors="replace"`` still guards a truncated/corrupt UTF-16 file.
    - A UTF-8 BOM is stripped (``"utf-8-sig"``) before anything else ever sees the text,
      so the first real character is the line's actual first character.
    - Anything else decodes as UTF-8 with ``errors="replace"`` (issue #294): an
      undecodable byte becomes a single U+FFFD rather than crashing the process, and
      reaches the normal validation-stage character checks instead.
    """
    if raw[:2] in _UTF16_BOMS:
        return raw.decode("utf-16", errors="replace")
    if raw[:3] == _UTF8_BOM:
        return raw.decode("utf-8-sig", errors="replace")
    return raw.decode("utf-8", errors="replace")


def read_text(path: str) -> str:
    """Read ``path`` from disk and decode it via :func:`decode_bytes`."""
    return decode_bytes(Path(path).read_bytes())
