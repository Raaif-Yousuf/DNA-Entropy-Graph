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
default computer's disk (about $3 to $4 a week), or roughly double that for the largest,
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

- The **status bar at the top of the app** always shows your estimated spending so far
  this month.
- The **Cloud page** lists every computer and every storage bucket this app has ever
  created for you, whether it is running, stopped, or gone, with an estimated cost for
  each. From there you can stop, start, or delete anything, or use **Delete everything
  this app created** to remove it all at once (this asks you to type your project's name
  first, so it cannot happen by accident).
- If you close the app while a computer is still running with no job attached to it, a
  message tells you exactly how many minutes until it stops itself, and offers to stop or
  delete it right then instead of waiting.
- In **Settings > Budget**, you can set a monthly spending warning (a friendly reminder,
  $50 a month by default) or a hard stop that disables the Run button once you reach it.

## Related

[03-run.md](03-run.md), where the live cost estimate appears before you press Run.
[05-history.md](05-history.md), past runs and their recorded costs.
[glossary.md](glossary.md) for "VM," "GPU," "L4," "A100," "bucket," and "spot."
