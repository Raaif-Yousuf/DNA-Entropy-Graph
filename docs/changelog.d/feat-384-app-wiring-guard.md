- `scripts/check_app_wiring.py` is the ninth repo guard and the C# half of the
  bug class `check_unused_fields.py` already covers in Python: code that
  compiles, runs, passes its tests and does nothing. Nine finding codes, each
  with its own allowlist key: `UNBOUND-COMMAND` and `UNBOUND-OBSERVABLE` (a
  `[RelayCommand]` or `[ObservableProperty]` that no `.xaml` binds, so the
  generated `ICommand` and the `PropertyChanged` notification both go
  nowhere), their `TEST-ONLY-` and `CODE-ONLY-` weaker variants,
  `MISSING-UID-RESOURCE` (an `x:Uid` with no `.resw` entry, which renders a
  blank label rather than failing the build), `ORPHAN-RESOURCE`,
  `UNREGISTERED-DEPENDENCY`, `DEAD-REGISTRATION` and `DANGLING-BINDING`.
- Its first run on the real tree reported 25 findings, which is what a skeleton
  app with ViewModels and no Views actually looks like. They are seeded into
  `scripts/app_wiring_allowlist.json`, each entry naming the issue whose page
  will bind it, to be deleted in that issue's own pull request.
- The allowlist is checked in both directions, so an entry whose finding stopped
  firing fails the run: the Python-side allowlist went stale within an hour of
  being written, and its guard said so.
- `--suggest-allowlist` prints a skeleton with every reason left EMPTY and
  writes no file, on purpose. An allowlist entry whose reason nobody wrote is a
  baseline silently raised, which is the one thing this guard exists to stop.
- `--self-test` (13 checks) asserts both directions: that each finding fires on
  a tree broken in exactly that one way, and that a correctly wired tree is
  clean. `scripts/tests/test_check_app_wiring.py` (20 tests) asserts the same
  rules independently, because a test file whose only assertion is "the
  self-test said PASS" would have agreed with the self-test this repo shipped
  that could not fail.
- Two bugs in the guard were found by its own self-test before it ever ran on
  the repository: a greedy `[^\]]*` in the `[ObservableProperty]` pattern that
  matched the wrong field, and a `DEAD-REGISTRATION` check that called every
  implementation type in `AddSingleton<IFoo, FooService>()` dead. A third was
  found on first real contact: skipping `ServiceRegistration.cs` when looking
  for consumers made every `sp => sp.GetRequiredService<FakeGcp>()` factory
  delegate invisible, so the one object behind five interfaces read as dead.
