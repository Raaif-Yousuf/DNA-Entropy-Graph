# 5. History and re-download

## The Runs page

Every run you have ever started, whether it finished, failed, or was cancelled, appears
on the **Runs** page, grouped by day. Each entry shows its name, what was in it, which
model and computer type were used, how long it took, roughly what it cost, and whether
its results are still available in the cloud or only on your own computer.

A finished run whose results are still on your computer also shows a small line on its row:
its entropy along the first sequence, at a glance. The lines fill in a moment after the list
appears. A run with no entropy track, or whose folder was moved or deleted, has no line.

## Re-running and re-downloading

- **Run again**: starts a new run using the exact same files and settings as a past one.
  Useful for trying a different model or option without re-adding your files.
- **Download partial results** (on a cancelled run, instead of Download again): if you
  cancelled a run and the computer had already saved part of the work, this brings those
  files back into the run's folder. They are only what was finished before the stop. It is
  not offered when the run never saved anything online.
- **Download again**: if you deleted the local copy of a run's results but they are still in
  your cloud storage, this fetches them again without re-running the analysis. Cloud
  copies are kept for a while after a run finishes (90 days by default, adjustable in
  Settings) and then automatically deleted; once that happens, Download again is no longer
  possible (the button is greyed out) and only Run again will get you a fresh result.
- **Delete files on this PC** / **Delete cloud copy**: free up space on your computer or in
  your cloud storage. Each asks you to confirm first. These are separate actions; deleting
  one does not delete the other. Deleting files on this PC removes only that run's own
  results folder, never your original input file.
- **Open**: shows a finished run in the genome viewer, or the progress page of a run still
  going. If the results folder is gone from your PC, it tells you to use Download again.
- **Remove from list**: takes the entry off your history list without touching any files,
  and only for runs that are over.

A run that failed shows, under its status, a short plain explanation of why and the one thing
to do about it. A run that finished but needs your attention (for example, your results are
saved but we could not confirm the rented computer shut down) shows the same kind of note.

Use the search box to find a run by its name, its input file name, its id, its note or its tags (capitals do not matter), and the status
box to show only running, completed, failed or cancelled runs.

## Notes and tags

Open a run's results and use **Your notes** to write a note (for example, "the clone that failed sequencing") and tags separated by commas (for example, "clone7, redo"). Press **Save notes**; the page says **Saved.** when it is stored with the run on this computer. If you keep typing while it is saving, the page says what was saved and asks you to press **Save notes** again for the rest. Tags show as small chips on the run's row on the Runs page, with the start of the note under them, and typing a word of a note or a tag into the search box shows only the runs that carry it. A note stays on this computer; it is never sent to Google Cloud. A note holds at most 2000 characters and a tag at most 40; a note that would not fit is cut at the limit when you press Save notes. If the run was removed from the list while you typed, the page says so instead of **Saved.** The diagnostics file you save from Settings leaves your notes and tags out, and hides them in the log files it copies, unless you tick **Include my run notes and tags in the file** before pressing Save diagnostics. When you do, a path, an email address or a sequence inside a note or tag is replaced by a marker such as `<path>` first, and a note that still looks like private data is replaced by `<omitted: note withheld>`; the file is always made. The box clears itself once a file has been saved (if you cancel or the save fails, it stays ticked). The hiding in logs matches whole words and each line of a note separately; a tag of one character, or one made only of digits, is not hidden because it would match ordinary text and numbers, and neither is a tag that is one of the log's own words (such as info, error, failed, completed or status), because the app never writes your tags into its logs and hiding those words would blank ordinary log lines, and a log file that still holds one of your words after that is left out of the file whole.

### Messages from the app

When something you did finishes or cannot finish (a delete, a download, a new theme), the app shows a message bar at the bottom of its window. Good news and plain information close by themselves after a few seconds. A warning or a problem stays until you press its close button, so you cannot miss it, and it names what to do next.

## If you use the app on more than one computer

Two computers signed into the same Google account do not automatically see each other's
run history, since each computer only remembers its own runs. If you need to see runs
made from another computer, use **Find runs from other computers** on the Runs page: it
looks through the results storage of the Google Cloud project you chose for finished runs this
computer does not know, and adds them to your list. A line under the progress text says how many
it added, or what to do if it could not look.

An added run says on its row which computer made it. It is read-only here: you can open it
after **Download again** has fetched its files, and you can **Remove from list**, but you cannot
run it again or delete its cloud copy from this computer (do that from the computer that made
it). A run already in your list is never added twice, and a run that is still going, or whose
files were already deleted from the cloud, is not listed.
If you **Remove from list** an added run, it stays off the list on later searches. To bring removed
runs back, use **Reset all settings** in Settings, then search again. A result file the app cannot
read, or one over 1 MiB, is skipped without stopping the search.

## Related

[06-costs-and-cleanup.md](06-costs-and-cleanup.md), what happens to cost and storage as
runs pile up.
[03-run.md](03-run.md), running a new sequence.

## Good to know

- **Download again** checks each file already in the run's folder against the size and checksum the run recorded. A file that matches is left exactly as it is. A file that is missing is fetched. A file that is cut off, damaged or that you edited is never overwritten: it is renamed in the same folder to `name (changed 2026-10-03 141500).ext` and the original is fetched next to it, and the app tells you how many files were kept aside, even if the download then fails part of the way. Delete the ones you do not need. If the run did not finish, or one of its files failed, the files that exist are restored and the app tells you they may be incomplete.
- **Delete cloud copy** is greyed out when it cannot work, and hovering over it says why: the run is still going (wait for it to finish), or the run has no cloud copy left (choose Run again if you need the results). Otherwise it asks you to confirm, then permanently deletes that one run's files from your Google Cloud storage. Nothing else is touched: not other runs, not the saved model files, and not the files on this PC. Once it is done, **Download again** is greyed out for that run, because the cloud copy is gone. It only deletes runs this installation made: for a run made on another computer, or one it cannot confirm is yours, it says so, deletes nothing and shows **Open storage page**, which opens that run's folder on Google's storage page. If the storage itself is not found, nothing changes. If Google refuses the delete before anything is removed, the message names what to fix (account permission, billing, or a turned-off service). If the delete stops partway (a lost connection, or Google refusing one file), the app says how many files are still in storage and nothing is marked as deleted; choose **Delete cloud copy** again and it removes the rest. (If the delete stopped because Google refused it for a reason pressing again cannot fix, such as the account's permission, an organization rule, billing, a turned-off service or Google's request limit, it still says how many files are left and names that one thing to do instead.) If the delete finished but the app could not check that the folder is empty, it says so and tells you to choose **Delete cloud copy** again to check. If the delete stopped and the app could not count what is left, it says some files may have been deleted and some may still be there (it never says nothing was deleted, and never that files remain, when it does not know), and tells you the one thing to do for that cause: choose the right Google account in Settings, ask your organization's administrator, turn billing back on, run the setup check on the Cloud page, wait a few minutes, check your internet connection, or choose **Delete cloud copy** again.
- **Run again** is greyed out while the run is still going.
- **Delete files on this PC** deletes file by file and never follows a shortcut (junction or symbolic link) inside the folder: the link itself is removed, and whatever it points to is left alone. If the run's folder itself, or a folder above it, is such a shortcut, nothing is deleted and the app says so: open the folder in File Explorer and delete it there if you want it gone. If some files cannot be deleted, the app says how many were deleted and how many were not. A file open in another program, and a file Windows will not let you change, each get their own message; close the program or fix the permission, then choose it again.

## Where the app opens

The app opens on the page you were on when you closed it. If you were looking at a run's progress or results, it opens that run (under Runs, so Back goes to the list). If that run finished while the app was closed, it opens on its results. If the run was deleted, or you were on a page the app cannot reopen (the viewer or the setup wizard), it opens on New run. Opening a file with the app, or clicking a finished-run notification, always takes you there instead.

## Runs from the earlier version

If you used the earlier command-line version of this tool, the app looks once, the first time it starts, for the runs it saved: a folder in your Downloads folder named after the run, holding a file ending `.entropy.bedgraph`. If it finds some, it asks before adding them to Runs. Choose **Continue** to add them or **Cancel** to leave them out; either way it does not ask again on its own. To look again later, open Settings and choose **Look for earlier runs**.

- Each earlier run opens from the folder it is already in. Nothing is copied, moved or changed, and the app only reads those folders.
- An earlier run has no copy in the cloud, so Download again and Delete cloud copy are greyed out for it, **Delete files on this PC** is greyed out too (the folder is your own, so the app never deletes it; delete it in File Explorer if you want it gone), and Run again may need you to add the sequence file again, because the earlier version did not keep a copy of it.
- The question also mentions the zone the earlier version last used and the SSH key file it used. They are for your information only: this version picks a zone itself and uses no SSH keys.
- Folders this app made itself are never offered, and a folder already in Runs is not added twice.
