- Fixed #294: `PasteReader.read()` crashed with `UnicodeDecodeError` on any non-UTF-8 byte
  in a pasted file or on stdin. Both branches now decode with `errors="replace"`, matching
  `readers/fasta.py` and `readers/detect.py`'s existing behaviour, so a bad byte reaches the
  normal validation-stage checks instead of crashing the process.
