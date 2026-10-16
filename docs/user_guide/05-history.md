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

Use the search box to find a run by its name, its input file name or its id, and the status
box to show only running, completed, failed or cancelled runs.

### Messages from the app

When something you did finishes or cannot finish (a delete, a download, a new theme), the app shows a message bar at the bottom of its window. Good news and plain information close by themselves after a few seconds. A warning or a problem stays until you press its close button, so you cannot miss it, and it names what to do next.

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

## Where the app opens

The app opens on the page you were on when you closed it. If you were looking at a run's progress or results, it opens that run (under Runs, so Back goes to the list). If that run finished while the app was closed, it opens on its results. If the run was deleted, or you were on a page the app cannot reopen (the viewer or the setup wizard), it opens on New run. Opening a file with the app, or clicking a finished-run notification, always takes you there instead.

## Runs from the earlier version

If you used the earlier command-line version of this tool, the app looks once, the first time it starts, for the runs it saved: a folder in your Downloads folder named after the run, holding a file ending `.entropy.bedgraph`. If it finds some, it asks before adding them to Runs. Choose **Continue** to add them or **Cancel** to leave them out; either way it does not ask again on its own. To look again later, open Settings and choose **Look for earlier runs**.

- Each earlier run opens from the folder it is already in. Nothing is copied, moved or changed, and the app only reads those folders.
- An earlier run has no copy in the cloud, so Download again and Delete cloud copy are greyed out for it, **Delete files on this PC** is greyed out too (the folder is your own, so the app never deletes it; delete it in File Explorer if you want it gone), and Run again may need you to add the sequence file again, because the earlier version did not keep a copy of it.
- The question also mentions the zone the earlier version last used and the SSH key file it used. They are for your information only: this version picks a zone itself and uses no SSH keys.
- Folders this app made itself are never offered, and a folder already in Runs is not added twice.
