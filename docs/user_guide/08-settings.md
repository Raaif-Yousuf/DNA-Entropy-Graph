# 8. Settings

Open **Settings** in the left-hand list. The page is a stack of titled groups, one card each.

## Appearance

Choose **Light**, **Dark** or **Use system**. The whole window changes the moment you choose,
including the genome viewer, and the choice is remembered the next time you open the app.
**Use system** follows the Windows light or dark setting. If the app cannot remember your
choice (another copy of the app has the settings file open), it tells you; the theme still
changes for now. Close the other copies, then choose it again.

## Run defaults

The files a new run starts with, one list for a GenBank file and one for a FASTA file or a pasted sequence. Tick or untick a file and it is remembered at once; the New run page shows the same ticks, and a choice you make there for a run is remembered here too. At least one file must stay ticked. WIG and the IGV track are alternatives (the worker writes one track format per run), so ticking one unticks the other.

## Files

Where the app keeps its own data: the app data folder, the run folders (each keeps a saved copy of the input file it ran, with how much space those copies and your pasted sequences take) and the log files. **Open folder** shows each in Windows Explorer. Nothing here deletes anything: your results and your input files stay until you remove them yourself. If Windows cannot open a folder, open File Explorer and paste the folder path shown on the page into its address bar.

## Cloud results

Each run's results are also stored in a storage area in your own Google Cloud project (a bucket), so you can download them again. **Keep stored results for** sets how many days Google keeps them before deleting them automatically: 7, 30, 90 (the default), 180 or 365. The choice is saved at once and applies when your next run starts, not to a run already going. Choosing fewer days than the bucket already keeps does not shrink it: other copies of the app, on other computers using the same Google project, may share the bucket, and the app never deletes results early on their behalf. After every choice the line under the box says the same thing, because the Settings page cannot see how long the bucket already keeps results: it applies when your next run starts, and a time shorter than the one already set on the bucket never shortens it, so older results may stay. To remove them sooner, delete them yourself in the Cloud Storage page of your Google Cloud project. (A button that shortens the bucket on purpose, after a confirmation, is planned as issue 598.) To lengthen it, choose a longer time. If another copy of the app has the settings file open, the group says so; close the other copies and choose again.

## Notifications

Three switches for what the app does when a run finishes: show a message (on), open the folder that holds the results (off) and open the Results page (off, so a finished run never pulls you away from what you are typing). When the app is not the window in front (or is minimized), the message also appears as a Windows notification, for a run that finished and for one that failed; **Open run** on it (or a click on the notification) opens that run's Results page, even if you had closed the app. If you do not see it, check that Windows Focus Assist is not hiding notifications. They are saved at once and apply from the next run that finishes, whichever page you are on. If another copy of the app has the settings file open, the group says so; close the other copies and change the switch again.

## Spending

A monthly warning cap in US dollars and a **Hard stop** tick box, saved at once. The group also shows your estimated spending so far this month. An empty box means no cap; text that is not a number above zero is refused with a message and the cap stays as it was. See [Costs and cleanup](06-costs-and-cleanup.md) for what the warning and the hard stop do.
## About and updates

Shows the version you are running, which updates to offer (**Stable**, or **Beta** for early versions), a switch for checking
when the app starts, and **Check now**. When a newer version is found, the page lists what changes in it and **Restart to
update** downloads it and restarts the app. It will not restart while a run is in progress. See
[01-install.md](01-install.md#staying-up-to-date).

## Diagnostics

**Save diagnostics** writes one file you can send to support. It includes your run logs, run
history (ids, states and error codes), safe settings and version numbers. It never includes
your sequences, file names, results, email address or sign-in.

## Advanced

A collapsed group. **Reset all settings** asks first, then puts every setting back to its starting value; your runs, results and input files are not touched. Nothing needs a restart: the theme goes back to Use system at once. A note says whether this copy is using a pretend cloud for testing. The analysis program image box is for developers: it works only when developer mode is already on in the settings file. If you paste an address that is not a full one, the box says so and the shipped image is used until you paste a full one. After a reset, every group on the page shows its starting values at once.

## External viewers

The buttons **Open in IGV** and **Open in Geneious** on the Results page start those programs
for you. This group tells the app where they are and how to talk to IGV.

- **IGV program** and **Geneious program**: the full path of the program. Leave it empty and the
  app looks in the usual install folders itself. **Browse...** lets you pick the file, and you can
  also paste a path. If nothing is at the path you typed, the row says so; choose **Browse...** to
  pick the program again.
- **IGV batch port**: the number IGV listens on for commands (IGV: View, Preferences, Advanced,
  Enable port). The default is 60151. Type a whole number from 1 to 65535; anything else is refused
  and the last good port stays in effect.
- **Reset to default** on each row clears that row only.

A change applies the next time you press the button; there is nothing to restart.
