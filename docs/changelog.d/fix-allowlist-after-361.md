- `scripts/check_unused_fields.py` caught two things on its first CI run that no local run had:
  `JobManifest.raw` was deleted as part of #361, which made its allowlist entry stale, and
  `SurprisalSummary`'s fields are unread because `analysis/surprisal.py` is knowingly unwired
  groundwork for #123. The stale entry is gone and the summary is allowlisted with an explicit
  instruction to delete that entry when #123 lands, so the wiring cannot quietly leave the summary
  unread. The both-directions allowlist check was the whole reason for building it that way.
