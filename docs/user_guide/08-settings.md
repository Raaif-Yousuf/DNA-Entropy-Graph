# 8. Settings

Open **Settings** in the left-hand list. The page is a stack of titled groups, one card each.

## Appearance

Choose **Light**, **Dark** or **Use system**. The whole window changes the moment you choose,
including the genome viewer, and the choice is remembered the next time you open the app.
**Use system** follows the Windows light or dark setting. If the app cannot remember your
choice (another copy of the app has the settings file open), it tells you; the theme still
changes for now. Close the other copies, then choose it again.

Under the theme, **Colors of the entropy lines** has two choices. **Default** uses a set of
colors picked so they stay easy to tell apart if you are red-green or blue-yellow color blind.
**High contrast** uses stronger colors that stand out more against the background, which helps in
bright rooms and on projectors. The choice is used by the overview chart, the small lines on the
Runs page, the picture you save, and the genome viewer. The chart and the Runs lines change the next
time you open their page; the viewer changes the next time you open a run in it.

## Run defaults

The files a new run starts with, one list for a GenBank file and one for a FASTA file or a pasted sequence. Tick or untick a file and it is remembered at once; the New run page shows the same ticks, and a choice you make there for a run is remembered here too. At least one file must stay ticked. WIG and the IGV track are alternatives (the worker writes one track format per run), so ticking one unticks the other.

## Save results to

The folder every new run saves its results into (your Downloads folder until you change it). **Change folder** opens a folder chooser and **Use Downloads** goes back to the start. The choice is remembered at once, and the New run page shows and changes the same folder. A whole drive such as D:\ is refused with a message beside the button, and the folder you had stays. If another copy of the app has the settings file open, the group says so; close the other copies and choose again. This choice is not part of Export settings, because a folder only means something on this computer.

## Files

Where the app keeps its own data: the app data folder, the run folders (each keeps a saved copy of the input file it ran, with how much space those copies and your pasted sequences take) and the log files. **Open folder** shows each in Windows Explorer. Nothing here deletes anything: your results and your input files stay until you remove them yourself. If Windows cannot open a folder, open File Explorer and paste the folder path shown on the page into its address bar.

## Google Cloud project

If you use more than one Google Cloud project (say one for teaching and one for research), pick the one the next run should use and press **Switch to this project**. You do not sign in again. The list holds the projects you have chosen or run in with the signed-in account. Under it the page shows the place that project keeps your results (a bucket), once a run has used it. A run that is already going, or a rented computer you chose to keep on, stays in the project it started in and ends there, whichever project you switch to. Before it switches, the app asks Google whether it can open the project. If it cannot, the page says so and offers **Choose another project**; if there is no internet connection it offers **Try again**. Nothing changes when the switch fails. The project you switch away from stays in the list, and the list follows you if you switch Google account while Settings is open. If your Google account changes while the app is checking a project, the page says so and offers **Try again**.

## Cloud results

Each run's results are also stored in a storage area in your own Google Cloud project (a bucket), so you can download them again. **Keep stored results for** sets how many days Google keeps them before deleting them automatically: 7, 30, 90 (the default), 180 or 365. The choice is saved at once and applies when your next run starts, not to a run already going. Choosing fewer days than the bucket already keeps does not shrink it: other copies of the app, on other computers using the same Google project, may share the bucket, and the app never deletes results early on their behalf. After every choice the line under the box says the same thing, because the Settings page cannot see how long the bucket already keeps results: it applies when your next run starts, and a time shorter than the one already set on the bucket never shortens it, so older results may stay. To remove them sooner, delete them yourself in the Cloud Storage page of your Google Cloud project. (A button that shortens the bucket on purpose, after a confirmation, is planned as issue 598.) To lengthen it, choose a longer time. If another copy of the app has the settings file open, the group says so; close the other copies and choose again.

## Notifications

Three switches for what the app does when a run finishes: show a message (on), open the folder that holds the results (off) and open the Results page (off, so a finished run never pulls you away from what you are typing). When the app is not the window in front (or is minimized), the message also appears as a Windows notification, for a run that finished and for one that failed; **Open run** on it (or a click on the notification) opens that run's Results page, even if you had closed the app. If you do not see it, check that Windows Focus Assist is not hiding notifications. They are saved at once and apply from the next run that finishes, whichever page you are on. If another copy of the app has the settings file open, the group says so; close the other copies and change the switch again.

## Spending

A monthly warning cap in US dollars and a **Hard stop** tick box, saved at once. The group also shows your estimated spending so far this month. An empty box means no cap; text that is not a number above zero is refused with a message and the cap stays as it was. See [Costs and cleanup](06-costs-and-cleanup.md) for what the warning and the hard stop do.

## Help

The first time you open the app, a short tour points at New run, Runs, Cloud and Settings, one step at a time. Press **Next** to move on or **Skip tour** to close it; the app remembers you have seen it. **Show the tour again** in this group starts it over, and **Open the user guide** opens this guide in your web browser. The **Help** button at the top right of the window opens the guide page for the screen you are on (for example, Help on the Cloud page opens the costs and cleanup page). After **Reset all settings** the tour shows again at the next start.

## This PC

Run the analysis on your own computer instead of a rented one. It needs an NVIDIA graphics card with about 24 GB of memory, an NVIDIA driver of version 550 or newer, and 30 GB of free disk space. The **Where to run** box chooses the default for new runs: **Cloud**, **This PC**, or **Auto** (This PC when it can run the model, the cloud when it cannot). If this PC cannot run the model, **This PC** is greyed out and the line under the box says why and what to do.

The health check below it shows your graphics card, free disk space and whether the engine (the model software the app installs for this feature) is installed and working. **Install** sets the engine up in one step (it downloads several gigabytes and can be cancelled), **Repair** sets it up again, **Remove** deletes it (your results and input copies are not touched), and **Check again** looks at the computer again. A run on this PC never creates anything in Google Cloud and costs nothing but time.

If the health check says the NVIDIA driver is not working, reinstall or update the driver from the NVIDIA website, restart the PC, then choose **Check again**. A run on this PC that stops reporting in, or runs past its time limit, is stopped by the app and shows a message telling you what to do. If you close the app while a run is going, the run stops, and the next time you open the app it shows that run as not finished so you can start it again. If you have two NVIDIA cards, the app uses the one with the most memory.

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

## Budget alert

Makes a Google Cloud Billing budget on your billing account that emails the billing administrators at 50%, 90% and 100% of a monthly **Monthly amount** (50 by default). **Create or update the alert** makes it, or changes the amount of the one this copy of the app already made (it never makes a second one); **Remove the alert** deletes it. The group shows what Google holds each time the page opens. It names the thresholds the budget really has (usually 50%, 90% and 100%), says when a budget has no fixed amount ("no amount": it follows last month's spend) and says "nothing to remove" when **Remove the alert** finds none. Changing to another Google Cloud project puts the amount box back to the default, so an amount typed for one project is never saved for another. If the group cannot read what Google holds (for example no connection), **Create or update the alert** and **Remove the alert** stay off until a look succeeds, so an alert you cannot see is never replaced. If you are not a billing administrator, the group says so, offers **Open the matching Google page** and **Copy a request for my administrator**, and nothing is created. See [06-costs-and-cleanup.md](06-costs-and-cleanup.md).

**Network proxy** is a second collapsed section in this group. Leave it on **Use the Windows settings** unless the app says your network's proxy refused the connection (see [When something goes wrong](07-when-something-goes-wrong.md)). If your IT help desk gave you a proxy address, choose **Use a proxy I type**, enter it as host:port, add a user name and password only if the proxy needs them, and click **Apply**. The change is used by the next connection, with no restart. The password is stored encrypted for your Windows account and is never written to the settings file or to a diagnostics file; **Reset all settings** forgets it.

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

## Share settings

**Export settings** saves your defaults and your own presets to a file you choose, so a lab can start from the same settings. **Import settings** reads a file made that way, lists every setting it would change (what it is now, then what it would become) and saves nothing until you select **Apply these changes**. **Cancel** throws the preview away.

- The file holds: theme, the "when a run finishes" switches, update choices, the idle-computer and cloud-results limits, the default output files, where runs happen, the IGV connection number, and your presets.
- The file never holds: your sign-in, your installation number, window position, the location of IGV or Geneious on your PC, or developer switches. If a file you import names one of those, it is skipped.
- A file made by a newer version of the app is refused, and the page tells you to update first.
- After you apply, the groups on this page show the new values at once, the theme changes straight away, and New run uses the new defaults the next time it opens.
