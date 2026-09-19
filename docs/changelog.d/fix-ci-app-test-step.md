- MEASURED 2026-09-19 (run 35448261797): `ci-app.yml`'s test step had never run a single test.
  It carried `--filter`, `--logger "trx;..."` and `--collect:"XPlat Code Coverage"`, all three
  VSTest options, while the app's projects run on xunit.v3 / Microsoft.Testing.Platform, where the
  runner does not recognise them and gives up: every one of the six test assemblies reported
  `Zero tests ran`, exit code 5. The plain `dotnet test -c Release -p:Platform=x64 --no-build`
  runs 31 tests, 30 passing and 1 skipped. The UI tests need no filter because they are all
  `[Fact(Skip=...)]`; TRX and coverage need the `Microsoft.Testing.Extensions.*` packages first.
- Removed `ci-app.yml`'s `if app/DnaEntropyGraph.sln exists` soft gate now that the solution has
  landed and a real run has executed every step against it, which is the confirmation
  `scripts/check_guard_drift.py` asks for. Leaving it would have turned a future accidental
  deletion of the solution into a green job.
- Corrected the claim, made a few hours earlier in `docs/dev_commands.md` and a `ToTest` row, that
  `ci-app.yml` "will need `working-directory: app`". It already sets it on every step. The
  `dotnet test` working-directory trap is real but is a trap for a human or an agent running the
  command by hand from the repo root, never for CI.
