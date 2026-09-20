- `ci-app.yml`, `ci-worker.yml` and `ci-docs.yml` now run on every pull request touching
  their own area (plus `workflow_dispatch`, kept for a manual re-run), instead of
  `workflow_dispatch` only. No `push` and no `schedule` trigger on any of them: a PR's own
  check is the gate, and `main` only ever gets there through a PR one of these already ran.
  `codeql.yml` stays `workflow_dispatch` only, with a comment explaining why (its csharp
  lane is a full Windows dotnet build, too heavy to pay for on every PR).
- Fixed `test_genes_flag_reports_a_gene_count_in_the_summary` in
  `worker/tests/test_cli.py`, which needs the optional `[genes]` extra
  (Pyrodigal) and neither installed it nor skipped without it. It now skips
  with `pytest.importorskip("pyrodigal")`, matching how `test_annotator.py`
  already guards itself; `ci-worker.yml` installs the `genes` extra, so the
  test still runs for real there.
