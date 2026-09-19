# DNA Entropy Graph: user guide

This guide is for the person running the app, not the person building it. If you want to
know how the software works inside, you want `docs/` one level up, not here. If you just
want to get a sequence analysed, start with [01-install.md](01-install.md) and read the
pages in order the first time.

This guide describes the app as it is designed to work. It is being written before the
app itself is finished, from the same approved design the developers are building to. A
screen might look slightly different by the time you read this, and where a specific
number (a price, a wait time) is not yet confirmed by a real run, the page says so plainly
instead of guessing. If something in the app does not match what this guide says, that is
worth reporting; see [07-when-something-goes-wrong.md](07-when-something-goes-wrong.md).

## Who this is for

You work with DNA sequences and you want a per-base entropy track (a measure of how
predictable each position is) without learning cloud computing first. You do not need to
know what a "VM" or a "quota" is before you start; this guide explains each one the first
time it comes up, in plain words before the technical term, and the full definition
always lives in the [glossary](glossary.md).

## Reading order

1. [01-install.md](01-install.md), install the app.
2. [02-connect-google-cloud.md](02-connect-google-cloud.md), the one-time setup that lets
   the app rent a computer for you. This is the step most worth reading carefully before
   you start, since it is where most first-time questions come from.
3. [03-run.md](03-run.md), run your first sequence.
4. [04-view.md](04-view.md), read the results.
5. [05-history.md](05-history.md), find and re-download a past run.
6. [06-costs-and-cleanup.md](06-costs-and-cleanup.md), what a run costs and how to be sure
   nothing is left running and billing.
7. [07-when-something-goes-wrong.md](07-when-something-goes-wrong.md), keep this one
   bookmarked. Most problems have a named cause and one thing to do about it.

Two more pages you can jump to any time:

- [glossary.md](glossary.md), every term this guide uses, one paragraph each.
- [faq.md](faq.md), the questions almost everyone asks before their first run.

## What this app does, in one paragraph

You give it a DNA sequence (a GenBank file, a FASTA file, or just pasted letters). It
rents a computer with a graphics card (a GPU) for a few minutes in your own Google Cloud
account, runs a model called Evo 2 on it to see how predictable each position in your
sequence is, and gives you back files you can open in IGV, Geneious, SnapGene, or
Benchling. Nothing about your sequence is ever sent anywhere except into the cloud
account that belongs to you.

## Related, for when you want more depth

- [`docs/science_and_formats.md`](../science_and_formats.md): the developer-level version
  of what the entropy number means and what every output file contains.
- [`docs/gcp_setup_manual.md`](../gcp_setup_manual.md): the fallback for the Google Cloud
  setup steps that the in-app wizard could not finish for you, referenced from
  [02-connect-google-cloud.md](02-connect-google-cloud.md).
- [`docs/threat_model.md`](../threat_model.md): the full, developer-level account of what
  the app can and cannot see or do, if you want more than the plain-words version in
  [02-connect-google-cloud.md](02-connect-google-cloud.md).
