"""Read a pasted sequence from a file or from stdin."""

from __future__ import annotations

import sys
from pathlib import Path


class PasteReader:
    """Reads raw text from a file path, or from stdin when ``path`` is ``None``.

    This is the "copy-paste" input for the demo. It does no cleaning — it just hands the
    raw text to the validation stage.
    """

    def __init__(self, path: str | None = None) -> None:
        self.path = path

    def read(self) -> str:
        # #294: match readers/fasta.py and readers/detect.py, which both decode with
        # errors="replace" rather than raising UnicodeDecodeError on the first bad byte —
        # a pasted file with any non-UTF-8 byte (Windows-1252 export, a stray binary/copy
        # artifact) is common enough on real lab machines that it must reach the normal
        # validation-stage ValidationError, not crash the process outright. Reading stdin
        # via its underlying buffer (rather than the text-mode ``sys.stdin.read()``) makes
        # the paste-from-stdin path agree with the paste-from-file path on this, since
        # both are the same "paste" reader and a biologist piping a file in should not see
        # different crash behaviour than pointing at it by path.
        if self.path is not None:
            return Path(self.path).read_text(encoding="utf-8", errors="replace")
        return sys.stdin.buffer.read().decode("utf-8", errors="replace")
