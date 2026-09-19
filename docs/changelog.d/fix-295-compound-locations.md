- Fixed #295: a GenBank `join(...)`/`complement(join(...))` (spliced) gene location was
  silently collapsed to its outer bounding box, with no indication the reported
  begin/end (and any downstream mean-entropy figure) included intron sequence.
  `readers/genbank.py` now detects a Biopython `CompoundLocation` and raises an explicit
  notice per compound feature naming its exact exon segments; the bounding box itself is
  unchanged (a `GeneFeature` change would touch `annotators/base.py`, outside this lane) —
  see the tracked `DECISION` issue on adding per-exon `GeneFeature.exons`.
- New GenBank fixture `worker/tests/data/spliced.gb`: one plain gene plus two compound
  (spliced) genes, one on each strand, for #295's tests.
- Filed #331 (P2): the GenBank reader accepts a gene feature whose coordinates fall
  outside its own record's sequence length with no validation or notice; not fixed this
  wave.
- Filed #333 (DECISION): whether `GeneFeature` should carry per-exon segments so a spliced
  gene's mean-entropy figure can be computed from exon bases only; needs changes in
  `annotators/base.py` and `writers/genbank.py`, outside this lane's ownership.
