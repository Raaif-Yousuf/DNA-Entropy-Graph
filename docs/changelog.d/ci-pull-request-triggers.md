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
- Skipped, rather than failed, the three THIRD-PARTY-NOTICES tests that run against the
  real dependency tree when the machine cannot enumerate it. They need a `dotnet` that can
  restore `app/` (impossible on a Linux runner: the app is WinUI 3 and its packages are
  Windows-only) and a populated `worker/.venv` with the optional extras, which is
  gitignored. `ci-docs.yml` already excludes the script itself from its guard loop for
  exactly this reason (#416); its tests were not excluded, so turning on pull-request CI
  made them red for a reason unrelated to any change under test. The new
  `scripts/tests/conftest.py` probes both prerequisites once per session and skips with a
  reason naming the missing one. Confirmed both arms: with only the `dev` extra installed
  the three skip and the rest of the suite is 337 passed, and after
  `pip install -e "worker[dev,genes]"` all 17 tests in the two files run for real and pass.
- Fixed `ci-app.yml`'s vpk pack step, which turned the first pull-request run of that
  workflow red: `--packVersion 0.0.0-ci` is below the `0.0.1` floor vpk 1.2.0 enforces, so
  it refused to pack at all. It is now `0.0.1-ci.<run number>`. The artifact is a
  seven-day throwaway that is never published, so nothing about the version is meaningful
  beyond being one vpk accepts.
