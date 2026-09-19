# 2. Connect Google Cloud

This is the step that decides whether this app is useful to you, so it gets a longer page
than the rest. Read it once before you start; it will save you time if something does not
go smoothly, and something not going smoothly on this page is common enough that it is
worth expecting, not a sign you did something wrong.

## What you are actually setting up, in one sentence

The app is asking Google to let it rent a computer with a graphics card for a few minutes
at a time, inside a private folder that belongs to you (a Google Cloud "project"), paid
for by a card you put on file with Google, not by the people who made this app. Nobody who
made this app can see your cloud account, your files, or your bill.

## The eight steps

### Step 1: Welcome

The app tells you roughly what a run costs (usually well under a dollar) and states
plainly that nothing you upload runs on a server the app's developers control. Everything
from here on happens inside your own Google account.

### Step 2: Sign in with Google

A browser window opens to Google's own sign-in page (not a window inside the app itself,
this is deliberate: it means your password is only ever typed into Google's own page, not
into anything this app controls). Sign in, then a page asks you to grant this app
permission. That permission is the important part of this whole page:

**The app asks for one broad permission, called `cloud-platform`.** In plain words, this
permission lets the app create, look at, and delete things inside your Google Cloud
project on your behalf: computers, storage, and the settings that control them. It is the
same broad permission that Google's own command-line tool asks for. The app only ever
uses it inside your own project, for things you asked it to do (running an analysis, and
the one-time setup on this page), and never touches anyone else's account or any account
the app's developers control. If that still sounds like a lot to hand over, it is, and
that is a fair thing to notice; the technical reason no narrower permission exists yet is
in [`docs/threat_model.md`](../threat_model.md) section 3, and you are always free to say
no and use the app only for the free pipeline test in Step 7, which needs no real cloud
computer at all.

**If this step times out or fails:** try again. **If your organization's Google account
blocks the app entirely** ("your organization's policy prohibits..."), you will need a
personal Google account instead, since some universities and companies restrict which
apps their accounts can authorize.

### Step 3: Choose or create a project

A "project" is a private, named folder in your Google account that holds the computer
this app rents. The app offers to create one for you with one click, or you can pick an
existing one if you already have one. Most people should just let the app create a new
one.

**If project creation is greyed out:** your organization has turned it off for regular
accounts. Ask whoever administers your organization's Google Cloud to create one for you
and add you as an Owner, or use a personal account instead.

### Step 4: Billing

Google will not turn on any rented computer without a payment method on file, even a free
trial one (see the free-trial note below). The app checks whether your project already
has one linked, and if not, walks you to Google's own page to add one. This step blocks
further progress until billing is linked, there is no way around it, because Google
itself requires it.

**A common trap here:** a brand-new Google Cloud account defaults to a "Free Trial." Free
Trial accounts cannot rent a computer with a graphics card at all, even though everything
else on this page will appear to work. If you are new to Google Cloud, look for a banner
offering to "activate your account" or "upgrade," and do that; your free credit is not
lost by doing so.

### Step 5: Turn on the cloud services and create your storage

One click. The app turns on the two Google services it needs (the ability to rent
computers, and a private storage folder for your files and results), creates that
storage folder, and creates a narrow, limited worker identity that is only ever allowed
to start, stop, or delete the specific computers this app creates for you, nothing else
in your project. You will see a short checklist with a green check or a red "fix it" note
per item.

**If any item fails:** it usually means your account is a member of the project but not
its Owner. Ask the project's Owner to fix it, or use "Create new" back in Step 3 to make
a project you own outright.

### Step 6: Ask for permission to use one graphics card

This is the step most likely to need a wait, and it is worth being honest about that now:
**brand-new Google Cloud billing accounts are commonly denied this request at first**,
because Google wants to see a little payment history before approving graphics-card
access. This is not something this app can fix for you.

The app reads whether your project already has permission (most do not, starting at
zero) and, when your account looks eligible, submits the request for you with one click.
When it is not eligible, or the automatic request is denied, the app gives you a link to
Google's own page with the exact request pre-filled and a copy-paste justification, so
you can submit it by hand. Approval, when it happens, is often within minutes; when it
does not happen automatically, it can take a few days as your billing account builds a
little history.

**You can skip this step and come back to it.** The free pipeline test in Step 7 does not
need a graphics card at all, so you can confirm everything else works while you wait.

**If the in-app request fails or you would rather do this by hand from the start**, the
full walkthrough with exact click-by-click steps is
[`docs/gcp_setup_manual.md`](../gcp_setup_manual.md).

### Step 7: Test run

Two optional tests, both cheap:

- **Quick pipeline test** (recommended first): runs the whole pipeline on a plain,
  inexpensive computer with no graphics card, using a stand-in for the real model. About
  3 minutes, about a cent. This proves your setup works end to end without needing
  Step 6's graphics-card permission at all.
- **Real GPU test**: an actual small run on a real graphics card. Roughly 6 to 15 minutes
  and roughly 30 cents, mostly spent waiting for the computer to start and the model to
  load rather than the analysis itself.

### Step 8: Local engine (optional, only shown if you have a suitable graphics card)

If the app detects an NVIDIA graphics card with at least 24 GB of memory already in your
own computer, it offers to install everything needed to run analyses locally instead of
in the cloud, at no per-run cost beyond your own electricity. This is optional and most
people will not see this step at all.

## Coming back to this later

You never have to redo this setup. Once it is done, you can re-run the health check any
time from **Settings > Account > Run setup check again**, which walks through the same
checks (project reachable, billing on, services on, quota available) without recreating
anything that already exists.

## Related

[glossary.md](glossary.md) for "project," "billing account," "OAuth," "GPU," and "quota."
[`docs/gcp_setup_manual.md`](../gcp_setup_manual.md), the hand-click fallback for any step
above that the in-app wizard could not finish for you.
[07-when-something-goes-wrong.md](07-when-something-goes-wrong.md) for what each specific
error message means.
