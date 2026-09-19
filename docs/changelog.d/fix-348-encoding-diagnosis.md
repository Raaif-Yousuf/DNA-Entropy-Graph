- Fixed #348: `validate_sequence` now special-cases a literal U+FFFD replacement
  character (produced by `readers/encoding.py`'s own `errors="replace"` fallback for a
  byte that was never valid UTF-8) with a distinct error naming the real cause ("the
  file was not saved as UTF-8") and one action (re-save with UTF-8 encoding), instead of
  the generic "Invalid character" wording used for a real typo.
- Docs: `docs/science_and_formats.md` section 4 documents the new diagnosis.
