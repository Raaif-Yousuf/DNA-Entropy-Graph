---
name: wired-to-nothing
description: Use before reporting ANY feature, fix or wiring change as done in DNA-Entropy-Graph. Code that compiles, runs, passes tests and does nothing is the inherited house bug, and no test has ever caught one by accident. Also use when reviewing a change that adds a binding, command, DI registration, startup-script step, bucket rule, label or setting.
---

# Wired to nothing

**The bug class:** code that compiles, runs, passes its tests, is reviewed as
correct, and does nothing at runtime. This project inherits the shape from the
prototype's own history (an optional cloud parameter no caller ever set, a
hook that ran on a render one frame too late, a visible clickable control
reading nothing) and adds new shapes of its own: a WinUI binding to a
property that does not exist, a `[RelayCommand]` nobody binds, a Google Cloud
resource created with no label so the Cloud page cannot find it again.

The reason tests rarely catch it is structural: a unit test mounts the thing
and calls it directly, which is exactly the step missing in production. The
test supplies the caller the real system does not have.

## The one rule

Before you say a change is done, **name the single observable that would
differ between "this works" and "this is wired to nothing", and go check that
observable.** Not the test suite. Not the diff. The observable.

If you cannot name one, you do not yet know whether the work is done. Say so,
and add a `docs/ToTest.md` row if checking it needs a real build or a real VM.

## Check by shape

Match what you changed to a row. Run the check. Read the output, not just its
exit code.

| You added/changed | The check that catches it |
|---|---|
| A XAML binding | `x:Bind` to a missing property is a *compile* error, `{Binding}` is not: `Select-String -Pattern '\{Binding ' app/src -Recurse` must return zero rows outside `DataTemplate`s that genuinely need it. Then run the page in Debug with `DebugSettings.BindingFailed` wired to throw, and open the page. |
| An `ICommand` / `[RelayCommand]` | Grep the XAML for `Command="{x:Bind <Name>Command}"`. Then click it and watch the ViewModel state actually change. |
| A DI service | `Guards.Tests/DiResolutionTests` builds the real `ServiceCollection` and resolves every `*ViewModel` and every `I*Service`. A service registered but never injected: grep constructors for the interface name — a registration with no consumer is a leak in the other direction. |
| A startup-script step | `bash -n` and `shellcheck worker/vm/startup.sh`, then on the CPU walking-skeleton VM read `progress.jsonl` for the step's own progress line. A step with no progress line is invisible and must get one. |
| A bucket lifecycle rule / IAM binding | Read it back after `Apply`; on the real project once via `CloudCli bucket describe`. "Applied" is not "present". |
| A label | After creating anything, `CloudCli resources list --install <id>` must show it. If it does not, the leak-detector cannot see it either, and Hard Rule 9/10 is violated silently. |
| A setting | Grep for the *read* side outside `SettingsViewModel`. Then change it and observe the behaviour differ. A setting saved and never read is the single most common instance of this bug class. |
| A `CloudError` class | The classifier test has a recorded payload *and* the ViewModel maps it to a `.resw` key *and* the key names an action (Hard Rule 13). Three greps, not one. |
| A worker output file | Read the file back with a real reader (Biopython for `.gb`, a bedGraph parser, igv.js actually loading it in the viewer), never the writer's own return value. |
| A `JobPhase` transition | The state-machine test table has a row; the UI state table in `docs/ui_conventions.md` has a row; the run-history row records it. |
| A direction/windowing change | The combined track differs from Forward-only exactly in the first `K` bases on a synthetic input; the seam is recorded in provenance (Critical Pitfalls, "Reverse means reverse complement"). |

## Smells that mean "check harder"

- The fix is one line and the symptom was dramatic.
- It passed on the first run with no surprises.
- You are about to write "should now work" instead of "does work".
- The change is in a file whose tests all mount it directly, with no path
  from a real entry point (`App.xaml.cs`, `worker/src/dna_entropy/cli.py`,
  `startup.sh`).
- A previous session already reported this exact thing done — check
  `gh issue view <n> --comments` and `rg -n "#<issue>"` before rebuilding it.

## When the fix "should work" but the symptom persists

Stop adding fixes. You may be looking at a second, independent cause — a
reported symptom is often more than one bug, and fixing the first hides the
rest. Re-run the *same* action and see whether the observable moved at all.
If it did not move, the fix is not partially working; it is not running.

Then record it per Hard Rule 18: `MEASURED <date>:` with the observation, or
`THEORY (unverified):`. `docs/entry_points.md` carries the disproven-diagnoses
table — read it before re-deriving a cause that may already be disproven.

## Shapes this repo already knows to watch for

**An optional field with a sensible default hides a missing caller.** The
prototype's own history has this shape: a parameter threaded end-to-end,
tested with an explicit value, defaulted so nothing looks wrong when nobody
sends it. For every optional field that changes output (a job option, a
cloud-request field, a viewer flag), grep the real caller (the app's
ViewModel, the CLI) for the field name; a field only a test ever sets has no
production caller.

**Parallel slices that each assume a sibling wires them together.** When work
on `app/` and `worker/` (or two `app/` projects) is split across agents, the
merge of the pieces is not done until ONE observable crosses every seam: a
run started in the UI actually reaches the worker, and a worker output file
actually reaches the viewer. A green suite per slice is exactly the evidence
this class fakes, because each slice's test supplies the caller its sibling
was supposed to be.

**A constant or config value that documents a behaviour nothing implements.**
For any new constant (a threshold, a label key, a retry count), ask who reads
it, not whether it is commented. A test that only relates the constant to
itself (`assert TIMEOUT_S / 2 == 30`) can never fail for the reason you care
about, because it never calls the function that is supposed to use it.

## What honest reporting looks like

Say which observable you checked and what it showed. If you could not check
one — no packaged build, no real GCP project, no GPU — **say that plainly and
add a `docs/ToTest.md` row** naming what a human must do and what passing
looks like. "Tests pass" is not evidence that a feature is reachable, and
claiming done on a green suite alone is how this bug class always ships.
