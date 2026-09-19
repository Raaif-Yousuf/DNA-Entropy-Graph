- Issue #68's five guards all exist now, in `app/tests/DnaEntropyGraph.Guards.Tests`: the `.csproj`
  scan for a `Google.*` reference outside `DnaEntropyGraph.Cloud` and for any inference package
  (Hard Rules 6 and 7), the code-behind scan for anything that branches in a `*.xaml.cs`
  (Hard Rule 8), the `.resw` em-dash scan and the XAML inline-string scan (Hard Rule 13), and
  `VmSpec.EnsurePreconditions()` refusing a spec missing any of the six standard labels, a positive
  `maxRunDuration` or an `instanceTerminationAction` (Hard Rule 10), called by `FakeGcp` before it
  will pretend to create anything.
- Each scanner reports what it actually **read**, not only what it objected to, because a violation
  count of zero means "found nothing to check" and "checked everything and it was fine" equally
  well. Every guard has a test for that exact false pass: an empty `.resw` reports zero entries
  scanned, a glob that matches nothing reports zero files scanned, and `RepoPaths` throws rather
  than scanning nothing if it cannot find `DnaEntropyGraph.sln`.
- MEASURED 2026-09-19: two of the guards were broken against the **real** tree and watched failing.
  An em dash in `Resources.resw`'s `AppDisplayName` failed the resw guard naming the file, the entry
  and the rule; a `Google.Cloud.Storage.V1` reference added to `DnaEntropyGraph.Core.csproj` failed
  the csproj guard naming Hard Rule 7. Central Package Management refused the reference first, which
  is a second layer nobody had counted on.
- `app/Directory.Build.props` gained the `<Version>` that `scripts/check_version_lockstep.py`
  (issue #33) requires. The guard had been soft-noticing while `app/` did not exist and went red the
  moment it did, which is the guard working.
