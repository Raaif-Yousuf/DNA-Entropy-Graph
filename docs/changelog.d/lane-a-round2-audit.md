- Round 2 audit: caught and fixed a Hard Rule 5 (ASCII-safe console output) violation in
  my own #331 notice string before reporting it done -- an em dash that a Windows
  console codepage mangled into `?` at runtime, found by actually running the real CLI
  end to end against the new fixture, not by reading the source.
- Round 2 audit, filed as issues rather than fixed (deeper pass over readers/ and
  validation/, per the shapes named in the round-2 brief): a U+FFFD-from-bad-encoding
  "Invalid character" error that never says it hit an encoding problem (#348); the
  GenBank reader letting Biopython's own parser exceptions escape as raw tracebacks on a
  missing ORIGIN block or lone-CR line endings (#349); `_safe_contig_name` accepting a
  Windows-reserved device name (CON, NUL, PRN, COM1, ...) or an unbounded-length id
  (#350); FASTA duplicate-header detection comparing the full header line instead of
  just the record ID, missing a real ID collision with a different description (#351);
  a grammatically broken "0 no gene feature(s)" summary notice (#352).
