- Fixed #330: a UTF-8 or UTF-16 byte-order mark broke FASTA and GenBank reading outright
  (rejected as "no records found") and a UTF-16 file decoded as UTF-8 turned into a wall
  of `�` replacement characters. Added the single shared
  `worker/src/dna_entropy/readers/encoding.py` that every reader (`paste.py`,
  `fasta.py`, `genbank.py`, `detect.py`) now decodes through: a UTF-8 BOM is stripped, a
  UTF-16 BOM (either byte order) is decoded in full, and everything else falls back to
  UTF-8 with `errors="replace"` (#294's fix, now centralized in one place instead of
  three, so it cannot drift into a fourth, fifth answer).
- Routing GenBank through this same decoder also fixed an unrelated inconsistency:
  Biopython's own file-opening used the OS locale encoding (cp1252 on this Windows box,
  MEASURED 2026-09-19), silently different from FASTA/paste's forced UTF-8, and would
  have decoded differently again on a UTF-8-locale Linux box.
- Docs: `docs/science_and_formats.md` section 4 now describes the shared decoder and
  the GenBank-locale-encoding fix.
