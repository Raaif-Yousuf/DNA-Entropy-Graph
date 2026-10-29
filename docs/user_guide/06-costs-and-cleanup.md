# 6. Costs and cleanup

This page answers two questions: "roughly what will this cost me before I press Run," and
"is anything still costing me money right now." Both matter, and the second one is the
one people forget to ask.

**About the numbers on this page:** they come from Google Cloud's own published prices for
the `us-central1` region, as recorded in this project's design document in September
2026, not from a live, independently re-checked price feed. Google's real prices can
differ by region and can change over time. The app itself always shows you a live cost
estimate before you press Run and a running total while a job is working, and that number
is the one to trust over anything printed here. Nothing on this page is invented; where a
figure below is a calculation built on top of Google's published rate rather than a
number Google states directly, that is said plainly.

## What you pay for

Three things, all billed by Google directly to the card on your billing account, never to
anyone connected with this app:

1. **The rented computer, by the minute it runs.** This is almost all of what a typical
   run costs.
2. **The rented computer's storage disk, by the month, for as long as it exists** (even
   while the computer itself is stopped and doing nothing). This is the cost that
   surprises people, covered in its own section below.
3. **A small amount of cloud storage** for your uploaded sequences and downloaded results,
   a few cents a month at most for typical use, and automatically cleaned up after your
   chosen retention period (90 days by default).

## Where the estimate on the New run page comes from

The estimate line is worked out on your PC from a price list that ships inside the app
(`Assets\pricing.json`, with the date the prices were gathered and where they came from).
It adds the hourly price of the computer to the hourly share of its storage disk, then
multiplies by minutes. When you have earlier finished runs of the same
tier on this PC, the start-up time comes from them (the middle value of the last ten) and
the analysis time for your file is added on top, so a long sequence costs more than a short
one even with history; otherwise both come from a built-in model of about 5 minutes on a warm computer or 12 minutes on a fresh one, plus
about 2 seconds of analysis for every thousand letters. Those built-in minutes are our
design guess until the first real GPU runs have been measured. When you are signed in and Google answers, the app uses Google's own live price list (read at most once a day and kept on this PC); when it cannot
reach it, the price list that came with the app is used instead, as fresh as the release you installed. A
Spot price it does not know is left out rather than guessed. Spot is off by default.
Every figure is labelled an estimate because the app cannot see your real bill. If the price
list has no price for a kind of computer yet (the H100 tier, for example), the page says
there is no estimate for it instead of guessing, and you can still press Run. Under the
estimate a second line gives what the disk costs per day if the computer is left stopped
after the run, which is the default (see the next sections).

## Roughly what a run costs

The default computer tier (called "L4," named after its graphics card) costs Google's
customers about **$0.85 an hour** on demand. A bigger tier ("A100") costs roughly **$3.67
to $5.07 an hour**, about four to six times as much, and is only needed for larger models
or unusually long sequences, the app asks you to confirm explicitly before using one so
you never end up on the expensive tier by accident.

Because a run is billed by the minute, not by the sequence, here is what that means for a
few real shapes of work. Each example assumes the default settings and the default L4
computer.

**One plasmid (a few thousand letters), the very first run in a brand-new project.**
The computer itself takes about 6 to 8 minutes to become ready the first time, since the
analysis model (a multi-gigabyte file) has to be downloaded and cached before anything
can run; every run after this one in the same project skips that wait. The actual
analysis for a sequence this short takes well under a minute. Total: **roughly 8 to 10
minutes, well under 20 cents.**

**The same plasmid, on a later run** (the computer or its cache is already warm): the
computer is ready in about 2 to 5 minutes instead of 8. Total: **roughly 5 minutes,
around 7 cents.**

**A 30,000-letter locus**, read in both directions (the default). Reading a sequence this
size still only takes about a minute of actual computer time, since the analysis is fast
even though the sequence is long; almost the entire cost is still the computer's start-up
time, not the analysis itself. Total: **roughly the same as a single plasmid, well under
20 cents.** This can be a little surprising: for sequences at this scale, a much longer
input does not cost dramatically more, because starting the computer dominates the bill.

**A batch of 40 small plasmids in one run.** The app starts one computer and loads the
model once for the whole batch, then works through your files one after another. Using
the same per-plasmid analysis time as above, a batch like this is estimated at **roughly
25 to 30 minutes total, in the neighborhood of 40 cents.** This one is a genuine estimate
built from arithmetic on the single-plasmid numbers above, not a measurement of a real
40-file batch, since no real batch run has been benchmarked yet at the time this page was
written. Treat it as a ballpark, and trust the app's own live estimate on your actual
files over this number.

**Twenty plasmids: one batch or twenty separate runs?** This is the question this page is most
often opened to answer, so here are both, with the same arithmetic as above ($0.85 an hour is
about 1.4 cents a minute on the default L4 computer):

| How you run them | Computer time | Roughly what you pay |
| --- | --- | --- |
| One batch of 20 files | about 5 to 8 minutes to start, plus about half a minute per plasmid: **15 to 18 minutes** | **20 to 25 cents** |
| Twenty separate runs, one after another | about 5 minutes each (the computer is stopped between runs and starts again in 2 to 5 minutes): **100 minutes** | **about $1.40** |

Same plasmids, roughly six times the price, because each separate run pays the start-up wait
again. If you have them all ready, put them in one run. As with the 40-file batch, both rows
are estimates from the single-plasmid numbers, not measurements; the live estimate on the New
run page is the figure to trust. On the A100 tier, multiply by four to six (see below).

This is also why a batch has a ceiling (50 files or 20 million letters, by default, see
[03-run.md](03-run.md)): cost scales with what you run, so a batch large enough to matter
for your bill is worth catching before the computer even starts, not after.

**On the bigger A100 computer**, the same runs cost roughly four to six times as much per
minute of computer time, so a run that costs 10 cents on the default L4 tier costs
somewhere around 40 cents to a dollar on A100. Most people never need to touch this
setting.

## The cost that surprises people: a stopped computer's disk

When a run finishes, the rented computer is **stopped**, not deleted, by default. A
stopped computer is not doing anything and is not being billed by the minute, but its
storage disk still exists and Google still charges for it: **roughly $15 a month** for the
default computer's disk (about 50 cents a day, which is the figure the New run page shows under the estimate, or about $3 to $4 a week), or roughly double that for the largest,
rarely-used model tier. The reason to keep it stopped instead of deleting it is speed:
your *next* run on the same project reuses that disk and comes back to life in about 2
minutes instead of needing the full 8-minute first-time setup again.

**You are protected from forgetting about this for long.** If a stopped computer sits
idle for more than 7 days, the app deletes it automatically, so the worst case if you
simply forget about it is roughly one week's disk cost, not an indefinite one. You can
also change what happens after every run (Stop, Delete immediately, or Keep running for a
set number of minutes) in the run options, and delete a stopped computer yourself any
time from the Cloud page.

## How to see what is costing you money right now

*Note: the Cloud page, the spending figure in the status bar and Settings > Budget are not in
the app yet (the Cloud page currently says so). Until they are, the Billing section of the
Google Cloud console lists every charge, and its Compute Engine page lists any computer
this app created.*

- The **status bar at the top of the app** always shows your estimated spending so far
  this month.
- The **Cloud page** lists every rented computer, every disk and the size of your results
  online that this copy of the app created in your project, with an estimated cost for each.
  From there you can **Stop**, **Start** or **Delete** a computer, or use **Delete everything
  this app created** to remove it all at once. That asks first and says exactly how many
  computers, disks and job folders will go. Afterwards it lists every item with what became
  of it, and says how many days Google keeps a deleted job folder in case you change your
  mind.
- If someone else uses another copy of this app on the same Google project, their computers
  show up on your Cloud page tagged **Another computer**. You can see them (they cost money
  on the same bill) but not stop or delete them from here.
- The app checks for leftovers when it starts and then every 5 minutes. If a rented computer of
  this copy of the app is running and no run is using it, a message names it and tells you to
  press Stop on the Cloud page.
- If you close the app while a computer is still running, a window lists each one with the
  minutes left before it ends itself at its time limit and what it costs per hour. Press
  **Stop it** (keeps the disk, stops the computer's cost), **Delete it**, or **Leave it
  running**. A computer left running is never left forever: it always ends itself at its time
  limit, but you pay until then. The same window says how much the stopped disks cost each
  month. If a computer is still being used by a run, the window says its results are not downloaded
  yet, and Stop or Delete ends that run. If a stop or delete does not work, the app stays open
  and says which computer; close the app again to retry, or use the Cloud page. If Google is
  slow to answer, the app closes after a short wait and says the stop will finish on Google's
  side.
- In **Settings > Budget**, you can set a monthly spending warning (a friendly reminder,
  $50 a month by default) or a hard stop that disables the Run button once you reach it.

## Get an email from Google when spending gets high

The estimate in this app is only an estimate. Google keeps its own count and can email you when
your spending nears an amount you choose. In **Settings > Budget alert**, type a monthly amount
(50 is suggested, in the currency of your billing account) and press **Create or update the
alert**. Google then emails the people who administer your billing account at 50%, 90% and 100%
of that amount each month. The alert watches this project only, and the app switches the Cloud
Billing Budget API on for you the first time. Press **Remove the alert** to delete it.

Creating a budget needs a role on the **billing account**, not just on the project, and the person
who made the project is often not a billing administrator. If Google refuses, the group says so and
offers two things: **Open the matching Google page** (the console page for your billing account) and
**Copy a request for my administrator**, which you can paste into an email. Once they make
the budget themselves or give you the Billing Account Costs Manager role, press the button again.
The group also tells you what state the alert is in. If the alert follows last month's spend instead of a fixed amount (a budget made in Google's own console), it says so and leaves the amount box empty; type an amount and press **Create or update the alert** to set a fixed one (with the box empty, the button asks for an amount and changes nothing). If you press **Remove the alert** and there is nothing left to remove (it was already deleted elsewhere), it says there was nothing to remove rather than "removed". If a budget somehow has no email thresholds, it says Google sends no warnings and tells you to press **Create or update the alert** (when the amount box is empty it tells you to type an amount first). If your Google account is not allowed to switch on the Cloud Billing Budget API, the group offers one thing: **Open the matching Google page**, where you switch it on. If your Google account is not even allowed to see which Google services are on for the project, nothing is saved (so an alert you cannot see is never overwritten) and the group offers **Copy a request for my administrator**, a request to a project owner for the Service Usage Viewer role.

This alert only emails you; it does not stop anything from running. Use the spending cap in
Settings to stop new runs.

## Spot computers

A Spot computer is a cheaper rented computer that Google can take back (Spot). It is off in this version and the New run page has no switch for it yet. When it is on and Google takes the computer back in the middle of a run, the app deletes it and starts the run again, once, on a normal computer, so the run still finishes; the run's history row records the one interruption.

## When Google has no graphics card free

Sometimes Google has no free graphics card in any place the app tries, and the run stops with a message saying so. On the Runs page that run has a **Wait for a graphics card** button. It starts the same run again, but this time Google keeps your request in a queue until a card is free, for up to 2 hours. While it waits, the run page says it is waiting for a graphics card and the app has to stay open. Press **Cancel** to stop waiting; the waiting request is removed. If no card turns up in time, the run stops again with a message saying so, and you can press **Run again** later.

## Related

[03-run.md](03-run.md), where the live cost estimate appears before you press Run.
[05-history.md](05-history.md), past runs and their recorded costs.
[glossary.md](glossary.md) for "VM," "GPU," "L4," "A100," "bucket," and "spot."
