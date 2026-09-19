- #162: added a corpus-driven fuzz test for the GenBank and FASTA readers.
  `worker/tests/data/malformed/` holds 29 deliberately broken fixtures (empty files,
  binary garbage, truncated mid-record, missing ORIGIN/LOCUS, lone-CR line endings, null
  bytes, an HTML error page saved with a `.fasta`/`.gb` extension, gzip magic bytes, a
  mismatched multi-LOCUS file, and more); `worker/tests/test_fuzz_readers.py`
  parametrizes over every fixture and asserts each produces either a clean
  `FastaReadError`/`GenBankReadError`/`ValidationError` or a clean success, never any
  other exception. `hypothesis` is not installed in `worker\.venv`; per this round's
  brief, nothing was installed to add it, so the property-based half of #162's own
  intent is filed separately as #368 rather than half-done here.
- The fuzz corpus itself doubled as an integration check for every fix landed this
  round: `genbank_bad_coordinates.gb` exercises #331's drop-with-notice and #352's
  corrected grammar together; `genbank_lone_cr.gb` and `genbank_missing_origin.gb`
  exercise #349 directly.
- Mutation-checked: temporarily disabling #349's try/except was caught immediately by
  two fixtures in the corpus (`genbank_missing_origin.gb`,
  `genbank_truncated_mid_feature_table.gb`), proving the fuzz test is a real regression
  guard, not decoration.
- Docs: `docs/tests.md` was named in #162's own body as touched, but is outside this
  lane's owned paths this wave -- not edited; noting it here so it is not missed.
