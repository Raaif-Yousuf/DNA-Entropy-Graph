# 5. History and re-download

## The Runs page

Every run you have ever started, whether it finished, failed, or was cancelled, appears
on the **Runs** page, grouped by day. Each entry shows its name, what was in it, which
model and computer type were used, how long it took, roughly what it cost, and whether
its results are still available in the cloud or only on your own computer.

## Re-running and re-downloading

- **Run again**: starts a new run using the exact same files and settings as a past one.
  Useful for trying a different model or option without re-adding your files.
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

Use the search box to find a run by its name, its input file name or its id, and the status
box to show only running, completed, failed or cancelled runs.

## If you use the app on more than one computer

Two computers signed into the same Google account do not automatically see each other's
run history, since each computer only remembers its own runs. If you need to see runs
made from another computer, use **Find runs from other computers** on the Runs page: it
looks through your cloud storage for any runs it can find and adds them to your list as
read-only entries.

## Related

[06-costs-and-cleanup.md](06-costs-and-cleanup.md), what happens to cost and storage as
runs pile up.
[03-run.md](03-run.md), running a new sequence.

## Good to know

- **Download again** checks each file already in the run's folder against the size and checksum the run recorded. A file that matches is left exactly as it is. A file that is missing is fetched. A file that is cut off, damaged or that you edited is never overwritten: it is renamed in the same folder to `name (changed 2026-10-03 141500).ext` and the original is fetched next to it, and the app tells you how many files were kept aside, even if the download then fails part of the way. Delete the ones you do not need. If the run did not finish, or one of its files failed, the files that exist are restored and the app tells you they may be incomplete.
- **Delete cloud copy** is greyed out when it cannot work, and hovering over it says why: the run is still going (wait for it to finish), or the run has no cloud copy left (choose Run again if you need the results). For now the button cannot delete a cloud copy at all, because that arrives in a later version; the copy is removed on its own when its storage time ends, or you can delete the run's folder in your Google Cloud storage from the Google Cloud console.
- **Run again** is greyed out while the run is still going.
- **Delete files on this PC** deletes file by file and never follows a shortcut (junction or symbolic link) inside the folder: the link itself is removed, and whatever it points to is left alone. If the run's folder itself, or a folder above it, is such a shortcut, nothing is deleted and the app says so: open the folder in File Explorer and delete it there if you want it gone. If some files cannot be deleted, the app says how many were deleted and how many were not. A file open in another program, and a file Windows will not let you change, each get their own message; close the program or fix the permission, then choose it again.
