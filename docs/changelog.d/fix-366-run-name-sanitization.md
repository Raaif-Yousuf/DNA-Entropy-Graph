- Fixed #366: `pipeline.run()` built the run's output folder and every file name inside it from
  `cfg.name` with no sanitization at all, so a `--name` containing a path separator, a leading `..`,
  or a few hundred characters went straight into a filesystem path the user never chose. Hard Rule
  14 says the user's files are read-only to us and outputs go to the chosen output folder, and a
  `--name` of `..\..\Documents` is that rule broken by a string, so `sanitize_run_name()` refuses or
  neutralises a traversal rather than merely tidying a name. `cli.py`'s own private `_sanitize_name`
  is gone: `run()` now applies the same function unconditionally at its own top, so the folder name
  and the file names inside it can never disagree.
- Found while verifying #350 end to end. The contig-name hardening that issue asked for was real,
  and protected nothing the user actually gets, because the run folder never went through it.
- Added `analysis/surprisal.py` for #123: per-position `-log2 P(actual base)` out of the same
  `(L, 4)` matrix at no extra GPU cost, with a documented finite ceiling (surprisal is unbounded
  above as P tends to 0, unlike entropy's natural `[0.0, 2.0]`), an ambiguity-fallback rule, and a
  summary carrying total log-likelihood over defined positions only. **It is not wired to anything
  yet**: no writer emits it and no CLI flag turns it on, so #123 stays open. The module and its 13
  tests are groundwork, not the feature.
