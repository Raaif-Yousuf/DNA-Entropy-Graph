- Fixed #349: `read_genbank` wraps Biopython's own parser (`Bio.GenBank.Scanner`, whose
  every malformed-content failure mode is a `ValueError` or a subclass of one) in a
  `try/except`, re-raising as `GenBankReadError` -- agreeing with `read_fasta`'s own
  blanket guarantee that a malformed file never reaches the caller as a raw traceback.
  Line endings are also normalized before the text reaches Biopython's scanner, matching
  `read_fasta`'s `text.splitlines()`: a lone `\r` (classic Mac, some sequencing
  instruments) now parses successfully instead of merely failing cleanly.
- Docs: `docs/science_and_formats.md` section 4 documents both fixes.
