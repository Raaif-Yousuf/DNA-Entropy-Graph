# UI conventions: WinUI patterns, copy rules, theme, and the state-to-UI table

**Status: specification, not yet implemented.** `app/` does not exist yet (issue #61).
This doc is what `DnaEntropyGraph.App`'s Views and `DnaEntropyGraph.Presentation`'s
ViewModels are built against, and the reference for the `.resw`/em-dash guard once it
exists (`docs/tests.md`'s note that a guard scanning every XAML-reachable string literal
"is not yet built, needs `app/`"). Screen-by-screen detail beyond what is summarized here
lives in [Appendix A, section 2](superpowers/specs/2026-09-18-appendix-a-app-design.md#2-screen-by-screen-ux).

---

## 1. Shell and navigation

`NavigationView` (left rail, compact on narrow widths) with three top-level items, **New
run**, **Runs**, **Cloud**, and a footer **Settings**. The title bar is extended with a
Mica backdrop. A status pill sits top-right at all times: `Signed in as x@y - project-id -
$0.00 this month`. Active runs show a badge on the **Runs** item so a user who navigates
away never loses track of a job in progress. Keyboard: `Ctrl+N` new run, `Ctrl+O` add
files, `Ctrl+Shift+V` paste sequence, `Ctrl+Enter` run, `Esc` cancel a dialog, `F5` refresh
(Cloud/History), `Ctrl+,` Settings, `Ctrl+L` focus the locus box in the viewer, `Alt+Left`
back.

Shell behaviour MEASURED 2026-10-02 (#489, #490): the header is the localized
`PageTitle_<key>` string (`ShellViewModel.CurrentPageTitle`), never the page key; New run
is selected at startup (`NavigationView.Loaded`), so header, selection and frame agree; a
destination with no page registered in `Startup/NavigationRoutes.cs` shows the one generic
`PlaceholderPage` instead of an empty frame; the OS window title is `AppDisplayName`.
Window placement: nothing is saved while the window is minimized or maximized, a saved
rect that is the minimized sentinel (-32000) or smaller than 320x200 is refused, and a
first launch is 1280x800 DIPs (scaled by the window DPI, kept within 5% of the work area).

## 2. Copy rules (CLAUDE.md rule 13; this is the rationale and the worked examples)

- **Every user-visible string lives in `Strings/en-US/Resources.resw`**, referenced by
  `x:Uid`, never inline in XAML or C#. This is what makes the string set auditable and
  localizable in one place, and is what a future `.resw`-scanning guard checks against.
- **No jargon without the plain phrase first.** "A rented computer with a graphics card (a
  VM)" is the house example: a lab biologist has no reason to already know what "a VM" is,
  so the plain phrase always comes first and the technical term (if it appears at all)
  trails in parentheses, not the other way around. Other load-bearing examples: "a
  one-time permission slip from Google (a GPU quota)", "a private folder for this app's
  work (a Google Cloud project)".
- **No em dashes anywhere the user can see**: `.resw`, error strings, exported files
  (stats, provenance, GenBank descriptions), release notes. Use a plain hyphen, a comma, or
  a full stop instead. `docs/` itself is the one exempt surface (CLAUDE.md rule 13's own
  carve-out); nothing user-facing is.
- **Every error names one action the user can take**, and that action is a real button or
  link whenever one exists, never advice with nothing to click. This is the same principle
  behind the error taxonomy in `cloud_design.md` section 5, applied at the copy layer: a
  classifier bucket maps to exactly one primary action, shown as a button, with "Copy
  details for support" always available as a secondary one.
- Numbers and dates go through `CultureInfo.CurrentCulture`; the string set itself ships
  English (`en-US`) only in v1, but the layout must tolerate roughly 30% longer strings so
  a later localization pass does not have to redo every page's layout.

## 3. Core WinUI patterns

- **`SettingsCard`/`SettingsExpander`** (CommunityToolkit.WinUI.Controls.SettingsControls)
  for every options list: Basic run options, Settings page sections, the wizard's OK/Fix
  rows. This keeps every options surface in the app visually consistent without a bespoke
  layout per page.
- **`InfoBar`** for inline, non-blocking errors and notices (validation pills on the New
  Run page, a stale-quota warning on the wizard). Reserve `ContentDialog` for anything that
  blocks progress or needs an explicit decision (Stop VM now, Delete everything this app
  created, a project-wide setup error). Every `ContentDialog` needs an explicit `XamlRoot`
  set from the calling page; a missing one is a common "it compiled but nothing showed"
  bug in WinUI 3 specifically (see the `winui-dev` skill).
- **`x:Bind` defaults to `Mode=OneTime`.** Anything that should update after the initial
  render needs `Mode=OneWay` or `Mode=TwoWay` set explicitly; a binding left at the default
  is a silent, compiling, nothing-updates bug, not a build error, which is exactly the
  "wired to nothing" shape CLAUDE.md's Critical Pitfalls warns about for XAML specifically.
- **No logic in code-behind** (CLAUDE.md rule 8, mechanically guarded). A `*.xaml.cs` file
  contains `InitializeComponent()`, constructor-injected dependencies, and nothing that
  branches; every behaviour lives in the paired ViewModel in `DnaEntropyGraph.Presentation`
  instead, which is what makes the ViewModel testable with a synchronous `IDispatcher` and
  no WinUI reference at all.
- **UI updates from a background task go through `DispatcherQueue`.** A progress event
  arriving from `JobEngine` on a background thread must be marshalled before it touches
  any bound property; this is the same category of bug as the `x:Bind` mode default, just
  triggered from the other direction (a real update that never reaches the UI thread,
  versus a UI thread with nothing telling it to update).

## 4. Theme

Three choices in Settings: **Light / Dark / Use system** (default: Use system),
implemented via `ThemeService` broadcasting a `ThemeChanged` message. The embedded viewer
(igv.js in WebView2) is not a native WinUI surface, so it needs its own theme sync:
`CoreWebView2.Profile.PreferredColorScheme` follows the app theme, and a `dark.css`
override (toggled by a `body.dark` class posted through the bridge) recolors igv.js's own
chrome (background, track labels, ruler, popover) and the entropy track colour for
contrast, since igv.js ships no dark mode of its own. All colours everywhere else come
from theme resources, not hardcoded brushes, so High Contrast mode (an accessibility
requirement, section 5 below) works without a second code path.

## 5. Accessibility and DPI

- WinUI 3 is per-monitor DPI v2 by default; the app uses only vector icons (`FontIcon`/
  `PathIcon`), never bitmaps, except the app's own logo (`.svg` via `SvgImageSource`).
  WebView2 and ScottPlot both inherit `RasterizationScale` automatically.
- `AutomationProperties.Name` is required on every icon-only button and on the drop zone.
- `LiveSetting=Polite` on the run-progress stage list, so Narrator announces phase changes
  without the user needing to focus the list manually.
- Minimum 40x40 hit targets; the drag-and-drop zone always has a visible "Add files..."
  button twin, since drag-and-drop alone is not discoverable or operable for everyone.
- No fixed heights on text-bearing controls, so text scaling (a Windows accessibility
  setting) does not clip content.

## 6. Toasts and file associations

Toasts use the Windows App SDK `AppNotificationManager`, registered at startup (required
explicitly for an unpackaged app, unlike a packaged MSIX which gets this for free).
Notification buttons ("Open results", "Open folder") carry an `action=openRun&id=<jobId>`
argument that `App.OnLaunched` reads to deep-link into the right run even from a cold
start. Failure toasts carry the error's title from the `ErrorCatalog`. An idle-VM warning
toast repeats every 15 minutes while a keep-alive VM runs with no active job, and a
separate reminder fires as a keep-alive window nears its expiry. File associations are
opt-in-by-default in the installer: per-user `HKCU\Software\Classes` ProgIDs for GenBank
and FASTA extensions, registered as *additional* "Open with" entries, never as the
default handler, so a user's existing Geneious or SnapGene file association is never
silently overwritten.

## 7. The `JobPhase`-to-UI table

Reading this table alongside `architecture.md` section 4 (the `JobPhase` state machine
itself) is the fastest way to answer "what should this page show right now" for any
run-progress or history-row state. This table is the UI *behavior* per phase (buttons,
cost ticker, history chip); the actual title/sub-text copy shown to the user, and every
live narration string the "Stage list row" column below only summarizes, are in
[`copy_catalog.md`](copy_catalog.md) sections 1 and 2, the single source both `.resw` and
this table draw from.

| `JobPhase` | Stage list row | Cost ticker | Cancel/Stop/Delete buttons | History chip |
|---|---|---|---|---|
| `Draft` / `Validating` | "Checking your files" | hidden | disabled | (not yet a Runs row visible in History until validation passes) |
| `Uploading` | "Uploading" | hidden (no VM yet) | Cancel only | Running |
| `Provisioning` | "Starting a GPU computer" (zone ladder narrated live: "Trying us-central1-a... no L4 capacity, trying us-central1-b") | running, VM not yet billing | Cancel, Stop VM now, Delete VM now | Running |
| `Preparing` | "Preparing the computer" (first run in a project explains the cache fill: "First run in this project takes longer, about 8 minutes, while the model is cached") | running | Cancel, Stop VM now, Delete VM now | Running |
| `Running` | "Analysing" (per input, per contig, per window and direction, e.g. "SetTnpB - record 2 of 5 - window 3 of 7, forward") | running | Cancel, Stop VM now, Delete VM now | Running |
| `Finalizing` | "Saving results" | final estimate shown, ticker still running (the VM is not yet confirmed stopped) | disabled (nothing left to stop) | Running |
| `Downloading` | "Downloading" | final estimate | disabled | Running |
| `Completed` | "Cleaning up" ("Stopping the GPU computer" / "Deleting" / "Keeping running for 30 min") shown briefly, then the page navigates to Results | final actual estimate, ticking stops once the VM's terminal state is verified | n/a | Success chip, cost, duration |
| `PartiallyCompleted` | (Results, with a banner naming which inputs failed) | final actual estimate | n/a | Warning chip |
| `Failed(code)` | error card at the stage where it failed, from `ErrorCatalog` | final estimate up to the failure | Delete VM if one still exists | Failure chip, with the error code |
| `Cancelling` / `Cancelled` | "Cancelled. Partial results kept." | final estimate | disabled | Cancelled chip |

The Run Progress page always shows a banner while any non-terminal phase is active: "You
can close the app. The run continues in the cloud and results download when you come
back." This is true by construction (the job lives entirely in the bucket and on the VM,
not in the app's own memory), and stating it plainly is what keeps a user from feeling
they must babysit a multi-minute run.

## 8. Two `XamlCompiler.exe` traps, MEASURED 2026-09-19 (issue #62)

Both crash this repo's pinned Windows App SDK version's `XamlCompiler.exe` with **no
diagnostic output at all** - not a normal `error WMC....` line, exit code 1 and nothing
else, confirmed by direct invocation, `dotnet build -v:diag`, and reading the tool's own
`output.json` (absent or stale on the failing run). Both were isolated to one attribute/one
resource entry on an otherwise-empty `Window`, in a multi-hour bisection this note exists
to save the next person from repeating:

- **`ExtendsContentIntoTitleBar="True"` as a XAML attribute on `<Window>`.** Set it from
  code-behind instead: `ExtendsContentIntoTitleBar = true;` as a plain one-line assignment
  in the constructor (after `InitializeComponent()`), same for `SystemBackdrop = new
  MicaBackdrop();`. Neither assignment branches, so both stay legal in `*.xaml.cs`
  under Hard Rule 8.
- **A `Window.Resources` (or likely any `<Resources>`) entry instantiating a custom,
  locally-defined `IValueConverter`.** Use an `x:Bind` **method call** instead of a
  converter resource: a plain `public static` function (e.g.
  `App/Converters/VisibilityHelper.FromBool(bool) -> Visibility`) referenced directly as
  `{x:Bind converters:VisibilityHelper.FromBool(ViewModel.SomeBool), Mode=OneWay}`. This
  needs no resource dictionary entry and builds clean.

If a future Windows App SDK bump fixes either of these upstream, this section (and the
workaround it documents) should be removed in the same commit as the version bump, not
left as unnecessary caution.

## 9. The New run page (issue #63)

`Views/NewRunPage.xaml` binds `NewRunViewModel` only; its code-behind forwards the two drag events to
`Services/DropPaths` (the "is this drag files?" branch and the storage-item read live there, not in a
`*.xaml.cs`) and the drop to `AddDroppedCommand`.

- **Add files** is `BrowseCommand` with a Ctrl+O `KeyboardAccelerator` on the button itself (no handler).
  The picker (`FilePickerService`) needs the main window handle, which `App.OnLaunched` stores in the
  `WindowHandleProvider` singleton. The picker and a dropped folder share one extension list,
  `SequenceFileTypes` (FASTA, GenBank, `.txt`); a dropped folder contributes the files directly inside it.
- **Paste a sequence** is an inline `Expander`, not a `ContentDialog` (decision #516): a dialog needs a
  `XamlRoot` and a click handler, both of which would put logic in code-behind. The live counter is
  `PastedSequenceCounter`. The pasted text is saved off the UI thread under app data. The box is cleared only
  when the paste became a usable pill (or one `Treat as RNA` fixes); a paste with a problem stays in the box,
  leaves no pill, and the problem is said in the status line.
- **Pills** are `InputPillItem`s in a `ListView` whose `DataTemplate` has an `x:DataType`. Kind, records,
  bases and genes are `NewRunPill*` resources. **Notices are copy chosen by code** (`InputNoticeCode`, kept
  beside the validator's English text, which stays a log and worker-parity detail): `InputNoticeCopy` maps each
  code to a `NewRunNotice_*` resource, and a Guards test checks every code has plain copy. A problem is the
  existing `RunError_input_*` copy plus "(record N, base M)". A U is shown as `NewRunPillRnaNotice` with a
  **Treat as RNA** button (`TreatAsRnaCommand`), which re-checks every pill with the RNA flag.
- **Checks never overwrite a newer check.** Each check of a pill takes a number from `BeginChecking`; only the
  latest may `Apply` or `Abandon` (a cancelled check leaves the checking state with `NewRunPillCheckStopped`).
- **A drop never throws.** `DropPaths.ReadAsync` runs in an `async void` handler, so it returns a
  `DroppedItems` (paths, items with no path such as a file inside a zip, a failed flag); the VM turns the last
  two into one status line with one action. `AddPathsCommand`/`AddDroppedCommand` take no cancellation token and
  allow concurrent runs: MEASURED 2026-10-03, a cancelable async command cancels its running execution when it
  is invoked again, which stopped the queued checks of an earlier drop.
- `scripts/check_app_wiring.py` does not resolve `{x:Bind}` inside a typed `DataTemplate` against the page
  ViewModel (the XAML compiler checks it against the `x:DataType`); a plain `{Binding}` there is still checked.
- **Run name** (`RunNamer`, `NameTemplate`, `RunNamePreview`) is built and tested but **not on the page**: the
  runner names the result folder from the input file (MEASURED 2026-10-03, `CloudJobRunner.cs:1286`, #514), so
  a name box would promise something false. The XAML to restore is on #514.
- `RunOptions.InputPath` is a single file (#515), so **Run** runs the selected pill; the page says so in a line
  next to Run once there is more than one pill.
## 10. Settings page (issues #106, #104)

`SettingsPage` is one scrolling `StackPanel` of groups. #106 built the **Diagnostics** group (the Save diagnostics button,
Open folder, the status line and one line saying what is and is never included). #104 adds its groups to the same
panel and keeps this one. The run-error strings say "Choose Save diagnostics in Settings"; `Guards.Tests/SaveDiagnosticsWiringTests`
pins the button label to that wording. Success and failure are shown both as a toast and inline under the button, because
`ToastService` is still a debug-output placeholder.

## Related

[`copy_catalog.md`](copy_catalog.md) (the narration, phase-title, and error-catalog text
this file's tables reference rather than repeat),
[`architecture.md`](architecture.md) (the `JobPhase` state machine this table maps),
[`cloud_design.md`](cloud_design.md) (the error taxonomy `ErrorCatalog` renders),
[Appendix A, sections 2, 5,
6](superpowers/specs/2026-09-18-appendix-a-app-design.md#2-screen-by-screen-ux) (full
screen-by-screen detail, the viewer bridge, and the complete accessibility/DPI/keyboard/
localization/toast/file-association list this doc summarizes).
