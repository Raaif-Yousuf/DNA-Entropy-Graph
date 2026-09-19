# Frequently asked questions

## Is my sequence data safe? Who can see it?

Your sequence goes from your computer straight into a private storage folder inside your
own Google Cloud account, gets analysed on a rented computer inside that same account,
and the results come straight back to you. Nobody connected with this app operates any
server your data passes through, and nobody connected with this app can see into your
Google Cloud account. See [`docs/threat_model.md`](../threat_model.md) for the full,
detailed version.

## What exactly is the app allowed to do in my Google account?

It can create, look at, and delete cloud resources (rented computers and storage) inside
your own project, on your behalf, to run your analyses. It cannot see or touch any other
Google account, and it never acts unless you asked it to. See
[02-connect-google-cloud.md](02-connect-google-cloud.md) for the honest, longer version
of this answer.

## What is a "VM," and why does the app need one?

A VM (virtual machine) is a rented computer, in this case one with a graphics card
powerful enough to run the analysis model. The app rents one for the few minutes your
job needs, then stops or deletes it. See the [glossary](glossary.md) for more.

## Why do I need to request permission for a graphics card? Why isn't it just on?

Google requires a one-time approval before any new account can rent graphics-card
computers, to prevent abuse. It is not something this app can skip on your behalf. New
billing accounts are sometimes turned down at first and need a few days of history before
trying again. See [02-connect-google-cloud.md](02-connect-google-cloud.md) Step 6.

## Why is my first run so much slower than later ones?

The analysis model is a large file that has to be downloaded once and cached the first
time you use a new Google Cloud project. Later runs reuse that cache and skip the wait,
often around eight minutes faster. This is a real, expected delay, not something going
wrong.

## How much will a run actually cost me?

Usually well under a dollar, often under 20 cents for a typical sequence. The app shows
you an estimate before you press Run and a running total while it works. See
[06-costs-and-cleanup.md](06-costs-and-cleanup.md) for worked examples with real numbers.

## Can this run up an unexpected bill?

Every rented computer has a built-in safety time limit, and a stopped computer left
forgotten is automatically deleted after 7 days rather than costing money forever. You can
also set a monthly spending cap in Settings. See
[06-costs-and-cleanup.md](06-costs-and-cleanup.md).

## What happens if I close the app while a run is going?

Nothing bad. The run keeps going in the cloud on its own, and your results will be there,
already downloaded, the next time you open the app.

## Do I need to know anything about cloud computing to use this?

No. The app walks you through the one-time setup and explains every technical term in
plain words the first time it comes up. The [glossary](glossary.md) has the full list if
you want it.

## Can two people on my lab share one Google account?

Yes, and the app is built so their runs and rented computers never interfere with each
other, each is separately labelled and tracked. Each person's run history is their own.

## What if my university or company blocks this kind of app?

Some organizations restrict which apps their Google accounts can authorize. If that
happens, use a personal Google account instead; the cloud project this app uses is
entirely separate from any account your organization manages.

## Is the model actually reading my sequence, or something generic?

Yes, your actual sequence, letter by letter, is what the model (Evo 2) analyses. See
[04-view.md](04-view.md) for what the resulting entropy numbers mean.

## Related

[glossary.md](glossary.md), [02-connect-google-cloud.md](02-connect-google-cloud.md),
[06-costs-and-cleanup.md](06-costs-and-cleanup.md),
[07-when-something-goes-wrong.md](07-when-something-goes-wrong.md).
