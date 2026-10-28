# The viewer: igv.js in WebView2, and the external viewers

Last updated 2026-10-15, against `main` at a51d84d. This file is the home of the viewer
integration design that used to live only in
[Appendix A section 5](superpowers/specs/2026-09-18-appendix-a-app-design.md) (issue #312). Where the
code and the appendix disagree, **this file and the code win**; the differences are listed in
section 9.

Two ways to look at a run's entropy track:

1. **The embedded viewer**: igv.js (MIT, vendored) inside a `WebView2` control on the Viewer page.
2. **External viewers**: the Results page's **Open in IGV** and **Open in Geneious** buttons hand the run's
   files to the desktop programs.

Where the code lives:

| Piece | File |
| --- | --- |
| State, error mapping, the held `load` command | `app/src/DnaEntropyGraph.Presentation/ViewModels/ViewerViewModel.cs` |
| Which files become which igv.js tracks | `app/src/DnaEntropyGraph.Presentation/Viewer/IgvLoadPlanner.cs` |
| Hosts, URLs, navigation allow-list, runtime probe | `app/src/DnaEntropyGraph.Presentation/Viewer/ViewerUrls.cs` |
| The WebView2 wiring (the only code that touches the control) | `app/src/DnaEntropyGraph.App/Services/IgvViewerHost.cs` |
| The page igv.js runs in and its bridge | `app/src/DnaEntropyGraph.App/Assets/viewer/` (`index.html`, `bridge.js`, `dark.css`, `viewer.css`, `igv.min.js`) |
| Open in IGV / Geneious | `app/src/DnaEntropyGraph.Presentation/Services/ExternalViewerOpener.cs`, `app/src/DnaEntropyGraph.Core/Viewers/` |

Hard Rule 8 shapes the split: every decision (which files, which URLs, which error, what to say) is in
`DnaEntropyGraph.Presentation` and is tested without a UI thread; `IgvViewerHost` only connects the control to
it. Every user-visible string is in `Resources.resw` (Hard Rule 13).

## 1. The data feed: two virtual hosts, no server

`file://` is blocked inside WebView2, and the app runs no web server. Both the viewer bundle and the run's files
are served through `CoreWebView2.SetVirtualHostNameToFolderMapping`:

| Host | Maps to | Access | Set |
| --- | --- | --- | --- |
| `https://viewer.deg/` | `<app folder>\Assets\viewer` | `Deny` | once, when the control starts |
| `https://run.deg/` | the run's output folder | `Allow` | on every change of `ViewerViewModel.MapRunFolder`: clear, then set |

MEASURED 2026-10-02: with `DenyCors` on `run.deg`, igv.js's fetch of `https://run.deg/<name>.fasta` from the
`viewer.deg` page fails ("Error accessing resource ... status: 0"), so the original design's `DenyCors` cannot
work. `Allow` is used instead. This is safe only because the sole page that can reach `run.deg` is the
navigation-locked `viewer.deg` bundle (section 4).

The ViewModel sets `MapRunFolder` **before** it posts the `load` command, so the host has mapped the folder by
the time igv.js asks for a file.

## 2. Which files become tracks (`IgvLoadPlanner`)

`IgvLoadPlanner.Plan(runFolder)` reads the folder's top level and returns either a `load` command or a named
failure. File names are the worker's writer names ([`science_and_formats.md`](science_and_formats.md)).

- **Reference:** the one `<name>.fasta` that has an entropy file with the same stem, loaded with
  `indexed:false` (the worker writes no `.fai`, so igv.js reads the whole FASTA into the page). A FASTA over 50 MB (up to
  1 GB) is loaded `indexed:true` with an `indexURL` on `https://fai.deg/`, a generated `.fai` in `AppDataRoot.FastaIndexDirectory`
  (issue #497); `FastaIndexWriter` streams the file, so it never sits in memory.
- **Entropy tracks:** `<name>.entropy.bedgraph`, and in the "separate tracks" direction mode also
  `<name>.entropy.fwd.*` and `<name>.entropy.rev.*`. bedGraph is preferred over `.wig` for each track; at most one
  file per track. Each is a `wig` track fixed to **0 to 2 bits with autoscale off** (Hard Rule 3), named
  "Entropy (bits)", "Entropy, forward (bits)" and "Entropy, reverse complement (bits)".
- **Genes:** `<name>.genes.gff3`, if present, as an `annotation` track named "Genes".
- **Regions (issue #552):** `<name>.regions.bed`, else `<name>.regions.gff3`, as an `annotation` (feature) track named "Entropy regions". At most one.
- **Smoothed tracks (issue #552):** `<name>.entropy.smooth<W>.bedgraph` (or `.wig`) are NOT in the load command. `IgvLoadPlan.SmoothedTracks` lists them by window; `ViewerViewModel.SmoothedTracks` turns each into a switch on the Viewer page (off at first, so the raw track stays the one drawn). Switching one on sends `{cmd:"track", op:"add", config}` (a `wig` track fixed to 0 to 2 bits); off sends `{cmd:"track", op:"remove", name}`. If the page cannot load a track it posts `{evt:"trackFailed", name}`; the model turns that switch back off and shows a message. The switches work only while the page shows the run, and a redrawn page starts with them all off.
- URLs are `https://run.deg/` plus the URL-escaped file name.

Failures never guess: no FASTA (`NoSequenceFile`), no entropy file for any FASTA (`NoEntropyTrack`), more than one
FASTA with entropy (`AmbiguousSequenceFile`), a FASTA over **1 GB** (`SequenceTooLarge`, `ViewerLimits.MaxIndexedFastaBytes`), one the indexer refuses (`IndexFailed`), or a
missing folder. Each maps to one `ViewerStatus` and one message with one action. The appendix's fallback for
inputs above about 50 MB: the `.fai` half is built (#497); streaming `features` arrays through `PostWebMessageAsJson` is **not
built**, and the viewer refuses above 1 GB.

## 3. The bridge (`bridge.js`)

Messages are JSON through `window.chrome.webview` (`postMessage` out of the page, `PostWebMessageAsJson` into it).

Commands, host to page:

| Command | Effect |
| --- | --- |
| `{cmd:"load", config}` | Removes any existing igv browser, then `igv.createBrowser` with `config.reference` and `config.tracks` |
| `{cmd:"theme", dark:bool}` | Toggles the `dark` class on `<body>` |
| `{cmd:"track", op:"add", config}` / `{cmd:"track", op:"remove", name}` | Adds or removes one optional track by name (`browser.loadTrack` / `removeTrackByName`); adding twice or removing an absent one does nothing |
| `{cmd:"goto", locus}` | `browser.search(locus)` |
| `{cmd:"snapshot"}` | Replies with the browser's SVG |

Events, page to host:

| Event | Meaning |
| --- | --- |
| `{evt:"ready"}` | The page loaded; sent once per page load, so it is also sent after a reload |
| `{evt:"loaded"}` | `createBrowser` finished |
| `{evt:"error", message}` | **Fatal**: the run could not be drawn |
| `{evt:"warning", message}` | A stray page error or an unhandled promise rejection. The ViewModel ignores these |
| `{evt:"locusChanged", locus}` | The visible range changed (`chr:start-end`, 1-based) |
| `{evt:"snapshot", svg}` | Reply to `snapshot` |

Ordering rules that exist because each one was a way to draw nothing:

- The ViewModel **holds** the `load` command until `ready` arrives (never posts into a page still loading) and
  **keeps** it after sending, so a reload (a new `ready`) or **Try again** redraws the same run.
- `bridge.js` runs commands one at a time through a promise queue, so two quick `load`s cannot leave an
  orphaned igv browser behind.
- `ViewerAttachGate` ensures one attach per control, and a page left during the asynchronous start-up is never
  subscribed.

## 4. Security settings

Set in `IgvViewerHost.Configure`; each is a deliberate hardening, not a default:

- `AreDevToolsEnabled = false` in release builds (true in `DEBUG` only; #69 owns a real developer-mode switch).
- `IsWebMessageEnabled = true`, but a web message is passed to the ViewModel **only if its source is
  `https://viewer.deg`**.
- `AreDefaultContextMenusEnabled = false`, `AreDefaultScriptDialogsEnabled = false`,
  `AreBrowserAcceleratorKeysEnabled = false`.
- `NavigationStarting` cancels every navigation except `https://viewer.deg/...` (`ViewerUrls.IsAllowedNavigation`:
  https, that host, default port, no user info). `NewWindowRequested` is handled (swallowed) and
  `DownloadStarting` is cancelled.
- `index.html` carries a Content-Security-Policy: `default-src 'none'; script-src 'self'; connect-src
  https://viewer.deg https://run.deg; frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'`
  and so on. Scripts are the two files beside the page (no inline script, no `eval`), and igv.js is vendored, so the
  viewer works with networking disabled and igv.js cannot phone home.
- The WebView2 profile lives under the app data root (`AppDataRoot.WebView2Directory`), not the user's browser data.

## 5. Dark mode

The control's `ActualThemeChanged` event calls `ViewerViewModel.SetDarkMode`, which posts `{cmd:"theme"}`; the host also sets the initial
value from the control's `ActualTheme` before the first navigation. `dark.css` applies when `<body>` has the `dark`
class. igv.js draws its tracks, ruler and ideogram on canvases with white backgrounds that CSS selectors cannot
recolour, so the whole `#igv` element is inverted and hue-rotated back (`filter: invert(0.9) hue-rotate(180deg)`):
white becomes near-black and the base and gene colours keep their hue. This replaces the appendix's per-element
override and its "swap the entropy track colour" note.

## 6. Failure modes and the one action each

| Situation | `ViewerStatus` | Action offered |
| --- | --- | --- |
| WebView2 Runtime missing (`GetAvailableBrowserVersionString` throws or returns empty) | `RuntimeMissing` | Link to the Evergreen bootstrapper (`ViewerUrls.RuntimeDownloadUrl`) |
| The control cannot start (corrupt runtime, unwritable profile folder) | `ViewerError` | Try again |
| The WebView2 renderer or browser process died | `ViewerError` (keeps the run) | One automatic `Reload` per attach; the new `ready` re-sends the `load`. No button: the message says to close the run and open it again |
| igv.js reports `{evt:"error"}` | `ViewerError` | Try again |
| A planner failure (section 2) | the matching status | Named in the message; for a FASTA over 50 MB it says to open the files in IGV |

## 7. External viewers

**Results page, Open in IGV** (`ExternalViewerOpener.OpenInIgvAsync`):

1. `ExternalViewerFiles.ForIgv` picks the genome (the first `.fasta`), the `.bedgraph` tracks and the `.gff3`
   files (not the `.geneious.gff3` one). A multi-input run has one folder per input; **only the first input is
   sent** (IGV shows one genome at a time) and the user is told (`OpenedFirstInputOnly`). No tracks is `NoFiles`;
   no FASTA is `NoGenome` (tracks drawn on whatever genome IGV holds would be a wrong picture).
2. If IGV's **batch port** answers (default 60151, set in IGV under View, Preferences, Advanced, and in this app's
   Settings, External viewers), the app sends `new`, `genome "<fasta>"`, then one `load "<path>"` per track.
3. If the port **refuses the connection** (IGV is not running), the app starts IGV with the tracks joined by commas
   and `-g <genome>`. A track path with a comma is left out of that argument (IGV splits on commas).
4. If IGV is running but **rejects** a command, **does not reply**, or **does not accept** the connection (probably
   busy), the app reports it and never starts a second IGV.

**Open in Geneious** starts Geneious with the `.gb` files, then every `.gff3` (the gene features and the Geneious
heatmap track).

The program is the saved path, else the autodetected one (`ViewerLocator`: `IGV-<version>` or `IGV_<version>`, newest
first, `igv.exe` then `igv.bat`; `Geneious*`; under Program Files, Program Files (x86) and the per-user Programs
folder), else the user's **Browse** choice, which is saved. A saved program that will not start is forgotten, so
the next press looks again.

Safety (`ViewerLaunchSafety`, `IgvBatchCommands`): a `.bat` or `.cmd` runs through `cmd.exe`, which reads
`& | ^ % < > ( ) !` and a quote in an argument as syntax, and `ProcessStartInfo.ArgumentList` does not escape them,
so an output folder called `x&calc` would run `calc`. Such a launch is refused (`UnsafeProgramArguments`). IGV's
batch port carries one command per line, so a path with a quote or line break is refused (`IgvPathUnsendable`).
Both viewers only **read** the run's files (Hard Rule 14); no path is ever logged.

## 8. Tests

`app/tests/DnaEntropyGraph.Presentation.Tests` (`ViewerViewModelTests`, `ResultsViewModelViewerTests`,
`ExternalViewerOpenerTests`, `ViewerCopyTests`), `app/tests/DnaEntropyGraph.Core.Tests/Viewers/`, and
`app/tests/DnaEntropyGraph.App.UiTests/ViewerProcessLauncherTests.cs`. None of them runs igv.js in a real
WebView2. See [`ToTest.md`](ToTest.md) for what is unproven on a real build.

## 9. Where this differs from Appendix A section 5

| Appendix said | As built |
| --- | --- |
| `Profile.PreferredColorScheme` follows the app theme, `dark.css` overrides igv chrome | A `dark` body class and an invert/hue-rotate filter (section 5) |
| `DenyCors` on the run folder | `Allow` (MEASURED 2026-10-02, section 1) |
| Fallback for inputs above about 50 MB: chunked `features` arrays and a local `.fai` | The local `.fai` is built (#497); chunked arrays are not, `SequenceTooLarge` above 1 GB |
| A native ScottPlot overview chart and a contig dropdown outside the WebView | Not part of the Viewer page. Tracked separately, not in this file |
| When the runtime is missing, hide the viewer and keep the native chart | The viewer shows the named-action error; there is no native chart fallback yet |
| `Process.Start(geneious.exe, files)` | Plus program discovery, launch-safety refusals and the IGV batch-port protocol (section 7) |
