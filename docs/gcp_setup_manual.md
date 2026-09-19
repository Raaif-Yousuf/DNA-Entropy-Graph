# Setting up Google Cloud by hand

This page is for when the app's own setup wizard cannot finish a step for you, usually
because your Google account belongs to a school or company that has its own extra rules.
Follow the steps in order. Each one says exactly what to click and what it should look
like when it worked. When you are done, go back to the app and press "Check again" on
the setup page. If every row turns green, you are finished and can close this page.

You do not need to install anything or use a command line to do any of this. Everything
happens in your web browser, on Google's own site.

A few words this page uses a lot, explained once:

- **Google Cloud project**: a private, named folder in your Google account that holds the
 computer (see below) this app will rent for you. Think of it like a separate email
 inbox just for this app's work; nothing in it is visible to anyone else unless you
 share it.
- **Billing account**: the payment method (a credit card, usually) linked to a project.
 Google will not turn on any rented computer for a project with no billing account
 linked, even a free one.
- **API**: a switch that turns on one specific Google Cloud feature for your project.
 "Enabling the Compute Engine API" means "letting this project rent computers at all."
 Nothing costs money just by being switched on; it only costs money once a computer is
 actually running.
- **GPU**: a graphics card. This app needs one to run its analysis quickly. A **GPU
 quota** is a one-time permission slip from Google saying how many of these graphics
 cards your project is allowed to use at once. New projects start at zero and need to
 ask for at least one.
- **Zone / region**: which physical Google data center your rented computer runs in
 (for example, "us-central1-a" is a zone in the central United States). You will see
 these names while requesting a quota; you do not need to understand them beyond
 picking one near you.

---

## Step 1: Create or choose a project

1. Go to [console.cloud.google.com](https://console.cloud.google.com) and sign in with
 the same Google account you used in the app.
2. Click the project picker at the top of the page (it shows the current project's name,
 or "Select a project" if you have none).
3. Click **New Project**.
4. Give it any name you like, for example "DNA Entropy Graph." Leave the other fields as
 they are unless your organization requires a specific one (some school and company
 accounts require picking a "location" here; if you are not sure, ask your IT
 department, since this is the one field this page cannot advise you on generically).
5. Click **Create**, then wait for the notification bell in the top bar to show the
 project is ready (usually a few seconds).
6. Make sure the new project is selected in the project picker before moving on.

**What it should look like when it worked:** the project's name appears in the picker at
the top of every Google Cloud console page from now on.

**If "New Project" is greyed out or missing:** your account's organization has turned off
project creation for regular members. Ask whoever manages your organization's Google
Cloud to either create a project for you and add you as an Owner, or to turn project
creation back on for your account. There is nothing you can do from your own account to
work around this.

## Step 2: Link a billing account

1. In the left-hand menu, click **Billing**.
2. If you already have a billing account, click **Link a billing account** and pick it.
3. If you do not have one yet, click **Create billing account** and follow Google's own
 sign-up (this asks for a payment method, the same as buying anything online).
4. Once linked, the Billing page for your project should say "Billing is enabled" near
 the top.

**What it should look like when it worked:** the Billing page for your project no longer
shows a "link a billing account" prompt.

**A quiet trap here:** a brand-new Google Cloud "Free Trial" billing account cannot be
used to request a GPU at all, even though it looks fully set up. If you signed up for
Google Cloud for the first time just now, look for a banner saying you are on a free
trial, and click through to "activate your account" or "upgrade" (your free credit is not
lost by doing this; it just removes the restriction on GPUs).

**If you get an error saying you do not have permission to link billing:** you are a
member of this project but not its Owner, or you are not an admin on the billing account.
Copy the exact error message and send it to whoever administers your organization's
billing; they are the only one who can grant that permission.

## Step 3: Turn on the two services this app needs

1. In the search bar at the top of the console, type "Compute Engine API" and open the
 result.
2. Click **Enable**. This can take up to a minute; the page will update on its own when
 it is done.
3. Repeat the same two steps for "Cloud Storage API."

**What it should look like when it worked:** each API's page shows a green
"API enabled" indicator instead of an **Enable** button.

**If you get a permission error instead:** you are a member of this project but not an
Owner or Editor. Ask the project's Owner to either enable these two APIs for you, or to
grant your account the Editor role on the project.

## Step 4: Ask Google for permission to use one GPU

This is the step that most often needs a human to ask for it, because Google wants a real
person to approve GPU access rather than granting it automatically to every new project.

1. In the search bar, type "Quotas" and open **IAM & Admin > Quotas**.
2. In the filter box, search for `NVIDIA_L4_GPUS`. This is the specific kind of graphics
 card this app uses by default.
3. Tick the checkbox next to the row for a region near you (for example,
 `us-central1` if you are in the central United States, or `europe-west4` if you are in
 western Europe. Any region works; picking one near you just makes runs a little
 faster).
4. Click **Edit Quotas** at the top of the page.
5. In the panel that opens, set the new limit to **1** and, in the justification box,
   paste the text below exactly as it is:

   ```
   Running the Evo 2 genomic language model for DNA sequence analysis with the
   DNA Entropy Graph desktop application. One GPU, short jobs (typically under
   30 minutes each).
   ```

6. Click **Submit request**. L4 requests like this one are usually approved automatically
 within a few minutes; occasionally Google reviews one by hand, which can take up to a
 day or two.
7. Repeat the exact same steps for the metric named `GPUS_ALL_REGIONS`, using the same
 justification text and a limit of **1**. This second one is a project-wide ceiling,
 separate from the per-region one above; skipping it is a common reason a request
 "worked" but computers still will not start. Both numbers need to read **1** or higher.

**What it should look like when it worked:** on the Quotas page, both `NVIDIA_L4_GPUS`
(in the region you picked) and `GPUS_ALL_REGIONS` show a current limit of 1 or more,
instead of 0.

**If your request is denied:** this usually means your billing account is very new and
has no payment history yet (Google's own words for this are "not enough usage history").
There is no faster path around this; Google's own quota system does not accept new
requests from a fresh billing account for a short time after it is created. Come back to
this page and try again in a few days. In the meantime, the app can still run the free
"quick pipeline test" (it does not need a GPU), and once a GPU quota is approved, real
runs will work with no further setup.

## Step 5: Go back to the app

Open DNA Entropy Graph, go to **Settings > Account > Run setup check again**, and wait
for every row to finish checking. A row that is still red names exactly which step above
it is still waiting on; jump back to that step, fix it, then press **Check again**. You
do not need every row to have been fixed by you personally in one sitting; the app will
happily pick up wherever you left off.

## Related

[`cloud_design.md`](cloud_design.md) (the same four checks, from the app's side: project
active, billing enabled, API enabled, GPU quota present, in that order and for that
reason). This page is the human-language version of the "Setup wizard API mapping" in
[Appendix B, section
2](superpowers/specs/2026-09-18-appendix-b-cloud-design.md#2-first-run-setup-wizard). The
`docs/user_guide/` page on connecting Google Cloud (named in issue #27's "Docs touched"
list, owned by a different part of this docs set) walks through the in-app wizard itself,
of which this page is the fallback for the steps that wizard could not finish
automatically.
