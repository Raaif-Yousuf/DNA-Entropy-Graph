# legacy/

Nothing under `legacy/` is live or shipped: no `.claude/settings.json` hook, CI workflow, or
script outside this directory reads from it. It exists only to be mined by the P0 cleanup
issues, each of which ports one named file into its live location with CLAIR's paths, repo
name, and guard scripts replaced. Once every P0 issue lands, delete this directory.

See `docs/migration/2026-09-19-clair-conventions-inventory.md` for the full file-by-file
verdict and adaptation notes, and `docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md`
for the adapted conventions this migration is drafted from.
