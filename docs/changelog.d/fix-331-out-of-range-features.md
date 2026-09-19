- Fixed #331: a GenBank gene feature whose coordinates fell outside its own record's
  sequence length (a feature-table/ORIGIN mismatch -- truncation, hand-editing,
  corruption) was accepted silently, and would have let `writers/genbank.py` slice a
  wrong, out-of-range span for its mean-entropy note. `readers/genbank.py` now drops
  such a feature with a loud notice naming the gene id, its coordinates, and the
  record's real length -- rather than a hard failure for the whole record, so the
  record's other genes are still reported.
- Two deliberate exceptions, reasoned in the code: a feature carrying GenBank's own `>`
  partial-end marker is allowed to report an end past the record's length (GenBank's own
  way of saying "known to continue beyond what was given"; THEORY (unverified),
  confirmed only against this reader's own synthetic fixture); a compound
  (spliced/origin-wrapping) feature's segments must each individually fit in bounds
  regardless of a partial marker, which a legitimate origin-wrapping feature on a
  circular plasmid (issue #128's intended shape) already satisfies per-segment, so this
  check does not block it.
- New fixture: `worker/tests/data/out_of_range.gb` (a good gene, an out-of-range gene,
  and a legitimately-partial gene on one record).
- Docs: `docs/science_and_formats.md` section 4 documents the check and both exceptions.
