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

**If your organization's IT department blocks the download or the install:** some
university and company networks restrict what can be installed. Ask your IT department
to allow it, or install it on a personal computer instead; the cloud account this app
uses is entirely separate from anything IT would need to configure.

## First launch

The app opens to a short welcome screen explaining what it does and roughly what a run
costs, then asks you to sign in with Google. That step is covered in the next page:
[02-connect-google-cloud.md](02-connect-google-cloud.md).

## Staying up to date

The app checks for updates each time it opens (you can turn this off in Settings) and
tells you when a newer version is available. Updates are small, download in the
background, and never interrupt a run in progress, a run continues in the cloud even if
you close the app entirely.

## Related

[02-connect-google-cloud.md](02-connect-google-cloud.md), the next step.
[07-when-something-goes-wrong.md](07-when-something-goes-wrong.md), if the install itself
does not go as described above.
