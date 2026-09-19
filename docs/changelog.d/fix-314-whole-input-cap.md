- Fixed #314: `readers/input.py::load_input()` validated `cfg.max_total_len` against each
  GenBank/FASTA record individually, never against the sum, so a multi-record file many
  times over the documented whole-input cap could pass cleanly one small record at a time.
  Both the GenBank and FASTA branches now track a running total across records and raise
  `ValidationError` (naming the length reached, the cap, and by how much it is over) the
  moment the sum exceeds the cap. Acceptance criteria were written into #314 as a comment
  before implementing (it carried `needs-criteria`); the issue's own per-record/whole-input
  framing was corrected in that comment against the real mechanism (`cfg.max_len` is a
  GPU per-window ceiling only, never a per-record validation bound — see
  `docs/science_and_formats.md` section 4).
- Filed #330 (P2): a UTF-8 BOM breaks FASTA and GenBank reading outright and gives a
  confusing "invalid character" error on the paste path; not fixed this wave.
- Docs: `docs/science_and_formats.md` section 4 (validation rules) now describes the
  encoding-robustness fix, the corrected whole-input-cap mechanism, and the
  compound-location notice behaviour.
