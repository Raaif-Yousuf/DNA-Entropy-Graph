# UI conventions: WinUI patterns, copy rules, theme, and the state-to-UI table

**Status (2026-10-15): partly built.** The app exists (issue #61). Built pages: New run,
Run progress, Runs, Results, Viewer and Settings. The Cloud page and the first-run wizard
screens are not built; the Cloud destination shows `PlaceholderPage`. This doc is what
`DnaEntropyGraph.App`'s Views and `DnaEntropyGraph.Presentation`'s ViewModels are built
against. The copy rules in section 2 are mechanically guarded by `Guards.Tests`
(`ReswGuardTests`, `XamlInlineStringGuardTests`, `DialogStringLiteralGuardTests`,
`StringResourceKeyGuardTests`, `CodeBehindBranchingGuardTests`). Screen-by-screen detail
beyond what is summarized here lives in
[Appendix A, section 2](superpowers/specs/2026-09-18-appendix-a-app-design.md#2-screen-by-screen-ux);
the viewer page is described in `docs/viewer.md` (issue #312).

---

## 1. Shell and navigation

`NavigationView` (left rail, compact on narrow widths) with three top-level items, **New
run**, **Runs**, **Cloud**, and a footer **Settings**. The title bar is extended with a
Mica backdrop. A status pill sits top-right at all times: `Signed in as x@y - project-id -
$0.00 this month`. Active runs show a badge on the **Runs** item so a user who navigates
away never loses track of a job in progress. Keyboard: `Ctrl+N` new run, `Ctrl+O` add
files, `Ctrl+Shift+V` paste sequence, `Ctrl+Enter` run, `Esc` cancel a dialog, `F5` refresh
(Cloud/History), `Ctrl+,` Settings, `Ctrl+L` focus the locus box in the viewer, `Alt+Left`
back. Viewer page keys (#247): Left/Right pan a quarter window, `+`/`-` zoom, `Home` or `1` whole record,
`2`/`3` 10 kb / 1 kb about the centre, `Ctrl+L` focus the locus box. bridge.js only forwards the key
(`{evt:"key"}`); `Viewer/ViewerKeyboard.cs` decides, so the rules are unit tested.

Shell behaviour MEASURED 2026-10-02 (#489, #490): the header is the localized
`PageTitle_<key>` string (`ShellViewModel.CurrentPageTitle`), never the page key; New run
is selected at startup (`NavigationView.Loaded`), so header, selection and frame agree; a
destination with no page registered in `Startup/NavigationRoutes.cs` shows the one generic
`PlaceholderPage` instead of an empty frame; the OS window title is `AppDisplayName`.
Window placement: nothing is saved while the window is minimized or maximized, a saved
rect that is the minimized sentinel (-32000) or smaller than 320x200 is refused, and a
first launch is 1280x800 DIPs (scaled by the window DPI, kept within 5% of the work area).

Session restore (#244): `RecordingNavigator` wraps the app's `INavigator`, so every navigation (nav rail or any ViewModel) saves
`session.page` and `session.run` (`SessionStore`). Only pages reopenable from a key are saved: New run, Runs, Cloud, Settings, and
Results or Run progress with their run id; the viewer and the wizard leave the last page alone. At startup `MainWindow.Loaded`
awaits `ShellViewModel.PrepareSessionAsync`, selects `StartupNavKey` (the remembered nav page, or Runs for a remembered run), and
the first `NavigateTo` of that item opens the run page on top, once. A remembered run that is gone opens New run; a Run progress
page whose run completed while the app was closed opens Results. An Open with or notification launch is routed by
`ActivationRouter` after the shell is ready, so it still wins. Both keys are `Excluded` in `SettingsCatalog` (not shared).

## 2. Copy rules (CLAUDE.md rule 13; this is the rationale and the worked examples)

- **Every user-visible string lives in `Strings/en-US/Resources.resw`**, referenced by
  `x:Uid`, never inline in XAML or C#. This is what makes the string set auditable and
  localizable in one place, and is what the `.resw`-scanning guards (`ReswGuardTests`, `XamlInlineStringGuardTests`) check against.
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
  Guard (#176): `RunErrorActions` (Core) declares the one action of every `RunErrorCodes` code, and
  `ErrorMessageActionGuardTests` fails when a code has no row or its `RunError_*` message does not say that
  action. Adding a code means adding its row and its message in the same commit.
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
applied live (#639): the Appearance `RadioButtons` selection is `SettingsViewModel.ThemeIndex` (two-way), and a change
applies through `IThemeApplier` (Presentation) and then saves, so a click and an arrow key take one path. The App
implementation, `WindowThemeApplier`, sets `RequestedTheme` on the window's content root (`RootGrid`, not the
`NavigationView`: the title bar and the Mica backdrop sit outside it, MEASURED 2026-10-03: with the theme on the
`NavigationView` only, a Light app on a dark system kept a dark title bar and pane) and recolours the caption buttons from
`CaptionButtonPalette` (the system draws them in the system theme: MEASURED 2026-10-03, white glyphs on a light bar,
app Light on dark system). `MainWindow` calls the same `Apply` with the saved theme at startup. The apply happens before the
save, so a locked settings file (#558) still changes the window for the session. The embedded viewer
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
- Keyboard (issue #110, Appendix A section 6): Alt+Left is a `KeyboardAccelerator` on
  `RootGrid` in `MainWindow.xaml` with `Invoked="{x:Bind ViewModel.GoBack}"` (x:Bind can
  bind an event to a parameterless ViewModel method). Ctrl+N is on the New run nav item and
  Ctrl+, on the Settings nav item (invoking an accelerator on a `NavigationViewItem` selects
  it); the comma has no `VirtualKey` name, so it is added in `MainWindow.xaml.cs` as
  `(VirtualKey)188`. Ctrl+O, Ctrl+Enter and F5 sit on the button they press. Ctrl+Shift+V
  runs `NewRunViewModel.PasteFromClipboard` through `IClipboardTextReader`. Esc closing a
  `ContentDialog` is the platform default. Nav `AccessKey`s live in the .resw
  (`NavX.AccessKey`). Not done: Ctrl+L (the locus box belongs to igv.js inside WebView2, not
  to a XAML control) and F5 on Cloud (that page is still the placeholder).
- Guards: `IconOnlyAutomationNameGuardTests` fails on an icon-only button with no
  `AutomationProperties.Name` (attribute, or `<Uid>.AutomationProperties.Name` in the .resw);
  `KeyboardShortcutWiringTests` fails if a shortcut or the progress page's live regions
  leave the XAML. The progress page marks the stage title and the detail line
  `LiveSetting=Polite`.
- High contrast: only theme resources for colours (`ThemeResource`), never a hard-coded
  brush; the walkthrough is a ToTest row.

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

### In-app messages (issue #585)

Every `IToastService.ShowToast(title, body, severity)` call becomes a message bar (an `InfoBar` at the bottom of
`MainWindow`), not a Windows notification. The service is `InAppMessageCenter` (Presentation, singleton, no WinUI types);
`ShellViewModel.Messages` exposes the same instance and the InfoBar binds to it. One message at a time, a new one replaces
the old. Severity decides lifetime: `Info` and `Success` close after 6 seconds (a `TimeProvider` timer, so tests never
sleep), `Warning` and `Error` stay until the user closes the bar. Any thread may call it; the state change goes through
`IDispatcher`. A ViewModel picks the severity per message (the Runs copy carries it in `RunsMessage`): a failed or refused
delete is `Error`, a finished one `Success`. The Windows toast with a deep link for a finished run while the app is in the
background (#107) is a separate channel and a second implementation behind the same seam; this section does not build it.
`Guards.Tests/ToastServiceWiringTests` resolves the real registration and fails if a message does not reach the shell.
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

**Guard (issue #423).** `scripts/check_xaml_shapes.py` (run by `premerge.py`, tests in
`scripts/tests/test_check_xaml_shapes.py`) finds these shapes by text before the compiler runs and prints
`file:line: SHAPE: what to do instead`. MEASURED 2026-10-16 on Windows App SDK 1.8.250906003, each shape added
to the otherwise-clean `MainWindow.xaml` and built with `dotnet build` of the App project: a clean tree built;
`ExtendsContentIntoTitleBar="True"` on `<Window>`, a `Window.Resources` entry for a local `IValueConverter`,
and `IsEnabled` on a `StackPanel` (which has no such property) each failed with `XamlCompiler.exe ... exited
with code 1` (MSB3073) and no other message. A local converter in `Page.Resources` (scratch copy of
`PlaceholderPage.xaml`) BUILT CLEAN, so the converter rule hard-fails only in `Window.Resources`; a local
converter in any other scope, or a `*Converter` element that is not a local class, is a WARN that never fails
the gate (THEORY (unverified): they crash too). Grid, Border, RelativePanel, Canvas, TextBlock and Image are
also flagged for `IsEnabled`; THEORY (unverified): they fail the same way.

If a future Windows App SDK bump fixes either of these upstream, this section (and the
workaround it documents) should be removed in the same commit as the version bump, not
left as unnecessary caution.

## 9. The New run page (issue #63)

`Views/NewRunPage.xaml` binds `NewRunViewModel` only; its code-behind forwards the two drag events to
`Services/DropPaths` (the "is this drag files?" branch and the storage-item read live there, not in a
`*.xaml.cs`) and the drop to `AddDroppedCommand`.

`NewRunViewModel` is a singleton (issue #527): a new page instance resolves the same ViewModel, so the files,
the selection, the paste box and the RNA choice survive a trip to Runs and back (MEASURED 2026-10-03: it was
transient and the list was empty after returning). It empties itself only when a run starts successfully
(`StartRunAsync`): Items, selection, PasteText and IsTreatingAsRna. Pasted copies of the files that were NOT run
are deleted as a Remove would; the copy behind the run that started stays (a retry restages from its recorded
path). The paste box (closed), the status line and Treat as RNA also reset. A start that fails keeps everything.
What stays across runs, on purpose: the preset, the ticked output files and the run target (they are the user's
intent for runs in general and match Settings); `PresetPickerTests` pins it. Not persisted across app restarts.
Back to the page never re-delivers its Open-with files: the page forwards `NavigationArrival` (parameter plus
`NavigationMode` as a number) and only a New navigation's parameter is acted on. `NewRunPage.OnNavigatedFrom`
calls `Bindings.StopTracking()`, so the long-lived ViewModel (its `PropertyChanged` and `Items`) does not keep a
left page alive; the page is not cached, a new instance is made on every visit. THEORY (unverified): the
memory effect itself was not measured, only that the release is explicit.

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
- **Model and graphics card pickers (issue #87).** The "Model and graphics card (GPU)" expander is `GpuPlanViewModel`
  (a thin shell over the pure `ModelGpuLinker`, over `GpuCatalog.Shipped`). Picking a model raises an unpinned card to
  the cheapest offered one that runs it; picking a card moves an unpinned model to the largest offered one that fits;
  each side has a **Lock** toggle that keeps it where it is. A card that cannot run the chosen model shows a lock icon
  and the reason in the list but can still be picked (the override). A pair that will not run shows a warning and a
  "Run anyway" tick, and **Run** waits for the tick; any later change withdraws it. What the linker moved on its own is
  said in a line under the pickers (an in-page line, not a toast: the page has no toast host). The model picker is
  hidden while the catalog offers one model (today). The row that proves the wiring: choose the A100 and the
  `RunOptions.GpuTier` the page starts is `A100_40`, so the request names `a2-highgpu-1g` and the estimate is asked for it.
## 10. Settings page (issues #106, #104)

`SettingsPage` has **no title of its own**: the shell's `NavigationView` header already shows "Settings" (#627), the same
pattern every page follows. It is one scrolling `StackPanel` of groups. **The group pattern (#639):** each group is a
`StackPanel Spacing=8` holding a `SubtitleTextBlockStyle` header (its own `Settings<Group>Header` resw key) and a `Border`
with `SettingsGroupCardStyle` (a `Page.Resources` style: card fill and stroke, 16 padding) containing the group's
controls; groups are spaced 24 apart. A new group (#640 to #644) adds one such `StackPanel`; no
CommunityToolkit SettingsControls dependency. **Appearance** (#639) is one `RadioButtons` (Light, Dark, Use system) with `SelectedIndex` bound two-way to
`ThemeIndex`: loose `RadioButton`s with `Command` only run on Click, and an arrow key moves their focus without changing the
selection (MEASURED 2026-10-03), so use the `RadioButtons` control for any choice set that must work from the keyboard. #106 built the **Diagnostics** group (the Save diagnostics button,
Open folder, the status line and one line saying what is and is never included). #104 adds its groups to the same
panel and keeps this one. The run-error strings say "Choose Save diagnostics in Settings"; `Guards.Tests/SaveDiagnosticsWiringTests`
pins the button label to that wording. Success and failure are shown both as a toast and inline under the button, because
`ToastService` is still a debug-output placeholder.

### Share settings (issue #242)

The last group on the page. **Export settings** and **Import settings** drive `SettingsTransferViewModel`; the import shows a
preview (the plain name of each setting, its current value, its new value) and writes only on **Apply these changes**.
What may travel is the allowlist `Core/SettingsPortability/SettingsCatalog`: every key is either `Exported` (with a validator, so
an imported value is one the app itself would have written) or `Excluded` (with a reason). `Guards.Tests/SettingsKeyCatalogGuardTests`
scans the source for settings keys and fails when one is in neither, so **a new setting must be added to the catalog in the
same commit** (decide: share it, or say why not; a path, an id, a secret or a developer switch is always excluded). The file
carries `format` and `schema`; a newer schema is refused with its own message. An applied import sends `SettingsResetMessage`,
which every settings group already treats as "re-read the store".

## 11. The setup wizard (issue #99)

One page (`WizardPage`, route `Wizard`, opened from the status pill in the title bar), not a stepper: welcome, sign in, then OK/Fix rows. `WizardViewModel` owns sign-in, the project step (a `SetupStepRow`, the list of projects, Create) and Finish; `SetupStepsViewModel` owns the rows for billing, services, storage and identity, and the GPU allowance, which are also the Settings health report ("Run setup check again"). All Google calls are `SetupCheckService` (`Core/Setup`), which never throws a Google failure: a failure is a `SetupStepResult` with a code (`SetupErrorCodes` or `AuthErrorCodes.ProjectInvalid`) and one `SetupAction` whose kind decides the button: Retry, OpenLink (a `HyperlinkButton`, plus **Check again**), CopyText (clipboard, with the billing request naming the account that was refused), PickProject, PickBillingAccount (a list of buttons, each linking that account), CreateProject.

- A row never shows colour alone: the badge text says OK, Fix, Checking, Skipped or Not checked yet, and its live region announces changes.
- Rows run in the preflight order of CLAUDE.md: billing, services (Compute API), GPU allowance, then storage and the worker account, so nothing is created in a project whose allowance or permissions already rule a run out. The chain stops at the first red row. **Skip for now** is offered only when the allowance was read and is zero (or could not be read for a reason with no action of its own); permission, org policy, API-off and billing errors abort with their one named action, and Finish never opens over one. Skipping carries on to storage.
- Every button shown does something: project actions on a step row (Create project, Pick an existing project) are handed to the wizard's project step, a link action with no page opens the manual guide, and choosing a billing account is the list below the row (the row has no second button).
- Checks are serialized (one gate in `SetupStepsViewModel`, one in `WizardViewModel`): a row's Check again waits for Run all, Finish is off while anything runs, an account change cancels running checks, and a failure that is not a Google error becomes a "Try again" row, never one stuck on Checking.
- Every Fix row has **Do it by hand** (`docs/gcp_setup_manual.md`).
- Lists and per-item buttons are commands on the item (`ProjectChoice`, `BillingAccountChoice`, `SetupStepRow.ActionCommand`), so the page has no code-behind logic (Hard Rule 8).
- The GPU row's **Ask Google for a GPU** (`SetupStepRow.AskCommand`, shown only when the allowance was read and is zero) runs `GpuQuotaRequester` (L4 in `SetupStepsViewModel.GpuAskRegion`, then the project-wide cap) and leaves one message and one button per outcome: granted checks the step again, requested offers Check again, no billing offers Check billing, ineligible or refused offers Google's quota page, a failure offers Try again.
- Not built yet: the pipeline test and the real-GPU test step (step 8, #97), the local engine step, and "Run setup check again" in Settings.

## 12. Windows notifications and activation (issue #107)

A run that finishes (done, partly done) or fails while the app window is not in front shows a Windows notification with an **Open run** button; clicking the notification or the button opens that run's Results page, also when the app was closed. The in-app message bar (`IToastService`) is unchanged and still shows for done and partly done; a failed run is a desktop notification only. Both sit behind the Settings switch **Notify when done**.

- `IDesktopNotifier` (Core) is implemented by `AppNotifier` (App, Windows App SDK `AppNotificationManager`, which works unpackaged once `DesktopActivation.Register` has run). It stays silent while the window is in front (`IAppWindow.IsInForeground`).
- The click carries `run=<job id>` (`AppDeepLink`, Core). The text comes from outside the app, so only a plain job id (letters, digits, `-`, `_`, at most 64) is acted on. `ActivationRouter` (Presentation) brings the window forward and navigates; every route into the app (notification click while running, launch from a notification, a later launch) ends there.
- `SingleInstance` (Program.Main) keeps one app per data folder: a second launch hands its arguments to the first and exits. The key includes the data folder, so a `--profile` sandbox is its own instance. #108 (file association) reuses it.
- **Open with (issue #108).** The Settings switch (Files group, `FileAssociationSettingsViewModel`) adds or removes HKCU "Open with" entries for `.gb .gbk .genbank .fasta .fa .fna` (`FileAssociationService`, Core; `CurrentUserClasses`, App): a program id whose open command is this exe, listed under each extension's `OpenWithProgids`. It never sets an extension's default value. A launch with sequence files (the second instance hands its command line to the first) goes through `ActivationRouter` to New run, as a `DroppedItems` navigation parameter that `NewRunPage.OnNavigatedTo` forwards to `NewRunViewModel.ReceiveNavigationParameterCommand`. Only local full paths to sequence types are accepted (`AppDeepLink.TryGetInputFiles`: no UNC or device root in either slash form, no wildcard, no other file type). DECISION (agent-made, reversible): a drive letter mapped to a network share is allowed, because the mapping is the user's own and Open with is chosen on that file in Explorer. Files go to the New run screen that is already showing (`NewRunIntake`, attached by `NewRunPage.OnNavigatedTo`) so a draft survives, and a launch is held until the shell has made its first navigation (`ShellReadiness`, marked by `MainWindow`). Enable is all or nothing, Disable removes the keys it created once empty, both are refused on a `--profile` sandbox (the entries live in the real registry) and unless the process is `DnaEntropyGraph.App.exe` (never `dotnet.exe`); the Velopack uninstall hook (`Program.OnBeforeUninstall`) also removes them.
- Not built: the idle-VM and keep-alive-expiry notifications (the reconciler in Core/Cloud has no event for them yet).

## 13. The entropy overview and sparklines (issue #118)

- Data path: `Core/Tracks/BedGraphReader` reads the raw `<name>.entropy.bedgraph` into **binned means** (16 bases per bin, so a
  100 Mb contig is about 6 million floats, not 400 MB), `EntropyOverviewSource` finds that file one folder down in the run
  folder, never entering a link, and reads each contig's `seam` from the neighbouring `provenance.json` (a missing or broken
  file costs only the seam). A row that is not four fields makes the whole file unreadable (null), never a half chart.
- Results: `ResultsViewModel.Overview` (the first contig) is loaded after the page's stats, off the UI thread, and bound to
  `Controls/EntropyOverviewChart` (ScottPlot.WinUI `WinUIPlot`, mouse input off). `EntropyPlotBuilder` is plain ScottPlot with no
  WinUI types, so the **Save picture** button (`OverviewPngExporter`, light colours on white, 1600 x 480) draws exactly what the
  chart draws and is tested headless in `App.UiTests` (a real PNG with the real SkiaSharp natives). The PNG goes in the run's own
  folder, named from the contig with only letters, digits and `_-.` kept.
- Runs: `RunListItem.Sparkline` (48 points, `TrackResampler`) is filled after the list shows, one finished run at a time, by
  `HistoryViewModel.LoadSparklinesAsync`; `Controls/EntropySparkline` is a plain `Polyline` (a ScottPlot control per card would
  own one GPU surface each). `EntropyOverviewSource` remembers the last 64 tracks by path, size and write time.
- Drag-select (#699): dragging a span on the strip opens the embedded viewer at that range (`OverviewSelection` clamps it and ignores a click; the Viewer page key takes a `ViewerRunTarget` with a locus, a plain folder string still works). Not done: a contig picker for a
  multi-contig run (the first contig is shown).
- Licences (Rule 21): ScottPlot.WinUI 5.1.59, ScottPlot, SkiaSharp, SkiaSharp.Views.WinUI and HarfBuzzSharp are all MIT
  (`THIRD-PARTY-NOTICES.md` regenerated).

## 14. The Cloud page (issue #105)

`CloudPage` (route `Cloud`) over `CloudResourcesViewModel`. Refresh lists through `InstallationCleanup.InventoryAsync` (this installation's computers and disks by label, job folders by manifest `createdBy.installationId`) and `IComputeGateway.ListAppVmsAsync` for the rest. A computer another installation labelled for this app is a read-only row tagged "Another computer" with no commands (Hard Rule 9; DECISION, reversible). Cost comes from `IPricingSource` (machine price while running, attached disks per day while stopped; empty when the price list cannot be read). Row commands live on `CloudVmRow`. Delete (one) and Delete everything go through `IDialogService.ConfirmAsync`; Delete everything names the counts and shows `CleanupReport` item by item, with the soft delete retention of job folders. Not built: typed confirmation, deleting the service account and role, a banner for a running computer with no run on this PC (the banner shows any running computer).

## 15. The slow-run notice and the stall question (issue #675)

`RunStallCoordinator` (Presentation/Services) is the runner's `OnNotice` and `OnStall` in production (ServiceRegistration). A slow-stage notice is a `RunNoticeMessage` that `RunProgressViewModel` shows as a warning InfoBar for its own run only (cleared when the runner says the stall cleared and when the run ends). The stall question is `IChoiceDialogService.ChooseAsync` (a three-button `ContentDialog` on the main window's `XamlRoot`, implemented by `DialogService`): Stop, Delete, Keep waiting, with Keep waiting as the close button and default. A dialog that cannot be shown (no window yet) or that the runner withdrew answers Keep waiting, never a deletion; the runner's own answer timeout and run time limit still end a run nobody answers.

## Related

[`copy_catalog.md`](copy_catalog.md) (the narration, phase-title, and error-catalog text
this file's tables reference rather than repeat),
[`architecture.md`](architecture.md) (the `JobPhase` state machine this table maps),
[`cloud_design.md`](cloud_design.md) (the error taxonomy `ErrorCatalog` renders),
[Appendix A, sections 2, 5,
6](superpowers/specs/2026-09-18-appendix-a-app-design.md#2-screen-by-screen-ux) (full
screen-by-screen detail, the viewer bridge, and the complete accessibility/DPI/keyboard/
localization/toast/file-association list this doc summarizes).
