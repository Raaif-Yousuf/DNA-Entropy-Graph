# 3. Run a sequence

## Add your sequence

On the **New run** page, either:

- Drag a file onto the window (a GenBank file, a FASTA file, or a plain text file), or
- Click **Add files...**, or
- Click **Paste sequence...** and type or paste letters directly.

You can add more than one file at once; the app runs them as a batch and gives you
results for each, up to a limit on how many files or how many total letters one batch can
hold (50 files or 20 million letters, by default). That ceiling is there for cost, not
policy: a batch large enough to matter for your bill should be caught before you press
Run, not discovered afterward. If you go over it, the app tells you your batch's actual
numbers against the limit and lets you either split the batch or raise the limit yourself;
see [06-costs-and-cleanup.md](06-costs-and-cleanup.md) for why the ceiling is set where it
is. GenBank and FASTA files are detected automatically by their contents, not just their
file extension, so a renamed file still works.

Each file you add gets its own small summary: how many records it contains, how many
letters (nucleotides), how many genes it found, and any notices. A green check means it
is ready to run. A yellow notice is something worth knowing but not a problem, for
example, "found a header line, ignoring it," or that a real GenBank/FASTA file contained
a handful of `N`s or other ambiguous letters, which by default this app keeps and runs
with rather than rejecting (see [04-view.md](04-view.md) for what that means for the
entropy track). A red message is something you need to fix before the file can run, and
it tells you exactly what is wrong and where, for example, "found the letter U at
position 12: this looks like RNA," with a button to turn on **Treat as RNA** right there
if that is what you meant.

## Name your run

If you added one file, the run is named after it by default. If you added several, give
the batch a name (there is a sensible default already filled in). This name becomes the
folder your results are saved into.

## The basic options

You do not need to change any of these for a first run; the defaults are chosen to be
reasonable for most sequences.

- **Model**: which version of the analysis model to use. The default, `evo2_7b`, is the
  right choice for almost everyone. Bigger models exist and cost more; the app will warn
  you and ask you to confirm before using one.
- **Find genes**: on by default for pasted or FASTA sequences (this predicts gene
  locations for you); off automatically for GenBank input, since a GenBank file already
  has its own genes recorded, and those are used instead.
- **Output files**: which result files to produce. All of them are on by default; see
  [04-view.md](04-view.md) for what each one is for.
- **Save to**: where results land on your computer. Defaults to your Downloads folder.
- **Where to run**: in the cloud (the normal choice) or on this computer, if you installed
  the local engine in [02-connect-google-cloud.md](02-connect-google-cloud.md)'s Step 8.

Below the options, the app shows an estimated cost and time before you commit to
anything. See [06-costs-and-cleanup.md](06-costs-and-cleanup.md) for what those numbers
actually mean and some worked examples.

## Advanced options (most people can skip this section)

An **Advanced** section holds settings for longer or unusual sequences: the amount of
surrounding sequence the model considers at each position (called the context length),
which direction the model reads your sequence in, and cloud settings like which graphics
card tier to use and what happens to the rented computer when the run finishes. The
defaults here were chosen deliberately and work well for typical sequences; you generally
only need this section for a very long sequence (tens of thousands of letters or more) or
if you want to save money by changing what happens to the computer afterward. See
[glossary.md](glossary.md) for "context length" and "reverse complement" if you are
curious.

## Press Run

Click the **Run** button (or press Ctrl+Enter). What happens next, in plain words:

1. **Checking your files**, a last check before anything is uploaded.
2. **Uploading**, your sequence goes to your own private cloud storage.
3. **Starting a computer**, the app rents a graphics-card computer in your project. The
   first time you do this in a brand-new project, this step narrates which region it is
   trying, since the very first computer in a region can occasionally take a couple of
   tries to find one that is free.
4. **Preparing the computer**, the analysis software and, the first time in a project, the
   model itself are loaded onto the computer. This first-time load is genuinely slow,
   often around eight extra minutes, because the model is a large file being downloaded
   and cached for next time. Every run after the first in the same project is much
   faster, because that cache is kept.
5. **Analysing**, the actual entropy calculation, narrated by which part of your sequence
   is being worked on right now.
6. **Saving results**, then **Downloading** them to your computer.
7. **Cleaning up**, the rented computer is stopped (the default) or deleted, depending on
   your settings; see [06-costs-and-cleanup.md](06-costs-and-cleanup.md).

**You can close the app at any point after the upload starts.** The run keeps going in
the cloud regardless, and your results will be waiting for you, downloaded automatically,
the next time you open the app. A banner on the progress page says exactly this while a
run is active.

You can also **Cancel** a run in progress, or use **Stop VM now** / **Delete VM now** if
you want to end the rented computer immediately rather than waiting for the run to finish
cleanly.

## Related

[04-view.md](04-view.md), what to do with the results once they arrive.
[06-costs-and-cleanup.md](06-costs-and-cleanup.md), what each stage above actually costs.
[07-when-something-goes-wrong.md](07-when-something-goes-wrong.md), if a run fails.
