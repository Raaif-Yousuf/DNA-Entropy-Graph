"""Read a pasted sequence from a file or from stdin."""

from __future__ import annotations

import sys

from .encoding import decode_bytes, read_text


class PasteReader:
    """Reads raw text from a file path, or from stdin when ``path`` is ``None``.

    This is the "copy-paste" input for the demo. It does no cleaning — it just hands the
    raw text to the validation stage.
    """

    def __init__(self, path: str | None = None) -> None:
        self.path = path

    def read(self) -> str:
        # #294/#330: decode through the one shared encoding.py so a non-UTF-8 byte
        # (Windows-1252 export, a stray binary/copy artifact) reaches the normal
        # validation-stage ValidationError instead of crashing, and a UTF-8/UTF-16 BOM
        # is handled instead of surviving as a literal character or a wall of U+FFFD.
        # Reading stdin via its underlying buffer (rather than the text-mode
        # ``sys.stdin.read()``) makes the paste-from-stdin path agree with the
        # paste-from-file path on this, since both are the same "paste" reader and a
        # biologist piping a file in should not see different behaviour than pointing at
        # it by path.
        if self.path is not None:
            return read_text(self.path)
        return decode_bytes(sys.stdin.buffer.read())
