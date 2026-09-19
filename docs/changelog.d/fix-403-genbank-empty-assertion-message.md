- Fixed #403: `readers/genbank.py`'s `GenBankReadError` message had an empty gap
  ("Could not parse the GenBank file: . Check...") whenever Biopython's scanner raised a
  bare, message-less `AssertionError` (issue #368's own regression fixture,
  `genbank_qualifier_missing_slash.gb`, is exactly this case). New helper
  `_describe_bare_assertion` recovers a true, non-empty reason from the exception's own
  traceback: for the one shape actually reachable through malformed GenBank content (a
  feature qualifier continuation line missing its leading `/`, `Scanner.py`'s
  `assert len(qualifiers) > 0` / `assert key == qualifiers[-1][0]`), it names the real,
  checkable cause; for any other bare assertion, it falls back to naming the internal
  check that failed (still concrete, never silent). A `ValueError` (which already carries
  real text from Biopython) is unaffected.
- Mutation-checked: temporarily skipping `_describe_bare_assertion` reproduces the empty
  "Could not parse the GenBank file: . " gap on `genbank_qualifier_missing_slash.gb`;
  restoring the fix turns it back into a named reason.
