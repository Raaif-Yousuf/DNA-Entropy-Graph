- Issue #368 (parent #162): added the property-based (Hypothesis) half of the GenBank/
  FASTA reader fuzzing `test_fuzz_readers.py`'s own docstring named as missing. New file
  `worker/tests/test_property_fuzz_readers.py`, generating over two strategies named in
  #368's own body: arbitrary bytes (`st.binary()`), and mutated real files (a
  `mutated_bytes()` `@st.composite` strategy applying random byte flips, deletions,
  insertions, truncations and encoding swaps to `worker/tests/data/sample.fasta`/
  `sample.gb`). Property under test matches `test_fuzz_readers.py`'s own bar exactly: every
  generated input either parses to a genuine, non-empty record set or raises one of the
  three clean, registered exception types (`FastaReadError`/`GenBankReadError`/
  `ValidationError`), never any other exception, and a clean success is never a silently
  empty record list or a record with a `None`/empty sequence.
- **Real bug found and fixed** (MEASURED 2026-09-19, via the mutated-file property on
  `sample.gb`): a feature-qualifier continuation line missing its leading `/` (e.g.
  `gene="geneB"` instead of `/gene="geneB"`) drives `Bio.GenBank.Scanner`'s internal
  feature-table parser into one of its own bare `assert` statements
  (`assert len(qualifiers) > 0` / `assert key == qualifiers[-1][0]`), which raises a raw
  `AssertionError`, not a `ValueError`. `readers/genbank.py`'s `except ValueError` clause
  did not catch it, so `read_genbank` leaked a raw, unnamed exception instead of a clean
  `GenBankReadError` -- exactly the bug class #162/#368 exist to prevent. Fixed by widening
  the except clause to `(ValueError, AssertionError)`. This DISPROVES issue #349's own
  landed claim ("every malformed-content failure this scanner raises for GenBank/EMBL is
  ... observed to be a ValueError") -- Hard Rule 18: the disproven claim was replaced in
  place in `readers/genbank.py`'s own comment, not left beside a correction. Permanent
  regression fixture added: `worker/tests/data/malformed/genbank_qualifier_missing_slash.gb`
  (auto-collected by `test_fuzz_readers.py`'s existing `GENBANK_FIXTURES` glob, no test
  code change needed). Mutation/revert-checked: reverting the except clause to
  `ValueError`-only reproduces the raw `AssertionError` on this exact fixture; restoring the
  widened clause turns it back into a clean `GenBankReadError`.
- `hypothesis` licence and dependency-addition details: see
  `docs/changelog.d/test-367-property-windowing-direction.md`.
- `.gitattributes`' `worker/tests/data/malformed/** -text` rule already covers the new
  fixture (checked, not re-created); the new fixture is plain ASCII with LF line endings,
  so it carries no CRLF/byte-integrity risk itself, but it lives under the same glob the
  rule already protects.
- `docs/tests.md` updated to name the new property test file, its two generation
  strategies, and its budget.
