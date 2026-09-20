- `ci-app.yml`, `ci-worker.yml` and `ci-docs.yml` now run on every pull request touching
  their own area (plus `workflow_dispatch`, kept for a manual re-run), instead of
  `workflow_dispatch` only. No `push` and no `schedule` trigger on any of them: a PR's own
  check is the gate, and `main` only ever gets there through a PR one of these already ran.
  `codeql.yml` stays `workflow_dispatch` only, with a comment explaining why (its csharp
  lane is a full Windows dotnet build, too heavy to pay for on every PR).
