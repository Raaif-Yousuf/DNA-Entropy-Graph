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

## Notifications

Three switches for what the app does when a run finishes: show a message (on), open the folder that holds the results (off) and open the Results page (on). They are saved at once and apply from the next run that finishes, whichever page you are on. If another copy of the app has the settings file open, the group says so; close the other copies and change the switch again.

## Diagnostics

**Save diagnostics** writes one file you can send to support. It includes your run logs, run
history (ids, states and error codes), safe settings and version numbers. It never includes
your sequences, file names, results, email address or sign-in.

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
