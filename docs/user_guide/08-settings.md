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

Where the app keeps its own data: the app data folder, the saved copy of every input file you ran (with how much space the copies take) and the log files. **Open folder** shows each in Windows Explorer. Nothing here deletes anything: your results and your input files stay until you remove them yourself. If a folder does not exist yet, run a sequence once and choose **Open folder** again.

## Notifications

Three switches for what the app does when a run finishes: show a message (on), open the folder that holds the results (off) and open the Results page (on). They are saved at once and apply from the next run that finishes, whichever page you are on. If another copy of the app has the settings file open, the group says so; close the other copies and change the switch again.

## Diagnostics

**Save diagnostics** writes one file you can send to support. It includes your run logs, run
history (ids, states and error codes), safe settings and version numbers. It never includes
your sequences, file names, results, email address or sign-in.

## Advanced

A collapsed group. **Reset all settings** asks first, then puts every setting back to its starting value; your runs, results and input files are not touched. Close the app and open it again to finish. A note says whether this copy is using a pretend cloud for testing. The analysis program image box is for developers: it works only when developer mode is already on in the settings file.
