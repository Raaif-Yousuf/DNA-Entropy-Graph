# 1. Install

## What you need

- Windows 11 (or a recent Windows 10). No other software needs to be pre-installed; the
  app brings everything it needs with it.
- A Google account. A personal one works. If your university or company gives you a
  Google account through their own organization, that can work too, but some
  organizations block apps like this one from being used at all; see
  [02-connect-google-cloud.md](02-connect-google-cloud.md) for what to do if that happens
  to you.
- You do **not** need administrator rights on the computer. The app installs into your own
  user folder, not into Program Files.

## Download and install

1. Go to the project's page on GitHub Releases and download `Setup.exe` from the newest
   release.
2. Run `Setup.exe`. It installs in a few seconds and does not ask for administrator
   permission, because it installs only for your own Windows user account.
3. The app opens automatically once installed, and adds a shortcut to your Start menu for
   next time.

**If Windows shows a warning about an unrecognized app:** this can happen the first time
a new publisher's installer runs, even a properly signed one. Click "More info," then "Run
anyway," if you downloaded `Setup.exe` from the official GitHub Releases page and not
from anywhere else.

**If the title bar says "Development build":** you are running a copy that was built from the source code or downloaded as a test
artifact, not the signed release. It is for testing and is labelled so it is never confused with the real app. Install `Setup.exe` from
the GitHub Releases page to get the release. Settings > About and updates shows the exact build.

**If your organization's IT department blocks the download or the install:** some
university and company networks restrict what can be installed. Ask your IT department
to allow it, or install it on a personal computer instead; the cloud account this app
uses is entirely separate from anything IT would need to configure.

## Removing the app

Uninstall from Windows Settings > Apps. The app asks once whether to also delete your run history, settings and saved copies of your input files;
the default is **No**, so a later reinstall finds everything as it was. Your result files (in your output folder, by default Downloads) are never
deleted by the uninstaller. If you do not answer within about ten seconds, or the uninstall runs silently (for example from an IT tool), your data is kept. If a folder named
`DNAEntropyGraph.removing-` followed by letters and digits is left next to your data folder after you chose Yes, it is the last of the removed data, and you can delete it yourself. To remove your data by hand, open Settings > Files > Open data folder before uninstalling and delete what is inside.

**If the viewer says it needs the WebView2 runtime:** use the link on that screen to install Microsoft's free WebView2 Runtime, then reopen the
results. Everything else in the app works without it.

## First launch

The app opens to a short welcome screen explaining what it does and roughly what a run
costs, then asks you to sign in with Google. That step is covered in the next page:
[02-connect-google-cloud.md](02-connect-google-cloud.md).

## Staying up to date

The app checks for updates each time it opens (you can turn this off in Settings, under
About and updates) and shows a bar at the top, "Update available: 1.2.0", when a newer version is out. Nothing happens
until you press **Restart to update**; the app then downloads the update and restarts into it. It will not restart while a run
is in progress: it tells you to press the button again when the run has finished. A run continues in the cloud even if you
close the app entirely.

You can also press **Check now** in Settings > About and updates, and pick the **Beta** updates there if you want early
versions (the default, Stable, only offers finished releases). The same page shows what changes in the version on offer.
A copy of the app that did not come from the installer cannot update itself; download the newest `Setup.exe` instead.

## Related

[02-connect-google-cloud.md](02-connect-google-cloud.md), the next step.
[07-when-something-goes-wrong.md](07-when-something-goes-wrong.md), if the install itself
does not go as described above.

## Optional: running on this PC

If your computer has an NVIDIA graphics card with about 24 GB of memory, the app can run the analysis there instead of on a rented computer. Open **Settings**, find **This PC**, and read the health check: it tells you whether your card, driver and disk space are enough. If they are, choose **Install**. If they are not, the app keeps using the cloud and nothing else changes. See [Settings](08-settings.md).
