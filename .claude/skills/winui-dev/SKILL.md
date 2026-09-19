---
name: winui-dev
description: Use before building, running, testing or debugging the WinUI 3 app in app/, before adding a page, ViewModel, binding, dialog, setting or DI registration, and when a XAML change compiles but shows nothing.
---

# WinUI dev

The app (`app/`) is a thin client: it owns the UI, the run history, and every
Google Cloud API call (CLAUDE.md's "Two halves, one repo"). It never imports
`torch` and never runs inference in-process (Hard Rule 6). This skill is the
day-to-day mechanics of building and debugging it; the architectural rules
(MVVM, the Cloud interface boundary, copy rules) are Hard Rules 6-14 in
`CLAUDE.md` and are not repeated here except where a command or a pitfall
needs them.

## Commands

```powershell
dotnet build app/DnaEntropyGraph.sln -c Debug -p:Platform=x64
dotnet test app/DnaEntropyGraph.sln
dotnet test app/tests/DnaEntropyGraph.Guards.Tests   # fast, seconds not minutes
scripts/dev_app.ps1                                  # sets DEG_FAKE_CLOUD=1 and launches
```

`scripts/dev_app.ps1` runs the app against `FakeGcp` (Hard Rule 7) by
setting `DEG_FAKE_CLOUD=1` before launch, so a dev loop never touches a real
Google Cloud project by accident. Running the app any other way against a
real project is a deliberate, explicit choice — do not do it without reading
`working-on-gcp` first, and never from an agent session (Hard Rule: GPU/cloud
work is owner/CI territory, not a laptop dev loop).

Full test surface, PowerShell equivalents and the GPU-test carve-out:
`docs/dev_commands.md`.

## Where things go

| Kind of change | Lives in |
| --- | --- |
| A page / view | `DnaEntropyGraph.Presentation` (XAML) + `DnaEntropyGraph.App` (the window/navigation host) |
| ViewModel logic | `DnaEntropyGraph.Presentation`, CommunityToolkit.Mvvm, **no WinUI reference** — testable without a UI thread (Hard Rule 8) |
| A new service / DI registration | `App.xaml.cs::ConfigureServices`. A service never registered resolves to a runtime exception on first page open, not at build time — `Guards.Tests/DiResolutionTests` is what catches a missing registration before a user does |
| A user-visible string | `app/src/DnaEntropyGraph.App/Strings/en-US/Resources.resw`, referenced from XAML via `x:Uid`, never inline (Hard Rule 13) |
| Anything with a branch or a calculation | Not in `*.xaml.cs`. Code-behind is `InitializeComponent()`, constructor DI, and nothing else that branches (Hard Rule 8) |

## `x:Bind` pitfalls

- **Defaults to `Mode=OneTime`.** A binding you expect to update on property
  change needs `Mode=OneWay` (or `TwoWay` for input controls) written
  explicitly. The silent-`OneTime` case looks exactly like "wired to
  nothing" (see that skill): the binding compiles, the page renders once
  with the right initial value, and then never updates again.
- **Compile-time typed**, unlike `{Binding}`. A property rename that misses
  the XAML is a *build* error for `x:Bind`, which is exactly why Hard Rule 8
  and the `wired-to-nothing` skill both prefer it: `{Binding}` to a missing
  property compiles fine and shows blank at runtime instead. Reserve
  `{Binding}` for inside a `DataTemplate` that genuinely needs it (an item
  template bound to a runtime-typed collection element), and leave a
  comment saying why when you do.
- `ContentDialog` needs an explicit `XamlRoot` (usually the page's own) or
  it throws instead of showing. Route dialogs through an `IDialogService`
  abstraction so ViewModel tests can fake it rather than needing a real
  `XamlRoot`.

## `[ObservableProperty]` rules

CommunityToolkit.Mvvm's source generator needs a `partial class` and a
backing field named `_camelCase` (the generator derives the public
`PascalCase` property from it). A ViewModel that will not compile with a
cryptic generator error is usually missing `partial` on the class
declaration, not a real logic bug.

## Threading

UI updates from a background task (a poll of a run's `status.json`, a cloud
operation callback) must marshal through the UI thread's dispatcher — inject
an `IDispatcher`-shaped abstraction rather than calling
`DispatcherQueue.GetForCurrentThread()` directly from a ViewModel, so tests
can run the same code without a UI thread at all (Hard Rule 8's own
requirement).

## The WebView2 viewer (igv.js)

- Call `EnsureCoreWebView2Async()` before touching `CoreWebView2` and check
  `CoreWebView2Environment.GetAvailableBrowserVersionString()` first; a
  missing WebView2 runtime fails with a named action, not a blank pane.
- Local run output reaches the viewer only through
  `SetVirtualHostNameToFolderMapping` — `file://` URLs are blocked inside
  WebView2 by design. If the viewer is blank, that is the first thing to
  check, before assuming the track file itself is wrong.
- **A blank viewer needs the DevTools console checked first**, not the C#
  side. `CoreWebView2.OpenDevToolsWindow()` (or right-click > Inspect once
  DevTools are enabled) surfaces igv.js's own JS errors, which are usually
  the real cause (a malformed bedGraph, a CORS-shaped failure from a
  mis-mapped virtual host) and are invisible from the C# debugger entirely.

## Theme

Read once at startup from the persisted setting. This is exactly the
"a setting saved and never read" shape from `wired-to-nothing`: if a theme
toggle in Settings does not visibly change the running window, check first
whether anything past the write side ever reads the setting back, before
assuming the toggle's own UI is broken.

## Packaging

The app is **unpackaged, self-contained** — not MSIX, not packaged
(Appendix C's repo layout, `docs/packaging_design.md`). `vpk pack` (Velopack)
runs only inside `release.yml`, never as part of a dev loop; do not reach
for it to "test packaging" locally without reading `docs/release_runbook.md`
first, since a local pack run is a different, heavier thing than a Debug
build and is not part of this skill's day-to-day loop.

## Related

`CLAUDE.md` Hard Rules 6-14 (the split, the cloud boundary, the copy rules);
`wired-to-nothing` (the per-shape checklist this skill's pitfalls above
each map onto); `docs/ui_conventions.md` (WinUI patterns, theme tokens, the
full copy-rules-with-examples reference); `docs/dev_commands.md` (every
command, PowerShell equivalents).
