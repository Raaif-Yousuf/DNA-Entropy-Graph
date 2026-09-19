- Wired #123 end to end: surprisal was groundwork-only (a module, tests, and a
  knowingly-unwired `SurprisalSummary`), closed anyway; the wiring the closed issue's own
  title promised was never actually done. `analysis/direction.py::analyze_direction` now
  computes surprisal from the SAME `fwd.probs`/`rev.probs` entropy already used (zero
  extra predictor calls) and combines it by the identical forward/reverse selection rule
  as entropy, exposed as `DirectionResult.surprisal_values`.
- Added `RunConfig.include_surprisal` (`worker/src/dna_entropy/config.py`, default `True`)
  and `--surprisal/--no-surprisal` on the CLI's `run` command, matching every other
  writer-suppression flag's shape (issue #304's `include_*` pattern).
- `writers/bedgraph.py`, `writers/wig.py`, and `writers/geneious.py` grew a `metric=`
  parameter (default `"entropy"`, byte-identical output for every existing caller) so
  surprisal reuses them wholesale instead of a parallel writer class: `<name>.surprisal.bedgraph`
  / `.wig` / `.geneious.gff3`.
- `writers/tsv.py`'s plain (non-`Both, separate tracks`) shape grows an optional 4th
  `surprisal_bits` column (`surprisal_blocks=`); `writers/summary.py`'s `write`/`write_multi`
  grew an optional `surprisal=` parameter reporting `SurprisalSummary`'s mean and total
  log-likelihood alongside the entropy stats. Both default to the pre-#123 shape when
  omitted.
- `pipeline.py` wires all of the above behind `cfg.include_surprisal`, for both the
  standard (FASTA/paste) and GenBank output paths.
- Deliberately scoped out (documented in `docs/science_and_formats.md` section 2b, not a
  gap left silent): the TSV's `Both, separate tracks` 5-column shape does not yet grow
  forward/reverse surprisal columns. An early draft added
  `DirectionResult.forward_surprisal`/`.reverse_surprisal` for that future writer;
  `scripts/check_unused_fields.py` immediately flagged both as genuinely unread (no writer
  consumed them yet), so they were removed rather than left "for later" — the guard
  catching this repo's own named bug class within the session that was fixing an instance
  of it.
- `scripts/unused_fields_allowlist.json`'s `SurprisalSummary.*` entry is now stale (it
  said "DELETE THIS ENTRY WHEN #123 LANDS") and must be removed by whoever owns that file;
  `check_unused_fields.py` fails on the stale entry until then — this is the mechanical
  proof the wiring landed, not a leftover bug.
- Tests: `worker/tests/test_direction.py` (6 new: forward-only/reverse-only surprisal,
  combined-follows-the-same-seam, averaged, both-separate, and the "only a hand-built
  DirectionResult can have `surprisal_values=None`" case), `worker/tests/test_writers.py`
  (6 new: `metric="surprisal"` for bedgraph/wig/geneious, plus summary surprisal
  reporting), `worker/tests/test_tsv.py` (3 new), `worker/tests/test_pipeline.py` (7 new,
  including the issue's own named observable and the CLI flag's actual effect on disk),
  `worker/tests/test_cli.py` (2 new, matching the existing `--tsv`/`--no-tsv` regression-guard
  shape). Mutation-checked: reverted the CLI wiring line and the surprisal `_combine` call
  by hand, watched the matching tests go red, restored byte-exactly.
